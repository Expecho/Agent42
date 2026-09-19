using System.Threading.Channels;
using Agent42.Agents;
using Agent42.Backend;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenTelemetry.Instrumentation.AspNetCore;
using Orleans.Dashboard;

const string OtelSourceName = LiveTelemetryCaptureExtensions.OtelSourceName;

var builder = WebApplication.CreateBuilder(args);

// Usage-telemetry ingestion channel (ADR 0031): the span processor enqueues projected usage events
// here (OnEnd, best-effort) and UsageIngestionService drains them into the global rollup grain.
// Bounded + DropWrite so a slow grain can never back-pressure the OTel export path.
var usageChannel = Channel.CreateBounded<UsageEvent>(
    new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
builder.Services.AddSingleton(usageChannel.Reader);
builder.Services.AddHostedService<UsageIngestionService>();

// OTel logging, metrics and tracing via Aspire service defaults: OTLP, plus Application Insights metrics
// when APPLICATIONINSIGHTS_CONNECTION_STRING is set.
builder.AddServiceDefaults();

// The Backend's span processors (user.* identity, usage rollups), the live telemetry console at /logs
// (ADR 0054) and, LAST, the Application Insights trace fan-out that strips chat content. One method,
// because the order is load-bearing and nothing in the suite runs this file. Right after
// AddServiceDefaults(), before anything else registers a trace processor.
builder.AddBackendTelemetry(usageChannel.Writer);

// Identity aliases (ADR 0074): an additional Entra object id resolving to the object id that owns the
// data, so one person's two accounts reach one dataset with no data movement. Built ONCE here, because
// the enrichment callback below receives an HttpRequest — not an HttpContext — and runs at
// HttpRequestIn.Start, BEFORE the middleware pipeline, where request-scoped resolution is not safe to
// assume; it captures this local instead of resolving DI. The same instance is registered for
// CurrentUser.GetUserId and UserTelemetryMiddleware, which do have request services.
// An invalid entry is dropped and reported, never fatal: identity aliasing is not on the availability
// path, so an app that boots with the alias inert is recoverable where one that refuses to boot is not.
var userIdAliases = UserIdAliases.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(userIdAliases);

// Stamp OTel semantic-convention user.* attributes onto the ASP.NET Core server span of every
// authenticated request (ADR 0009). The Easy-Auth-specific extraction lives in CurrentUser here in
// the Backend, keeping the shared ServiceDefaults generic (ADR 0002). This Configure delegate
// composes with the AddAspNetCoreInstrumentation Filter set in ServiceDefaults — both run, they set
// different options properties, so neither overwrites the other.
builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
    options.EnrichWithHttpRequest = (activity, request) =>
        activity.ApplyUserTelemetryTags(request.Headers, userIdAliases));

// CosmosClient wired from the Aspire-provided "cosmos" connection.
builder.AddAzureCosmosClient("cosmos");

// No operator embedding floor any more: embeddings are a per-user model chosen in Preferences (the
// document-RAG generator resolves from the user's overlay, ADR 0007/0018), so AzureOpenAI:Endpoint/ApiKey
// are no longer eager-validated at startup. They remain readable ONLY by the dormant Foundry-provider
// inheritance (ProviderSettings.ResolveFoundry) — baseline Models:Providers is empty (ADR 0019), so nothing
// triggers it; a user's Foundry provider declares its own endpoint + key. The app now boots with no Azure
// OpenAI config at all, like it already did with no chat provider.

// The managed identity drives the services that are NOT Azure OpenAI: blob storage (user-delegation SAS)
// and — kept for a future discovering provider — the ARM management plane. TenantId still binds it.
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    TenantId = builder.Configuration["AzureOpenAI:TenantId"],
});

// Model providers (ADR 0013, availability floor removed by ADR 0019). The app ships NO baseline chat
// provider — Models:Providers is empty in appsettings — so chat is entirely the per-user overlay
// (ADR 0018): a user with none configured gets an empty picker and a gated composer, and their grain
// stays valid via the no-op NoModelChatClient. There is no operator default model; a user who picks a
// third-party endpoint they added still sends the session — document excerpts (ADR 0008), image SAS URLs
// (ADR 0004), the user's street address (ADR 0012) — outside the Entra tenant, which is why the pick is
// explicit per conversation. The empty baseline's Default stays "".
var modelOptions = builder.Configuration.GetSection(ModelProvidersOptions.SectionName)
    .Get<ModelProvidersOptions>() ?? new ModelProvidersOptions();

