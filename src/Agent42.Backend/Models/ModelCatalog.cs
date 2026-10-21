using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;

namespace Agent42.Backend;

/// <summary>
/// One model the picker can choose. <see cref="Id"/> is the QUALIFIED id (<c>provider:model</c>) —
/// the string a conversation is pinned to — because a bare model name is no longer unique: the same
/// deployment name can exist on two Foundry accounts, and GitHub's ids are a namespace of their own.
/// </summary>
public record ModelInfo(
    string Id,
    string Provider,
    string? DisplayName,
    string? Model,
    string? Version,
    bool SupportsTools,
    bool SupportsVision,
    // Trailing default so existing constructions stay valid; true only for a declared image-generation
    // model (ADR 0020). A discovered Foundry deployment is chat, so it never sets this.
    bool SupportsImageGeneration = false,
    // Trailing default so existing constructions stay valid; true only for a declared embedding model.
    // Surfaced on /api/models so the Preferences "default embedding model" selector can filter to it.
    bool SupportsEmbeddings = false,
    // Trailing default so existing constructions stay valid; true only for a declared speech model (ADR 0038).
    // Surfaced on /api/models so the Preferences "default speech model" selector can filter to it.
    bool SupportsSpeech = false,
    // Trailing default so existing constructions stay valid; true only for a declared reasoning model
    // (ADR 0071). Surfaced on /api/models for completeness — unlike the embedding and speech flags it
    // filters no picker, because a reasoning model IS a chat model and belongs in the ordinary list.
    bool SupportsReasoning = false,
    // Trailing default so existing constructions stay valid; the human-facing name of the model's PROVIDER
    // (StoredProvider/ProviderOptions.DisplayName), surfaced on /api/models so the picker can LABEL each
    // provider optgroup with it. null → the frontend falls back to the provider key. Distinct from
    // <see cref="DisplayName"/>, which names the MODEL; this names the group the model sits under.
    string? ProviderDisplayName = null,
    // Trailing default so existing constructions stay valid; true only for a GitHub Copilot model (ADR 0108), which runs
    // only in Copilot agents. Pickers hide it everywhere else; the server-side guard is CopilotOnlyChatClient.
    bool CopilotOnly = false,
    // Trailing default so existing constructions stay valid; true only for a declared transcription (speech-to-text)
    // model (ADR 0111). Surfaced on /api/models so the "default transcription model" selector can filter to it.
    bool SupportsTranscription = false);

/// <summary>The models the picker can choose from, plus the current default.</summary>
public record ModelCatalogResult(string Default, IReadOnlyList<ModelInfo> Models);

