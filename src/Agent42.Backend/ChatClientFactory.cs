using System.ClientModel;
using System.Collections.Concurrent;
using Agent42.Agents;
using Azure.AI.OpenAI;
using Azure.Core;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Agent42.Backend;

/// <summary>
/// Builds OTel-instrumented <see cref="IChatClient"/>s across every configured provider (ADR 0013),
/// one per qualified <c>provider:model</c> id. Clients are cached, so switching a conversation
/// between models doesn't rebuild the underlying HTTP pipeline each turn.
///
/// <para>Two credential shapes, and they are the reason this type exists: an Azure AI Foundry
/// provider is reached with a <see cref="TokenCredential"/> (the container app's managed identity),
/// and any OpenAI-compatible provider — GitHub Models, Ollama, vLLM — with an
/// <see cref="ApiKeyCredential"/> against a configured endpoint.</para>
///
/// <para>The endpoint always comes from CONFIG and never from the model or a request. That is what
/// keeps this off the SSRF path that <c>fetch_web_page</c> is on (ADR 0011), and it must stay true:
/// do not let a future change accept an endpoint from anywhere else.</para>
/// </summary>
public sealed class MultiProviderChatClientFactory : IChatClientFactory
{
    private readonly IReadOnlyDictionary<string, Provider> _providers;
    private readonly string _defaultProvider;
    private readonly string _otelSourceName;
    private readonly ILogger<MultiProviderChatClientFactory> _logger;
    private readonly ConcurrentDictionary<string, IChatClient> _clients = new();

    public MultiProviderChatClientFactory(
        IReadOnlyDictionary<string, Provider> providers,
        string defaultModelId,
        string otelSourceName,
        ILogger<MultiProviderChatClientFactory> logger)
    {
        if (providers.Count == 0)
        {
            throw new InvalidOperationException(
                "No model providers are configured. The Models:Providers section must contain at "
                + "least the Foundry provider — see appsettings.json.");
        }

        _providers = providers;
        DefaultModelId = defaultModelId;
        _defaultProvider = ModelId.Parse(defaultModelId, providers.Keys.First()).Provider;
        _otelSourceName = otelSourceName;
        _logger = logger;
    }

    public string DefaultModelId { get; }

    public IChatClient GetClient(string? modelId) => _clients.GetOrAdd(Resolve(modelId).QualifiedId, Build);

    public ModelCapabilities GetCapabilities(string? modelId)
    {
        var (provider, model, _) = Resolve(modelId);

        // A Foundry deployment is discovered, not declared, so nothing lists its capabilities —
        // assume the full Azure OpenAI feature set, which is what the app assumed before ADR 0013.
        // An OpenAI-compatible model is only as capable as its config entry says: an id that is not
        // in the list at all gets NOTHING, because guessing "true" here is the unsafe direction —
        // SupportsVision: true wrongly hands a third party a SAS URL into our private container.
        if (provider.Options.Kind == ProviderKind.AzureOpenAI)
        {
            return ModelCapabilities.Full;
        }

        var configured = provider.Options.Models
            .FirstOrDefault(m => string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));

        return configured is null
            ? new ModelCapabilities(SupportsTools: false, SupportsVision: false)
            : new ModelCapabilities(configured.SupportsTools, configured.SupportsVision);
    }

    // Maps a (possibly legacy, possibly stale) id onto a provider that actually exists.
    private (Provider Provider, string Model, string QualifiedId) Resolve(string? modelId)
    {
        var id = string.IsNullOrWhiteSpace(modelId) ? DefaultModelId : modelId;
        var (providerKey, model) = ModelId.Parse(id, _defaultProvider);

        if (_providers.TryGetValue(providerKey, out var provider))
        {
            return (provider, model, ModelId.Qualify(providerKey, model));
        }

        // The provider named by a PERSISTED id is gone from config — a provider was renamed, or its
        // API key was pulled so it was skipped at startup (see Program.cs). Fall back rather than
        // throw: a conversation must never become unopenable because of a config edit.
        _logger.LogWarning(
            "Model '{ModelId}' names provider '{Provider}', which is not configured; "
            + "falling back to the default model '{Default}'.", id, providerKey, DefaultModelId);

        var (defaultProviderKey, defaultModel) = ModelId.Parse(DefaultModelId, _defaultProvider);
        return (_providers[defaultProviderKey], defaultModel, DefaultModelId);
    }

    private IChatClient Build(string qualifiedId)
    {
        var (provider, model, _) = Resolve(qualifiedId);

        // Every client keeps the OTel wrapper, whichever provider it belongs to — a third-party model
        // is exactly the one you want traced.
        return provider.Client(model)
            .AsBuilder()
            .UseOpenTelemetry(sourceName: _otelSourceName, configure: cfg => cfg.EnableSensitiveData = true)
            .Build();
    }

    /// <summary>One configured provider: its options plus the underlying SDK client it hands out models from.</summary>
    public sealed class Provider(ProviderOptions options, Func<string, IChatClient> client)
    {
        public ProviderOptions Options { get; } = options;

        public Func<string, IChatClient> Client { get; } = client;

        /// <summary>An Azure AI Foundry provider, reached with the app's managed identity.</summary>
        public static Provider Foundry(ProviderOptions options, Uri endpoint, TokenCredential credential)
        {
            var client = new AzureOpenAIClient(endpoint, credential);
            return new Provider(options, model => client.GetChatClient(model).AsIChatClient());
        }

        /// <summary>
        /// Any OpenAI-compatible provider, reached with an API key. The transport is deliberately the
        /// SDK's own default and NOT an IHttpClientFactory client: a named HttpClient silently inherits
        /// AddServiceDefaults' 3-retry resilience pipeline (see gotchas), and AgentGrain is non-reentrant,
        /// so a retried inference call would multiply the stall on the user's turn. If you ever set
        /// OpenAIClientOptions.Transport from IHttpClientFactory, call RemoveAllResilienceHandlers first.
        /// </summary>
        public static Provider OpenAICompatible(ProviderOptions options, Uri endpoint, string apiKey)
        {
            var client = new OpenAIClient(
                new ApiKeyCredential(apiKey), new OpenAIClientOptions { Endpoint = endpoint });
            return new Provider(options, model => client.GetChatClient(model).AsIChatClient());
        }
    }
}
