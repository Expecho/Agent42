using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.CognitiveServices;

namespace Agent42.Backend;

/// <summary>One model deployment available in the Azure AI Foundry environment.</summary>
public record ModelInfo(string Deployment, string? Model, string? Version);

/// <summary>The deployments the picker can choose from, plus the current default.</summary>
public record ModelCatalogResult(string Default, IReadOnlyList<ModelInfo> Models);

/// <summary>Lists the model deployments available to the agent, queried live.</summary>
public interface IModelCatalog
{
    Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Live-queries the configured Azure AI Foundry (Cognitive Services) account for its
/// chat-capable model deployments via the Azure Resource Manager SDK. There is no caching:
/// each call reflects the deployments as they exist right now, so a model added or removed
/// in the portal shows up in the picker without redeploying the app.
/// </summary>
public sealed class AzureModelCatalog(
    ArmClient armClient,
    IConfiguration configuration,
    string defaultDeployment,
    ILogger<AzureModelCatalog> logger) : IModelCatalog
{
    public async Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
    {
        var subscriptionId = configuration["AzureOpenAI:SubscriptionId"];
        var resourceGroup = configuration["AzureOpenAI:ResourceGroup"];
        var accountName = configuration["AzureOpenAI:AccountName"];

        // Without the account coordinates we can't enumerate deployments; fall back to a
        // single-entry catalog so the picker still shows the model the agent is using.
        if (string.IsNullOrWhiteSpace(subscriptionId)
            || string.IsNullOrWhiteSpace(resourceGroup)
            || string.IsNullOrWhiteSpace(accountName))
        {
            logger.LogWarning(
                "AzureOpenAI:SubscriptionId/ResourceGroup/AccountName not fully configured; "
                + "the model picker will only offer the default deployment '{Default}'.", defaultDeployment);
            return DefaultOnly();
        }

        try
        {
            var accountId = CognitiveServicesAccountResource.CreateResourceIdentifier(
                subscriptionId, resourceGroup, accountName);
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

                models.Add(new ModelInfo(deployment.Data.Name, props.Model?.Name, props.Model?.Version));
            }

            if (models.Count == 0)
            {
                return DefaultOnly();
            }

            // Default first, then alphabetical, so the picker opens on the active model.
            var ordered = models
                .OrderByDescending(m => string.Equals(m.Deployment, defaultDeployment, StringComparison.OrdinalIgnoreCase))
                .ThenBy(m => m.Deployment, StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new ModelCatalogResult(defaultDeployment, ordered);
        }
        catch (Exception ex)
        {
            // A missing role assignment or transient ARM error shouldn't break chat; the UI
            // degrades to the default model. See README/deploy notes for the required RBAC.
            logger.LogWarning(ex, "Live model query failed; falling back to the default deployment '{Default}'.",
                defaultDeployment);
            return DefaultOnly();
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

    private ModelCatalogResult DefaultOnly() =>
        new(defaultDeployment, [new ModelInfo(defaultDeployment, defaultDeployment, null)]);
}

/// <summary>
/// A fixed catalog for local development (DevHost), where no Azure environment exists.
/// Lets the picker be exercised offline against the canned chat client.
/// </summary>
public sealed class StaticModelCatalog(string defaultDeployment, IReadOnlyList<ModelInfo> models) : IModelCatalog
{
    public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(new ModelCatalogResult(defaultDeployment, models));
}
