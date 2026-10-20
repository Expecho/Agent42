using System.Collections.Concurrent;
using System.Diagnostics;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;

namespace Agent42.Tests;

/// <summary>
/// The Chrome extension's real core (<c>lib/bridge.js</c>, <c>lib/mcp-server.js</c>, <c>lib/gate.js</c>,
/// <c>lib/tools.js</c>) over a fake browser, run by node (<c>test/harness/run-bridge.mjs</c>) against a real Kestrel
/// <c>/api/local-bridge</c>. The backend side is the unchanged ADR 0076 path: the real
/// <see cref="LocalBridgeConnectionFactory"/> and the SDK's <c>McpClient</c>. This is the plan's Step 0(f): a
/// hand-written JSON-RPC server accepted by SDK 2.2.0. The harness connects as the extension does: the extension's
/// Origin plus the subprotocol pair, never an X-Api-Key header on the socket.
/// </summary>
public sealed class ChromeExtensionBridgeTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly McpConnectionSecrets NoSecrets = new(null, null);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly Process _process;

        public Harness(Process process)
        {
            _process = process;
            _process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Lines.Enqueue(e.Data);
                }
            };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is not null)
                {
                    Lines.Enqueue("STDERR " + e.Data);
                }
            };
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
        }

        public ConcurrentQueue<string> Lines { get; } = [];

        public string Output => string.Join("\n", Lines);

        public async Task<int> ExitCodeWithin(TimeSpan timeout)
        {
            using var cts = new CancellationTokenSource(timeout);
            await _process.WaitForExitAsync(cts.Token);
            return _process.ExitCode;
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync();
            }

            _process.Dispose();
        }
    }

    private static Harness StartHarness(WebApplication app, string key, string answers = "", int? heartbeatMs = null)
    {
        var socket = LocalBridgeTestHost.KestrelUri(app);
        var psi = new ProcessStartInfo("node", Path.Combine(RepoRoot(), "tools", "Agent42.ChromeExtension", "test", "harness", "run-bridge.mjs"))
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.Environment["AGENT42_SERVER"] = $"http://127.0.0.1:{socket.Port}";
        psi.Environment["AGENT42_BRIDGE_KEY"] = key;
        psi.Environment["AGENT42_EXTENSION_ORIGIN"] = LocalBridgeLimits.ExtensionOrigin;
        psi.Environment["AGENT42_ANSWERS"] = answers;
        if (heartbeatMs is { } ms)
        {
            psi.Environment["AGENT42_HEARTBEAT_MS"] = ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
            psi.Environment["AGENT42_PONG_TIMEOUT_MS"] = PongTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return new Harness(Process.Start(psi)!);
    }

    private async Task<(WebApplication App, LocalBridgeRegistry Registry, StoredMcpServer Server, string Key, Harness Harness)> ConnectedAsync(
        string user, string answers = "", int? heartbeatMs = null)
    {
        var registry = new LocalBridgeRegistry();
        var (server, key) = await LocalBridgeTestHost.NewBridgeServerAsync(Cluster.GrainFactory, user);
        var app = await LocalBridgeTestHost.StartAsync(Cluster.GrainFactory, registry, kestrel: true);
        var harness = StartHarness(app, key, answers, heartbeatMs);
        try
        {
            await LocalBridgeTestHost.WaitUntilAsync(() => registry.FindByKeyHash(server.BridgeKeyHash!) is not null, "the extension to connect");
        }
        catch (TimeoutException)
        {
            Assert.Fail("The extension harness never connected:\n" + harness.Output);
        }

        return (app, registry, server, key, harness);
    }

    private static async Task<McpServerConnection> ToolsAsync(LocalBridgeRegistry registry, StoredMcpServer server)
        => await new LocalBridgeConnectionFactory(new NullInner(), registry, NullLoggerFactory.Instance)
            .ConnectAsync(server, NoSecrets, CancellationToken.None);

    private static async Task<object?> CallAsync(McpServerConnection connection, string name, Dictionary<string, object?>? args = null)
    {
        var tool = Assert.IsAssignableFrom<AIFunction>(connection.Tools.Single(t => t.Name == name));
        return await tool.InvokeAsync(new AIFunctionArguments(args ?? []));
    }

    // AC 5 (Step 0f): the SDK's McpClient lists the extension's tools, with the declared annotations, and calls one.
    [Fact]
    public async Task ExtensionCore_ListsAndCallsToolsThroughTheBridge()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-list");
        await using var __ = app;
        await using var ___ = harness;
        var connection = await ToolsAsync(registry, server);
        await using var _ = connection.Connection;

        var names = connection.Tools.Select(t => t.Name).ToList();
        Assert.Contains("list_tabs", names);
        Assert.Contains("screenshot", names);
        Assert.Contains("evaluate", names);
        var readOnly = connection.Tools.OfType<McpClientTool>().Where(t => t.ProtocolTool.Annotations?.ReadOnlyHint == true).Select(t => t.Name);
        Assert.Contains("list_tabs", readOnly);
        Assert.DoesNotContain("click", readOnly);

        Assert.Contains("Example Domain", (await CallAsync(connection, "list_tabs"))?.ToString(), StringComparison.Ordinal);
    }

    // AC 6: a write prompts; a deny is answered by the extension and the browser never sees the call.
    [Fact]
    public async Task ExtensionCore_DeniedCallNeverReachesTheBrowser()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-deny", answers: "deny");
        await using var __ = app;
        await using var ___ = harness;
        var connection = await ToolsAsync(registry, server);
        await using var _ = connection.Connection;

        var result = await CallAsync(connection, "click", new() { ["tabId"] = 1, ["ref"] = "e0" });

        Assert.Contains("The user declined this browser action.", result?.ToString(), StringComparison.Ordinal);
        Assert.Contains("PROMPT click", harness.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("CALL click", harness.Output, StringComparison.Ordinal);
    }

    // AC 7: evaluate runs without a prompt (the user decision).
    [Fact]
    public async Task ExtensionCore_EvaluateRunsUnprompted()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-eval");
        await using var __ = app;
        await using var ___ = harness;
        var connection = await ToolsAsync(registry, server);
        await using var _ = connection.Connection;

        var result = await CallAsync(connection, "evaluate", new() { ["tabId"] = 1, ["expression"] = "6 * 7" });

        Assert.Equal("42", result?.ToString());
        Assert.DoesNotContain("PROMPT", harness.Output, StringComparison.Ordinal);
        Assert.Contains("CALL evaluate", harness.Output, StringComparison.Ordinal);
    }

    // AC 9: a long page of non-ASCII and markup is paged by the extension, so the backend's cut never fires.
    [Fact]
    public async Task ExtensionCore_LargeResult_IsNeverTruncatedByTheBackend()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-large");
        await using var __ = app;
        await using var ___ = harness;
        var connection = await ToolsAsync(registry, server);
        await using var _ = connection.Connection;

        var raw = await CallAsync(connection, "read_text", new() { ["tabId"] = 1 });
        var shaped = McpToolResultShaper.Shape(raw, TurnContext.Closed, "call-1")?.ToString() ?? "";

        Assert.DoesNotContain(McpToolLimits.TruncationMarker, shaped, StringComparison.Ordinal);
        Assert.Contains("more characters. Call again with offset=", shaped, StringComparison.Ordinal);
    }

    // AC 9h: the screenshot crosses the real bridge and the real shaper into the turn as an image, not text.
    [Fact]
    public async Task ExtensionCore_Screenshot_ReachesTheTurnAsAnImage()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-shot");
        await using var __ = app;
        await using var ___ = harness;
        var connection = await ToolsAsync(registry, server);
        await using var _ = connection.Connection;
        var turn = TurnContext.ForChat(ephemeral: false, interactive: true, broker: null);
        turn.SetToolImagesVisible(true);

        var raw = await CallAsync(connection, "screenshot", new() { ["tabId"] = 1 });
        var shaped = Assert.IsType<string>(McpToolResultShaper.Shape(raw, turn, "call-1"));

        Assert.Contains("[image 1: image/jpeg", shaped, StringComparison.Ordinal);
        Assert.Contains("1280×720 image pixels", shaped, StringComparison.Ordinal);
        var image = Assert.Single(turn.ToolImagesFor("call-1"));
        Assert.Equal((byte)0xFF, image.Data.Span[0]);
    }

    // AC 8: a revoked key is seen at the preflight, and the extension stops without retrying.
    [Fact]
    public async Task ExtensionCore_StopsOnRevokedKey()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-revoke");
        await using var __ = app;
        await using var ___ = harness;

        Assert.NotNull(await Cluster.GrainFactory.GetGrain<IUserMcpConfigGrain>("chrome-ext-revoke").MintBridgeKeyAsync(server.Id));
        await registry.EvictAsync("chrome-ext-revoke", server.Id, "key regenerated");

        Assert.Equal(3, await harness.ExitCodeWithin(TimeSpan.FromSeconds(20)));
        Assert.Contains("STATUS key-rejected", harness.Output, StringComparison.Ordinal);
        Assert.Equal(2, harness.Lines.Count(l => l.StartsWith("STATUS connecting", StringComparison.Ordinal)));
    }

    // AC 8: a second bridge on the same key takes over, and the displaced extension stops instead of fighting back.
    [Fact]
    public async Task ExtensionCore_StopsWhenReplaced()
    {
        var (app, registry, server, key, first) = await ConnectedAsync("chrome-ext-replace");
        await using var __ = app;
        await using var ___ = first;
        var firstSession = registry.FindByKeyHash(server.BridgeKeyHash!);

        await using var second = StartHarness(app, key);

        Assert.Equal(4, await first.ExitCodeWithin(TimeSpan.FromSeconds(20)));
        Assert.Contains("STATUS replaced", first.Output, StringComparison.Ordinal);
        await LocalBridgeTestHost.WaitUntilAsync(
            () => registry.FindByKeyHash(server.BridgeKeyHash!) is { } now && !ReferenceEquals(now, firstSession), "the second bridge");
    }

    // The harness's pong deadline in the heartbeat test. It is generous so that a loaded CI box does not miss a pong
    // by milliseconds, but it is well inside the test's wait, so an unanswered ping would still close the socket.
    private const int PongTimeoutMs = 1500;

    // The heartbeat: about 20 pings, every 200 ms, over 4 s. The socket stays up only if the backend answers each one
    // within the deadline. A missing pong closes it and shows "retrying".
    [Fact]
    public async Task ExtensionCore_HeartbeatKeepsTheSocketOpen()
    {
        var (app, registry, server, _, harness) = await ConnectedAsync("chrome-ext-heartbeat", heartbeatMs: 200);
        await using var __ = app;
        await using var ___ = harness;
        var session = registry.FindByKeyHash(server.BridgeKeyHash!);

        await Task.Delay(TimeSpan.FromMilliseconds(PongTimeoutMs + 2500));

        Assert.DoesNotContain("STATUS retrying", harness.Output, StringComparison.Ordinal);
        Assert.Same(session, registry.FindByKeyHash(server.BridgeKeyHash!));
    }

    private sealed class NullInner : IMcpConnectionFactory
    {
        public Task<McpServerConnection> ConnectAsync(StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
