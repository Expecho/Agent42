using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The /api/providers CRUD contract (ADR 0018), hosted in-memory (TestServer) against a real Orleans
/// test cluster — so the encrypt-before-grain, mask-on-read, and per-user isolation guarantees are
/// exercised end to end. The API layer encrypts the key, so the host registers IProviderSecretProtector (m7).
/// </summary>
public sealed class ProvidersApiTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private TestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);
        // The API encrypts the key before the grain (ADR 0018), so the protector must be registered —
        // an ephemeral key ring is fine for tests (no persisted secrets).
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        // MapAgentApi maps every endpoint; the route matcher infers all of their parameters up front, so
        // the services the OTHER endpoints resolve as parameters must be present even though these tests
        // only hit /api/providers (otherwise minimal-API treats an unregistered service param as a body).
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        _app = appBuilder.Build();
        _app.MapAgentApi();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private static HttpRequestMessage Request(HttpMethod method, string uri, string principalId, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, principalId);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return request;
    }

    private static object OllamaBody(string? apiKey = "secret-token") => new
    {
        key = "ollama",
        kind = "OpenAICompatible",
        displayName = "Local Ollama",
        endpoint = "http://localhost:11434/v1",
        apiKey,
        models = new[] { new { id = "llama3.1", displayName = "Llama 3.1", supportsTools = true, supportsVision = false } },
        useResponsesApi = false,
    };

    // AC4: a stored key is never returned — GET masks it to hasKey, and the plaintext appears nowhere.
    [Fact]
    public async Task Get_NeverReturnsKey()
    {
        (await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-a", OllamaBody("super-secret-value"))))
            .EnsureSuccessStatusCode();

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "user-a"));
        response.EnsureSuccessStatusCode();
        var raw = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("super-secret-value", raw);
        using var doc = JsonDocument.Parse(raw);
        var provider = doc.RootElement.GetProperty("providers").EnumerateArray().Single();
        Assert.Equal("ollama", provider.GetProperty("key").GetString());
        Assert.True(provider.GetProperty("hasKey").GetBoolean());
        Assert.False(provider.TryGetProperty("apiKey", out _));
        Assert.False(provider.TryGetProperty("apiKeyCiphertext", out _));
    }

    // AC7: a localhost endpoint is accepted; a malformed endpoint is rejected with 400.
    [Fact]
    public async Task AcceptsLocalhost_RejectsMalformed()
    {
        var ok = await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-b", OllamaBody()));
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        var bad = await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-b", new
        {
            key = "broken",
            kind = "OpenAICompatible",
            endpoint = "not-a-valid-url",
            apiKey = "k",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var badKind = await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-b", new
        {
            key = "x",
            kind = "Nonsense",
            endpoint = "https://example.com",
            apiKey = "k",
        }));
        Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
    }

    // AC8 (ADR 0019): a provider that declares NO models is rejected with 400 — a keyed-but-modelless
    // provider would count toward providers.Count yet contribute no default, breaking "providers ⟺ a
    // usable default exists".
    [Fact]
    public async Task RejectsProviderWithNoModels()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-d", new
        {
            key = "modelless",
            kind = "OpenAICompatible",
            endpoint = "http://localhost:11434/v1",
            apiKey = "k",
            models = Array.Empty<object>(),
        }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("at least one model", await response.Content.ReadAsStringAsync());
    }

    // AC5 (over HTTP): editing without re-entering the key keeps the stored key (blank ApiKey = keep).
    [Fact]
    public async Task Put_BlankKey_KeepsStoredKey()
    {
        (await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-c", OllamaBody("original-key"))))
            .EnsureSuccessStatusCode();

        // Update the display name, sending NO key (but the required ≥1 model — ADR 0019).
        var put = await _client.SendAsync(Request(HttpMethod.Put, "/api/providers/ollama", "user-c", new
        {
            key = "ollama",
            kind = "OpenAICompatible",
            displayName = "Renamed",
            endpoint = "http://localhost:11434/v1",
            apiKey = (string?)null,
            models = new[] { new { id = "llama3.1", displayName = "Llama 3.1", supportsTools = true, supportsVision = false } },
        }));
        put.EnsureSuccessStatusCode();

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "user-c"));
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var provider = doc.RootElement.GetProperty("providers").EnumerateArray().Single();
        Assert.Equal("Renamed", provider.GetProperty("displayName").GetString());
        Assert.True(provider.GetProperty("hasKey").GetBoolean()); // key survived the keyless edit
    }

    // AC10 (ADR 0020): a provider declaring an image-generation model is accepted, round-trips the
    // supportsImageGeneration flag through GET, and still masks the key to hasKey.
    [Fact]
    public async Task Providers_AcceptImageModel_MaskKey()
    {
        var post = await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-img", new
        {
            key = "images",
            kind = "OpenAICompatible",
            displayName = "Image Provider",
            endpoint = "https://images.example/v1",
            apiKey = "img-secret",
            models = new[]
            {
                new { id = "gpt-image-1", displayName = "GPT Image 1", supportsTools = false, supportsVision = false, supportsImageGeneration = true },
            },
            useResponsesApi = false,
        }));
        post.EnsureSuccessStatusCode();

        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "user-img"));
        var raw = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("img-secret", raw);

        using var doc = JsonDocument.Parse(raw);
        var provider = doc.RootElement.GetProperty("providers").EnumerateArray().Single();
        Assert.True(provider.GetProperty("hasKey").GetBoolean());
        var model = provider.GetProperty("models").EnumerateArray().Single();
        Assert.Equal("gpt-image-1", model.GetProperty("id").GetString());
        Assert.True(model.GetProperty("supportsImageGeneration").GetBoolean());
        Assert.False(model.GetProperty("supportsVision").GetBoolean());
    }

    // Reads the overlay default straight off GET /api/providers (ProvidersResponse.Default) — the read
    // side of PUT /api/default-model, which deliberately has no GET of its own.
    private async Task<string?> GetDefaultAsync(string principalId)
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", principalId));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("default").GetString();
    }

    // AC1: PUT /api/default-model persists the id and GET /api/providers echoes it back as `default`.
    [Fact]
    public async Task PutDefaultModel_PersistsAndIsReturnedByGet()
    {
        var put = await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-def", new
        {
            @default = "ollama:llama3.1",
        }));

        Assert.Equal(HttpStatusCode.NoContent, put.StatusCode);
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-def"));
    }

    // AC2: a blank (or null) body clears the default back to null — the grain normalises blank to null.
    [Fact]
    public async Task PutDefaultModel_Blank_ClearsTheDefault()
    {
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-clear", new { @default = "ollama:llama3.1" })))
            .EnsureSuccessStatusCode();
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-clear"));

        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-clear", new { @default = "  " })))
            .EnsureSuccessStatusCode();
        Assert.Null(await GetDefaultAsync("user-clear"));

        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-clear", new { @default = (string?)null })))
            .EnsureSuccessStatusCode();
        Assert.Null(await GetDefaultAsync("user-clear"));
    }

    // AC3: the default is per user — one user's default is invisible to another.
    [Fact]
    public async Task PutDefaultModel_IsPerUser()
    {
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "dora", new { @default = "ollama:llama3.1" })))
            .EnsureSuccessStatusCode();

        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("dora"));
        Assert.Null(await GetDefaultAsync("eli"));
    }

    // AC4: provider CRUD PRESERVES the default — SaveAsync/DeleteAsync deliberately leave Default alone,
    // so adding, editing and deleting a provider never silently drop the user's chosen default.
    [Fact]
    public async Task ProviderCrud_PreservesTheDefault()
    {
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-crud", new { @default = "ollama:llama3.1" })))
            .EnsureSuccessStatusCode();

        (await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "user-crud", OllamaBody())))
            .EnsureSuccessStatusCode();
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-crud"));

        (await _client.SendAsync(Request(HttpMethod.Put, "/api/providers/ollama", "user-crud", OllamaBody(null))))
            .EnsureSuccessStatusCode();
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-crud"));

        (await _client.SendAsync(Request(HttpMethod.Delete, "/api/providers/ollama", "user-crud")))
            .EnsureSuccessStatusCode();
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-crud"));
    }

    // AC5: an over-long id is rejected with 400 and the stored default is left as it was. The cap keeps
    // unbounded text out of a persisted grain state — it is NOT a validity check (that is the resolver's
    // read-time job).
    [Fact]
    public async Task PutDefaultModel_TooLong_Rejected_AndLeavesStoredValue()
    {
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-long", new { @default = "ollama:llama3.1" })))
            .EnsureSuccessStatusCode();

        var tooLong = await _client.SendAsync(Request(HttpMethod.Put, "/api/default-model", "user-long", new
        {
            @default = new string('x', 201),
        }));

        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);
        Assert.Contains("200 characters", await tooLong.Content.ReadAsStringAsync());
        Assert.Equal("ollama:llama3.1", await GetDefaultAsync("user-long"));
    }

    // AC8: CRUD is per-user — one user cannot see another's providers, and delete only affects the owner.
    [Fact]
    public async Task Crud_IsPerUser()
    {
        (await _client.SendAsync(Request(HttpMethod.Post, "/api/providers", "alice", OllamaBody())))
            .EnsureSuccessStatusCode();

        // Bob sees nothing.
        var bob = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "bob"));
        using (var bobDoc = JsonDocument.Parse(await bob.Content.ReadAsStringAsync()))
        {
            Assert.Empty(bobDoc.RootElement.GetProperty("providers").EnumerateArray());
        }

        // Bob deleting "ollama" does not touch Alice's.
        (await _client.SendAsync(Request(HttpMethod.Delete, "/api/providers/ollama", "bob"))).EnsureSuccessStatusCode();

        var alice = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "alice"));
        using (var aliceDoc = JsonDocument.Parse(await alice.Content.ReadAsStringAsync()))
        {
            Assert.Single(aliceDoc.RootElement.GetProperty("providers").EnumerateArray());
        }

        // Alice deleting it removes it.
        (await _client.SendAsync(Request(HttpMethod.Delete, "/api/providers/ollama", "alice"))).EnsureSuccessStatusCode();
        var aliceAfter = await _client.SendAsync(Request(HttpMethod.Get, "/api/providers", "alice"));
        using var afterDoc = JsonDocument.Parse(await aliceAfter.Content.ReadAsStringAsync());
        Assert.Empty(afterDoc.RootElement.GetProperty("providers").EnumerateArray());
    }
}
