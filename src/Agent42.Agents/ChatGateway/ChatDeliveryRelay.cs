using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Proactive chat delivery (ADR 0111, C9): the hand-off of an away reply or a model notification to
/// <see cref="IChatDeliverySender"/>. Both methods never throw: the reply is already persisted, and Web Push runs beside it.
///
/// <para>Built per grain activation with <c>new</c> (ADR 0106's pattern, extended by ADR 0147/0148); public only because this
/// repo has no <c>InternalsVisibleTo</c>. It holds no grain state: the origin and agent name are read through
/// <c>Func</c>s on every call, because <c>AgentGrain.SetChatOriginAsync</c> can set the origin after activation.</para>
///
/// <para><b>Reply text is accumulated only where an away reply is raised</b>, never in <c>ChatCoreAsync</c> (see
/// <c>AgentGrain.AwayReplyText</c>). Like <c>SendAwayReplyPushAsync</c> (ADR 0028), do not call this from
/// <c>ChatCoreAsync</c>.</para>
/// </summary>
public sealed class ChatDeliveryRelay(
    IChatDeliverySender sender, string userId, string conversationId, string grainKey, ILogger logger,
    Func<ChatOrigin?> origin, Func<string?> agentName)
{
    /// <summary>
    /// Hands an away reply's text to the chat gateway. Nothing for an incognito run (ADR 0034), an empty reply, or a web
    /// conversation whose model already notified the home chat itself this turn (<c>send_push_notification</c> delivers
    /// there too, ADR 0075).
    /// </summary>
    public async Task DeliverAwayReplyAsync(TurnContext turn, string text)
    {
        var currentOrigin = origin();
        if (turn.Ephemeral || string.IsNullOrWhiteSpace(text) || (currentOrigin is null && turn.PushesSent > 0))
        {
            return;
        }

        var (bounded, _) = WebhookLimits.Truncate(text, WebhookLimits.MaxReplyChars);
        try
        {
            await sender.DeliverAsync(
                userId,
                new ChatDelivery(
                    conversationId, currentOrigin, ChatDeliveryKind.AwayReply, agentName() ?? "Agent 42", bounded));
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Chat delivery failed for conversation {GrainKey}; the reply was already persisted.", grainKey);
        }
    }

    /// <summary>
    /// Hands the model's <c>send_push_notification</c> to the home chat, beside Web Push. Nothing for an incognito run
    /// (the tool refuses those before it gets here).
    /// </summary>
    public async Task DeliverNotificationAsync(TurnContext turn, string title, string body, CancellationToken cancellationToken)
    {
        if (turn.Ephemeral)
        {
            return;
        }

        try
        {
            await sender.DeliverAsync(
                userId,
                new ChatDelivery(conversationId, origin(), ChatDeliveryKind.Notification, title, body),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Chat delivery of a notification failed for conversation {GrainKey}.", grainKey);
        }
    }
}
