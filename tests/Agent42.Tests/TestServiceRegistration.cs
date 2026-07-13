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

    /// <summary>
    /// Registers the offline web-tool stand-ins (ADR 0011) the grain resolves for <c>web_search</c> and
    /// <c>fetch_web_page</c>, plus the <see cref="IGeocoder"/> behind <c>get_current_location</c>
    /// (ADR 0012): canned results, canned page text and one canned address, so the tool paths run with no
    /// SearXNG sidecar, no Google API key and no network. Every silo configurator that hosts an
    /// <see cref="AgentGrain"/> needs this — a grain dependency missing from one configurator fails that
    /// host's activations only, which is why it lives here rather than being repeated per configurator.
    /// (<see cref="CapSizedFetcherSiloConfigurator"/> is the exception that proves it: it registers its
    /// deps inline so it can swap the fetcher, so anything added here must be added there too.)
    /// </summary>
    public static IServiceCollection AddWebToolFakes(this IServiceCollection services)
    {
        services.AddSingleton<IWebSearch, InMemoryWebSearch>();
        services.AddSingleton<IWebPageFetcher, InMemoryWebPageFetcher>();
        services.AddSingleton<IGeocoder, FakeGeocoder>();
        return services;
    }
}
