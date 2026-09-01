namespace Agent42.Agents;

/// <summary>
/// Stores a searchable plain-text projection of chat messages and answers owner-scoped text
/// queries over it (ADR 0061). Every operation is scoped by <paramref name="userId"/> — the
/// opaque Easy-Auth principal id, which is also the production container's partition key — so one
/// user can never read or delete another's messages.
///
/// <para>This is a <b>projection</b>, not the record. The conversation itself lives in the grain's
/// <c>SerializedSession</c> (ADR 0004); this index exists because Cosmos full-text search needs a
/// clean-text corpus at message granularity, which one JSON blob per conversation cannot provide.
/// Losing the index costs search results, never conversation content — which is what lets every
/// write be best-effort.</para>
///
/// <para>Production is Cosmos DB (<c>CosmosChatSearchIndex</c>); local dev and tests use
/// <see cref="InMemoryChatSearchIndex"/> — the same real/in-memory split as
/// <see cref="IDocumentIndex"/> and <see cref="IAttachmentStore"/>.</para>
/// </summary>
public interface IChatSearchIndex
{
    /// <summary>
    /// Upserts <paramref name="turns"/> for one conversation. Keyed by
    /// <c>{conversationId}:{ordinal}</c>, so re-indexing the same ordinals overwrites rather than
    /// duplicates — the property that makes <c>ReindexAsync</c> idempotent.
    ///
    /// <para><b>Ordinals must come from <c>GetHistoryAsync()</c>'s projection</b>, on both the live
    /// path and the backfill. See the ADR 0061 gotcha: two different counting schemes silently
    /// double-index every conversation that ever had a skipped turn.</para>
    /// </summary>
    Task IndexTurnsAsync(
        string userId,
        string conversationId,
        IReadOnlyList<ChatSearchTurn> turns,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="maxResults"/> of <paramref name="userId"/>'s messages whose
    /// text contains <paramref name="query"/>, <b>case-insensitively</b>. Empty for a blank query.
    ///
    /// <para>Case-insensitivity is explicit in both implementations and is not the default in
    /// either: Cosmos <c>CONTAINS</c> needs its third argument, and .NET string comparison needs
    /// <see cref="StringComparison.OrdinalIgnoreCase"/>. The Cosmos half is unreachable by test, so
    /// the two can only be kept in step deliberately.</para>
    /// </summary>
    Task<IReadOnlyList<ChatSearchHit>> SearchAsync(
        string userId,
        string query,
        int maxResults,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every indexed message of one conversation, so deleting a conversation doesn't leave
    /// its text behind (privacy — called from <c>AgentGrain.PurgeAsync</c>).
    ///
    /// <para>Unlike <see cref="IDocumentIndex.DeleteAsync"/> this is <b>not</b> a whole-partition
    /// drop: the partition here is the <i>user</i>, not the conversation, so an implementation must
    /// filter by <paramref name="conversationId"/> within it.</para>
    /// </summary>
    Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken cancellationToken = default);
}
