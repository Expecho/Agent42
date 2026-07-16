using Agent42.Agents;

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
        ModelProvidersOptions options, IConfiguration configuration)
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
                // Endpoint, API key and ARM coordinates fall back to the AzureOpenAI:* keys when this
                // provider doesn't declare them (the shipped `foundry` provider declares none) — one
                // source of truth, shared with Bicep's env vars and the embedding client. Declaring the
                // endpoint (+ its own key) is how a SECOND Foundry account is configured, with no code change.
                var settings = ProviderSettings.ResolveFoundry(provider, configuration);
                if (string.IsNullOrWhiteSpace(settings.Endpoint))
                {
                    throw new InvalidOperationException(
                        $"Model provider '{key}' is an AzureOpenAI provider with no endpoint: it declares "
                        + "none, and AzureOpenAI:Endpoint is not set either.");
                }

                // A blank Foundry key is FATAL, the deliberate reversal of the third-party skip below
                // (ADR 0017): Foundry is the availability path itself, so a missing key must fail at
                // startup — like the eager endpoint check and SEARXNG_SECRET's @minLength guard — rather
                // than silently degrade. This throw is what the Bicep @minLength on the key mirrors.
                if (string.IsNullOrWhiteSpace(settings.ApiKey))
                {
                    throw new InvalidOperationException(
                        $"Foundry provider '{key}' has no API key: set Models:Providers:{key}:ApiKey or "
                        + "AzureOpenAI:ApiKey (ADR 0017 — the Foundry key is the availability path).");
                }

                providers[key] = MultiProviderChatClientFactory.Provider.Foundry(
                    provider, new Uri(settings.Endpoint), settings.ApiKey);

                if (provider.Discover)
                {
                    // Carries THIS provider's coordinates. A single shared ARM catalog would make a
                    // second Foundry account enumerate the first account's deployments and label them
                    // as its own — silently wrong rather than a failure.
                    discovering.Add((key, settings));
                }
                else
                {
                    // Discovery off (ADR 0017: a data-plane key can't authenticate ARM) — so a Foundry
                    // provider contributes the deployments it DECLARES, exactly like an OpenAI-compatible
                    // one below. Without this the picker would show only the default and every other
                    // deployment would be unreachable through the UI.
                    declared.AddRange(DeclaredModels(key, provider));
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
            declared.AddRange(DeclaredModels(key, provider));
        }

        return new ModelProviderSetup(providers, discovering, declared, skipped);
    }

    // The config-declared models of a non-discovering provider, qualified under its key — used by both
    // an OpenAI-compatible provider and a non-discovering (key-based) Foundry provider (ADR 0017).
    private static IEnumerable<ModelInfo> DeclaredModels(string key, ProviderOptions provider) =>
        provider.Models.Select(m => new ModelInfo(
            Id: ModelId.Qualify(key, m.Id),
            Provider: key,
            DisplayName: m.DisplayName ?? m.Id,
            Model: m.Id,
            Version: null,
            SupportsTools: m.SupportsTools,
            SupportsVision: m.SupportsVision));
}
