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

    /// <summary>The task started with "ask me questions first" (ADR 0118): a clarify phase interviews the user about the
    /// request before the planner runs. Never set for a task started from a chat.</summary>
    public bool Clarify { get; init; }

    /// <summary>The task started with "self-review the result" (ADR 0119): the agent that writes a team's result checks it
    /// against the task and fixes gaps before the team's judge sees it. A record stored without the field reads false.
    /// Never set for a task started from a chat.</summary>
    public bool SelfReview { get; init; }

    /// <summary>The conversation that started this task through <c>start_fleet_task</c> (ADR 0115), or null for a task
    /// started on the page. A task with an origin approves its questions automatically and reports back to it.</summary>
    public string? OriginConversationId { get; init; }

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

    /// <summary>This task's tool selection (ADR 0021 keys, sanitized), overriding the user's DEFAULT chat selection,
    /// or null to follow that default when the run starts (ADR 0099). An empty list means no selectable tools.</summary>
    public IReadOnlyList<string>? EnabledTools { get; init; }

    /// <summary>The workspace notes (<c>note_set</c>) as this task left them, so a follow-up starts from them.</summary>
    public IReadOnlyDictionary<string, string> Notes { get; init; } = new Dictionary<string, string>();

    /// <summary>The file names uploaded or copied with THIS task, stored under <c>inputs/</c> in the workspace (ADR 0101).</summary>
    public IReadOnlyList<string> Inputs { get; init; } = [];

    /// <summary>The workspace's indexed document names, carried forward to a follow-up like <see cref="Notes"/>. The
    /// fleet's <c>search_documents</c> and <c>read_document</c> reach exactly these (ADR 0101).</summary>
    public IReadOnlyList<string> Documents { get; init; } = [];

    /// <summary>The workspace paths of the generated <c>.txt</c> copies of the documents, carried forward like
    /// <see cref="Documents"/>, so a user file that happens to end in <c>.txt</c> is never taken for one.</summary>
    public IReadOnlyList<string> TextCopies { get; init; } = [];

    /// <summary>The lessons the task's planners received (ADR 0124), recorded the first time one did. A record stored
    /// without the field reads empty.</summary>
    public IReadOnlyList<FleetAppliedLesson> AppliedLessons { get; init; } = [];

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