builder.Services.AddSingleton(new ArmClient(credential));

// The rules — which providers are skipped, what a Foundry provider inherits, what a declared model
// contributes — live in ModelProviderBuilder, not here: nothing in the test suite runs Program.cs.
var models = ModelProviderBuilder.Build(modelOptions, builder.Configuration);

// The BASELINE factory (the operator env providers). Now ships EMPTY (ADR 0019 removed the baseline
// chat availability floor), so it hands out the no-op NoModelChatClient with DefaultModelId "". The agent
// grain never consumes it directly — it goes through the per-user layer (ADR 0018), which uses this same
// baseline as its input; an empty baseline means the per-user set is the ONLY source of chat providers.
builder.Services.AddSingleton<IChatClientFactory>(sp => new MultiProviderChatClientFactory(
    models.Providers,
    modelOptions.Default,
    OtelSourceName,
    sp.GetRequiredService<ILogger<MultiProviderChatClientFactory>>()));

// Per-user providers (ADR 0018): the resolver merges the baseline with the signed-in user's overlay,
// and the factory provider hands the agent grain a user-scoped factory (resolved on activation, cached
// by the overlay's version). IProviderSecretProtector is registered further down (DI is lazy).
builder.Services.AddSingleton<IUserProviderResolver>(sp => new UserProviderResolver(
    sp.GetRequiredService<IGrainFactory>(),
    sp.GetRequiredService<IProviderSecretProtector>(),
    models,
    modelOptions.Default));

builder.Services.AddSingleton<IUserChatClientFactoryProvider>(sp => new UserChatClientFactoryProvider(
    sp.GetRequiredService<IUserProviderResolver>(),
    OtelSourceName,
    sp.GetRequiredService<ILogger<MultiProviderChatClientFactory>>()));

builder.Services.AddSingleton<IModelCatalog>(sp => new CompositeModelCatalog(
    [.. models.Discovering.Select(d => new AzureModelCatalog(
        sp.GetRequiredService<ArmClient>(), d.Key, d.DisplayName, d.Settings, modelOptions.Default,
        sp.GetRequiredService<ILogger<AzureModelCatalog>>()))],
    models.DeclaredModels,
    modelOptions.Default,
    sp.GetRequiredService<ILogger<CompositeModelCatalog>>()));

// Per-user model catalog (ADR 0018): /api/models is user-scoped, built from the same resolver as the
// per-user factory so an added provider's models appear in the picker without a restart.
builder.Services.AddSingleton<IUserModelCatalogProvider>(sp => new UserModelCatalogProvider(
    sp.GetRequiredService<IUserProviderResolver>(),
    sp.GetRequiredService<ArmClient>(),
    sp.GetRequiredService<ILoggerFactory>()));

// Blob-backed attachment store (ADR 0004): durable image bytes live in blob storage, never in
// the serialized session. Prefer an Aspire-provided connection (Azurite under the AppHost);
// otherwise use the blob endpoint + the same managed identity, whose Storage Blob Data
// Contributor role also enables key-free user-delegation SAS.
var blobConnection = builder.Configuration.GetConnectionString("blobs");
if (!string.IsNullOrWhiteSpace(blobConnection))
{
    builder.Services.AddSingleton(new BlobServiceClient(blobConnection));
}
else
{
    var blobEndpoint = builder.Configuration["Storage:BlobEndpoint"]
        ?? throw new InvalidOperationException(
            "Storage:BlobEndpoint is not configured. Set it in appsettings.json, user secrets, " +
            "or the Storage__BlobEndpoint environment variable (e.g. https://<account>.blob.core.windows.net).");
    builder.Services.AddSingleton(new BlobServiceClient(new Uri(blobEndpoint), credential));
}

