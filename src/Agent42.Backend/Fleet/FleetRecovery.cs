using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>What a recovery takes from an interrupted task's log (ADR 0152): the team the user already approved (null
/// when no accepted plan survives) and the framed digest of what was done. <paramref name="AgentsDone"/> of
/// <paramref name="AgentsTotal"/> counts the plan's agents that finished.</summary>
public sealed record FleetRecoveryResult(FleetTeamPlan? Plan, string Digest, int AgentsDone, int AgentsTotal);

/// <summary>
/// Reads an interrupted, stopped or failed task's event log and builds the input a recovery starts from. Pure over the
/// log lines, so it is unit-tested against synthetic events. Works from structural events alone (<see cref="Types"/>), plus
/// each agent's last text when the log still holds it: text stops being persisted past <c>Fleet:MaxEventLogBytes</c>, and
/// the digest says so rather than pretend. It knows only the ROOT team: a sub-team's result is a tool result inside its
/// parent agent's run, which cannot be replayed (ADR 0152). Everything in the digest except the record's status is
/// untrusted (agent text, judge feedback, plan names), so each field is flattened to one line, capped, and the whole
/// digest is framed as data, like <see cref="FleetDigest"/>.
/// </summary>
public static class FleetRecovery
{
    /// <summary>The events recovery keeps; its own list, not <see cref="FleetDigest.StructuralTypes"/>, which lacks four of them.</summary>
    public static readonly IReadOnlyList<string> Types =
    [
        "team.started", "attempt.started", "plan.submitted", "plan.accepted", "agent.done", "attempt.result",
        "verdict", "todos.updated", "clarify.done", "agent.text",
    ];

    private const int FieldChars = 400;
    private const int ResultChars = 800;
    private const int AgentTextChars = 600;

    /// <summary>Reads the log and builds the recovery. A log that can't be read gives a digest without events, never a failure:
    /// the recovery then runs on the planner path with the workspace and the request.</summary>
    public static async Task<FleetRecoveryResult> ReadAsync(
        IFleetStore store, FleetTaskRecord task, int maxChars, ILogger logger, CancellationToken cancellationToken)
    {
        IReadOnlyList<string> lines = [];
        try
        {
            lines = await store.ReadEventsAsync(task.UserId, task.Id, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Ids only, never log lines.
            logger.LogWarning(ex, "Fleet recovery could not read the event log of task {TaskId}", task.Id);
        }

        return Build(task, lines, maxChars);
    }

    /// <summary>The recovery from raw log lines, the digest within <paramref name="maxChars"/>.</summary>
    public static FleetRecoveryResult Build(FleetTaskRecord task, IEnumerable<string> lines, int maxChars)
    {
        var events = FleetAnalysis.Parse(lines).Where(e => Types.Contains(e.Type)).ToList();
        var root = events.FirstOrDefault(e => e.Type == "team.started" && Text(e, "depth") == "0") is { } started ? Text(started, "teamId") : null;
        var mine = events.Where(e => e.Type == "clarify.done" || (root is not null && Text(e, "teamId") == root)).ToList();
        var submitted = AcceptedPlanEvent(mine);
        var plan = submitted is null ? null : ToPlan(submitted, task.Request);
        var attempt = submitted is null ? mine.Select(e => Text(e, "attempt")).LastOrDefault(a => a is not null) : Text(submitted, "attempt");
        var done = mine.Where(e => e.Type == "agent.done" && Text(e, "attempt") == attempt).Select(e => (Text(e, "agent") ?? "").ToLowerInvariant()).ToHashSet();
        var total = plan?.Agents.Count ?? 0;
        var finished = plan?.Agents.Count(a => done.Contains(a.Name.ToLowerInvariant())) ?? 0;

        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"The earlier task ended as {task.Status} after {task.Tokens:N0} tokens and {task.Agents} agents.")
            .Append(task.Reason is null ? "" : " Reason: " + Flat(task.Reason, FieldChars));
        AppendBrief(text, mine);
        AppendPlan(text, mine, submitted, plan, done, attempt);
        AppendProgress(text, mine);
        text.Append(events.Any(e => e.Type == "agent.text")
            ? ""
            : "\n(No agent output was kept in the log, only the structure above. Check the workspace files and the canvas for what was written.)");
        var body = text.Length <= maxChars ? text.ToString() : text.ToString(0, Math.Max(0, maxChars)) + " [cut]";
        return new FleetRecoveryResult(plan, FleetToolLimits.FrameAsData(body), finished, total);
    }

