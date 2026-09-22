using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Tests;

/// <summary>
/// The research loop's side of the fake (ADR 0082): the AI judge's verdict, and a scripted plan that spans TWO loop
/// iterations — <c>todos_add</c> → <c>web_search</c> → complete the first todo → answer (the todo evaluator sees one
/// left and re-runs), then complete the second → answer (the judge is asked and agrees).
/// </summary>
public sealed partial class FakeChatClient
{
    /// <summary>Prompt trigger for the two-iteration research script.</summary>
    public const string ResearchTrigger = "research the topic";

    /// <summary>Prompt trigger for a plan that is never finished, so only the iteration cap ends the turn.</summary>
    public const string ResearchNeverDoneTrigger = "research forever";

    /// <summary>
    /// Prompt marker the judge answers "not yet" to, whatever the todos say. With the research trigger it runs the
    /// loop to the cap after the plan is done, which is the judge-driven half of the stop rule.
    /// </summary>
    public const string JudgeUnsatisfiedMarker = "never satisfied";

    // The judge's system prompt opens with this (Agent Framework 1.19, measured).
    private const string JudgeSystemMarker = "You are an evaluator";

    private int _judgeCalls;

    private int _largestPromptChars;

    /// <summary>
    /// The most characters of message text and tool results one tool-bearing model call was sent since
    /// <see cref="Reset"/> — what the research budget (ADR 0082) bounds.
    /// </summary>
    public int LargestPromptChars => Volatile.Read(ref _largestPromptChars);

    /// <summary>Prompt trigger that schedules a reminder whose prompt is <see cref="ResearchTrigger"/>.</summary>
    public const string ScheduleResearchTrigger = "schedule a research run";

    private static FunctionCallContent BuildResearchReminderCall() =>
        new("research-reminder-call-1", "schedule_prompt", new Dictionary<string, object?>
        {
            ["name"] = "research-again",
            ["prompt"] = ResearchTrigger,
            ["dueInMinutes"] = 1.0,
            ["repeatEveryMinutes"] = 60.0,
        });

    // Records the size of a tool-bearing (agent) request, so a test can see the budget hold.
    private void RecordPromptSize(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        if (options?.Tools is not { Count: > 0 })
        {
            return;
        }

        var chars = messages.Sum(m => (m.Text?.Length ?? 0)
            + m.Contents.OfType<FunctionResultContent>().Sum(r => r.Result?.ToString()?.Length ?? 0));
        int seen;
        while (chars > (seen = Volatile.Read(ref _largestPromptChars))
            && Interlocked.CompareExchange(ref _largestPromptChars, chars, seen) != seen)
        {
        }
    }

    private StreamGate? _loopGate;

    /// <summary>
    /// Parks the first model call of a LATER loop iteration (one whose request carries the loop's feedback) before
    /// it streams anything: iteration 1 has finished and been persisted, iteration 2 has not produced a thing. That
    /// is the state the in-flight history rule and the stop-under-a-loop rule are about (ADR 0082). Taken down by
    /// <see cref="CloseLoopGate"/> and by <see cref="Reset"/>.
    /// </summary>
    public StreamGate OpenLoopGate()
    {
        var gate = new StreamGate(0);
        Volatile.Write(ref _loopGate, gate);
        return gate;
    }

    /// <summary>Releases the loop gate and takes it down, so later iterations and turns stream freely.</summary>
    public void CloseLoopGate()
    {
        var gate = Volatile.Read(ref _loopGate);
        Volatile.Write(ref _loopGate, null);
        gate?.Release.TrySetResult();
    }

    // Parks here when a loop gate is up and this request belongs to a later iteration. One-shot through Reached, not
    // by taking the gate down: the parked call must stay releasable by CloseLoopGate, and the parked iteration's own
    // follow-up calls pass once it has been reached.
    private async Task AwaitLoopGateAsync(IReadOnlyList<ChatMessage> messages, CancellationToken cancellationToken)
    {
        if (LoopFeedbackCount(messages) == 0
            || Volatile.Read(ref _loopGate) is not { } gate
            || !gate.Reached.TrySetResult())
        {
            return;
        }

        await gate.Release.Task.WaitAsync(cancellationToken);
    }

    /// <summary>How many times the AI judge was asked, across the silo; <see cref="Reset"/> zeroes it.</summary>
    public int JudgeCalls => Volatile.Read(ref _judgeCalls);