builder.Services.AddSingleton<IAttachmentStore, BlobAttachmentStore>();

// Data Protection (ADR 0018): encrypts the API keys of user-configured providers. In prod the key
// ring MUST be persisted out-of-process — an ephemeral ring rotates on restart and orphans every
// stored secret. Gate on the PROD branch (Storage:BlobEndpoint set, and NOT the Azurite/AppHost
// GetConnectionString("blobs") path) and use the MI PersistKeysToAzureBlobStorage(BlobClient) overload:
// shared-key auth is disabled on the account, so the connection-string overload cannot work. The
// master key itself is protected only by Storage SSE + RBAC (no ProtectKeysWith…) — the user secrets
// it encrypts are DP-encrypted on top. DevHost/tests fall through to the default ephemeral ring (fine —
// nothing persisted there).
var dataProtection = builder.Services.AddDataProtection();
var dataProtectionBlobEndpoint = builder.Configuration["Storage:BlobEndpoint"];
if (string.IsNullOrWhiteSpace(blobConnection) && !string.IsNullOrWhiteSpace(dataProtectionBlobEndpoint))
{
    var keyRingBlob = new BlobClient(
        new Uri(dataProtectionBlobEndpoint.TrimEnd('/') + "/dataprotection/keys.xml"), credential);
    dataProtection.PersistKeysToAzureBlobStorage(keyRingBlob);
}

builder.Services.AddSingleton<IProviderSecretProtector, ProviderSecretProtector>();

// Encrypts the secrets of user-configured MCP servers (config-driven remote MCP servers), on the same
// DP key ring but a distinct protection scope (IMcpSecretProtector's own purpose) so MCP-secret rotation
// is independent of model-provider keys. DevHost/tests fall through to the default ephemeral ring.
builder.Services.AddSingleton<IMcpSecretProtector, McpSecretProtector>();

// Per-user MCP tools (config-driven remote MCP servers): the provider connects to the signed-in user's
// enabled MCP servers and hands the agent grain their tools (resolved on activation, version-cached,
// best-effort, bounded connection lifetime). The agent grain merges them into BuildAgent's tool list,
// gated by the ToolCatalog.Mcp key (ADR 0021).
builder.Services.AddSingleton<IMcpConnectionFactory, McpConnectionFactory>();
builder.Services.AddSingleton<IUserMcpToolProvider, UserMcpToolProvider>();
// The InteractiveOAuth access-token resolution shared by the activation path above and the user-pressed
// connection test below (ADR 0064). Extracted so the test cannot false-negative on a server the chat path
// connects to fine: there is exactly one implementation of the refresh-at-connect rule.
builder.Services.AddSingleton<McpOAuthTokenResolver>();
// The one-shot connection probe behind POST /api/mcp-servers/{id}/test. Resolved OPTIONALLY out of
// IServiceProvider inside the endpoint handler (the /api/speech pattern), so a host without an
// IMcpConnectionFactory — DevHost's real shape before its offline fake — degrades to a curated 503 instead
// of breaking route matching for the whole host.
builder.Services.AddSingleton<McpConnectionTester>();
// The local bridge (ADR 0076): a LocalBridge MCP server is reached over a socket its helper opened to
// /api/local-bridge. AddLocalBridge DECORATES the IMcpConnectionFactory registered above, so it must stay
// below it — ProjectLayoutTests.Hosts_RegisterLocalBridge pins the order.
builder.Services.AddLocalBridge();
// Uploaded agent skills (ADR 0049). The two registrations are NOT equivalent, and the asymmetry is
// deliberate: IUserSkillProvider is an AgentGrain constructor dependency, so it is MANDATORY in every
// host that activates the grain (the IUserMcpToolProvider / IPushNotificationSender shape — miss it and
// every activation fails). ISkillPackageStore is only ever resolved OPTIONALLY, out of IServiceProvider
// inside the endpoint handler, so a host that omits it degrades to a 409 instead of breaking route
// matching for the whole host (the /api/speech and /api/config-health pattern).
builder.Services.AddSingleton<ISkillPackageStore, BlobSkillPackageStore>();
builder.Services.AddSingleton<IUserSkillProvider, UserSkillProvider>();
// Drives the interactive-OAuth HTTP exchanges (PRM discovery, code→token, refresh — Phase 3) over a plain
// HttpClient (not DI-resolved, so no AddServiceDefaults resilience inheritance). Used by the /api/mcp/oauth/*
// endpoints and by the tool provider's connect-time token refresh.
builder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));

