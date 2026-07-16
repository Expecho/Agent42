using Agent42.Agents;
using Agent42.Backend;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Trace;

const string OtelSourceName = "Agent42.Backend";

// The provider key the Azure AI Foundry account is configured under in appsettings' Models:Providers
// map. Only used to qualify the inherited default model (see below); providers are otherwise
// discovered from config, never named in code.
const string FoundryProviderKey = "foundry";

var builder = WebApplication.CreateBuilder(args);

// OTel logging, metrics and tracing (OTLP exporter) via Aspire service defaults.
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(OtelSourceName)
        // Carry the user.* identity to downstream/in-grain spans (ADR 0010): the processor stamps
        // every started span from the identity a request-scoped middleware writes into Orleans
        // RequestContext (see app.Use below). Complements — does not replace — the server-span
        // EnrichWithHttpRequest above, which owns the one span that starts before that middleware.
        .AddProcessor(new UserTelemetrySpanProcessor()));

// Stamp OTel semantic-convention user.* attributes onto the ASP.NET Core server span of every
// authenticated request (ADR 0009). The Easy-Auth-specific extraction lives in CurrentUser here in
// the Backend, keeping the shared ServiceDefaults generic (ADR 0002). This Configure delegate
// composes with the AddAspNetCoreInstrumentation Filter set in ServiceDefaults — both run, they set
// different options properties, so neither overwrites the other.
builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
    options.EnrichWithHttpRequest = (activity, request) =>
        activity.ApplyUserTelemetryTags(request.Headers));

// CosmosClient wired from the Aspire-provided "cosmos" connection.
builder.AddAzureCosmosClient("cosmos");

// The chat model backing the agent, wrapped with OTel instrumentation.
// Validated eagerly below so a missing endpoint fails at startup, not on the
// first grain activation.
var azureOpenAIEndpoint = builder.Configuration["AzureOpenAI:Endpoint"];
if (string.IsNullOrWhiteSpace(azureOpenAIEndpoint))
{
    throw new InvalidOperationException(
        "AzureOpenAI:Endpoint is not configured. Set it in appsettings.json, " +
        "user secrets, or the AzureOpenAI__Endpoint environment variable " +
        "(e.g. https://<your-resource>.services.ai.azure.com).");
}

// The Azure OpenAI API key (ADR 0017): the SINGLE source that authenticates both the Foundry chat/
// Responses provider (which inherits it, see ModelProviderBuilder) and the embedding client below.
// It is the availability path, so a blank key fails at startup — same discipline as the endpoint above,
// and mirrored by the Bicep @minLength guard. (ModelProviderBuilder also throws for the provider, but the
// embedding client reads this key directly and never sees the Models: section, so validate it here too.)
var azureOpenAIApiKey = builder.Configuration["AzureOpenAI:ApiKey"];
if (string.IsNullOrWhiteSpace(azureOpenAIApiKey))
{
    throw new InvalidOperationException(
        "AzureOpenAI:ApiKey is not configured. Set it in user secrets or the AzureOpenAI__ApiKey " +
        "environment variable (the Azure OpenAI resource key; ADR 0017 replaced managed identity for the " +
        "Foundry data plane). It is a secret and must never be committed.");
}

var defaultDeployment = builder.Configuration["AzureOpenAI:DeploymentName"] ?? "gpt-5.4";

// The managed identity still drives the services that are NOT Azure OpenAI and cannot use the key:
// blob storage (user-delegation SAS) and — kept for a future discovering provider — the ARM management
// plane. Foundry inference/embeddings moved to the API key (ADR 0017).
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    TenantId = builder.Configuration["AzureOpenAI:TenantId"],
});

