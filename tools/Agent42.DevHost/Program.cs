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
