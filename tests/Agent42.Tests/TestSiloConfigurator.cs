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
        });
    }
}
