namespace Agent42.Agents;

/// <summary>
/// What one document ingest produced: the chunks that were embedded and upserted, and whether the
/// document was longer than the chunk cap allowed.
///
/// <para><b>The chunks come back deliberately, and they are what the inline excerpts are ranked over
/// (ADR 0066).</b> The alternative — re-reading the top-K from <see cref="IDocumentIndex"/> — is
/// rejected for three independent reasons: the Cosmos vector path is prod-only and unverified by CI
/// (ADR 0007), so a read-back test would be green offline and could return nothing in production;
/// <c>SearchAsync</c> has no per-document filter, so it would mix in excerpts from the conversation's
/// other documents and present them as this URL's content; and avoiding it keeps
/// <see cref="IDocumentIndex"/> and both its implementations out of the diff entirely.</para>
///
/// <para><paramref name="Truncated"/> is reported rather than swallowed, so
/// the model must know it is reasoning over part of a document,
/// not all of it.</para>
/// </summary>
public sealed record IngestedDocument(IReadOnlyList<EmbeddedChunk> Chunks, bool Truncated)
{
    /// <summary>Nothing was extractable — e.g. a scanned, image-only PDF.</summary>
    public static readonly IngestedDocument Empty = new([], Truncated: false);

    /// <summary>Number of chunks embedded and indexed.</summary>
    public int ChunkCount => Chunks.Count;
}

/// <summary>
/// Ingests a document into a conversation's retrieval index — extract, chunk, embed, upsert (ADR 0007)
/// — as a port the <c>Agent42.Agents</c> side can depend on (ADR 0066).
///
/// <para>The pipeline itself lives in the Backend (<c>DocumentIngestionService</c>) because it is where
/// the concrete index and embedding clients are wired; this interface exists so <c>AgentGrain</c>'s
/// <c>read_web_document</c> tool can reach it without the Agents project referencing the Backend —
/// the same real/in-memory seam shape as <see cref="IDocumentIndex"/> and <see cref="IAttachmentStore"/>.
/// Unlike those, there is no in-memory variant: the pipeline is Azure-free already, so offline hosts
/// register the real one over an in-memory index.</para>
/// </summary>
public interface IDocumentIngestor
{
    /// <summary>
    /// Extracts, chunks, embeds and indexes <paramref name="content"/> under <paramref name="ownerKey"/>
    /// (the <c>{userId}/{conversationId}</c> grain key), embedding at most
    /// <paramref name="maxChunks"/> chunks — <c>null</c> for unlimited, which is what the browser-upload
    /// path uses.
    ///
    /// <para>Throws <see cref="EmbeddingModelNotConfiguredException"/> when the owner has no embedding
    /// model and <see cref="EmbeddingDimensionMismatchException"/> when its vectors don't fit the store.
    /// Callers turn both into a message rather than a failure: an endpoint into a 400, the tool into
    /// text for the model.</para>
    /// </summary>
    Task<IngestedDocument> IngestAsync(
        string ownerKey,
        string fileName,
        string mediaType,
        byte[] content,
        int? maxChunks = null,
        CancellationToken cancellationToken = default);
}
