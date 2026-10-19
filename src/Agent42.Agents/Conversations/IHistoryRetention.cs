using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// History retention for a CONTINUOUS chat (ADR 0016): stamp every message, then prune the leading run of expired
/// whole turns. Owned by the grain activation (ADR 0106); it mutates only the collections in its context and never
/// writes state, so the grain's persist pipeline keeps its order (stamp and prune → serialize → write).
/// </summary>
public interface IHistoryRetention
{
    /// <summary>Stamps and prunes <paramref name="context"/>'s collections in place.</summary>
    RetentionResult Apply(RetentionContext context);
}

/// <summary>
/// What one retention pass works on: the session's message list and the two state collections that index off it.
/// <paramref name="ProjectedTurnCount"/> counts the client-visible projection (the search ordinals); it is read on
/// either side of the removal, because the shift is measured in PROJECTED turns, never in user turns.
/// </summary>
public sealed record RetentionContext(
    List<ChatMessage> Messages,
    List<List<AttachmentRef>> TurnAttachments,
    Dictionary<string, AttachmentRef> GeneratedImages,
    Func<int> ProjectedTurnCount);

/// <summary>
/// Whether a pass dropped anything (the search reconcile's input), and by how many projected turns (the incognito
/// ordinal remap's input).
/// </summary>
public readonly record struct RetentionResult(bool Pruned, int DroppedProjectedTurns)
{
    public static RetentionResult None => default;
}
