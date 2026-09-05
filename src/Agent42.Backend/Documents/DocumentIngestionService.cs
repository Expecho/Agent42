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
    IUserChatClientFactoryProvider chatClientFactoryProvider,
    IDocumentIndex index)
{
    /// <summary>
    /// Extracts, chunks, embeds and indexes <paramref name="content"/> under
    /// <paramref name="ownerKey"/> (the <c>{userId}/{conversationId}</c> grain key). Returns the chunks
    /// indexed — empty when the document yields no extractable text (e.g. a scanned/image-only PDF),
    /// which the caller can surface to the user.
    /// <para>The embedding generator is resolved from the <b>owner's</b> configured default embedding model
    /// (ADR 0018) — the userId is taken from <paramref name="ownerKey"/>, NEVER from an ambient principal, so
    /// this works identically on the browser-upload and the Easy-Auth-excluded <c>/api/agent-run</c> paths.
    /// Throws <see cref="EmbeddingModelNotConfiguredException"/> when the owner has no embedding model, and
    /// <see cref="EmbeddingDimensionMismatchException"/> when its vectors don't fit the store — the upload
    /// endpoints map both to a clear 4xx.</para>
    /// <para><paramref name="maxChunks"/> is <c>null</c> (unlimited) on the two browser/agent-run upload
    /// paths, where the user chose the file and the request bounds it. It is set on the
    /// <c>read_web_document</c> path (ADR 0066), where a model-chosen URL can point at a document whose
    /// byte size says nothing about how much embedding work it implies — every chunk is a round trip on a
    /// <b>non-reentrant</b> grain. The tail is dropped and reported, never silently discarded.</para>
    /// </summary>
    public async Task<IngestedDocument> IngestAsync(
        string ownerKey, string fileName, string mediaType, byte[] content, int? maxChunks = null,
        CancellationToken cancellationToken = default)
    {
        var segments = extractor.Extract(fileName, mediaType, content);
        var chunks = chunker.Chunk(fileName, segments);
        if (chunks.Count == 0)
        {
            return IngestedDocument.Empty;
        }

        var truncated = maxChunks is int cap && chunks.Count > cap;
        if (truncated)
        {
            chunks = chunks.Take(maxChunks!.Value).ToList();
        }

        // The owner's default embedding model (ADR 0018). userId is derived from the ownerKey grain key
        // ({userId}/{conversationId}) — the same rule AgentGrain uses — so no ambient GetUserId is read.
        var userId = UserIdFromOwnerKey(ownerKey);
        var factory = await chatClientFactoryProvider.GetForUserAsync(userId, cancellationToken);
        if (string.IsNullOrEmpty(factory.DefaultEmbeddingModelId))
        {
            throw new EmbeddingModelNotConfiguredException();
        }

        var generated = await factory.GetEmbeddingGenerator().GenerateAsync(
            chunks.Select(c => c.Text), cancellationToken: cancellationToken);

        // Reject a model whose vectors don't fit the store (the prod container is fixed-dimension, ADR 0007)
        // BEFORE any upsert, so a mismatch is a clear message, not a partial write + opaque Cosmos error.
        if (index.ExpectedDimensions is int expected && generated.Count > 0
            && generated[0].Vector.Length != expected)
        {
            throw new EmbeddingDimensionMismatchException(expected, generated[0].Vector.Length);
        }

        var embedded = chunks
            .Zip(generated, (chunk, embedding) => new EmbeddedChunk(chunk, embedding.Vector))
            .ToList();

        await index.UpsertChunksAsync(ownerKey, embedded, cancellationToken);
        return new IngestedDocument(embedded, truncated);
    }

    // userId is everything before the first '/' of the {userId}/{conversationId} owner key (mirrors
    // AgentGrain.UserIdFromGrainKey). Never an ambient principal — the /api/agent-run path has none.
    private static string UserIdFromOwnerKey(string ownerKey)
    {
        var slash = ownerKey.IndexOf('/');
        return slash < 0 ? ownerKey : ownerKey[..slash];
    }
}
