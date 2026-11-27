using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

public sealed partial class MultiProviderChatClientFactory
{
    public sealed partial class Provider
    {
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
}
