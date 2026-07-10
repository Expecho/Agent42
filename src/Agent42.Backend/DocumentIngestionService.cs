using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Synchronously ingests an uploaded document for retrieval (ADR 0007): extract text → chunk →
/// embed → upsert into the conversation's <see cref="IDocumentIndex"/>. Runs inside the upload
/// request at per-conversation, small-document scale; background/streaming ingestion is out of
/// scope. Documents are ingest-only — the raw bytes are never stored as a chat attachment, so they
/// never reach the model via the image <c>UriContent</c> path, only as excerpts auto-injected by the
/// always-on document retrieval provider (ADR 0008).
/// </summary>
public sealed class DocumentIngestionService(
    IDocumentTextExtractor extractor,
    DocumentChunker chunker,
    IEmbeddingGenerator<string, Embedding<float>> embeddings,
    IDocumentIndex index)
{
    /// <summary>
    /// Extracts, chunks, embeds and indexes <paramref name="content"/> under
    /// <paramref name="ownerKey"/> (the <c>{userId}/{conversationId}</c> grain key). Returns the
    /// number of chunks indexed — zero when the document yields no extractable text (e.g. a
    /// scanned/image-only PDF), which the caller can surface to the user.
    /// </summary>
    public async Task<int> IngestAsync(
        string ownerKey, string fileName, string mediaType, byte[] content, CancellationToken cancellationToken = default)
    {
        var segments = extractor.Extract(fileName, mediaType, content);
        var chunks = chunker.Chunk(fileName, segments);
        if (chunks.Count == 0)
        {
            return 0;
        }

        var generated = await embeddings.GenerateAsync(
            chunks.Select(c => c.Text), cancellationToken: cancellationToken);

        var embedded = chunks
            .Zip(generated, (chunk, embedding) => new EmbeddedChunk(chunk, embedding.Vector))
            .ToList();

        await index.UpsertChunksAsync(ownerKey, embedded, cancellationToken);
        return chunks.Count;
    }
}
