using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The <see cref="IChatClient"/> a DecisionModel provider hands out (ADR 0137). A Decision-1 deployment classifies; it does
/// not chat. This is the server-side guard for any path that reaches a client for the router's id (a persisted id, a hand-made
/// request): it refuses, instead of letting <c>Resolve</c> fall back to the default model.
///
/// <para>Shape of <see cref="CopilotOnlyChatClient"/>: stateless, one shared instance.</para>
/// </summary>
public sealed class DecisionModelOnlyChatClient : IChatClient
{
    public static readonly DecisionModelOnlyChatClient Instance = new();

    public const string Message =
        "This is the Auto router (Decision-1), not a chat model. Pick a chat model for this conversation.";

    private DecisionModelOnlyChatClient()
    {
    }

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException(Message);

    public object? GetService(Type serviceType, object? serviceKey = null)
        => serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}

/// <summary>The router's coordinates (ADR 0137): the user's Foundry resource endpoint, key and Decision-1 deployment name.</summary>
public sealed record DecisionProviderSettings(string Endpoint, string ApiKey, string Deployment);
