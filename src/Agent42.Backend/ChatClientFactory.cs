using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Agent42.Agents;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace Agent42.Backend;

/// <summary>
/// Builds OTel-instrumented <see cref="IChatClient"/>s across every configured provider (ADR 0013),
/// one per qualified <c>provider:model</c> id. Clients are cached, so switching a conversation
/// between models doesn't rebuild the underlying HTTP pipeline each turn.
///
/// <para>Every provider is now reached with an <b>API key</b> (ADR 0017, superseding ADR 0013/0014's
/// managed identity for Foundry): a Foundry provider with an Azure OpenAI key, and any OpenAI-compatible
/// provider — GitHub Models, Ollama, vLLM — with its endpoint's key. What still differs is the auth
/// HEADER: the Azure SDK sends Azure's <c>api-key</c> header on the Chat path, while the Responses
/// <c>/openai/v1</c> path needs a custom <c>api-key</c>-header policy because the OpenAI SDK's default is
/// <c>Authorization: Bearer</c> (Step-1 spike, ADR 0017).</para>
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
        // An empty provider set is now a supported state, not a misconfiguration (ADR 0019): a user who
        // has configured no chat provider gets an empty factory whose DefaultModelId is "" and whose
        // GetClient hands out the no-op NoModelChatClient. The grain guards on DefaultModelId before ever
        // running inference, so the no-op is never actually invoked. `_defaultProvider` is only read by
        // Resolve, which the empty-guards below short-circuit past — so a "" default provider is harmless.
        _providers = providers;
        DefaultModelId = defaultModelId;
        _defaultProvider = providers.Count == 0
            ? ""
            : ModelId.Parse(defaultModelId, providers.Keys.First()).Provider;
        _otelSourceName = otelSourceName;
        _logger = logger;
    }

    public string DefaultModelId { get; }

    public IChatClient GetClient(string? modelId) =>
        _providers.Count == 0 ? NoModelChatClient.Instance : _clients.GetOrAdd(Resolve(modelId).QualifiedId, Build);

    public ModelCapabilities GetCapabilities(string? modelId)
    {
        // No providers configured (ADR 0019): nothing is capable of anything. Return before Resolve,
        // which assumes at least one provider exists (it dereferences _providers[defaultProviderKey]).
        if (_providers.Count == 0)
        {
            return new ModelCapabilities(SupportsTools: false, SupportsVision: false);
        }

        var (provider, model, _) = Resolve(modelId);

        // A Foundry deployment is discovered, not declared, so nothing lists its capabilities —
        // assume the full Azure OpenAI feature set, which is what the app assumed before ADR 0013.
        // An OpenAI-compatible model is only as capable as its config entry says: an id that is not
        // in the list at all gets NOTHING, because guessing "true" here is the unsafe direction —
        // SupportsVision: true wrongly hands a third party a SAS URL into our private container.
        if (provider.Options.Kind == ProviderKind.AzureOpenAI)
        {
            // Hosted server-side tools (web search, code interpreter) ride the Responses API only —
            // Chat Completions 400s on them (ADR 0011/0014) — so a Foundry provider advertises them
            // exactly when it runs Responses. Tools/vision stay Full either way (they are orthogonal
            // to the wire API); only the hosted-tools bit tracks the Responses switch.
            return ModelCapabilities.Full with { SupportsHostedTools = Provider.UseResponses(provider.Options) };
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

        /// <summary>
        /// Whether this provider's models are built through the OpenAI <b>Responses</b> API (ADR 0014)
        /// instead of Chat Completions. Pure and <b>public</b> on purpose: a unit test asserts WHICH path
        /// a flagged provider takes without a network call (two distinct qualified ids are always distinct
        /// clients regardless of API, so a caching assertion alone cannot prove the branch fired). Responses
        /// is Foundry-only — an OpenAI-compatible provider ignores the flag (GitHub Models has no Responses
        /// surface), so the <see cref="ProviderKind"/> gate is part of the decision, not just the caller's.
        /// </summary>
        public static bool UseResponses(ProviderOptions options) =>
            options.Kind == ProviderKind.AzureOpenAI && options.UseResponsesApi;

        /// <summary>An Azure AI Foundry provider, reached with an Azure OpenAI API key (ADR 0017).</summary>
        public static Provider Foundry(ProviderOptions options, Uri endpoint, string apiKey)
        {
            if (UseResponses(options))
            {
                // ADR 0014/0017: reach Foundry's GA Responses surface on the STABLE OpenAI client via the
                // OpenAI-compatible /openai/v1 base URL. Auth is the Azure OpenAI resource KEY, but the
                // OpenAI SDK's ApiKeyCredential would send `Authorization: Bearer <key>`, whereas Azure's
                // /openai/v1 authenticates the resource key through the `api-key` REQUEST HEADER — so a
                // custom AuthenticationPolicy sets that header instead (Step-1 spike, 2026-07-16: correct
                // key past auth on both header positions, wrong key 401; `api-key` is Azure's documented
                // mechanism). No named HttpClient — its AddServiceDefaults retry pipeline would multiply the
                // stall on a non-reentrant AgentGrain turn (see gotchas, and OpenAICompatible below). The
                // endpoint still comes only from config — SSRF invariant intact (ADR 0013).
                //
                // The scoped suppressions cover BOTH the OpenAIClient(AuthenticationPolicy, options) ctor
                // and GetResponsesClient() ([Experimental("OPENAI001")]) plus the stateless adapter
                // ([Experimental("MAAI001")] — a DISTINCT id; OPENAI001 alone does not build, Step-1 spike).
#pragma warning disable OPENAI001
#pragma warning disable MAAI001
                var v1 = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1");
                var responsesClient = new OpenAIClient(
                    new ApiKeyHeaderPolicy(apiKey), new OpenAIClientOptions { Endpoint = v1 });

                // Stateless is MANDATORY, not a default to override: the grain holds all history
                // client-side (ADR 0004) and reads it back via TryGetInMemoryChatHistory, which the
                // Responses stateful default (store server-side) would break. includeReasoningEncryptedContent
                // must be false — the helper defaults it true, sending include:["reasoning.encrypted_content"],
                // which non-reasoning models reject with HTTP 400; false verified on the shipped default
                // gpt-5.4 (Step-1 spike). A reasoning Foundry model would want true, but those are deferred
                // (ADR 0014, spec LOW-5).
                return new Provider(options, model => responsesClient
                    .GetResponsesClient()
                    .AsIChatClientWithStoredOutputDisabled(model, includeReasoningEncryptedContent: false));
#pragma warning restore MAAI001
#pragma warning restore OPENAI001
            }

            // Chat Completions: the Azure SDK client sends Azure's `api-key` header itself — no custom
            // policy needed (Step-1 spike: streamed a completion under the key).
            var client = new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey));
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

    /// <summary>
    /// Sets Azure's <c>api-key</c> request header on the stable <c>OpenAIClient</c> so it authenticates the
    /// Foundry <c>/openai/v1</c> Responses surface with the Azure OpenAI resource key (ADR 0017, superseding
    /// ADR 0014's managed-identity bearer bridge). The OpenAI SDK's own <see cref="ApiKeyCredential"/> would
    /// send <c>Authorization: Bearer &lt;key&gt;</c> — Azure's <c>/openai/v1</c> reads the resource key from
    /// the <c>api-key</c> header (the Step-1 spike, 2026-07-16, confirmed the key gets past auth here and a
    /// wrong key 401s). This is an <see cref="AuthenticationPolicy"/> so it slots into the
    /// <c>OpenAIClient(AuthenticationPolicy, options)</c> ctor, REPLACING the SDK's default auth rather than
    /// layering on top of it. Public for the same reason <see cref="Provider"/> is — the repo has no
    /// <c>InternalsVisibleTo</c>, so the header-setting is unit-testable (no I/O) only if reachable from the
    /// test project; the LIVE auth against <c>/openai/v1</c> is the Step-1 manual gate (ADR 0017).
    /// </summary>
    public sealed class ApiKeyHeaderPolicy(string apiKey) : AuthenticationPolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request.Headers.Set("api-key", apiKey);
            ProcessNext(message, pipeline, index);
        }

        public override async ValueTask ProcessAsync(
            PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int index)
        {
            message.Request.Headers.Set("api-key", apiKey);
            await ProcessNextAsync(message, pipeline, index);
        }
    }
}
