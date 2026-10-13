namespace Agent42.Agents;

/// <summary>
/// One agent fleet task as persisted in Blob (ADR 0097): the user's request, how it ended, and what a follow-up
/// needs (the result, the workspace, the notes, and the task it followed up). Our own JSON, not Orleans grain state,
/// but the type stays in the flat namespace anyway (ADR 0039).
/// </summary>
public sealed record FleetTaskRecord
{
    public required string Id { get; init; }
    public required string UserId { get; init; }
    public required string Request { get; init; }
    public string? FollowUpOf { get; init; }

    /// <summary>The workspace this task works in. A follow-up shares the workspace of the task it follows up.</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>The qualified <c>provider:model</c> id the planner, judge and moderators run on.</summary>
    public required string PlannerModel { get; init; }

    public string Status { get; init; } = FleetTaskStatus.Running;
    public string? Result { get; init; }

    /// <summary>Why a task stopped or failed.</summary>
    public string? Reason { get; init; }

    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public long Tokens { get; init; }
    public int Agents { get; init; }
    public long MaxTokens { get; init; }

    /// <summary>The workspace notes (<c>note_set</c>) as this task left them, so a follow-up starts from them.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();

    public bool CanFollowUp => Status == FleetTaskStatus.Done && Result is not null;
}

/// <summary>The states of a <see cref="FleetTaskRecord"/>.</summary>
public static class FleetTaskStatus
{
    public const string Running = "running";
    public const string Done = "done";
    public const string Stopped = "stopped";
    public const string Failed = "failed";

    /// <summary>Stored as running, but no longer in the in-process registry: the app restarted mid-task. It is
    /// computed and persisted when the task is read, and never resumed.</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>A file in a fleet workspace: its path relative to the workspace, with <c>/</c> separators.</summary>
public sealed record FleetWorkspaceFile(string Path, long Size);
