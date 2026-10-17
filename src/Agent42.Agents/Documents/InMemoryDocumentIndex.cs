using System.Collections.Concurrent;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IDocumentIndex"/> for local dev (DevHost) and tests, where no Cosmos
/// vector store is available. Ranks by cosine similarity over a full scan — correct and fast at
/// per-conversation scale, and the same brute-force fallback the Cosmos <c>quantizedFlat</c> index
/// uses for &lt;1000 vectors (ADR 0007).
///
/// The backing store is <b>static / process-global</b> for the same reason as
/// <see cref="InMemoryAttachmentStore"/>: in tests the ingestion endpoint and the retrieval grain
/// run in two separate DI containers, so an instance store would split their state and the
/// upload→search path would silently miss.
/// </summary>
public sealed class InMemoryDocumentIndex : IDocumentIndex
{
    private static readonly ConcurrentDictionary<string, List<EmbeddedChunk>> Store = new();

    // Accepts any dimension: the offline deterministic generator is 128-dim, a real model is 3072, and the
    // brute-force cosine scan tolerates whatever it is given — so there is no fixed-dimension wall to enforce.
    public int? ExpectedDimensions => null;

    public Task UpsertChunksAsync(
        string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
    {
        var bucket = Store.GetOrAdd(ownerKey, _ => []);
        lock (bucket)
        {
            bucket.AddRange(chunks);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DocumentChunk>> SearchAsync(
        string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
    {
        if (topK <= 0 || !Store.TryGetValue(ownerKey, out var bucket))
        {
            return Task.FromResult<IReadOnlyList<DocumentChunk>>([]);
        }

        EmbeddedChunk[] snapshot;
        lock (bucket)
        {
            snapshot = [.. bucket];
        }

        // Score in a plain loop (a ReadOnlySpan can't be captured by a LINQ lambda), then rank.
        var scored = new (DocumentChunk Chunk, double Score)[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
        {
            scored[i] = (snapshot[i].Chunk, VectorSimilarity.Cosine(queryEmbedding.Span, snapshot[i].Embedding.Span));
        }

        var ranked = scored
            .OrderByDescending(x => x.Score)
            .Take(topK)
            .Select(x => x.Chunk);

        return Task.FromResult<IReadOnlyList<DocumentChunk>>([.. ranked]);
    }

    public Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
    {
        Store.TryRemove(ownerKey, out _);
        return Task.CompletedTask;
    }

    public Task<DocumentChunkRange> ReadChunksAsync(
        string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default)
    {
        if (!Store.TryGetValue(ownerKey, out var bucket))
        {
            return Task.FromResult(new DocumentChunkRange(0, []));
        }

        DocumentChunk[] ordered;
        lock (bucket)
        {
            // Distinct by index, first occurrence wins — the same dedupe the Cosmos twin applies to legacy
            // duplicate copies (ADR 0098).
            ordered = [.. bucket
                .Select(e => e.Chunk)
                .Where(c => string.Equals(c.FileName, fileName, StringComparison.Ordinal))
                .DistinctBy(c => c.ChunkIndex)
                .OrderBy(c => c.ChunkIndex)];
        }

        var slice = ordered
            .Where(c => c.ChunkIndex >= startChunkIndex && c.ChunkIndex < startChunkIndex + Math.Max(count, 0))
            .ToArray();

        return Task.FromResult(new DocumentChunkRange(ordered.Length, slice));
    }

    public Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default)
    {
        if (Store.TryGetValue(ownerKey, out var bucket))
        {
            lock (bucket)
            {
                bucket.RemoveAll(e => string.Equals(e.Chunk.FileName, fileName, StringComparison.Ordinal));
            }
        }

        return Task.CompletedTask;
    }
}
