using System.ClientModel.Primitives;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Responses;

namespace Agent42.Backend;

public sealed partial class MultiProviderChatClientFactory
{
    public sealed partial class Provider
    {
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
                    // The wrapper recovers an answer that arrives only in the completed event, which the adapter drops (see its summary).
                    model => new ResponsesCompletedTextFallbackChatClient(responsesClient
                        .GetResponsesClient()
                        .AsIChatClientWithStoredOutputDisabled(model, includeReasoningEncryptedContent: false)),
                    embeddingGenerator: embeddings,
                    speechService: model => new OpenAISpeechService(responsesClient.GetAudioClient(model)),
                    transcriptionService: model => new OpenAITranscriptionService(responsesClient.GetAudioClient(model)),
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
                transcriptionService: model => new OpenAITranscriptionService(chatClient.GetAudioClient(model)),
                copilot: CopilotProviderSettings.Foundry(endpoint, apiKey, useResponses: false));
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
