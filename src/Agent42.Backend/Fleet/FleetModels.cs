using System.ComponentModel;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Agent42.Backend;

/// <summary>How the agents of a fleet team work together (ADR 0097).</summary>
public enum FleetPattern { Sequential, Concurrent, GroupChat, Handoff, Magentic, Staged }

/// <summary>A team the planner designed: its pattern and its agents as declarative YAML.</summary>
public sealed class FleetTeamPlan
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    public string Task { get; set; } = "";
    public string Rationale { get; set; } = "";
    public FleetPattern Pattern { get; set; }
    public int MaxGroupChatRounds { get; set; }
    public string TerminationCriteria { get; set; } = "";
    public List<List<string>> Stages { get; set; } = [];
    public List<FleetAgentDefinition> Agents { get; set; } = [];
}

/// <summary>One agent of a plan, as the planner submits it.</summary>
public sealed class FleetAgentDefinition
{
    [Description("PascalCase name of the agent, must match the name in the yaml.")]
    public string Name { get; set; } = "";

    [Description("The declarative agent definition in yaml.")]
    public string Yaml { get; set; } = "";
}

/// <summary>The judge's review of a team's result.</summary>
public sealed record FleetVerdict(int Score, bool Passed, string Feedback);

/// <summary>Where a team sits in its task: how deep, which team and which attempt. The ids tag every event.</summary>
public sealed record FleetTeamContext(int Depth, CancellationToken CancellationToken)
{
    public string? TeamId { get; init; }
    public int Attempt { get; init; }
}
