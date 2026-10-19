using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The history-retention half of <see cref="AgentGrain"/> (ADR 0016): the gate that decides whether a conversation is
/// windowed, and the one call each persist site makes into <see cref="HistoryRetention"/> (ADR 0106), which owns the
/// stamp and prune rules.
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>This activation's retention rules, built in <c>OnActivateAsync</c> beside the search projection.</summary>
    private IHistoryRetention _retention = null!;

    /// <summary>
    /// Stamp, then prune. The single entry point the persist sites call immediately before their
    /// serialize; returns whether anything was dropped, which the caller passes to
    /// <see cref="ChatSearchProjection.ReconcileAfterPruneAsync"/> AFTER its state write. The incognito ordinals the
    /// removal shifted are remapped here, at once, in the frame the shift happened in.
    ///
    /// <para><b>The whole method is gated on the conversation being continuous, and the STAMP is inside
    /// that gate deliberately.</b> A per-chat conversation is never pruned, so a timestamp on its
    /// messages is never read — and it is not free: measured, <c>createdAt</c> costs about 35 bytes a
    /// message, ~1.2% of the very 2 MB item (ADR 0004) this feature exists to defend. Charging that to
    /// every conversation in the app to serve the handful that are continuous is the wrong trade, so
    /// the gate is here rather than inside the prune alone. It is safe
    /// because a conversation cannot change its id, and the id is what makes it continuous.</para>
    /// </summary>
    private bool ApplyRetention()
    {
        if (!IsRetentionWindowed()
            || !_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return false;
        }

        var result = _retention.Apply(new RetentionContext(
            messages, _state.State.TurnAttachments, _state.State.GeneratedImages, () => ProjectHistory().Count));
        if (result.Pruned)
        {
            _search.RemapAfterPrune(result.DroppedProjectedTurns);
        }

        return result.Pruned;
    }

    /// <summary>
    /// True when this grain's history is bounded to <see cref="RetentionLimits.Window"/> — the only
    /// conversations this feature stamps or prunes. See
    /// <see cref="RetentionLimits.ContinuousConversationIdPrefix"/> for why an id shape is the signal
    /// and no ADR 0016 reversal is needed.
    ///
    /// <para><b>Two prefixes qualify, not one.</b> ADR 0016's continuous chats were the original case;
    /// ADR 0070's MCP conversations are the second, and they are here because the growth is worse
    /// rather than merely similar — an external caller reusing one id has no UI, no sidebar row and no
    /// reason to ever start a fresh one, so nothing bounds that session at all. A browser chat, by
    /// contrast, gets a new id per conversation and is deliberately left unstamped: see the
    /// per-message byte cost in <see cref="ApplyRetention"/>'s remarks.</para>
    /// </summary>
    private bool IsRetentionWindowed()
    {
        var id = ConversationId();
        return id.StartsWith(RetentionLimits.ContinuousConversationIdPrefix, StringComparison.Ordinal)
            || id.StartsWith(AgentMcpLimits.ConversationIdPrefix, StringComparison.Ordinal);
    }
}
