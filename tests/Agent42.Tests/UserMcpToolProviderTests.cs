using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The per-user MCP tool provider's orchestration — version-cached, best-effort skip, truncation, bounded
/// connection lifetime — against a real Orleans config grain and a STUB connection seam. The real SDK
/// connect/list path is proven by the Step 0 spike (recorded in the spec); these tests own OUR logic.
/// </summary>
public sealed class UserMcpToolProviderTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly FakeMcpConnectionFactory _connections = new();
    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);
    // OAuth refresh is exercised only by the None/Header/ClientCredentials-free tests below; this client is
    // never called for those, so a bare HttpClient stands in.
    private readonly McpOAuthClient _oauth = new(new HttpClient());

    // The token resolver is a collaborator since it was extracted for McpConnectionTester to share (ADR 0064);
    // its behaviour is unchanged, so these tests still own the provider's end of it.
    private McpOAuthTokenResolver NewTokenResolver(McpOAuthClient? oauth = null) =>
        new(Cluster.GrainFactory, _protector, oauth ?? _oauth, NullLogger<McpOAuthTokenResolver>.Instance);

    private UserMcpToolProvider NewProvider() =>
        new(Cluster.GrainFactory, _connections, _protector, NewTokenResolver(), NullLogger<UserMcpToolProvider>.Instance);

    private StoredMcpServer HeaderServer(string id, string plaintextSecret = "plain-secret") => new(
        Id: id,
        Name: $"server-{id}",
        Endpoint: "https://example.com/mcp",
        Auth: nameof(McpAuthMode.Header),
        Enabled: true,
        HeaderName: "Authorization",
        HeaderValueCiphertext: _protector.Protect(plaintextSecret));

    // "Tools load": an enabled server's tool reaches the set, and the provider passes the DECRYPTED secret
    // to the connection factory (the encrypt-before-grain / decrypt-on-connect round-trip).
    [Fact]
    public async Task LoadsTools_AndDecryptsHeaderSecret()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-load");
        await grain.SaveAsync(HeaderServer("a", "top-secret"));

        var set = await NewProvider().GetForUserAsync("mcp-tp-load");

        Assert.Single(set.AllTools);
        Assert.Equal("tool_a", set.AllTools[0].Name);
        Assert.Contains("top-secret", _connections.ObservedHeaderValues); // decrypted, not ciphertext
    }

    // A disabled server contributes no tools.
    [Fact]
    public async Task DisabledServer_ContributesNoTools()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-disabled");
        await grain.SaveAsync(HeaderServer("a") with { Enabled = false });

        var set = await NewProvider().GetForUserAsync("mcp-tp-disabled");
        Assert.Empty(set.AllTools);
    }

    // AC8: a dead MCP server is skipped, not fatal — its healthy neighbour's tools still load.
    [Fact]
    public async Task DeadServer_IsSkipped_NotFatal()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-dead");
        await grain.SaveAsync(HeaderServer("dead"));
        await grain.SaveAsync(HeaderServer("alive"));
        _connections.DeadServerIds.Add("dead");

        var set = await NewProvider().GetForUserAsync("mcp-tp-dead");

        Assert.Single(set.AllTools);
        Assert.Equal("tool_alive", set.AllTools[0].Name);

        // AC7 (ADR 0026): a skipped/dead server still appears as an EMPTY group — its id must survive so the
        // grain can expand the `mcp` wildcard into the complete per-server key set. Both servers are present.
        Assert.Equal(2, set.Servers.Count);
        Assert.Empty(Assert.Single(set.Servers, s => s.ServerId == "dead").Tools);
        Assert.Single(Assert.Single(set.Servers, s => s.ServerId == "alive").Tools);
    }

    // AC9: an oversized tool result is truncated to the budget, with the marker; a small one is untouched.
    [Fact]
    public async Task ToolResult_Truncated_ToBudget()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-trunc");
        await grain.SaveAsync(HeaderServer("big"));
        _connections.ToolsFor = _ =>
            [AIFunctionFactory.Create(() => new string('a', McpToolLimits.MaxToolResultLength + 5_000), "tool_big")];

        var set = await NewProvider().GetForUserAsync("mcp-tp-trunc");
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(set.AllTools));

        var result = (await tool.InvokeAsync(new AIFunctionArguments()))?.ToString() ?? "";
        Assert.EndsWith(McpToolLimits.TruncationMarker, result);
        Assert.Equal(McpToolLimits.MaxToolResultLength + McpToolLimits.TruncationMarker.Length, result.Length);
    }

    // AC10: a version bump reconnects and reflects the new server set (config hot-reload, next resolve).
    [Fact]
    public async Task VersionBump_ReloadsTools()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-reload");
        var provider = NewProvider();

        await grain.SaveAsync(HeaderServer("a"));
        var v1 = await provider.GetForUserAsync("mcp-tp-reload");
        Assert.Single(v1.AllTools);

        await grain.SaveAsync(HeaderServer("b"));
        var v2 = await provider.GetForUserAsync("mcp-tp-reload");
        Assert.Equal(2, v2.AllTools.Count);
        Assert.True(v2.Version > v1.Version);
    }

    // An unchanged overlay is a cache hit — the same version does NOT reconnect.
    [Fact]
    public async Task SameVersion_IsCacheHit_DoesNotReconnect()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-cachehit");
        await grain.SaveAsync(HeaderServer("a"));
        var provider = NewProvider();

        await provider.GetForUserAsync("mcp-tp-cachehit");
        var callsAfterFirst = _connections.ConnectCount;
        await provider.GetForUserAsync("mcp-tp-cachehit");

        Assert.Equal(callsAfterFirst, _connections.ConnectCount); // no new connect on the hit
    }

    // ADR 0069, and THE case the feature exists for: a server that was unreachable when the version was first
    // resolved has its group cached EMPTY, and nothing dislodges that — its names were never erased, so on
    // recovery it reports exactly what it always did and no config write happens. Before this, the only ways
    // out were an unrelated re-save or a Backend restart.
    //
    // The middle assertion is the load-bearing one: it pins the DEFECT. Without it the last assertion could
    // pass simply because the provider always reconnects, and the test would prove nothing about invalidation.
    [Fact]
    public async Task ARecoveredServer_StaysEmpty_UntilInvalidation()
    {
        const string user = "mcp-tp-recovered";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        var provider = NewProvider();

        await grain.SaveAsync(HeaderServer("a"));
        _connections.DeadServerIds.Add("a");
        Assert.Empty((await provider.GetForUserAsync(user)).AllTools);

        _connections.DeadServerIds.Remove("a");
        Assert.Empty((await provider.GetForUserAsync(user)).AllTools);

        // What McpConnectionTester calls after a successful test.
        await grain.InvalidateToolCacheAsync();

        Assert.Single((await provider.GetForUserAsync(user)).AllTools);
    }

    // ADR 0069's other motivating case: a server we already know about grows a tool remotely. Same shape —
    // the middle assertion pins the staleness, so the last one is about invalidation and not about the
    // provider being eager.
    [Fact]
    public async Task AChangedToolList_IsInvisible_UntilInvalidation()
    {
        const string user = "mcp-tp-changed";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        var provider = NewProvider();

        await grain.SaveAsync(HeaderServer("a"));
        Assert.Single((await provider.GetForUserAsync(user)).AllTools);

        _connections.ToolsFor = s =>
        [
            AIFunctionFactory.Create(() => "ok", $"tool_{s.Id}"),
            AIFunctionFactory.Create(() => "ok", "tool_added_later"),
        ];
        Assert.Single((await provider.GetForUserAsync(user)).AllTools);

        await grain.InvalidateToolCacheAsync();

        var after = await provider.GetForUserAsync(user);
        Assert.Equal(2, after.AllTools.Count);
        Assert.Contains(after.AllTools, t => t.Name == "tool_added_later");
    }

    // Bounded lifetime: after more than RetainedVersionsPerUser (=2) versions, the OLDEST version's live
    // connection is disposed on eviction — the connection leak the copied exemplar's unbounded cache would have.
    [Fact]
    public async Task OldVersions_AreEvicted_AndTheirConnectionsDisposed()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-evict");
        var provider = NewProvider();

        await grain.SaveAsync(HeaderServer("a"));
        await provider.GetForUserAsync("mcp-tp-evict");
        var firstConnection = _connections.LastDisposable!;

        // Two more version bumps push the first version out of the retain-2 window.
        await grain.SaveAsync(HeaderServer("b"));
        await provider.GetForUserAsync("mcp-tp-evict");
        Assert.False(firstConnection.Disposed); // still within the window (2 retained)

        await grain.SaveAsync(HeaderServer("c"));
        await provider.GetForUserAsync("mcp-tp-evict");
        Assert.True(firstConnection.Disposed); // evicted and disposed
    }

    // Integration: the REAL McpConnectionFactory over the ModelContextProtocol SDK against an unreachable
    // endpoint. Proves the actual transport wiring + Header branch + failure path skip a dead server (the
    // best-effort rule) end to end — complementing the Step 0 spike (which proved the happy path live).
    [Fact]
    public async Task RealFactory_DeadEndpoint_IsSkipped_NotFatal()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-real-dead");
        // Port 1 refuses immediately; the provider's connect timeout bounds it either way.
        await grain.SaveAsync(HeaderServer("dead") with { Endpoint = "http://localhost:1/mcp" });

        var provider = new UserMcpToolProvider(
            Cluster.GrainFactory,
            new McpConnectionFactory(NullLoggerFactory.Instance),
            _protector,
            NewTokenResolver(),
            NullLogger<UserMcpToolProvider>.Instance);

        var set = await provider.GetForUserAsync("mcp-tp-real-dead");
        Assert.Empty(set.AllTools); // dead server skipped, no throw
    }

    private StoredMcpServer OAuthServer(string id, McpOAuthTokens? tokens) => new(
        Id: id,
        Name: $"oauth-{id}",
        Endpoint: "https://mcp.example.com/mcp",
        Auth: nameof(McpAuthMode.InteractiveOAuth),
        Enabled: true,
        TokenCiphertext: tokens is null ? null : _protector.Protect(tokens.Serialize()));

    // An InteractiveOAuth server with a stored, unexpired token loads its tools, and the provider passes the
    // decrypted access token to the connection.
    [Fact]
    public async Task OAuthServer_WithToken_LoadsTools_AndInjectsAccessToken()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-oauth-load");
        var tokens = new McpOAuthTokens("access-abc", "refresh-1", DateTimeOffset.UtcNow.AddHours(1),
            "https://as.example.com/token", "client-1", ["mcp.read"]);
        await grain.SaveAsync(OAuthServer("o1", tokens));

        var set = await NewProvider().GetForUserAsync("mcp-oauth-load");

        Assert.Single(set.AllTools);
        Assert.Contains("access-abc", _connections.ObservedOAuthTokens);
    }

    // An InteractiveOAuth server that has not completed the Connect flow (no token) is skipped, not fatal.
    [Fact]
    public async Task OAuthServer_WithoutToken_IsSkipped()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-oauth-notoken");
        await grain.SaveAsync(OAuthServer("o1", tokens: null));

        var set = await NewProvider().GetForUserAsync("mcp-oauth-notoken");
        Assert.Empty(set.AllTools);
    }

    // A near-expiry token is refreshed at connect (app-owned) via a fake token endpoint, the refreshed bundle
    // is persisted, and the connection sees the NEW access token.
    [Fact]
    public async Task OAuthServer_NearExpiry_RefreshesAndPersists()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-oauth-refresh");
        var expiring = new McpOAuthTokens("access-old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(10),
            "https://as.example.com/token", "client-1", ["mcp.read"]);
        await grain.SaveAsync(OAuthServer("o1", expiring));

        var oauth = new McpOAuthClient(new HttpClient(new RefreshStubHandler()));
        var provider = new UserMcpToolProvider(
            Cluster.GrainFactory, _connections, _protector, NewTokenResolver(oauth),
            NullLogger<UserMcpToolProvider>.Instance);

        var set = await provider.GetForUserAsync("mcp-oauth-refresh");

        Assert.Single(set.AllTools);
        Assert.Contains("access-new", _connections.ObservedOAuthTokens); // refreshed token injected
        // The refreshed bundle was persisted (decrypting the stored ciphertext yields the new access token).
        var stored = Assert.Single((await grain.GetAsync()).Servers);
        var persisted = McpOAuthTokens.Deserialize(_protector.Unprotect(stored.TokenCiphertext));
        Assert.Equal("access-new", persisted!.AccessToken);
    }

    private sealed class RefreshStubHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"access_token":"access-new","refresh_token":"refresh-2","expires_in":3600}""",
                    System.Text.Encoding.UTF8, "application/json"),
            });
    }

    // A successful connect is the ONLY place tool names are learned, and it persists them so GET /api/tools
    // can render per-tool checkboxes without ever connecting. The group carries the SAME list it just
    // recorded — one source of names for the picker and for the grain's wildcard expansion. If these two ever
    // diverge, the expansion mints mcp:{id}:{tool} keys the picker has no checkbox for, and the next Save
    // drops them.
    [Fact]
    public async Task SuccessfulConnect_RecordsDiscoveredToolNames()
    {
        const string user = "mcp-tp-record";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(HeaderServer("a"));

        var set = await NewProvider().GetForUserAsync(user);

        Assert.Equal(["tool_a"], Assert.Single(set.Servers).KnownToolNames);
        Assert.Equal(["tool_a"], Assert.Single((await grain.GetAsync()).Servers).KnownToolNames);
    }

    // AC13 — one half of the invariant the whole feature rests on: names are NEVER erased. A server that is
    // unreachable this activation contributes no tools, but it must keep the names it was known to expose, so
    // its per-tool checkboxes survive the outage. Without this a transient failure would render the group with
    // ZERO children, and the next picker Save would either silently disable the server or silently re-grant a
    // tool the user deliberately deselected — the exact failure per-tool selection was designed to avoid.
    //
    // This is also why McpServerTools.KnownToolNames is not derived from Tools: right here, Tools is empty and
    // the names are not.
    [Fact]
    public async Task FailedConnect_KeepsPreviouslyKnownToolNames()
    {
        const string user = "mcp-tp-failed-keeps-names";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(HeaderServer("a"));

        // First activation succeeds and learns the names.
        await NewProvider().GetForUserAsync(user);
        Assert.Equal(["tool_a"], Assert.Single((await grain.GetAsync()).Servers).KnownToolNames);

        // The server goes dark. Bump the version so the provider re-connects rather than serving its cache.
        _connections.DeadServerIds.Add("a");
        await grain.SaveAsync(HeaderServer("a") with { Name = "still-dark" });

        var set = await NewProvider().GetForUserAsync(user);

        var group = Assert.Single(set.Servers);
        Assert.Empty(group.Tools);                            // unreachable: contributes nothing
        Assert.Equal(["tool_a"], group.KnownToolNames);       // ...but the checkboxes survive
        Assert.Equal(["tool_a"], Assert.Single((await grain.GetAsync()).Servers).KnownToolNames);
    }

    // The same guarantee on the OTHER empty-group path: a server skipped before any connect is attempted
    // (unsupported auth mode, or InteractiveOAuth with no token yet) must also keep its names.
    [Fact]
    public async Task SkippedServer_KeepsPreviouslyKnownToolNames()
    {
        const string user = "mcp-tp-skipped-keeps-names";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(HeaderServer("a"));
        await NewProvider().GetForUserAsync(user);

        // InteractiveOAuth with no stored token → the "not connected" Skip path, never reaching ConnectAsync.
        await grain.SaveAsync(HeaderServer("a") with { Auth = nameof(McpAuthMode.InteractiveOAuth), TokenCiphertext = null });

        var group = Assert.Single((await NewProvider().GetForUserAsync(user)).Servers);
        Assert.Empty(group.Tools);
        Assert.Equal(["tool_a"], group.KnownToolNames);
    }

    private sealed class FakeMcpConnectionFactory : IMcpConnectionFactory
    {
        public HashSet<string> DeadServerIds { get; } = new(StringComparer.Ordinal);
        public Func<StoredMcpServer, IReadOnlyList<AITool>> ToolsFor { get; set; } =
            s => [AIFunctionFactory.Create(() => "ok", $"tool_{s.Id}")];
        public List<string?> ObservedHeaderValues { get; } = [];
        public List<string?> ObservedOAuthTokens { get; } = [];
        public int ConnectCount { get; private set; }
        public TrackingDisposable? LastDisposable { get; private set; }

        public Task<McpServerConnection> ConnectAsync(
            StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
        {
            ConnectCount++;
            ObservedHeaderValues.Add(secrets.HeaderValue);
            ObservedOAuthTokens.Add(secrets.OAuthAccessToken);
            if (DeadServerIds.Contains(server.Id))
            {
                throw new InvalidOperationException("simulated dead server");
            }

            LastDisposable = new TrackingDisposable();
            return Task.FromResult(new McpServerConnection(ToolsFor(server), LastDisposable));
        }
    }

    private sealed class TrackingDisposable : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
