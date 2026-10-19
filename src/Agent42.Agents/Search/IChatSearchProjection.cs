namespace Agent42.Agents;

/// <summary>
/// The chat-search projection of ONE conversation (ADR 0061/0068), owned by its grain activation (ADR 0106). The grain
/// calls it at each persist site, in the order its persist pipeline fixes: reconcile after the state write, then index.
/// An interface so the grain's behaviour can be tested with a fake and the projection without a silo.
/// </summary>
public interface IChatSearchProjection
{
    /// <summary>True when this activation served an incognito turn whose ordinals must never be indexed.</summary>
    bool HasEphemeralTurns { get; }

    /// <summary>Indexes the turns added since the last index write of this activation. Best-effort.</summary>
    Task IndexNewTurnsAsync();

    /// <summary>Marks an incognito turn's ordinals as accounted for WITHOUT indexing them. Best-effort.</summary>
    Task SkipEphemeralTurnAsync();

    /// <summary>Shifts the incognito ordinals down by a retention prune's projected-turn delta.</summary>
    void RemapAfterPrune(int droppedProjectedTurns);

    /// <summary>Re-anchors the index after a prune (or retries a failed re-anchor). Called after the state write.</summary>
    Task ReconcileAfterPruneAsync(bool pruned);

    /// <summary>Re-indexes the whole conversation from ordinal 0. Throws: the caller is an explicit user action.</summary>
    Task<int> ReindexAsync();

    /// <summary>Removes the conversation's indexed turns, with the conversation. Best-effort.</summary>
    Task DeleteConversationAsync();

    /// <summary>
    /// Forgets the incognito ordinals at or past <paramref name="projectedCount"/>, after the last turn was removed
    /// (ADR 0092). With <paramref name="pullBackCursor"/> (an in-memory-only truncation) the cursor moves back too, so
    /// the next turn's ordinals start where the removed turn's did.
    /// </summary>
    void TruncateTo(int projectedCount, bool pullBackCursor);
}
