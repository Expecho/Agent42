using Agent42.Agents;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using OpenAI.Responses;

namespace Agent42.Backend;

/// <summary>
/// Turns the conversation's <see cref="PromptCacheKey"/> into the provider's <c>prompt_cache_key</c> (ADR 0104).
///
/// <para>On <b>every</b> call it removes <see cref="PromptCacheKey.PropertyName"/> from a <b>clone</b> of the options,
/// whatever the provider; a call that never set it (fleet, research, follow-ups, Copilot) passes through untouched.
/// Measured that no adapter would have serialized the unknown key anyway, so the strip is defence in depth. Only an
/// <see cref="ProviderKind.AzureOpenAI"/> provider is sent the key: an OpenAI-compatible server may reject an unknown
/// body field, and Anthropic caches through the shim's <c>cache_control</c> instead.</para>
///
/// <para>It composes <see cref="ChatOptions.RawRepresentationFactory"/>, and the caller wins: a caller's own
/// options object keeps every field it set and gains the key only when it has none. Set here rather than in the
/// grain, because a grain-side OpenAI options object would reach the Anthropic shim, which passes unknown objects
/// through and so would lose its caching. Wrapped below <c>UseOpenTelemetry</c> in <c>MultiProviderChatClientFactory.Build</c>,
/// so a sensitive-data span can still record the (hashed) property.</para>
/// </summary>
public sealed class PromptCacheKeyChatClient(IChatClient inner, ProviderOptions provider) : DelegatingChatClient(inner)
{
    private static ReadOnlySpan<byte> PatchPath => "$.prompt_cache_key"u8;

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(messages, Rewrite(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(messages, Rewrite(options), cancellationToken);

    private ChatOptions? Rewrite(ChatOptions? options)
    {
        if (options?.AdditionalProperties?.TryGetValue(PromptCacheKey.PropertyName, out var value) is not true)
        {
            return options;
        }

        var clone = options.Clone();
        var rest = new AdditionalPropertiesDictionary(options.AdditionalProperties);
        rest.Remove(PromptCacheKey.PropertyName);
        clone.AdditionalProperties = rest.Count > 0 ? rest : null;

        if (provider.Kind == ProviderKind.AzureOpenAI && value is string { Length: > 0 } key)
        {
            clone.RawRepresentationFactory = WithKey(
                options.RawRepresentationFactory, key, MultiProviderChatClientFactory.Provider.UseResponses(provider));
        }

        return clone;
    }

    // Measured (plan-prompt-caching Step 0a): on Responses the factory must return CreateResponseOptions, whose
    // PromptCacheKey the stored-output-disabled helper keeps; Chat Completions' options have no such property in
    // OpenAI 2.14.0, so it goes in through the JSON patch. Any other caller result passes through untouched.
    private static Func<IChatClient, object?> WithKey(Func<IChatClient, object?>? caller, string key, bool responses) =>
        client =>
        {
            var raw = caller?.Invoke(client);
#pragma warning disable OPENAI001, SCME0001 // CreateResponseOptions and JsonPatch are [Experimental] in OpenAI 2.14.0.
            switch (raw ?? (responses ? new CreateResponseOptions() : new ChatCompletionOptions()))
            {
                case CreateResponseOptions r:
                    r.PromptCacheKey ??= key;
                    return r;
                case ChatCompletionOptions c:
                    if (!c.Patch.Contains(PatchPath))
                    {
                        c.Patch.Set(PatchPath, key);
                    }

                    return c;
                default:
                    return raw;
            }
#pragma warning restore OPENAI001, SCME0001
        };
}
