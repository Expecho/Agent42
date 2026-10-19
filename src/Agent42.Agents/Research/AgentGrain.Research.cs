using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;

namespace Agent42.Agents;

/// <summary>
/// The research loop (ADR 0082): a research-kind chat runs plan → search/read → reflect → answer, composed from
/// Agent Framework building blocks around the ordinary <see cref="ChatClientAgent"/> that
/// <see cref="BuildAgent"/> builds. <c>TodoProvider</c> holds the plan, <c>LoopAgent</c> re-runs the agent until
/// the todos are done and an AI judge agrees, and a chat-client reducer bounds what each model call sees.
///
/// <para><b>One user turn still produces ONE assistant turn, and none while it runs</b> — the invariant ADR 0046
/// stream recovery and ADR 0080 replay rest on. <c>LoopAgent</c> persists every finished iteration into the
/// session mid-turn, so this file hides the turn from <see cref="GetHistoryAsync"/> while it is in flight, and
/// folds it when it ends: only the last iteration that spoke keeps its text, every tool call stays.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// The todo provider of the CURRENT <see cref="_agent"/>, or null when that agent is not a research
    /// composition. Assigned with the agent by every <see cref="BuildAgent"/> (<see cref="ApplyComposition"/>), so it
    /// always describes the agent in use — it is the single switch the rest of this file reads.
    /// </summary>
    private TodoProvider? _researchTodos;

    /// <summary>
    /// The research run each in-flight turn belongs to, keyed by that turn's own user <see cref="ChatMessage"/>.
    /// The loop's observer evaluator finds its run through <c>LoopContext.InitialMessages</c>, which holds the
    /// very reference the grain passed in (measured). Keyed per run rather than a field, because two runs can be
    /// in flight on one activation (the <see cref="_turnCts"/> remarks). Not <c>AgentRunOptions</c>: its
    /// <c>AdditionalProperties</c> is copied into <c>ChatOptions.AdditionalProperties</c> of every model call
    /// (measured), which is a request body we do not want to decorate.
    /// </summary>
    private readonly ConditionalWeakTable<ChatMessage, ResearchRun> _researchRuns = new();

    /// <summary>
    /// The research turn currently hidden from <see cref="GetHistoryAsync"/>, or null. Published by identity like
    /// <see cref="_turnCts"/> — <c>??=</c> on the way in, cleared only if still ours — because it carries a running
    /// turn's identity, not a per-turn value: a blind assignment would let a reminder firing mid-stream unhide the
    /// chat's half-finished turn. It lives for the activation and is cleared at the end of each of the two run
    /// sites (<see cref="ChatCoreAsync"/> and <see cref="ReceiveReminder"/>).
    /// </summary>
    private ResearchRun? _researchInFlight;

    // True for the loop's own feedback messages, which the history provider must never store: they are model
    // input, not a user turn, and storing one would break the TurnAttachments 1:1 join, chat-search ordinals and
    // replay. Stored inconsistently by the framework (measured), so the filter is not optional.
    private static bool IsResearchLoopFeedback(ChatMessage message)
        => message.Role == ChatRole.User
            && string.Equals(message.AuthorName, ResearchLimits.LoopAuthorName, StringComparison.Ordinal);

    // The todo provider of the agent being built when it is a research composition, else null. A no-tools model
    // gets a plain agent: the todo tools are tools, so the gate that drops every other tool drops these.
    private TodoProvider? ResearchTodosFor(ModelCapabilities capabilities)
        => AgentKinds.IsResearch(_state.State.AgentKind) && capabilities.SupportsTools
            ? new TodoProvider()
            : null;

    // Adds the research composition's todo provider, when there is one.
    private static void AddResearchProviders(List<AIContextProvider> providers, TodoProvider? todos)
    {
        if (todos is not null)
        {
            providers.Add(todos);
        }
    }

