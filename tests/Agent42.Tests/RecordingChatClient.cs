using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// An <see cref="IChatClient"/> that records the messages and options of its last call and answers with
/// <see cref="Reply"/> (default: the text "ok"). For asserting what a wrapper such as
/// <c>AnthropicCompatibilityChatClient</c> hands the client below it.
/// </summary>
public sealed class RecordingChatClient : IChatClient
{
    public List<ChatMessage>? Messages { get; private set; }

    public ChatOptions? Options { get; private set; }

    public ChatMessage Reply { get; set; } = new(ChatRole.Assistant, "ok");

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        Messages = [.. messages];
        Options = options;
        return Task.FromResult(new ChatResponse(Reply));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        Messages = [.. messages];
        Options = options;
        await Task.Yield();
        yield return new ChatResponseUpdate(Reply.Role, Reply.Contents);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
