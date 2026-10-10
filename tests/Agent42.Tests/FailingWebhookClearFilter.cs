using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// Makes <see cref="IAgentWebhookGrain.ClearAsync"/> throw for a webhook grain whose key carries
/// <see cref="Marker"/>, so <c>AgentDelete_WhenWebhookClearFails_AgentSurvives</c> can prove the agent delete clears
/// the webhook BEFORE it touches the registry (ADR 0094). Every other call passes straight through.
/// </summary>
public sealed class FailingWebhookClearFilter : IIncomingGrainCallFilter
{
    public const string Marker = "webhook-clear-fails";

    public Task Invoke(IIncomingGrainCallContext context)
    {
        if (context.Grain is IAgentWebhookGrain
            && context.InterfaceMethod.Name == nameof(IAgentWebhookGrain.ClearAsync)
            && context.TargetId.Key.ToString().Contains(Marker, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Simulated webhook clear failure.");
        }

        return context.Invoke();
    }
}
