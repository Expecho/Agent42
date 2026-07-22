namespace Agent42.Backend;

/// <summary>How a provider is reached: an Azure AI Foundry account, or any OpenAI-compatible endpoint.</summary>
public enum ProviderKind
{
    /// <summary>
    /// Azure AI Foundry, reached with an Azure OpenAI <b>API key</b> (ADR 0017, superseding the
    /// managed-identity credential of ADR 0013/0014). The key authenticates the Chat Completions path
    /// (the SDK's <c>api-key</c> header) and the Responses <c>/openai/v1</c> path (a custom
    /// <c>api-key</c>-header policy). It is the availability path, so a blank key is fatal at startup.
    /// </summary>
    AzureOpenAI,

    /// <summary>Any OpenAI-compatible endpoint reached with an API key — GitHub Models, Ollama, vLLM.</summary>
    OpenAICompatible,
}

/// <summary>
/// One model a provider offers, declared in config. Declared by <see cref="ProviderKind.OpenAICompatible"/>
/// providers, and by a <see cref="ProviderKind.AzureOpenAI"/> (Foundry) provider that has discovery off
/// (ADR 0017) — a key-based Foundry provider cannot ARM-discover, so it declares its deployments here.
/// </summary>
public sealed class ConfiguredModel
{
    /// <summary>The provider's own model string, unqualified — e.g. <c>openai/gpt-4o-mini</c> or <c>gpt-5.4</c>.</summary>
    public string Id { get; set; } = "";

    public string? DisplayName { get; set; }

    /// <summary>
    /// Whether the model honours function calling. NOT a per-provider fact: on GitHub Models the
    /// OpenAI-published models call tools, while <c>microsoft/phi-4</c> rejects a tools request
    /// outright and <c>deepseek/DeepSeek-V3-0324</c> silently never calls one (ADR 0013 spike).
    /// </summary>
    public bool SupportsTools { get; set; }

    /// <summary>
    /// Whether the model can read an image supplied as a URL. Load-bearing beyond a failed turn:
    /// our images reach the model as a SAS URL into a PRIVATE blob container (ADR 0004), and a
    /// vision-capable third-party model really does fetch it (ADR 0013 spike) — so a wrong
    /// <c>true</c> here hands that URL to an endpoint outside the tenant for nothing.
    /// </summary>
    public bool SupportsVision { get; set; }

    /// <summary>
    /// Whether this is an <b>image-generation</b> model (ADR 0020): picking it makes a turn produce an
    /// image (text-to-image, or image-to-image when the turn/conversation has an input image) via an
    /// OpenAI-compatible <c>/v1/images</c> endpoint, instead of a chat reply. Default <c>false</c> — a
    /// declared model is a chat model unless it says otherwise, and a discovered Foundry deployment is chat.
    /// Egress note (ADR 0013): generating sends the prompt — and for edits the input image bytes — outside
    /// the tenant, so this is opt-in per model.
    /// </summary>
    public bool SupportsImageGeneration { get; set; }

    /// <summary>
    /// Whether this is an <b>embedding</b> model: the user picks it as the "default embedding model" in
    /// Preferences and document RAG (ADR 0007) resolves its generator from the model's provider (endpoint +
    /// key), reached via <c>GetEmbeddingClient</c> — a distinct model kind from chat. Default <c>false</c>.
    /// The embedding model must produce vectors matching the <c>doc-chunks</c> container dimension (3072);
    /// a mismatch is rejected at ingest.
    /// </summary>
    public bool SupportsEmbeddings { get; set; }
}

/// <summary>One configured provider.</summary>
public sealed class ProviderOptions
{
    public ProviderKind Kind { get; set; }

    /// <summary>Display name for the picker's group heading; falls back to the provider key.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// For <see cref="ProviderKind.OpenAICompatible"/>: the inference endpoint. For
    /// <see cref="ProviderKind.AzureOpenAI"/>: leave EMPTY — it resolves from <c>AzureOpenAI:Endpoint</c>,
    /// which Bicep threads in and the embedding client also reads. See <see cref="FoundrySettings"/>.
    /// </summary>
    public string? Endpoint { get; set; }

    /// <summary>
    /// A SECRET; never committed — it arrives as <c>Models__Providers__&lt;key&gt;__ApiKey</c>.
    /// <para>For <see cref="ProviderKind.OpenAICompatible"/> providers this is the endpoint's API key, and
    /// blank is a SUPPORTED state: the provider is skipped with a warning and the app boots on the others
    /// (ADR 0012's degrade precedent, not ADR 0011's fail-the-deploy one — an expired token must not take
    /// the app down).</para>
    /// <para>For <see cref="ProviderKind.AzureOpenAI"/> (Foundry) providers this is a provider-level
    /// OVERRIDE of the Azure OpenAI key (ADR 0017); left blank it inherits <c>AzureOpenAI:ApiKey</c> — see
    /// <see cref="ProviderSettings.ResolveFoundry"/>. Unlike a third-party key, a Foundry key on the
    /// availability path that resolves blank is FATAL, not skipped (<see cref="ModelProviderBuilder"/>).</para>
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>Live-query this provider's models (Foundry/ARM only); otherwise <see cref="Models"/> is the list.</summary>
    public bool Discover { get; set; }

