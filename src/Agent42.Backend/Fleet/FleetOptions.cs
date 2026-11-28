namespace Agent42.Backend;

/// <summary>
/// The agent fleet's limits and switches (ADR 0097), bound from the <c>Fleet</c> section. The defaults are the
/// engine's own constants. The task-level ones (depths, attempts) shape every task, and the caps (tokens, agents,
/// log and workspace size) bound what one task can cost or store. A user can override 25 of them for their own tasks
/// (ADR 0135, <see cref="FleetSettingsService"/>); the token caps, concurrency, size caps and kill switches stay the operator's.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

    /// <summary>How long a running task may publish no event, with no question open, before it pushes "gone quiet" (ADR 0154).
    /// Operator-only. <see cref="TimeSpan.Zero"/> turns it off.</summary>
    public TimeSpan StuckAfter { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>How deep teams nest: agents of a team below this depth get <c>delegate_to_team</c>.</summary>
    public int MaxTeamDepth { get; set; } = 6;

    /// <summary>Turns of a GroupChat or rounds of a Magentic team when the planner sets none.</summary>
    public int DefaultMaxGroupChatRounds { get; set; } = 60;

    /// <summary>Agents all teams of one task can spawn together.</summary>
    public int MaxAgentsPerTask { get; set; } = 40;

    /// <summary>The token cap the page proposes for a new task.</summary>
    public long DefaultMaxTokensPerTask { get; set; } = 2_000_000;

    /// <summary>The highest token cap a task may ask for. Every task has a cap, so there is no "no limit".</summary>
    public long MaxTokensPerTaskLimit { get; set; } = 10_000_000;

    /// <summary>Once this fraction of a budget is used, agents can no longer delegate to new teams.</summary>
    public double BudgetSoftLimit { get; set; } = 0.8;

    /// <summary>Sub-teams of one level that run at the same time.</summary>
    public int MaxConcurrentSubTeamsPerLevel { get; set; } = 6;

    /// <summary>Teams up to this depth ask the user to approve their plan; -1 turns approval off.</summary>
    public int ApprovalDepth { get; set; }

    /// <summary>Questions the clarify phase may ask the user about a task before it is planned (ADR 0118). Below 1 counts as 1.</summary>
    public int MaxClarifyingQuestions { get; set; } = 8;

    /// <summary>The most times the agent that writes a team's result runs in a self-review (ADR 0119), the first run
    /// included: 2 means one fix pass. Below 2 turns self-review off.</summary>
    public int SelfReviewMaxIterations { get; set; } = 2;

    /// <summary>Results of teams up to this depth are reviewed by a judge; -1 turns reviewing off.</summary>
    public int JudgeDepth { get; set; } = 1;

    /// <summary>Minimum judge score (1 to 10) for a result to pass.</summary>
    public int PassingScore { get; set; } = 7;

    /// <summary>Teams tried for a (sub-)task when the judge rejects the result; the best result is kept.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Times a team fixes its own rejected result before a new team is tried.</summary>
    public int MaxFixRounds { get; set; } = 1;

    /// <summary>How much of a rejected or earlier result a retry or follow-up gets, in characters.</summary>
    public int MaxRetryResultLength { get; set; } = 20_000;

    /// <summary>How much of a tool call's arguments or result an event carries, in characters.</summary>
    public int MaxToolTextLength { get; set; } = 4_000;

    /// <summary>Fleet tasks running at once over ALL users. Each user runs at most one.</summary>
    public int MaxConcurrentTasks { get; set; } = 2;

    /// <summary>Size of a task's persisted event log. Past it, streamed text is no longer persisted, but the
    /// structural events and the result still are.</summary>
    public long MaxEventLogBytes { get; set; } = 20 * 1024 * 1024;

    /// <summary>Bytes of workspace files synced to Blob per workspace.</summary>
    public long MaxWorkspaceBytes { get; set; } = 50 * 1024 * 1024;

    /// <summary>Workspace files synced to Blob per workspace.</summary>
    public int MaxWorkspaceFiles { get; set; } = 500;

    /// <summary>Files one task may attach (ADR 0101).</summary>
    public int MaxInputFiles { get; set; } = 10;

    /// <summary>Bytes of one attached file, and of one generated text copy. Chat's upload cap.</summary>
    public long MaxInputFileBytes { get; set; } = 10 * 1024 * 1024;

    /// <summary>The kill switch for Copilot fleet agents, which keep Copilot's built-in shell, file and URL tools
    /// in the container (ADR 0097 amends 0085). Off: the planner is not told about them, and a YAML that asks
    /// for one gets a load error.</summary>
    public bool CopilotAgentsEnabled { get; set; } = true;

    /// <summary>The catalog model id a task runs on when its request names none (ADR 0135). Empty: the user's catalog
    /// default. A stale id (the model left the catalog) falls back to that default instead of failing the task.</summary>
    public string DefaultModel { get; set; } = "";

    /// <summary>The user's catalog model id a plain <c>copilot</c> agent runs on. Empty: the task's model.</summary>
    public string DefaultCopilotModel { get; set; } = "";

    /// <summary>The operator kill switch for the nightly dream (ADR 0124). Off: no dream runs, manual ones included, and
    /// every user's reminder unregisters itself on its next tick. Lessons already learned still apply.</summary>
    public bool DreamEnabled { get; set; } = true;

    /// <summary>The UTC time of day the nightly dream window opens (<c>HH:mm</c>). 01:00 UTC is 02:00-03:00 in the
    /// Netherlands; there is no per-user timezone, so DST drift is accepted.</summary>
    public string DreamAtUtc { get; set; } = "01:00";

    /// <summary>Hours the dream window stays open. The reminder ticks hourly, so a missed tick costs at most an hour.
    /// Below 1 counts as 1.</summary>
    public int DreamWindowHours { get; set; } = 4;

    /// <summary>Finished tasks the dream reads, newest first. Below 1 counts as 1.</summary>
    public int DreamMaxTasks { get; set; } = 20;

    /// <summary>Fewer finished tasks than this and the dream makes no model call.</summary>
    public int DreamMinTasks { get; set; } = 3;

    /// <summary>Tasks that must have finished since the last run for a NIGHTLY run to start, so an idle night costs nothing.</summary>
    public int DreamMinNewTasks { get; set; } = 2;

    /// <summary>The digest's size in characters; the oldest tasks are dropped first.</summary>
    public int DreamMaxDigestChars { get; set; } = 30_000;

    /// <summary>The size in characters of the digest a recovery hands its agents (ADR 0152). Not a user setting.</summary>
    public int MaxRecoveryDigestChars { get; set; } = 12_000;

    /// <summary>Distinct workspaces whose tasks must support a lesson. Below 1 counts as 1.</summary>
    public int DreamMinSupport { get; set; } = 2;

    /// <summary>New lessons one run may add. Below 1 counts as 1.</summary>
    public int DreamMaxNewPerRun { get; set; } = 3;

    /// <summary>Characters of one lesson's text. Below 20 counts as 20.</summary>
    public int LessonMaxChars { get; set; } = 300;

    /// <summary>Lessons one planner call receives, newest first. Below 1 counts as 1.</summary>
    public int LessonsInPrompt { get; set; } = 8;

    /// <summary>Days a lesson lives after it was last supported. Below 1 counts as 1.</summary>
    public int LessonTtlDays { get; set; } = 90;

    /// <summary>Lessons stored per user, disabled tombstones excluded. Below 1 counts as 1.</summary>
    public int LessonsMaxStored { get; set; } = 200;

    /// <summary>The whole dream run's deadline, the digest reads included.</summary>
    public TimeSpan DreamTimeout { get; set; } = TimeSpan.FromMinutes(3);

    /// <summary>How often an idle event stream gets a comment line, so the ACA ingress does not drop it.</summary>
    public TimeSpan SseKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>A copy to apply one user's overrides to (ADR 0135): the options are a settable class, and the singleton
    /// the operator bound must never be mutated. Every member is a value type or a string, so a shallow copy is complete.</summary>
    public FleetOptions Clone() => (FleetOptions)MemberwiseClone();

    public static FleetOptions From(IConfiguration configuration)
        => configuration.GetSection(SectionName).Get<FleetOptions>() ?? new FleetOptions();
}
