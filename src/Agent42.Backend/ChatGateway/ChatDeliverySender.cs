using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The Backend's <see cref="IChatDeliverySender"/> (ADR 0111, C9): puts an away reply or a model notification into a
/// chat nobody is typing in. In process (CLAUDE.md rule 12), so it can only reach connections whose session is live on
/// this replica.
///
/// <para><b>Where it goes.</b> An <see cref="ChatDeliveryKind.AwayReply"/> goes to its conversation's origin route when
/// that connection still exists, is paired and has a live session. Otherwise, and always for a
/// <see cref="ChatDeliveryKind.Notification"/>, it goes to the first connection with notifications on and a home route
/// whose session is live. When nothing qualifies nothing is sent: Web Push still ran.</para>
///
/// <para><b>Final-only.</b> It sends through the same <see cref="ChatReplyStreamer"/> a pipeline reply does, from a
/// completed buffer and with editing off, so the platform's formatting, chunking, fallback and per-call time-box are
/// reused rather than copied. It never throws: a chat that cannot be reached must not fail the turn that raised it.</para>
/// </summary>
public sealed class ChatDeliverySender : IChatDeliverySender
{
    private readonly IGrainFactory _grains;
    private readonly ChatSessionRegistry _sessions;
    private readonly ChatReplyStreamer _streamer;
    private readonly Dictionary<string, IChatPlatformAdapter> _adapters;
    private readonly ILogger<ChatDeliverySender> _logger;

    public ChatDeliverySender(
        IGrainFactory grains,
        ChatSessionRegistry sessions,
        ChatReplyStreamer streamer,
        IEnumerable<IChatPlatformAdapter> adapters,
        ILogger<ChatDeliverySender> logger)
    {
        _grains = grains;
        _sessions = sessions;
        _streamer = streamer;
        _adapters = adapters.ToDictionary(a => a.Kind, StringComparer.Ordinal);
        _logger = logger;
    }

    public async Task DeliverAsync(string userId, ChatDelivery delivery, CancellationToken cancellationToken = default)
    {
        try
        {
            var connections = await _grains.GetGrain<IUserChatConnectionsGrain>(userId).ListAsync();
            if (Choose(connections, delivery) is not var (connection, routeKey))
            {
                return;
            }

            await SendAsync(connection, routeKey, delivery, cancellationToken);
        }
        catch (Exception ex)
        {
            // Never the text: it is the owner's conversation.
            _logger.LogWarning(ex, "A chat delivery for conversation {ConversationId} could not be sent.", delivery.ConversationId);
        }
    }

    private (ChatConnection Connection, string RouteKey)? Choose(IReadOnlyList<ChatConnection> connections, ChatDelivery delivery)
    {
        if (delivery.Kind == ChatDeliveryKind.AwayReply && delivery.Origin is { } origin)
        {
            var source = connections.FirstOrDefault(c => c.Id == origin.ConnectionId);
            if (source is not null && IsReachable(source) && origin.RouteKey.Length > 0)
            {
                return (source, origin.RouteKey);
            }
        }

        var home = connections.FirstOrDefault(c => c.NotificationsEnabled && !string.IsNullOrEmpty(c.HomeRouteKey) && IsReachable(c));
        return home is null ? null : (home, home.HomeRouteKey!);
    }

    // Paired (the owner is known, so the chat is theirs), a known platform, and a live session on this replica.
    private bool IsReachable(ChatConnection connection)
        => connection.IsPaired && _adapters.ContainsKey(connection.Platform) && _sessions.Get(connection.Id) is not null;

    private async Task SendAsync(ChatConnection connection, string routeKey, ChatDelivery delivery, CancellationToken cancellationToken)
    {
        var buffer = new ChatReplyBuffer();
        buffer.Apply(new AgentStreamEvent(AgentStreamEventType.TextDelta, $"**{delivery.Title}**\n\n{delivery.Text}"));
        buffer.Complete();

        var destination = new ChatReplyDestination(
            () => _sessions.Get(connection.Id),
            ParseRoute(routeKey),
            _adapters[connection.Platform].Capabilities with { SupportsEdit = false });
        await _streamer.StreamAsync(destination, buffer, cancellationToken);
    }

    // The inverse of ChatRouteKey.For: "chat" or "chat#thread". A chat id never contains '#'.
    private static ChatTarget ParseRoute(string routeKey)
    {
        var split = routeKey.IndexOf('#', StringComparison.Ordinal);
        return split < 0 ? new ChatTarget(routeKey) : new ChatTarget(routeKey[..split], routeKey[(split + 1)..]);
    }
}
