using System.ClientModel;
using System.ClientModel.Primitives;
using System.Collections.Concurrent;
using Agent42.Agents;
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
/// provider — GitHub Models, Ollama, vLLM — with its endpoint's key, and Anthropic (Claude, ADR 0103) through its
/// own SDK with the user's Anthropic key. Both Foundry chat wire paths —
/// Chat Completions and Responses — run over Foundry's GA <c>/openai/v1</c> surface (no <c>api-version</c>)
/// with a custom <c>api-key</c>-header policy, because the OpenAI SDK's default is
/// <c>Authorization: Bearer</c> (ADR 0017). The Chat path used to go through <c>AzureOpenAIClient</c>'s
/// classic <c>api-version</c> data plane, but the stable SDK caps that query at 2024-10-21, which newer
/// Foundry deployments reject. Embeddings moved off it too, onto <c>/openai/v1</c> at the resource root.</para>
///
/// <para>The endpoint always comes from CONFIG and never from the model or a request. That is what
/// keeps this off the SSRF path that <c>fetch_web_page</c> is on (ADR 0011), and it must stay true:
/// do not let a future change accept an endpoint from anywhere else.</para>
/// </summary>
public sealed class MultiProviderChatClientFactory : IChatClientFactory, ICopilotProviderSource
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
        ILogger<MultiProviderChatClientFactory> logger,
        string defaultEmbeddingModelId = "",
        string defaultSpeechModelId = "")
    {
        // An empty provider set is now a supported state, not a misconfiguration (ADR 0019): a user who
        // has configured no chat provider gets an empty factory whose DefaultModelId is "" and whose
        // GetClient hands out the no-op NoModelChatClient. The grain guards on DefaultModelId before ever
        // running inference, so the no-op is never actually invoked. `_defaultProvider` is only read by
        // Resolve, which the empty-guards below short-circuit past — so a "" default provider is harmless.
        _providers = providers;
        DefaultModelId = defaultModelId;
        // "" when no embedding model is configured (the resolver only sets a non-empty id when it names a
        // declared embedding-capable model). Callers gate on this before embedding — see GetEmbeddingGenerator.
        DefaultEmbeddingModelId = defaultEmbeddingModelId;
        // "" when no speech model is configured (resolver sets a non-empty id only for a declared speech model,
        // ADR 0038). The /api/speech endpoint gates on GetSpeechService() being non-null — see GetSpeechService.
        DefaultSpeechModelId = defaultSpeechModelId;
        _defaultProvider = providers.Count == 0
            ? ""
            : ModelId.Parse(defaultModelId, providers.Keys.First()).Provider;
        _otelSourceName = otelSourceName;
        _logger = logger;
    }

    public string DefaultModelId { get; }

    public string DefaultEmbeddingModelId { get; }

    public string DefaultSpeechModelId { get; }

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
        // An OpenAI-compatible or Anthropic model is only as capable as its config entry says (Anthropic never
        // advertises hosted tools, ADR 0103): an id that is not
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
            : new ModelCapabilities(
                configured.SupportsTools, configured.SupportsVision,
                SupportsImageGeneration: configured.SupportsImageGeneration);
    }

    public IImageGenerationService? GetImageGenerationService(string? modelId)
    {
        // No providers, or the resolved model is not an image model → no generator. GetCapabilities carries
        // the same no-provider / non-declared-model guards, so the gate stays in one place (ADR 0020).
        if (_providers.Count == 0 || !GetCapabilities(modelId).SupportsImageGeneration)
        {
            return null;
        }

        var (provider, model, _) = Resolve(modelId);
        // Only an OpenAI-compatible provider carries an ImageService func (Foundry image gen is out of scope,
        // ADR 0020) — and only such a model can report SupportsImageGeneration, so this is never null here.
        return provider.ImageService?.Invoke(model);
    }

    public IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator()
    {
        // No providers, or no embedding model configured (the resolver leaves the id "" unless it names a
        // declared embedding-capable model) → the no-op that throws on use. Callers gate on
        // DefaultEmbeddingModelId first: upload surfaces the error, retrieval skips embedding entirely.
        if (_providers.Count == 0 || string.IsNullOrEmpty(DefaultEmbeddingModelId))
        {
            return NoEmbeddingGenerator.Instance;
        }

        // The embedding id names a provider in this set (the resolver validated it against the same set the
        // factory was built from), so Resolve returns that provider's embedding model, not a chat fallback.
        var (provider, model, _) = Resolve(DefaultEmbeddingModelId);
        return provider.EmbeddingGenerator?.Invoke(model) ?? NoEmbeddingGenerator.Instance;
    }

    public ISpeechService? GetSpeechService()
    {
        // No providers, or no speech model configured (the resolver leaves the id "" unless it names a declared
        // speech-capable model) → null. The /api/speech endpoint turns null into a 409 and the browser voice
        // (ADR 0037) stays in use. Mirrors GetEmbeddingGenerator's default-id resolution (ADR 0038).
        if (_providers.Count == 0 || string.IsNullOrEmpty(DefaultSpeechModelId))
        {
            return null;
        }

        var (provider, model, _) = Resolve(DefaultSpeechModelId);
        return provider.SpeechService?.Invoke(model);
    }

    // The copilot agent kind: the same resolution GetClient uses, so a copilot chat reaches exactly the provider and
    // deployment its ordinary chat client would.
    public (CopilotProviderSettings Settings, string Model)? GetCopilotProvider(string? modelId)
    {
        if (_providers.Count == 0)
        {
            return null;
        }

        var (provider, model, _) = Resolve(modelId);
        return provider.Copilot is { } settings ? (settings, model) : null;
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
        var builder = provider.Client(model).AsBuilder();

        // A declared reasoning model asks the provider for a reasoning summary (ADR 0071). Here rather than
        // inside each provider's client lambda: this is the one place that already has BOTH the resolved
        // provider and the bare model id, and it is where the other cross-cutting decoration (OTel, below)
        // lives — so one wiring covers Foundry, OpenAI-compatible and Anthropic alike. (On Anthropic it becomes
        // `thinking.display: "summarized"`, passed through by AnthropicCompatibilityChatClient, ADR 0103.)
        //
        // Measured on the pinned stack: on the Responses path (ADR 0014, how prod Foundry runs) this becomes
        // `reasoning: {summary: "concise"}` and is the ONLY reason a reasoning model emits any summary at
        // all; on Chat Completions it is inert — the request body is byte-identical — because those
        // providers (DeepSeek and friends) send `reasoning_content` unsolicited. So it is safe to apply
        // uniformly, and doing so keeps the checkbox meaning one thing to the user on every provider.
        //
        // Note it does NOT gate DISPLAY: ProjectStreamedContent projects any TextReasoningContent it sees,
        // so an unticked model that reasons anyway still shows its thinking. The flag only asks.
        if (Provider.RequestsReasoning(provider.Options, model))
        {
            builder = builder.ConfigureOptions(options =>
                options.Reasoning ??= new ReasoningOptions { Output = ReasoningOutput.Summary });
        }

        // The prompt-cache key (ADR 0104) is stripped on every call and sent only to Azure OpenAI. Inside OTel, so the
        // span sees the caller's options.
        return builder
            .UseOpenTelemetry(sourceName: _otelSourceName, configure: cfg => cfg.EnableSensitiveData = true)
            .Use(inner => new PromptCacheKeyChatClient(inner, provider.Options))
            .Build();
    }

    /// <summary>One configured provider: its options plus the underlying SDK client it hands out models from.</summary>
    public sealed class Provider(
        ProviderOptions options,
        Func<string, IChatClient> client,
        Func<string, IImageGenerationService>? imageService = null,
        Func<string, IEmbeddingGenerator<string, Embedding<float>>>? embeddingGenerator = null,
        Func<string, ISpeechService>? speechService = null,
        CopilotProviderSettings? copilot = null)
    {
        public ProviderOptions Options { get; } = options;

        /// <summary>
        /// How the Copilot runtime reaches this provider (the copilot agent kind), or null for one built without
        /// it. Set by <see cref="Foundry"/> and <see cref="OpenAICompatible"/>, the only places that hold the
        /// endpoint and the decrypted key together and map onto a Copilot BYOK provider (<see cref="Anthropic"/>
        /// leaves it null, so a Copilot agent on Claude is refused, ADR 0103).
        /// </summary>
        public CopilotProviderSettings? Copilot { get; } = copilot;

        public Func<string, IChatClient> Client { get; } = client;

        /// <summary>
        /// Builds an <see cref="ISpeechService"/> for a speech (text-to-speech) model of this provider
        /// (ADR 0038) — via the Azure/OpenAI <c>GetAudioClient</c>, NOT the chat path. Set on every Foundry and
        /// OpenAI-compatible provider (an Anthropic one has none, ADR 0103); the per-model gate is the resolver, which only names a model marked
        /// <c>SupportsSpeech</c> as the default speech model.
        /// </summary>
        public Func<string, ISpeechService>? SpeechService { get; } = speechService;

        /// <summary>
        /// Builds an <see cref="IEmbeddingGenerator{String, Single}"/> for an embedding model of this provider
        /// (document RAG, ADR 0007) — via the Azure/OpenAI <c>GetEmbeddingClient</c>, NOT the chat/Responses
        /// path. Set on every Foundry and OpenAI-compatible provider (Anthropic has no embeddings, ADR 0103); the
        /// per-model gate is the resolver, which
        /// only names a model marked <c>SupportsEmbeddings</c> as the default embedding model.
        /// </summary>
        public Func<string, IEmbeddingGenerator<string, Embedding<float>>>? EmbeddingGenerator { get; } = embeddingGenerator;

        /// <summary>
        /// Builds an <see cref="IImageGenerationService"/> for a model of this provider (ADR 0020), or null for
        /// a provider that does no image generation (every Foundry provider — image gen is OpenAI-compatible
        /// only). Set once for an OpenAI-compatible provider, mirroring <see cref="Client"/>; the per-model gate
        /// is <see cref="GetImageGenerationService"/> (only a model flagged <c>SupportsImageGeneration</c> reaches it).
        /// </summary>
        public Func<string, IImageGenerationService>? ImageService { get; } = imageService;

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

        /// <summary>
        /// Whether <paramref name="model"/> is declared as a <b>reasoning</b> model on this provider
        /// (ADR 0071) — the signal to ask the provider for a reasoning summary.
        ///
        /// <para>Pure and <b>public</b> for the same reason as <see cref="UseResponses"/>: a unit test
        /// asserts the rule without a network call. Matched on the BARE model id, which is what
        /// <c>Provider.Client</c> and <c>Build</c> are handed — <c>ConfiguredModel.Id</c> is unqualified,
        /// while <c>ModelInfo.Id</c> is qualified, and comparing against the wrong one silently never
        /// matches.</para>
        ///
        /// <para>Deliberately NOT a <c>ModelCapabilities</c> bit: <see cref="MultiProviderChatClientFactory.GetCapabilities"/>
        /// short-circuits a Foundry provider to <c>ModelCapabilities.Full with {...}</c> without consulting
        /// the declared models at all, so a bit added there would read true for every Foundry model. This is
        /// the third capability-flag pattern in the codebase — neither a <c>ModelCapabilities</c> bit nor a
        /// picker-only filter like <c>SupportsEmbeddings</c>/<c>SupportsSpeech</c>, which never reach client
        /// construction.</para>
        /// </summary>
        public static bool RequestsReasoning(ProviderOptions options, string model) =>
            options.Models.Any(m => m.SupportsReasoning
                && string.Equals(m.Id, model, StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The <c>/openai/v1</c> base for Foundry embeddings: the endpoint with any
        /// <c>/api/projects/{name}</c> suffix dropped, because the project-scoped v1 surface has no
        /// embeddings route. The resource key authenticates at the root either way.
        /// </summary>
        public static Uri FoundryEmbeddingsEndpoint(Uri endpoint)
        {
            var path = endpoint.AbsolutePath;
            var project = path.IndexOf("/api/projects/", StringComparison.OrdinalIgnoreCase);
            var root = (project >= 0 ? path[..project] : path).TrimEnd('/');
            return new UriBuilder(endpoint) { Path = root + "/openai/v1", Query = "" }.Uri;
        }

        /// <summary>An Azure AI Foundry provider, reached with an Azure OpenAI API key (ADR 0017).</summary>
        public static Provider Foundry(ProviderOptions options, Uri endpoint, string apiKey)
        {
            // Embeddings use the same GA /openai/v1 surface and `api-key` policy as chat, whichever chat wire
            // API the provider uses. They used to go through AzureOpenAIClient's classic data plane, whose
            // api-version (2024-10-21) a Foundry project endpoint rejects with "API version not supported".
            // The v1 embeddings route lives at the RESOURCE root only: a project-scoped /openai/v1 serves
            // chat but 404s on /embeddings (live probe, 2026-10-17), hence FoundryEmbeddingsEndpoint.
#pragma warning disable OPENAI001
            var embeddingClient = new OpenAIClient(
                new ApiKeyHeaderPolicy(apiKey), new OpenAIClientOptions { Endpoint = FoundryEmbeddingsEndpoint(endpoint) });
#pragma warning restore OPENAI001
            Func<string, IEmbeddingGenerator<string, Embedding<float>>> embeddings =
                model => embeddingClient.GetEmbeddingClient(model).AsIEmbeddingGenerator();

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
                // Speech synthesis (ADR 0038) rides the SAME GA /openai/v1 client — NOT the classic
                // api-version data plane the embedding client uses (which the stable Azure SDK caps at
                // 2024-10-21, rejected by newer TTS deployments). Set on every Foundry provider; the
                // resolver's default-speech pick is the per-model gate.
                return new Provider(
                    options,
                    model => responsesClient
                        .GetResponsesClient()
                        .AsIChatClientWithStoredOutputDisabled(model, includeReasoningEncryptedContent: false),
                    embeddingGenerator: embeddings,
                    speechService: model => new OpenAISpeechService(responsesClient.GetAudioClient(model)),
                    copilot: CopilotProviderSettings.Foundry(endpoint, apiKey, useResponses: true));
#pragma warning restore MAAI001
#pragma warning restore OPENAI001
            }

            // Chat Completions on Foundry's GA "v1" surface ({endpoint}/openai/v1), NOT the classic
            // api-version data plane. AzureOpenAIClient.GetChatClient targets
            // /openai/deployments/{model}/chat/completions?api-version=…, and the stable Azure.AI.OpenAI
            // 2.1.0 caps that query at its newest ServiceVersion (2024-10-21) — which newer Foundry
            // deployments reject with "api version not supported". The v1 surface carries NO api-version
            // (the same reason the Responses branch above uses it), so build the stable OpenAIClient at
            // /openai/v1 with the `api-key`-header policy: Azure reads the resource key from the `api-key`
            // request header there, exactly as on the Responses path (ADR 0017 — do NOT swap it for the
            // OpenAI SDK's ApiKeyCredential, which would send an undocumented Authorization: Bearer).
            // Embeddings have their own v1 client above, at the resource root. No named HttpClient (its AddServiceDefaults retry pipeline
            // would multiply the stall on a non-reentrant AgentGrain turn — see OpenAICompatible below).
            // The endpoint still comes only from config — SSRF invariant intact (ADR 0013).
#pragma warning disable OPENAI001
            var v1Chat = new Uri(endpoint.AbsoluteUri.TrimEnd('/') + "/openai/v1");
            var chatClient = new OpenAIClient(
                new ApiKeyHeaderPolicy(apiKey), new OpenAIClientOptions { Endpoint = v1Chat });
#pragma warning restore OPENAI001
            return new Provider(
                options,
                model => chatClient.GetChatClient(model).AsIChatClient(),
                embeddingGenerator: embeddings,
                // Speech (ADR 0038) rides the same /openai/v1 client as chat (see the Responses branch note).
                speechService: model => new OpenAISpeechService(chatClient.GetAudioClient(model)),
                copilot: CopilotProviderSettings.Foundry(endpoint, apiKey, useResponses: false));
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
            // The SAME OpenAIClient hands out chat, image AND embedding clients — one transport, no
            // IHttpClientFactory (ADR 0020). GetImageClient/GetEmbeddingClient do no I/O; the per-model gates
            // (GetImageGenerationService, the resolver's embedding pick) decide which func a model reaches, so
            // building them for every model here is free.
            return new Provider(
                options,
                model => client.GetChatClient(model).AsIChatClient(),
                model => new OpenAIImageGenerator(client.GetImageClient(model)),
                model => client.GetEmbeddingClient(model).AsIEmbeddingGenerator(),
                // Speech synthesis (ADR 0038) off the same OpenAIClient — GetAudioClient does no I/O, and the
                // resolver's default-speech pick is the per-model gate, so building it for every model is free.
                model => new OpenAISpeechService(client.GetAudioClient(model)),
                CopilotProviderSettings.OpenAICompatible(endpoint, apiKey));
        }

        /// <summary>
        /// Anthropic (Claude) over its native Messages API (ADR 0103), through the official SDK's
        /// <see cref="IChatClient"/>. Not <see cref="OpenAICompatible"/> against Anthropic's compatibility endpoint:
        /// that one drops prompt caching and proper thinking. Each model's client is wrapped in
        /// <see cref="AnthropicCompatibilityChatClient"/>, the single home for the request shapes current Claude
        /// models reject. Chat only: no image, embedding, speech or Copilot funcs.
        ///
        /// <para>Every SDK setting that would otherwise fall back to an <c>ANTHROPIC_*</c> environment variable
        /// (<c>ApiKey</c>, <c>AuthToken</c>, <c>BaseUrl</c>) is set from the user's stored provider, so nothing ambient
        /// leaks in (ADR 0019/0030). The endpoint comes from config only (the SSRF note at the top of this class). The
        /// transport is the SDK's own, not an IHttpClientFactory client, for the same retry reason as
        /// <see cref="OpenAICompatible"/>; <paramref name="handler"/> exists only as the test seam.</para>
        /// </summary>
        public static Provider Anthropic(
            ProviderOptions options, Uri endpoint, string apiKey, HttpMessageHandler? handler = null)
        {
            // Init-only properties, so the test seam needs its own construction rather than a later assignment.
            var client = handler is null
                ? new global::Anthropic.AnthropicClient
                {
                    ApiKey = apiKey,
                    AuthToken = null,
                    BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
                    MaxRetries = AnthropicLimits.MaxRetries,
                }
                : new global::Anthropic.AnthropicClient
                {
                    ApiKey = apiKey,
                    AuthToken = null,
                    BaseUrl = endpoint.AbsoluteUri.TrimEnd('/'),
                    MaxRetries = AnthropicLimits.MaxRetries,
                    HttpClient = new HttpClient(handler),
                };

            return new Provider(
                options,
                model => new AnthropicCompatibilityChatClient(
                    client.AsIChatClient(model, AnthropicLimits.DefaultMaxOutputTokens), model));
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
