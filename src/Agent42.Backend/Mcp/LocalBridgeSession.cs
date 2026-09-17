using System.Buffers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// One accepted bridge socket (ADR 0076): the receive loop, channel routing, the channel and message caps,
/// and close. Several MCP sessions share the socket — the tool provider retains live clients for two overlay
/// versions and the connection tester opens its own — so each <see cref="OpenChannelAsync"/> is a channel
/// the helper maps to its own upstream session. One <see cref="LocalBridgeEnvelope"/> per text message.
///
/// <para><b>Overflow closes a channel, never the bridge.</b> A channel beyond
/// <see cref="LocalBridgeLimits.MaxChannels"/> is refused, and a frame beyond
/// <see cref="LocalBridgeLimits.MaxMessageBytes"/> — either direction — closes the channel it belongs to.
/// The channel id of an oversize incoming frame is read from its first bytes, which is why both envelope
/// copies serialise <c>ch</c> first.</para>
/// </summary>
public sealed class LocalBridgeSession : ILocalBridgeConnection
{
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(5);

    private readonly WebSocket _socket;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly Dictionary<int, LocalBridgeChannelTransport> _channels = [];
    private readonly CancellationTokenSource _stop = new();
    private int _nextChannel;
    private bool _closed;

    public LocalBridgeSession(WebSocket socket, ILoggerFactory loggerFactory)
    {
        _socket = socket;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<LocalBridgeSession>();
    }

    public async Task<ITransport> OpenChannelAsync(CancellationToken cancellationToken)
    {
        LocalBridgeChannelTransport channel;
        lock (_gate)
        {
            if (_closed)
            {
                throw new LocalBridgeNotConnectedException();
            }

            if (_channels.Count >= LocalBridgeLimits.MaxChannels)
            {
                throw new InvalidOperationException("The local bridge has too many open channels.");
            }

            channel = new LocalBridgeChannelTransport(++_nextChannel, this, _loggerFactory);
            _channels[channel.Channel] = channel;
        }

        try
        {
            await SendAsync(new LocalBridgeEnvelope(channel.Channel, LocalBridgeEnvelope.Open), cancellationToken);
        }
        catch
        {
            Forget(channel.Channel)?.Fault(null);
            throw;
        }

        return channel;
    }

