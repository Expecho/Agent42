// Local development host for the Agent 42 frontend: serves the real wwwroot and
// the real API surface, but with localhost clustering, in-memory grain storage
// and a canned markdown-emitting chat client — no Cosmos or Azure OpenAI needed.
using Agent42.Agents;
using Agent42.Backend;
using Agent42.DevHost;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.AI;
using Orleans.Dashboard;

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

// No per-user MCP store offline (config-driven remote MCP servers): the single-user adapter hands back a
// FIXED tool set for every user. The MCP providers CRUD/UI is still exercisable; only overlay→tools
// reflection is prod-wired.
//
// The set is seeded (it used to be empty) so the per-MCP-tool picker is exercisable offline. Two demo tools
// under the adapter's constant "offline" server id; the Tools convenience derives KnownToolNames from their
// names, which is the offline analogue of a successful connect.
builder.Services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider
{
    Tools =
    [
        AIFunctionFactory.Create(() => "ok", "demo_search", "a demo MCP search tool"),
        AIFunctionFactory.Create(() => "ok", "demo_delete", "a demo MCP delete tool"),
    ],
});
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
// Connection testing (ADR 0064) over an OFFLINE fake connection factory. The real McpConnectionFactory would
// dial a live MCP server, which offline means every Test press returns "could not connect" and no leg of the
// feature is exercisable — so IMcpConnectionFactory (a seam that exists for exactly this) is faked here while
// the tester, resolver, endpoint and UI are the real ones.
//
// Honest residual: this proves the WIRING, not an MCP handshake. The real factory's wire path stays
// production-verified, as it has been since ADR 0024.
builder.Services.AddSingleton<IMcpConnectionFactory>(new OfflineMcpConnectionFactory());
builder.Services.AddSingleton<McpOAuthTokenResolver>();
builder.Services.AddSingleton<McpConnectionTester>();

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
builder.Services.AddSingleton<IChatSearchIndex, InMemoryChatSearchIndex>();
builder.Services.AddSingleton<DocumentIngestionService>();

// Web tools offline (ADR 0011): canned search results and canned page text, so the web_search /
// fetch_web_page tool path renders end to end with no SearXNG sidecar, no Docker and no network.
builder.Services.AddSingleton<IWebSearch, InMemoryWebSearch>();
builder.Services.AddSingleton<IWebPageFetcher, InMemoryWebPageFetcher>();

// Location offline (ADR 0012): the get_current_location tool path runs end to end — the browser's real
// fix rides the chat request — but nothing is reverse-geocoded, so no Google API key is needed and no
// request leaves the machine. The tool reports coordinates without a street address.
builder.Services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();

// Long-term memory offline (ADR 0056): OFF. HindsightIntegration(null) makes BuildAgent attach no memory
// provider, so nothing is recalled, nothing is retained, and no request leaves the machine — there is no
// canned Hindsight server to talk to, and a fake one would prove nothing the package's own tests do not
// already prove in its repo. Still registered, because it is a mandatory grain dependency: leaving it out
// fails activation rather than disabling memory.
builder.Services.AddSingleton(new HindsightIntegration(null));

// Web Push offline (ADR 0028): no VAPID keys, so the no-op sender — the async reply paths run end to end
// but push nothing. Every AgentGrain host must register an IPushNotificationSender or activation fails, and
// every host that maps MapAgentApi must register PushConfig or /api/push/config's param inference breaks
// routing for the whole host. Enabled:false, so GET /api/push/config reports the feature off.
builder.Services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
builder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

// The live telemetry console at /logs (ADR 0054). addInstrumentation is TRUE here and only here: DevHost
// deliberately does NOT call AddServiceDefaults() — that would drag in resilience handlers, health checks
// and service discovery it does without — so it has no OpenTelemetry pipeline of its own and capture has
// to bring the sources and instrumentation with it. No exporter is registered, so nothing leaves the
// machine; the buffer behind /logs is the only sink offline.
builder.AddLiveTelemetryCapture(addInstrumentation: true);

