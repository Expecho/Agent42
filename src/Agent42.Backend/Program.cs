using Agent42.Agents;
using Agent42.Backend;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.ResourceManager;
using Azure.Storage.Blobs;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.AI;
using OpenTelemetry.Trace;

const string OtelSourceName = "Agent42.Backend";

var builder = WebApplication.CreateBuilder(args);

// OTel logging, metrics and tracing (OTLP exporter) via Aspire service defaults.
builder.AddServiceDefaults();
builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(OtelSourceName));

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

var defaultDeployment = builder.Configuration["AzureOpenAI:DeploymentName"] ?? "gpt-4o";

// One credential drives both the data plane (chat inference) and the management plane
// (listing the account's model deployments), so the container app's managed identity is
// used for both.
var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
{
    TenantId = builder.Configuration["AzureOpenAI:TenantId"],
});

// Resolves an OTel-instrumented chat client per model deployment, so a conversation can
// switch between the models deployed in Azure AI Foundry.
builder.Services.AddSingleton<IChatClientFactory>(
    new AzureOpenAIChatClientFactory(
        new Uri(azureOpenAIEndpoint), credential, defaultDeployment, OtelSourceName));

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
// text-embedding-3-small deployment in the AI Foundry account, RG agent42 — the same account the
// chat client uses, so no new RBAC). Validated eagerly so a missing deployment name fails at
// startup, not on the first upload.
var embeddingDeployment = builder.Configuration["AzureOpenAI:EmbeddingDeploymentName"];
if (string.IsNullOrWhiteSpace(embeddingDeployment))
{
    throw new InvalidOperationException(
        "AzureOpenAI:EmbeddingDeploymentName is not configured. Set it in appsettings.json, " +
        "user secrets, or the AzureOpenAI__EmbeddingDeploymentName environment variable " +
        "(the name of a text-embedding-3-small deployment in the AI Foundry account).");
}

// Reuse the same endpoint + managed identity as the chat client; the identity's existing
// Cognitive Services OpenAI User grant already authorizes embeddings (no new RBAC).
var azureOpenAIClient = new AzureOpenAIClient(new Uri(azureOpenAIEndpoint), credential);
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

// Live catalog of the deployed models, queried through the Azure Resource Manager SDK.
builder.Services.AddSingleton(new ArmClient(credential));
builder.Services.AddSingleton<IModelCatalog>(sp => new AzureModelCatalog(
    sp.GetRequiredService<ArmClient>(),
    sp.GetRequiredService<IConfiguration>(),
    defaultDeployment,
    sp.GetRequiredService<ILogger<AzureModelCatalog>>()));

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

app.MapDefaultEndpoints();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();

app.Run();
