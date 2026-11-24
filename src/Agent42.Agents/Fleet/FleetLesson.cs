namespace Agent42.Agents;

/// <summary>
/// One lesson about fleet team design, distilled by the nightly dream from a user's recent finished tasks (ADR 0124).
/// Our own JSON in Blob (<c>{userId}/lessons.json</c>), flat namespace like the rest of the fleet (ADR 0039). Every
/// field after the required ones has a default, so an older document reads with defaults.
/// </summary>
public sealed record FleetLesson
{
    public required string Id { get; init; }
    public required string Text { get; init; }
    public string Topic { get; init; } = FleetLessonTopics.Other;
    public string Confidence { get; init; } = FleetLessonConfidence.Medium;

    /// <summary>The ids of the finished tasks that support it (cap 10).</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    public string Status { get; init; } = FleetLessonStatus.Active;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset LastSupportedAt { get; init; }

    /// <summary>The number of tasks a planner received this lesson for (once per task, not per planner call).</summary>
    public int AppliedCount { get; init; }

    /// <summary>The last 20 tasks it was applied in.</summary>
    public IReadOnlyList<FleetLessonApplication> Applications { get; init; } = [];
}

/// <summary>A task a lesson was applied in, and when.</summary>
public sealed record FleetLessonApplication(string TaskId, DateTimeOffset At);

/// <summary>The states of a <see cref="FleetLesson"/>. A disabled lesson stays as a tombstone so it is not re-proposed.</summary>
public static class FleetLessonStatus
{
    public const string Active = "active";
    public const string Disabled = "disabled";
}

/// <summary>The fixed set of lesson topics.</summary>
public static class FleetLessonTopics
{
    public const string TeamShape = "team-shape";
    public const string Staging = "staging";
    public const string Review = "review";
    public const string Scope = "scope";
    public const string Budget = "budget";
    public const string Other = "other";

    public static readonly IReadOnlyList<string> All = [TeamShape, Staging, Review, Scope, Budget, Other];
}

/// <summary>The fixed set of lesson confidences.</summary>
public static class FleetLessonConfidence
{
    public const string Low = "low";
    public const string Medium = "medium";
    public const string High = "high";

    public static readonly IReadOnlyList<string> All = [Low, Medium, High];
}

/// <summary>The user's dream settings. The toggle defaults OFF: nothing spends a user's tokens until they say so.</summary>
public sealed record FleetLessonSettings
{
    public bool Enabled { get; init; }

    /// <summary>The qualified <c>provider:model</c> id the dream runs on, or null for the catalog default.</summary>
    public string? Model { get; init; }

    public DateTimeOffset? LastRunAt { get; init; }

    /// <summary>When the user last opened the Lessons dialog; lessons created after it count for the badge.</summary>
    public DateTimeOffset? SeenAt { get; init; }
}

/// <summary>One dream run, nightly or manual, as shown in the dream log.</summary>
public sealed record FleetDreamRun
{
    public required string Id { get; init; }
    public string Trigger { get; init; } = FleetDreamTrigger.Nightly;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string Status { get; init; } = FleetDreamStatus.Running;
    public string? Reason { get; init; }
    public int TasksRead { get; init; }
    public int NewTasks { get; init; }
    public int Learned { get; init; }
    public int Reinforced { get; init; }
    public int Refused { get; init; }
    public string? Model { get; init; }
    public long? InputTokens { get; init; }
    public long? OutputTokens { get; init; }

    /// <summary>The prompt changes this run proposed and stored as pending (ADR 0143).</summary>
    public int Proposed { get; init; }
}

/// <summary>What started a <see cref="FleetDreamRun"/>.</summary>
public static class FleetDreamTrigger
{
    public const string Nightly = "nightly";
    public const string Manual = "manual";
}

/// <summary>The states of a <see cref="FleetDreamRun"/>.</summary>
public static class FleetDreamStatus
{
    public const string Running = "running";
    public const string Ran = "ran";
    public const string Skipped = "skipped";
    public const string Failed = "failed";

    /// <summary>Stored as running, but no longer in the in-process registry: the app stopped mid-run. Computed and
    /// persisted when the document is read, like a fleet task's (gotchas: "interrupted is computed on read").</summary>
    public const string Interrupted = "interrupted";
}

/// <summary>The whole <c>lessons.json</c> document of one user.</summary>
public sealed record FleetLessonsDocument
{
    public IReadOnlyList<FleetLesson> Lessons { get; init; } = [];
    public FleetLessonSettings Settings { get; init; } = new();

    /// <summary>The last 30 runs, newest first.</summary>
    public IReadOnlyList<FleetDreamRun> Runs { get; init; } = [];

    /// <summary>The prompt changes the dream proposed (ADR 0143): pending ones wait for the user's Approve or Dismiss, decided
    /// ones stay as tombstones so a dismissed change is not proposed again.</summary>
    public IReadOnlyList<FleetPromptProposal> PromptProposals { get; init; } = [];
}

/// <summary>
/// A targeted edit of one of the user's fleet prompts that the dream proposed (ADR 0143). Nothing changes until the user
/// approves it; approve re-applies the edit to the CURRENT prompt text, so the user's own later edits elsewhere survive.
/// </summary>
public sealed record FleetPromptProposal
{
    public required string Id { get; init; }
    public required string PromptKey { get; init; }
    public string Kind { get; init; } = FleetPromptProposalKind.Replace;

    /// <summary>The exact passage to replace (<see cref="FleetPromptProposalKind.Replace"/>), or null for an append.</summary>
    public string? Find { get; init; }

    public string Replacement { get; init; } = "";
    public string Rationale { get; init; } = "";

    /// <summary>The ids of the finished tasks that support it.</summary>
    public IReadOnlyList<string> Evidence { get; init; } = [];

    public string Status { get; init; } = FleetPromptProposalStatus.Pending;
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
}

/// <summary>The kinds of <see cref="FleetPromptProposal"/>.</summary>
public static class FleetPromptProposalKind
{
    public const string Replace = "replace";
    public const string Append = "append";

    public static readonly IReadOnlyList<string> All = [Replace, Append];
}

/// <summary>The states of a <see cref="FleetPromptProposal"/>.</summary>
public static class FleetPromptProposalStatus
{
    public const string Pending = "pending";
    public const string Approved = "approved";
    public const string Dismissed = "dismissed";

    /// <summary>The prompt changed so the edit no longer applies; found when the user tried to approve it.</summary>
    public const string Outdated = "outdated";
}

/// <summary>A lesson a task's planner received: its id and a text snapshot, so the task view stays right after the lesson is deleted.</summary>
public sealed record FleetAppliedLesson(string Id, string Text);
