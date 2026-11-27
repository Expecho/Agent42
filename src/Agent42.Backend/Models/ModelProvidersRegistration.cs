using Azure.Core;
using Azure.ResourceManager;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers the model providers, the per-user resolver and factory, and the model catalogs (ADR 0013, 0018, 0019).
///
/// <para>Lifted out of <c>Program.cs</c> (cc-06), the <see cref="HindsightRegistration"/> shape. The Backend host
/// calls it; DevHost and the test silos register their own canned variants (ADR 0105), so none of this is shared.</para>
/// </summary>
public static class ModelProvidersRegistration
{
    private const string OtelSourceName = LiveTelemetryCaptureExtensions.OtelSourceName;

    /// <summary>
    /// Model providers (ADR 0013, availability floor removed by ADR 0019). The app ships NO baseline chat
    /// provider — <c>Models:Providers</c> is empty in appsettings — so chat is entirely the per-user overlay
    /// (ADR 0018): a user with none configured gets an empty picker and a gated composer, and their grain
    /// stays valid via the no-op <c>NoModelChatClient</c>. There is no operator default model; a user who picks a
    /// third-party endpoint they added still sends the session — document excerpts (ADR 0008), image SAS URLs
    /// (ADR 0004), the user's street address (ADR 0012) — outside the Entra tenant, which is why the pick is
    /// explicit per conversation. The empty baseline's Default stays "".
    ///
    /// <para>Returns the built setup because the host's startup log reports the providers that were skipped for want
    /// of a key (ADR 0013). The managed <paramref name="credential"/> drives the ARM management plane, kept for a
    /// discovering provider.</para>
    /// </summary>
    public static ModelProviderSetup AddModelProviders(
        this IServiceCollection services, IConfiguration configuration, TokenCredential credential)
    {
        var modelOptions = configuration.GetSection(ModelProvidersOptions.SectionName)
            .Get<ModelProvidersOptions>() ?? new ModelProvidersOptions();

        services.AddSingleton(new ArmClient(credential));

        // The rules — which providers are skipped, what a Foundry provider inherits, what a declared model
        // contributes — live in ModelProviderBuilder, not here: nothing in the test suite runs Program.cs.
        var models = ModelProviderBuilder.Build(modelOptions, configuration);

        // The BASELINE factory (the operator env providers). Now ships EMPTY (ADR 0019 removed the baseline
        // chat availability floor), so it hands out the no-op NoModelChatClient with DefaultModelId "". The agent
        // grain never consumes it directly — it goes through the per-user layer (ADR 0018), which uses this same
        // baseline as its input; an empty baseline means the per-user set is the ONLY source of chat providers.
        services.AddSingleton<IChatClientFactory>(sp => new MultiProviderChatClientFactory(
            models.Providers,
            modelOptions.Default,
            OtelSourceName,
            sp.GetRequiredService<ILogger<MultiProviderChatClientFactory>>(),
            attachmentInliner: sp.GetService<AttachmentInliner>()));

        // Per-user providers (ADR 0018): the resolver merges the baseline with the signed-in user's overlay,
        // and the factory provider hands the agent grain a user-scoped factory (resolved on activation, cached
        // by the overlay's version). IProviderSecretProtector is registered later (DI is lazy).
        services.AddSingleton<IUserProviderResolver>(sp => new UserProviderResolver(
            sp.GetRequiredService<IGrainFactory>(),
            sp.GetRequiredService<IProviderSecretProtector>(),
            models,
            modelOptions.Default));

        services.AddSingleton<IUserChatClientFactoryProvider>(sp => new UserChatClientFactoryProvider(
            sp.GetRequiredService<IUserProviderResolver>(),
            OtelSourceName,
            sp.GetRequiredService<ILogger<MultiProviderChatClientFactory>>(),
            // Registered only by local storage: turns an attachment reference URL into image bytes at request time.
            sp.GetService<AttachmentInliner>()));

        services.AddSingleton<IModelCatalog>(sp => new CompositeModelCatalog(
            [.. models.Discovering.Select(d => new AzureModelCatalog(
                sp.GetRequiredService<ArmClient>(), d.Key, d.DisplayName, d.Settings, modelOptions.Default,
                sp.GetRequiredService<ILogger<AzureModelCatalog>>()))],
            models.DeclaredModels,
            modelOptions.Default,
            sp.GetRequiredService<ILogger<CompositeModelCatalog>>()));

        // Per-user model catalog (ADR 0018): /api/models is user-scoped, built from the same resolver as the
        // per-user factory so an added provider's models appear in the picker without a restart.
        services.AddSingleton<IUserModelCatalogProvider>(sp => new UserModelCatalogProvider(
            sp.GetRequiredService<IUserProviderResolver>(),
            sp.GetRequiredService<ArmClient>(),
            sp.GetRequiredService<ILoggerFactory>(),
            // Optional: registered by AddCopilotAgents. Lists a connected GitHub Copilot plan's models (ADR 0108).
            sp.GetService<CopilotModelDirectory>()));

        // The Auto router (ADR 0137): a per-user Decision-1 client over the user's own Foundry resource. A port each
        // host registers (ADR 0105): DevHost registers a canned one, a test silo none (no router = no Auto).
        UserModelRouterProvider.AddModelRouter(services);

        return models;
    }
}
