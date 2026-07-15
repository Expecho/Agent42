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
    /// <summary>The fully-capable model the grain tests run on unless they pick another.</summary>
    public const string DefaultModel = "fake-model";

    /// <summary>A model that supports neither tools nor vision — the degraded path of ADR 0013.</summary>
    public const string NoToolsNoVisionModel = "fake:text-only";

    /// <summary>A tools-capable model that cannot see images, so the two gates can be told apart.</summary>
    public const string NoVisionModel = "fake:blind";

    /// <summary>A model on the Responses API (ADR 0014): reaches OpenAI's hosted server-side tools.</summary>
    public const string HostedToolsModel = "fake:responses";

    /// <summary>
    /// This is the ONLY IChatClientFactory the grain tests get, so the capability predicate lives here:
    /// without it every model would report itself fully capable and the grain's degraded paths (no
    /// tools, no load_skill, images withheld) would be untestable.
    /// </summary>
    public static ModelCapabilities Capabilities(string? modelId) => modelId switch
    {
        NoToolsNoVisionModel => new ModelCapabilities(SupportsTools: false, SupportsVision: false),
        NoVisionModel => new ModelCapabilities(SupportsTools: true, SupportsVision: false),
        HostedToolsModel => ModelCapabilities.Full with { SupportsHostedTools = true },
        _ => ModelCapabilities.Full,
    };

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            services.AddSingleton<IChatClient, FakeChatClient>();
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(
                    sp.GetRequiredService<IChatClient>(),
                    DefaultModel,
                    capabilities: Capabilities));
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
