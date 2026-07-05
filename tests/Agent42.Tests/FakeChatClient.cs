using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// Deterministic in-memory <see cref="IChatClient"/> so agent tests run without a real model.
/// </summary>
public sealed class FakeChatClient : IChatClient
{
    private static readonly string[] ReplyChunks = ["Hello! ", "How can I ", "help you today?"];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, string.Concat(ReplyChunks))));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var chunk in ReplyChunks)
        {
            yield return new ChatResponseUpdate(ChatRole.Assistant, chunk);
            await Task.Yield();
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
