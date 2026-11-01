namespace Agent42.Agents;

/// <summary>
/// The grain's door to the agent fleet (ADR 0115): <c>start_fleet_task</c> starts a task tied to the conversation, and
/// <c>get_fleet_task</c> reads one. A port (ADR 0105, rule 7), because the fleet runs in-process in the Backend, not in
/// a grain (ADR 0097): hosts with the fleet register the Backend's <c>FleetLauncher</c>, any other host registers
/// <see cref="DisabledFleetLauncher"/>.
/// </summary>
public interface IFleetLauncher
{
    /// <summary>
    /// Starts a task for <paramref name="userId"/> that reports back to <paramref name="conversationId"/>.
    /// <paramref name="model"/> is the conversation's model, used when it can run the fleet; otherwise the user's
    /// default model runs it. Returns the task id, or why the task can't start, as text for the model.
    /// </summary>
    Task<FleetLaunchResult> StartAsync(
        string userId, string conversationId, string task, string? model, string? followUpOf, CancellationToken cancellationToken = default);

    /// <summary>The user's task, or null when it doesn't exist or isn't theirs.</summary>
    Task<FleetTaskRecord?> GetAsync(string userId, string taskId, CancellationToken cancellationToken = default);
}

/// <summary>How a start went: the new task's id, or the reason it didn't start.</summary>
public sealed record FleetLaunchResult(string? TaskId, string? Error);

/// <summary>The port's stand-in for a host without the agent fleet: every start is refused, and no task is found.</summary>
public sealed class DisabledFleetLauncher : IFleetLauncher
{
    public const string Unavailable = "The agent fleet isn't available here.";

    public Task<FleetLaunchResult> StartAsync(
        string userId, string conversationId, string task, string? model, string? followUpOf, CancellationToken cancellationToken = default)
        => Task.FromResult(new FleetLaunchResult(null, Unavailable));

    public Task<FleetTaskRecord?> GetAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult<FleetTaskRecord?>(null);
}