// Model providers (ADR 0013). A conversation can run on any configured provider — Azure AI Foundry
// with the managed identity, or any OpenAI-compatible endpoint with an API key (GitHub Models in
// prod; an Ollama or vLLM endpoint works by config alone, with no code change).
//
// The default stays a FOUNDRY model, and that is a deliberate security position, not a default that
// happened: a third-party model is opt-in per conversation, because picking one sends the session —
// document excerpts (ADR 0008), image SAS URLs (ADR 0004) and the user's street address (ADR 0012) —
// outside the Entra tenant.
var modelOptions = builder.Configuration.GetSection(ModelProvidersOptions.SectionName)
    .Get<ModelProvidersOptions>() ?? new ModelProvidersOptions();

// The default model INHERITS AzureOpenAI:DeploymentName (which Bicep threads in) rather than being
// hardcoded in appsettings — otherwise that env var would look authoritative and change nothing, the
// same dead-config trap the SearXNG server.port gotcha describes. Setting Models:Default explicitly is
// supported, and is the only way to default to a non-Foundry model — which would mean defaulting the
// whole app to a third-party endpoint, so ADR 0013 keeps Foundry the default deliberately.
if (string.IsNullOrWhiteSpace(modelOptions.Default))
{
    modelOptions.Default = ModelId.Qualify(FoundryProviderKey, defaultDeployment);
}

builder.Services.AddSingleton(new ArmClient(credential));

// The rules — which providers are skipped, what a Foundry provider inherits, what a declared model
// contributes — live in ModelProviderBuilder, not here: nothing in the test suite runs Program.cs.
var models = ModelProviderBuilder.Build(modelOptions, builder.Configuration);

builder.Services.AddSingleton<IChatClientFactory>(sp => new MultiProviderChatClientFactory(
    models.Providers,
    modelOptions.Default,
    OtelSourceName,
    sp.GetRequiredService<ILogger<MultiProviderChatClientFactory>>()));

builder.Services.AddSingleton<IModelCatalog>(sp => new CompositeModelCatalog(
    [.. models.Discovering.Select(d => new AzureModelCatalog(
        sp.GetRequiredService<ArmClient>(), d.Key, d.Settings, modelOptions.Default,
        sp.GetRequiredService<ILogger<AzureModelCatalog>>()))],
    models.DeclaredModels,
    modelOptions.Default,
    sp.GetRequiredService<ILogger<CompositeModelCatalog>>()));

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

// Document RAG (ADR 0007). The embedding deployment is a manual prerequisite the user owns (a
// text-embedding-3-large deployment in the AI Foundry account, RG agent42 — the same account the
// chat client uses, so no new RBAC). Validated eagerly so a missing deployment name fails at
// startup, not on the first upload.
var embeddingDeployment = builder.Configuration["AzureOpenAI:EmbeddingDeploymentName"];
if (string.IsNullOrWhiteSpace(embeddingDeployment))
{
    throw new InvalidOperationException(
        "AzureOpenAI:EmbeddingDeploymentName is not configured. Set it in appsettings.json, " +
        "user secrets, or the AzureOpenAI__EmbeddingDeploymentName environment variable " +
        "(the name of a text-embedding-3-large deployment in the AI Foundry account).");
}

// Reuse the same endpoint + API key as the Foundry chat provider (ADR 0017): one source of truth for
// the key, exactly like the endpoint. The key authorizes embeddings on the same account (no RBAC).
var azureOpenAIClient = new AzureOpenAIClient(new Uri(azureOpenAIEndpoint), new AzureKeyCredential(azureOpenAIApiKey));
builder.Services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(
    azureOpenAIClient.GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator());

builder.Services.AddSingleton<IDocumentTextExtractor, DocumentTextExtractor>();
builder.Services.AddSingleton<DocumentChunker>();
builder.Services.AddSingleton<DocumentIngestionService>();

