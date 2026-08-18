using Microsoft.Extensions.AI;

namespace Agent42.Agents.ImageGeneration;

/// <summary>
/// A one-shot <see cref="IChatClient"/> adapter that returns a pre-built assistant message, ignoring the
/// request (ADR 0020). It exists for a single reason the Step-0 spike settled: Microsoft.Agents.AI 1.13 has
/// <b>no</b> public way to append a user+assistant message pair to an <c>AgentSession</c> without invoking
/// inference (<c>TryGetInMemoryChatHistory</c> yields no mutable list on a fresh session). So the grain records
/// a generated-image turn by running THIS client through <c>RunAsync</c>: the framework appends the user turn
/// and this fixed assistant reply into the session's history naturally, and the whole turn serializes like any
/// other — no bespoke history mutation.
///
/// <para>The image itself was already generated and stored by the grain (this client makes no network call);
/// the assistant message it returns carries only a short <see cref="UriContent"/> reference (a blob SAS URL),
/// never bytes, so the session stays within the 2 MB cap (ADR 0004).</para>
/// </summary>
internal sealed class ImageGenerationChatClient(IReadOnlyList<AIContent> assistantContents) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [.. assistantContents])));

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, [.. assistantContents]);
        await Task.CompletedTask;
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
