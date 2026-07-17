using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The <see cref="IChatClient"/> an empty <see cref="MultiProviderChatClientFactory"/> hands out when a
/// user has configured no chat provider (ADR 0019). It exists so the agent grain can still build a valid
/// <c>_agent</c>/<c>_session</c> with no model — <c>ChatClientAgent</c> construction and all session
/// serialize/deserialize/create operations only <b>wrap</b> a client and never invoke inference, so a
/// grain wrapping this keeps history, bind, register-document, reminder and rehydrate working unguarded.
///
/// <para>Running inference IS guarded — the grain checks
/// <c>string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)</c> before ever calling the model — so
/// <see cref="GetResponseAsync"/>/<see cref="GetStreamingResponseAsync"/> are never reached in practice.
/// They throw a clear <see cref="InvalidOperationException"/> rather than a cryptic
/// <c>KeyNotFoundException</c> so that a future caller who forgets the guard fails loudly and legibly.</para>
///
/// <para>A single shared, stateless instance is enough — it holds no per-model state.</para>
/// </summary>
public sealed class NoModelChatClient : IChatClient
{
    /// <summary>The shared instance the empty factory hands out (stateless, so one is enough).</summary>
    public static readonly NoModelChatClient Instance = new();

    private const string Message =
        "No model provider is configured. Add a model provider in Preferences before chatting (ADR 0019). "
        + "This client is a no-op the grain must never actually run — the no-model path is guarded upstream.";

    private NoModelChatClient()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
