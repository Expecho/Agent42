using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agent42.Tests;

/// <summary>
/// <c>/api/local-bridge</c> and the connection path over it (ADR 0076), in-memory: the endpoint fails
/// closed, a connected helper's tools list and run through the unchanged MCP client, channels are isolated,
/// the caps close a channel and never the bridge, and a connect invalidates the tool cache at most once per
/// interval. The two claims about a real transport dying live in <see cref="LocalBridgeKestrelTests"/>.
/// </summary>
public sealed class LocalBridgeEndpointTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly McpConnectionSecrets NoSecrets = new(null, null);

    private static LocalBridgeConnectionFactory Factory(LocalBridgeRegistry registry, IMcpConnectionFactory? inner = null) =>
        new(inner ?? new RecordingInnerFactory(), registry, NullLoggerFactory.Instance);

    private static HttpRequestMessage Get(string? key, string query = "")
    {
        var request = new HttpRequestMessage(HttpMethod.Get, LocalBridgeEndpoint.Route + query);
        if (key is not null)
        {
            request.Headers.Add("X-Api-Key", key);
        }

        return request;
    }

    private static async Task<string> CallEchoAsync(McpServerConnection connection, object? arguments = null)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(connection.Tools));
        var args = arguments is null
            ? new AIFunctionArguments()
            : new AIFunctionArguments(JsonSerializer.SerializeToElement(arguments).Deserialize<Dictionary<string, object?>>()!);
        var result = await tool.InvokeAsync(args);
        return result?.ToString() ?? "";
    }

    private static async Task AssertFaultsPromptly(Task call)
    {
        var finished = await Task.WhenAny(call, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, call), "the call hung instead of faulting");
        await Assert.ThrowsAnyAsync<Exception>(() => call);
    }

    public static TheoryData<string> Failures() =>
        ["missing", "unknown", "query-string", "disabled", "deleted", "not-bridge", "hash-mismatch", "regenerated"];

    // AC11 (and AC8's endpoint half): every failure is the same 401, decided before the upgrade check.
    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Endpoint_FailsClosed(string failure)
    {
        var user = $"bridge-ep-fail-{failure}";
        var grains = Cluster.GrainFactory;
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(grains, user);
        var config = grains.GetGrain<IUserMcpConfigGrain>(user);
        await using var app = await LocalBridgeTestHost.StartAsync(grains, new LocalBridgeRegistry());

        HttpRequestMessage request;
        switch (failure)
        {
            case "missing":
                request = Get(null);
                break;
            case "unknown":
                request = Get("sk_bridge_never-minted");
                break;
            case "query-string":
                request = Get(null, $"?X-Api-Key={Uri.EscapeDataString(key)}&key={Uri.EscapeDataString(key)}");
                break;
            case "disabled":
                await config.SaveAsync(server with { Enabled = false });
                request = Get(key);
                break;
            case "deleted":
                // The directory entry is orphaned by hand, as a crash between write and deregister would leave it.
                await config.DeleteAsync(server.Id);
                await grains.GetGrain<ILocalBridgeKeyDirectoryGrain>(BridgeKey.Hash(key)).RegisterAsync(user, server.Id);
                request = Get(key);
                break;
            case "not-bridge":
                // SaveAsync preserves the hash, so this is a stored hash that matches on a non-bridge server.
                await config.SaveAsync(server with { Auth = nameof(McpAuthMode.None), Endpoint = "http://127.0.0.1/mcp" });
                request = Get(key);
                break;
            case "hash-mismatch":
                const string stray = "sk_bridge_registered-but-not-stored";
                await grains.GetGrain<ILocalBridgeKeyDirectoryGrain>(BridgeKey.Hash(stray)).RegisterAsync(user, server.Id);
                request = Get(stray);
                break;
            default:
                Assert.NotNull(await config.MintBridgeKeyAsync(server.Id));
                request = Get(key);
                break;
        }

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LocalBridgeEndpoint.RejectionBody, await response.Content.ReadAsStringAsync());
    }

    // AC11: the same rule holds for an actual upgrade attempt.
    [Fact]
    public async Task Endpoint_RefusesAnUpgradeWithoutAKey()
    {
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            app.GetTestServer().CreateWebSocketClient().ConnectAsync(
                new Uri(app.GetTestServer().BaseAddress, LocalBridgeEndpoint.Route), CancellationToken.None));

        Assert.Contains("401", error.Message, StringComparison.Ordinal);
    }

    // AC12
    [Fact]
    public async Task Endpoint_NonUpgrade_Is400()
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-400");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());

        var response = await app.GetTestClient().SendAsync(Get(key));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // Chrome extension ADR: a web page's request is refused even with a valid key. Any Origin but the
    // extension's is refused before the key is looked at, so a page that learned a key still cannot open the socket.
    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:61326")]
    [InlineData("null")]
    [InlineData("chrome-extension://aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public async Task Endpoint_RejectsAWebPageOrigin_EvenWithAValidKey(string origin)
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, $"bridge-ep-origin-{origin.Length}");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());
        var request = Get(key);
        request.Headers.Add("Origin", origin);

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(LocalBridgeEndpoint.RejectionBody, await response.Content.ReadAsStringAsync());
    }

    // The extension's own Origin passes the rule (400 = authenticated, not an upgrade): its preflight.
    [Fact]
    public async Task Endpoint_AcceptsTheExtensionOrigin()
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-ext-origin");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());
        var request = Get(key);
        request.Headers.Add("Origin", LocalBridgeLimits.ExtensionOrigin);

        var response = await app.GetTestClient().SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // The console helper sends no Origin at all, and must keep working.
    [Fact]
    public async Task Endpoint_AcceptsNoOrigin()
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-no-origin");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());

        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);

        Assert.Equal(System.Net.WebSockets.WebSocketState.Open, helper.Socket.State);
    }

    public static TheoryData<string?, string, bool> SubprotocolCases() => new()
    {
        { LocalBridgeLimits.ExtensionOrigin, "{name}, {key}", true },
        { LocalBridgeLimits.ExtensionOrigin, "{key}, {name}", true },
        { "https://evil.example", "{name}, {key}", false },
        { null, "{name}, {key}", false },
        { LocalBridgeLimits.ExtensionOrigin, "{key}", false },
        { LocalBridgeLimits.ExtensionOrigin, "{name}, {key}, extra", false },
    };

    // The extension cannot set X-Api-Key on a WebSocket (and declarativeNetRequest does not touch its own socket,
    // measured), so its key rides Sec-WebSocket-Protocol, read ONLY under the extension's exact Origin.
    [Theory]
    [MemberData(nameof(SubprotocolCases))]
    public async Task Endpoint_SubprotocolKey_OnlyFromTheExtensionOrigin(string? origin, string protocols, bool accepted)
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, $"bridge-ep-sub-{origin?.Length}-{protocols.Length}");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());
        var client = app.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            if (origin is not null)
            {
                request.Headers.Origin = origin;
            }

            request.Headers.SecWebSocketProtocol = protocols
                .Replace("{name}", LocalBridgeLimits.ExtensionSubprotocol, StringComparison.Ordinal)
                .Replace("{key}", key, StringComparison.Ordinal);
        };
        var uri = new Uri(app.GetTestServer().BaseAddress, LocalBridgeEndpoint.Route);

        if (accepted)
        {
            using var socket = await client.ConnectAsync(uri, CancellationToken.None);
            Assert.Equal(LocalBridgeLimits.ExtensionSubprotocol, socket.SubProtocol);
            await socket.CloseOutputAsync(System.Net.WebSockets.WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
        }
        else
        {
            var error = await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(uri, CancellationToken.None));
            Assert.Contains("401", error.Message, StringComparison.Ordinal);
        }
    }

    // The extension heartbeat: a ping on channel 0 is answered with a pong on channel 0.
    [Fact]
    public async Task Ping_IsAnsweredWithPong_OnChannelZero()
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-ping");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());
        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);

        await helper.SendAsync(new System.Text.Json.Nodes.JsonObject { ["ch"] = 0, ["type"] = "ping" });

        await LocalBridgeTestHost.WaitUntilAsync(() => !helper.Pongs.IsEmpty, "a pong");
        Assert.Equal([0], helper.Pongs);
    }

    // A ping that names an MCP channel is not a heartbeat: ignored, never answered. Frames are handled in order,
    // so once the channel-0 ping's pong arrives the channel-5 ping before it has been dealt with.
    [Fact]
    public async Task Ping_OnANonZeroChannel_IsIgnored()
    {
        var (_, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-ping-5");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, new LocalBridgeRegistry());
        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);

        await helper.SendAsync(new System.Text.Json.Nodes.JsonObject { ["ch"] = 5, ["type"] = "ping" });
        await helper.SendAsync(new System.Text.Json.Nodes.JsonObject { ["ch"] = 0, ["type"] = "ping" });

        await LocalBridgeTestHost.WaitUntilAsync(() => !helper.Pongs.IsEmpty, "a pong");
        await Task.Delay(100);
        Assert.Equal([0], helper.Pongs);
    }

    // AC13: through the unchanged MCP client — initialize, tools/list and tools/call all ride the channel.
    [Fact]
    public async Task ConnectedBridge_ListsAndCallsTools()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-call");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry);
        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the bridge to register");

        var connection = await Factory(registry).ConnectAsync(server, NoSecrets, CancellationToken.None);
        await using var _ = connection.Connection;

        Assert.Equal("echo", Assert.Single(connection.Tools).Name);
        var channel = Assert.Single(helper.OpenedChannels);
        Assert.Contains($"helper:{channel}", await CallEchoAsync(connection), StringComparison.Ordinal);
    }

    // AC14: two MCP sessions share one socket without crossing replies.
    [Fact]
    public async Task Channels_AreIsolated()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-channels");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry);
        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the bridge to register");
        var factory = Factory(registry);

        var first = await factory.ConnectAsync(server, NoSecrets, CancellationToken.None);
        var second = await factory.ConnectAsync(server, NoSecrets, CancellationToken.None);
        await using var a = first.Connection;
        await using var b = second.Connection;

        var results = await Task.WhenAll(Enumerable.Range(0, 10).Select(i => CallEchoAsync(i % 2 == 0 ? first : second)));

        var channels = helper.OpenedChannels.Order().ToArray();
        Assert.Equal(2, channels.Length);
        Assert.Equal(5, results.Count(r => r.Contains($"helper:{channels[0]}", StringComparison.Ordinal)));
        Assert.Equal(5, results.Count(r => r.Contains($"helper:{channels[1]}", StringComparison.Ordinal)));

        // Disposing one client closes only its channel, and the other keeps working.
        await first.Connection.DisposeAsync();
        await LocalBridgeTestHost.WaitUntilAsync(() => helper.ClosedChannels.Count == 1, "the channel close frame");
        Assert.Contains("helper:", await CallEchoAsync(second), StringComparison.Ordinal);
    }

    // AC15: nothing connected → an immediate, curated throw, never a wait.
    [Fact]
    public async Task NotConnected_FailsFast()
    {
        var (server, _) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-down");
        var watch = Stopwatch.StartNew();

        await Assert.ThrowsAsync<LocalBridgeNotConnectedException>(() =>
            Factory(new LocalBridgeRegistry()).ConnectAsync(server, NoSecrets, CancellationToken.None));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"took {watch.Elapsed}");
    }

    // AC18: the channel cap refuses a channel, an oversize frame closes a channel — in either direction —
    // and in every case the bridge itself stays up.
    [Fact]
    public async Task Limits_CloseChannelNotBridge()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-ep-limits");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry);
        await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the bridge to register");
        var factory = Factory(registry);

        // Incoming oversize: the helper answers with a frame past the cap; that call faults — promptly, which is
        // why a TimeoutException from the guard below is a failure, not an acceptable throw.
        var big = await factory.ConnectAsync(server, NoSecrets, CancellationToken.None);
        await AssertFaultsPromptly(CallEchoAsync(big, new { oversize = true }));
        await big.Connection.DisposeAsync();

        // Outgoing oversize: an argument past the cap never leaves; that call fails.
        var outgoing = await factory.ConnectAsync(server, NoSecrets, CancellationToken.None);
        await AssertFaultsPromptly(CallEchoAsync(outgoing, new { text = new string('y', LocalBridgeLimits.MaxMessageBytes) }));
        await outgoing.Connection.DisposeAsync();

        // Channel cap: fill it, the next open is refused.
        var connection = registry.FindByKeyHash(server.BridgeKeyHash!)!;
        var held = new List<ModelContextProtocol.Protocol.ITransport>();
        for (var i = 0; i < LocalBridgeLimits.MaxChannels; i++)
        {
            held.Add(await connection.OpenChannelAsync(CancellationToken.None));
        }

        await Assert.ThrowsAsync<InvalidOperationException>(() => connection.OpenChannelAsync(CancellationToken.None));
        await held[0].DisposeAsync();

        // ...and the bridge still serves a fresh session.
        var fresh = await factory.ConnectAsync(server, NoSecrets, CancellationToken.None);
        await using var _ = fresh.Connection;
        Assert.Contains("helper:", await CallEchoAsync(fresh), StringComparison.Ordinal);
        foreach (var transport in held.Skip(1))
        {
            await transport.DisposeAsync();
        }
    }

    // AC19
    [Fact]
    public async Task Connect_InvalidatesToolCache_RateLimited()
    {
        const string user = "bridge-ep-invalidate";
        var clock = new TestTimeProvider();
        var registry = new LocalBridgeRegistry(clock);
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, user);
        var config = Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user);
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry);
        var start = await config.GetVersionAsync();

        async Task ConnectAndDropAsync()
        {
            await using var helper = await LocalBridgeTestHost.ConnectTestServerAsync(app, key);
            await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the bridge to register");
            await helper.CloseAsync();
            await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is null, "the bridge to unregister");
        }

        await ConnectAndDropAsync();
        Assert.Equal(start + 1, await config.GetVersionAsync()); // connect bumped, disconnect did not

        await ConnectAndDropAsync();
        Assert.Equal(start + 1, await config.GetVersionAsync()); // inside the interval: no bump

        clock.Offset = LocalBridgeLimits.InvalidateMinInterval;
        await ConnectAndDropAsync();
        Assert.Equal(start + 2, await config.GetVersionAsync());
    }

    // AC20
    [Fact]
    public async Task Decorator_RoutesOnlyLocalBridge()
    {
        var inner = new RecordingInnerFactory();
        var factory = Factory(new LocalBridgeRegistry(), inner);

        foreach (var mode in new[] { McpAuthMode.None, McpAuthMode.Header, McpAuthMode.ClientCredentials, McpAuthMode.InteractiveOAuth })
        {
            await factory.ConnectAsync(
                new StoredMcpServer($"s-{mode}", "x", "https://example.com/mcp", mode.ToString(), Enabled: true), NoSecrets, CancellationToken.None);
        }

        await Assert.ThrowsAsync<LocalBridgeNotConnectedException>(() => factory.ConnectAsync(
            new StoredMcpServer("b", "x", "", nameof(McpAuthMode.LocalBridge), Enabled: true, BridgeKeyHash: "h"), NoSecrets, CancellationToken.None));

        Assert.Equal(["s-None", "s-Header", "s-ClientCredentials", "s-InteractiveOAuth"], inner.Calls);
    }

    private sealed class RecordingInnerFactory : IMcpConnectionFactory
    {
        public List<string> Calls { get; } = [];

        public Task<McpServerConnection> ConnectAsync(StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
        {
            Calls.Add(server.Id);
            return Task.FromResult(new McpServerConnection([], new NoopDisposable()));
        }
    }

    private sealed class NoopDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
