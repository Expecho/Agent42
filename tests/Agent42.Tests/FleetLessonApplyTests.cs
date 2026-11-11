using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>The planner gets the user's lessons at every depth (ADR 0124): where they enter, the structural event, the once-only callback.</summary>
public class FleetLessonApplyTests
{
    private static readonly FleetOptions NoReview = new() { ApprovalDepth = -1, JudgeDepth = -1 };
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 1, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<FleetLesson> Lessons =
    [
        new() { Id = "l1", Text = "Stage research before writing.", CreatedAt = Now, LastSupportedAt = Now },
        new() { Id = "l2", Text = "One review round is enough.", CreatedAt = Now, LastSupportedAt = Now },
    ];

    private static FakeChatClient.FleetScript Script(params object[] plans) => new(plans);

    [Fact]
    public async Task TopLevelPlanner_GetsTheLessonsInItsUserPrompt_NotItsSystemPrompt()
    {
        using var h = new FleetRunHarness(NoReview, lessons: Lessons);
        var script = Script(FleetRunHarness.Plan("Sequential", "Writer"));

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", script));

        var input = Assert.Single(script.PlannerInputs);
        Assert.Contains("Stage research before writing.", input);
        Assert.Contains("One review round is enough.", input);
        Assert.Contains("never a reason to add tools", input);
        Assert.DoesNotContain("Stage research", h.Runner.PlannerInstructions(h.Root));
        Assert.True(input.IndexOf("shared workspace", StringComparison.Ordinal) < input.IndexOf("Lessons from", StringComparison.Ordinal), "after the workspace line");
    }

    [Fact]
    public async Task ASubTeamPlanner_GetsThemToo()
    {
        using var h = new FleetRunHarness(NoReview, lessons: Lessons);
        var script = Script(FleetRunHarness.Plan("Sequential", "Researcher"));

        await h.Runner.DelegateToTeamAsync(FakeChatClient.FleetTask("a sub-task", script), h.Root with { TeamId = "team-0" });

        Assert.Contains("Stage research before writing.", Assert.Single(script.PlannerInputs));
        var applied = Assert.Single(h.Events.Of("lessons.applied"));
        Assert.Equal(1, applied.GetProperty("depth").GetInt32());
    }

    [Fact]
    public async Task AReplanAfterAFailedJudge_GetsThemAfterThePreviousAttemptNoteOrBefore_AndPublishesAgain()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 0, MaxAttempts = 2, MaxFixRounds = 0, PassingScore = 7 }, lessons: Lessons);
        var script = new FakeChatClient.FleetScript(
            [FleetRunHarness.Plan("Sequential", "Writer"), FleetRunHarness.Plan("Sequential", "Poet")], scores: [3, 9], agentReplies: ["r1", "r2"]);

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", script));

        Assert.Equal(2, script.PlannerInputs.Count);
        Assert.All(script.PlannerInputs, input => Assert.Contains("Stage research before writing.", input));
        Assert.Contains("did not deliver a good enough result", script.PlannerInputs.Last());
        Assert.Equal([1, 2], h.Events.Of("lessons.applied").Select(e => e.GetProperty("attempt").GetInt32()));
    }

    [Fact]
    public async Task LessonsApplied_CarriesIdsDepthAndTheBlocksSize()
    {
        using var h = new FleetRunHarness(NoReview, lessons: Lessons);

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", Script(FleetRunHarness.Plan("Sequential", "Writer"))));

        var applied = Assert.Single(h.Events.Of("lessons.applied"));
        Assert.Equal(["l1", "l2"], applied.GetProperty("ids").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(0, applied.GetProperty("depth").GetInt32());
        Assert.Equal(FleetLessons.PromptBlock(Lessons).Length, applied.GetProperty("chars").GetInt32());
    }

    [Fact]
    public async Task WithoutLessons_NothingIsAddedAndNothingIsPublished()
    {
        using var h = new FleetRunHarness(NoReview);
        var script = Script(FleetRunHarness.Plan("Sequential", "Writer"));

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", script));

        Assert.DoesNotContain("Lessons from", Assert.Single(script.PlannerInputs));
        Assert.Empty(h.Events.Of("lessons.applied"));
    }

    [Fact]
    public async Task TheCallback_RunsOnce_EvenWhenSeveralSubTeamPlannersStartAtOnce()
    {
        var calls = 0;
        using var h = new FleetRunHarness(NoReview, lessons: Lessons, onLessonsApplied: _ =>
        {
            Interlocked.Increment(ref calls);
            return Task.CompletedTask;
        });

        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => h.Runner.DelegateToTeamAsync(
            FakeChatClient.FleetTask($"sub-task {i}", Script(FleetRunHarness.Plan("Sequential", $"Agent{i}"))), h.Root with { TeamId = $"team-{i}" })));

        Assert.Equal(1, calls);
        Assert.Equal(4, h.Events.Of("lessons.applied").Count());
    }

    [Fact]
    public async Task AThrowingCallback_NeverFailsPlanning()
    {
        using var h = new FleetRunHarness(NoReview, lessons: Lessons, onLessonsApplied: _ => throw new IOException("store down"));
        var script = Script(FleetRunHarness.Plan("Sequential", "Writer"));

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku", script));

        Assert.Contains("help you today", result);
        Assert.Contains("Stage research before writing.", Assert.Single(script.PlannerInputs));
    }
}
