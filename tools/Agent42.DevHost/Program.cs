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
// The set is seeded (it used to be empty) so the per-MCP-tool picker is exercisable offline. Three demo
// tools under the adapter's constant "offline" server id; the Tools convenience derives KnownToolNames from
// their names, which is the offline analogue of a successful connect.
//
// demo_find_links returns a JSON LINK LIST rather than "ok", and the group is marked as URL-seeding
// (ADR 0067), because without both halves the feature is INERT offline: SingleUserMcpToolProvider never
// reads UserMcpConfig, so the checkbox saved through the DevHost UI can never reach BuildAgent — and the
// other two demo tools return a literal "ok", so there would be no URL to harvest either. With them, the
// whole path (harvest → seeded list → read_web_document) runs against InMemoryWebDocumentFetcher.
//
// The honest residual is ADR 0064's: this proves the WIRING, not an MCP handshake.
AITool[] offlineMcpTools =
[
    AIFunctionFactory.Create(() => "ok", "demo_search", "a demo MCP search tool"),
    AIFunctionFactory.Create(() => "ok", "demo_delete", "a demo MCP delete tool"),
    AIFunctionFactory.Create(
        (string filter) => "{\"links\":[{\"text\":\"the offline whitepaper\",\"href\":\"https://demo.invalid/whitepaper.pdf\"}]}",
        "demo_find_links",
        "lists the links on a page, filtered by their visible text"),
];

// Servers is assigned directly rather than through the Tools convenience, because the seeding flag is a
// per-SERVER property and belongs on the group — the adapter offers no shortcut for it, deliberately.
// KnownToolNames is derived from the tools' own names, which is what the convenience would have done.
builder.Services.AddSingleton<IUserMcpToolProvider>(new SingleUserMcpToolProvider
{
    Servers =
    [
        new McpServerTools(
            SingleUserMcpToolProvider.DefaultServerId,
            offlineMcpTools,
            [.. offlineMcpTools.Select(t => t.Name)],
            SeedsFetchableUrls: true),
    ],
});
builder.Services.AddSingleton<ISkillPackageStore, InMemorySkillPackageStore>();

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

// Provider and MCP secret encryption (ADR 0018): the protectors come from AddAgent42Api below, on this ring.
// Offline uses the default EPHEMERAL key ring — nothing is persisted, so no blob store is needed. The per-user
// overlay grains still work (in-memory storage below), so the providers and MCP servers UIs are exercisable here.
builder.Services.AddDataProtection();
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
// The local bridge (ADR 0076): a LocalBridge MCP server is reached over a socket its helper opened to
// /api/local-bridge. AddLocalBridge DECORATES the IMcpConnectionFactory registered above, so it must stay
// below it — ProjectLayoutTests.Hosts_RegisterLocalBridge pins the order.
builder.Services.AddLocalBridge();

// In-memory attachment store: no Azure Storage offline. The canned client ignores image
// content, so uploads still round-trip for the display proxy and history rendering.
builder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();

// Document RAG offline (ADR 0007): a deterministic embedding generator and an in-memory vector index, so a
// document upload → question round-trips locally with no Cosmos or Azure OpenAI. The real (Azure-free)
// extractor, chunker and ingestion service come from AddAgent42Silo below.
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>, DeterministicEmbeddingGenerator>();
builder.Services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
builder.Services.AddSingleton<IChatSearchIndex, InMemoryChatSearchIndex>();

// Web tools offline (ADR 0011): canned search results and canned page text, so the web_search /
// fetch_web_page tool path renders end to end with no SearXNG sidecar, no Docker and no network.
builder.Services.AddSingleton<IWebSearch, InMemoryWebSearch>();
builder.Services.AddSingleton<IWebPageFetcher, InMemoryWebPageFetcher>();

// read_web_document offline (ADR 0066): a canned document instead of a real download, so the whole tool
// path — allowlist check, ingest into the in-memory vector index, in-process ranking, deferred rebuild —
// runs with no network. The INGESTOR is the real one (the pipeline is Azure-free, from AddAgent42Silo
// below); only the fetcher differs offline, because only it opens a socket. That is also why no address
// guard is re-implemented here: there is nothing to guard.
builder.Services.AddSingleton<IWebDocumentFetcher, InMemoryWebDocumentFetcher>();

// Location offline (ADR 0012): the get_current_location tool path runs end to end — "where am i" makes the
// canned model call it, and the browser answers it with its real fix as a client tool (ADR 0093) — but
// nothing is reverse-geocoded, so no Google API key is needed and no request leaves the machine. The tool
// reports coordinates without a street address.
builder.Services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();

// Long-term memory offline (ADR 0056): OFF. HindsightIntegration(null) makes BuildAgent attach no memory
// provider, so nothing is recalled, nothing is retained, and no request leaves the machine — there is no
// canned Hindsight server to talk to, and a fake one would prove nothing the package's own tests do not
// already prove in its repo. Still registered, because it is a mandatory grain dependency: leaving it out
// fails activation rather than disabling memory.
builder.Services.AddSingleton(new HindsightIntegration(null));

// Copilot agents offline: the in-process twin runs a copilot chat on the canned client, with no Copilot runtime and
// no network. The grain resolves the factory optionally; registered so copilot chats work in DevHost.
builder.Services.AddSingleton<ICopilotAgentFactory, ChatClientCopilotAgentFactory>();

// The agent fleet offline (ADR 0097): the in-memory store, so tasks run on the canned client and are gone on restart.
// Copilot fleet agents are off: there is no Copilot runtime here, and a planner that asks for one gets a load error.
builder.Services.AddAgentFleetInMemory(builder.Configuration);

