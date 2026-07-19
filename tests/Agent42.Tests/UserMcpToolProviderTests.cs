using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The per-user MCP tool provider's orchestration — version-cached, best-effort skip, truncation, bounded
/// connection lifetime — against a real Orleans config grain and a STUB connection seam. The real SDK
/// connect/list path is proven by the Step 0 spike (recorded in the spec); these tests own OUR logic.
/// </summary>
public sealed class UserMcpToolProviderTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    private readonly FakeMcpConnectionFactory _connections = new();
    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    private UserMcpToolProvider NewProvider() =>
        new(_cluster.GrainFactory, _connections, _protector, NullLogger<UserMcpToolProvider>.Instance);

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
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-load");
        await grain.SaveAsync(HeaderServer("a", "top-secret"));

        var set = await NewProvider().GetForUserAsync("mcp-tp-load");

        Assert.Single(set.Tools);
        Assert.Equal("tool_a", set.Tools[0].Name);
        Assert.Contains("top-secret", _connections.ObservedHeaderValues); // decrypted, not ciphertext
    }

    // A disabled server contributes no tools.
    [Fact]
    public async Task DisabledServer_ContributesNoTools()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-disabled");
        await grain.SaveAsync(HeaderServer("a") with { Enabled = false });

        var set = await NewProvider().GetForUserAsync("mcp-tp-disabled");
        Assert.Empty(set.Tools);
    }

    // AC8: a dead MCP server is skipped, not fatal — its healthy neighbour's tools still load.
    [Fact]
    public async Task DeadServer_IsSkipped_NotFatal()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-dead");
        await grain.SaveAsync(HeaderServer("dead"));
        await grain.SaveAsync(HeaderServer("alive"));
        _connections.DeadServerIds.Add("dead");

        var set = await NewProvider().GetForUserAsync("mcp-tp-dead");

        Assert.Single(set.Tools);
        Assert.Equal("tool_alive", set.Tools[0].Name);
    }

    // AC9: an oversized tool result is truncated to the budget, with the marker; a small one is untouched.
    [Fact]
    public async Task ToolResult_Truncated_ToBudget()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-trunc");
        await grain.SaveAsync(HeaderServer("big"));
        _connections.ToolsFor = _ =>
            [AIFunctionFactory.Create(() => new string('a', McpToolLimits.MaxToolResultLength + 5_000), "tool_big")];

        var set = await NewProvider().GetForUserAsync("mcp-tp-trunc");
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(set.Tools));

        var result = (await tool.InvokeAsync(new AIFunctionArguments()))?.ToString() ?? "";
        Assert.EndsWith(McpToolLimits.TruncationMarker, result);
        Assert.Equal(McpToolLimits.MaxToolResultLength + McpToolLimits.TruncationMarker.Length, result.Length);
    }

    // AC10: a version bump reconnects and reflects the new server set (config hot-reload, next resolve).
    [Fact]
    public async Task VersionBump_ReloadsTools()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-reload");
        var provider = NewProvider();

        await grain.SaveAsync(HeaderServer("a"));
        var v1 = await provider.GetForUserAsync("mcp-tp-reload");
        Assert.Single(v1.Tools);

        await grain.SaveAsync(HeaderServer("b"));
        var v2 = await provider.GetForUserAsync("mcp-tp-reload");
        Assert.Equal(2, v2.Tools.Count);
        Assert.True(v2.Version > v1.Version);
    }

    // An unchanged overlay is a cache hit — the same version does NOT reconnect.
    [Fact]
    public async Task SameVersion_IsCacheHit_DoesNotReconnect()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-cachehit");
        await grain.SaveAsync(HeaderServer("a"));
        var provider = NewProvider();

        await provider.GetForUserAsync("mcp-tp-cachehit");
        var callsAfterFirst = _connections.ConnectCount;
        await provider.GetForUserAsync("mcp-tp-cachehit");

        Assert.Equal(callsAfterFirst, _connections.ConnectCount); // no new connect on the hit
    }

    // Bounded lifetime: after more than RetainedVersionsPerUser (=2) versions, the OLDEST version's live
    // connection is disposed on eviction — the connection leak the copied exemplar's unbounded cache would have.
    [Fact]
    public async Task OldVersions_AreEvicted_AndTheirConnectionsDisposed()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-evict");
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
        var grain = _cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("mcp-tp-real-dead");
        // Port 1 refuses immediately; the provider's connect timeout bounds it either way.
        await grain.SaveAsync(HeaderServer("dead") with { Endpoint = "http://localhost:1/mcp" });

        var provider = new UserMcpToolProvider(
            _cluster.GrainFactory,
            new McpConnectionFactory(NullLoggerFactory.Instance),
            _protector,
            NullLogger<UserMcpToolProvider>.Instance);

        var set = await provider.GetForUserAsync("mcp-tp-real-dead");
        Assert.Empty(set.Tools); // dead server skipped, no throw
    }

    private sealed class FakeMcpConnectionFactory : IMcpConnectionFactory
    {
        public HashSet<string> DeadServerIds { get; } = new(StringComparer.Ordinal);
        public Func<StoredMcpServer, IReadOnlyList<AITool>> ToolsFor { get; set; } =
            s => [AIFunctionFactory.Create(() => "ok", $"tool_{s.Id}")];
        public List<string?> ObservedHeaderValues { get; } = [];
        public int ConnectCount { get; private set; }
        public TrackingDisposable? LastDisposable { get; private set; }

        public Task<McpServerConnection> ConnectAsync(
            StoredMcpServer server, string? decryptedHeaderValue, CancellationToken cancellationToken)
        {
            ConnectCount++;
            ObservedHeaderValues.Add(decryptedHeaderValue);
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
