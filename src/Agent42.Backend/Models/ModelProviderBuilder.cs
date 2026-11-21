using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>What the <c>Models:</c> section turns into: the clients, the catalogs, and what was dropped.</summary>
public sealed record ModelProviderSetup(
    IReadOnlyDictionary<string, MultiProviderChatClientFactory.Provider> Providers,
    IReadOnlyList<(string Key, string? DisplayName, FoundrySettings Settings)> Discovering,
    IReadOnlyList<ModelInfo> DeclaredModels,
    IReadOnlyList<string> SkippedProviders);

/// <summary>How one provider's build turned out.</summary>
public enum ProviderBuildStatus
{
    /// <summary>Built into a usable <see cref="MultiProviderChatClientFactory.Provider"/>.</summary>
    Built,

    /// <summary>Deliberately skipped — an OpenAI-compatible or Anthropic provider with no API key. Not an error.</summary>
    Skipped,

    /// <summary>
    /// Invalid config (a Foundry provider with no endpoint or no key; an OpenAI-compatible or Anthropic
    /// provider with no endpoint). <b>Fatal for the operator baseline</b> (<see cref="Build"/> throws — the
    /// blank-Foundry-key rule, ADR 0017), but a <b>per-user overlay override falls back to the baseline
    /// entry</b> instead of throwing (ADR 0018). <see cref="ProviderBuildResult.Error"/> carries why.
    /// </summary>
    Invalid,
}

/// <summary>The outcome of building one provider entry (ADR 0018 — the per-provider seam the resolver reuses).</summary>
public sealed record ProviderBuildResult(
    ProviderBuildStatus Status,
    MultiProviderChatClientFactory.Provider? Provider = null,
    (string Key, string? DisplayName, FoundrySettings Settings)? Discovering = null,
    IReadOnlyList<ModelInfo>? DeclaredModels = null,
    string? Error = null);

/// <summary>
/// Turns the bound <c>Models:</c> options into the provider clients and catalog inputs (ADR 0013).
///
/// <para>This lives outside <c>Program.cs</c> on purpose: nothing in the test suite runs Program, so
/// leaving the rules here — which providers are skipped, what a Foundry provider inherits, what a
/// declared model contributes — inline in startup would make them untestable, and they are exactly the
/// rules worth pinning.</para>
///
/// <para><see cref="TryBuildProvider"/> is the <b>single</b> home of those rules, per provider. The
/// startup baseline (<see cref="Build"/>) and the per-user resolver (<c>UserProviderResolver</c>,
/// ADR 0018) both call it, so the Foundry inheritance / skip / fatal logic is never duplicated
/// (the single-source-of-truth gotcha). The difference between them is only what they do with an
/// <see cref="ProviderBuildStatus.Invalid"/> result: the baseline throws, the overlay falls back.</para>
/// </summary>
public static class ModelProviderBuilder
{
    public static ModelProviderSetup Build(
        ModelProvidersOptions options, IConfiguration configuration)
    {
        var providers = new Dictionary<string, MultiProviderChatClientFactory.Provider>(
            StringComparer.OrdinalIgnoreCase);
        var discovering = new List<(string Key, string? DisplayName, FoundrySettings Settings)>();
        var declared = new List<ModelInfo>();
        var skipped = new List<string>();

        foreach (var (key, provider) in options.Providers)
        {
            var result = TryBuildProvider(key, provider, configuration);
            switch (result.Status)
            {
                case ProviderBuildStatus.Invalid:
                    // The baseline is the availability path (ADR 0017), so an invalid provider is FATAL
                    // here — the blank-Foundry-key throw and the missing-endpoint throw both surface as
                    // this. A per-user overlay does not reach this branch: it catches Invalid and keeps
                    // the baseline entry (ADR 0018).
                    throw new InvalidOperationException(result.Error);

                case ProviderBuildStatus.Skipped:
                    skipped.Add(key);
                    break;

                default:
                    providers[key] = result.Provider!;
                    if (result.Discovering is { } d)
                    {
                        discovering.Add(d);
                    }

                    if (result.DeclaredModels is { Count: > 0 } dm)
                    {
                        declared.AddRange(dm);
                    }

                    break;
            }
        }

        return new ModelProviderSetup(providers, discovering, declared, skipped);
    }