    // The last plan of the root team that was ACCEPTED (it passed validation, approval and the budget reservation). A plan that
    // was only submitted, because the task was interrupted on the approval prompt, is not one to reuse.
    private static FleetAnalysis.Event? AcceptedPlanEvent(List<FleetAnalysis.Event> mine)
    {
        var accepted = mine.Where(e => e.Type == "plan.accepted").Select(e => Text(e, "attempt")).ToHashSet();
        return mine.LastOrDefault(e => e.Type == "plan.submitted" && accepted.Contains(Text(e, "attempt")));
    }

    private static FleetTeamPlan? ToPlan(FleetAnalysis.Event e, string request)
    {
        if (!Enum.TryParse<FleetPattern>(Text(e, "pattern"), out var pattern) || e.Data?["agents"] is not JsonArray array)
        {
            return null;
        }

        var agents = array.OfType<JsonObject>()
            .Select(a => new FleetAgentDefinition { Name = a["name"]?.ToString() ?? "", Yaml = a["yaml"]?.ToString() ?? "" })
            .Where(a => a.Name.Length > 0 && a.Yaml.Length > 0)
            .ToList();
        if (agents.Count == 0 || agents.Count != array.Count)
        {
            return null;
        }

        return new FleetTeamPlan
        {
            Task = request,
            Rationale = Text(e, "rationale") ?? "",
            Pattern = pattern,
            Agents = agents,
            MaxGroupChatRounds = int.TryParse(Text(e, "maxGroupChatRounds"), CultureInfo.InvariantCulture, out var rounds) ? rounds : 0,
            TerminationCriteria = Text(e, "terminationCriteria") ?? "",
            Stages = e.Data?["stages"] is JsonArray stages
                ? [.. stages.OfType<JsonArray>().Select(stage => stage.Select(n => n?.ToString() ?? "").ToList())]
                : [],
        };
    }

    private static void AppendBrief(StringBuilder text, List<FleetAnalysis.Event> mine)
    {
        if (mine.LastOrDefault(e => e.Type == "clarify.done") is { } clarify && Text(clarify, "brief") is { Length: > 0 } brief)
        {
            text.Append("\n\nThe brief the user and the clarify phase agreed on:\n").Append(Flat(brief, ResultChars));
        }
    }

    private static void AppendPlan(StringBuilder text, List<FleetAnalysis.Event> mine, FleetAnalysis.Event? submitted, FleetTeamPlan? plan, HashSet<string> done, string? attempt)
    {
        if (plan is null || submitted is null)
        {
            return;
        }

        text.Append(CultureInfo.InvariantCulture, $"\n\nThe team that was running (attempt {attempt}): {Flat(plan.Pattern.ToString(), 30)}, {Flat(plan.Rationale, FieldChars)}");
        foreach (var agent in plan.Agents)
        {
            text.Append("\n- ").Append(Flat(agent.Name, 40)).Append(done.Contains(agent.Name.ToLowerInvariant()) ? ": finished" : ": did not finish");
            var last = mine.LastOrDefault(e => e.Type == "agent.text" && Text(e, "attempt") == attempt
                && string.Equals(Text(e, "agent"), agent.Name, StringComparison.OrdinalIgnoreCase));
            if (last is not null && Text(last, "text") is { Length: > 0 } output)
            {
                text.Append("; its last output: ").Append(Tail(Flat(output, int.MaxValue), AgentTextChars));
            }
        }
    }

    private static void AppendProgress(StringBuilder text, List<FleetAnalysis.Event> mine)
    {
        foreach (var e in mine.Where(e => e.Type is "attempt.result" or "verdict"))
        {
            text.Append(e.Type == "verdict"
                ? $"\nAttempt {Flat(Text(e, "attempt"), 6)} verdict: score {Flat(Text(e, "score"), 6)}, passed {Flat(Text(e, "passed"), 6)}. Feedback: {Flat(Text(e, "feedback"), FieldChars)}"
                : $"\nAttempt {Flat(Text(e, "attempt"), 6)} result: {Flat(Text(e, "result"), ResultChars)}");
        }

        if (mine.LastOrDefault(e => e.Type == "todos.updated")?.Data?["todos"] is JsonArray { Count: > 0 } todos)
        {
            text.Append("\nThe team's todo list when it stopped:");
            foreach (var todo in todos.OfType<JsonObject>())
            {
                text.Append(todo["done"]?.GetValue<bool>() == true ? "\n- [x] " : "\n- [ ] ").Append(Flat(todo["title"]?.ToString(), 200));
            }
        }
    }

    private static string? Text(FleetAnalysis.Event e, string name) => e.Data?[name]?.ToString();

    private static string Tail(string text, int max) => text.Length <= max ? text : "…" + text[^max..];

    // One line, capped: untrusted text can't fake a section or a closing tag across a line break.
    private static string Flat(string? text, int max)
    {
        var flat = string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return max == int.MaxValue ? flat : FleetAnalyzerLimits.Short(flat, max);
    }
}
