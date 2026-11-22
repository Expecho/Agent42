using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using OpenAI.Responses;

namespace Agent42.Backend;

#pragma warning disable OPENAI001 // The experimental Responses surface is what this wrapper reads.

/// <summary>
/// A Responses stream that carries the answer ONLY in its final <c>response.completed</c> event (no <c>output_text.delta</c>
/// events) reaches the adapter as a lone <see cref="UsageContent"/>: the adapter maps deltas, not the completed output, so the
/// turn ended with output tokens billed and an empty reply (<c>gpt-6.2-sol</c>, 2026-11-22: finish reason stop, 11 output tokens,
/// content <c>UsageContent</c> only; reproduced in <c>ResponsesCompletedTextFallbackTests</c>).
///
/// <para>When a stream has yielded no text at all, the completed event's message items are emitted as one
/// <see cref="TextContent"/> just before the usage update. A stream that did stream text is passed through untouched, so a
/// model that works today sees no change. One stream is one model call, so a tool-calling turn is judged per call.</para>
/// </summary>
public sealed class ResponsesCompletedTextFallbackChatClient(IChatClient inner) : DelegatingChatClient(inner)
{
    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var streamedText = false;
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
        {
            streamedText |= update.Contents.OfType<TextContent>().Any(t => !string.IsNullOrEmpty(t.Text));
            if (!streamedText && update.RawRepresentation is StreamingResponseCompletedUpdate completed
                && CompletedText(completed) is { Length: > 0 } text)
            {
                yield return new ChatResponseUpdate(ChatRole.Assistant, text) { RawRepresentation = completed };
            }

            yield return update;
        }
    }

    // The assistant message text of the finished response. Reasoning and tool-call items carry none here: a tool call is mapped by
    // the adapter from its own item, and reasoning is never shown without a summary.
    private static string CompletedText(StreamingResponseCompletedUpdate completed)
        => string.Concat(completed.Response.OutputItems
            .OfType<MessageResponseItem>()
            .SelectMany(item => item.Content)
            .Select(part => part.Text));
}