// Document RAG (ADR 0007/0018). There is NO operator embedding generator: the embedding model is
// per-user (chosen in Preferences), so ingestion and retrieval resolve their generator from the user's
// chat-client factory (IUserChatClientFactoryProvider.GetEmbeddingGenerator) — no fixed deployment, no
// AzureOpenAI:EmbeddingDeploymentName prerequisite. A user with no embedding model configured cannot
// attach documents (the composer gates it) and RAG retrieval degrades to empty.
builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<DocumentChunker>();
builder.Services.AddSingleton<DocumentIngestionService>();

// The document index (ADR 0007) branches on whether the doc-chunks container is configured;
// that decision lives with the Documents feature (ADR 0040/0050), not inline here.
builder.Services.AddDocumentIndex(builder.Configuration);
builder.Services.AddChatSearchIndex(builder.Configuration);

// Web tools (ADR 0011): search runs against the SearXNG sidecar in this Container App, reached over
// localhost. Eager-validated so a missing endpoint fails at startup rather than in the middle of a
// chat — same discipline as the embedding deployment above.
var webSearchEndpoint = builder.Configuration["WebSearch:Endpoint"];
if (string.IsNullOrWhiteSpace(webSearchEndpoint))
{
    throw new InvalidOperationException(
        "WebSearch:Endpoint is not configured. Set it in appsettings.json, user secrets, or the " +
        "WebSearch__Endpoint environment variable (the SearXNG sidecar, e.g. http://localhost:8888).");
}

// Explicit timeouts on both web clients, and NO retry pipeline.
//
// AddServiceDefaults() applies AddStandardResilienceHandler() to EVERY HttpClient via
// ConfigureHttpClientDefaults, so these named clients would silently inherit 3 retries plus the
// handler's own generous per-attempt/total timeouts. Both are wrong here, and RemoveAllResilienceHandlers
// is what actually opts out (setting HttpClient.Timeout alone does not — the pipeline still wraps it):
//   * AgentGrain is NON-REENTRANT, so a slow search→fetch chain serializes the conversation's turn and
//     delays the user's first token — and a retry multiplies that stall by four. The scrape client's
//     timeout is higher than the search client's because a context.dev scrape renders the page (and, when
//     following, does 1 + N of them) — but it is still bounded, for the same non-reentrancy reason.
// A web lookup that hasn't answered within these timeouts is not worth the stall; the tools degrade by
// telling the model the lookup failed.
// EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental, but it is the only supported way
// to opt a named client out of the pipeline ConfigureHttpClientDefaults applied to it. Suppressed
// deliberately and narrowly: the alternative — inheriting retries that stall a non-reentrant grain — is a
// latency regression, not a style preference. If this API is ever removed, the fix is to stop applying
// the standard handler by default in ServiceDefaults, not to accept the retries.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient(SearxngWebSearch.HttpClientName, http =>
{
    http.BaseAddress = new Uri(webSearchEndpoint);
    http.Timeout = TimeSpan.FromSeconds(10);
})
    .RemoveAllResilienceHandlers();

// fetch_web_page over the hosted context.dev scrape API (ADR 0027). Unlike the old WebPageFetcher this is
// NOT an SSRF sink — our container never dials the model's URL, context.dev does — so there is NO
// ConfigurePrimaryHttpMessageHandler / connect-time guard to install here; the host is fixed and ours.
// A missing key is NOT eager-validated into a startup throw (unlike SEARXNG_SECRET, which is on the
// availability path): with no key the client is still registered, calls 401, and the tool tells the model
// the page could not be read. ConfigureClient carries the blank-key branch — a blank bearer value throws.
var contextApiKey = builder.Configuration["ContextScrape:ApiKey"];
builder.Services.AddHttpClient(ContextWebPageFetcher.HttpClientName, http =>
{
    ContextWebPageFetcher.ConfigureClient(http, contextApiKey);
    http.Timeout = TimeSpan.FromSeconds(30);
})
    .RemoveAllResilienceHandlers();

