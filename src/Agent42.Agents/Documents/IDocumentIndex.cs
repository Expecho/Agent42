namespace Agent42.Agents;

/// <summary>
/// Stores a conversation's document chunks and their embeddings, and answers nearest-neighbour
/// queries for retrieval (ADR 0007). Every operation is scoped by <c>ownerKey</c> — the per-user,
/// per-conversation grain key <c>{userId}/{conversationId}</c> (ADR 0001) — so one conversation
/// can never read or delete another's chunks. Documents are per-conversation; there is no
/// cross-conversation library.
///
/// Production is Cosmos DB vector search (<c>CosmosDocumentIndex</c>); local dev and tests use an
/// in-memory implementation (<see cref="InMemoryDocumentIndex"/>) — the same real/in-memory split
/// as <see cref="IAttachmentStore"/> and <see cref="IChatClientFactory"/>.
/// </summary>
public interface IDocumentIndex
{
    /// <summary>
    /// The exact vector dimension this store accepts, or <c>null</c> when it accepts any (the in-memory
    /// index). The prod Cosmos container's vector policy is fixed (3072 for <c>text-embedding-3-large</c>,
    /// ADR 0007), so ingest rejects a user-chosen embedding model whose vectors don't match, with a clear
    /// message instead of an opaque Cosmos error.
    /// </summary>
    int? ExpectedDimensions { get; }

    /// <summary>Upserts a batch of embedded chunks under <paramref name="ownerKey"/>.</summary>
    Task UpsertChunksAsync(
        string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the up-to-<paramref name="topK"/> chunks under <paramref name="ownerKey"/> whose
    /// embeddings are most similar (cosine) to <paramref name="queryEmbedding"/>, most similar
    /// first. Empty when the conversation has no indexed documents.
    /// </summary>
    Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every chunk under <paramref name="ownerKey"/>. Called when a conversation is deleted
    /// so its indexed document text doesn't outlive it (privacy — see <c>AgentGrain.PurgeAsync</c>).
    /// </summary>
    Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns up to <paramref name="count"/> chunks of <paramref name="fileName"/> under
    /// <paramref name="ownerKey"/>, starting at <paramref name="startChunkIndex"/>, in chunk order — the
    /// sequential read behind <c>read_document</c> (ADR 0098). Distinct by <see cref="DocumentChunk.ChunkIndex"/>
    /// (first occurrence wins): a conversation re-uploaded before re-ingest replaced its chunks may hold two
    /// copies with identical indices. <see cref="DocumentChunkRange.TotalChunks"/> is the file's distinct chunk
    /// count, <c>0</c> when the file has none.
    /// </summary>
    Task<DocumentChunkRange> ReadChunksAsync(
        string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default);

    /// <summary>
    /// Removes every chunk of <paramref name="fileName"/> under <paramref name="ownerKey"/> and leaves the
    /// conversation's other documents alone. Ingest calls it right before upserting, so re-ingesting a file
    /// name replaces its chunks rather than duplicating them (ADR 0098).
    /// </summary>
    Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default);
}

/// <summary>
/// One ordered slice of a document's chunks plus the document's total distinct chunk count, so the caller
/// can number parts (<see cref="IDocumentIndex.ReadChunksAsync"/>).
/// </summary>
public sealed record DocumentChunkRange(int TotalChunks, IReadOnlyList<DocumentChunk> Chunks);
