using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// A host that serves only <c>/api/local-bridge</c> (ADR 0076) — no <c>MapAgentApi</c>, so none of its
/// parameter-inference floor is needed. In-memory by default; <paramref name="kestrel"/> for the tests
/// whose claim is about a real transport dying (ADR 0046 measured that TestServer cannot reproduce one).
/// </summary>
internal static class LocalBridgeTestHost
{
    public static async Task<WebApplication> StartAsync(IGrainFactory grains, LocalBridgeRegistry registry, bool kestrel = false)
    {
        var builder = WebApplication.CreateSlimBuilder();
        if (kestrel)
        {
            builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, port: 0));
        }
        else
        {
            builder.WebHost.UseTestServer();
        }

        builder.Services.AddSingleton(grains);
        builder.Services.AddSingleton(registry);
        var app = builder.Build();
        app.UseLocalBridge();
        await app.StartAsync();
        return app;
    }

    public static Uri KestrelUri(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri($"ws://127.0.0.1:{new Uri(address).Port}{LocalBridgeEndpoint.Route}");
    }

    /// <summary>Creates an enabled LocalBridge server for <paramref name="userId"/> and mints its key.</summary>
    public static async Task<(StoredMcpServer Server, string Key)> NewBridgeServerAsync(IGrainFactory grains, string userId, string id = "bridge")
    {
        var config = grains.GetGrain<IUserMcpConfigGrain>(userId);
        await config.SaveAsync(new StoredMcpServer(id, "Neo", "", nameof(McpAuthMode.LocalBridge), Enabled: true));
        var key = await config.MintBridgeKeyAsync(id) ?? throw new InvalidOperationException("mint refused");
        var server = (await config.GetAsync()).Servers.Single(s => s.Id == id);
        return (server, key);
    }

    /// <summary>Connects a fake helper over TestServer.</summary>
    public static async Task<FakeBridgeHelper> ConnectTestServerAsync(WebApplication app, string key, string label = "helper")
    {
        var client = app.GetTestServer().CreateWebSocketClient();
        client.ConfigureRequest = request => request.Headers["X-Api-Key"] = key;
        var socket = await client.ConnectAsync(new Uri(app.GetTestServer().BaseAddress, LocalBridgeEndpoint.Route), CancellationToken.None);
        return new FakeBridgeHelper(socket, label);
    }

    /// <summary>Connects a fake helper over a real loopback socket.</summary>
    public static async Task<FakeBridgeHelper> ConnectKestrelAsync(WebApplication app, string key, string label = "helper")
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("X-Api-Key", key);
        await socket.ConnectAsync(KestrelUri(app), CancellationToken.None);
        return new FakeBridgeHelper(socket, label);
    }

    /// <summary>Polls until <paramref name="condition"/> holds — the socket registers asynchronously.</summary>
    public static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        for (var i = 0; i < 200; i++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"Timed out waiting for {what}.");
    }
}

