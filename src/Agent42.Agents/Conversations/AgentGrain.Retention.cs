using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The history-retention half of <see cref="AgentGrain"/>: stamping every message with a creation
/// time, and pruning a CONTINUOUS agent chat (ADR 0016) back to
/// <see cref="RetentionLimits.Window"/> on every persisted turn.
///
/// <para><b>The grain has to stamp the timestamps itself.</b> Measured on Agent Framework 1.19:
/// neither run path sets <c>ChatMessage.CreatedAt</c> — after <c>RunStreamingAsync</c> (the
/// production chat path) every message reads back null, and the streaming coalescer drops even a
/// provider-supplied value. What the framework does do is <i>serialize</i> <c>createdAt</c> as part
/// of the session's state bag, so a value stamped in place on the list from
/// <c>TryGetInMemoryChatHistory</c> survives serialize → JSON → deserialize. That is the whole
/// reason this feature needs no new <c>[Id(n)]</c> on <see cref="AgentGrainState"/>, and therefore
/// no migration and no golden document.</para>
///
/// <para><b>The prune is a prefix of WHOLE user turns</b>, and each of the three rules that makes
/// it safe is a defect if dropped: a leading non-user prefix is never touched, whole turns keep a
/// <c>FunctionResultContent</c> with its <c>FunctionCallContent</c> (most providers reject an
/// orphan), and <c>TurnAttachments</c> is re-aligned by the same count so the positional join in
/// <c>ProjectUserTurn</c> still holds. <c>StripToolHistory</c> is the existing precedent for
/// deleting from the session in place, but it is safe today only because it never removes a
/// <b>user</b> message. This does, so it maintains that join itself.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Stamps <c>CreatedAt</c> on the session's messages, replacing any value that cannot be
    /// believed. Run before every serialize, at all three persist sites.
    ///
    /// <para>The rule is a clamp, not a bare <c>??=</c>: a value is replaced with "now" when it is
    /// <b>null</b> (the normal case, and the backfill for history that predates this feature),
    /// <b>in the future</b> (clock skew), or <b>older than
    /// <see cref="RetentionLimits.MaxPlausibleAge"/></b> (an epoch or default-zero value from a
    /// provider on the <c>RunAsync</c> path, which would otherwise mark a live turn expired and
    /// delete it on the next persist).</para>
    ///
    /// <para>Consequence worth knowing: existing conversations carry no timestamps, so the first
    /// persisted turn after deploy stamps their whole history with "now" and nothing becomes
    /// eligible for pruning until a window later. History is never pruned retroactively — the real
    /// creation times were never recorded and cannot be recovered.</para>
    /// </summary>
    private void StampMessageTimestamps()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();

        foreach (var message in messages)
        {
            if (message.CreatedAt is not { } stamp || stamp > now || now - stamp > RetentionLimits.MaxPlausibleAge)
            {
                message.CreatedAt = now;
            }
        }
    }

    /// <summary>
    /// Stamp, then prune. The single entry point the three persist sites call immediately before their
    /// serialize; returns whether anything was dropped, which the caller passes to
    /// <see cref="ReconcileSearchIndexAfterPruneAsync"/> AFTER its state write.
    ///
    /// <para>The order is not interchangeable: pruning first would judge this turn's brand-new messages,
    /// which carry no <c>CreatedAt</c> at all, against the window.</para>
    ///
    /// <para><b>The whole method is gated on the conversation being continuous, and the STAMP is inside
    /// that gate deliberately.</b> A per-chat conversation is never pruned, so a timestamp on its
    /// messages is never read — and it is not free: measured, <c>createdAt</c> costs about 35 bytes a
    /// message, ~1.2% of the very 2 MB item (ADR 0004) this feature exists to defend. Charging that to
    /// every conversation in the app to serve the handful that are continuous is the wrong trade, so
    /// the gate is here rather than inside <see cref="PruneExpiredTurnsAsync"/> alone. It is safe
    /// because a conversation cannot change its id, and the id is what makes it continuous.</para>
    /// </summary>
    private async Task<bool> ApplyRetentionAsync()
    {
        if (!IsRetentionWindowed())
        {
            return false;
        }

        StampMessageTimestamps();
        return await PruneExpiredTurnsAsync();
    }

    /// <summary>
    /// Deletes the leading run of expired turns from a continuous chat's session, remaps the incognito
    /// ordinals the removal just shifted, and reports whether anything went.
    ///
    /// <para><b>The shift is measured in PROJECTED turns, and that is not the number of user turns
    /// dropped.</b> <c>GetHistoryAsync</c> emits both roles and skips a turn carrying neither text nor
    /// an image, so the two diverge the first time a blank or image-only turn is dropped. It is taken as
    /// <c>countBefore - countAfter</c> across one projection either side of the removal — never derived
    /// arithmetically.</para>
    ///
    /// <para><b>The ephemeral remap belongs HERE, not in the reconcile.</b> This is the moment the
    /// ordinals move, so it is the only place the shift and the recorded ordinals are certain to be in
    /// the same frame. Deferring it to a reconcile that may be retried a turn later re-applies an old
    /// shift to ordinals already recorded in the new frame — which indexes incognito text. See
    /// <c>_searchReconcilePending</c>.</para>
    ///
    /// <para>Runs before the serialize at each of the three persist sites, so a pruned session is what
    /// gets written rather than something trimmed afterwards. Only deletion reclaims the 2 MB budget
    /// (ADR 0004); trimming what is sent to the model would not.</para>
    /// </summary>
    private async Task<bool> PruneExpiredTurnsAsync()
    {
        if (!_session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) || messages is null)
        {
            return false;
        }

        var userIndices = UserTurnIndices(messages);
        var expired = ExpiredLeadingTurnCount(
            messages, userIndices, _timeProvider.GetUtcNow() - RetentionLimits.Window);

        if (expired == 0)
        {
            return false;
        }

        var before = (await GetHistoryAsync()).Count;
        RemoveLeadingTurns(messages, userIndices, expired);
        var after = (await GetHistoryAsync()).Count;

        var droppedProjectedTurns = Math.Max(before - after, 0);
        RemapEphemeralOrdinals(droppedProjectedTurns);

        _logger.LogInformation(
            "Agent grain {GrainKey} pruned {Turns} expired turn(s) ({Ordinals} projected) outside the "
            + "{Window} retention window", this.GetPrimaryKeyString(), expired, droppedProjectedTurns,
            RetentionLimits.Window);

        // Reported as "something went", not as a count: the index reconcile deletes the whole
        // conversation regardless of how much, and the count has already been consumed above by the one
        // caller that needs it.
        return true;
    }

    // The index of every user message, in order — one entry per TURN, since a turn is defined as a user
    // message plus everything up to the next one.
    private static List<int> UserTurnIndices(List<ChatMessage> messages)
    {
        var indices = new List<int>();
        for (var i = 0; i < messages.Count; i++)
        {
            if (messages[i].Role == ChatRole.User)
            {
                indices.Add(i);
            }
        }

        return indices;
    }

    // How many turns off the FRONT are wholly expired. A leading run only: the walk stops at the first
    // turn that is not, so a live turn is never deleted because an older one behind it expired.
    private static int ExpiredLeadingTurnCount(
        List<ChatMessage> messages, List<int> userIndices, DateTimeOffset cutoff)
    {
        // Never empty the session, however old everything in it is (RetentionLimits.MinimumPreservedTurns).
        var droppable = userIndices.Count - RetentionLimits.MinimumPreservedTurns;

        var expired = 0;
        while (expired < droppable && TurnIsExpired(messages, userIndices, expired, cutoff))
        {
            expired++;
        }

        return expired;
    }

    // A turn is expired only when EVERY message in it is, so a turn whose reply landed inside the window
    // survives with its prompt. An unstamped message counts as not expired — StampMessageTimestamps runs
    // first, so reaching that branch means something is wrong, and the safe reading of "unknown age" is
    // "keep it".
    private static bool TurnIsExpired(
        List<ChatMessage> messages, List<int> userIndices, int turn, DateTimeOffset cutoff)
    {
        var start = userIndices[turn];
        var end = turn + 1 < userIndices.Count ? userIndices[turn + 1] : messages.Count;

        for (var i = start; i < end; i++)
        {
            if (messages[i].CreatedAt is not { } at || at >= cutoff)
            {
                return false;
            }
        }

        return true;
    }

    // Deletes the first <paramref name="droppedUserTurns"/> whole turns and everything that indexes off
    // them. Three rules here are each a defect if dropped — see the class summary.
    private void RemoveLeadingTurns(List<ChatMessage> messages, List<int> userIndices, int droppedUserTurns)
    {
        // Removal starts at the FIRST USER MESSAGE, never at 0: anything before it is a leading non-user
        // prefix, which the storage filter's own comment says is not pinned down (the framework default
        // excludes only ChatHistory-sourced messages). Such a message is invisible to every existing test —
        // GetHistoryAsync projects User and Assistant only — while still counting in this walk.
        var start = userIndices[0];
        var end = droppedUserTurns < userIndices.Count ? userIndices[droppedUserTurns] : messages.Count;

        for (var i = start; i < end; i++)
        {
            if (AssistantImageId(messages[i]) is { } imageId)
            {
                _state.State.GeneratedImages.Remove(imageId);
            }
        }

        messages.RemoveRange(start, end - start);

        // CLAMPED, and honestly: the clamp is DEFENSIVE, not a fix for a reachable bug. It is tempting to
        // write it up as one — a conversation predating TurnAttachments deserializes the list EMPTY while
        // its session still holds image parts, and an unclamped RemoveRange would throw ArgumentException
        // inside the turn, after the model has already answered. But measured, that state never reaches
        // here: all three persist paths call AlignTurnAttachmentsToHistory + Add BEFORE their run, and
        // Align pads using the same user-message count this walk uses, so by now the list is exactly as
        // long as the user turns and droppedUserTurns is always at least one short of it. (Removing the
        // Math.Min leaves the whole suite green — checked.) It stays because the invariant it protects is
        // an argument about three call sites in three files, and the failure it prevents is the one shape
        // this feature exists to avoid.
        _state.State.TurnAttachments.RemoveRange(
            0, Math.Min(droppedUserTurns, _state.State.TurnAttachments.Count));
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
    /// per-message byte cost in <see cref="ApplyRetentionAsync"/>'s remarks.</para>
    /// </summary>
    private bool IsRetentionWindowed()
    {
        var id = ConversationId();
        return id.StartsWith(RetentionLimits.ContinuousConversationIdPrefix, StringComparison.Ordinal)
            || id.StartsWith(AgentMcpLimits.ConversationIdPrefix, StringComparison.Ordinal);
    }
}
