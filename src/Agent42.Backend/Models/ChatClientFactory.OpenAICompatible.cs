using System.ClientModel;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using OpenAI;

namespace Agent42.Backend;

public sealed partial class MultiProviderChatClientFactory
{
    public sealed partial class Provider
    {
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
                CopilotProviderSettings.OpenAICompatible(endpoint, apiKey),
                model => new OpenAITranscriptionService(client.GetAudioClient(model)));
        }
    }
}
