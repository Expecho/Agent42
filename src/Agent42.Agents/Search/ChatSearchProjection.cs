using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Projects one conversation into <see cref="IChatSearchIndex"/> (ADR 0061): after a persisted turn, on a prune
/// (ADR 0068), on purge, and on the user-invocable backfill. Constructed by the grain in <c>OnActivateAsync</c> and
/// owned by that activation (ADR 0106). It never touches grain state: it reads the history through the delegate it is
/// given and writes only the index.
///
/// <para><b>Every write here is best-effort</b> — its own try/catch, logged, never rethrown. That is
/// what ADR 0008's reasoning binds even though this feature is deliberately not 0008's shape: the
/// index is a projection, so losing a write costs search results and never conversation content, and
/// nothing about search may be able to fail a turn. The invisible-gap that follows is closed by
/// <see cref="ReindexAsync"/> being user-invocable and idempotent.</para>
///
/// <para><b>Ordinals come from one projection, and that is the whole design.</b> Both the live path
/// and the backfill index by the position of a turn in the grain's projected history (the delegate below,
/// the same list <c>GetHistoryAsync</c> returns), so they cannot disagree by construction — which is what makes a
/// re-index overwrite rather than duplicate. A live path counting raw session messages against a backfill counting
/// projected turns would silently double-index every conversation that ever had a skipped turn.</para>
/// </summary>
public sealed class ChatSearchProjection(
    IChatSearchIndex index, ILogger logger, string grainKey, Func<IReadOnlyList<ChatTurn>> history)
    : IChatSearchProjection
{
    private readonly string _userId = AgentGrain.UserIdFromGrainKey(grainKey);

    private readonly string _conversationId = AgentGrain.ConversationIdFromGrainKey(grainKey);

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
    /// Ordinals this activation served as <b>ephemeral</b> turns, which therefore must never be
    /// indexed — by the live path or by <see cref="ReindexAsync"/>.
    ///
    /// <para>A cursor alone is not enough, and that gap is easy to miss. Advancing
    /// <see cref="_indexedTurnCount"/> past an incognito turn keeps the <i>live</i> path off it, but
    /// <see cref="ReindexAsync"/> re-projects from ordinal 0 by design — so without this set a
    /// reindex on the same activation would write exactly the text the incognito guarantee exists to
    /// keep out, at ordinals the live path had deliberately skipped.</para>
    ///
    /// <para>Activation-scoped, like the cursor. See the residual in ADR 0061: once a <i>later</i>
    /// normal turn persists the session, that write carries the whole session — ephemeral messages
    /// included, which is ADR 0034's own pre-existing behaviour — so after a deactivation those turns
    /// are indistinguishable from ordinary history and a reindex will pick them up. The shipped UI
    /// cannot produce such a mixed conversation; the API can.</para>
    /// </summary>
    private readonly HashSet<int> _ephemeralOrdinals = [];

    /// <summary>
    /// True when a retention prune (ADR 0068) has deleted turns whose index documents have NOT yet
    /// been removed, because <c>DeleteConversationAsync</c> threw. Retried on the next persisted turn.
    ///
    /// <para><b>This marks the one place ADR 0061's best-effort rule does not reach.</b> Every other
    /// index write may be swallowed because the projection can be rebuilt from the conversation at any
    /// time — a licence that rests explicitly on <see cref="ReindexAsync"/> being a user-invocable
    /// repair. That repair only UPSERTS: <c>DeleteConversationAsync</c> is the sole removal in
    /// <see cref="IChatSearchIndex"/>. So a swallowed delete after a destructive prune leaves orphaned
    /// tail documents that nothing else can clean up — a three-turn conversation returning five hits,
    /// two of them text that no longer exists anywhere.</para>
    ///
    /// <para><b>It is a bool and NOT an accumulated delta, and that is a correctness fix rather than a
    /// simplification.</b> <c>DeleteConversationAsync</c> removes the whole conversation whatever the
    /// delta is, so a carried count was only ever consumed by the ephemeral-ordinal remap — and
    /// carrying it across turns put it in the wrong reference frame. An ephemeral turn taken BETWEEN a
    /// failed reconcile and its retry records its ordinals in the already-pruned frame, so applying the
    /// earlier prune's shift to them a second time walks them backwards onto real turns: the incognito
    /// text is then indexed and two genuine turns are skipped. The remap therefore happens where the
    /// shift happens — in <see cref="RemapAfterPrune"/>, called by the retention prune — and this flag carries
    /// nothing but "retry me".</para>
    ///
    /// <para><b>Activation-scoped, and that is a real residual rather than self-healing.</b> If the
    /// grain deactivates while this is set, the retry is lost and the orphans become permanent: reindex
    /// only upserts, and the only other <c>DeleteConversationAsync</c> caller is the purge.
    /// Recorded in ADR 0068 rather than papered over.</para>
    /// </summary>
    private bool _reconcilePending;

    public bool HasEphemeralTurns => _ephemeralOrdinals.Count > 0;

    /// <summary>
    /// Re-anchors the search index after a retention prune shifted every ordinal down: drop the
    /// conversation's documents, reset the cursor, and let the caller's own
    /// <see cref="IndexNewTurnsAsync"/> re-project from ordinal 0.
    ///
    /// <para>Rebuilding wholesale rather than deleting a range is not laziness — there IS no
    /// delete-by-ordinal-range. Without it the next write upserts over the low ordinals and leaves the
    /// tail documents behind, indefinitely.</para>
    ///
    /// <para>Called AFTER the caller's <c>WriteStateAsync</c>, at all three persist sites.
    /// <paramref name="pruned"/> says whether THIS turn dropped anything; a pending retry runs even
    /// when it did not. The ephemeral-ordinal remap is deliberately NOT done here — see
    /// <see cref="_reconcilePending"/> for why that frame matters.</para>
    /// </summary>
    public async Task ReconcileAfterPruneAsync(bool pruned)
    {
        if (!pruned && !_reconcilePending)
        {
            return;
        }

        try
        {
            await index.DeleteConversationAsync(_userId, _conversationId);

            // Only once the awaited delete has SUCCEEDED — the ordering DeleteConversationAsync below uses, and for a
            // sharper reason here: resetting the cursor after a swallowed failure would re-project the whole
            // conversation over ordinals whose stale documents are still there.
            _indexedTurnCount = 0;
            _reconcilePending = false;
        }
        catch (Exception ex)
        {
            _reconcilePending = true;
            logger.LogWarning(
                ex, "Agent grain {GrainKey} failed to re-anchor its search index after a retention "
                + "prune; retrying on the next persisted turn", grainKey);
        }
    }

    /// <summary>
    /// Shifts the recorded incognito ordinals down by the pruned amount, dropping any that fell off
    /// the front. Called by the retention prune, at the moment the shift actually happens
    /// — deferring it to the reconcile puts it in the wrong reference frame (see
    /// <see cref="_reconcilePending"/>).
    ///
    /// <para><b>Never <c>Clear()</c> here.</b> <see cref="DeleteConversationAsync"/>
    /// clears, and is right to — the conversation is going away. This runs on a conversation that is
    /// still alive and about to be re-projected, and an incognito turn (ADR 0034) stays in the
    /// in-memory session for the rest of the activation. Clearing would hand that re-projection
    /// exactly the text the guarantee exists to keep out of Cosmos, at its new ordinal.</para>
    /// </summary>
    public void RemapAfterPrune(int droppedProjectedTurns)
    {
        if (_ephemeralOrdinals.Count == 0)
        {
            return;
        }

        var remapped = _ephemeralOrdinals
            .Select(ordinal => ordinal - droppedProjectedTurns)
            .Where(ordinal => ordinal >= 0)
            .ToList();

        _ephemeralOrdinals.Clear();
        foreach (var ordinal in remapped)
        {
            _ephemeralOrdinals.Add(ordinal);
        }
    }

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
        var turns = history();
        var projected = Project(turns, fromOrdinal: 0, _ephemeralOrdinals);

        if (projected.Count > 0)
        {
            await index.IndexTurnsAsync(_userId, _conversationId, projected);
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
    public async Task IndexNewTurnsAsync()
    {
        try
        {
            var turns = history();

            // Clamped rather than assumed: a purge (or any future path that shortens history) would
            // otherwise leave the counter past the end and skip every subsequent turn.
            var from = Math.Min(_indexedTurnCount, turns.Count);
            var projected = Project(turns, from, _ephemeralOrdinals);

            if (projected.Count > 0)
            {
                await index.IndexTurnsAsync(_userId, _conversationId, projected);
            }

            _indexedTurnCount = turns.Count;
        }
        catch (Exception ex)
        {
            // Swallowed on purpose (ADR 0061). The conversation is already durably written by the
            // time this runs; the projection can be rebuilt from it at any time by a reindex.
            logger.LogWarning(
                ex, "Agent grain {GrainKey} failed to index its turns for search (best-effort)", grainKey);
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
    public Task SkipEphemeralTurnAsync()
    {
        try
        {
            var count = history().Count;

            // Record WHICH ordinals were incognito, not merely how far we have got: ReindexAsync
            // re-projects from 0 and would otherwise index them.
            for (var ordinal = _indexedTurnCount; ordinal < count; ordinal++)
            {
                _ephemeralOrdinals.Add(ordinal);
            }

            _indexedTurnCount = count;
        }
        catch (Exception ex)
        {
            // Best-effort like the writes, but failing CLOSED: if the count cannot be read, leave the
            // counter where it is and accept a redundant re-index of already-indexed turns rather
            // than risk advancing past turns that were never written.
            logger.LogWarning(
                ex, "Agent grain {GrainKey} could not advance its search index cursor past an "
                + "ephemeral turn (best-effort)", grainKey);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Removes this conversation's indexed messages. Best-effort for the same reason as the writes,
    /// and additionally because this runs inside the grain's <c>PurgeAsync</c>, which
    /// <c>DELETE /api/conversations/{id}</c> awaits synchronously on a non-reentrant grain — a slow
    /// or failing index delete must not hang or fail the user's delete.
    /// </summary>
    public async Task DeleteConversationAsync()
    {
        try
        {
            await index.DeleteConversationAsync(_userId, _conversationId);
            _indexedTurnCount = 0;
            _ephemeralOrdinals.Clear();
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex, "Agent grain {GrainKey} failed to delete its search index entries (best-effort)", grainKey);
        }
    }

    public void TruncateTo(int projectedCount, bool pullBackCursor)
    {
        _ephemeralOrdinals.RemoveWhere(ordinal => ordinal >= projectedCount);
        if (pullBackCursor)
        {
            _indexedTurnCount = Math.Min(_indexedTurnCount, projectedCount);
        }
    }

    /// <summary>
    /// The single projection both paths share. Turns are numbered by their position in
    /// <paramref name="turns"/> — never by a running counter the caller keeps — so the live path and
    /// the backfill produce identical document ids for identical history.
    /// </summary>
    public static List<ChatSearchTurn> Project(
        IReadOnlyList<ChatTurn> turns, int fromOrdinal, IReadOnlySet<int> ephemeralOrdinals)
    {
        var projected = new List<ChatSearchTurn>();
        for (var ordinal = Math.Max(fromOrdinal, 0); ordinal < turns.Count; ordinal++)
        {
            // Never index a turn this activation served incognito, whichever path is projecting.
            if (ephemeralOrdinals.Contains(ordinal))
            {
                continue;
            }

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
}
