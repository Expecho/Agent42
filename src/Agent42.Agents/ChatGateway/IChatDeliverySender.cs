namespace Agent42.Agents;

/// <summary>
/// The outbound port a conversation grain calls to put a result nobody is watching into a chat (ADR 0111): an away
/// reply (a reminder, a queued agent-run or email turn) or the model's <c>send_push_notification</c>. Implemented by
/// the Backend's chat gateway, in process (CLAUDE.md rule 12).
///
/// <para>A <b>per-host port</b> (ADR 0105, rule 7), like <see cref="IAgentWebhookSender"/>: every silo host registers
/// one before <c>AddAgent42Silo()</c> (Backend and DevHost the gateway's; the test silos a recording or the
/// <see cref="NullChatDeliverySender"/>). Delivery is best effort and never throws into the grain: a chat that cannot
/// be reached must not fail the turn, and Web Push still runs beside it.</para>
/// </summary>
public interface IChatDeliverySender
{
    Task DeliverAsync(string userId, ChatDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>What to deliver, and where it came from.</summary>
/// <param name="ConversationId">The conversation that produced it.</param>
/// <param name="Origin">The chat the conversation was started from, or <c>null</c> for a web conversation.</param>
/// <param name="Kind">An away reply (goes to the origin chat first) or a notification (goes to the home chat).</param>
/// <param name="Title">A short heading (the push title).</param>
/// <param name="Text">The reply or notification text, already bounded by the caller.</param>
public sealed record ChatDelivery(string ConversationId, ChatOrigin? Origin, ChatDeliveryKind Kind, string Title, string Text);

/// <summary>Where a delivery is routed.</summary>
public enum ChatDeliveryKind
{
    /// <summary>A turn's reply nobody watched: to its origin chat, else to the home chat when notifications are on.</summary>
    AwayReply,

    /// <summary>The model's <c>send_push_notification</c>: to the home chat when notifications are on.</summary>
    Notification,
}

/// <summary>The port's no-op, for silo hosts that run no chat gateway.</summary>
public sealed class NullChatDeliverySender : IChatDeliverySender
{
    public Task DeliverAsync(string userId, ChatDelivery delivery, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}