// The Orleans dashboard's owner (ADR 0062). DevHost has no appsettings.json, so the value is set here.
// "local-dev" is exactly what CurrentUser.GetUserId falls back to with no Easy Auth principal header —
// which is every request offline — so the dashboard is open here through the SAME rule that admits
// exactly one principal in production. That is the whole point: there is no IsDevelopment() branch in
// the gate, because an environment branch is a second code path that only ever runs where it cannot be
// tested. NEVER put this value in src/Agent42.Backend/appsettings.json.
builder.Configuration.AddInMemoryCollection(
    new Dictionary<string, string?> { [DashboardOwnerGate.OwnerOidConfigKey] = "local-dev" });

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
    silo.UseInMemoryReminderService();
    silo.AddDashboard();
});

var app = builder.Build();

app.UseOrleansDashboardOwnerGate();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();
app.MapOrleansDashboardUi();

// StartAsync + WaitForShutdownAsync rather than Run(), because the seed below is a GRAIN CALL: it needs the
// silo running, and calling it before the runtime is up NREs inside Orleans' InsideRuntimeClient.
await app.StartAsync();

// Seed the MCP overlay so the per-MCP-tool picker has something to render offline.
//
// BOTH halves are required and their ids MUST match, or the leg proves nothing. The tool PROVIDER above
// feeds AgentGrain (gating + wildcard expansion) under the constant server id "offline"; GET /api/tools
// reads the CONFIG GRAIN, which the offline adapter never writes — SingleUserMcpToolProvider does not call
// RecordToolNamesAsync (that lives in the Backend provider), and the MCP UI cannot supply KnownToolNames
// (neither McpServerUpsertRequest nor ToStored carries them). Adding a server through the UI would mint a
// fresh Guid id and get no tool rows. So the grain is seeded programmatically, pinned to "offline".
//
// Consequence, expected: these two tools enter BuildAgent's list for every offline conversation under
// null == all.
//
// "local-dev" is CurrentUser's fallback user id when no Easy Auth principal header is present, which is
// every request offline. It is private there, so it is repeated (not imported) here.
var mcpConfig = app.Services.GetRequiredService<IGrainFactory>().GetGrain<IUserMcpConfigGrain>("local-dev");
await mcpConfig.SaveAsync(new StoredMcpServer(
    Id: SingleUserMcpToolProvider.DefaultServerId,
    Name: "Demo MCP server",
    Endpoint: "https://demo.invalid/mcp",
    Auth: nameof(McpAuthMode.None),
    Enabled: true,
    KnownToolNames: ["demo_search", "demo_delete"]));

// A second server that has NEVER connected, so the cold-start half is exercisable offline too: it renders a
// plain checkbox with the "tools not discovered yet" hint and no disclosure, and — the part worth checking by
// hand — a stored mcp:{id}:{tool} key for it must SURVIVE a picker Save even though no child box exists to
// carry it. Deliberately absent from the tool provider above: a configured server nobody has connected to.
await mcpConfig.SaveAsync(new StoredMcpServer(
    Id: "offline-cold",
    Name: "Never-connected server",
    Endpoint: "https://cold.invalid/mcp",
    Auth: nameof(McpAuthMode.None),
    Enabled: true));

await app.WaitForShutdownAsync();

/// <summary>
/// Offline stand-in for <see cref="IMcpConnectionFactory"/>: hands back a couple of canned tools instead of
/// dialling a real MCP server, so the ADR 0064 Test button has something to report offline.
///
/// <para>The endpoint whose id is <c>https://cold.invalid/mcp</c> (the seeded never-connected server) THROWS,
/// so the failure leg — a curated reason, no exception text — is exercisable too.</para>
/// </summary>
internal sealed class OfflineMcpConnectionFactory : IMcpConnectionFactory
{
    public Task<McpServerConnection> ConnectAsync(
        StoredMcpServer server, McpConnectionSecrets secrets, CancellationToken cancellationToken)
    {
        if (server.Endpoint.Contains("cold.invalid", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("offline: simulated unreachable MCP server");
        }

        IReadOnlyList<AITool> tools =
        [
            AIFunctionFactory.Create(() => "ok", "demo_search", "a demo MCP search tool"),
            AIFunctionFactory.Create(() => "ok", "demo_delete", "a demo MCP delete tool"),
        ];
        return Task.FromResult(new McpServerConnection(tools, new NoopAsyncDisposable()));
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
