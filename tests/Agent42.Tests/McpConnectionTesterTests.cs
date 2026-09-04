using System.Reflection;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The user-pressed MCP connection probe (ADR 0064) against a real Orleans config grain and a STUB
/// connection seam — the <see cref="UserMcpToolProviderTests"/> shape, for the same reason: these tests own
/// OUR diagnosis logic (curated reasons, name recording, disposal), not the SDK's wire path.
/// </summary>
public sealed class McpConnectionTesterTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly FakeConnectionFactory _connections = new();
    private readonly IMcpSecretProtector _protector =
        new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance);
    private readonly McpOAuthClient _oauth = new(new HttpClient());

    private McpConnectionTester NewTester(McpOAuthClient? oauth = null) => new(
        Cluster.GrainFactory,
        _connections,
        _protector,
        new McpOAuthTokenResolver(Cluster.GrainFactory, _protector, oauth ?? _oauth,
            NullLogger<McpOAuthTokenResolver>.Instance),
        NullLogger<McpConnectionTester>.Instance);

    private StoredMcpServer HeaderServer(string id) => new(
        Id: id,
        Name: $"server-{id}",
        Endpoint: "https://example.com/mcp",
        Auth: nameof(McpAuthMode.Header),
        Enabled: true,
        HeaderName: "Authorization",
        HeaderValueCiphertext: _protector.Protect("plain-secret"));

    private StoredMcpServer OAuthServer(string id, McpOAuthTokens? tokens) => new(
        Id: id,
        Name: $"oauth-{id}",
        Endpoint: "https://mcp.example.com/mcp",
        Auth: nameof(McpAuthMode.InteractiveOAuth),
        Enabled: true,
        TokenCiphertext: tokens is null ? null : _protector.Protect(tokens.Serialize()));

    // AC1: the whole point of the feature — a reachable server reports ok, its count and its names.
    [Fact]
    public async Task Test_ReportsToolsForAReachableServer()
    {
        const string user = "mcp-test-reachable";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(HeaderServer("a"));

        var outcome = await NewTester().TestAsync(user, "a", CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.True(outcome!.Ok);
        Assert.Equal("ok", outcome.Reason);
        Assert.Equal(["tool_a"], outcome.ToolNames);
    }

    // AC2: the MCP server's response body is response-controlled, so a failure NEVER reflects the exception
    // (the ConfigHealth.ProbeAsync discipline). The detail goes to the log instead.
    [Fact]
    public async Task Test_ReturnsACuratedReason_NeverTheException()
    {
        const string user = "mcp-test-dead";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(HeaderServer("a"));
        _connections.DeadServerIds.Add("a");

        var outcome = await NewTester().TestAsync(user, "a", CancellationToken.None);

        Assert.NotNull(outcome);
        Assert.False(outcome!.Ok);
        Assert.Equal("could not connect", outcome.Reason);
        Assert.DoesNotContain(FakeConnectionFactory.SecretExceptionText, outcome.Reason);
        Assert.Empty(outcome.ToolNames);
    }

    // AC3: closing ADR 0058's cold start — a successful test records the names, so the per-tool checkboxes
    // appear without waiting for a chat activation.
    [Fact]
    public async Task Test_RecordsDiscoveredToolNames()
    {
        const string user = "mcp-test-records";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(HeaderServer("a"));

        await NewTester().TestAsync(user, "a", CancellationToken.None);

        var stored = Assert.Single((await grain.GetAsync()).Servers);
        Assert.Equal(["tool_a"], stored.KnownToolNames);
    }

    // AC4 (C8): RecordToolNamesAsync no-ops when the list is UNCHANGED, not when it is EMPTY — so recording
    // [] would ERASE the names and reach ADR 0058's supposedly-unreachable state (a server rendering
    // unchecked + "tools not discovered yet" while stored mcp:{id}:{tool} keys still grant a tool). A
    // zero-tool connect must therefore write NOTHING.
    [Fact]
    public async Task Test_NeverErasesKnownToolNamesOnAZeroToolConnect()
    {
        const string user = "mcp-test-zero-tools";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await grain.SaveAsync(HeaderServer("a"));
        await NewTester().TestAsync(user, "a", CancellationToken.None); // learns ["tool_a"]

        _connections.ToolsFor = _ => []; // the server now exposes nothing
        var outcome = await NewTester().TestAsync(user, "a", CancellationToken.None);

        Assert.True(outcome!.Ok); // it DID connect — that is what the user asked
        Assert.Equal("connected, but the server exposes no tools", outcome.Reason);
        Assert.Empty(outcome.ToolNames);
        var stored = Assert.Single((await grain.GetAsync()).Servers);
        Assert.Equal(["tool_a"], stored.KnownToolNames); // survived
    }

    // AC5 (C5): a test is not an activation — its socket dies with the request rather than entering the
    // provider's version cache.
    [Fact]
    public async Task Test_DisposesItsConnection()
    {
        const string user = "mcp-test-disposes";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(HeaderServer("a"));

        await NewTester().TestAsync(user, "a", CancellationToken.None);

        Assert.True(_connections.LastDisposable!.Disposed);
    }

    // AC6 (M6): "never populates the socket cache" is not observable at runtime from here, so it is pinned
    // STRUCTURALLY — the tester cannot reach IUserMcpToolProvider because it does not take one.
    [Fact]
    public void Tester_DoesNotDependOnTheToolProvider()
    {
        var ctor = Assert.Single(typeof(McpConnectionTester).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
        Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType == typeof(IUserMcpToolProvider));
    }

    // AC7: "you have not signed in to this server yet" is a different diagnosis from "the URL is wrong" —
    // the user's next action differs, so the two reasons must differ.
    [Fact]
    public async Task Test_DistinguishesNotConnectedFromUnreachable()
    {
        const string user = "mcp-test-notoken";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).SaveAsync(OAuthServer("o1", tokens: null));

        var outcome = await NewTester().TestAsync(user, "o1", CancellationToken.None);

        Assert.False(outcome!.Ok);
        Assert.Equal("not connected (no OAuth token yet)", outcome.Reason);
        Assert.Equal(0, _connections.ConnectCount); // never dialled
    }

    // AC8 (C4): the extracted resolver is SHARED with the activation path, so a near-expiry token is
    // refreshed here too. Without this the probe would report failure on a server chat connects to fine —
    // a false negative, the worst defect a diagnostic tool can ship.
    [Fact]
    public async Task Test_RefreshesANearExpiryToken()
    {
        const string user = "mcp-test-refresh";
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        var expiring = new McpOAuthTokens("access-old", "refresh-1", DateTimeOffset.UtcNow.AddSeconds(10),
            "https://as.example.com/token", "client-1", ["mcp.read"]);
        await grain.SaveAsync(OAuthServer("o1", expiring));

        var outcome = await NewTester(new McpOAuthClient(new HttpClient(new RefreshStubHandler())))
            .TestAsync(user, "o1", CancellationToken.None);

        Assert.True(outcome!.Ok);
        Assert.Contains("access-new", _connections.ObservedOAuthTokens); // refreshed token was used to connect
    }

    // A disabled server is still tested — the user is diagnosing it — but the outcome says so, or "ok" would
    // read as "and it is in use".
    [Fact]
    public async Task Test_TestsADisabledServer_AndSaysItIsOff()
    {
        const string user = "mcp-test-disabled";
        await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user)
            .SaveAsync(HeaderServer("a") with { Enabled = false });

        var outcome = await NewTester().TestAsync(user, "a", CancellationToken.None);

        Assert.True(outcome!.Ok);
        Assert.Equal("ok, but this server is switched off", outcome.Reason);
    }

    // A null outcome is what the endpoint turns into a 404 (AC9's server half).
    [Fact]
    public async Task Test_ReturnsNullForAnUnknownServer()
    {
        var outcome = await NewTester().TestAsync("mcp-test-unknown", "nope", CancellationToken.None);
        Assert.Null(outcome);
    }

    private sealed class FakeConnectionFactory : IMcpConnectionFactory
    {
        public const string SecretExceptionText = "sensitive-upstream-detail";

        public HashSet<string> DeadServerIds { get; } = new(StringComparer.Ordinal);
        public Func<StoredMcpServer, IReadOnlyList<AITool>> ToolsFor { get; set; } =
            s => [AIFunctionFactory.Create(() => "ok", $"tool_{s.Id}")];
        public List<string?> ObservedOAuthTokens { get; } = [];
        public int ConnectCount { get; private set; }
        public TrackingDisposable? LastDisposable { get; private set; }

        public Task<McpServerConnection> ConnectAsync(
            StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
        {
            ConnectCount++;
            ObservedOAuthTokens.Add(secrets.OAuthAccessToken);
            if (DeadServerIds.Contains(server.Id))
            {
                throw new InvalidOperationException(SecretExceptionText);
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
}
