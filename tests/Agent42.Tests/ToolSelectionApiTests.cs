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
/// The selectable-tools HTTP contract (ADR 0021), hosted in-memory (TestServer) against a real Orleans
/// test cluster: the /api/tools catalog, the per-conversation tools GET/PUT round-trip, and the agent /
/// profile endpoints carrying EnabledTools. The load-bearing one is AC14 — a language-only profile save
/// must not wipe a previously-saved tool default (the read-modify-write / absent-means-keep contract),
/// which only an endpoint-level test can prove.
/// </summary>
public sealed class ToolSelectionApiTests : IAsyncLifetime
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
        // MapAgentApi maps every endpoint; the route matcher infers all of their parameters up front, so the
        // services the OTHER endpoints resolve as parameters must be present even though these tests only hit
        // /api/tools, /api/conversations/*, /api/agents and /api/profile (otherwise minimal-API treats an
        // unregistered service param as a body). Same set-up as ProvidersApiTests.
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
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

    private static async Task<string[]> ReadStringArrayAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return [.. doc.RootElement.EnumerateArray().Select(e => e.GetString()!)];
    }

    // AC12 (catalog): /api/tools returns exactly the five selectable keys, with the hosted-tools flag.
    [Fact]
    public async Task Tools_Catalog_ReturnsFiveSelectableKeys()
    {
        var response = await _client.SendAsync(Request(HttpMethod.Get, "/api/tools", "user-cat"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var keys = doc.RootElement.EnumerateArray().Select(e => e.GetProperty("key").GetString()).ToArray();

        Assert.Equal(
            new[] { "web_search", "location", "scheduling", "skills", "code_interpreter" }.Order(),
            keys.Order());

        // code_interpreter is the only one flagged as needing a hosted-tools model.
        var codeInterpreter = doc.RootElement.EnumerateArray()
            .Single(e => e.GetProperty("key").GetString() == "code_interpreter");
        Assert.True(codeInterpreter.GetProperty("requiresHostedTools").GetBoolean());
    }

    // AC12 (agent): POST /api/agents round-trips EnabledTools; a create that omits it stores null (= all).
    [Fact]
    public async Task Agent_RoundTripsEnabledTools()
    {
        var scoped = await _client.SendAsync(Request(HttpMethod.Post, "/api/agents", "user-agent", new
        {
            name = "Scheduler",
            description = "schedules",
            model = "fake-model",
            systemPrompt = "You schedule.",
            enabledTools = new[] { "web_search", "scheduling" },
        }));
        scoped.EnsureSuccessStatusCode();

        using (var doc = JsonDocument.Parse(await scoped.Content.ReadAsStringAsync()))
        {
            // The create body is now AgentSaveResponse { agent, apiKey } (ADR 0022) — the definition is nested.
            var tools = doc.RootElement.GetProperty("agent").GetProperty("enabledTools")
                .EnumerateArray().Select(e => e.GetString()).ToArray();
            Assert.Equal(new[] { "web_search", "scheduling" }, tools);
        }

        // A create that omits enabledTools stores null — the "all tools" sentinel, no migration for old clients.
        var full = await _client.SendAsync(Request(HttpMethod.Post, "/api/agents", "user-agent", new
        {
            name = "Full",
            description = "everything",
            model = "fake-model",
            systemPrompt = "You do everything.",
        }));
        full.EnsureSuccessStatusCode();
        using (var doc = JsonDocument.Parse(await full.Content.ReadAsStringAsync()))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("agent").GetProperty("enabledTools").ValueKind);
        }

        // GET /api/agents carries the round-tripped set for the scoped agent.
        var list = await _client.SendAsync(Request(HttpMethod.Get, "/api/agents", "user-agent"));
        using var listDoc = JsonDocument.Parse(await list.Content.ReadAsStringAsync());
        var scheduler = listDoc.RootElement.EnumerateArray().Single(a => a.GetProperty("name").GetString() == "Scheduler");
        var schedulerId = scheduler.GetProperty("id").GetString();
        Assert.Equal(
            new[] { "web_search", "scheduling" },
            scheduler.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());

        // AC12 also covers EDIT: PUT /api/agents/{id} threads a changed EnabledTools through the same path.
        var edit = await _client.SendAsync(Request(HttpMethod.Put, $"/api/agents/{schedulerId}", "user-agent", new
        {
            name = "Scheduler",
            description = "schedules",
            model = "fake-model",
            systemPrompt = "You schedule.",
            enabledTools = new[] { "location" },
        }));
        edit.EnsureSuccessStatusCode();
        using var editDoc = JsonDocument.Parse(await edit.Content.ReadAsStringAsync());
        Assert.Equal(
            new[] { "location" },
            editDoc.RootElement.GetProperty("agent").GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    // AC13: PUT /api/conversations/{id}/tools then GET returns the same set. Unknown keys are dropped.
    [Fact]
    public async Task Conversation_ToolsPutThenGet_ReturnsSet()
    {
        var put = await _client.SendAsync(Request(HttpMethod.Put, "/api/conversations/conv-1/tools", "user-conv", new
        {
            tools = new[] { "web_search", "not-a-real-tool", "scheduling" },
        }));
        put.EnsureSuccessStatusCode();
        // The response is the sanitized effective set (garbage dropped, catalog order).
        Assert.Equal(new[] { "web_search", "scheduling" }, await ReadStringArrayAsync(put));

        var get = await _client.SendAsync(Request(HttpMethod.Get, "/api/conversations/conv-1/tools", "user-conv"));
        get.EnsureSuccessStatusCode();
        Assert.Equal(new[] { "web_search", "scheduling" }, await ReadStringArrayAsync(get));
    }

    // AC14 (the load-bearing one): a language-only profile save does NOT wipe a previously-saved tool
    // default, and a tools-only save does NOT wipe the language — the read-modify-write / absent-means-keep
    // contract. Only reachable at the endpoint, so it is not covered by the grain-level round-trip test.
    [Fact]
    public async Task Profile_LanguageOnlySave_DoesNotWipeToolDefault()
    {
        // Save a tool default first (no language).
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            enabledTools = new[] { "web_search" },
        }))).EnsureSuccessStatusCode();

        // Then a language-only save (no enabledTools field at all).
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            preferredLanguage = "nl-NL",
        }))).EnsureSuccessStatusCode();

        var get = await _client.SendAsync(Request(HttpMethod.Get, "/api/profile", "user-prof"));
        using var doc = JsonDocument.Parse(await get.Content.ReadAsStringAsync());
        Assert.Equal("nl-NL", doc.RootElement.GetProperty("preferredLanguage").GetString());
        Assert.Equal(
            new[] { "web_search" },
            doc.RootElement.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());

        // And the reverse: a tools-only save leaves the language intact.
        (await _client.SendAsync(Request(HttpMethod.Put, "/api/profile", "user-prof", new
        {
            enabledTools = new[] { "scheduling" },
        }))).EnsureSuccessStatusCode();

        var after = await _client.SendAsync(Request(HttpMethod.Get, "/api/profile", "user-prof"));
        using var afterDoc = JsonDocument.Parse(await after.Content.ReadAsStringAsync());
        Assert.Equal("nl-NL", afterDoc.RootElement.GetProperty("preferredLanguage").GetString());
        Assert.Equal(
            new[] { "scheduling" },
            afterDoc.RootElement.GetProperty("enabledTools").EnumerateArray().Select(e => e.GetString()).ToArray());
    }
}
