namespace Agent42.Backend;

/// <summary>
/// The agent fleet's limits and switches (ADR 0097), bound from the <c>Fleet</c> section. The defaults are the
/// engine's own constants. The task-level ones (depths, attempts) shape every task, and the caps (tokens, agents,
/// log and workspace size) bound what one task can cost or store.
/// </summary>
public sealed class FleetOptions
{
    public const string SectionName = "Fleet";

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

    /// <summary>The user's catalog model id a plain <c>copilot</c> agent runs on. Empty: the task's model.</summary>
    public string DefaultCopilotModel { get; set; } = "";

    /// <summary>How often an idle event stream gets a comment line, so the ACA ingress does not drop it.</summary>
    public TimeSpan SseKeepAlive { get; set; } = TimeSpan.FromSeconds(15);

    public static FleetOptions From(IConfiguration configuration)
        => configuration.GetSection(SectionName).Get<FleetOptions>() ?? new FleetOptions();
}
