using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>The digest the dream reads: its framed text, the tasks in it (each mapped to its workspace, so support is
/// counted over distinct workspaces) and how many tasks it holds.</summary>
public sealed record FleetDigestResult(string Text, IReadOnlyDictionary<string, string> TaskWorkspaces, int TasksRead);

/// <summary>
/// Builds the text the nightly dream reads (ADR 0124): one block per finished task with its request, each plan, each
/// verdict, the failures and the lessons it had applied. Built in code, not by an agent browsing tasks (ADR 0123 is the
/// rejected shape): one bounded pass, nothing to loop on.
/// <para>
/// Bounded reads: a log can be <c>Fleet:MaxEventLogBytes</c> (20 MB) and the store loads a whole blob, so the logs are
/// read ONE at a time, newest task first, only the structural events are kept, and reading stops once the digest is
/// full. Everything in a block except the record's own counters is untrusted (requests, judge feedback, plan names), so
/// each free-text field is flattened to one line and capped, and the whole digest goes inside the data frame.
/// </para>
/// </summary>
public static class FleetDigest
{
    /// <summary>The events the digest keeps; every other event type is dropped as soon as the log is parsed.</summary>
    public static readonly IReadOnlyList<string> StructuralTypes = ["plan.submitted", "verdict", "attempt.failed", "planner.failed", "task.finished"];

    /// <summary>Characters of one task's block.</summary>
    public const int MaxTaskChars = 3_000;

    private const int RequestChars = 600;
    private const int RationaleChars = 300;
    private const int FeedbackChars = 400;
    private const int FailureChars = 250;

    /// <summary>Whether a task counts as finished for the dream: it ended on its own, was stopped or failed. A running task
    /// and an interrupted one (no verdict, no result) carry no signal.</summary>
    public static bool IsFinished(FleetTaskRecord task)
        => task.Status is FleetTaskStatus.Done or FleetTaskStatus.Failed or FleetTaskStatus.Stopped;

    /// <summary>
    /// The digest of <paramref name="finishedNewestFirst"/> (at most <c>DreamMaxTasks</c>), within <c>DreamMaxDigestChars</c>:
    /// the newest tasks win and the oldest are dropped. Blocks read oldest first.
    /// </summary>
    public static async Task<FleetDigestResult> BuildAsync(IFleetStore store, string userId, IReadOnlyList<FleetTaskRecord> finishedNewestFirst,
        FleetOptions options, CancellationToken cancellationToken)
    {
        var budget = Math.Max(1_000, options.DreamMaxDigestChars);
        var blocks = new List<string>();
        var workspaces = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = 0;
        foreach (var task in finishedNewestFirst.Take(Math.Max(1, options.DreamMaxTasks)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var block = Block(task, await ReadStructuralAsync(store, userId, task.Id, cancellationToken), Math.Min(MaxTaskChars, budget));
            if (used > 0 && used + block.Length > budget)
            {
                break;
            }

            blocks.Add(block);
            workspaces[task.Id] = task.WorkspaceId;
            used += block.Length;
            if (used >= budget)
            {
                break;
            }
        }

        blocks.Reverse();
        return new FleetDigestResult(FleetToolLimits.FrameAsData(string.Join("\n\n", blocks)), workspaces, blocks.Count);
    }

    // The log is parsed and filtered here and then goes out of scope, before the next task's log is read.
    private static async Task<IReadOnlyList<FleetAnalysis.Event>> ReadStructuralAsync(IFleetStore store, string userId, string taskId, CancellationToken cancellationToken)
    {
        try
        {
            var lines = await store.ReadEventsAsync(userId, taskId, cancellationToken);
            return [.. FleetAnalysis.Parse(lines).Where(e => StructuralTypes.Contains(e.Type))];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A task whose log can't be read still carries its record; the block says so.
            return [];
        }
    }

    private static string Block(FleetTaskRecord task, IReadOnlyList<FleetAnalysis.Event> events, int maxChars)
    {
        var text = new StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Task {task.Id} (workspace {task.WorkspaceId}): {task.Status}, {task.Tokens:N0} tokens, {task.Agents} agents.")
            .Append(task.FollowUpOf is null ? "" : $" Follow-up of {Flat(task.FollowUpOf, 60)}.")
            .Append('\n').Append("Request: ").Append(Flat(task.Request, RequestChars))
            .Append('\n').Append("Reason: ").Append(Flat(task.Reason, FailureChars))
            .Append(task.AppliedLessons.Count == 0 ? "" : "\nLessons applied: " + string.Join(", ", task.AppliedLessons.Select(l => Flat(l.Id, 40))));
        foreach (var e in events)
        {
            var line = Line(e);
            if (line is not null)
            {
                text.Append('\n').Append(line);
            }
        }

        text.Append('\n').Append(Counts(events));
        return text.Length <= maxChars ? text.ToString() : text.ToString(0, maxChars) + " [cut]";
    }

    private static string? Line(FleetAnalysis.Event e) => e.Type switch
    {
        "plan.submitted" => PlanLine(e),
        "verdict" => VerdictLine(e),
        "attempt.failed" => $"attempt failed {Where(e)}: {Flat(e.Data?["reason"]?.ToString(), FailureChars)}",
        "planner.failed" => $"planner failed {Where(e)}: {Flat(e.Data?["error"]?.ToString(), FailureChars)}",
        _ => null,
    };

    private static string PlanLine(FleetAnalysis.Event e)
    {
        var names = e.Data?["agents"] is JsonArray agents
            ? string.Join(", ", agents.Select(a => Flat((a as JsonObject)?["name"]?.ToString(), 40)).Where(n => n.Length > 0))
            : "";
        return $"plan {Where(e)}: {Flat(e.Data?["pattern"]?.ToString(), 30)}, agents [{names}], rationale: {Flat(e.Data?["rationale"]?.ToString(), RationaleChars)}";
    }

    private static string VerdictLine(FleetAnalysis.Event e)
        => $"verdict {Where(e)}: score {Flat(e.Data?["score"]?.ToString(), 6)}, passed {Flat(e.Data?["passed"]?.ToString(), 6)}, next {Flat(e.Data?["next"]?.ToString(), 10)}. Feedback: {Flat(e.Data?["feedback"]?.ToString(), FeedbackChars)}";

    private static string Where(FleetAnalysis.Event e)
        => $"[team {Flat(e.Data?["teamId"]?.ToString(), 40)}, attempt {Flat(e.Data?["attempt"]?.ToString(), 6)}]";

    private static string Counts(IReadOnlyList<FleetAnalysis.Event> events)
    {
        var verdicts = events.Where(e => e.Type == "verdict").ToList();
        var fixes = verdicts.Count(v => v.Data?["next"]?.ToString() == "fix");
        var newTeams = verdicts.Count(v => v.Data?["next"]?.ToString() == "newTeam");
        return events.Count == 0
            ? "(no plan or verdict events were persisted for this task)"
            : $"Counts: {events.Count(e => e.Type == "plan.submitted")} plans, {verdicts.Count} verdicts ({(verdicts.Count == 0 ? "never judged" : "judged")}), {fixes} fix rounds, {newTeams} new teams, {events.Count(e => e.Type is "attempt.failed" or "planner.failed")} failures.";
    }

    // One line, capped: untrusted text can't fake a task block or a closing tag across a line break.
    private static string Flat(string? text, int max)
        => FleetAnalyzerLimits.Short(string.Join(' ', (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), max);
}
