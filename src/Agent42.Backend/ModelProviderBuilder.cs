using Agent42.Agents;
using Azure.Core;

namespace Agent42.Backend;

/// <summary>What the <c>Models:</c> section turns into: the clients, the catalogs, and what was dropped.</summary>
public sealed record ModelProviderSetup(
    IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> Providers,
    IReadOnlyList<(string Key, FoundrySettings Settings)> Discovering,
    IReadOnlyList<ModelInfo> DeclaredModels,
    IReadOnlyList<string> SkippedProviders);

/// <summary>
/// Turns the bound <c>Models:</c> options into the provider clients and catalog inputs (ADR 0013).
///
/// <para>This lives outside <c>Program.cs</c> on purpose: nothing in the test suite runs Program, so
/// leaving the rules here — which providers are skipped, what a Foundry provider inherits, what a
/// declared model contributes — inline in startup would make them untestable, and they are exactly the
/// rules worth pinning.</para>
/// </summary>
public static class ModelProviderBuilder
{
    public static ModelProviderSetup Build(
        ModelProvidersOptions options, IConfiguration configuration, TokenCredential credential)
    {
        var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>(
            StringComparer.OrdinalIgnoreCase);
        var discovering = new List<(string Key, FoundrySettings Settings)>();
        var declared = new List<ModelInfo>();
        var skipped = new List<string>();

        foreach (var (key, provider) in options.Providers)
        {
            if (provider.Kind == ProviderKind.AzureOpenAI)
            {
                // Endpoint and ARM coordinates fall back to the AzureOpenAI:* keys when this provider
                // doesn't declare them (the shipped `foundry` provider declares none) — one source of
                // truth, shared with Bicep's env vars and the embedding client. Declaring all four is
                // how a SECOND Foundry account is configured, with no code change.
                var settings = ProviderSettings.ResolveFoundry(provider, configuration);
                if (string.IsNullOrWhiteSpace(settings.Endpoint))
                {
                    throw new InvalidOperationException(
                        $"Model provider '{key}' is an AzureOpenAI provider with no endpoint: it declares "
                        + "none, and AzureOpenAI:Endpoint is not set either.");
                }

                providers[key] = MultiProviderChatClientFactory.Provider.Foundry(
                    provider, new Uri(settings.Endpoint), credential);

                if (provider.Discover)
                {
                    // Carries THIS provider's coordinates. A single shared ARM catalog would make a
                    // second Foundry account enumerate the first account's deployments and label them
                    // as its own — silently wrong rather than a failure.
                    discovering.Add((key, settings));
                }

                continue;
            }

            // An OpenAI-compatible provider with no API key is SKIPPED, not fatal — contrast
            // SEARXNG_SECRET, which fails the deploy because a crash-looping sidecar takes the backend
            // down with it. A GitHub token that expires must never take the app down: Foundry is the
            // availability path, and a third-party provider is a convenience on top of it.
            if (string.IsNullOrWhiteSpace(provider.ApiKey))
            {
                skipped.Add(key);
                continue;
            }

            if (string.IsNullOrWhiteSpace(provider.Endpoint))
            {
                throw new InvalidOperationException(
                    $"Model provider '{key}' is an OpenAICompatible provider and must declare an Endpoint.");
            }

            providers[key] = MultiProviderChatClientFactory.Provider.OpenAICompatible(
                provider, new Uri(provider.Endpoint), provider.ApiKey);

            // OpenAI-compatible providers are not discovered (a scope decision): their models are the
            // ones declared in config, with the capability flags declared alongside them.
            declared.AddRange(provider.Models.Select(m => new ModelInfo(
                Id: ModelId.Qualify(key, m.Id),
                Provider: key,
                DisplayName: m.DisplayName ?? m.Id,
                Model: m.Id,
                Version: null,
                SupportsTools: m.SupportsTools,
                SupportsVision: m.SupportsVision)));
        }

        return new ModelProviderSetup(providers, discovering, declared, skipped);
    }
}