// read_web_document over an in-container fetch (ADR 0066). THIS ONE IS AN SSRF SINK, unlike the scrape
// client above, and the ConfigurePrimaryHttpMessageHandler further down is the entire control: our
// container — which holds the managed identity Azure IMDS mints Cosmos/Storage/OpenAI tokens for — dials
// a URL the MODEL chose, and (because /api/agent-run is on excludedPaths, ADR 0022) a URL an API key
// holder can put in front of it with no browser session. PrivateAddressGuard runs at CONNECT time inside
// GuardedDocumentFetcher.CreateHandler, so it sees the address actually dialled and covers every
// redirect hop; a guard applied to the URL string instead would be TOCTOU-broken and blind to redirects.
// A guard that is built, tested and never wired here is the highest-consequence way this feature can
// ship green, which is why a source-text assertion pins that line.
//
// Explicit timeout + RemoveAllResilienceHandlers for the same reason as the clients above; note the
// timeout is NOT the whole story here, because the fetcher reads with ResponseHeadersRead — see
// WebDocumentLimits.DownloadDeadline, which bounds the body read the timeout stops covering.
builder.Services.AddHttpClient(GuardedDocumentFetcher.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(30);
})
    .ConfigurePrimaryHttpMessageHandler(
        () => GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable))
    .RemoveAllResilienceHandlers();

