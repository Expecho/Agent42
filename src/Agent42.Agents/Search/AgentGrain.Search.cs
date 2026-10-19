namespace Agent42.Agents;

/// <summary>
/// The chat-search half of <see cref="AgentGrain"/> (ADR 0061): the grain side of <see cref="ChatSearchProjection"/>,
/// which owns the cursor, the incognito ordinals and the reconcile retry (ADR 0106). The grain calls it at each persist
/// site — reconcile after the state write, then index — and on purge.
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// This activation's search projection, built in <c>OnActivateAsync</c> once the grain key is known. Over
    /// <see cref="GetHistoryAsync"/>'s projection, so the live path, the backfill and the history the client sees
    /// share one set of ordinals.
    /// </summary>
    private IChatSearchProjection _search = null!;

    private IChatSearchProjection CreateSearchProjection()
        => new ChatSearchProjection(
            _chatSearchIndex, _logger, this.GetPrimaryKeyString(), () => [.. ProjectHistory().Select(p => p.Turn)]);

    /// <inheritdoc cref="ChatSearchProjection.ReindexAsync"/>
    public Task<int> ReindexAsync() => _search.ReindexAsync();

    private string ConversationId() => ConversationIdFromGrainKey(this.GetPrimaryKeyString());
}
