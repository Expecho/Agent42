using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Told when a fleet task finished (ADR 0115). <see cref="FleetTaskService"/> calls it after the task's final save,
/// for a Done, Failed or Stopped task that has an origin conversation, never for an interrupted one.
/// </summary>
public interface IFleetCompletionNotifier
{
    Task NotifyAsync(FleetTaskRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Sends a finished fleet task's result to the conversation that started it, as a background turn
/// (<see cref="IAgentGrain.ReceiveFleetResultAsync"/>, one-way), framed as fleet data by
/// <see cref="FleetToolLimits.FrameResult"/>. The grain does the rest: the turn, the away push and the chat-app delivery.
/// </summary>
public sealed class FleetConversationNotifier(IGrainFactory grains) : IFleetCompletionNotifier
{
    public Task NotifyAsync(FleetTaskRecord record, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(record.OriginConversationId))
        {
            return Task.CompletedTask;
        }

        // The grain key is {userId}/{conversationId} (AgentApi.AgentKey): the task's own user, so a task can only
        // ever report to a conversation of the user who owns it.
        return grains.GetGrain<IAgentGrain>($"{record.UserId}/{record.OriginConversationId}")
            .ReceiveFleetResultAsync(record.Id, FleetToolLimits.FrameResult(record));
    }
}