/// <summary>Lists the models available to the agent.</summary>
public interface IModelCatalog
{
    Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Live-queries ONE Azure AI Foundry (Cognitive Services) account for its chat-capable model
/// deployments via the Azure Resource Manager SDK. There is no caching: each call reflects the
/// deployments as they exist right now, so a model added or removed in the portal shows up in the
/// picker without redeploying the app.
///
/// <para>The account coordinates are constructor arguments, NOT read from IConfiguration — there is
/// one instance of this per discovering Foundry provider (ADR 0013), and reading three global
/// <c>AzureOpenAI:*</c> keys would make a second Foundry account enumerate the FIRST account's
/// deployments and label them as its own: silently wrong rather than a failure.</para>
/// </summary>
public sealed class AzureModelCatalog(
    ArmClient armClient,
    string providerKey,
    string? providerDisplayName,
    FoundrySettings settings,
    string defaultModelId,
    ILogger<AzureModelCatalog> logger) : IModelCatalog
{
    public async Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
    {
        // Without the account coordinates we can't enumerate deployments; contribute nothing and let
        // the composite fall back — it always offers the default model.
        if (string.IsNullOrWhiteSpace(settings.SubscriptionId)
            || string.IsNullOrWhiteSpace(settings.ResourceGroup)
            || string.IsNullOrWhiteSpace(settings.AccountName))
        {
            logger.LogWarning(
                "Provider '{Provider}': SubscriptionId/ResourceGroup/AccountName not fully configured "
                + "(nor inherited from AzureOpenAI:*); its models will not appear in the picker.", providerKey);
            return new ModelCatalogResult(defaultModelId, []);
        }

        try
        {
            var accountId = CognitiveServicesAccountResource.CreateResourceIdentifier(
                settings.SubscriptionId, settings.ResourceGroup, settings.AccountName);
            var account = armClient.GetCognitiveServicesAccountResource(accountId);

            var models = new List<ModelInfo>();
            await foreach (var deployment in account.GetCognitiveServicesAccountDeployments()
                .GetAllAsync(cancellationToken))
            {
                var props = deployment.Data.Properties;
                if (props is null || !IsUsableChatDeployment(props))
                {
                    continue;
                }

                // A discovered Foundry deployment is assumed fully capable — the same assumption the
                // app made before it could talk to anywhere else.
                models.Add(new ModelInfo(
                    Id: ModelId.Qualify(providerKey, deployment.Data.Name),
                    Provider: providerKey,
                    DisplayName: deployment.Data.Name,
                    Model: props.Model?.Name,
                    Version: props.Model?.Version,
                    SupportsTools: true,
                    SupportsVision: true,
                    ProviderDisplayName: providerDisplayName));
            }

            return new ModelCatalogResult(defaultModelId, models);
        }
        catch (Exception ex)
        {
            // A missing role assignment or transient ARM error shouldn't break chat; this provider
            // contributes nothing and the picker degrades to whatever the others offer.
            logger.LogWarning(ex,
                "Provider '{Provider}': live model query failed; its models will not appear in the picker.",
                providerKey);
            return new ModelCatalogResult(defaultModelId, []);
        }
    }

    // A deployment belongs in the chat picker when it advertises the chatCompletion
    // capability and has finished provisioning. Deployments that don't report capabilities
    // are included rather than hidden, since the capability map is occasionally empty.
    private static bool IsUsableChatDeployment(Azure.ResourceManager.CognitiveServices.Models.CognitiveServicesAccountDeploymentProperties props)
    {
        var succeeded = props.ProvisioningState is null
            || string.Equals(props.ProvisioningState.ToString(), "Succeeded", StringComparison.OrdinalIgnoreCase);
        if (!succeeded)
        {
            return false;
        }

        var caps = props.Capabilities;
        if (caps is null || caps.Count == 0)
        {
            return true;
        }

        return caps.TryGetValue("chatCompletion", out var chat)
            && string.Equals(chat, "true", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// The catalog the API actually serves: fans out over every configured provider (ADR 0013) —
/// discovering Foundry accounts via ARM, and OpenAI-compatible and Anthropic (ADR 0103) providers from their
/// declared model list, since those are not discovered (a deliberate scope decision, not a limitation of the shape).
///
/// <para>A provider that fails contributes nothing rather than failing the whole catalog — the
/// per-provider version of the degradation the single-account catalog already had. The default model
/// is always offered, even if every provider came back empty, so the picker is never blank.</para>
/// </summary>
public sealed class CompositeModelCatalog(
    IReadOnlyList<IModelCatalog> discovering,
    IReadOnlyList<ModelInfo> declared,
    string defaultModelId,
    ILogger<CompositeModelCatalog> logger) : IModelCatalog
{
    public async Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var models = new List<ModelInfo>(declared);

        foreach (var catalog in discovering)
        {
            try
            {
                var result = await catalog.GetAsync(cancellationToken);
                models.AddRange(result.Models);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A model provider failed to list its models; skipping it.");
            }
        }

        // Synthesize the default only when there IS one. An empty default (ADR 0019: the user has
        // configured no chat provider) must yield an EMPTY catalog, not a bogus "" model — the picker
        // stays empty and the composer gates on it.
        if (models.Count == 0 && !string.IsNullOrEmpty(defaultModelId))
        {
            models.Add(new ModelInfo(
                defaultModelId, ModelId.Parse(defaultModelId, "").Provider, defaultModelId,
                defaultModelId, null, SupportsTools: true, SupportsVision: true));
        }

        // Default first, then grouped by provider and alphabetical within it, so the picker opens on
        // the active model.
        var ordered = models
            .OrderByDescending(m => string.Equals(m.Id, defaultModelId, StringComparison.OrdinalIgnoreCase))
            .ThenBy(m => m.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(m => m.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ModelCatalogResult(defaultModelId, ordered);
    }
}

/// <summary>
/// A fixed catalog for local development (DevHost), where no Azure environment exists.
/// Lets the picker be exercised offline against the canned chat client.
/// </summary>
public sealed class StaticModelCatalog(string defaultModelId, IReadOnlyList<ModelInfo> models) : IModelCatalog
{
    public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelCatalogResult(defaultModelId, models));
}