// Web Push offline (ADR 0028): no VAPID keys, so the no-op sender — the async reply paths run end to end
// but push nothing. Every AgentGrain host must register an IPushNotificationSender or activation fails, and
// every host that maps MapAgentApi must register PushConfig or /api/push/config's param inference breaks
// routing for the whole host. Enabled:false, so GET /api/push/config reports the feature off.
builder.Services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();

// Outbound webhooks (ADR 0094): the no-op sender. The real one's private-address guard has no local opt-out on
// purpose, so DevHost sends nothing and "Send test" reports a 200. Every AgentWebhookGrain host needs a sender.
builder.Services.AddSingleton<IAgentWebhookSender, NullAgentWebhookSender>();
builder.Services.AddSingleton<IWebhookSecretProtector, WebhookSecretProtector>();
builder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

// The chat gateway (ADR 0111) offline: one process is the whole world, so the lease is always held. Real platform
// adapters are registered too; a bot token pasted here really connects (they are outbound-only, so no tunnel).
builder.Services.AddSingleton<IChatGatewayLease, NoOpChatGatewayLease>();
// ChatGateway:TelegramApiBaseUrl points the Telegram adapter at a local stand-in for api.telegram.org, so the whole
// path (validate, poll, pair, stream, commands) runs offline against the real adapter. Unset means the real Bot API.
if (builder.Configuration["ChatGateway:TelegramApiBaseUrl"] is { Length: > 0 } telegramApi)
{
    builder.Services.AddSingleton(new TelegramOptions(new Uri(telegramApi)));
}

// The same for Slack and Discord (phase 1b): ChatGateway:SlackApiBaseUrl points the Web API (and so Socket Mode, whose
// URL apps.connections.open returns) at a stand-in; ChatGateway:DiscordApiBaseUrl + ChatGateway:DiscordGatewayUrl point
// Discord's REST and the real Discord.Net gateway client at one. Unset means the real platforms.
if (builder.Configuration["ChatGateway:SlackApiBaseUrl"] is { Length: > 0 } slackApi)
{
    builder.Services.AddSingleton(new SlackOptions(new Uri(slackApi)));
}

if (builder.Configuration["ChatGateway:DiscordApiBaseUrl"] is { Length: > 0 } discordApi)
{
    builder.Services.AddSingleton(new DiscordOptions(new Uri(discordApi), builder.Configuration["ChatGateway:DiscordGatewayUrl"]));
}

builder.Services.AddChatGateway();
// Signal (ADR 0111 phase 3): dormant unless ChatGateway:Signal:Enabled; see ChatGatewayRegistration.AddSignalChat.
builder.Services.AddSignalChat(builder.Configuration);

// The registrations every host shares (ADR 0105), AFTER every port above: the endpoint invariants, then the
// grain-side ones, which also fail startup naming any grain dependency this host did not register.
builder.Services.AddAgent42Api();
builder.Services.AddAgent42Silo();

// Inbound email (ADR 0096) offline: a fixed, PUBLIC dev signing key, so POST /api/email-in is exercisable with curl and
// a locally computed signature (HMAC-SHA256 of timestamp+token, hex). Never a real key — this file is committed.
builder.Services.AddSingleton(new EmailConfig(SigningKey: "dev-mailgun-signing-key", Domain: "in.localhost"));
builder.Services.AddSingleton<MailgunReplayGuard>();

// The server-side MCP surface (ADR 0070), registered offline exactly as in production so the feature is
// exercisable here: an MCP client can point at http://localhost:61326/api/agent-mcp with an agent's key.
// Nothing about it is faked — the gate, the key directory and the grain drain are the real ones; only the
// chat client behind them is canned.
builder.Services.AddAgentMcpServer();

// The live telemetry console at /logs (ADR 0054). addInstrumentation is TRUE here and only here: DevHost
// deliberately does NOT call AddServiceDefaults() — that would drag in resilience handlers, health checks
// and service discovery it does without — so it has no OpenTelemetry pipeline of its own and capture has
// to bring the sources and instrumentation with it. No exporter is registered, so nothing leaves the
// machine; the buffer behind /logs is the only sink offline.
builder.AddLiveTelemetryCapture(addInstrumentation: true);

// The owner tier (ADR 0062/0121). DevHost has no appsettings.json, so the owner is set here.
// "local-dev" is exactly what CurrentUser.GetUserId falls back to with no Easy Auth principal header —
// which is every request offline — so the owner-only surfaces are open here through the SAME rule that
// admits only the owners in production. There is no IsDevelopment() branch in the gate, because an
// environment branch is a second code path that only ever runs where it cannot be tested.
// NEVER put this value in src/Agent42.Backend/appsettings.json.
builder.Services.AddSingleton<IOwnerPolicy>(new OwnerPolicy(["local-dev"]));

builder.UseOrleans(silo =>
{
    silo.UseLocalhostClustering();
    silo.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
    silo.UseInMemoryReminderService();
    silo.AddDashboard();
});

var app = builder.Build();

app.UseOwnerGate();
app.UseAgentMcpKeyGate();

// The local-bridge socket (ADR 0076). Also on excludedPaths, so its endpoint self-enforces the bridge key.
// Only its UseWebSockets half is order-sensitive (the route is an endpoint); it sits here so every public,
// self-enforcing surface is wired in one place.
app.UseLocalBridge();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();
app.MapOrleansDashboardUi();
app.MapAgentMcpServer();

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
    KnownToolNames: ["demo_search", "demo_delete", "demo_find_links"],
    // Matches the provider group above (ADR 0067). Both halves again: the provider's flag is what
    // BuildAgent reads, this one is what the MCP dialog renders — a mismatch would show an unticked box
    // over a seeding server.
    SeedsFetchableUrls: true));

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
