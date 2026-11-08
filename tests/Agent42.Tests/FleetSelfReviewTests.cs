using System.Runtime.CompilerServices;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

#pragma warning disable MAAI001 // LoopAgent and its evaluators are [Experimental] in Agent Framework 1.25 (ADR 0082, ADR 0119)

namespace Agent42.Tests;

/// <summary>
/// Self-review for a fleet task (ADR 0119): which agents get wrapped in a <c>LoopAgent</c>, that the loop re-runs on a gap
/// and stops on "done" or at the cap, that a failing judge fails open while a stop still ends the run, and that the
/// re-runs and the judge count against the task budget.
/// </summary>
public sealed class FleetSelfReviewTests
{
    private const string More = AIJudgeLoopEvaluator.MoreVerdictMarker;
    private const string Done = AIJudgeLoopEvaluator.DoneVerdictMarker;

    private static FleetRunHarness Harness(bool selfReview = true, int cap = 2, int judgeDepth = 1, IChatClient? judge = null)
        => new(new FleetOptions { ApprovalDepth = -1, SelfReviewMaxIterations = cap, JudgeDepth = judgeDepth }, selfReview: selfReview, defaultClient: judge);

    private static AIAgent Agent(string name, IChatClient? client = null)
        => (client ?? new FakeChatClient()).AsAIAgent(new ChatClientAgentOptions
        {
            Name = name,
            Description = name,
            ChatOptions = new ChatOptions { Instructions = $"You are {name}." },
        });

    private static FleetTeamPlan Plan(FleetPattern pattern, params string[] names)
        => new() { Pattern = pattern, Agents = [.. names.Select(n => new FleetAgentDefinition { Name = n })] };

    private static readonly IReadOnlyDictionary<string, string> NoModels = new Dictionary<string, string>();

    [Fact]
    public void Enabled_NeedsTheFlag_TheCap_AndAJudgedDepth()
    {
        using var on = Harness();
        Assert.True(FleetSelfReview.Enabled(on.Run, 0));
        Assert.True(FleetSelfReview.Enabled(on.Run, 1));
        Assert.False(FleetSelfReview.Enabled(on.Run, 2));

        using var off = Harness(selfReview: false);
        using var cap1 = Harness(cap: 1);
        using var cap0 = Harness(cap: 0);
        using var unjudged = Harness(judgeDepth: -1);
        Assert.False(FleetSelfReview.Enabled(off.Run, 0));
        Assert.False(FleetSelfReview.Enabled(cap1.Run, 0));
        Assert.False(FleetSelfReview.Enabled(cap0.Run, 0));
        Assert.False(FleetSelfReview.Enabled(unjudged.Run, 0));
    }

    [Fact]
    public void Producers_AreTheResultWriters_ByPattern()
    {
        Assert.Equal(["B"], FleetSelfReview.Producers(Plan(FleetPattern.Sequential, "A", "B")));

        var staged = Plan(FleetPattern.Staged, "A", "B", "C");
        staged.Stages = [["A"], ["B", "C"]];
        Assert.Equal(["B", "C"], FleetSelfReview.Producers(staged).Order());

        foreach (var pattern in new[] { FleetPattern.Concurrent, FleetPattern.GroupChat, FleetPattern.Handoff, FleetPattern.Magentic })
        {
            Assert.Empty(FleetSelfReview.Producers(Plan(pattern, "A", "B")));
        }
    }

