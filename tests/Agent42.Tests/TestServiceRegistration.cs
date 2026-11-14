using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agent42.Tests;

/// <summary>
/// Registers the offline document-RAG stand-ins (ADR 0007) shared by every test host: the
/// deterministic embedding generator and the process-global in-memory index (what the grain's
/// always-on document retrieval provider resolves), plus the real extractor/chunker and the ingestion
/// service (what the upload endpoint resolves). Registered in both the silo and the WebApplication
/// DI containers, exactly like the in-memory attachment store — the index is process-global, so an
/// upload served by the endpoint is visible to the grain in the separate silo (constraint 10).
///
/// <para>It also carries the chat search index (ADR 0061), which is not document RAG. This is the
/// ONE helper that every silo configurator AND every API host calls — including
/// <see cref="CapSizedFetcherSiloConfigurator"/>, which skips <see cref="AddWebToolFakes"/> — and
/// <c>IChatSearchIndex</c> needs both: it is a mandatory <c>AgentGrain</c> dependency (a miss fails
/// activation) and the search endpoint resolves it (a miss returns 409). Putting it here is what
/// makes both true everywhere at once.</para>
/// </summary>
internal static class TestServiceRegistration
{
    public static IServiceCollection AddDocumentRagFakes(this IServiceCollection services)
    {
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, DeterministicEmbeddingGenerator>();
        services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
        services.AddSingleton<IChatSearchIndex, TestChatSearchIndex>();
        services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
        services.AddSingleton<DocumentChunker>();
        services.AddSingleton<DocumentIngestionService>();
        // The read_web_document ingestion port (ADR 0066). It rides THIS helper rather than
        // AddWebToolFakes for the same reason IChatSearchIndex does: this is the one helper every silo
        // configurator AND every API host calls, and it is a mandatory AgentGrain dependency, so a miss
        // fails activation. The real pipeline is used offline too — only the FETCHER has an in-memory
        // variant, because only it opens a socket.
        services.AddSingleton<IDocumentIngestor, WebDocumentIngestor>();
        // DocumentIngestionService resolves the owner's embedding generator through the per-user factory seam
        // (ADR 0018). A silo registers its own provider (with the fake chat client + embeddings) BEFORE calling
        // this, so TryAdd is a no-op there; an endpoint-only container (the upload endpoint) gets this
        // embedding-capable stub — a no-op chat client (unused on the ingest path) + the deterministic generator.
        services.TryAddSingleton<IUserChatClientFactoryProvider>(sp =>
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(
                NoModelChatClient.Instance,
                embeddingGenerator: sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                defaultEmbeddingModelId: "offline-embed")));
        return services;
    }

    /// <summary>
    /// Registers the offline web-tool stand-ins (ADR 0011) the grain resolves for <c>web_search</c> and
    /// <c>read_web_document</c>, plus the <see cref="IGeocoder"/> behind <c>get_current_location</c>
    /// (ADR 0012): canned results, a canned document and one canned address, so the tool paths run with no
    /// SearXNG sidecar, no Google API key and no network. Every silo configurator that hosts an
    /// <see cref="AgentGrain"/> needs this — a grain dependency missing from one configurator fails that
    /// host's activations only, which is why it lives here rather than being repeated per configurator.
    /// (<see cref="CapSizedFetcherSiloConfigurator"/> is the exception that proves it: it registers its
    /// deps inline so it can swap the fetcher, so anything added here must be added there too.)
    /// </summary>
    public static IServiceCollection AddWebToolFakes(this IServiceCollection services)
    {
        services.AddSingleton<IWebSearch, InMemoryWebSearch>();
        // read_web_document (ADR 0066). The offline fetcher deliberately re-implements no address guard:
        // the guard lives in the production fetcher's ConnectCallback because it must run against the
        // address actually dialled, and a fake that opens no socket has nothing to guard.
        services.AddSingleton<IWebDocumentFetcher, InMemoryWebDocumentFetcher>();
        services.AddSingleton<IGeocoder, FakeGeocoder>();
        return services;
    }
}
