using System.Net;
using System.Text.Json;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;
using Agent42.Agents.Telemetry;
using Agent42.Agents.Attachments;

namespace Agent42.Tests;

/// <summary>
/// The <c>GET /api/usage</c> contract (ADR 0031), hosted in-memory (TestServer). Asserts the wire
/// shape, range validation, and — the load-bearing property — that the payload is GLOBAL/cross-user:
/// data applied under one user is visible to a different signed-in caller.
/// </summary>
public sealed class UsageApiTests : IAsyncLifetime
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

        // MapAgentApi infers every endpoint's params up front, so the services the OTHER endpoints resolve
        // must be present even though these tests only hit /api/usage. Mirrors ConfigHealthApiTests.
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

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

    private HttpRequestMessage Request(string uri, string caller = "user-a")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, caller);
        return request;
    }

    private static UsageEvent Token(string user, long input, long output) =>
        new(UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "foundry", "gpt-5.4", user, "", input, output, 0, 0, false);

    // AC12: the endpoint returns the reference contract shape (totals + three tables).
    [Fact]
    public async Task Usage_ReturnsContractShape()
    {
        var response = await _client.SendAsync(Request("/api/usage"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        var totals = root.GetProperty("totals");
        foreach (var field in new[] { "inputTokens", "outputTokens", "cachedInputTokens", "callCount", "errorCount", "toolCallCount" })
        {
            Assert.True(totals.TryGetProperty(field, out _), $"totals missing {field}");
        }

        Assert.Equal(JsonValueKind.Array, root.GetProperty("models").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("tools").ValueKind);
        Assert.Equal(JsonValueKind.Array, root.GetProperty("users").ValueKind);
    }

    // AC13: an unknown range is a 400; an omitted range defaults to 30d (a 200 with the shape).
    [Fact]
    public async Task Usage_RangeValidation()
    {
        var bad = await _client.SendAsync(Request("/api/usage?range=all-time"));
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var defaulted = await _client.SendAsync(Request("/api/usage"));
        Assert.Equal(HttpStatusCode.OK, defaulted.StatusCode);

        foreach (var range in new[] { "24h", "7d", "30d", "all" })
        {
            var ok = await _client.SendAsync(Request($"/api/usage?range={range}"));
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }
    }

    // AC14: data applied under user "bob" is visible to a DIFFERENT signed-in caller ("user-a") — the
    // dashboard is global/cross-user by design.
    [Fact]
    public async Task Usage_IsCrossUserVisible()
    {
        await _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
            .ApplyAsync([Token("ada", 100, 20), Token("bob", 200, 40)]);

        var response = await _client.SendAsync(Request("/api/usage?range=30d", caller: "user-a"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(300, root.GetProperty("totals").GetProperty("inputTokens").GetInt64());

        var users = root.GetProperty("users").EnumerateArray().Select(u => u.GetProperty("user").GetString()).ToList();
        Assert.Contains("ada", users);
        Assert.Contains("bob", users);
    }

    // Empty store → zeroed totals and empty tables (no crash on a fresh deployment).
    [Fact]
    public async Task Usage_EmptyStore_ReturnsZeroed()
    {
        var response = await _client.SendAsync(Request("/api/usage"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(0, doc.RootElement.GetProperty("totals").GetProperty("inputTokens").GetInt64());
        Assert.Empty(doc.RootElement.GetProperty("models").EnumerateArray());
    }
}
