using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Agent42.Backend;
using Agent42.LocalBridge;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit.Abstractions;

namespace Agent42.Tests;

/// <summary>Runs only when <c>AGENT42_BRIDGE_E2E=1</c> — xunit 2 has no runtime skip, so the attribute decides.</summary>
public sealed class LiveBridgeFactAttribute : FactAttribute
{
    public LiveBridgeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENT42_BRIDGE_E2E") != "1")
        {
            Skip = "Set AGENT42_BRIDGE_E2E=1 with BrowserOS neo listening (AGENT42_BRIDGE_UPSTREAM, default http://127.0.0.1:9210/mcp).";
        }
    }
}

/// <summary>
/// ADR 0076 AC33, end to end against a REAL BrowserOS neo on this machine: real Kestrel, a key minted by the
/// real grain, the real helper <see cref="BridgeClient"/> relaying through <see cref="HttpUpstream"/>, and the
/// backend's <see cref="LocalBridgeConnectionFactory"/> + <c>McpClient</c> on the other side. Only the person
/// is scripted. Opt-in, because it opens and drives a tab in the signed-in browser — DevHost cannot run this
/// path at all (its tool provider ignores the overlay and its chat client is canned).
/// </summary>
public sealed partial class LocalBridgeLiveTests(SharedClusterFixture fixture, ITestOutputHelper output) : SharedClusterTests(fixture)
{
    private const string AgentName = "agent42-bridge-e2e";

