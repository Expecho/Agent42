using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// The idle retention sweep of <see cref="AgentGrain"/> (ADR 0140): an Orleans reminder that prunes a chat whose agent set
/// a retention window while nobody is in it, and deletes the whole chat once everything in it has expired. The shape is
/// decision (<see cref="SweepRetentionAsync"/>) / delete (<see cref="DeleteExpiredChatAsync"/>) / prune and persist
/// (<see cref="PersistSweepAsync"/>), kept apart for the cognitive-complexity gate.
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Registers the sweep reminder for a chat bound with a retention value (ADR 0140). Called after the bind's state
    /// write, and never for an ephemeral bind (incognito leaves no trace, ADR 0034/0134). Idempotent.
    /// </summary>
    private async Task RegisterRetentionSweepAsync()
    {
        if (_state.State.RetentionMinutes is not { } minutes)
        {
            return;
        }

        var period = RetentionLimits.SweepPeriod(TimeSpan.FromMinutes(minutes));
        await this.RegisterOrUpdateReminder(RetentionLimits.SweepReminderName, period, period);
    }

    private async Task UnregisterRetentionSweepAsync()
    {
        if (await this.GetReminder(RetentionLimits.SweepReminderName) is { } reminder)
        {
            await this.UnregisterReminder(reminder);
        }
    }

    /// <summary>
    /// One tick of the sweep. Dispatched from <c>ReceiveReminder</c> BEFORE anything that runs a model, pushes or counts
    /// usage. Never runs over an in-flight turn (ADR 0106): a tick that lands between two <c>MoveNext</c> calls of a
    /// streaming turn skips, and the next one retries.
    /// </summary>
    private async Task SweepRetentionAsync()
    {
        if (_runsInFlight > 0)
        {
            return;
        }

        // A stale tick — never bound, no retention value, or already purged — unregisters itself.
        if (_state.State.RetentionMinutes is not { } minutes || string.IsNullOrEmpty(_state.State.AgentId))
        {
            await UnregisterRetentionSweepAsync();
            return;
        }

        // A session that never held a message has no history list at all; one that was persisted but has none is unknown,
        // and unknown is left alone rather than deleted.
        List<ChatMessage> messages = [];
        if (_session.TryGetInMemoryChatHistory(out List<ChatMessage>? existing) && existing is not null)
        {
            messages = existing;
        }
        else if (!string.IsNullOrEmpty(_state.State.SerializedSession))
        {
            return;
        }

        var window = TimeSpan.FromMinutes(minutes);
        var needsStamp = messages.Exists(m => m.CreatedAt is null);

        // An empty bound chat (its first turn never persisted) has nothing to preserve: delete it.
        if (messages.Count == 0)
        {
            await DeleteExpiredChatAsync();
            return;
        }

        // Stamp, then prune (leaves at least the newest turn). Unstamped messages are stamped "now", so unknown age
        // keeps the turn; the chat is deleted only when the newest message carries a stamp older than the window.
        var pruned = ApplyRetention();
        if (messages[^1].CreatedAt is { } newest && _timeProvider.GetUtcNow() - newest > window)
        {
            await DeleteExpiredChatAsync();
            return;
        }

        if (pruned || needsStamp)
        {
            await PersistSweepAsync(pruned);
        }
    }

    private async Task PersistSweepAsync(bool pruned)
    {
        var serializedSession = await _agent.SerializeSessionAsync(_session);
        _state.State.SerializedSession = JsonSerializer.Serialize(serializedSession);
        await _state.WriteStateAsync();

        // AFTER the durable write, like every persist site (ADR 0068).
        await _search.ReconcileAfterPruneAsync(pruned);
        await _search.IndexNewTurnsAsync();
    }

    /// <summary>
    /// The whole-chat delete, with the effect of the Delete button (<c>DELETE /api/conversations/{id}</c>): purge the
    /// grain (session, search index, documents, reminders), remove the sidebar entry, revoke the share link — in that
    /// order — and reset the chat-app route so the next inbound message mints a fresh bound conversation.
    /// </summary>
    private async Task DeleteExpiredChatAsync()
    {
        var conversationId = ConversationId();
        var origin = _state.State.Origin;
        _logger.LogInformation("Agent grain {GrainKey} swept: every message expired, deleting the chat", this.GetPrimaryKeyString());

        await PurgeAsync();
        await GrainFactory.GetGrain<IConversationIndexGrain>(_userId).RemoveAsync(conversationId);
        await GrainFactory.GetGrain<IUserShareLinksGrain>(_userId).RemoveAsync(conversationId);

        if (origin is not null)
        {
            await ResetChatRouteAsync(origin, conversationId);
        }
    }

    // Only a route still pointing at THIS conversation is reset: one that moved on (/new, a rollover) is left alone.
    private async Task ResetChatRouteAsync(ChatOrigin origin, string conversationId)
    {
        var route = GrainFactory.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(_userId, origin.ConnectionId, origin.RouteKey));
        if ((await route.GetAsync()).ConversationId == conversationId)
        {
            await route.StartNewAsync();
        }
    }

    /// <summary>
    /// A fork starts its own window (ADR 0140): its copied messages carry the SOURCE's stamps, so a fork cut at an early
    /// exchange would otherwise be swept at its first tick while the user looks at it.
    /// </summary>
    private void RestampSessionNow()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        foreach (var message in messages)
        {
            message.CreatedAt = now;
        }
    }
}
