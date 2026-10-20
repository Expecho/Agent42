using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The in-process twin of the Copilot agent, registered by DevHost and the test silo (the same role
/// <c>InMemoryAttachmentStore</c> plays for Blob storage). It runs a plain <see cref="ChatClientAgent"/> on the
/// conversation's own chat client, which is the canned client offline and the fake one in tests, so a copilot chat works with no
/// runtime binary and no network.
///
/// <para>It copies the two Copilot behaviours the grain has to cope with. It keeps its OWN transcript, under its own
/// state key (<see cref="TranscriptStateKey"/>), separate from the mirror the grain reads, the way Copilot keeps
/// its own in SessionFs. And it invokes every tool from a thread-pool thread, off the caller's scheduler, the way
/// Copilot invokes custom tools from its RPC thread, with no ExecutionContext flowed. That is what exercises the
/// grain's <see cref="SchedulerBoundAIFunction"/> marshalling and its <see cref="TurnContext"/> re-establish.</para>
/// </summary>
public sealed class ChatClientCopilotAgentFactory : ICopilotAgentFactory
{
    /// <summary>Where the twin keeps its own transcript in the session's state bag.</summary>
    public const string TranscriptStateKey = "CopilotTwinTranscript";

    /// <summary>A GitHub Copilot model has no chat client to run on, only the real runtime (ADR 0108).</summary>
    public const string GitHubModelNeedsRuntime =
        "_This copilot agent runs on a GitHub Copilot model, which needs the Copilot runtime. It isn't available "
        + "here._\n\n";

    public AIAgent CreateAgent(CopilotAgentRequest request)
        => GitHubCopilotProvider.IsGitHubModel(request.ModelId)
            ? throw new CopilotUnavailableException(GitHubModelNeedsRuntime)
            : new ChatClientAgent(
            request.ChatClientFactory.GetClient(request.ModelId),
            new ChatClientAgentOptions
            {
                Name = request.Name,
                ChatOptions = new ChatOptions
                {
                    Instructions = request.Instructions,
                    Tools = [.. request.Tools.Select(t => new OffSchedulerAIFunction(t))],
                },
                ChatHistoryProvider = new InMemoryChatHistoryProvider(
                    new InMemoryChatHistoryProviderOptions { StateKey = TranscriptStateKey }),
            });

    public Task DeleteConversationAsync(
        string userId, string conversationId, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    // Runs the tool on the thread pool, never inline on the caller's scheduler, and with the caller's ExecutionContext
    // NOT flowed: the SDK's RPC thread carries none of the turn's async-locals, so the twin must not either, or the
    // grain's TurnContext re-establish (ADR 0106) would pass here and fail against the real runtime.
    private sealed class OffSchedulerAIFunction(AIFunction inner) : DelegatingAIFunction(inner)
    {
        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments, CancellationToken cancellationToken)
        {
            Task<object?> call;
            using (ExecutionContext.SuppressFlow())
            {
                call = Task.Run(() => base.InvokeCoreAsync(arguments, cancellationToken).AsTask(), cancellationToken);
            }

            return await call;
        }
    }
}
