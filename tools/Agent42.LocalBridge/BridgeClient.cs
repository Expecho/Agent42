using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Agent42.LocalBridge;

/// <summary>How <see cref="BridgeClient.RunAsync"/> ended.</summary>
public enum BridgeExit
{
    /// <summary>The caller cancelled (Ctrl+C).</summary>
    Cancelled,

    /// <summary>The server refused the key. Never retried — a revoked key must not hammer the endpoint.</summary>
    Unauthorized,

    /// <summary>
    /// Another helper connected with the same key and took over. Not retried either: two helpers that both
    /// reconnect would displace each other every second, failing every in-flight call each time.
    /// </summary>
    Replaced,
}

/// <summary>
/// The helper's side of the socket (ADR 0076): dials <c>/api/local-bridge</c> with the bridge key in
/// <c>X-Api-Key</c>, maps each backend channel to its own <see cref="UpstreamChannel"/>, and reconnects with
/// exponential backoff (1 s → 60 s) — except on 401, where it stops.
///
/// <para><b>The key goes in the header only</b>, never the URL: a query string would land in access logs,
/// and the server does not read one anyway.</para>
/// </summary>
public sealed class BridgeClient
{
    public const string ApiKeyHeader = "X-Api-Key";
    public const string Route = "/api/local-bridge";

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan KeepAlive = TimeSpan.FromSeconds(30);

    // Without a pong deadline a keep-alive only sends, and a dead path (a sleeping laptop's Wi-Fi) is noticed
    // only when TCP gives up; with it the socket aborts and the reconnect loop takes over.
    private static readonly TimeSpan KeepAliveTimeout = TimeSpan.FromSeconds(20);

    // Must equal LocalBridgeLimits.ReplacedCloseReason in the backend — pinned by a test.
    public const string ReplacedCloseReason = "replaced by a newer connection";
    private const int MaxFrameBytes = 1024 * 1024;

    private readonly BridgeOptions _options;
    private readonly IUpstream _upstream;
    private readonly ConfirmationGate _gate;
    private readonly Action<string> _log;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public BridgeClient(
        BridgeOptions options,
        IUpstream upstream,
        ConfirmationGate gate,
        Action<string> log,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _options = options;
        _upstream = upstream;
        _gate = gate;
        _log = log;
        _delay = delay ?? Task.Delay;
    }

    /// <summary>The socket URI for a server base URL: http→ws, https→wss, path <see cref="Route"/>.</summary>
    public static Uri SocketUri(Uri server)
    {
        var builder = new UriBuilder(server)
        {
            Scheme = server.Scheme == Uri.UriSchemeHttps ? "wss" : "ws",
            Path = server.AbsolutePath.TrimEnd('/') + Route,
            Query = "",
        };
        return builder.Uri;
    }

    public async Task<BridgeExit> RunAsync(CancellationToken cancellationToken)
    {
        var backoff = MinBackoff;
        while (!cancellationToken.IsCancellationRequested)
        {
            var (exit, connected) = await ConnectOnceAsync(cancellationToken);
            if (exit is { } final)
            {
                return final;
            }

            if (connected)
            {
                backoff = MinBackoff;
            }

            try
            {
                _log($"reconnecting in {backoff.TotalSeconds:0} s.");
                await _delay(backoff, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, MaxBackoff.Ticks));
        }

        return BridgeExit.Cancelled;
    }

    // One connection's whole life. Returns a final exit (stop, do not reconnect) or null to reconnect, and
    // whether the connect itself succeeded (which resets the backoff).
    private async Task<(BridgeExit? Exit, bool Connected)> ConnectOnceAsync(CancellationToken cancellationToken)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader(ApiKeyHeader, _options.Key);
        socket.Options.KeepAliveInterval = KeepAlive;
        socket.Options.KeepAliveTimeout = KeepAliveTimeout;
        socket.Options.CollectHttpResponseDetails = true;
        try
        {
            await socket.ConnectAsync(SocketUri(_options.Server), cancellationToken);
            _log($"connected to {_options.Server}; relaying to {_options.Upstream}.");
            await ServeAsync(socket, cancellationToken);
            if (socket.CloseStatusDescription == ReplacedCloseReason)
            {
                _log("another local bridge connected with this key and took over. Stopping.");
                return (BridgeExit.Replaced, true);
            }

            _log("disconnected.");
            return (null, true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return (BridgeExit.Cancelled, false);
        }
        catch (WebSocketException) when (socket.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            _log("the server rejected the bridge key (401). It was regenerated, or the server was deleted, disabled or switched away from Local bridge. Stopping.");
            return (BridgeExit.Unauthorized, false);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or HttpRequestException)
        {
            var status = socket.HttpStatusCode == 0 ? "" : $" (HTTP {(int)socket.HttpStatusCode})";
            _log($"connection failed{status}: {ex.Message}");
            return (null, false);
        }
    }

    private async Task ServeAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var sendGate = new SemaphoreSlim(1, 1);
        var channels = new Dictionary<int, UpstreamChannel>();

        async Task SendAsync(BridgeEnvelope envelope)
        {
            var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
            await sendGate.WaitAsync(cancellationToken);
            try
            {
                await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
            }
            finally
            {
                sendGate.Release();
            }
        }

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(socket, cancellationToken);
                if (text is null)
                {
                    return;
                }

                await DispatchAsync(BridgeEnvelope.TryParse(text), channels, SendAsync);
            }
        }
        finally
        {
            foreach (var channel in channels.Values)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private async Task DispatchAsync(BridgeEnvelope? envelope, Dictionary<int, UpstreamChannel> channels, Func<BridgeEnvelope, Task> send)
    {
        switch (envelope)
        {
            case { Type: BridgeEnvelope.Open } open when !channels.ContainsKey(open.Channel):
                channels[open.Channel] = new UpstreamChannel(open.Channel, _upstream, _gate, send, _log);
                break;
            case { Type: BridgeEnvelope.MessageType, Message: { } message } frame
                when channels.TryGetValue(frame.Channel, out var channel):
                channel.Enqueue(message);
                break;
            case { Type: BridgeEnvelope.Close } close when channels.Remove(close.Channel, out var closed):
                await closed.DisposeAsync();
                break;
        }
    }

    // One whole text message, or null when the server closed. Oversize frames are dropped whole.
    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        var overflow = false;
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                if (socket.State == WebSocketState.CloseReceived)
                {
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", cancellationToken);
                }

                return null;
            }

            overflow |= message.Length + result.Count > MaxFrameBytes;
            if (!overflow)
            {
                message.Write(buffer, 0, result.Count);
            }

            if (result.EndOfMessage)
            {
                // An empty string parses as no envelope, so a binary or oversize frame is simply ignored.
                return result.MessageType == WebSocketMessageType.Text && !overflow
                    ? Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length)
                    : "";
            }
        }
    }
}
