using System.Collections.Concurrent;
using Agent42.Agents;
using Microsoft.Agents.AI;

namespace Agent42.Tests;

/// <summary>
/// The test silo's <see cref="ICopilotAgentFactory"/>: the in-process twin (<see cref="ChatClientCopilotAgentFactory"/>)
/// behind a recorder. A test reads what the grain asked for (tools, skill folders, instructions) per conversation,
/// can make the factory refuse a conversation, and sees which conversations were deleted. Registered as its concrete
/// type too, like <see cref="RecordingPushNotificationSender"/>, so a test can fetch it from the silo container.
/// </summary>
public sealed class RecordingCopilotAgentFactory : ICopilotAgentFactory
{
    private readonly ChatClientCopilotAgentFactory _twin = new();
    private readonly ConcurrentDictionary<string, List<CopilotAgentRequest>> _requests = new();
    private readonly ConcurrentDictionary<string, string> _refusals = new();
    private readonly ConcurrentQueue<string> _deleted = new();

    /// <summary>Every request built for <paramref name="conversationId"/>, in order. One per BuildAgent.</summary>
    public IReadOnlyList<CopilotAgentRequest> RequestsFor(string conversationId)
        => _requests.TryGetValue(conversationId, out var list) ? [.. list] : [];

    /// <summary>The conversations whose transcript was deleted.</summary>
    public IReadOnlyCollection<string> Deleted => _deleted.ToArray();

    /// <summary>Makes every later request for <paramref name="conversationId"/> throw <see cref="CopilotUnavailableException"/>.</summary>
    public void Refuse(string conversationId, string reason) => _refusals[conversationId] = reason;

    // A GitHub Copilot model (ADR 0108) stands in for the real runtime here: the twin refuses one, so the agent runs on
    // the silo's default (fake) model instead, and the request is recorded with the GitHub model id the grain asked for.
    public AIAgent CreateAgent(CopilotAgentRequest request)
    {
        _requests.AddOrUpdate(request.ConversationId, _ => [request], (_, list) => { list.Add(request); return list; });
        return _refusals.TryGetValue(request.ConversationId, out var reason)
            ? throw new CopilotUnavailableException(reason)
            : _twin.CreateAgent(GitHubCopilotProvider.IsGitHubModel(request.ModelId)
                ? request with { ModelId = TestSiloConfigurator.DefaultModel }
                : request);
    }

    public Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
    {
        _deleted.Enqueue(conversationId);
        return Task.CompletedTask;
    }
}
