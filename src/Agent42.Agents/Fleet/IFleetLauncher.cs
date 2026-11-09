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

    // The fleet analyzer (ADR 0122). Every read returns text for the model: capped, and framed as data. All of them
    // are scoped to userId, so another user's task reads as not found. They sit on this port, not a new one, because
    // the grain already holds it and every test silo already registers it (ADR 0105).

    /// <summary>The user's most recent tasks, newest first.</summary>
    Task<string> ListRecentAsync(string userId, int limit, CancellationToken cancellationToken = default);

    /// <summary>A diagnosis of one task: status, failure signals, plan, open question, last events.</summary>
    Task<string> AnalyzeAsync(string userId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>A page of the task's event log, optionally filtered by comma-separated event types.</summary>
    Task<string> ReadEventsAsync(string userId, string taskId, string? types, long afterSeq, int limit, CancellationToken cancellationToken = default);

    /// <summary>The task's workspace listing, or one text file when <paramref name="path"/> is set.</summary>
    Task<string> ReadWorkspaceAsync(string userId, string taskId, string? path, CancellationToken cancellationToken = default);

    /// <summary>The fleet's effective limits and switches.</summary>
    string DescribeConfig();

    /// <summary>Recent trace spans of this user only (never another user's), optionally filtered by text.</summary>
    Task<string> ReadSpansAsync(string userId, string? search, int limit, CancellationToken cancellationToken = default);

    /// <summary>Stops the user's running task. The answer is text for the model.</summary>
    Task<string> StopAsync(string userId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a NEW task repeating <paramref name="taskId"/>'s request and attached files. Not an origin task, so it is
    /// not auto-approved. <paramref name="disableTools"/> can only remove tools from the original selection.
    /// </summary>
    Task<FleetLaunchResult> RerunAsync(
        string userId, string taskId, string? note, IReadOnlyList<string>? disableTools, long? maxTokens,
        CancellationToken cancellationToken = default);
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

    public Task<string> ListRecentAsync(string userId, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public Task<string> AnalyzeAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public Task<string> ReadEventsAsync(string userId, string taskId, string? types, long afterSeq, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public Task<string> ReadWorkspaceAsync(string userId, string taskId, string? path, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public string DescribeConfig() => Unavailable;

    public Task<string> ReadSpansAsync(string userId, string? search, int limit, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public Task<string> StopAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => Task.FromResult(Unavailable);

    public Task<FleetLaunchResult> RerunAsync(
        string userId, string taskId, string? note, IReadOnlyList<string>? disableTools, long? maxTokens,
        CancellationToken cancellationToken = default)
        => Task.FromResult(new FleetLaunchResult(null, Unavailable));
}
