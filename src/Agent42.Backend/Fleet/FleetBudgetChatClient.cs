using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Counts every model response of a fleet task in its <see cref="FleetBudget"/>, and stops model calls once the task
/// is cancelled (ADR 0097). It wraps the user's client per task and never changes the shared client, so the usage
/// dashboard's own OpenTelemetry layer underneath still sees every call.
/// </summary>
public sealed class FleetBudgetChatClient(IChatClient innerClient, FleetBudget budget) : DelegatingChatClient(innerClient)
{
    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        budget.Token.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        var response = await base.GetResponseAsync(messages, options, linked.Token);
        budget.AddTokens(response.Usage);
        return response;
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        budget.Token.ThrowIfCancellationRequested();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        await foreach (var update in base.GetStreamingResponseAsync(messages, options, linked.Token))
        {
            foreach (var usage in update.Contents.OfType<UsageContent>())
            {
                budget.AddTokens(usage.Details);
            }

            yield return update;
        }
    }
}