    /// <summary>
    /// Runs the receive loop until the socket closes, the peer vanishes, or <see cref="CloseAsync"/> is
    /// called. On exit every open channel is faulted, so no MCP request is left waiting on a dead socket.
    /// </summary>
    public async Task RunAsync(CancellationToken requestAborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stop.Token);
        Exception? error = null;
        try
        {
            await ReceiveLoopAsync(linked.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
            error = ex;
        }
        finally
        {
            FaultAll(error ?? new IOException("The local bridge closed."));
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(LocalBridgeLimits.MaxMessageBytes);
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var (count, oversize, kind) = await ReceiveMessageAsync(buffer, cancellationToken);
                if (kind == WebSocketMessageType.Close)
                {
                    await AcknowledgeCloseAsync();
                    return;
                }

                if (kind != WebSocketMessageType.Text)
                {
                    continue; // binary frames are not part of the contract
                }

                if (oversize)
                {
                    await CloseOversizeChannelAsync(buffer.AsSpan(0, count).ToArray());
                    continue;
                }

                await RouteAsync(Encoding.UTF8.GetString(buffer, 0, count));
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // The helper started a clean close: complete the handshake so it is not left waiting for our half.
    private async Task AcknowledgeCloseAsync()
    {
        if (_socket.State != WebSocketState.CloseReceived)
        {
            return;
        }

        using var timeout = new CancellationTokenSource(CloseGrace);
        await _sendGate.WaitAsync(timeout.Token);
        try
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "closed", timeout.Token);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    // Reads one whole message into buffer. Past the cap, keeps the first MaxMessageBytes and drains the rest,
    // so the socket stays usable and the channel id can still be read from the prefix.
    private async Task<(int Count, bool Oversize, WebSocketMessageType Kind)> ReceiveMessageAsync(
        byte[] buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        var oversize = false;
        var drain = new byte[4096];
        while (true)
        {
            var target = count < LocalBridgeLimits.MaxMessageBytes
                ? new ArraySegment<byte>(buffer, count, LocalBridgeLimits.MaxMessageBytes - count)
                : new ArraySegment<byte>(drain);
            var result = await _socket.ReceiveAsync(target, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return (0, false, WebSocketMessageType.Close);
            }

            if (count < LocalBridgeLimits.MaxMessageBytes)
            {
                count += result.Count;
            }
            else
            {
                oversize |= result.Count > 0;
            }

            if (result.EndOfMessage)
            {
                return (count, oversize, result.MessageType);
            }
        }
    }

    private async Task RouteAsync(string json)
    {
        var envelope = LocalBridgeEnvelope.TryParse(json);
        if (envelope is null)
        {
            _logger.LogInformation("Ignoring a malformed local-bridge frame.");
            return;
        }

        LocalBridgeChannelTransport? channel;
        lock (_gate)
        {
            channel = _channels.GetValueOrDefault(envelope.Channel);
        }

        if (channel is null)
        {
            return; // a late frame for a channel already closed, or one the backend never opened
        }

        switch (envelope.Type)
        {
            case LocalBridgeEnvelope.MessageType when envelope.Message is { } payload:
                await DeliverOrCloseAsync(channel, payload);
                break;
            case LocalBridgeEnvelope.Close:
                Forget(channel.Channel)?.Fault(new IOException("The local bridge closed this channel."));
                break;
        }
    }

    private async Task DeliverOrCloseAsync(LocalBridgeChannelTransport channel, JsonElement payload)
    {
        try
        {
            await channel.DeliverAsync(payload);
        }
        catch (JsonException ex)
        {
            // Not a JSON-RPC message: that channel's session is unusable, the bridge is not.
            _logger.LogInformation(ex, "Closing local-bridge channel {Channel}: its frame was not JSON-RPC.", channel.Channel);
            await FaultAndCloseChannelAsync(channel.Channel, ex);
        }
    }

    private async Task CloseOversizeChannelAsync(byte[] prefix)
    {
        if (TryReadChannel(prefix) is { } id)
        {
            _logger.LogInformation("Closing local-bridge channel {Channel}: a frame exceeded {Limit} bytes.", id, LocalBridgeLimits.MaxMessageBytes);
            await FaultAndCloseChannelAsync(id, new InvalidOperationException("A local-bridge frame exceeded the size limit."));
        }
    }

    // Reads the leading "ch" property of a (possibly truncated) envelope.
    private static int? TryReadChannel(byte[] prefix)
    {
        var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
        try
        {
            if (reader.Read() && reader.TokenType == JsonTokenType.StartObject
                && reader.Read() && reader.TokenType == JsonTokenType.PropertyName && reader.ValueTextEquals("ch")
                && reader.Read() && reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
            // fall through: an unreadable prefix names no channel
        }

        return null;
    }

    /// <summary>
    /// Sends one envelope. WebSocket sends must not overlap, hence the gate. A frame over the size cap closes
    /// its channel and throws — the MCP client sees a failed send, the bridge carries on.
    /// </summary>
    internal async Task SendAsync(LocalBridgeEnvelope envelope, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(envelope.Serialize());
        if (bytes.Length > LocalBridgeLimits.MaxMessageBytes)
        {
            var error = new InvalidOperationException("A local-bridge frame exceeded the size limit.");
            await FaultAndCloseChannelAsync(envelope.Channel, error);
            throw error;
        }

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>Forgets a channel the backend is done with and tells the helper, best-effort.</summary>
    internal async Task CloseChannelAsync(int channel)
    {
        if (Forget(channel) is null || _socket.State != WebSocketState.Open)
        {
            return;
        }

        try
        {
            await SendAsync(new LocalBridgeEnvelope(channel, LocalBridgeEnvelope.Close), _stop.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The socket is going away; the helper drops the channel with it.
        }
    }

    private async Task FaultAndCloseChannelAsync(int channel, Exception error)
    {
        LocalBridgeChannelTransport? transport;
        lock (_gate)
        {
            transport = _channels.GetValueOrDefault(channel);
        }

        transport?.Fault(error);
        await CloseChannelAsync(channel);
    }

    public async Task CloseAsync(string reason)
    {
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            _closed = true;
        }

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(CloseGrace);
                await _sendGate.WaitAsync(timeout.Token);
                try
                {
                    // CloseOutputAsync, not CloseAsync: the receive loop is the only reader, and it sees the
                    // helper's acknowledging close frame and exits. The grace timer below is the backstop.
                    await _socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, reason, timeout.Token);
                }
                finally
                {
                    _sendGate.Release();
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // Already gone.
        }
        finally
        {
            _stop.CancelAfter(CloseGrace);
            FaultAll(new IOException($"The local bridge was closed: {reason}"));
        }
    }

    private LocalBridgeChannelTransport? Forget(int channel)
    {
        lock (_gate)
        {
            return _channels.Remove(channel, out var removed) ? removed : null;
        }
    }

    private void FaultAll(Exception error)
    {
        List<LocalBridgeChannelTransport> open;
        lock (_gate)
        {
            _closed = true;
            open = [.. _channels.Values];
            _channels.Clear();
        }

        foreach (var channel in open)
        {
            channel.Fault(error);
        }
    }
}
