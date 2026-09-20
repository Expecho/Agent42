using System.Net.WebSockets;
using Agent42.Agents;
using Agent42.Backend;
using Agent42.LocalBridge;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The two local-bridge claims that are about a real transport (ADR 0076), so they run on real Kestrel over
/// a loopback socket — ADR 0046 measured that TestServer's in-memory pipe cannot reproduce a dead
/// connection, and a test there would pass against broken code.
/// </summary>
public sealed class LocalBridgeKestrelTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly McpConnectionSecrets NoSecrets = new(null, null);

    // AC16. The helper's socket is aborted while a tools/call is waiting for an answer that will never come.
    // The call must fault promptly: a hang here is a non-reentrant AgentGrain turn stuck on a dead bridge.
    // Mutation-checked: without FaultAll in LocalBridgeSession.RunAsync's finally, this times out.
    [Fact]
    public async Task SocketDrop_FaultsPendingCall()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-kestrel-drop");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry, kestrel: true);
        await using var helper = await LocalBridgeTestHost.ConnectKestrelAsync(app, key);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the bridge to register");

        var connection = await new LocalBridgeConnectionFactory(new NullInner(), registry, NullLoggerFactory.Instance)
            .ConnectAsync(server, NoSecrets, CancellationToken.None);
        await using var _ = connection.Connection;
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(connection.Tools));

        var pending = tool.InvokeAsync(new AIFunctionArguments(new Dictionary<string, object?> { ["hang"] = true })).AsTask();
        await Task.Delay(200);
        Assert.False(pending.IsCompleted, "the hanging call answered — the test would prove nothing");

        await helper.AbortAsync(); // the TCP connection dies, no close handshake

        var finished = await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.Same(pending, finished);
        await Assert.ThrowsAnyAsync<Exception>(() => pending);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is null, "the dead bridge to unregister");
    }

    // AC17. A second helper for the same server takes over; the first is closed with a policy status and
    // calls route to the second.
    [Fact]
    public async Task SecondConnection_ReplacesFirst()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-kestrel-replace");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry, kestrel: true);

        await using var first = await LocalBridgeTestHost.ConnectKestrelAsync(app, key, "first");
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the first bridge");
        var firstSession = registry.FindByKeyHash(server.BridgeKeyHash!);

        await using var second = await LocalBridgeTestHost.ConnectKestrelAsync(app, key, "second");
        await LocalBridgeTestHost.WaitUntilAsync(
            () => registry.FindByKeyHash(server.BridgeKeyHash!) is { } now && !ReferenceEquals(now, firstSession), "the second bridge");

        await first.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WebSocketCloseStatus.PolicyViolation, first.CloseStatus);
        Assert.Equal(WebSocketState.Open, second.Socket.State);

        var connection = await new LocalBridgeConnectionFactory(new NullInner(), registry, NullLoggerFactory.Instance)
            .ConnectAsync(server, NoSecrets, CancellationToken.None);
        await using var _ = connection.Connection;
        var tool = Assert.IsAssignableFrom<AIFunction>(Assert.Single(connection.Tools));
        var result = (await tool.InvokeAsync(new AIFunctionArguments()))?.ToString();
        Assert.Contains("second:", result, StringComparison.Ordinal);
        Assert.Empty(first.OpenedChannels);
    }

    // Two REAL helpers on one key: the newer one takes over and the displaced one STOPS rather than
    // reconnecting and displacing it back — which would fail every in-flight call about once a second.
    [Fact]
    public async Task DisplacedHelper_Stops()
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, "bridge-kestrel-duel");
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry, kestrel: true);
        var baseUri = new Uri(LocalBridgeTestHost.KestrelUri(app).GetLeftPart(UriPartial.Authority).Replace("ws://", "http://"));

        BridgeClient NewHelper() => new(
            new BridgeOptions(baseUri, BridgeOptions.DefaultUpstream, key, new HashSet<string>()),
            new HttpUpstream(BridgeOptions.DefaultUpstream),
            new ConfirmationGate(new NeverAsked()),
            _ => { },
            (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(50), ct));

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = NewHelper().RunAsync(stop.Token);
        await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the first helper");
        var firstSession = registry.FindByKeyHash(server.BridgeKeyHash!);

        var second = NewHelper().RunAsync(stop.Token);

        Assert.Equal(BridgeExit.Replaced, await first.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(500); // long enough for a (wrongly) reconnecting first helper to take over again
        var current = registry.FindByKeyHash(server.BridgeKeyHash!);
        Assert.NotNull(current);
        Assert.NotSame(firstSession, current);
        Assert.False(second.IsCompleted);

        await stop.CancelAsync();
        Assert.Equal(BridgeExit.Cancelled, await second.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    private sealed class NeverAsked : IConfirmationPrompt
    {
        public Task<ConfirmationAnswer> ConfirmAsync(string toolName, string arguments, CancellationToken cancellationToken) =>
            Task.FromResult(ConfirmationAnswer.Deny);
    }

    private sealed class NullInner : IMcpConnectionFactory
    {
        public Task<McpServerConnection> ConnectAsync(StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}
