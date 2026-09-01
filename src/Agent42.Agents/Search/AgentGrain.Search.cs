using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The chat-search half of <see cref="AgentGrain"/> (ADR 0061): projecting the conversation into
/// <see cref="IChatSearchIndex"/> after a persisted turn, purging it with the conversation, and the
/// user-invocable backfill.
///
/// <para><b>Every write here is best-effort</b> — its own try/catch, logged, never rethrown. That is
/// what ADR 0008's reasoning binds even though this feature is deliberately not 0008's shape: the
/// index is a projection, so losing a write costs search results and never conversation content, and
/// nothing about search may be able to fail a turn. The invisible-gap that follows is closed by
/// <see cref="ReindexAsync"/> being user-invocable and idempotent.</para>
///
/// <para><b>Ordinals come from one projection, and that is the whole design.</b> Both the live path
/// and the backfill index by the position of a turn in <c>GetHistoryAsync()</c>'s projected list, so
/// they cannot disagree by construction — which is what makes a re-index overwrite rather than
/// duplicate. A live path counting raw session messages against a backfill counting projected turns
/// would silently double-index every conversation that ever had a skipped turn.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// How many projected turns have already been written to the index during THIS activation.
    /// Activation-scoped and deliberately not persisted.
    ///
    /// <para>Starting at zero means the first indexed turn of an activation re-projects the whole
    /// conversation and every later one writes only what is new. That costs one O(n) pass per
    /// activation, bounded by the conversation itself (which ADR 0004 caps at 2 MB), and buys a
    /// property worth more than the writes: it <b>self-heals</b>. A write dropped by the best-effort
    /// rule in an earlier activation is silently repaired the next time the conversation is used,
    /// which narrows the invisible gap that best-effort would otherwise leave to the reindex
    /// endpoint alone.</para>
    /// </summary>
    private int _indexedTurnCount;

    /// <summary>
    /// Re-indexes the whole conversation from ordinal 0 (ADR 0061's backfill). Idempotent: the
    /// ordinals are the same ones the live path derives, so re-running overwrites the same document
    /// ids rather than appending. Returns the number of turns indexed.
    ///
    /// <para>Unlike the live path this does NOT swallow — the caller is an explicit user action
    /// (<c>POST /api/search/reindex</c>) and a failure it cannot see is worse than one it reports.
    /// </para>
    /// </summary>
    public async Task<int> ReindexAsync()
    {
        var turns = await GetHistoryAsync();
        var projected = ProjectForSearch(turns, fromOrdinal: 0);

        if (projected.Count > 0)
        {
            await _chatSearchIndex.IndexTurnsAsync(_userId, ConversationId(), projected);
        }

        // Keep the live path's bookkeeping consistent with what is now on disk, so the next turn of
        // an already-warm grain appends instead of redundantly re-projecting.
        _indexedTurnCount = turns.Count;
        return projected.Count;
    }

    /// <summary>
    /// Indexes the turns added since the last index write of this activation. Best-effort: any
    /// failure is logged and swallowed, because a turn must never fail because search did.
    /// </summary>
    private async Task IndexNewTurnsForSearchAsync()
    {
        try
        {
            var turns = await GetHistoryAsync();

            // Clamped rather than assumed: a purge (or any future path that shortens history) would
            // otherwise leave the counter past the end and skip every subsequent turn.
            var from = Math.Min(_indexedTurnCount, turns.Count);
            var projected = ProjectForSearch(turns, from);

            if (projected.Count > 0)
            {
                await _chatSearchIndex.IndexTurnsAsync(_userId, ConversationId(), projected);
            }

            _indexedTurnCount = turns.Count;
        }
        catch (Exception ex)
        {
            // Swallowed on purpose (ADR 0061). The conversation is already durably written by the
            // time this runs; the projection can be rebuilt from it at any time by a reindex.
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} failed to index its turns for search (best-effort)",
                this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// Marks an ephemeral turn's projected turns as "already accounted for" WITHOUT indexing them,
    /// so a later normal turn on the same activation cannot re-project them into the index.
    ///
    /// <para><b>This is the incognito guarantee, and it is not obvious.</b> Skipping the index write
    /// on the ephemeral turn is not enough: an incognito turn stays in the in-memory session (ADR
    /// 0034 promises "not persisted, not listed", explicitly not "not in memory"), so the next
    /// normal turn's re-projection would sweep it up and write it after all. Measured — the test
    /// <c>Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne</c> fails without this, indexing the
    /// incognito text at its original ordinal.</para>
    ///
    /// <para>The cost is that the activation's self-heal is given up from this point on: turns
    /// before the counter are no longer re-checked. That is the right trade — re-indexing something
    /// incognito is a correctness failure, while a missed repair is what the reindex endpoint is
    /// for.</para>
    /// </summary>
    private async Task SkipIndexingForEphemeralTurnAsync()
    {
        try
        {
            _indexedTurnCount = (await GetHistoryAsync()).Count;
        }
        catch (Exception ex)
        {
            // Best-effort like the writes, but failing CLOSED: if the count cannot be read, leave the
            // counter where it is and accept a redundant re-index of already-indexed turns rather
            // than risk advancing past turns that were never written.
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} could not advance its search index cursor past an "
                + "ephemeral turn (best-effort)", this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// Removes this conversation's indexed messages. Best-effort for the same reason as the writes,
    /// and additionally because this runs inside <c>PurgeAsync</c>, which
    /// <c>DELETE /api/conversations/{id}</c> awaits synchronously on a non-reentrant grain — a slow
    /// or failing index delete must not hang or fail the user's delete.
    /// </summary>
    private async Task DeleteSearchIndexForConversationAsync()
    {
        try
        {
            await _chatSearchIndex.DeleteConversationAsync(_userId, ConversationId());
            _indexedTurnCount = 0;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex, "Agent grain {GrainKey} failed to delete its search index entries (best-effort)",
                this.GetPrimaryKeyString());
        }
    }

    /// <summary>
    /// The single projection both paths share. Turns are numbered by their position in
    /// <paramref name="turns"/> — never by a running counter the caller keeps — so the live path and
    /// the backfill produce identical document ids for identical history.
    /// </summary>
    private static List<ChatSearchTurn> ProjectForSearch(IReadOnlyList<ChatTurn> turns, int fromOrdinal)
    {
        var projected = new List<ChatSearchTurn>();
        for (var ordinal = Math.Max(fromOrdinal, 0); ordinal < turns.Count; ordinal++)
        {
            var turn = turns[ordinal];

            // An image-only turn projects to empty text; there is nothing to search, and indexing it
            // would put an unmatchable document in the corpus for every generated image.
            if (!string.IsNullOrWhiteSpace(turn.Text))
            {
                projected.Add(new ChatSearchTurn(ordinal, turn.Role, turn.Text));
            }
        }

        return projected;
    }

    private string ConversationId() => ConversationIdFromGrainKey(this.GetPrimaryKeyString());
}
