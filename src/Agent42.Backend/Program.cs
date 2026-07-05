using Agent42.Agents;
using Agent42.Backend;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.Azure.Cosmos;
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
        options.IsResourceCreationEnabled = true;
    });

    // Durable reminders (the schedule_prompt agent tool) persisted in Cosmos, so a
    // scheduled reminder survives silo restarts just like the agent state does.
    silo.UseCosmosReminderService(options =>
    {
        options.ConfigureCosmosClient(sp =>
            ValueTask.FromResult(sp.GetRequiredService<CosmosClient>()));
        options.DatabaseName = "agent42";
        options.ContainerName = "reminders";
        options.IsResourceCreationEnabled = true;
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
