using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// The research loop (ADR 0082), end to end through a real grain: the loop runs until the todos are done and the
/// judge agrees, never past the cap, and one user turn still becomes exactly one assistant turn — hidden while it
/// runs, folded to the last iteration that spoke when it ends.
///
/// <para>The fake's research script spans two iterations: <c>todos_add</c> → <c>web_search</c> → complete todo 1 →
/// answer, then complete todo 2 → answer. A reply written in a later iteration carries a <c>[loopfeedback:1]</c>
/// marker, which is how these tests tell the final answer from the draft.</para>
/// </summary>
public sealed class ResearchAgentTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string LaterIterationMarker = "[loopfeedback:1]";

    private IEnumerable<FakeChatClient> Fakes => Cluster.Silos
        .Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>());

    private int JudgeCalls => Fakes.Sum(f => f.JudgeCalls);

    // AC7: two iterations — the todo evaluator re-runs while one todo is open, then the judge is asked once and agrees.
    [Fact]
    public async Task ResearchTurn_LoopsUntilTodosDone()
    {
        var agent = await ResearchChatAsync("research-loops-until-done");

        var events = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        Assert.Equal(1, JudgeCalls);
        Assert.Single(events, e => e.Type == AgentStreamEventType.TextReset);
        Assert.Equal(2, events.Count(e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == "todos_complete"));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // AC8: a plan that is never finished stops at the cap, and so does a judge that is never satisfied.
    [Theory]
    [InlineData(FakeChatClient.ResearchNeverDoneTrigger, 0)]
    [InlineData(FakeChatClient.ResearchTrigger + " but " + FakeChatClient.JudgeUnsatisfiedMarker, ResearchLimits.MaxIterations - 2)]
    public async Task ResearchTurn_StopsAtIterationCap(string prompt, int expectedJudgeCalls)
    {
        var agent = await ResearchChatAsync($"research-cap-{expectedJudgeCalls}");

        var events = await CollectAsync(agent.ChatAsync(prompt, null));

        // Every iteration after the first starts a new answer, so the resets count the iterations exactly.
        Assert.Equal(ResearchLimits.MaxIterations - 1, events.Count(e => e.Type == AgentStreamEventType.TextReset));
        // The evaluators are not asked after the capped iteration, and the judge only once the todos are done.
        Assert.Equal(expectedJudgeCalls, JudgeCalls);
        Assert.Single(await agent.GetHistoryAsync(), t => t.Role == "assistant");
    }

    // AC9: an ordinary chat never gets the loop, even on the research prompt.
    [Fact]
    public async Task NonResearch_IsPlainAgent()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("research-plain-chat");

        var events = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        Assert.DoesNotContain("todos_add", Reply(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type is AgentStreamEventType.Todos or AgentStreamEventType.TextReset);
        Assert.Equal(0, JudgeCalls);
    }

    // AC9: the todo tools are tools, so a model that takes none gets a plain agent and no judge either.
    [Fact]
    public async Task NoToolsModel_IsPlainAgent()
    {
        var agent = await ResearchChatAsync("research-no-tools", TestSiloConfigurator.NoToolsNoVisionModel);

        var events = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        Assert.Contains("[tools:]", Reply(events), StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Todos);
        Assert.Equal(0, JudgeCalls);
    }

    // AC10: a research session moves to a plain agent (a no-tools model) and back without error, and each shape
    // still answers — the todo state left in the session is an orphan key the plain shape must ignore.
    [Fact]
    public async Task Session_CrossesAgentShapes()
    {
        var agent = await ResearchChatAsync("research-crosses-shapes");
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        var plain = await CollectAsync(agent.ChatAsync("hello plain", TestSiloConfigurator.NoToolsNoVisionModel));
        var back = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, TestSiloConfigurator.DefaultModel));

        Assert.Contains("help you today?", Reply(plain), StringComparison.Ordinal);
        Assert.Contains(back, e => e.Type == AgentStreamEventType.Todos);
        Assert.Equal(3, (await agent.GetHistoryAsync()).Count(t => t.Role == "assistant"));
    }

    // AC11 + AC12: while the loop runs, history shows nothing of the turn — not even iteration 1, which is already
    // stored. After it, exactly one user and one assistant turn: the final iteration's text, every tool call of every
    // iteration, and no loop feedback posing as a user turn.
    [Fact]
    public async Task ResearchTurn_HistoryHasOneAssistantTurn()
    {
        var agent = await ResearchChatAsync("research-one-assistant-turn");
        var gates = Fakes.Select(f => f.OpenLoopGate()).ToList();

        var turn = CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));
        await Task.WhenAny(gates.Select(g => g.Reached.Task)).WaitAsync(TimeSpan.FromSeconds(30));

        var during = await agent.GetHistoryAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Empty(during);

        foreach (var fake in Fakes)
        {
            fake.CloseLoopGate();
        }

        await turn;

        var history = await agent.GetHistoryAsync();
        Assert.Collection(
            history,
            user => Assert.Equal(FakeChatClient.ResearchTrigger, user.Text),
            answer =>
            {
                Assert.Equal("assistant", answer.Role);
                Assert.Contains(LaterIterationMarker, answer.Text, StringComparison.Ordinal);
                Assert.Equal(
                    ["todos_add", "web_search", "todos_complete", "todos_complete"],
                    answer.ToolCalls!.Select(c => c.ToolName));
            });
    }

    // A failure in iteration 3 surfaces like any failed turn, but iterations 1 and 2 already stored an answer each —
    // so the turn is still folded and unhidden: one user turn, ONE assistant turn (iteration 2's), and the
    // conversation carries on. Unfolded, the two stored answers would read as two assistant turns.
    [Fact]
    public async Task ResearchTurn_FailingInALaterIteration_StillFolds()
    {
        var agent = await ResearchChatAsync("research-fails-later");
        var prompt = $"{FakeChatClient.ResearchTrigger} {FakeChatClient.JudgeUnsatisfiedMarker} {FakeChatClient.ResearchFailLaterMarker}";

        await Assert.ThrowsAnyAsync<Exception>(() => CollectAsync(agent.ChatAsync(prompt, null)));

        var failed = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant"], failed.Select(t => t.Role));
        Assert.Contains(LaterIterationMarker, failed[1].Text, StringComparison.Ordinal);
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));
        Assert.Equal(["user", "assistant", "user", "assistant"], (await agent.GetHistoryAsync()).Select(t => t.Role));
    }

    // AC13: the live stream ends on the final iteration's text (the client clears the draft at the TextReset), and
    // the reply the follow-up helper is fed is that text too.
    [Fact]
    public async Task ResearchTurn_RepliesWithFinalIterationText()
    {
        var agent = await ResearchChatAsync("research-final-text");

        var events = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        var reset = events.FindIndex(e => e.Type == AgentStreamEventType.TextReset);
        var live = string.Concat(events.Skip(reset + 1).Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));
        Assert.StartsWith(LaterIterationMarker, live, StringComparison.Ordinal);
        Assert.Equal(live, (await agent.GetHistoryAsync())[^1].Text);

        var suggestionPrompt = Fakes.Select(f => f.LastSuggestionPrompt).Single(p => p is not null)!;
        Assert.Contains(LaterIterationMarker, suggestionPrompt, StringComparison.Ordinal);
    }

    // AC14: with a document attached and a history far larger than the budget, what the model sees stays bounded.
    // The reducer sits under the function-invoking client, so it sees the final request, RAG excerpts included.
    [Fact]
    public async Task ResearchPrompt_IsBoundedWithRag()
    {
        var agent = await ResearchChatAsync("research-bounded-rag");
        await agent.RegisterDocumentAsync("handbook.pdf");
        var budgetChars = (ResearchLimits.ContextWindowTokens - ResearchLimits.MaxOutputTokens) * 4;
        var longQuestion = FakeChatClient.ResearchTrigger + " "
            + string.Join(' ', Enumerable.Range(0, budgetChars / 20).Select(i => $"word{i}"));

        for (var turn = 0; turn < 3; turn++)
        {
            await CollectAsync(agent.ChatAsync(longQuestion, null));
        }

        // Sanity: three of those questions alone are well past the budget, so the bound is a real constraint.
        Assert.True(longQuestion.Length * 3 > budgetChars, "the history must exceed the budget, or this proves nothing");
        Assert.InRange(Fakes.Max(f => f.LargestPromptChars), 1, budgetChars);
    }

    // AC16: the todo list lives in the session, so a second question must start from an empty plan rather than
    // inherit the first one's — both from a chat turn and from a reminder.
    [Fact]
    public async Task SecondResearchTurn_StartsWithFreshTodos()
    {
        var agent = await ResearchChatAsync("research-fresh-todos");
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        var second = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        var first = second.First(e => e.Type == AgentStreamEventType.Todos).Todos!;
        Assert.Equal([1, 2], first.Select(t => t.Id));
        Assert.All(first, t => Assert.False(t.Done));
    }

    // The reminder path runs the loop through RunAsync, not ChatCoreAsync, so it has its own reset, hide and fold.
    [Fact]
    public async Task ReminderResearchTurn_ResetsTodos()
    {
        var agent = await ResearchChatAsync(
            "research-reminder-todos", tools: [ToolCatalog.WebSearch, ToolCatalog.Scheduling]);
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));
        await CollectAsync(agent.ChatAsync(FakeChatClient.ScheduleResearchTrigger, null));

        await agent.AsReference<IRemindable>().ReceiveReminder("research-again", default);

        // The scripted plan completes todos 1 and 2. Inherited todos would be 3 and 4 after a second add, so the
        // script's completions would miss and the loop would run to the cap. Finishing in two iterations, with
        // the judge asked once, is the observable proof that the plan started empty.
        var history = await agent.GetHistoryAsync();
        Assert.Equal(6, history.Count);
        Assert.Equal(FakeChatClient.ResearchTrigger, history[4].Text);
        Assert.Contains(LaterIterationMarker, history[5].Text, StringComparison.Ordinal);
        Assert.Equal(
            ["todos_add", "web_search", "todos_complete", "todos_complete"], history[5].ToolCalls!.Select(c => c.ToolName));
        Assert.Equal(3, JudgeCalls);
    }

    // AC17: a turn run to the cap stays far under the 2 MB session item. The loop adds no state of its own beyond the
    // tiny todo list (the reducer is stateless), so what remains is the rule 5 truncation of each tool result.
    [Fact]
    public async Task ResearchTurn_DoesNotBlowTheSessionSizeBudget()
    {
        var agent = await ResearchChatAsync("research-session-budget");

        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger + " but " + FakeChatClient.JudgeUnsatisfiedMarker, null));

        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", agent.GetGrainId(), state);

        Assert.InRange(System.Text.Encoding.UTF8.GetByteCount(state.State.SerializedSession ?? ""), 1, 256 * 1024);
    }

    // AC18: the checklist streams as the plan changes — added, then ticked one by one — and only when it changes.
    [Fact]
    public async Task ResearchTurn_StreamsTodoChecklist()
    {
        var agent = await ResearchChatAsync("research-checklist");

        var events = await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));

        var lists = events.Where(e => e.Type == AgentStreamEventType.Todos).Select(e => e.Todos!).ToList();
        Assert.Collection(
            lists,
            added => Assert.Equal([false, false], added.Select(t => t.Done)),
            one => Assert.Equal([true, false], one.Select(t => t.Done)),
            both => Assert.Equal([true, true], both.Select(t => t.Done)));
        Assert.Equal(["Find sources", "Summarise the findings"], lists[0].Select(t => t.Title));
    }

    // Surviving a reactivation: the kind is persisted, the session deserializes into the research shape, and the
    // folded turn reads back the same.
    [Fact]
    public async Task ResearchChat_SurvivesReactivation()
    {
        var agent = await ResearchChatAsync("research-reactivation");
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));
        var before = await agent.GetHistoryAsync();

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reopened = Cluster.GrainFactory.GetGrain<IAgentGrain>("research-reactivation");

        Assert.Equal(before.Select(t => t.Text), (await reopened.GetHistoryAsync()).Select(t => t.Text));
        var again = await CollectAsync(reopened.ChatAsync(FakeChatClient.ResearchTrigger, null));
        Assert.Contains(again, e => e.Type == AgentStreamEventType.Todos);
    }

    private async Task<IAgentGrain> ResearchChatAsync(
        string key, string model = TestSiloConfigurator.DefaultModel, IReadOnlyList<string>? tools = null)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.BindAgentAsync(new AgentBinding(
            "research-agent", ResearchAgentDefaults.Name, ResearchAgentDefaults.SystemPrompt, model,
            tools ?? ResearchAgentDefaults.EnabledTools, AgentKinds.Research));
        return agent;
    }

    private static string Reply(IEnumerable<AgentStreamEvent> events)
        => string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }
}
