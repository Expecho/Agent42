using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// Stamps every message with a creation time and prunes a CONTINUOUS agent chat (ADR 0016) back to
/// <see cref="RetentionLimits.Window"/>. The grain decides whether a conversation is windowed and calls
/// <see cref="Apply"/> immediately before its serialize at each persist site (ADR 0106).
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
public sealed class HistoryRetention(TimeProvider clock, ILogger logger, string grainKey) : IHistoryRetention
{
    /// <summary>
    /// Stamp, then prune. The order is not interchangeable: pruning first would judge this turn's brand-new messages,
    /// which carry no <c>CreatedAt</c> at all, against the window.
    /// </summary>
    public RetentionResult Apply(RetentionContext context)
    {
        StampMessageTimestamps(context.Messages);
        return PruneExpiredTurns(context);
    }

    /// <summary>
    /// Stamps <c>CreatedAt</c> on the session's messages, replacing any value that cannot be
    /// believed.
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
    private void StampMessageTimestamps(List<ChatMessage> messages)
    {
        var now = clock.GetUtcNow();

        foreach (var message in messages)
        {
            if (message.CreatedAt is not { } stamp || stamp > now || now - stamp > RetentionLimits.MaxPlausibleAge)
            {
                message.CreatedAt = now;
            }
        }
    }

    /// <summary>
    /// Deletes the leading run of expired turns, and reports whether anything went and by how many projected turns.
    ///
    /// <para><b>The shift is measured in PROJECTED turns, and that is not the number of user turns
    /// dropped.</b> <c>GetHistoryAsync</c> emits both roles and skips a turn carrying neither text nor
    /// an image, so the two diverge the first time a blank or image-only turn is dropped. It is taken as
    /// <c>countBefore - countAfter</c> across one projection either side of the removal — never derived
    /// arithmetically.</para>
    ///
    /// <para><b>The caller remaps the incognito ordinals with that delta at once</b>, before the reconcile: this is
    /// the moment the ordinals move, so it is the only place the shift and the recorded ordinals are certain to be in
    /// the same frame (<see cref="ChatSearchProjection.RemapAfterPrune"/>).</para>
    ///
    /// <para>Runs before the serialize, so a pruned session is what gets written rather than something trimmed
    /// afterwards. Only deletion reclaims the 2 MB budget (ADR 0004); trimming what is sent to the model would
    /// not.</para>
    /// </summary>
    private RetentionResult PruneExpiredTurns(RetentionContext context)
    {
        var messages = context.Messages;
        var userIndices = UserTurnIndices(messages);
        var expired = ExpiredLeadingTurnCount(messages, userIndices, clock.GetUtcNow() - RetentionLimits.Window);

        if (expired == 0)
        {
            return RetentionResult.None;
        }

        var before = context.ProjectedTurnCount();
        RemoveLeadingTurns(context, userIndices, expired);
        var after = context.ProjectedTurnCount();

        var droppedProjectedTurns = Math.Max(before - after, 0);

        logger.LogInformation(
            "Agent grain {GrainKey} pruned {Turns} expired turn(s) ({Ordinals} projected) outside the "
            + "{Window} retention window", grainKey, expired, droppedProjectedTurns, RetentionLimits.Window);

        return new RetentionResult(Pruned: true, droppedProjectedTurns);
    }

    /// <summary>
    /// The index of every user message, in order — one entry per TURN, since a turn is defined as a user
    /// message plus everything up to the next one. Shared with edit/fork (ADR 0092). Public rather than internal
    /// because this repo has no <c>InternalsVisibleTo</c>.
    /// </summary>
    public static List<int> UserTurnIndices(List<ChatMessage> messages)
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
    private static void RemoveLeadingTurns(RetentionContext context, List<int> userIndices, int droppedUserTurns)
    {
        var messages = context.Messages;

        // Removal starts at the FIRST USER MESSAGE, never at 0: anything before it is a leading non-user
        // prefix, which the storage filter's own comment says is not pinned down (the framework default
        // excludes only ChatHistory-sourced messages). Such a message is invisible to every existing test —
        // GetHistoryAsync projects User and Assistant only — while still counting in this walk.
        var start = userIndices[0];
        var end = droppedUserTurns < userIndices.Count ? userIndices[droppedUserTurns] : messages.Count;

        for (var i = start; i < end; i++)
        {
            if (AgentGrain.AssistantImageId(messages[i]) is { } imageId)
            {
                context.GeneratedImages.Remove(imageId);
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
        context.TurnAttachments.RemoveRange(0, Math.Min(droppedUserTurns, context.TurnAttachments.Count));
    }
}
