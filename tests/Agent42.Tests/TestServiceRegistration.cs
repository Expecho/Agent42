using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// Registers the offline document-RAG stand-ins (ADR 0007) shared by every test host: the
/// deterministic embedding generator and the process-global in-memory index (what the grain's
/// always-on document retrieval provider resolves), plus the real extractor/chunker and the ingestion
/// service (what the upload endpoint resolves). Registered in both the silo and the WebApplication
/// DI containers, exactly like the in-memory attachment store — the index is process-global, so an
/// upload served by the endpoint is visible to the grain in the separate silo (constraint 10).
/// </summary>
internal static class TestServiceRegistration
{
    public static IServiceCollection AddDocumentRagFakes(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, DeterministicEmbeddingGenerator>();
        services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddSingleton<DocumentChunker>();
        services.AddSingleton<DocumentIngestionService>();
        return services;
    }
}