// Branch the document index exactly like the blob store above: use Cosmos vector search only when
// the doc-chunks container is configured (production, threaded from Bicep). Under the AppHost's
// Cosmos emulator — which can't do vector search — this key is absent, so the in-memory index is
// used and the emulator never receives a vector query (constraint 6). The container is pre-created
// in Bicep with its vector policy; CosmosDocumentIndex never creates on use (disableLocalAuth).
var docChunksContainer = builder.Configuration["Cosmos:DocChunksContainerName"];
if (!string.IsNullOrWhiteSpace(docChunksContainer))
{
    builder.Services.AddSingleton<IDocumentIndex>(sp => new CosmosDocumentIndex(
        sp.GetRequiredService<CosmosClient>(), "agent42", docChunksContainer));
}
else
{
    builder.Services.AddSingleton<IDocumentIndex, InMemoryDocumentIndex>();
}

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
//     delays the user's first token — and a retry multiplies that stall by four.
//   * Retrying is actively harmful for the fetch client: a BlockedAddressException from the SSRF guard
//     surfaces as an HttpRequestException, which the standard handler treats as transient — so a URL we
//     just refused on security grounds would be re-dialled three more times.
// A web lookup that hasn't answered within these timeouts is not worth the stall; the tools degrade by
// telling the model the lookup failed.
// EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental, but it is the only supported way
// to opt a named client out of the pipeline ConfigureHttpClientDefaults applied to it. Suppressed
// deliberately and narrowly: the alternative — inheriting retries on the SSRF-guarded fetch client — is
// a security and latency regression, not a style preference. If this API is ever removed, the fix is to
// stop applying the standard handler by default in ServiceDefaults, not to accept the retries.
#pragma warning disable EXTEXP0001
builder.Services.AddHttpClient(SearxngWebSearch.HttpClientName, http =>
{
    http.BaseAddress = new Uri(webSearchEndpoint);
    http.Timeout = TimeSpan.FromSeconds(10);
})
    .RemoveAllResilienceHandlers();

builder.Services.AddHttpClient(WebPageFetcher.HttpClientName, http =>
{
    http.Timeout = TimeSpan.FromSeconds(15);
})
    // The SSRF guard: fetch_web_page dials a URL the *model* chose, from inside a container whose
    // managed identity can mint Cosmos/Storage/OpenAI tokens at the IMDS endpoint. The check runs in
    // ConnectCallback, on the IP actually being dialled — a pre-resolve check would be TOCTOU-broken
    // and would miss redirects (ADR 0011).
    .ConfigurePrimaryHttpMessageHandler(
        () => WebPageFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable))
    .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001

builder.Services.AddSingleton<IWebSearch>(sp => new SearxngWebSearch(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(SearxngWebSearch.HttpClientName),
    sp.GetRequiredService<ILogger<SearxngWebSearch>>()));

builder.Services.AddSingleton<IWebPageFetcher>(sp => new WebPageFetcher(
    sp.GetRequiredService<IHttpClientFactory>().CreateClient(WebPageFetcher.HttpClientName)));

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

// Orleans silo: clustering comes from the Aspire Orleans resource when running
// under Aspire orchestration; localhost clustering is the fallback for a single
// standalone instance (e.g. one Container App replica with no Aspire AppHost).
// the agent state is persisted with Microsoft.Orleans.Persistence.Cosmos.
builder.UseOrleans(silo =>
{
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

// Construct the default chat client now (no network I/O) so configuration mistakes
// surface at startup instead of failing the first grain activation.
_ = app.Services.GetRequiredService<IChatClientFactory>().GetClient(null);

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

// First in the pipeline: extract the request's identity once and publish it into Orleans
// RequestContext, so it propagates to every downstream span — in-request HttpClient spans and,
// across the grain call, the in-grain chat-completion span (ADR 0010). RequestContext is an
// in-cluster channel (never an outbound HTTP header), so no user PII leaks to Azure OpenAI —
// the reason this is RequestContext and deliberately not Activity.Baggage. Runs after the server
// span has already started, which is why that span stays enriched via EnrichWithHttpRequest above.
app.Use(UserTelemetryMiddleware.InvokeAsync);

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();

app.Run();
