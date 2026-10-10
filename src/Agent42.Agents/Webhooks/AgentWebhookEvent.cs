namespace Agent42.Agents;

/// <summary>
/// One outbound webhook event (ADR 0094): what <see cref="AgentGrain"/> hands to its agent's
/// <see cref="IAgentWebhookGrain"/> when a bound turn has been durably persisted, or the content-free event the
/// "Send test" button sends. The Backend sender turns it into the signed JSON body; the delivery id and timestamp are
/// assigned at send time, not here.
///
/// <para><b>Already truncated</b> when it is built (<see cref="WebhookLimits"/>), so an oversized event never crosses
/// the grain boundary. It never carries an attachment or SAS URL (credential-bearing, ADR 0004/0013), only
/// <see cref="ImageCount"/>.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentWebhookEvent")]
public sealed record AgentWebhookEvent(
    [property: Id(0)] string Type,
    [property: Id(1)] string AgentId,
    [property: Id(2)] string AgentName,
    [property: Id(3)] DateTimeOffset OccurredAt,
    [property: Id(4)] string? ConversationId = null,
    [property: Id(5)] string? Trigger = null,
    [property: Id(6)] string? ReminderName = null,
    [property: Id(7)] string? Prompt = null,
    [property: Id(8)] bool PromptTruncated = false,
    [property: Id(9)] string? Reply = null,
    [property: Id(10)] bool ReplyTruncated = false,
    [property: Id(11)] bool Stopped = false,
    [property: Id(12)] int ImageCount = 0,
    [property: Id(13)] bool ReplacesLastTurn = false)
{
    /// <summary>A completed, persisted turn of a bound conversation.</summary>
    public const string TurnCompleted = "agent.turn.completed";

    /// <summary>The "Send test" button's event: no conversation content at all.</summary>
    public const string Test = "webhook.test";

    /// <summary>The <see cref="Trigger"/> values (ADR 0094 decision 9).</summary>
    public static class Triggers
    {
        public const string Interactive = "interactive";
        public const string Background = "background";
        public const string Reminder = "reminder";
    }
}