    /// <summary>
    /// Builds one provider entry, applying the Foundry inheritance / skip / fatal rules exactly once
    /// (ADR 0013/0017). Never throws: an invalid provider comes back as
    /// <see cref="ProviderBuildStatus.Invalid"/> with a message, so the caller chooses whether that is
    /// fatal (baseline) or a fall-back (overlay, ADR 0018).
    /// </summary>
    public static ProviderBuildResult TryBuildProvider(
        string key, ProviderOptions provider, IConfiguration configuration)
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
                return Invalid(
                    $"Model provider '{key}' is an AzureOpenAI provider with no endpoint: it declares "
                    + "none, and AzureOpenAI:Endpoint is not set either.");
            }

            // A blank Foundry key is FATAL for the baseline, the deliberate reversal of the third-party
            // skip below (ADR 0017): Foundry is the availability path itself, so a missing key must fail
            // at startup — like the eager endpoint check and SEARXNG_SECRET's @minLength guard — rather
            // than silently degrade. This Invalid is what the Bicep @minLength on the key mirrors. (A
            // per-user overlay treats the same Invalid as a fall-back to the baseline, not a throw.)
            if (string.IsNullOrWhiteSpace(settings.ApiKey))
            {
                return Invalid(
                    $"Foundry provider '{key}' has no API key: set Models:Providers:{key}:ApiKey or "
                    + "AzureOpenAI:ApiKey (ADR 0017 — the Foundry key is the availability path).");
            }

            var foundry = MultiProviderChatClientFactory.Provider.Foundry(
                provider, new Uri(settings.Endpoint), settings.ApiKey);

            // A discovering provider carries THIS provider's coordinates. A single shared ARM catalog
            // would make a second Foundry account enumerate the first account's deployments and label
            // them as its own — silently wrong rather than a failure. Discovery off (ADR 0017: a
            // data-plane key can't authenticate ARM) → the provider contributes the deployments it
            // DECLARES, exactly like an OpenAI-compatible one below.
            return provider.Discover
                ? new ProviderBuildResult(ProviderBuildStatus.Built, foundry, (key, provider.DisplayName, settings), [])
                : new ProviderBuildResult(
                    ProviderBuildStatus.Built, foundry, null, [.. DeclaredModels(key, provider)]);
        }

        return TryBuildDeclaredProvider(key, provider);
    }

    // An OpenAI-compatible or Anthropic provider (ADR 0103): keyed, endpoint-required, models declared. These kinds
    // share every rule here and differ only in which client they build.
    private static ProviderBuildResult TryBuildDeclaredProvider(string key, ProviderOptions provider)
    {
        // A provider with no API key is SKIPPED, not fatal — contrast
        // SEARXNG_SECRET, which fails the deploy because a crash-looping sidecar takes the backend
        // down with it. A GitHub token that expires must never take the app down: Foundry is the
        // availability path, and a third-party provider is a convenience on top of it.
        if (string.IsNullOrWhiteSpace(provider.ApiKey))
        {
            return new ProviderBuildResult(ProviderBuildStatus.Skipped);
        }

        // GitHub Copilot (ADR 0108) has no endpoint and declares no models (they are discovered from the user's plan
        // by the Copilot model directory), so it is branched off before the endpoint rule.
        if (provider.Kind == ProviderKind.GitHubCopilot)
        {
            return new ProviderBuildResult(
                ProviderBuildStatus.Built, MultiProviderChatClientFactory.Provider.GitHubCopilot(provider, provider.ApiKey), null, []);
        }

        if (string.IsNullOrWhiteSpace(provider.Endpoint))
        {
            return Invalid(
                $"Model provider '{key}' is an {provider.Kind} provider and must declare an Endpoint.");
        }

        // A Decision-1 router (ADR 0137) declares exactly one "model": its deployment name. It contributes NO catalog model
        // (the `[]`, copied from Copilot), so it never reaches the picker, a default, or an embedding/speech selector.
        if (provider.Kind == ProviderKind.DecisionModel)
        {
            return provider.Models.Count != 1 || string.IsNullOrWhiteSpace(provider.Models[0].Id)
                ? Invalid($"Model provider '{key}' is a DecisionModel provider and must declare exactly one model (the deployment name).")
                : new ProviderBuildResult(
                    ProviderBuildStatus.Built,
                    MultiProviderChatClientFactory.Provider.DecisionModel(
                        provider, new DecisionProviderSettings(provider.Endpoint, provider.ApiKey, provider.Models[0].Id.Trim())),
                    null, []);
        }

        // An explicit Anthropic branch: before ADR 0103 every non-Foundry kind fell through to the OpenAI client.
        var built = provider.Kind == ProviderKind.Anthropic
            ? MultiProviderChatClientFactory.Provider.Anthropic(provider, new Uri(provider.Endpoint), provider.ApiKey)
            : MultiProviderChatClientFactory.Provider.OpenAICompatible(
                provider, new Uri(provider.Endpoint), provider.ApiKey);

        // These providers are not discovered (a scope decision): their models are the
        // ones declared in config, with the capability flags declared alongside them.
        return new ProviderBuildResult(
            ProviderBuildStatus.Built, built, null, [.. DeclaredModels(key, provider)]);
    }

    private static ProviderBuildResult Invalid(string error) =>
        new(ProviderBuildStatus.Invalid, Error: error);

    // The config-declared models of a non-discovering provider, qualified under its key — used by an
    // OpenAI-compatible or Anthropic provider and a non-discovering (key-based) Foundry provider (ADR 0017).
    private static IEnumerable<ModelInfo> DeclaredModels(string key, ProviderOptions provider) =>
        provider.Models.Select(m => new ModelInfo(
            Id: ModelId.Qualify(key, m.Id),
            Provider: key,
            DisplayName: m.DisplayName ?? m.Id,
            Model: m.Id,
            Version: null,
            SupportsTools: m.SupportsTools,
            SupportsVision: m.SupportsVision,
            SupportsImageGeneration: m.SupportsImageGeneration,
            SupportsEmbeddings: m.SupportsEmbeddings,
            SupportsSpeech: m.SupportsSpeech,
            SupportsReasoning: m.SupportsReasoning,
            ProviderDisplayName: provider.DisplayName,
            SupportsTranscription: m.SupportsTranscription,
            Hint: m.Hint));
}
