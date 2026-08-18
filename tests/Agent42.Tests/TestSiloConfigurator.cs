using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;
using Agent42.Agents.Models;
using Agent42.Agents.Attachments;
using Agent42.Agents.ImageGeneration;
using Agent42.Agents.Mcp;
using Agent42.Agents.Push;
using Agent42.Agents.Conversations;

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
    /// An image-generation model (ADR 0020): picking it produces an image, not a chat reply. Declared
    /// tools-capable ONLY so AC9 can interleave a real tool turn before the image turn without the switch-to-
    /// image-model strip collapsing it — a real image provider declares no tools, but the id-keyed strip/restore
    /// under test is orthogonal to tool-stripping, and a preserved non-1:1 assistant structure is what makes
    /// that test non-vacuous. Not vision-capable: an image model consumes images via the edit API, not a URL.
    /// </summary>
    public const string ImageModel = "fake:image";

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
        ImageModel => new ModelCapabilities(
            SupportsTools: true, SupportsVision: false, SupportsImageGeneration: true),
        _ => ModelCapabilities.Full,
    };

    public void Configure(ISiloBuilder siloBuilder)
    {
        siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
        siloBuilder.UseInMemoryReminderService();
        siloBuilder.ConfigureServices(services =>
        {
            // Registered as its concrete type too (single instance) so a suggestion-gating test can read
            // FakeChatClient.SuggestionCalls from each silo's container — mirrors RecordingPushNotificationSender.
            services.AddSingleton<FakeChatClient>();
            services.AddSingleton<IChatClient>(sp => sp.GetRequiredService<FakeChatClient>());
            // The image generator (ADR 0020) the ImageModel resolves to: echoes the edit input back and
            // returns a large recognizable PNG for text-to-image, so a test can tell the branches apart and
            // prove the bytes never enter the session.
            services.AddSingleton<IImageGenerationService, ProbeImageGenerator>();
            services.AddSingleton<IChatClientFactory>(sp =>
                new SingleChatClientFactory(
                    sp.GetRequiredService<IChatClient>(),
                    DefaultModel,
                    capabilities: Capabilities,
                    imageGenerator: sp.GetRequiredService<IImageGenerationService>(),
                    // The deterministic embedding generator + a stub id, so the grain's document retrieval
                    // (which resolves its generator from this factory) round-trips offline (ADR 0007/0018).
                    embeddingGenerator: sp.GetRequiredService<IEmbeddingGenerator<string, Embedding<float>>>(),
                    defaultEmbeddingModelId: "offline-embed"));
            // The grain resolves its factory through the per-user seam (ADR 0018); offline/tests have no
            // overlay store, so the single fake factory stands in for every user.
            services.AddSingleton<IUserChatClientFactoryProvider>(sp =>
                new SingleUserChatClientFactoryProvider(sp.GetRequiredService<IChatClientFactory>()));
            // The grain resolves its MCP tools through the per-user seam (config-driven remote MCP servers).
            // Registered as its concrete type too, so a test can fetch it from the silo container and set
            // its Tools to exercise the mcp gate (mirrors SwitchableChatClientFactoryProvider).
            services.AddSingleton<SingleUserMcpToolProvider>();
            services.AddSingleton<IUserMcpToolProvider>(sp => sp.GetRequiredService<SingleUserMcpToolProvider>());
            // Push sender (ADR 0028): the recording stand-in, registered as its concrete type too so a
            // behavioural test can read what the async reply paths pushed. Every AgentGrain host needs a
            // sender registered or activation fails.
            services.AddSingleton<RecordingPushNotificationSender>();
            services.AddSingleton<IPushNotificationSender>(sp => sp.GetRequiredService<RecordingPushNotificationSender>());
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
