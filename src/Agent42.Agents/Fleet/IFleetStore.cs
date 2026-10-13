namespace Agent42.Agents;

/// <summary>
/// Where the agent fleet keeps its state (ADR 0097): one Blob container, per user, with the tasks, their event logs,
/// the workspace files and the edited prompts. Every call is scoped by <c>userId</c>, so one user never reaches
/// another's data. Production is <see cref="FleetStore"/> over the Azure adapter, and DevHost and tests use it over
/// <see cref="InMemoryFleetBlobs"/>.
/// </summary>
public interface IFleetStore
{
    /// <summary>The user's tasks, newest first.</summary>
    Task<IReadOnlyList<FleetTaskRecord>> ListTasksAsync(string userId, CancellationToken cancellationToken = default);

    Task<FleetTaskRecord?> GetTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces a task, under its own <see cref="FleetTaskRecord.UserId"/>.</summary>
    Task SaveTaskAsync(FleetTaskRecord task, CancellationToken cancellationToken = default);

    /// <summary>Deletes a task and its event log. Its workspace goes too, unless another task of the user still uses
    /// it (a follow-up chain shares one workspace until its last task is deleted).</summary>
    Task DeleteTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default);

    /// <summary>Appends lines (each ending in a newline) to the task's event log.</summary>
    Task AppendEventsAsync(string userId, string taskId, string lines, CancellationToken cancellationToken = default);

    /// <summary>The task's event log, one JSON event per line, oldest first.</summary>
    Task<IReadOnlyList<string>> ReadEventsAsync(string userId, string taskId, CancellationToken cancellationToken = default);

    Task PutWorkspaceFileAsync(string userId, string workspaceId, string path, byte[] content, CancellationToken cancellationToken = default);

    /// <summary>The file's bytes, or null when it doesn't exist.</summary>
    Task<byte[]?> GetWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<FleetWorkspaceFile>> ListWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default);

    Task DeleteWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default);

    Task DeleteWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default);

    /// <summary>The user's edited system prompts by key; prompts not in it use their default.</summary>
    Task<IReadOnlyDictionary<string, string>> GetPromptsAsync(string userId, CancellationToken cancellationToken = default);

    Task SetPromptsAsync(string userId, IReadOnlyDictionary<string, string> prompts, CancellationToken cancellationToken = default);
}