/// <summary>
/// The helper's side of the bridge, spoken as <b>raw JSON</b> — it never names the backend envelope type, so
/// it checks the wire, not a shared definition. It plays a tiny MCP server on every channel: one tool,
/// <c>echo</c>, whose result names this helper and the channel it came in on. Two arguments steer it:
/// <c>hang</c> never answers, and <c>oversize</c> answers with a frame past the backend's size cap.
/// </summary>
internal sealed class FakeBridgeHelper : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly string _label;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly Task _loop;

    public FakeBridgeHelper(WebSocket socket, string label)
    {
        _socket = socket;
        _label = label;
        _loop = Task.Run(LoopAsync);
    }

    public ConcurrentBag<int> OpenedChannels { get; } = [];

    public ConcurrentBag<int> ClosedChannels { get; } = [];

    /// <summary>Every <c>pong</c> control frame received, by the channel it named.</summary>
    public ConcurrentBag<int> Pongs { get; } = [];

    public WebSocketCloseStatus? CloseStatus => _socket.CloseStatus;

    /// <summary>Completes when the receive loop ends (the backend closed, or the socket died).</summary>
    public Task Completion => _loop;

    public WebSocket Socket => _socket;

    private async Task LoopAsync()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, CancellationToken.None);
                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                    }

                    return;
                }

                await HandleAsync(JsonNode.Parse(message.ToArray())!.AsObject());
            }
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or OperationCanceledException)
        {
            // the socket was aborted
        }
    }

    private async Task HandleAsync(JsonObject envelope)
    {
        var channel = envelope["ch"]!.GetValue<int>();
        switch (envelope["type"]!.GetValue<string>())
        {
            case "open":
                OpenedChannels.Add(channel);
                return;
            case "close":
                ClosedChannels.Add(channel);
                return;
            case "pong":
                Pongs.Add(channel);
                return;
        }

        var rpc = envelope["msg"]!.AsObject();
        if (rpc["id"] is not { } id)
        {
            return; // a notification
        }

        var method = rpc["method"]?.GetValue<string>();
        if (method is not ("initialize" or "tools/list" or "tools/call"))
        {
            // Answer like a pre-2026-07-28 server. MCP SDK 2.2.0 opens with a `server/discover` probe and only
            // falls back to `initialize` on an error — or after a 5 s timeout if the frame is dropped (measured).
            await SendAsync(new JsonObject
            {
                ["ch"] = channel,
                ["type"] = "message",
                ["msg"] = new JsonObject
                {
                    ["jsonrpc"] = "2.0",
                    ["id"] = id.DeepClone(),
                    ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" },
                },
            });
            return;
        }

        JsonNode? result = method switch
        {
            "initialize" => new JsonObject
            {
                ["protocolVersion"] = rpc["params"]!["protocolVersion"]!.GetValue<string>(),
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "fake-neo", ["version"] = "0" },
            },
            "tools/list" => new JsonObject
            {
                ["tools"] = new JsonArray(new JsonObject
                {
                    ["name"] = "echo",
                    ["inputSchema"] = new JsonObject { ["type"] = "object" },
                }),
            },
            "tools/call" => await CallAsync(channel, rpc["params"]!["arguments"]?.AsObject()),
            _ => null,
        };

        if (result is null)
        {
            return;
        }

        await SendAsync(new JsonObject
        {
            ["ch"] = channel,
            ["type"] = "message",
            ["msg"] = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result },
        });
    }

    private async Task<JsonNode?> CallAsync(int channel, JsonObject? arguments)
    {
        if (arguments?["hang"] is not null)
        {
            return null; // never answer
        }

        var text = $"{_label}:{channel}";
        if (arguments?["oversize"] is not null)
        {
            text = new string('x', LocalBridgeLimits.MaxMessageBytes + 1024);
        }

        await Task.Yield();
        return new JsonObject
        {
            ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
        };
    }

    public async Task SendAsync(JsonObject envelope)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.ToJsonString());
        await _send.WaitAsync();
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }
    }

    /// <summary>A clean close handshake, as a helper that is shutting down would do.</summary>
    public async Task CloseAsync()
    {
        await _send.WaitAsync();
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "helper stopping", CancellationToken.None);
        }
        finally
        {
            _send.Release();
        }

        await _loop.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Kills the connection with no close handshake — the dead-transport case.</summary>
    public async Task AbortAsync()
    {
        _socket.Abort();
        await _loop;
    }

    // Graceful where possible: TestServer's in-memory socket does not tell the server about an Abort (ADR 0046),
    // so aborting there would leave the endpoint running until host shutdown times out.
    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                await CloseAsync().WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex) when (ex is TimeoutException or WebSocketException or InvalidOperationException)
            {
                // fall through to the abort
            }
        }

        _socket.Abort();
        await _loop;
        _socket.Dispose();
    }
}