    // The judge's structured verdict (JudgeVerdict: answered + gapAnalysis), or null when this is not a judge call.
    private ChatResponse? JudgeVerdict(IEnumerable<ChatMessage> messages, ChatOptions? options)
    {
        var list = messages.ToList();
        var system = (options?.Instructions ?? "") + string.Concat(list.Where(m => m.Role == ChatRole.System).Select(m => m.Text));
        if (!system.Contains(JudgeSystemMarker, StringComparison.Ordinal))
        {
            return null;
        }

        Interlocked.Increment(ref _judgeCalls);
        var unsatisfied = list.Any(m => (m.Text ?? "").Contains(JudgeUnsatisfiedMarker, StringComparison.OrdinalIgnoreCase));
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, unsatisfied
            ? """{"answered":false,"gapAnalysis":"Dig deeper."}"""
            : """{"answered":true,"gapAnalysis":""}"""));
    }

    // The next call of the research script, or null to answer in text. Counts over every iteration of the turn,
    // which is why LastUserIndex skips the loop's own feedback messages.
    private static FunctionCallContent? NextResearchCall(
        IReadOnlyList<ChatMessage> list, int lastUserIndex, ChatOptions? options, string text)
    {
        // A plain agent (non-research chat, or a no-tools model) has no todo tools, so it just answers.
        if (options?.Tools?.Any(t => t.Name == "todos_add") is not true)
        {
            return null;
        }

        var adds = CountCallsThisTurn(list, lastUserIndex, "todos_add");
        if (text.Contains(ResearchNeverDoneTrigger, StringComparison.OrdinalIgnoreCase))
        {
            return adds == 0 ? TodosAdd("An endless step") : null;
        }

        var completes = CountCallsThisTurn(list, lastUserIndex, "todos_complete");
        return (adds, CountCallsThisTurn(list, lastUserIndex, "web_search"), completes, LoopFeedbackCount(list)) switch
        {
            (0, _, _, _) => TodosAdd("Find sources", "Summarise the findings"),
            (_, 0, _, _) => BuildWebSearchCall("the topic"),
            (_, _, 0, _) => TodosComplete(1),
            (_, _, 1, > 0) => TodosComplete(2),
            _ => null,
        };
    }

    /// <summary>
    /// Prompt marker that makes the model fail at the START of the loop's third iteration, after iterations 1 and 2
    /// have each stored an answer — the failure a research turn must still fold (ADR 0082). Pair it with
    /// <see cref="JudgeUnsatisfiedMarker"/>, or the loop ends after two iterations and never gets there.
    /// </summary>
    public const string ResearchFailLaterMarker = "then fail";

    private static void ThrowIfLaterIterationFails(IReadOnlyList<ChatMessage> list)
    {
        if (LastUserIndex(list) is >= 0 and var i
            && (list[i].Text ?? "").Contains(ResearchFailLaterMarker, StringComparison.OrdinalIgnoreCase)
            && CountCallsThisTurn(list, i, "todos_complete") == 2
            && list.LastOrDefault(m => !IsInjectedContext(m)) is { } last && IsLoopFeedback(last))
        {
            throw new InvalidOperationException("fake provider failure in a later research iteration");
        }
    }

    // How many of the loop's feedback messages this request carries. Only the current iteration's is ever present:
    // the history provider drops them before storing.
    private static int LoopFeedbackCount(IEnumerable<ChatMessage> messages)
        => messages.Count(IsLoopFeedback);

    private static bool IsLoopFeedback(ChatMessage message)
        => message.Role == ChatRole.User && message.AuthorName == ResearchLimits.LoopAuthorName;

    // Marks a reply written in a later loop iteration, so a test can tell the final answer from the draft.
    private static string LoopEcho(IEnumerable<ChatMessage> messages)
        => LoopFeedbackCount(messages) is > 0 and var n ? $"[loopfeedback:{n}] " : "";

    private static FunctionCallContent TodosAdd(params string[] titles) =>
        new($"todos-add-{Guid.NewGuid():N}", "todos_add", new Dictionary<string, object?>
        {
            ["todos"] = titles.Select(t => new Dictionary<string, object?> { ["title"] = t }).ToList(),
        });

    private static FunctionCallContent TodosComplete(int id) =>
        new($"todos-complete-{id}-{Guid.NewGuid():N}", "todos_complete", new Dictionary<string, object?>
        {
            ["items"] = new List<Dictionary<string, object?>> { new() { ["id"] = id, ["reason"] = "done" } },
        });
}
