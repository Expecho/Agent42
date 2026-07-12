using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Configures a TestCluster silo with in-memory stand-ins for the Cosmos DB grain
/// storage and reminder service used in production, and a deterministic fake chat
/// client so agent tests run without a real model.
/// </summary>
public sealed class TestSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(sp.GetRequiredService<IChatClient>(), "fake-model"));
            // Process-global in-memory stores, shared with the endpoint DI container (constraint 10).
            services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
            // Document ingestion + retrieval stand-ins (ADR 0007): the grain resolves these to
            // search uploaded documents.
            services.AddDocumentRagFakes();
            // Web tools (ADR 0011): canned search + fetch, so the grain's tool path runs with no
            // SearXNG sidecar and no network.
            services.AddWebToolFakes();
        });
    }
}
