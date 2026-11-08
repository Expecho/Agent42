using System.Diagnostics.Metrics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Everything one fleet task run needs (ADR 0097), built by the task service per task: the user's models (already
/// wrapped in the task's budget), the tools, the prompts, the budget, the workspace and where events and questions go.
/// Nothing here is shared between tasks.
/// </summary>
public sealed class FleetRun
{
    public required string TaskId { get; init; }
    public required FleetOptions Options { get; init; }
    public required FleetPromptSet Prompts { get; init; }
    public required FleetBudget Budget { get; init; }
    public required FleetWorkspace Workspace { get; init; }

    /// <summary>The task's shared canvas (ADR 0102), loaded from the store before the run starts.</summary>
    public required FleetCanvas Canvas { get; init; }

    /// <summary>The task's model: the planner, judge, moderator and Magentic manager run on it, and it is the default
    /// of agents whose YAML names none.</summary>
    public required string DefaultModel { get; init; }

    /// <summary>The user's tool-capable models by qualified id, each wrapped in <see cref="FleetBudgetChatClient"/>.</summary>
    public required IReadOnlyDictionary<string, IChatClient> Clients { get; init; }

    /// <summary>Display names of <see cref="Clients"/>, for the planner prompt.</summary>
    public IReadOnlyDictionary<string, string?> ModelNames { get; init; } = new Dictionary<string, string?>();

    /// <summary>
    /// The user's GitHub Copilot models (ADR 0108) by qualified id, with display names. They have no chat client: the
    /// planner is told they run only as <c>copilot/&lt;id&gt;</c>.
    /// </summary>
    public IReadOnlyDictionary<string, string?> CopilotModelNames { get; init; } = new Dictionary<string, string?>();

    /// <summary>The user's BYOK models (not GitHub's) the Copilot runtime can reach, by qualified id with display names.
    /// The planner is told they run as <c>copilot/&lt;id&gt;</c>.</summary>
    public IReadOnlyDictionary<string, string?> CopilotByokModelIds { get; init; } = new Dictionary<string, string?>();

    /// <summary>The catalog id a plain <c>copilot</c> resolves to (honours <c>Fleet:DefaultCopilotModel</c>), or null when unknown.</summary>
    public string? CopilotDefaultModel { get; init; }

    /// <summary>The catalog's tools (web, MCP), besides the workspace tools and <c>delegate_to_team</c>.</summary>
    public IReadOnlyList<AIFunction> Tools { get; init; } = [];

    /// <summary>Creates <c>copilot</c> agents, or null when the host has none. It returns null for any other model.</summary>
    public FleetCopilotAgentFactory? CopilotFactory { get; init; }

    /// <summary>Whether the planner is told about Copilot agents.</summary>
    public bool CopilotEnabled { get; init; }

    public required IFleetEventSink Events { get; init; }
    public required IFleetInteraction Interaction { get; init; }

    /// <summary>A task started from a chat (ADR 0115): the plan is approved without asking, and logged as a
    /// <c>question.auto</c> event. The Copilot policy and the interaction carry their own auto mode.</summary>
    public bool AutoApprove { get; init; }
    public required ILogger Logger { get; init; }

    /// <summary>Called after every attempt (a team's run), to sync the workspace to Blob.</summary>
    public Func<CancellationToken, Task>? AfterAttempt { get; init; }

    public IChatClient DefaultClient => Clients[DefaultModel];
}

/// <summary>The fleet's metric: agents spawned, on the app's own meter so the existing OpenTelemetry setup exports it.</summary>
public static class FleetMetrics
{
    private static readonly Meter Meter = new(LiveTelemetryCaptureExtensions.OtelSourceName);

    public static readonly Counter<long> AgentsSpawned =
        Meter.CreateCounter<long>("agents.spawned", unit: "{agent}", description: "Number of agents spawned by agent fleet tasks.");
}
