using Anthropic.Models.Messages;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>The Anthropic provider's fixed request limits (ADR 0103).</summary>
public static class AnthropicLimits
{
    /// <summary>
    /// The <c>max_tokens</c> sent when the caller sets no <see cref="ChatOptions.MaxOutputTokens"/>. The SDK's own
    /// fallback is a hard-coded 1024, which would silently truncate a chat reply or a long <c>create_artifact</c>
    /// argument. Measured on the stub transport that the SDK sends 32 000 non-streamed, too (ADR 0103).
    /// </summary>
    public const int DefaultMaxOutputTokens = 32_000;

    /// <summary>
    /// The SDK's retry count, set explicitly. It matches the OpenAI SDK's default <c>ClientRetryPolicy</c> (3, read
    /// from the pinned System.ClientModel), which the other providers keep. The SDK's own default is 2 (ADR 0103).
    /// </summary>
    public const int MaxRetries = 3;
}

/// <summary>
/// The single home for the request shapes current Claude models reject (ADR 0103). Applied only inside
/// <c>Provider.Anthropic</c>, because Agent Framework internals (Magentic manager, judge loop, declarative agents) can
/// send these too and cannot be patched at the call site. Per request it:
/// <list type="bullet">
/// <item>maps a required tool mode (<c>tool_choice: any</c> / <c>{type: tool}</c>, a 400) to <c>Auto</c>;</item>
/// <item>drops <c>Temperature</c>/<c>TopP</c>/<c>TopK</c> (non-default values are a 400);</item>
/// <item>maps <c>Reasoning.Effort = None</c> (<c>thinking: disabled</c>, a 400) to <c>Low</c>, and leaves
/// <c>Reasoning.Output</c> alone (ADR 0071's "show thinking" flag depends on it);</item>
/// <item>appends a <c>"Continue."</c> user turn after a trailing assistant message (an assistant prefill is a 400),
/// unless that message is a pending tool call;</item>
/// <item>drops a message that would reach the wire with zero blocks, never one carrying tool content;</item>
/// <item>turns on top-level automatic prompt caching.</item>
/// </list>
///
/// <para>It <b>always clones</b>: the messages are the grain's live session history, and a mutation would be
/// persisted (ADR 0004). <c>ChatClientFactory.Build</c> wraps this client, so the reasoning flag and OTel run
/// outside it and it sees the final options.</para>
/// </summary>
public sealed class AnthropicCompatibilityChatClient(IChatClient inner, string model) : DelegatingChatClient(inner)
{
    /// <summary>The user turn appended after a trailing assistant message.</summary>
    public const string ContinuePrompt = "Continue.";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetResponseAsync(RewriteMessages(messages), RewriteOptions(options), cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(RewriteMessages(messages), RewriteOptions(options), cancellationToken);

    private ChatOptions RewriteOptions(ChatOptions? options)
    {
        var clone = options?.Clone() ?? new ChatOptions();

        if (clone.ToolMode is RequiredChatToolMode)
        {
            clone.ToolMode = ChatToolMode.Auto;
        }

        clone.Temperature = null;
        clone.TopP = null;
        clone.TopK = null;
        clone.Reasoning = RewriteReasoning(clone.Reasoning);
        // This client is built for ONE bare model id, so that id always wins. A caller's ChatOptions.ModelId can hold the
        // QUALIFIED catalog id (a fleet agent's YAML model hint, 'Claude:claude-opus-5-5', which the framework's
        // ChatClientPromptAgentFactory copies into the options), and Anthropic answers that with a 404 not_found_error.
        clone.ModelId = model;
        clone.RawRepresentationFactory = CachingFactory(
            clone.RawRepresentationFactory, model,
            clone.MaxOutputTokens ?? AnthropicLimits.DefaultMaxOutputTokens);
        return clone;
    }

    // Thinking cannot be disabled on current models; low effort is the documented substitute. A fresh object, so the
    // caller's ReasoningOptions (possibly shared by ChatClientFactory.Build's ConfigureOptions) is never mutated.
    private static ReasoningOptions? RewriteReasoning(ReasoningOptions? reasoning) =>
        reasoning is { Effort: ReasoningEffort.None }
            ? new ReasoningOptions { Effort = ReasoningEffort.Low, Output = reasoning.Output }
            : reasoning;

    // Returning MessageCreateParams makes the SDK adapter take Model and MaxTokens FROM the params and ignore
    // ChatOptions and the client defaults, so both are copied in. A caller's own MessageCreateParams keeps every field
    // it set and only gains cache_control; any other caller result passes through untouched (the caller wins).
    private static Func<IChatClient, object?> CachingFactory(
        Func<IChatClient, object?>? caller, string modelId, int maxTokens) =>
        client =>
        {
            var raw = caller?.Invoke(client);
            return raw switch
            {
                null => new MessageCreateParams
                {
                    Model = modelId,
                    MaxTokens = maxTokens,
                    Messages = [],
                    CacheControl = new CacheControlEphemeral(),
                },
                MessageCreateParams { CacheControl: null } p => p with { CacheControl = new CacheControlEphemeral() },
                _ => raw,
            };
        };

    private static List<ChatMessage> RewriteMessages(IEnumerable<ChatMessage> messages)
    {
        // A new list of the caller's own message objects: dropping and appending never touches the caller's list, and
        // no message object is edited.
        var list = messages.Where(HasSendableContent).ToList();

        if (list is [.., { Role: var role } last] && role == ChatRole.Assistant
            && !last.Contents.Any(c => c is FunctionCallContent))
        {
            list.Add(new ChatMessage(ChatRole.User, ContinuePrompt));
        }

        return list;
    }

    // Mirrors what the SDK puts on the wire (measured on 12.58.0, pinned in AnthropicCompatibilityChatClientTests): it
    // drops whitespace-only text, an empty unsigned thinking block and media it cannot send, and then skips a message
    // left with zero blocks. Dropping the same messages here keeps the trailing-assistant check above looking at the
    // message that really ends the request. Tool content is never dropped: an orphaned tool_result is a 400.
    private static bool HasSendableContent(ChatMessage message) =>
        message.Contents.Any(content => content switch
        {
            FunctionCallContent or FunctionResultContent or HostedFileContent => true,
            TextContent text => !string.IsNullOrWhiteSpace(text.Text),
            TextReasoningContent reasoning => !string.IsNullOrWhiteSpace(reasoning.Text)
                || !string.IsNullOrEmpty(reasoning.ProtectedData),
            DataContent data => IsImageOrPdf(data.MediaType)
                || data.MediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase),
            UriContent uri => IsImageOrPdf(uri.MediaType),
            _ => false,
        });

    private static bool IsImageOrPdf(string mediaType) =>
        mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
        || string.Equals(mediaType, "application/pdf", StringComparison.OrdinalIgnoreCase);
}
