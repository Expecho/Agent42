using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The outbound webhook's fire site (ADR 0094): the one helper all three run sites call once a turn has been
/// durably persisted — the chat persist in <c>ChatTurnAsync</c>, the image turn's own write, and the reminder's own
/// write. Every branch lives in here, so each site stays one unconditional call (S3776, rule 9).
///
/// <para><b>The gate is the PERSISTED flag.</b> At the chat site the flag is the return of
/// <c>PersistTurnUnlessEphemeralAsync</c>, which decided from the run's own <see cref="TurnContext"/> (ADR 0106).</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Hands one completed turn to the bound agent's webhook grain. A no-op when the turn was not persisted or the
    /// conversation is not bound to a stored agent (so an unbound chat never activates a webhook grain). The call is
    /// <c>[OneWay]</c> and wrapped: a webhook can never fault or stall a turn that is already recorded.
    /// </summary>
    private async Task NotifyAgentWebhookAsync(bool persisted, WebhookTurn turn)
    {
        if (!persisted || _state.State.AgentId is not { Length: > 0 } agentId)
        {
            return;
        }

        var (prompt, promptTruncated) = WebhookLimits.Truncate(turn.Prompt, WebhookLimits.MaxPromptChars);
        var (reply, replyTruncated) = WebhookLimits.Truncate(turn.Reply, WebhookLimits.MaxReplyChars);
        var evt = new AgentWebhookEvent(
            AgentWebhookEvent.TurnCompleted, agentId, _state.State.AgentName ?? "", DateTimeOffset.UtcNow,
            ConversationIdFromGrainKey(this.GetPrimaryKeyString()), turn.Trigger, turn.ReminderName,
            prompt, promptTruncated, reply, replyTruncated, turn.Stopped, turn.ImageCount, turn.ReplacesLastTurn);

        try
        {
            await GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(_userId, agentId)).DeliverAsync(evt);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} could not hand its turn to the webhook; the turn is already persisted",
                this.GetPrimaryKeyString());
        }
    }

    // A reminder's reply: the LAST assistant message with text, never response.Text — a research loop (ADR 0082)
    // returns every iteration's messages, and their concatenation is not the reply the owner sees.
    private static string LastAssistantText(AgentResponse response)
        => response.Messages.LastOrDefault(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text))
            ?.Text ?? "";

    // The trigger of a ChatCoreAsync turn, from its TurnContext.Interactive.
    private static string ChatTrigger(bool interactive)
        => interactive ? AgentWebhookEvent.Triggers.Interactive : AgentWebhookEvent.Triggers.Background;

    /// <summary>What one run site knows about the turn it just persisted.</summary>
    private sealed record WebhookTurn(
        string Trigger, string Prompt, string Reply, bool Stopped = false, int ImageCount = 0,
        string? ReminderName = null, bool ReplacesLastTurn = false);
}
