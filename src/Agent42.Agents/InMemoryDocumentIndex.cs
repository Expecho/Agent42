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
            scored[i] = (snapshot[i].Chunk, CosineSimilarity(queryEmbedding.Span, snapshot[i].Embedding.Span));
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

    // Cosine similarity in [-1, 1]; higher is more similar. A zero-magnitude vector scores 0
    // (never ranks), so a degenerate embedding can't poison the ordering.
    private static double CosineSimilarity(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        if (a.Length != b.Length)
        {
            return 0;
        }

        double dot = 0, magA = 0, magB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        if (magA == 0 || magB == 0)
        {
            return 0;
        }

        return dot / (Math.Sqrt(magA) * Math.Sqrt(magB));
    }
}