#pragma warning disable MAAI001 // The compaction strategies and LoopAgent are [Experimental] in Agent Framework
    // 1.19 (ADR 0082 accepts the exposure); the scope is these two methods, nothing else.

    // Bounds what each model call sees. A reducer on the CHAT CLIENT, below the agent's function-invoking client, so
    // every call of the tool loop is bounded and RAG, memory and skills context all sit inside the budget. Not the
    // CompactionProvider: on 1.19 it froze the request, so from the second turn on the model was sent the first
    // question again and the real one was never stored (measured, ADR 0082). Stored history is untouched —
    // rule 5 truncation still bounds the session.
    private static IChatClient WithResearchBudget(IChatClient client, TodoProvider? todos)
        => todos is null
            ? client
            : new ChatClientBuilder(client)
                .UseChatReducer(new ContextWindowCompactionStrategy(
                    ResearchLimits.ContextWindowTokens, ResearchLimits.MaxOutputTokens).AsChatReducer())
                .Build();

    // Wraps the agent in the loop. The evaluators run in order and the first that asks for another iteration wins:
    // the observer never does (it only records the boundary), the todo evaluator keeps going while todos remain,
    // and only then is the judge asked. The judge calls the conversation's own client, so there is no new egress
    // (rule 13), but it is one extra call per finished iteration.
    private AIAgent WithResearchLoop(ChatClientAgent agent, string modelId, TodoProvider? todos)
        => todos is null
            ? agent
            : new LoopAgent(
                agent,
                [
                    new DelegateLoopEvaluator(ObserveResearchIteration),
                    new TodoCompletionLoopEvaluator(),
                    new AIJudgeLoopEvaluator(
                        _chatClientFactory.GetClient(modelId),
                        new AIJudgeLoopEvaluatorOptions { Criteria = [.. ResearchLimits.JudgeCriteria] }),
                ],
                new LoopAgentOptions
                {
                    MaxIterations = ResearchLimits.MaxIterations,
                    OnBehalfOfAuthorName = ResearchLimits.LoopAuthorName,
                    ExcludeOnBehalfOfMessages = true,
                    // The reminder path runs RunAsync; its result should be the answer, not every draft.
                    NonStreamingReturnsLastResponseOnly = true,
                });

    // Records that an iteration finished, and where it ended in the stored history. Runs after the iteration's
    // messages are persisted and before the next iteration's first update, which is what makes it a boundary.
    private ValueTask<LoopEvaluation> ObserveResearchIteration(LoopContext context, CancellationToken cancellationToken)
    {
        if (context.InitialMessages is [.., var sent]
            && _researchRuns.TryGetValue(sent, out var run)
            && context.Session is { } session
            && session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages))
        {
            run.Boundaries.Add(messages.Count);
        }

        return ValueTask.FromResult(LoopEvaluation.Stop());
    }
