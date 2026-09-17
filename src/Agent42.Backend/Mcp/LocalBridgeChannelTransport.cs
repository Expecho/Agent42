using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Agent42.Backend;

/// <summary>
/// One MCP session over one channel of a bridge socket (ADR 0076), shaped as an ordinary transport so
/// <c>McpClient.CreateAsync</c> runs over it unchanged. Measured in the Step 0 spike (MCP SDK 2.2.0): a
/// <see cref="TransportBase"/> subclass that calls <see cref="TransportBase.SetConnected"/> up front needs
/// nothing of <c>SessionId</c>, and <see cref="TransportBase.SetDisconnected"/> faults a pending request
/// within a millisecond.
///
/// <para><b>Socket loss must reach this transport.</b> <see cref="LocalBridgeSession"/> calls
/// <see cref="Fault"/> on every open channel when the socket dies; without it a pending <c>tools/call</c>
/// would hang the non-reentrant <c>AgentGrain</c> turn that is waiting on it.
/// <c>SocketDrop_FaultsPendingCall</c> is mutation-checked against exactly that call.</para>
/// </summary>
public sealed class LocalBridgeChannelTransport : TransportBase
{
    private readonly LocalBridgeSession _session;

    public LocalBridgeChannelTransport(int channel, LocalBridgeSession session, ILoggerFactory loggerFactory)
        : base($"local-bridge:{channel}", loggerFactory)
    {
        Channel = channel;
        _session = session;
        SetConnected();
    }

    /// <summary>The channel id this transport's envelopes carry.</summary>
    public int Channel { get; }

    public override async Task SendMessageAsync(JsonRpcMessage message, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new InvalidOperationException("The local bridge channel is closed.");
        }

        var payload = JsonSerializer.SerializeToElement(message, McpJsonUtilities.DefaultOptions);
        await _session.SendAsync(new LocalBridgeEnvelope(Channel, LocalBridgeEnvelope.MessageType, payload), cancellationToken);
    }

    /// <summary>Hands one message from the helper to the MCP client. Called in arrival order by the session.</summary>
    internal async Task DeliverAsync(JsonElement payload)
    {
        var message = payload.Deserialize<JsonRpcMessage>(McpJsonUtilities.DefaultOptions);
        if (message is not null && IsConnected)
        {
            await WriteMessageAsync(message);
        }
    }

    /// <summary>Faults every pending request on this channel (socket lost, channel closed by the helper).</summary>
    internal void Fault(Exception? error) => SetDisconnected(error);

    public override async ValueTask DisposeAsync()
    {
        if (IsConnected)
        {
            SetDisconnected();
            await _session.CloseChannelAsync(Channel);
        }
    }
}