// The SearXNG web UI proxy (ADR 0059). A SECOND client against the same sidecar, deliberately: three
// primary-handler defaults that suit web_search break a proxy, and all three are shared with
// SearxngWebSearch and ConfigHealth's probe, so they cannot simply be changed on the existing client.
//   * AllowAutoRedirect — the client would follow the 3xx itself, so the endpoint would never see a
//     Location to rewrite; the redirect rules would be dead in production while green against a stub.
//   * UseCookies — the pooled CookieContainer is shared across every consumer of the named client, so
//     one user's SearXNG preference cookie would replay onto another user's request AND onto
//     web_search's own queries, silently changing search behaviour.
//   * AutomaticDecompression — without it the proxy forwards the browser's Accept-Encoding and emits
//     gzip bytes labelled text/html.
// RemoveAllResilienceHandlers for the same reason as the two clients above: a retried proxy request is
// a stalled page, and the grain-latency argument applies to a user watching a browser tab too.
builder.Services.AddHttpClient(SearxngProxy.HttpClientName, http =>
{
    http.BaseAddress = new Uri(webSearchEndpoint);
    http.Timeout = TimeSpan.FromSeconds(10);
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

builder.Services.AddSingleton<IWebSearch>(sp => new SearxngWebSearch(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(SearxngWebSearch.HttpClientName),
    sp.GetRequiredService<ILogger<SearxngWebSearch>>()));

builder.Services.AddSingleton<IWebPageFetcher>(sp => new ContextWebPageFetcher(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(ContextWebPageFetcher.HttpClientName),
    sp.GetRequiredService<ILogger<ContextWebPageFetcher>>()));

// read_web_document (ADR 0066): the guarded fetcher over the client registered above, plus the ingestion
// port the tool reaches the Backend-owned pipeline through. Both are mandatory AgentGrain dependencies,
// so every silo host must register them or activation fails.
builder.Services.AddSingleton<IWebDocumentFetcher>(sp => new GuardedDocumentFetcher(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(GuardedDocumentFetcher.HttpClientName)));

builder.Services.AddSingleton<IDocumentIngestor, WebDocumentIngestor>();

// User location (ADR 0012): reverse-geocoding for the get_current_location tool.
//
// Unlike the embedding deployment above, a missing key is NOT eager-validated into a startup throw —
// it degrades. Without a key the tool still works and still reports the user's coordinates; it just
// cannot name the street. That is why this branch exists rather than a null check at the call site.
var googleApiKey = builder.Configuration["Geocoding:GoogleApiKey"];
if (!string.IsNullOrWhiteSpace(googleApiKey))
{
    var geocodingBaseAddress =
        builder.Configuration["Geocoding:BaseAddress"] ?? GoogleGeocoder.DefaultBaseAddress;

    // Same explicit-timeout / no-retry discipline as the web clients above, and for the same reason:
    // AgentGrain is non-reentrant, so a retried geocode multiplies the stall on the user's turn. 5s,
    // because the user's turn is blocked on it — and a geocode that hasn't answered by then is not
    // worth the wait (the tool degrades to coordinates).
#pragma warning disable EXTEXP0001
    builder.Services.AddHttpClient(GoogleGeocoder.HttpClientName, http =>
    {
        http.BaseAddress = new Uri(geocodingBaseAddress);
        http.Timeout = TimeSpan.FromSeconds(5);
    })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

    builder.Services.AddSingleton<IGeocoder>(sp => new GoogleGeocoder(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleGeocoder.HttpClientName),
        googleApiKey,
        sp.GetRequiredService<ILogger<GoogleGeocoder>>()));
}
else
{
    builder.Services.AddSingleton<IGeocoder, CoordinatesOnlyGeocoder>();
}

// Long-term memory across conversations (ADR 0056): a Hindsight memory bank per user, recalled into the
// agent's instructions before each model call and retained after it. Optional and off by default; see
// HindsightRegistration for what the two branches mean and why this is not inline here.
builder.Services.AddHindsightMemory(builder.Configuration);

// Web Push notifications (ADR 0028): a push when an agent replies on an async path (a fired reminder or an
// agent-run) while the user is away.
//
// Like the geocoding and context.dev keys above, a missing key is NOT eager-validated into a startup throw —
// the VAPID private key is the ONLY secret and is OPTIONAL / not on the availability path (copy the
// contextApiKey/googleMapsApiKey shape, NOT searxngSecret): with no key the app starts, PushConfig reports
// Enabled:false, GET /api/push/config returns enabled:false, the UI hides the toggle, and the no-op sender
// does nothing. The public key + subject are non-secret (committed in appsettings.json); the browser fetches
// the public key from GET /api/push/config. Enabled is the single source of the "push is on" fact — the same
// signal that decides real-vs-null sender here and what the endpoint reports.
var vapidPublicKey = builder.Configuration["Push:VapidPublicKey"] ?? "";
var vapidPrivateKey = builder.Configuration["Push:VapidPrivateKey"] ?? "";
var vapidSubject = builder.Configuration["Push:VapidSubject"] ?? "";
// "Enabled" must mean "actually usable", so it requires BOTH the private key (the secret that lets us
// send) AND the committed public key (which the browser needs to subscribe). Gating on the private key
// alone would let a half-configured operator — secret set but public key not yet pasted — ship a visible
// toggle whose Enable click calls pushManager.subscribe with an empty applicationServerKey and silently
// fails. With either missing, push is simply off (the optional-secret degradation).
var pushEnabled = !string.IsNullOrWhiteSpace(vapidPrivateKey) && !string.IsNullOrWhiteSpace(vapidPublicKey);

builder.Services.AddSingleton(new PushConfig(pushEnabled, vapidPublicKey, vapidSubject));

// The server-side MCP surface at /api/agent-mcp (ADR 0070) — stored agents exposed as a tool to an
// external MCP client, the inverse direction of the client-side servers above. Deliberately not part of
// MapAgentApi: MapMcp throws without these services, and a dependency one host lacks breaks routing for
// every endpoint that host maps. See AgentMcpServerRegistration.
builder.Services.AddAgentMcpServer();

if (pushEnabled)
{
    // Same explicit-timeout / no-retry discipline as the web + geocoding clients above, and for the same
    // reason: the send runs inside a NON-REENTRANT AgentGrain activation, so a retried send to a dead push
    // endpoint would multiply the stall on the grain. The timeout matches the sender's whole-fan-out budget;
    // RemoveAllResilienceHandlers is what actually opts out of the inherited 3-retry pipeline.
#pragma warning disable EXTEXP0001
    builder.Services.AddHttpClient(WebPushClientAdapter.HttpClientName, http =>
    {
        http.Timeout = WebPushNotificationSender.SendBudget;
    })
        .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

    builder.Services.AddSingleton<IWebPushClient>(sp => new WebPushClientAdapter(
        sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebPushClientAdapter.HttpClientName),
        vapidSubject, vapidPublicKey, vapidPrivateKey));
    builder.Services.AddSingleton<IPushNotificationSender, WebPushNotificationSender>();
}
else
{
    builder.Services.AddSingleton<IPushNotificationSender, NullPushNotificationSender>();
}