    [Fact]
    public void Apply_Off_ReturnsTheAgentsUntouched()
    {
        using var h = Harness(selfReview: false);
        List<AIAgent> agents = [Agent("A"), Agent("B")];

        var result = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "A", "B"), agents, NoModels, h.Root);

        Assert.Same(agents, result);
    }

    [Fact]
    public void Apply_Sequential_WrapsOnlyTheLastAgent()
    {
        using var h = Harness();
        var result = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "A", "B"), [Agent("A"), Agent("B")], NoModels, h.Root);

        Assert.IsNotType<LoopAgent>(result[0]);
        var looped = Assert.IsType<LoopAgent>(result[1]);
        Assert.Equal("B", looped.Name);
        Assert.NotNull(looped.GetService<ChatOptions>());
    }

    [Fact]
    public void Apply_Staged_WrapsTheLastStageOnly()
    {
        using var h = Harness();
        var plan = Plan(FleetPattern.Staged, "A", "B", "C");
        plan.Stages = [["A"], ["B", "C"]];

        var result = FleetSelfReview.Apply(h.Run, plan, [Agent("A"), Agent("B"), Agent("C")], NoModels, h.Root);

        Assert.Equal([false, true, true], result.Select(a => a is LoopAgent));
    }

    [Fact]
    public void Apply_OtherPatterns_StayUnwrapped()
    {
        using var h = Harness();
        foreach (var pattern in new[] { FleetPattern.Concurrent, FleetPattern.GroupChat, FleetPattern.Handoff, FleetPattern.Magentic })
        {
            var result = FleetSelfReview.Apply(h.Run, Plan(pattern, "A", "B"), [Agent("A"), Agent("B")], NoModels, h.Root);
            Assert.DoesNotContain(result, a => a is LoopAgent);
        }
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("Copilot/github-copilot:gpt-5")]
    public void Apply_CopilotAgent_StaysUnwrapped(string model)
    {
        using var h = Harness();
        var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["B"] = model };

        var result = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "A", "B"), [Agent("A"), Agent("B")], models, h.Root);

        Assert.DoesNotContain(result, a => a is LoopAgent);
    }

    [Fact]
    public void Apply_BeyondJudgeDepth_StaysUnwrapped()
    {
        using var h = Harness(judgeDepth: 1);

        var result = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "A"), [Agent("A")], NoModels, h.Root with { Depth = 2 });

        Assert.DoesNotContain(result, a => a is LoopAgent);
    }

    [Fact]
    public async Task Loop_ReRunsOnAGap_AndStopsWhenTheJudgeIsSatisfied()
    {
        var writer = new SequenceChatClient("draft one", "draft two", "draft three");
        using var h = Harness(cap: 3, judge: new SequenceChatClient($"{More} the ending is missing", Done));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", writer)], NoModels, h.Root with { TeamId = "team-1" })[0];

        var response = await agent.RunAsync("the task");

        Assert.Equal(2, writer.Calls);
        Assert.Equal("draft two", response.Text);
        var iteration = Assert.Single(h.Events.Of(FleetSelfReview.IterationEvent));
        Assert.Equal("team-1", iteration.GetProperty("teamId").GetString());
        Assert.Equal("Writer", iteration.GetProperty("agent").GetString());
        Assert.Equal(1, iteration.GetProperty("iteration").GetInt32());
    }

    [Fact]
    public async Task Loop_ADoneVerdict_RunsTheAgentOnce()
    {
        var writer = new SequenceChatClient("draft one", "draft two");
        using var h = Harness(judge: new SequenceChatClient(Done));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", writer)], NoModels, h.Root)[0];

        var response = await agent.RunAsync("the task");

        Assert.Equal(1, writer.Calls);
        Assert.Equal("draft one", response.Text);
        Assert.Empty(h.Events.Of(FleetSelfReview.IterationEvent));
    }

    [Theory]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    public async Task Loop_NeverRunsMoreOftenThanTheCap(int cap, int expectedRuns)
    {
        // A judge that always wants more (or gives no verdict at all) is bounded by the cap.
        var writer = new SequenceChatClient("a", "b", "c", "d", "e");
        using var h = Harness(cap: cap, judge: new SequenceChatClient(More));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", writer)], NoModels, h.Root)[0];

        await agent.RunAsync("the task");

        Assert.Equal(expectedRuns, writer.Calls);
    }

    /// <summary>A judge that answers without any verdict marker means "more" (measured), so only the cap bounds it.</summary>
    [Fact]
    public async Task Loop_AJudgeWithoutAVerdict_IsBoundedByTheCap()
    {
        var writer = new SequenceChatClient("a", "b", "c", "d", "e");
        using var h = Harness(cap: 3, judge: new SequenceChatClient("banana"));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", writer)], NoModels, h.Root)[0];

        await agent.RunAsync("the task");

        Assert.Equal(3, writer.Calls);
    }

    [Fact]
    public async Task Loop_AJudgeThatFails_KeepsTheResult()
    {
        var writer = new SequenceChatClient("draft one", "draft two");
        using var h = Harness(judge: new ThrowingChatClient(new InvalidOperationException("judge is down")));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", writer)], NoModels, h.Root)[0];

        var response = await agent.RunAsync("the task");

        Assert.Equal(1, writer.Calls);
        Assert.Equal("draft one", response.Text);
        var failed = Assert.Single(h.Events.Of(FleetSelfReview.FailedEvent));
        Assert.Contains("judge is down", failed.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Loop_AStop_IsNotSwallowedByTheJudgeGuard()
    {
        using var h = Harness(judge: new ThrowingChatClient(new OperationCanceledException()));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", new SequenceChatClient("draft one"))], NoModels, h.Root)[0];

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => agent.RunAsync("the task"));
        Assert.Empty(h.Events.Of(FleetSelfReview.FailedEvent));
    }

    [Fact]
    public async Task Loop_TheJudgeCountsAgainstTheTaskBudget()
    {
        using var h = Harness(cap: 3, judge: new SequenceChatClient(usage: 100, More, Done));
        var agent = FleetSelfReview.Apply(h.Run, Plan(FleetPattern.Sequential, "Writer"), [Agent("Writer", new SequenceChatClient("a", "b", "c"))], NoModels, h.Root)[0];

        await agent.RunAsync("the task");

        // Two judge calls of 100 input tokens each, through the budgeted default client.
        Assert.Equal(200, h.Budget.Tokens);
    }

    // The fake's structured AI judge (the research loop's, ADR 0082) answers "not answered" while the task text carries
    // FakeChatClient.JudgeUnsatisfiedMarker, and "answered" otherwise, so the marker drives the loop end to end.
    private static string UnsatisfiedTask => $"Write a haiku, {FakeChatClient.JudgeUnsatisfiedMarker}";

    [Fact]
    public async Task Runner_Sequential_TheTeamResultIsTheSecondDraft()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 }, selfReview: true);
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9],
            agentReplies: ["draft one", "draft two", "draft three"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask(UnsatisfiedTask, script));

        // The cap (2) stops a judge that is never satisfied after one fix pass; the feedback is never the result.
        Assert.Equal("draft two", result);
        Assert.Single(h.Events.Of(FleetSelfReview.IterationEvent));
        Assert.True(Assert.Single(h.Events.Of("verdict")).GetProperty("passed").GetBoolean());
        // Both drafts are the agent's own text; the loop's feedback to it never is (it would read as the agent's words).
        var texts = h.Events.Of("agent.text").Select(e => e.GetProperty("text").GetString()).ToList();
        Assert.Equal(["draft one", "draft two"], texts);
    }

    [Fact]
    public async Task Runner_ASatisfiedJudge_TheAgentRunsOnce()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 }, selfReview: true);
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9],
            agentReplies: ["draft one", "draft two"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", script));

        Assert.Equal("draft one", result);
        Assert.Empty(h.Events.Of(FleetSelfReview.IterationEvent));
    }

    /// <summary>A fix round re-runs the SAME wrapped agent: the loop must start clean and be capped again, or the cost
    /// multiplier in ADR 0119 is wrong (and the events carry no attempt because of it).</summary>
    [Fact]
    public async Task Runner_FixRound_ReRunsTheWrappedAgent_AndLoopsAgain()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 }, selfReview: true);
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [3, 9],
            agentReplies: ["draft 1", "draft 2", "draft 3", "draft 4", "draft 5"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask(UnsatisfiedTask, script));

        // Run 1: draft 1, then one fix pass (draft 2); the team judge scores 3 and the team fixes its result: draft 3, draft 4.
        Assert.Equal("draft 4", result);
        Assert.Equal(2, h.Events.Of(FleetSelfReview.IterationEvent).Count());
        Assert.Equal(2, script.JudgeCalls);
        Assert.Equal(2, h.Events.Of("attempt.result").Count());
    }

    /// <summary>The last stage of a Staged team with several agents is a Concurrent join: each looping agent contributes its
    /// last draft once, and never two drafts glued together.</summary>
    [Fact]
    public async Task Runner_StagedLastStage_JoinsTheLastDraftOfEachAgent()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 }, selfReview: true);
        var plan = new
        {
            rationale = "fits",
            pattern = "Staged",
            agents = new[] { "Researcher", "WriterA", "WriterB" }.Select(a => new { name = a, yaml = FakeChatClient.FleetAgentYaml(a) }).ToArray(),
            stages = new[] { new[] { "Researcher" }, new[] { "WriterA", "WriterB" } },
        };
        var script = new FakeChatClient.FleetScript([plan], scores: [9], agentReplies: ["x"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask(UnsatisfiedTask, script));

        Assert.Contains("[WriterA]: x", result);
        Assert.Contains("[WriterB]: x", result);
        Assert.DoesNotContain("xx", result);
        // Only the last stage loops: the researcher ran once, each writer looped once.
        Assert.Equal(["WriterA", "WriterB"], h.Events.Of(FleetSelfReview.IterationEvent).Select(e => e.GetProperty("agent").GetString()).Order());
    }

    [Fact]
    public async Task Runner_SelfReviewOff_TheAgentRunsOnce()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9],
            agentReplies: ["draft one", "draft two"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask(UnsatisfiedTask, script));

        Assert.Equal("draft one", result);
        Assert.Empty(h.Events.Of(FleetSelfReview.IterationEvent));
    }

    /// <summary>Replies in order and repeats the last one; counts calls; reports <c>usage</c> input tokens per call.</summary>
    private sealed class SequenceChatClient(long usage, params string[] replies) : IChatClient
    {
        private int _calls;

        public SequenceChatClient(params string[] replies) : this(0, replies)
        {
        }

        public int Calls => Volatile.Read(ref _calls);

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var reply = replies[Math.Min(Interlocked.Increment(ref _calls) - 1, replies.Length - 1)];
            var response = new ChatResponse(new ChatMessage(ChatRole.Assistant, reply));
            if (usage > 0)
            {
                response.Usage = new UsageDetails { InputTokenCount = usage, OutputTokenCount = 0 };
            }

            return Task.FromResult(response);
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            foreach (var update in response.ToChatResponseUpdates())
            {
                yield return update;
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class ThrowingChatClient(Exception exception) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw exception;

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw exception;

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