#pragma warning restore MAAI001

    // Starts a research run for this turn, or returns null for an ordinary agent. Resets the todo list — it lives in
    // the session, so without this a second question would inherit the first one's plan — and hides the turn from
    // history until it ends. Called by BOTH run sites; see _researchInFlight.
    private ResearchRun? BeginResearchRun(ChatMessage userMessage)
    {
        if (_researchTodos is null)
        {
            return null;
        }

        foreach (var key in _researchTodos.StateKeys)
        {
            _session.StateBag.TryRemoveValue(key);
        }

        // A brand-new session has no history state until its first run completes, which reads as "starts at 0".
        var run = new ResearchRun(
            _session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages) ? messages.Count : 0);
        _researchRuns.AddOrUpdate(userMessage, run);
        _researchInFlight ??= run;
        return run;
    }

    // Folds a finished, stopped or failed research run into one assistant turn. At most once per run (the guard is
    // the run's own flag): the fold removes messages, so the recorded boundaries are stale afterwards. A no-op for
    // an ordinary turn.
    private void FoldResearchRun(ResearchRun? run)
    {
        if (run is { Folded: false } && _session.TryGetInMemoryChatHistory(out List<ChatMessage>? messages))
        {
            run.Folded = true;
            FoldResearchTurn(messages, run);
        }
    }

    // Unhides a research run's turn, if it is still the one hidden — folding it first when asked, which is how a
    // `finally` covers a run that failed or was abandoned before the caller's own fold. An abandoned stream must not
    // leave history hidden for the activation.
    private void ReleaseResearchRun(ResearchRun? run, bool foldFirst = false)
    {
        if (foldFirst)
        {
            FoldResearchRun(run);
        }

        if (run is not null && ReferenceEquals(_researchInFlight, run))
        {
            _researchInFlight = null;
        }
    }

    // The history GetHistoryAsync may show: everything before a research turn that is still running. A plain turn
    // reaches history only when it ends, so this makes a research turn look the same to stream recovery (ADR 0046).
    private List<ChatMessage> VisibleHistory(List<ChatMessage> messages)
        => _researchInFlight is { } run && run.HistoryStart < messages.Count
            ? messages.GetRange(0, run.HistoryStart)
            : messages;

    /// <summary>
    /// Folds a finished research turn into one assistant answer: text is kept only on the assistant messages of
    /// the LAST iteration that produced text, and stripped from every earlier one. A message left with no parts
    /// is dropped, because providers reject an empty message. Tool calls and results are untouched, so ADR 0080
    /// replays every search of every iteration onto the one answer.
    /// </summary>
    private static void FoldResearchTurn(List<ChatMessage> messages, ResearchRun run)
    {
        var bounds = new List<int> { run.HistoryStart };
        bounds.AddRange(run.Boundaries.Where(b => b > run.HistoryStart && b < messages.Count));
        bounds.Add(messages.Count);

        var keep = LastSpeakingSegment(messages, bounds);
        if (keep < 0)
        {
            return;
        }

        var emptied = new HashSet<ChatMessage>(ReferenceEqualityComparer.Instance);
        for (var i = run.HistoryStart; i < messages.Count; i++)
        {
            if ((i < bounds[keep] || i >= bounds[keep + 1]) && StripAnswerText(messages[i]))
            {
                emptied.Add(messages[i]);
            }
        }

        messages.RemoveAll(emptied.Contains);
    }

    // The index of the last segment (between two consecutive bounds) holding assistant text, or -1.
    private static int LastSpeakingSegment(List<ChatMessage> messages, List<int> bounds)
    {
        for (var segment = bounds.Count - 2; segment >= 0; segment--)
        {
            for (var i = bounds[segment]; i < bounds[segment + 1]; i++)
            {
                if (messages[i].Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(messages[i].Text))
                {
                    return segment;
                }
            }
        }

        return -1;
    }

    // Removes the text parts of an assistant message, and says whether that left it empty. Only a message this
    // strip emptied is reported, so an unrelated, already-empty message is never swept up.
    private static bool StripAnswerText(ChatMessage message)
    {
        if (message.Role != ChatRole.Assistant || !message.Contents.Any(c => c is TextContent))
        {
            return false;
        }

        for (var i = message.Contents.Count - 1; i >= 0; i--)
        {
            if (message.Contents[i] is TextContent)
            {
                message.Contents.RemoveAt(i);
            }
        }

        return message.Contents.Count == 0;
    }

    // The events one streamed update produces on a research turn: a TextReset when a later iteration starts to
    // speak (its text supersedes the draft streamed so far, as the fold will), then the ordinary projection, then
    // the checklist when a tool result changed it. An ordinary turn gets the ordinary projection only. Async and
    // list-returning so StreamModelReplyAsync keeps one loop and stays under S3776.
    private async ValueTask<IEnumerable<AgentStreamEvent>> ProjectUpdateAsync(
        AgentResponseUpdate update, StringBuilder reply, TurnTokenTotals tokens, ResearchRun? run)
    {
        if (run is null)
        {
            return ProjectStreamedUpdate(update, reply, tokens);
        }

        var events = new List<AgentStreamEvent>();
        if (run.StartsNewAnswer(update))
        {
            reply.Clear();
            events.Add(new AgentStreamEvent(AgentStreamEventType.TextReset));
        }

        events.AddRange(ProjectStreamedUpdate(update, reply, tokens));
        if (update.Contents.Any(c => c is FunctionResultContent) && await TodosChangedAsync(run) is { } todos)
        {
            events.Add(new AgentStreamEvent(AgentStreamEventType.Todos, Todos: todos));
        }

        return events;
    }

    // The checklist, when it differs from the last one streamed on this run. Read between updates, when the todo
    // tool that changed it has returned and released the provider's lock (measured: no deadlock).
    private async Task<IReadOnlyList<ResearchTodo>?> TodosChangedAsync(ResearchRun run)
    {
        if (_researchTodos is null)
        {
            return null;
        }

        var todos = (await _researchTodos.GetAllTodosAsync(_session))
            .Select(t => new ResearchTodo(t.Id, t.Title, t.IsComplete))
            .ToList();
        var signature = string.Join('\n', todos.Select(t => $"{t.Id}:{t.Done}:{t.Title}"));
        if (signature == run.LastTodos)
        {
            return null;
        }

        run.LastTodos = signature;
        return todos;
    }

    /// <summary>
    /// One research turn's bookkeeping. <see cref="HistoryStart"/> is the history length before the turn, and
    /// <see cref="Boundaries"/> the length after each finished iteration, in order — together they cut the turn
    /// into iterations for <see cref="FoldResearchTurn"/>. <see cref="ReplyIteration"/> is the iteration the
    /// streamed <c>reply</c> text belongs to, which is how the stop path tells an unpersisted tail from text an
    /// already-persisted iteration holds.
    /// </summary>
    private sealed class ResearchRun(int historyStart)
    {
        public int HistoryStart { get; } = historyStart;

        public List<int> Boundaries { get; } = [];

        public int Completed => Boundaries.Count;

        public int ReplyIteration { get; private set; }

        public string? LastTodos { get; set; }

        public bool Folded { get; set; }

        // True when this update is the first text of an iteration later than the one `reply` holds.
        public bool StartsNewAnswer(AgentResponseUpdate update)
        {
            if (Completed == ReplyIteration || !update.Contents.Any(c => c is TextContent { Text.Length: > 0 }))
            {
                return false;
            }

            ReplyIteration = Completed;
            return true;
        }

        // True when anything of this turn is stored, i.e. at least its user message.
        public bool UserMessageStored(int historyCount) => historyCount > HistoryStart;

        // True when the text in `reply` came from an iteration that did not finish, so the session does not hold it:
        // its iteration is the current one, and nothing was stored since the last recorded boundary.
        public bool ReplyIsUnpersisted(int historyCount)
            => ReplyIteration == Completed && historyCount == (Completed > 0 ? Boundaries[^1] : HistoryStart);
    }
}