    /// <summary>ARM coordinates for a discovering Foundry provider. Empty → inherit the <c>AzureOpenAI:*</c> keys.</summary>
    public string? SubscriptionId { get; set; }

    /// <summary>ARM coordinates for a discovering Foundry provider. Empty → inherit the <c>AzureOpenAI:*</c> keys.</summary>
    public string? ResourceGroup { get; set; }

    /// <summary>ARM coordinates for a discovering Foundry provider. Empty → inherit the <c>AzureOpenAI:*</c> keys.</summary>
    public string? AccountName { get; set; }

    public List<ConfiguredModel> Models { get; set; } = [];

    /// <summary>
    /// When set on a <see cref="ProviderKind.AzureOpenAI"/> (Foundry) provider, EVERY model of that
    /// provider is reached through the OpenAI <b>Responses</b> API run <b>statelessly</b>
    /// (<c>store=false</c>) instead of Chat Completions — ADR 0014. Provider-level and all-or-nothing:
    /// Foundry models are ARM-discovered, not declared, so there is no per-model seam to hang it on.
    /// Reached with no package change — a stable <c>OpenAIClient</c> pointed at <c>{endpoint}/openai/v1</c>
    /// with an <c>api-key</c>-header policy carrying the Azure OpenAI key (ADR 0017, superseding ADR 0014's
    /// managed-identity bearer policy) — and stateless is mandatory, never overridable, because the
    /// grain holds all history client-side (ADR 0004). Ignored on <see cref="ProviderKind.OpenAICompatible"/>
    /// providers (GitHub Models does not support Responses). Ships <b>off</b> in prod; turning it on is a
    /// separate, deliberate change.
    /// </summary>
    public bool UseResponsesApi { get; set; }
}

/// <summary>The <c>Models:</c> config section (ADR 0013).</summary>
public sealed class ModelProvidersOptions
{
    public const string SectionName = "Models";

    /// <summary>The qualified id a conversation starts on. Stays a Foundry model: a third-party model is opt-in.</summary>
    public string Default { get; set; } = "";

    public Dictionary<string, ProviderOptions> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>The endpoint, API key, and ARM coordinates a Foundry provider actually uses, after inheritance.</summary>
public sealed record FoundrySettings(
    string? Endpoint, string? ApiKey, string? SubscriptionId, string? ResourceGroup, string? AccountName);

/// <summary>
/// Qualified model ids: <c>provider:model</c>.
///
/// <para>The separator is a colon and NOT a slash, and the split is on the FIRST colon, because a
/// provider's own model string may contain a slash — GitHub Models ids are <c>publisher/name</c>
/// (<c>openai/gpt-4o-mini</c>). Everything after the first colon is the provider's opaque model
/// string and is never parsed further.</para>
/// </summary>
public static class ModelId
{
    /// <summary>
    /// Splits a qualified id. An id with no colon is a LEGACY id (every conversation persisted before
    /// ADR 0013 holds one) and resolves against <paramref name="defaultProvider"/> — which is what
    /// makes this change need no state migration.
    /// </summary>
    public static (string Provider, string Model) Parse(string id, string defaultProvider)
    {
        var separator = id.IndexOf(':');

        // separator == 0 means the id has no provider part (":gpt-4o"); treat it as legacy too,
        // rather than inventing a provider named "".
        return separator <= 0
            ? (defaultProvider, id)
            : (id[..separator], id[(separator + 1)..]);
    }

    public static string Qualify(string provider, string model) => $"{provider}:{model}";
}

/// <summary>
/// Resolves what a Foundry provider did not declare from the top-level <c>AzureOpenAI:*</c> keys.
///
/// <para>This inheritance is deliberate and load-bearing: Bicep threads <c>AzureOpenAI__Endpoint</c>
/// as an env var and the embedding client (out of scope for multi-provider) reads the same key, so a
/// second copy of the endpoint under <c>Models:</c> would be a second source of truth — and prod
/// would silently talk to whichever one appsettings won. Declaring all four fields on a provider is
/// therefore how a SECOND Foundry account is added, with no code change.</para>
///
/// <para>Used by BOTH the factory and the catalog; there is exactly one copy of this rule.</para>
/// </summary>
public static class ProviderSettings
{
    public static FoundrySettings ResolveFoundry(ProviderOptions provider, IConfiguration configuration) =>
        new(
            Inherit(provider.Endpoint, configuration["AzureOpenAI:Endpoint"]),
            // The Azure OpenAI key (ADR 0017): a provider-level ApiKey overrides, else the top-level
            // AzureOpenAI:ApiKey — the SAME single source the embedding client reads, so there is never a
            // second copy of the key (the endpoint-inheritance invariant, extended to the key).
            Inherit(provider.ApiKey, configuration["AzureOpenAI:ApiKey"]),
            Inherit(provider.SubscriptionId, configuration["AzureOpenAI:SubscriptionId"]),
            Inherit(provider.ResourceGroup, configuration["AzureOpenAI:ResourceGroup"]),
            Inherit(provider.AccountName, configuration["AzureOpenAI:AccountName"]));

    private static string? Inherit(string? declared, string? inherited) =>
        string.IsNullOrWhiteSpace(declared) ? inherited : declared;
}
