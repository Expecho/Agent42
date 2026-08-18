using Microsoft.Extensions.AI;

namespace Agent42.Agents.Documents;

/// <summary>
/// Thrown when document RAG needs to embed text but the user has configured no default embedding model.
/// The upload path maps this to a clear 4xx ("configure an embedding model in Preferences"); the
/// retrieval path never lets it fire — it checks <see cref="IChatClientFactory.DefaultEmbeddingModelId"/>
/// first and degrades to empty context (a document conversation must never brick a turn, ADR 0019's
/// no-model-chat discipline extended to embeddings).
/// </summary>
public sealed class EmbeddingModelNotConfiguredException()
    : InvalidOperationException("No embedding model is configured. Add a model marked as an embedding "
        + "model to a provider and pick it as the default embedding model in Preferences.");

/// <summary>
/// Thrown at ingest when the chosen embedding model produces vectors of a different dimension than the
/// document store expects (the prod <c>doc-chunks</c> container's vector policy is fixed at 3072 dims —
/// ADR 0007). Switching to a compatible embedding model, or deleting-and-reingesting, is the fix. The
/// upload path maps this to a clear 4xx.
/// </summary>
public sealed class EmbeddingDimensionMismatchException(int expected, int actual)
    : InvalidOperationException(
        $"The selected embedding model produces {actual}-dimension vectors, but the document store expects "
        + $"{expected}. Pick an embedding model that outputs {expected}-dimension vectors.")
{
    public int Expected { get; } = expected;

    public int Actual { get; } = actual;
}

/// <summary>
/// The no-op <see cref="IEmbeddingGenerator{String, Single}"/> the per-user factory hands out when no
/// default embedding model is configured — the embedding-side analogue of <c>NoModelChatClient</c>
/// (ADR 0019). Generating throws <see cref="EmbeddingModelNotConfiguredException"/> rather than
/// returning a bogus vector: callers must gate on <see cref="IChatClientFactory.DefaultEmbeddingModelId"/>
/// before use (upload surfaces the error; retrieval skips embedding entirely).
/// </summary>
public sealed class NoEmbeddingGenerator : IEmbeddingGenerator<string, Embedding<float>>
{
    public static readonly NoEmbeddingGenerator Instance = new();

    private NoEmbeddingGenerator()
    {
    }

    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
        IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null,
        CancellationToken cancellationToken = default)
        => throw new EmbeddingModelNotConfiguredException();

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