    [LiveBridgeFact]
    public async Task RealNeo_ListsReadsGatesAndRevokes()
    {
        var upstream = new Uri(Environment.GetEnvironmentVariable("AGENT42_BRIDGE_UPSTREAM") ?? "http://127.0.0.1:9210/mcp");
        const string user = "bridge-live-user";
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, user);
        await using var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry, kestrel: true);

        var prompt = new QueuedPrompt();
        var log = new ConcurrentQueue<string>();
        var options = new BridgeOptions(new Uri(LocalBridgeTestHost.KestrelUri(app).GetLeftPart(UriPartial.Authority).Replace("ws://", "http://")),
            upstream, key, new HashSet<string>());
        var helper = new BridgeClient(options, new HttpUpstream(upstream), new ConfirmationGate(prompt), log.Enqueue,
            (_, ct) => Task.Delay(TimeSpan.FromMilliseconds(100), ct));
        using var stopHelper = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var running = helper.RunAsync(stopHelper.Token);

        try
        {
            await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the helper to connect");

            var connection = await new LocalBridgeConnectionFactory(new NoInner(), registry, NullLoggerFactory.Instance)
                .ConnectAsync(server, new McpConnectionSecrets(null, null), CancellationToken.None);
            var client = Assert.IsAssignableFrom<McpClient>(connection.Connection);
            await using var _ = client;
            output.WriteLine($"tools/list through the bridge: {connection.Tools.Count} tools; negotiated {client.NegotiatedProtocolVersion}");
            Assert.Contains(connection.Tools, t => t.Name == "snapshot");

            // tabs is NOT read-only → prompts. Answer "always": the close below must then run unprompted.
            prompt.Answers.Enqueue(ConfirmationAnswer.AllowForSession);
            var opened = await client.CallToolAsync("tabs", new Dictionary<string, object?> { ["action"] = "new", ["url"] = "about:blank", ["agentName"] = AgentName });
            var session = SessionOf(opened);
            var page = PageOf(opened);
            output.WriteLine($"tabs new → page {page}, session {session}, prompts so far {prompt.Seen.Count}: {Text(opened)}");
            Assert.Single(prompt.Seen);

            var args = new Dictionary<string, object?> { ["page"] = page, ["agentName"] = AgentName, ["session"] = session };

            // snapshot IS read-only and carries no URL → no prompt.
            var snapshot = await client.CallToolAsync("snapshot", args);
            output.WriteLine($"snapshot isError={snapshot.IsError}, prompts {prompt.Seen.Count}");
            Assert.True(snapshot.IsError != true, Text(snapshot));
            Assert.Single(prompt.Seen);

            // navigate, DENIED: an isError result the helper wrote itself; the tab does not move.
            prompt.Answers.Enqueue(ConfirmationAnswer.Deny);
            var denied = await client.CallToolAsync("navigate", new Dictionary<string, object?>(args) { ["action"] = "url", ["url"] = "https://example.com/" });
            output.WriteLine($"navigate (deny) isError={denied.IsError}: {Text(denied)}");
            Assert.True(denied.IsError);
            Assert.Equal(ConfirmationGate.DeclinedText, Text(denied));
            Assert.Equal("navigate", prompt.Seen.Last().Tool);
            var afterDeny = Text(await client.CallToolAsync("read", new Dictionary<string, object?>(args) { ["format"] = "text" }));
            Assert.DoesNotContain("Example Domain", afterDeny, StringComparison.Ordinal);

            // navigate, ALLOWED: the tab really moves.
            prompt.Answers.Enqueue(ConfirmationAnswer.Allow);
            var allowed = await client.CallToolAsync("navigate", new Dictionary<string, object?>(args) { ["action"] = "url", ["url"] = "https://example.com/" });
            output.WriteLine($"navigate (allow) isError={allowed.IsError}: {Text(allowed)[..Math.Min(200, Text(allowed).Length)]}");
            Assert.True(allowed.IsError != true, Text(allowed));
            var afterAllow = Text(await client.CallToolAsync("read", new Dictionary<string, object?>(args) { ["format"] = "text" }));
            output.WriteLine($"read after allow: {afterAllow[..Math.Min(120, afterAllow.Length)]}");
            Assert.Contains("Example Domain", afterAllow, StringComparison.Ordinal);
            Assert.Equal(3, prompt.Seen.Count);

            // The session approval from the first tabs prompt still stands: no fourth prompt.
            var closed = await client.CallToolAsync("tabs", new Dictionary<string, object?> { ["action"] = "close", ["page"] = page, ["agentName"] = AgentName, ["session"] = session });
            Assert.True(closed.IsError != true, Text(closed));
            Assert.Equal(3, prompt.Seen.Count);

            // Regenerate, exactly as the API does it: the grain rotates the key, the registry closes the socket.
            // The helper reconnects with the old key, meets 401, and stops on its own.
            Assert.NotNull(await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>(user).MintBridgeKeyAsync(server.Id));
            await registry.EvictAsync(user, server.Id, "bridge key regenerated");
            var exit = await running.WaitAsync(TimeSpan.FromSeconds(30));
            output.WriteLine($"after regenerate the helper exited with {exit}; log: {string.Join(" | ", log)}");
            Assert.Equal(BridgeExit.Unauthorized, exit);
        }
        finally
        {
            await stopHelper.CancelAsync();
            await running.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private static string? SessionOf(CallToolResult result) =>
        result.Meta?["com.browseros.neo/session"]?.GetValue<string>();

    private static int PageOf(CallToolResult result)
    {
        var json = JsonSerializer.Serialize(result);
        var match = PageId().Match(Text(result));
        Assert.True(match.Success, $"no page id in: {json}");
        return int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
    }

    // Measured on neo 0.0.53: `tabs new` answers "opened page 2" as its first text block.
    [GeneratedRegex(@"opened page (\d+)")]
    private static partial Regex PageId();

    private sealed class QueuedPrompt : IConfirmationPrompt
    {
        public ConcurrentQueue<ConfirmationAnswer> Answers { get; } = new();

        public ConcurrentQueue<(string Tool, string Arguments)> Seen { get; } = new();

        public Task<ConfirmationAnswer> ConfirmAsync(string toolName, string arguments, CancellationToken cancellationToken)
        {
            Seen.Enqueue((toolName, arguments));
            return Task.FromResult(Answers.TryDequeue(out var answer) ? answer : ConfirmationAnswer.Deny);
        }
    }

    private sealed class NoInner : IMcpConnectionFactory
    {
        public Task<McpServerConnection> ConnectAsync(StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
