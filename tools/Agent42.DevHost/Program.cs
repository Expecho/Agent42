// Local development host for the Agent 42 frontend: serves the real wwwroot and
// the real API surface, but with localhost clustering, in-memory grain storage
// and a canned markdown-emitting chat client — no Cosmos or Azure OpenAI needed.
using Agent42.Agents;
using Agent42.Backend;
using Agent42.DevHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;

var webRoot = Path.GetFullPath(Path.Combine(
    AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "Agent42.Backend", "wwwroot"));

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = webRoot,
});

// The canned client ignores the model, but a factory + static catalog let the model picker be
// exercised offline without any Azure environment. The catalog uses QUALIFIED ids (ADR 0013) and
// spans two providers, so the picker's per-provider grouping is exercisable by hand.
//
// "github:text-only-demo" is deliberately not-capable: it is the only way to drive the degraded paths
// (no tools, no load_skill, images withheld) in a browser rather than only in tests.
const string DefaultModel = "foundry:gpt-4o";
const string TextOnlyModel = "github:text-only-demo";
// An image-generation model (ADR 0020): selecting it makes a turn produce an image via the in-memory
// generator (a canned PNG) — so the generate/store/display/history flow is exercisable in a browser offline.
const string ImageModel = "images:image-demo";
// A declared embedding model (document RAG, ADR 0007/0018) so the Preferences "default embedding model"
// selector and the RAG round-trip are exercisable offline against the deterministic generator.
const string EmbeddingModel = "foundry:text-embedding-3-large";

builder.Services.AddSingleton<IChatClient, CannedChatClient>();
// The offline image generator returns a canned PNG (no real /v1/images endpoint offline — ADR 0020).
builder.Services.AddSingleton<IImageGenerationService, InMemoryImageGenerator>();
builder.Services.AddSingleton<IChatClientFactory>(sp =>
    new SingleChatClientFactory(
        sp.GetRequiredService<IChatClient>(),
        DefaultModel,
        capabilities: modelId => modelId switch
        {
            TextOnlyModel => new ModelCapabilities(SupportsTools: false, SupportsVision: false),
            ImageModel => new ModelCapabilities(
                SupportsTools: false, SupportsVision: false, SupportsImageGeneration: true),
            _ => ModelCapabilities.Full,
        },
        imageGenerator: sp.GetRequiredService<IImageGenerationService>(),
        // The deterministic embedding generator + a stub id, so a document upload → question round-trips
        // offline with no Cosmos or Azure OpenAI (ADR 0007/0018) — the RAG retrieval resolves from here.
        embeddingGenerator: sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
        defaultEmbeddingModelId: EmbeddingModel));

// No per-user overlay store offline (ADR 0018): the single canned factory stands in for every user.
builder.Services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
    new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));

// No per-user MCP store offline (config-driven remote MCP servers): the single-user adapter hands back an
// empty tool set for every user. The MCP providers CRUD/UI is still exercisable; only overlay→tools
// reflection is prod-wired.
builder.Services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider());
builder.Services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();
builder.Services.AddSingleton<IUserSkillProvider, UserSkillProvider>();

builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog(DefaultModel,
[
    new ModelInfo("foundry:gpt-4o", "foundry", "GPT-4o", "gpt-4o", "2024-08-06", true, true,
        ProviderDisplayName: "Azure AI Foundry"),
    new ModelInfo("foundry:gpt-4o-mini", "foundry", "GPT-4o mini", "gpt-4o-mini", "2024-07-18", true, true,
        ProviderDisplayName: "Azure AI Foundry"),
    new ModelInfo("foundry:o3-mini", "foundry", "o3-mini", "o3-mini", "2025-01-31", true, false,
        ProviderDisplayName: "Azure AI Foundry"),
    new ModelInfo(TextOnlyModel, "github", "Text-only demo (no tools, no vision)", "text-only-demo", null, false, false,
        ProviderDisplayName: "GitHub Models"),
    new ModelInfo(ImageModel, "images", "Image generator (demo)", "image-demo", null,
        SupportsTools: false, SupportsVision: false, SupportsImageGeneration: true,
        ProviderDisplayName: "Image models"),
    new ModelInfo(EmbeddingModel, "foundry", "Text embedding (demo)", "text-embedding-3-large", null,
        SupportsTools: false, SupportsVision: false, SupportsImageGeneration: false, SupportsEmbeddings: true,
        ProviderDisplayName: "Azure AI Foundry"),
]));

// No per-user overlay store offline (ADR 0018): the single static catalog stands in for every user.
builder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
    new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));

// Provider secret encryption (ADR 0018): the /api/providers endpoint encrypts keys before the grain.
// Offline uses the default EPHEMERAL key ring — nothing is persisted, so no blob store is needed. The
// per-user overlay grain still works (in-memory storage below), so the providers UI is exercisable here.
builder.Services.AddDataProtection();
builder.Services.AddSingleton<IProviderSecretProtector, ProviderSecretProtector>();
// MCP secret encryption (config-driven remote MCP servers): /api/mcp-servers encrypts the header secret
// before the grain. Same ephemeral offline ring; the per-user MCP overlay grain still works, so the MCP
// servers CRUD/UI is exercisable here (only overlay→tools reflection is prod-wired).
builder.Services.AddSingleton<IMcpSecretProtector, McpSecretProtector>();
// The interactive-OAuth HTTP client (Phase 3) — resolved by the /api/mcp/oauth/* endpoints, exercisable
// against a real remote MCP server offline (the token is stored in the in-memory overlay grain).
builder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));

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

// Google Calendar offline (ADR 0053): a WORKING canned calendar, not a degraded one — the six tools run
// end to end and create → list → update → delete really round-trips, with no Google connection and no
// request leaving the machine. That is the deliberate difference from CoordinatesOnlyGeocoder above:
// an offline calendar that answered "not connected" would leave the whole tool path unexercised.
// InMemoryCalendarService is DevHost/tests ONLY and must never be registered by Agent42.Backend.
builder.Services.AddSingleton<ICalendarService, InMemoryCalendarService>();

// The OAuth half is registered too, so Preferences → Calendar renders and its endpoints answer. With no
// GoogleCalendar:ClientId/ClientSecret configured, /api/google/status reports configured:false and /start
// 409s — which is the honest offline behaviour, and the one path that cannot be faked.
builder.Services.AddSingleton<IGoogleSecretProtector, GoogleSecretProtector>();
builder.Services.AddSingleton(new GoogleOAuthClient(new HttpClient()));

// Web Push offline (ADR 0028): no VAPID keys, so the no-op sender — the async reply paths run end to end
// but push nothing. Every AgentGrain host must register an IPushNotificationSender or activation fails, and
// every host that maps MapAgentApi must register PushConfig or /api/push/config's param inference breaks
// routing for the whole host. Enabled:false, so GET /api/push/config reports the feature off.
builder.Services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
builder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

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
