using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// Shows the model the images its MCP tools returned this run (the Chrome-extension ADR), for the current turn only.
/// The shaper in front of every MCP tool (<c>McpToolResultShaper</c>) takes an image out of the tool result, leaves a
/// text placeholder there (that text is all the session ever persists), and records the bytes in the run's
/// <see cref="TurnContext"/> under the tool call id. On each model call of the loop this client puts them back into the
/// <i>request</i>.
///
/// <para><b>Option (b), measured:</b> one synthetic user message after the whole tool-result group of an iteration,
/// carrying the images as <see cref="DataContent"/>. That is the only shape all three adapters send as an image.
/// Chat Completions serializes an image inside a tool result as JSON <i>text</i>, while Responses and Anthropic
/// would take it there natively. Never between two tool results: Chat Completions needs them contiguous after
/// <c>tool_calls</c>, and Anthropic needs the <c>tool_result</c> blocks first.</para>
///
/// <para><b>It always clones.</b> The messages it is handed are the grain's live session objects (the
/// <c>AnthropicCompatibilityChatClient</c> rule), and an edit would persist base64 into the 2 MB session (ADR 0004).
/// It builds a new list and never touches a message or a content it was given.</para>
///
/// <para><b>Placement:</b> innermost, directly around the provider client and below <c>UseOpenTelemetry</c>. That
/// layer records message contents (<c>EnableSensitiveData</c>), so above it every span would carry the base64. It
/// is also below the function-invoking client, so it runs on every model call of the loop. With no recorded image
/// (every non-vision run, every Copilot run, any run without an MCP image) it passes the request through untouched.</para>
/// </summary>
public sealed class ToolImageChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    /// <summary>The text that introduces the inserted images to the model.</summary>
    public const string Introduction = "The images returned by the tool call(s) above, shown for this turn only:";

    public override Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetResponseAsync(WithToolImages(messages, TurnContext.CurrentOrClosed), options, cancellationToken);

    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => base.GetStreamingResponseAsync(WithToolImages(messages, TurnContext.CurrentOrClosed), options, cancellationToken);

    /// <summary>
    /// The request with the run's tool images inserted after their tool-result groups, or <paramref name="messages"/>
    /// itself (the same instance) when there is nothing to insert.
    /// </summary>
    public static IEnumerable<ChatMessage> WithToolImages(IEnumerable<ChatMessage> messages, TurnContext turn)
    {
        if (!turn.HasToolImages)
        {
            return messages;
        }

        var source = messages as IReadOnlyList<ChatMessage> ?? [.. messages];

        // Only this turn's tool results: the ones after the last user message. A provider that reuses short call ids
        // (call_0) across turns would otherwise get this run's image inserted after an old turn's result too.
        var turnStart = 0;
        for (var i = source.Count - 1; i >= 0; i--)
        {
            if (source[i].Role == ChatRole.User)
            {
                turnStart = i + 1;
                break;
            }
        }

        var result = new List<ChatMessage>(source.Count + 1);
        var pending = new List<AIContent>();
        for (var i = 0; i < source.Count; i++)
        {
            var message = source[i];
            var isToolResult = i >= turnStart && message.Contents.Count > 0 && message.Contents.All(c => c is FunctionResultContent);
            if (!isToolResult)
            {
                Flush(result, pending);
            }

            result.Add(message);
            if (isToolResult)
            {
                foreach (var call in message.Contents.OfType<FunctionResultContent>())
                {
                    pending.AddRange(turn.ToolImagesFor(call.CallId).Select(i => new DataContent(i.Data, i.MediaType)));
                }
            }
        }

        Flush(result, pending);
        return result;
    }

    private static void Flush(List<ChatMessage> result, List<AIContent> pending)
    {
        if (pending.Count == 0)
        {
            return;
        }

        result.Add(new ChatMessage(ChatRole.User, [new TextContent(Introduction), .. pending]));
        pending.Clear();
    }
}
