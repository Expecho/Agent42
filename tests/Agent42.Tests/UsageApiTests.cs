using System.Net;
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
/// The <c>GET /api/usage</c> contract (ADR 0031), hosted in-memory (TestServer). Asserts the wire
/// shape, range validation, and — the load-bearing property since ADR 0121 — that the payload is scoped to
/// the signed-in caller: another user's data is never visible, however the request is shaped.
/// </summary>
public sealed class UsageApiTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string BlobHeader = "X-MS-CLIENT-PRINCIPAL";
    private const string OwnerOid = "oid-owner";
    private const string OwnerEmail = "owner@example.com";

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
        appBuilder.Services.AddSingleton<IOwnerPolicy>(new OwnerPolicy([OwnerOid]));

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

    // `email` becomes the preferred_username claim in the principal blob, which is what an owner's email key is
    // derived from: it lets a test present a stranger whose email EQUALS an owner's.
    private HttpRequestMessage Request(string uri, string caller = "user-a", string? email = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, caller);
        if (email is not null)
        {
            var blob = JsonSerializer.Serialize(new { claims = new[] { new { typ = "preferred_username", val = email } } });
            request.Headers.Add(BlobHeader, Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(blob)));
        }

        return request;
    }

    private static UsageEvent Token(string user, long input, long output, string userId = "") =>
        new(UsageEventKind.Token, DateTimeOffset.UtcNow, "Agent42", "foundry", "gpt-5.4", user, "", input, output, 0, 0, false, UserId: userId);

    private async Task<long> InputTokensAsync(HttpRequestMessage request)
    {
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("totals").GetProperty("inputTokens").GetInt64();
    }

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

    // ADR 0121 AC10: a caller sees only their own rows; another user's data is invisible, and a `user` query
    // parameter (which used to be the filter) is ignored rather than honoured, so it cannot widen the scope.
    [Fact]
    public async Task Usage_IsolatedPerUser()
    {
        await _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
            .ApplyAsync([Token("ada", 100, 20, "oid-ada"), Token("bob", 200, 40, "oid-bob")]);

        Assert.Equal(100, await InputTokensAsync(Request("/api/usage?range=30d", caller: "oid-ada")));
        Assert.Equal(200, await InputTokensAsync(Request("/api/usage?range=30d", caller: "oid-bob")));

        // Asking for someone else's data by name or id does not get it.
        Assert.Equal(100, await InputTokensAsync(Request("/api/usage?range=30d&user=oid-bob", caller: "oid-ada")));
        Assert.Equal(100, await InputTokensAsync(Request("/api/usage?range=30d&user=bob", caller: "oid-ada")));

        // A user with no data of their own sees none of anyone else's.
        Assert.Equal(0, await InputTokensAsync(Request("/api/usage?range=30d", caller: "oid-nobody")));
    }

    // ADR 0121 AC11: a stranger whose EMAIL equals an owner's sees neither the owner's new rows nor the legacy
    // (pre-UserId) rows; the owner sees both. Email is a label; the object id is the key.
    [Fact]
    public async Task Usage_SpoofedEmail_SeesNothing()
    {
        await _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
            .ApplyAsync([
                Token(OwnerEmail, 100, 10, OwnerOid), // new row
                Token(OwnerEmail, 1000, 10),          // legacy row: recorded before UserId existed
            ]);

        Assert.Equal(0, await InputTokensAsync(Request("/api/usage?range=30d", caller: "oid-stranger", email: OwnerEmail)));
        Assert.Equal(1100, await InputTokensAsync(Request("/api/usage?range=30d", caller: OwnerOid, email: OwnerEmail)));

        // An owner id with a DIFFERENT email still sees their own new rows but not someone else's legacy rows.
        Assert.Equal(100, await InputTokensAsync(Request("/api/usage?range=30d", caller: OwnerOid, email: "other@example.com")));
    }

    // ADR 0121 AC12: the applications dropdown is scoped to the caller, and the all-users list is gone.
    [Fact]
    public async Task Usage_Applications_ScopedToCaller_AndUsersListIsGone()
    {
        await _cluster.GrainFactory.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
            .ApplyAsync([Token("ada", 1, 1, "oid-ada") with { Application = "AdaApp" }, Token("bob", 1, 1, "oid-bob") with { Application = "BobApp" }]);

        var response = await _client.SendAsync(Request("/api/usage/applications", caller: "oid-ada"));
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["AdaApp"], doc.RootElement.EnumerateArray().Select(e => e.GetString()).ToArray());

        var users = await _client.SendAsync(Request("/api/usage/users", caller: "oid-ada"));
        Assert.Equal(HttpStatusCode.NotFound, users.StatusCode);
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
