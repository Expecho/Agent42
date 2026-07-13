// Local development host for the Agent 42 frontend: serves the real wwwroot and
// the real API surface, but with localhost clustering, in-memory grain storage
// and a canned markdown-emitting chat client — no Cosmos or Azure OpenAI needed.
using Agent42.Agents;
using Agent42.Backend;
using Agent42.DevHost;
using Microsoft.Extensions.AI;

var webRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent42.Backend", "wwwroot"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});

// The canned client ignores the model, but a factory + static catalog let the model
// picker be exercised offline without any Azure environment.
builder.Services.AddSingleton<IChatClient, CannedChatClient>();
builder.Services.AddSingleton<IChatClientFactory>(sp =>
    new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), "gpt-4o"));
builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("gpt-4o",
[
    new ModelInfo("gpt-4o", "gpt-4o", "2024-08-06"),
    new ModelInfo("gpt-4o-mini", "gpt-4o-mini", "2024-07-18"),
    new ModelInfo("o3-mini", "o3-mini", "2025-01-31"),
]));

// In-memory attachment store: no Azure Storage offline. The canned client ignores image
// content, so uploads still round-trip for the display proxy and history rendering.
builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();

// Document RAG offline (ADR 0007): the real (deterministic, Azure-free) extractor + chunker, a
// deterministic embedding generator, an in-memory vector index, and the ingestion service — so a
// document upload → question round-trips locally with no Cosmos or Azure OpenAI.
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<DocumentChunker>();
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, DeterministicEmbeddingGenerator>();
builder.Services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
builder.Services.AddSingleton<DocumentIngestionService>();

// Web tools offline (ADR 0011): canned search results and canned page text, so the web_search /
// fetch_web_page tool path renders end to end with no SearXNG sidecar, no Docker and no network.
builder.Services.AddSingleton<IWebSearch, InMemoryWebSearch>();
builder.Services.AddSingleton<IWebPageFetcher, InMemoryWebPageFetcher>();

// Location offline (ADR 0012): the get_current_location tool path runs end to end — the browser's real
// fix rides the chat request — but nothing is reverse-geocoded, so no Google API key is needed and no
// request leaves the machine. The tool reports coordinates without a street address.
builder.Services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
    silo.UseInMemoryReminderService();
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();

app.Run();
