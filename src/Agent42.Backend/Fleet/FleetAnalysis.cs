using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Turns a fleet task and its persisted event log into the text the analyzer tools hand the model (ADR 0122). Pure: no
/// I/O, so every shape is tested over plain strings. The record's own fields are ours; every event's data is model or
/// tool output, so the whole body goes inside <see cref="FleetToolLimits.WrapAsData"/> (capped, closing tag neutralized).
/// </summary>
internal static class FleetAnalysis
{
    // Events that say something went wrong. Verdicts, finished teams and tool results only count when they failed
    // (see IsFailureSignal); the others always do.
    private static readonly HashSet<string> AlwaysFailures = new(StringComparer.Ordinal)
    {
        "planner.failed", "attempt.failed", "agent.failed", "workflow.error", "plan.rejected", "delegation.refused",
        "selfreview.failed",
    };

    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal) { "agent.text", "agent.thinking" };

    /// <summary>One parsed event line.</summary>
    internal sealed record Event(long Seq, string Type, string Time, JsonNode? Data);

    /// <summary>The parsed events of a log; a line that isn't a JSON event is skipped.</summary>
    internal static IReadOnlyList<Event> Parse(IEnumerable<string> lines)
    {
        var events = new List<Event>();
        foreach (var line in lines)
        {
            try
            {
                if (JsonNode.Parse(line) is JsonObject o && o["type"]?.GetValue<string>() is { } type)
                {
                    events.Add(new Event(o["seq"]?.GetValue<long>() ?? 0, type, o["time"]?.ToString() ?? "", o["data"]));
                }
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                // A torn last line of an append blob: nothing to read there.
            }
        }

        return events;
    }

    /// <summary>The diagnosis of one task: header outside the frame, evidence inside it.</summary>
    internal static string Summarize(FleetTaskRecord record, IReadOnlyList<Event> events)
    {
        var header = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Fleet task {record.Id}: {record.Status}. ")
            .Append(CultureInfo.InvariantCulture, $"Tokens {record.Tokens:N0} of {record.MaxTokens:N0}. Agents {record.Agents}. Planner model {record.PlannerModel}.\n")
            .Append($"Page: {FleetToolLimits.TaskLink(record.Id)}\n");
        var body = new StringBuilder()
            .Append("Request: ").AppendLine(FleetAnalyzerLimits.Short(record.Request, 1_000))
            .Append("Reason: ").AppendLine(record.Reason ?? "(none)")
            .Append(Timing(record))
            .Append("Tool override: ").AppendLine(record.EnabledTools is { } tools ? string.Join(", ", tools) : "(the user's default selection)")
            .Append("Attached files: ").AppendLine(record.Inputs.Count.ToString(CultureInfo.InvariantCulture))
            .Append(OpenQuestion(events))
            .Append(Histogram(events))
            .Append(Failures(events))
            .Append(Plan(events))
            .Append(LastEvents(events));
        return header + FleetToolLimits.WrapAsData(body.ToString(), record.Id);
    }

    private static string Timing(FleetTaskRecord record)
        => $"Started {record.StartedAt:u}, " + (record.FinishedAt is { } finished ? $"finished {finished:u}" : "not finished") + "\n";

    // A task that waits on a question never completes by itself, which is the commonest quiet reason a run "can't complete".
    private static string OpenQuestion(IReadOnlyList<Event> events)
    {
        var last = events.LastOrDefault(e => e.Type is "prompt" or "prompt.answered" or "question.auto");
        return last is { Type: "prompt" }
            ? $"WAITING ON A QUESTION since seq {last.Seq}: {FleetAnalyzerLimits.Short(last.Data?.ToJsonString(), FleetAnalyzerLimits.EventLineChars)}\n"
            : "";
    }

    private static string Histogram(IReadOnlyList<Event> events)
    {
        if (events.Count == 0)
        {
            return "Events: none were persisted.\n";
        }

        var counts = events.GroupBy(e => e.Type, StringComparer.Ordinal)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Take(14).Select(g => $"{g.Key}={g.Count()}");
        return $"Events: {events.Count} (last seq {events[^1].Seq} at {events[^1].Time}). By type: {string.Join(", ", counts)}\n";
    }

    private static string Failures(IReadOnlyList<Event> events)
    {
        var signals = events.Where(IsFailureSignal).ToList();
        if (signals.Count == 0)
        {
            return "Failure signals: none.\n";
        }

        var text = new StringBuilder().Append($"Failure signals ({signals.Count}, newest last):\n");
        foreach (var signal in signals.TakeLast(FleetAnalyzerLimits.MaxFailureSignals))
        {
            text.AppendLine(Line(signal));
        }

        return text.ToString();
    }

    private static bool IsFailureSignal(Event e) => e.Type switch
    {
        "verdict" => e.Data?["passed"]?.GetValue<bool>() == false,
        "team.finished" => e.Data?["failed"]?.GetValue<bool>() == true,
        "tool.result" => e.Data?["failed"]?.GetValue<bool>() == true,
        "task.finished" => e.Data?["status"]?.ToString() is not "done",
        _ => AlwaysFailures.Contains(e.Type),
    };

    private static string Plan(IReadOnlyList<Event> events)
    {
        if (events.LastOrDefault(e => e.Type == "plan.submitted")?.Data is not JsonObject plan)
        {
            return "Plan: none was submitted.\n";
        }

        var names = plan["agents"] is JsonArray agents
            ? string.Join(", ", agents.Select(a => (a as JsonObject)?["name"]?.ToString() ?? a?.ToString()).Where(n => !string.IsNullOrEmpty(n)))
            : "";
        return $"Plan (latest): {plan["pattern"]}, agents [{names}], rationale: {FleetAnalyzerLimits.Short(plan["rationale"]?.ToString(), 300)}\n";
    }

    private static string LastEvents(IReadOnlyList<Event> events)
    {
        var recent = events.Where(e => !TextTypes.Contains(e.Type)).TakeLast(6).ToList();
        return recent.Count == 0
            ? ""
            : "Last events:\n" + string.Join('\n', recent.Select(Line)) + "\n";
    }

    private static string Line(Event e)
        => $"#{e.Seq} {e.Time} {e.Type} {FleetAnalyzerLimits.Short(e.Data?.ToJsonString(), FleetAnalyzerLimits.EventLineChars)}";

    /// <summary>One page of events with seq above <paramref name="afterSeq"/>, optionally only the comma-separated types.</summary>
    internal static string Page(IReadOnlyList<Event> events, string? types, long afterSeq, int limit, string taskId)
    {
        var wanted = (types ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var matching = events.Where(e => e.Seq > afterSeq && (wanted.Count == 0 || wanted.Contains(e.Type))).ToList();
        var page = matching.Take(limit).ToList();
        var next = matching.Count > page.Count ? $"More: call again with afterSeq={page[^1].Seq}." : "End of the log.";
        var body = page.Count == 0 ? "(no events match)" : string.Join('\n', page.Select(Line));
        return $"{page.Count} of {matching.Count} matching events (seq above {afterSeq}). {next}\n" + FleetToolLimits.WrapAsData(body, taskId);
    }
}
