using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The fleet engine (ADR 0097) on <see cref="FakeChatClient"/>'s fleet script: planning, loading YAML, running every
/// pattern, judging, fixing, new teams, delegation limits, the token cap and plan approval.
/// </summary>
public class FleetTeamRunnerTests
{
    private static readonly FleetOptions NoReview = new() { ApprovalDepth = -1, JudgeDepth = -1 };

    private static string Task(params object[] plans) => FakeChatClient.FleetTask("Write a haiku about lighthouses", new(plans));

    [Fact]
    public async Task SequentialTeam_ProducesResult()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer", "Editor")], scores: [9]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        Assert.Contains("help you today", result);
        Assert.Equal(["team.started", "attempt.started", "plan.submitted", "plan.accepted", "agent.started"],
            h.Events.Events.Select(e => e.Type).Distinct().Take(5));
        var verdict = Assert.Single(h.Events.Of("verdict"));
        Assert.True(verdict.GetProperty("passed").GetBoolean());
        Assert.Equal(2, h.Budget.Agents);
        Assert.Contains(h.Events.Of("team.finished"), e => !e.GetProperty("failed").GetBoolean());
    }

    [Fact]
    public async Task Planner_MissingArgs_RejectedWithMessage()
    {
        using var h = new FleetRunHarness(NoReview);

        var result = await h.Runner.RunAsync(Task(new { rationale = "only this" }, FleetRunHarness.Plan("Sequential", "Writer")));

        var rejection = Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString();
        Assert.Contains("misses pattern, agents", rejection);
        Assert.Contains("help you today", result);
    }

    [Fact]
    public async Task InvalidYaml_ErrorsBackToPlanner()
    {
        using var h = new FleetRunHarness(NoReview);
        var broken = new { rationale = "x", pattern = "Sequential", agents = new[] { new { name = "Writer", yaml = "kind: Prompt\nname: [bad" } } };

        await h.Runner.RunAsync(Task(broken, FleetRunHarness.Plan("Sequential", "Writer")));

        var rejection = Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString();
        Assert.Contains("Writer:", rejection);
        Assert.Single(h.Events.Of("plan.accepted"));
    }

    [Fact]
    public async Task HostedToolKind_Rejected()
    {
        using var h = new FleetRunHarness(NoReview);
        var hosted = new
        {
            rationale = "x",
            pattern = "Sequential",
            agents = new[] { new { name = "Writer", yaml = "kind: Prompt\nname: Writer\ninstructions: x\ntools:\n  - kind: webSearch\n    name: web_search\n" } },
        };

        await h.Runner.RunAsync(Task(hosted, FleetRunHarness.Plan("Sequential", "Writer")));

        var rejection = Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString();
        Assert.Contains("hosted kind", rejection);
        Assert.Contains("web_search function", rejection);
    }

    [Fact]
    public async Task UnknownFunction_AndUnknownModel_Rejected()
    {
        using var h = new FleetRunHarness(NoReview);
        var bad = new
        {
            rationale = "x",
            pattern = "Sequential",
            agents = new[]
            {
                new { name = "Writer", yaml = FakeChatClient.FleetAgentYaml("Writer", tools: "launch_rockets") },
                new { name = "Editor", yaml = FakeChatClient.FleetAgentYaml("Editor", model: "claude-opus-5-5") },
            },
        };

        await h.Runner.RunAsync(Task(bad, FleetRunHarness.Plan("Sequential", "Writer")));

        var rejection = Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString();
        Assert.Contains("unknown function tool(s) launch_rockets", rejection);
        Assert.Contains("Unknown model 'claude-opus-5-5'", rejection);
    }

    [Fact]
    public async Task Planner_UsesUserCatalogModels()
    {
        using var h = new FleetRunHarness(NoReview);

        var instructions = h.Runner.PlannerInstructions(h.Root);
        Assert.Contains($"- {FleetRunHarness.ModelA} (Model A)", instructions);
        Assert.Contains($"- {FleetRunHarness.ModelB}", instructions);
        Assert.Contains($"runs on {FleetRunHarness.ModelA} by default", instructions);
        Assert.DoesNotContain("claude", instructions, StringComparison.OrdinalIgnoreCase);
        // No Copilot factory on this run, so the planner is not told about Copilot agents.
        Assert.DoesNotContain("GitHub Copilot", instructions);
        Assert.Contains("write_artifact", instructions);

        var onB = new
        {
            rationale = "x",
            pattern = "Sequential",
            agents = new[] { new { name = "Writer", yaml = FakeChatClient.FleetAgentYaml("Writer", model: FleetRunHarness.ModelB) } },
        };
        await h.Runner.RunAsync(Task(onB));

        var agent = Assert.Single(h.Events.Of("plan.submitted")).GetProperty("agents")[0];
        Assert.Equal(FleetRunHarness.ModelB, agent.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Judge_RejectsThenFixThenNewTeam_KeepsBest()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 0, MaxAttempts = 2, MaxFixRounds = 1, PassingScore = 7 });
        var script = new FakeChatClient.FleetScript(
            [FleetRunHarness.Plan("Sequential", "Writer"), FleetRunHarness.Plan("Sequential", "Poet")],
            scores: [3, 5, 4, 2],
            agentReplies: ["r1", "r2", "r3", "r4"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        Assert.Equal("r2", result);
        Assert.Equal(["fix", "newTeam", "fix", null], h.Events.Of("verdict").Select(v => v.GetProperty("next").GetString()));
        Assert.Equal(2, script.PlannerCalls);
        var fix = h.Events.Of("attempt.started").ElementAt(1);
        Assert.Equal(1, fix.GetProperty("fixOf").GetInt32());
    }

    [Fact]
    public async Task Judge_WithoutVerdict_Passes()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")]);

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        var verdict = Assert.Single(h.Events.Of("verdict"));
        Assert.True(verdict.GetProperty("passed").GetBoolean());
        Assert.Equal("The judge gave no verdict.", verdict.GetProperty("feedback").GetString());
    }

    /// <summary>ADR 0102: the canvas tools are agent functions like the workspace tools, so the planner lists them.</summary>
    [Fact]
    public void Planner_SeesCanvasTools()
    {
        using var h = new FleetRunHarness(NoReview);

        var functions = h.Runner.AgentFunctions(h.Root).Select(f => f.Name).ToList();
        var instructions = h.Runner.PlannerInstructions(h.Root);

        Assert.All(FleetCanvas.ToolNames, name =>
        {
            Assert.Contains(name, functions);
            Assert.Contains($"- {name}:", instructions);
        });
    }

    [Fact]
    public async Task Judge_InputIncludesCanvas()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 0, MaxAttempts = 1, PassingScore = 7 });
        await h.Canvas.Tools.Single(t => t.Name == "canvas_write").InvokeAsync(new()
        {
            ["name"] = "report",
            ["kind"] = "markdown",
            ["content"] = "# Lighthouse haiku",
            ["expectedVersion"] = 0,
        });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9]);

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        var input = Assert.Single(script.JudgeInputs);
        Assert.Contains("Pages on the canvas (1):", input);
        Assert.Contains("--- report (markdown, \"report\", version 1)\n# Lighthouse haiku", input);
    }

    /// <summary>ADR 0117: AgentFunctions runs twice per attempt (planner listing, agent factory), so the list is memoized.</summary>
    [Fact]
    public void TodosFor_SameAttemptSameList()
    {
        using var h = new FleetRunHarness(NoReview);
        var team1 = h.Root with { TeamId = "team-1", Attempt = 1 };

        var first = h.Runner.AgentFunctions(team1).Where(f => FleetTodos.ToolNames.Contains(f.Name)).ToList();
        var second = h.Runner.AgentFunctions(team1).Where(f => FleetTodos.ToolNames.Contains(f.Name)).ToList();

        Assert.Equal(FleetTodos.ToolNames.Count, first.Count);
        Assert.Same(h.Runner.TodosFor(team1), h.Runner.TodosFor(team1));
        Assert.Equal(first, second);
        Assert.NotSame(h.Runner.TodosFor(team1), h.Runner.TodosFor(team1 with { Attempt = 2 }));
        Assert.NotSame(h.Runner.TodosFor(team1), h.Runner.TodosFor(team1 with { TeamId = "team-2" }));
    }

    [Fact]
    public void TodoTools_NeedATeam_AndAreListedForThePlanner()
    {
        using var h = new FleetRunHarness(NoReview);

        Assert.Null(h.Runner.TodosFor(h.Root));
        Assert.DoesNotContain(h.Runner.AgentFunctions(h.Root), f => f.Name == "todo_add");
        var instructions = h.Runner.PlannerInstructions(h.Root with { TeamId = "team-1", Attempt = 1 });
        Assert.All(FleetTodos.ToolNames, name => Assert.Contains($"- {name}:", instructions));
        Assert.Contains("The todo functions", instructions); // the default planner prompt says who should get them
    }

    [Fact]
    public async Task JudgeInput_IncludesOpenTodos()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 0, MaxAttempts = 1, PassingScore = 7 });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9]);
        // The root team is "team-1" and its first attempt 1: plan the list before the run, as an agent would during it.
        var todos = h.Runner.TodosFor(h.Root with { TeamId = "team-1", Attempt = 1 })!;
        await todos.Tools.Single(t => t.Name == "todo_add").InvokeAsync(new() { ["titles"] = new[] { "Draft the haiku", "Check the syllables" } });
        await todos.Tools.Single(t => t.Name == "todo_complete").InvokeAsync(new() { ["ids"] = new[] { 1 } });

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        var input = Assert.Single(script.JudgeInputs);
        Assert.Contains("1 open of 2", input);
        Assert.Contains("- [ ] Check the syllables", input);
    }

    [Fact]
    public async Task JudgeInput_HasNoTodoSection_WhenNoneWerePlanned()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 0, MaxAttempts = 1, PassingScore = 7 });
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9]);

        await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script));

        Assert.DoesNotContain("todo list", Assert.Single(script.JudgeInputs));
    }

    [Fact]
    public void Delegate_AbsentAtMaxDepth()
    {
        using var h = new FleetRunHarness(new FleetOptions { MaxTeamDepth = 2 });

        Assert.Contains(h.Runner.AgentFunctions(h.Root with { Depth = 1 }), f => f.Name == "delegate_to_team");
        Assert.DoesNotContain(h.Runner.AgentFunctions(h.Root with { Depth = 2 }), f => f.Name == "delegate_to_team");
        Assert.DoesNotContain("delegate_to_team", h.Runner.PlannerInstructions(h.Root with { Depth = 2 }));
    }

    // AC13 (ADR 0104): cached input is counted on its own and is NOT added to Tokens — it is a subset of input, so
    // adding it would make the budget limit count it twice.
    [Fact]
    public void AddTokens_CountsCachedSeparately()
    {
        using var budget = new FleetBudget(1_000, 5, 0.8, 1, 2);

        budget.AddTokens(new() { InputTokenCount = 600, OutputTokenCount = 50, CachedInputTokenCount = 500 });

        Assert.Equal((600L, 50L, 500L, 650L), (budget.InputTokens, budget.OutputTokens, budget.CachedInputTokens, budget.Tokens));
        Assert.False(budget.IsCancelled);
    }

    [Fact]
    public async Task Delegate_RefusedAtSoftLimit()
    {
        using var h = new FleetRunHarness(new FleetOptions { BudgetSoftLimit = 0.8 }, maxTokens: 100);
        h.Budget.AddTokens(new() { InputTokenCount = 85 });

        var reply = await h.Runner.DelegateToTeamAsync("a sub-task", h.Root with { TeamId = "team-1" });

        Assert.StartsWith("No team was created", reply);
        Assert.Single(h.Events.Of("delegation.refused"));
        Assert.Empty(h.Events.Of("team.started"));
    }

    [Fact]
    public async Task Delegate_RunsSubTeamOneLevelDeeper()
    {
        using var h = new FleetRunHarness(NoReview);
        var task = Task(FleetRunHarness.Plan("Sequential", "Researcher"));

        var reply = await h.Runner.DelegateToTeamAsync(task, h.Root with { TeamId = "team-0" });

        Assert.Contains("help you today", reply);
        var started = Assert.Single(h.Events.Of("team.started"));
        Assert.Equal(1, started.GetProperty("depth").GetInt32());
        Assert.Equal("team-0", started.GetProperty("parentTeamId").GetString());
    }

    [Fact]
    public async Task TokenCap_StopsTask()
    {
        using var h = new FleetRunHarness(NoReview, maxTokens: 50);
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")], usagePerCall: 100);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", script)));

        Assert.True(h.Budget.IsCancelled);
        Assert.Contains("token budget of 50 tokens is used up", h.Budget.CancelReason);
    }

    [Theory]
    [InlineData("Concurrent")]
    [InlineData("GroupChat")]
    [InlineData("Handoff")]
    [InlineData("Magentic")]
    [InlineData("Staged")]
    public async Task BuildWorkflow_EachPattern_Runs(string pattern)
    {
        using var h = new FleetRunHarness(NoReview);
        var plan = new
        {
            rationale = "fits",
            pattern,
            maxGroupChatRounds = 4,
            agents = new[] { "Alpha", "Beta" }.Select(a => new { name = a, yaml = FakeChatClient.FleetAgentYaml(a) }).ToArray(),
            stages = new[] { new[] { "Alpha" }, new[] { "Beta" } },
        };

        var result = await h.Runner.RunAsync(Task(plan));

        Assert.False(string.IsNullOrWhiteSpace(result));
        Assert.Single(h.Events.Of("attempt.result"));
        Assert.Contains(h.Events.Of("agent.started"), e => true);
        if (pattern == "Staged")
        {
            Assert.Equal(2, h.Events.Of("stage.started").Count());
        }
    }

    [Fact]
    public async Task Approval_Change_ThenRun()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = 0, JudgeDepth = -1 }, answers: ["use a poet instead", "y"]);

        var result = await h.Runner.RunAsync(Task(FleetRunHarness.Plan("Sequential", "Writer"), FleetRunHarness.Plan("Sequential", "Poet")));

        Assert.Equal(2, h.Interaction.Questions.Count);
        Assert.Contains("use a poet instead", Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString());
        Assert.All(h.Events.Of("plan.submitted"), e => Assert.True(e.GetProperty("needsApproval").GetBoolean()));
        Assert.Single(h.Events.Of("plan.accepted"));
        Assert.Contains("help you today", result);
    }

    [Fact]
    public async Task Approval_No_StopsTask()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = 0 }, answers: ["n"]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => h.Runner.RunAsync(Task(FleetRunHarness.Plan("Sequential", "Writer"))));

        Assert.Equal("stopped by the user", h.Budget.CancelReason);
        Assert.Empty(h.Events.Of("plan.accepted"));
    }

    [Fact]
    public async Task AgentBudget_TooBigTeam_Rejected()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1, MaxAgentsPerTask = 1 });

        await h.Runner.RunAsync(Task(FleetRunHarness.Plan("Sequential", "Writer", "Editor"), FleetRunHarness.Plan("Sequential", "Writer")));

        Assert.Contains("only 1 agent(s) are left", Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString());
        Assert.Equal(1, h.Budget.Agents);
    }

    [Fact]
    public async Task NoPlan_TeamFails()
    {
        using var h = new FleetRunHarness(NoReview);

        var result = await h.Runner.RunAsync(Task());

        Assert.Null(result);
        Assert.Single(h.Events.Of("attempt.failed"));
        Assert.True(Assert.Single(h.Events.Of("team.finished")).GetProperty("failed").GetBoolean());

        // A delegating agent gets text back rather than nothing.
        using var sub = new FleetRunHarness(NoReview);
        Assert.Equal(FleetTeamRunner.NoTeamResult, await sub.Runner.RunTeamAsync(Task(), sub.Root));
    }

    [Fact]
    public void ValidateStages_Valid_IsNull()
        => Assert.Null(FleetTeamRunner.ValidateStages(StagedPlan(["A", "B"], [["A"], ["b"]])));

    [Fact]
    public void ValidateStages_EmptyOrEmptyStage_Refused()
    {
        Assert.Contains("set stages", FleetTeamRunner.ValidateStages(StagedPlan(["A"], [])));
        Assert.Contains("set stages", FleetTeamRunner.ValidateStages(StagedPlan(["A"], [["A"], []])));
    }

    [Fact]
    public void ValidateStages_UnknownMissingAndDuplicate_AllReported()
    {
        var problems = FleetTeamRunner.ValidateStages(StagedPlan(["A", "B", "C"], [["A", "X"], ["A"]]));

        Assert.Contains("unknown agents: X", problems);
        Assert.Contains("missing from the stages: B, C", problems);
        Assert.Contains("more than one stage: A", problems);
    }

    [Fact]
    public void Prompts_Render_OnePass_ValueNotReExpanded()
    {
        var rendered = FleetPrompts.Render("a {{x}} b {{y}} {{unknown}}", ("x", "{{y}}"), ("y", "Y"));

        Assert.Equal("a {{y}} b Y {{unknown}}", rendered);
    }

    [Fact]
    public void Prompts_OverrideSetAndReset()
    {
        var edited = FleetPrompts.WithOverride(new Dictionary<string, string>(), "judge", "be strict");
        Assert.Equal("be strict", new FleetPromptSet(edited).Get("judge"));
        Assert.True(new FleetPromptSet(edited).IsEdited("judge"));

        var reset = FleetPrompts.WithOverride(edited, "judge", FleetPrompts.Find("judge")!.Default.ReplaceLineEndings("\r\n"));
        Assert.Empty(reset);
        Assert.Throws<ArgumentException>(() => FleetPrompts.WithOverride(edited, "nope", "x"));
    }

    private static FleetTeamPlan StagedPlan(string[] agents, List<List<string>> stages) => new()
    {
        Pattern = FleetPattern.Staged,
        Agents = [.. agents.Select(a => new FleetAgentDefinition { Name = a })],
        Stages = stages,
    };
}
