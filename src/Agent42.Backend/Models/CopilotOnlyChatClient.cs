using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The <see cref="IChatClient"/> a GitHub Copilot provider hands out (ADR 0108). Its models run only on the Copilot
/// runtime, with the user's GitHub token per session; there is no chat-completions endpoint Agent42 may call with
/// that token. This client is the ONE server-side guard for every other path that reaches a client for a model id
/// (a plain chat, a non-copilot stored agent, research, follow-ups, a fleet non-copilot agent): it refuses with a
/// message the user can act on. The pickers hide these models too, but the picker is not the guard.
///
/// <para>Shape of <see cref="NoModelChatClient"/>: stateless, one shared instance.</para>
/// </summary>
public sealed class CopilotOnlyChatClient : IChatClient
{
    public static readonly CopilotOnlyChatClient Instance = new();

    public const string Message =
        "This GitHub Copilot model runs only in Copilot agents. Pick another model for this chat, or create a "
        + "Copilot agent to use it.";

    private CopilotOnlyChatClient()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    // Answers for itself, so a caller (and a test) can tell this client apart under the factory's wrappers.
    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
