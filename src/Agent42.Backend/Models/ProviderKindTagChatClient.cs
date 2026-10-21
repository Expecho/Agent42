using System.Diagnostics;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Stamps the serving provider's <see cref="ProviderKind"/> onto the current <c>chat</c> span as
/// <see cref="UsageSpanProjector.ProviderKindTag"/> (ADR 0110), so <c>/usage</c> can price the call. The semconv
/// <c>gen_ai.provider.name</c> cannot do this: it reads <c>"openai"</c> for Azure OpenAI AND for every
/// OpenAI-compatible endpoint, and only the first has a price row.
///
/// <para>Wrapped directly INSIDE <c>UseOpenTelemetry</c> in <c>MultiProviderChatClientFactory.Build</c>, where
/// <see cref="Activity.Current"/> is the chat span the OTel client just started (<c>ProviderKindTag_IsOnChatSpan</c>
/// pins this for both the streaming and non-streaming paths). The factory knows the kind and the grain does not,
/// which is why this is a client and not a RequestContext value like <c>agent42.application</c>.</para>
/// </summary>
public sealed class ProviderKindTagChatClient(IChatClient inner, ProviderKind kind) : DelegatingChatClient(inner)
{
    private readonly string _kind = kind.ToString();

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Tag();
        return base.GetResponseAsync(messages, options, cancellationToken);
    }

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Tag();
        return base.GetStreamingResponseAsync(messages, options, cancellationToken);
    }

    // Only the chat span: with no listener (or unsampled) the OTel client starts no span, and Activity.Current is
    // then a parent such as invoke_agent, which must not be stamped with a per-call fact.
    private void Tag()
    {
        if (Activity.Current is { } span
            && span.GetTagItem(UsageSpanProjector.OperationNameTag) as string == UsageSpanProjector.ChatOperation)
        {
            span.SetTag(UsageSpanProjector.ProviderKindTag, _kind);
        }
    }
}
