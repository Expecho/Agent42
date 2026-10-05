using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// Stopping a turn in flight (ADR 0078). The property under test is not "the stream ends" — a dead socket
/// already does that (ADR 0046) — it is that <b>the turn survives its own cancellation</b>: whatever had
/// streamed is written to the session as a normal turn, because Agent Framework writes nothing at all when
/// a run is cancelled (measured, 1.19: the session comes back EMPTY, not partial).
///
/// <para>Every test here parks the fake provider's stream at an exact boundary rather than racing its
/// 200 ms pacing — see <see cref="FakeChatClient.StreamGate"/>. The fake is a per-silo singleton and a
/// grain may activate on any silo, so a gate is opened on all of them and the first one to be reached is
/// the one that matters.</para>
/// </summary>
public sealed class StopGenerationTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // The fake streams its canned reply in three chunks, behind an echo PREFIX it adds to every turn (the
    // tool list, the system prompt, counters). So "it really stopped" cannot be an equality assertion on the
    // whole text — it is about the TAIL, which only a turn that ran to the end can carry, and the HEAD,
    // which the first chunk delivers.
    private const string ReplyHead = "Hello!";
    private const string ReplyTail = "help you today?";

    [Fact]
    public async Task Stop_PersistsThePartialReply()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-partial-conversation");
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "tell me a long story");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var history = await agent.GetHistoryAsync();
        var lastTurn = history[^1];
        Assert.NotEqual("user", lastTurn.Role);
        Assert.False(
            string.IsNullOrWhiteSpace(lastTurn.Text),
            "The text that had already streamed must survive the stop — the framework persists none of it.");
    }

    [Fact]
    public async Task Stop_PersistsLessThanTheWholeReply()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-shorter-conversation");
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "tell me a long story");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var text = (await agent.GetHistoryAsync())[^1].Text ?? "";
        Assert.Contains(ReplyHead, text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyTail, text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_KeepsTheUserMessageInHistory()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-keeps-prompt-conversation");
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "remember this prompt please");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && (t.Text ?? "").Contains("remember this prompt"));
    }

    [Fact]
    public async Task Stop_PartialReplyBecomesHistoryForTheNextTurn()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-then-continue-conversation");
        var gates = OpenStreamGates();

        var stopped = DrainAsync(agent, "first, tell me a long story");
        await ReachedAsync(gates);
        await agent.StopAsync();

        // The gate comes down before the second turn, or that turn parks on it and the test hangs rather
        // than fails — see FakeChatClient.CloseStreamGate.
        CloseStreamGates();
        await stopped;

        var partial = (await agent.GetHistoryAsync())[^1].Text;

        // The next turn runs ungated and completes normally. What it proves is that the stopped turn is
        // real history: the session round-tripped through SerializeSessionAsync with the partial reply in
        // it, so the conversation continues from a cancelled answer rather than losing it.
        await DrainAsync(agent, "and now the short version");

        var history = await agent.GetHistoryAsync();
        Assert.Equal(4, history.Count);
        Assert.Equal(partial, history[1].Text);
        Assert.DoesNotContain(ReplyTail, history[1].Text ?? "", StringComparison.Ordinal);
        Assert.Contains(ReplyTail, history[3].Text ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_BeforeAnyText_StoresTheUserMessageOnly()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-before-text-conversation");

        // Parked before the first chunk: the reply is still empty when the stop lands, which is what
        // happens in the real app when the user stops during a tool call.
        var gates = OpenStreamGates(afterChunks: 0);

        var turn = DrainAsync(agent, "stop me before I start");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var history = await agent.GetHistoryAsync();

        // The prompt survives (it is not retyped), and there is no empty assistant turn: an empty
        // assistant message would be sent back to the provider on every later turn, and some reject one.
        var single = Assert.Single(history);
        Assert.Equal("user", single.Role);
        Assert.Contains("stop me before I start", single.Text ?? "");
    }

    [Fact]
    public async Task Stop_AfterADelta_DoesNotGenerateSuggestions()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-no-suggestions-conversation");

        // After a delta, deliberately: stopping BEFORE any text would be gated by the pre-existing
        // empty-reply guard, so that version of this test would pass with the feature removed.
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "tell me a long story");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        Assert.False(
            string.IsNullOrWhiteSpace((await agent.GetHistoryAsync())[^1].Text),
            "Precondition: this test is only meaningful if the stopped turn kept some reply text.");
        Assert.Equal(0, TotalSuggestionCalls());
    }

    [Fact]
    public async Task Stop_StillEmitsDone_FlaggedStopped()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-done-event-conversation");
        var gates = OpenStreamGates();

        var events = new List<AgentStreamEvent>();
        var turn = DrainAsync(agent, "tell me a long story", events);
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        // A stop is an ending, not a failure: the stream terminates the same way a finished turn does, so
        // the client settles its caret, tool chips and reasoning disclosure on the normal path.
        var done = events[^1];
        Assert.Equal(AgentStreamEventType.Done, done.Type);
        Assert.True(done.Stopped);
    }

    [Fact]
    public async Task CompletedTurn_IsNotFlaggedStopped()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-unflagged-conversation");

        var events = new List<AgentStreamEvent>();
        await DrainAsync(agent, "hi", events);

        // Null rather than false, so the NDJSON writer omits it entirely — otherwise every per-token
        // delta of every turn would carry "stopped":false.
        Assert.Null(events[^1].Stopped);
        Assert.All(events, e => Assert.Null(e.Stopped));
    }

    [Fact]
    public async Task Stop_WithNoTurnInFlight_IsANoOp()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-idle-conversation");

        // Never throws, and says nothing: the client cannot know whether a turn is still running, and an
        // error here would only tempt it into retrying a race it cannot win.
        await agent.StopAsync();

        Assert.Empty(await agent.GetHistoryAsync());
    }

    [Fact]
    public async Task Stop_AfterTheTurnEnded_DoesNotAffectTheNextTurn()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-late-press-conversation");

        await DrainAsync(agent, "hi");

        // The handle is unpublished by identity when a turn ends, so a late press has nothing to cancel
        // and cannot reach into the turn that follows.
        await agent.StopAsync();

        var events = new List<AgentStreamEvent>();
        await DrainAsync(agent, "hi again", events);

        Assert.Null(events[^1].Stopped);
        Assert.Contains(ReplyTail, (await agent.GetHistoryAsync())[^1].Text ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_DoesNotCancelABackgroundTurn()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-background-conversation");
        var gates = OpenStreamGates();

        // An agent-run turn (ADR 0022) has no browser, so it never publishes its stop handle. Stopping
        // one is out of scope by design — cancelling a turn nobody is watching would lose it for no
        // one's benefit — and this is the test that keeps "out of scope" true rather than aspirational.
        await agent.EnqueuePromptAsync("run in the background", null);
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();

        var history = await WaitForTurnsAsync(agent, 2);
        Assert.Contains(ReplyTail, history[^1].Text ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Stop_OnAnEphemeralTurn_PersistsNothing()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-ephemeral-conversation");
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "tell me a long story", ephemeral: true);
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        // The stop branch appends the partial turn to the LIVE session — so it is there, in memory, and
        // the conversation can continue — while the incognito gate still skips the state write (ADR 0034).
        // Both halves matter: appending must not become a back door around the write it sits in front of.
        Assert.NotEmpty(await agent.GetHistoryAsync());

        // Proven by a round trip rather than by reading a storage provider: nothing was written, so the
        // reactivated grain has nothing to load. That also holds whichever silo the grain ran on.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.Empty(await agent.GetHistoryAsync());
    }

    // AC15 (ADR 0082): a stop in iteration 2 of a research loop, before iteration 2 produced anything. Iteration 1 is
    // already in the session — the user message included — so the hand-append must add NOTHING: no second user
    // message (which would shift every TurnAttachments join after it) and no second copy of iteration 1's text.
    [Fact]
    public async Task Stop_MidResearchLoop_KeepsHistoryConsistent()
    {
        const string key = "stop-mid-research-conversation";
        var agent = await ResearchChatAsync(key);
        var gates = OpenLoopGates();

        var turn = DrainAsync(agent, FakeChatClient.ResearchTrigger);
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseLoopGates();
        await turn;

        var history = await agent.GetHistoryAsync();
        Assert.Collection(
            history,
            user => Assert.Equal(FakeChatClient.ResearchTrigger, user.Text),
            answer =>
            {
                Assert.Contains(ReplyTail, answer.Text, StringComparison.Ordinal);
                Assert.DoesNotContain("[loopfeedback:", answer.Text, StringComparison.Ordinal);
                Assert.Equal(1, CountOf(answer.Text!, ReplyTail));
            });

        // It reopens, and the next turn lands right after it: the user count and the attachment join agree.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await DrainAsync(Cluster.GrainFactory.GetGrain<IAgentGrain>(key), "and a plain follow-up");
        var reopened = await Cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();
        Assert.Equal(["user", "assistant", "user", "assistant"], reopened.Select(t => t.Role));
    }

    // The same stop, but after iteration 2 has started its answer: that partial text is the unpersisted tail, so it
    // IS appended — and it replaces iteration 1's draft, as the live bubble did at the TextReset.
    [Fact]
    public async Task Stop_MidResearchAnswer_KeepsTheLaterIterationsTail()
    {
        var agent = await ResearchChatAsync("stop-mid-research-answer-conversation");
        var loopGates = OpenLoopGates();

        var turn = DrainAsync(agent, FakeChatClient.ResearchTrigger);
        await ReachedAsync(loopGates);
        var textGates = OpenStreamGates();
        CloseLoopGates();
        await ReachedAsync(textGates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var history = await agent.GetHistoryAsync();
        Assert.Equal(2, history.Count);
        Assert.StartsWith("[loopfeedback:1]", history[1].Text, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyTail, history[1].Text ?? "", StringComparison.Ordinal);
    }

    // AC6 (copilot): a stopped copilot turn is persisted like any other. The mirror writes nothing for a cancelled
    // run, the same as a ChatClientAgent, so the grain's hand-append is the one writer and the partial reply survives.
    [Fact]
    public async Task Stop_CopilotTurn_PersistsPartialReply()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("stop-copilot-conversation");
        await agent.BindAgentAsync(new AgentBinding(
            "copilot-agent", "Pilot", "You fly.", TestSiloConfigurator.DefaultModel, null, AgentKinds.Copilot));
        var gates = OpenStreamGates();

        var turn = DrainAsync(agent, "tell me a long story");
        await ReachedAsync(gates);
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var history = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant"], history.Select(t => t.Role));
        Assert.Contains(ReplyHead, history[1].Text ?? "", StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyTail, history[1].Text ?? "", StringComparison.Ordinal);
    }

    // --- helpers -------------------------------------------------------------------------------------

    private async Task<IAgentGrain> ResearchChatAsync(string key)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.BindAgentAsync(new AgentBinding(
            "research-agent", ResearchAgentDefaults.Name, ResearchAgentDefaults.SystemPrompt,
            TestSiloConfigurator.DefaultModel, ResearchAgentDefaults.EnabledTools, AgentKinds.Research));
        return agent;
    }

    private List<FakeChatClient.StreamGate> OpenLoopGates() =>
        [.. Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().OpenLoopGate())];

    private void CloseLoopGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>().CloseLoopGate();
        }
    }

    private static int CountOf(string text, string part)
        => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    // The fake is a per-silo singleton and the grain may activate on any silo, so every silo gets a gate.
    private List<FakeChatClient.StreamGate> OpenStreamGates(int afterChunks = 1) =>
        [.. Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().OpenStreamGate(afterChunks))];

    // Whichever silo ran the turn is the one whose gate was reached.
    private static Task ReachedAsync(List<FakeChatClient.StreamGate> gates) =>
        Task.WhenAny(gates.Select(g => g.Reached.Task));

    // Called right after every stop, and mandatory before any later turn in the same test: a gate left
    // installed parks the NEXT turn at the same boundary, which hangs the run instead of failing it.
    private void CloseStreamGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<FakeChatClient>().CloseStreamGate();
        }
    }

    private static async Task DrainAsync(
        IAgentGrain agent, string prompt, List<AgentStreamEvent>? events = null, bool ephemeral = false)
    {
        await foreach (var evt in agent.ChatAsync(prompt, null, ephemeral: ephemeral))
        {
            events?.Add(evt);
        }
    }

    private int TotalSuggestionCalls() =>
        Cluster.Silos.Sum(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().SuggestionCalls);

    // EnqueuePromptAsync is [OneWay], so the background turn finishes on its own schedule.
    private static async Task<IReadOnlyList<ChatTurn>> WaitForTurnsAsync(IAgentGrain agent, int count)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var history = await agent.GetHistoryAsync();
            if (history.Count >= count)
            {
                return history;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"The background turn never reached {count} history turns.");
        return [];
    }
}