// Orleans silo: clustering comes from the Aspire Orleans resource when running
// under Aspire orchestration; localhost clustering is the fallback for a single
// standalone instance (e.g. one Container App replica with no Aspire AppHost).
// the agent state is persisted with Microsoft.Orleans.Persistence.Cosmos.
builder.UseOrleans(silo =>
{
    // Flow the W3C trace context (Activity.Current) across grain-call boundaries. Orleans does NOT
    // propagate it by default — grain calls hop through the Orleans scheduler, which does not carry
    // Activity.Current across the boundary — so without this, any span started INSIDE a grain (the
    // HttpClient instrumentation span for the chat-completion / geocoding / web-fetch calls) begins as
    // a new ROOT span, detached from the ASP.NET server span that triggered it. That is the "many
    // traces have no parent" symptom. This is a DISTINCT mechanism from the user.* attribution channel
    // (ADR 0010): identity rides Orleans RequestContext, which IS auto-propagated — trace-parent linkage
    // is not. Registers both the incoming and outgoing grain-call filters (this process co-hosts the
    // silo + client, so one call covers both directions). Async paths (fired reminders, agent-run) still
    // begin fresh roots by design — they have no ambient inbound Activity. See gotchas.
    silo.AddActivityPropagation();
    silo.AddDashboard(); // ADR 0062. Consequences + why NOT the community package: Directory.Packages.props.

    if (string.IsNullOrEmpty(builder.Configuration["Orleans:Clustering:ProviderType"]))
    {
        silo.UseLocalhostClustering();
    }

    silo.AddCosmosGrainStorage(AgentGrain.StorageProviderName, options =>
    {
        options.ConfigureCosmosClient(sp =>
            ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
        options.DatabaseName = "agent42";
        options.ContainerName = "agent-state";
        options.IsResourceCreationEnabled = false;
    });

    // Durable reminders (the schedule_prompt agent tool) persisted in Cosmos, so a
    // scheduled reminder survives silo restarts just like the agent state does.
    // Resource creation is disabled: the account has disableLocalAuth: true, and
    // Cosmos's AAD data-plane RBAC cannot create databases/containers (only ARM
    // control-plane can), so the "reminders" container is pre-created in
    // infra/modules/cosmos.bicep instead.
    silo.UseCosmosReminderService(options =>
    {
        options.ConfigureCosmosClient(sp =>
            ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
        options.DatabaseName = "agent42";
        options.ContainerName = "reminders";
        options.IsResourceCreationEnabled = false;
    });
});

var app = builder.Build();

// An alias entry is an authorization grant, so one refused for being malformed must be visible rather
// than silently absent. Warning, not Information: Logging:LogLevel:Default is Warning in production, so
// an Information record would never reach a log at all (ADR 0054). The Configuration health tab reports
// the same count for the operator who is not reading logs.
if (userIdAliases.DroppedEntries.Count > 0)
{
    app.Logger.LogWarning(
        "Ignored {Count} invalid identity alias entry/entries (ADR 0074): {Detail}",
        userIdAliases.DroppedEntries.Count,
        string.Join("; ", userIdAliases.DroppedEntries));
}

// Say so once, loudly enough to find, when a provider was dropped for want of a key — otherwise
// "the GitHub models aren't in the picker" is a silent mystery. Deliberately NOT a throw (ADR 0013):
// the app must keep serving on Foundry when a third-party token is missing or has expired.
foreach (var skipped in models.SkippedProviders)
{
    app.Logger.LogWarning(
        "Model provider '{Provider}' has no ApiKey and was skipped; its models will not appear in the "
        + "picker. Set Models__Providers__{Provider}__ApiKey to enable it.", skipped, skipped);
}

// Say so once, loudly enough to find, when the location tool is running without reverse geocoding —
// otherwise "it only ever gives me coordinates" is a silent mystery (ADR 0012). Not a throw: an absent
// key degrades the feature, it does not take the app down.
if (app.Services.GetRequiredService<IGeocoder>() is CoordinatesOnlyGeocoder)
{
    app.Logger.LogInformation(
        "Geocoding:GoogleApiKey is not configured — get_current_location will report coordinates " +
        "without a street address. Set Geocoding__GoogleApiKey to enable reverse geocoding.");
}

// Ahead of everything else, because it rewrites the request itself rather than observing it: the ACA
// ingress terminates TLS and forwards over plain HTTP, so without this Request.Scheme is "http" for every
// request and two consumers mint http:// absolute URLs — the share link owners copy (ADR 0035/0042) and
// the MCP interactive-OAuth redirect_uri (ADR 0025) — as does the framework's own UseDefaultFiles 301,
// which is why this sits in front of the static-file middleware too.
//
// XForwardedProto ONLY: nothing here needs the client IP or host, and trusting those from an unidentified
// proxy is a strictly larger surface for no consumer. ForwardLimit stays at its default of 1 — a single
// ingress hop.
//
// The trust lists must be emptied with .Clear(), NOT with an `= { }` initializer: those properties are
// get-only, so `KnownNetworks = { }` parses as a nested collection initializer that adds nothing and
// leaves the framework defaults (127.0.0.0/8 and ::1) in place — it compiles, reads as "cleared", and
// silently makes this whole middleware inert for any ingress that isn't loopback. Measured, not assumed
// (ADR 0042). Emptying them is required because the ACA ingress's address is not knowable from inside the
// container, which does mean a client can spoof X-Forwarded-Proto. Accepted: the blast radius is the
// scheme of a minted URL, every external request arrives through the ingress that sets the header, and
// the share token never rides the path anyway (it is in the fragment).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// First of our own middleware (the proxy fix-up above only corrects what the ingress obscured): extract
// the request's identity once and publish it into Orleans
// RequestContext, so it propagates to every downstream span — in-request HttpClient spans and,
// across the grain call, the in-grain chat-completion span (ADR 0010). RequestContext is an
// in-cluster channel (never an outbound HTTP header), so no user PII leaks to Azure OpenAI —
// the reason this is RequestContext and deliberately not Activity.Baggage. Runs after the server
// span has already started, which is why that span stays enriched via EnrichWithHttpRequest above.
app.Use(UserTelemetryMiddleware.InvokeAsync);

// Restrict the Orleans dashboard to its single configured owner (ADR 0062). Easy Auth already gates
// the path — it is absent from excludedPaths, which is default-deny — so this is the second, narrower
// rule that keeps a cluster console from becoming a third cross-user read surface beside /logs and
// /usage. MUST stay ABOVE UseDefaultFiles/UseStaticFiles: below them it still works today only
// because no wwwroot/dashboard/ exists, and anyone later adding one would have it served ungated
// while the gate still looked installed. ProjectLayoutTests asserts this ordering.
app.UseOrleansDashboardOwnerGate();

// Authenticate the server-side MCP surface (ADR 0070) BEFORE the protocol reaches it. /api/agent-mcp is
// on excludedPaths, so Easy Auth never sees it and MapMcp serves initialize/tools/list from the same
// endpoint as tools/call — a check living only inside the tool method would leave the first two
// anonymously reachable. Same ordering rule as the dashboard gate above: ABOVE UseDefaultFiles/
// UseStaticFiles, so it cannot be bypassed by a file later dropped under that path in wwwroot.
app.UseAgentMcpKeyGate();

// The local-bridge socket (ADR 0076). Also on excludedPaths, so its endpoint self-enforces the bridge key.
// Only its UseWebSockets half is order-sensitive (the route is an endpoint); it sits here so every public,
// self-enforcing surface is wired in one place.
app.UseLocalBridge();

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();
app.MapOrleansDashboardUi();
app.MapAgentMcpServer();

app.Run();
