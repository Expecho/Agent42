using System.Text;
using Microsoft.Extensions.AI;

namespace Agent42.Agents.Documents;

/// <summary>
/// Offline/test <see cref="IEmbeddingGenerator{String, Single}"/>: turns text into a stable
/// bag-of-words hashed vector, no Azure needed. Deterministic (same text → same vector) and
/// term-overlap aware (texts sharing words get similar vectors), so cosine ranking over these
/// vectors is meaningful — a query about "magrathea" retrieves the chunk mentioning it. Used by
/// DevHost and the test host in place of the real Azure OpenAI embedding client, the same
/// real/offline split as <see cref="IChatClientFactory"/> and <see cref="IAttachmentStore"/>.
/// </summary>
public sealed class DeterministicEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    // Small vs. the 3072-dim production model — enough buckets for term overlap to separate texts,
    // cheap to compute. The production Cosmos path uses text-embedding-3-large (ADR 0007).
    public const int Dimensions = 128;

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var result = new GeneratedEmbeddings<Embedding<float>>();
        foreach (var value in values)
        {
            result.Add(new Embedding<float>(Vectorize(value)));
        }

        return Task.FromResult(result);
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }

    // Bag-of-words: each lowercased alphanumeric token is hashed into one of Dimensions buckets and
    // increments it. Shared tokens → aligned non-zero dimensions → high cosine similarity.
    private static float[] Vectorize(string text)
    {
        var vector = new float[Dimensions];
        var token = new StringBuilder();

        foreach (var ch in text)
        {
            if (char.IsLetterOrDigit(ch))
            {
                token.Append(char.ToLowerInvariant(ch));
            }
            else if (token.Length > 0)
            {
                Accumulate(vector, token.ToString());
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            Accumulate(vector, token.ToString());
        }

        return vector;
    }

    private static void Accumulate(float[] vector, string token)
    {
        // Stable, non-cryptographic hash (FNV-1a) so buckets are identical across processes/runs —
        // string.GetHashCode is randomized per process and would break cross-process determinism.
        uint hash = 2166136261;
        foreach (var ch in token)
        {
            hash = (hash ^ ch) * 16777619;
        }

        vector[hash % Dimensions] += 1f;
    }
}
