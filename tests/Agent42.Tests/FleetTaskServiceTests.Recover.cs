using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>Recover (ADR 0152): a new task on an unfinished task's workspace, its approved team rebuilt from the log.</summary>
public sealed partial class FleetTaskServiceTests
{
    private static object NotePlan(string agent) => new
    {
        rationale = "fits",
        pattern = "Sequential",
        agents = new[] { new { name = agent, yaml = FakeChatClient.FleetAgentYaml(agent, null, "note_set") } },
    };

    // Runs a real task, then rewrites its record as interrupted: a real workspace, canvas and event log to recover from.
    private async Task<FleetTaskRecord> InterruptedTaskAsync(
        FleetTaskService service, string request, string status = FleetTaskStatus.Interrupted, string user = Alice)
    {
        var (taskId, error) = await service.SubmitAsync(user, new(request));
        Assert.Null(error);
        var done = await FinishedAsync(service, user, taskId!);
        var interrupted = done with { Status = status, Result = null, Reason = "The app shut down while this task ran." };
        await _store.SaveTaskAsync(interrupted);
        return interrupted;
    }

    private static string LogEvent(long seq, string type, object data)
        => JsonSerializer.Serialize(new { seq, type, time = DateTimeOffset.UtcNow, data }) + "\n";

    private async Task<FleetTaskRecord> HandWrittenLogAsync(string request, params string[] events)
    {
        var record = Record("old-1", Alice, FleetTaskStatus.Interrupted) with { MaxTokens = 100_000, Request = request };
        await _store.SaveTaskAsync(record);
        await _store.AppendEventsAsync(Alice, record.Id, string.Concat(events));
        return record;
    }

    private static string RootTeam => LogEvent(1, "team.started", new { teamId = "team-1", depth = 0 });

    private static string SubmittedPlan(params string[] yaml) => LogEvent(2, "plan.submitted", new
    {
        teamId = "team-1",
        attempt = 1,
        pattern = "Sequential",
        rationale = "fits",
        agents = yaml.Select((y, i) => new { name = $"Agent{i}", description = "d", model = FleetRunHarness.ModelA, yaml = y }).ToArray(),
    });

    private static string AcceptedPlan => LogEvent(3, "plan.accepted", new { teamId = "team-1", attempt = 1 });

    private async Task<List<JsonElement>> EventsAsync(string taskId, string type)
        => [.. (await _store.ReadEventsAsync(Alice, taskId)).Where(l => TypeOf(l) == type).Select(l => JsonDocument.Parse(l).RootElement.GetProperty("data").Clone())];

    public static TheoryData<string> NotRecoverable() => new() { "done", "other user", "unknown", "with follow-up" };

    [Theory]
    [MemberData(nameof(NotRecoverable))]
    public async Task Recover_RefusedUnlessUnfinishedAndOwn(string why)
    {
        await _store.SaveTaskAsync(Record("done-1", Alice, FleetTaskStatus.Done) with { Result = "r" });
        await _store.SaveTaskAsync(Record("bobs-1", Bob, FleetTaskStatus.Interrupted));
        await _store.SaveTaskAsync(Record("int-1", Alice, FleetTaskStatus.Interrupted) with { MaxTokens = 100_000 });
        var request = why switch
        {
            "done" => new FleetSubmit("x", RecoverOf: "done-1"),
            "other user" => new FleetSubmit("x", RecoverOf: "bobs-1"),
            "unknown" => new FleetSubmit("x", RecoverOf: "missing"),
            _ => new FleetSubmit("x", RecoverOf: "int-1", FollowUpOf: "done-1"),
        };

        var (taskId, error) = await Service().SubmitAsync(Alice, request);

        Assert.Null(taskId);
        Assert.False(string.IsNullOrWhiteSpace(error), why);
        Assert.Equal(2, (await _store.ListTasksAsync(Alice)).Count);
    }

    [Theory]
    [InlineData(FleetTaskStatus.Interrupted)]
    [InlineData(FleetTaskStatus.Stopped)]
    [InlineData(FleetTaskStatus.Failed)]
    public async Task Recover_SharesWorkspace_KeepsOriginal(string status)
    {
        var service = Service();
        var old = await InterruptedTaskAsync(service, OnePlan, status);
        await _store.PutWorkspaceFileAsync(Alice, old.WorkspaceId, "draft.md", Encoding.UTF8.GetBytes("half"));

        var (taskId, error) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        var recovered = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(FleetTaskStatus.Done, recovered.Status);
        Assert.Equal(old.WorkspaceId, recovered.WorkspaceId);
        Assert.Equal(old.Id, recovered.RecoveredFrom);
        Assert.Null(recovered.FollowUpOf);
        Assert.Equal(old.Request, recovered.Request);
        Assert.Equal(old.PlannerModel, recovered.PlannerModel);
        Assert.Equal(status, (await _store.GetTaskAsync(Alice, old.Id))!.Status);
        // Materialized, then synced back: the earlier file is still there.
        Assert.Contains(await _store.ListWorkspaceAsync(Alice, old.WorkspaceId), f => f.Path == "draft.md");
    }

    [Fact]
    public async Task Recover_RefusedWhileATaskRuns()
    {
        var service = Service(Held);
        await _store.SaveTaskAsync(Record("int-1", Alice, FleetTaskStatus.Interrupted) with { MaxTokens = 100_000 });
        var (first, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, first!);

        var (taskId, error) = await service.SubmitAsync(Alice, new("", RecoverOf: "int-1"));

        Assert.Null(taskId);
        Assert.Contains("already running", error);
    }

    [Fact]
    public async Task Recover_FailedStart_KeepsWorkspace()
    {
        var blobs = new FaultyFleetBlobs();
        var store = new FleetStore(blobs);
        var service = Service(store: store);
        var old = Record("int-1", Alice, FleetTaskStatus.Interrupted) with { Request = OnePlan, MaxTokens = 100_000 };
        await store.SaveTaskAsync(old);
        await store.PutWorkspaceFileAsync(Alice, old.WorkspaceId, "inputs/a.md", Encoding.UTF8.GetBytes("keep me"));
        blobs.FailPutWhen = (key, text) => key.Contains("/tasks/", StringComparison.Ordinal) && text.Contains("\"recoveredFrom\"", StringComparison.Ordinal);

        await Assert.ThrowsAsync<IOException>(() => service.SubmitAsync(Alice, new("", RecoverOf: old.Id)));

        Assert.Equal(["inputs/a.md"], (await store.ListWorkspaceAsync(Alice, old.WorkspaceId)).Select(f => f.Path));
        // The registry slot was released: the user can try again.
        blobs.FailPutWhen = null;
        var (taskId, error) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        Assert.Null(error);
        Assert.NotNull(taskId);
    }

    [Fact]
    public async Task Recover_SeedConsumedOnce()
    {
        var seed = new FleetTeamPlan { Pattern = FleetPattern.Sequential, Rationale = "approved", Agents = [new() { Name = "Writer", Yaml = FakeChatClient.FleetAgentYaml("Writer") }] };
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Planned"), FleetRunHarness.Plan("Sequential", "Planned"), FleetRunHarness.Plan("Sequential", "Planned")]);
        var task = FakeChatClient.FleetTask("Write a haiku", script);
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1 }, seedPlan: seed);

        // A sub-team and a later attempt don't take it: they go to the planner and the seed stays.
        var (_, subAgents) = await h.Runner.CreateTeamAsync(task, h.Root with { Depth = 1, TeamId = "team-2", Attempt = 1 });
        var (_, laterAgents) = await h.Runner.CreateTeamAsync(task, h.Root with { TeamId = "team-1", Attempt = 2 });
        Assert.Equal(2, script.PlannerCalls);
        Assert.Equal("Planned", subAgents.Single().Name);
        Assert.Equal("Planned", laterAgents.Single().Name);

        // The root team's first attempt takes it, once.
        var (plan, agents) = await h.Runner.CreateTeamAsync(task, h.Root with { TeamId = "team-1", Attempt = 1 });
        Assert.Equal(2, script.PlannerCalls);
        Assert.Equal("approved", plan.Rationale);
        Assert.Equal("Writer", agents.Single().Name);
        var submitted = h.Events.Of("plan.submitted").Last();
        Assert.False(submitted.GetProperty("needsApproval").GetBoolean());

        await h.Runner.CreateTeamAsync(task, h.Root with { TeamId = "team-1", Attempt = 1 });
        Assert.Equal(3, script.PlannerCalls);
    }

    [Fact]
    public async Task Recover_DoesNotReingest_KeepsDocuments()
    {
        var service = Service();
        var old = Record("int-1", Alice, FleetTaskStatus.Interrupted) with
        {
            MaxTokens = 100_000,
            Request = OnePlan,
            Inputs = ["notes.md"],
            Documents = ["notes.md"],
            TextCopies = ["inputs/notes.md.txt"],
        };
        await _store.SaveTaskAsync(old);
        await _store.PutWorkspaceFileAsync(Alice, old.WorkspaceId, "inputs/notes.md", Encoding.UTF8.GetBytes("Magrathea"));

        var (taskId, _) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        var recovered = await FinishedAsync(service, Alice, taskId!);

        Assert.Empty(recovered.Inputs);
        Assert.Equal(["notes.md"], recovered.Documents);
        Assert.Equal(["inputs/notes.md.txt"], recovered.TextCopies);
        Assert.Empty(await EventsAsync(taskId!, "documents.ingested"));
        var input = (await EventsAsync(taskId!, "team.started")).First().GetProperty("task").GetString();
        Assert.Contains("'notes.md'", input);
    }

    [Fact]
    public async Task Recover_SeesWorkspaceFilesAndCanvas()
    {
        var service = Service();
        var write = new FakeChatClient.FleetScript(
            [CanvasPlan("Writer")],
            agentCalls: [("canvas_write", new() { ["name"] = "report", ["kind"] = "markdown", ["content"] = "# Draft", ["expectedVersion"] = 0, ["title"] = "Report" })]);
        var old = await InterruptedTaskAsync(service, FakeChatClient.FleetTask("Write a report", write));
        await _store.PutWorkspaceFileAsync(Alice, old.WorkspaceId, "chapter1.md", Encoding.UTF8.GetBytes("one"));

        var (taskId, _) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        await FinishedAsync(service, Alice, taskId!);

        var input = (await EventsAsync(taskId!, "team.started")).First().GetProperty("task").GetString();
        Assert.Contains("- chapter1.md", input);
        Assert.Contains("- report", input);
        var prepared = Assert.Single(await EventsAsync(taskId!, "recover.prepared"));
        Assert.Equal(old.Id, prepared.GetProperty("from").GetString());
        Assert.True(prepared.GetProperty("files").GetInt32() >= 1);
    }

    [Fact]
    public async Task Recover_ReusesAcceptedPlan_WithoutPlannerOrApproval()
    {
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")]);
        var old = await InterruptedTaskAsync(Service(), FakeChatClient.FleetTask("Write a haiku", script));
        Assert.Equal(1, script.PlannerCalls);

        // Plan approval is on at depth 0 here: a task that asked would wait for an answer and never finish.
        var service = Service(Held);
        var (taskId, error) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        var recovered = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(FleetTaskStatus.Done, recovered.Status);
        Assert.Equal(1, script.PlannerCalls);
        Assert.False(Assert.Single(await EventsAsync(taskId!, "plan.submitted")).GetProperty("needsApproval").GetBoolean());
        Assert.Empty(await EventsAsync(taskId!, "prompt"));
        Assert.True(Assert.Single(await EventsAsync(taskId!, "recover.prepared")).GetProperty("planReused").GetBoolean());
    }

    public static TheoryData<string> FallbackCases() => new() { "empty log", "submitted, not accepted", "rejected seed" };

    [Theory]
    [MemberData(nameof(FallbackCases))]
    public async Task Recover_FallsBack_ToPlanner(string why)
    {
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")]);
        var request = FakeChatClient.FleetTask("Write a haiku", script);
        var old = why switch
        {
            "empty log" => await HandWrittenLogAsync(request),
            "submitted, not accepted" => await HandWrittenLogAsync(request, RootTeam, SubmittedPlan(FakeChatClient.FleetAgentYaml("Agent0"))),
            _ => await HandWrittenLogAsync(request, RootTeam, SubmittedPlan(FakeChatClient.FleetAgentYaml("Agent0", null, "no_such_tool")), AcceptedPlan),
        };
        var service = Service();

        var (taskId, error) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id));
        var recovered = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(FleetTaskStatus.Done, recovered.Status);
        Assert.Equal(1, script.PlannerCalls);
    }

    [Fact]
    public async Task Notes_SavedAfterEachAttempt()
    {
        var blobs = new FaultyFleetBlobs();
        var store = new FleetStore(blobs);
        var service = Service(store: store);
        var script = new FakeChatClient.FleetScript([NotePlan("Writer")], agentCalls: [("note_set", new() { ["key"] = "tone", ["value"] = "dry" })]);
        // The final save fails, as a hard kill would: what is stored is the record as the attempt left it.
        blobs.FailPutWhen = (key, text) => key.Contains("/tasks/", StringComparison.Ordinal) && text.Contains("\"status\":\"done\"", StringComparison.Ordinal);

        var (taskId, _) = await service.SubmitAsync(Alice, new(FakeChatClient.FleetTask("Write a haiku", script)));
        // Once the run is gone from the registry, a read rewrites the never-finished record as interrupted, keeping its notes.
        var stored = (await TestWait.WaitForAsync(() => service.GetAsync(Alice, taskId!), r => r is { Status: FleetTaskStatus.Interrupted }, "the task to be lost"))!;
        Assert.Equal("dry", Assert.Single(stored.Notes).Value);
    }

    [Fact]
    public async Task Recover_IsNotAnOriginTask_NoClarify()
    {
        var service = Service();
        var old = Record("int-1", Alice, FleetTaskStatus.Interrupted) with
        {
            MaxTokens = 100_000,
            Request = OnePlan,
            OriginConversationId = "chat-1",
            Clarify = true,
            SelfReview = true,
            EnabledTools = [],
        };
        await _store.SaveTaskAsync(old);

        var (taskId, _) = await service.SubmitAsync(Alice, new("", RecoverOf: old.Id, OriginConversationId: "chat-2", Clarify: true));
        var recovered = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(recovered.OriginConversationId);
        Assert.False(recovered.Clarify);
        Assert.True(recovered.SelfReview);
        Assert.Empty(recovered.EnabledTools!);
        Assert.Empty(await EventsAsync(taskId!, "clarify.started"));
        Assert.Empty(await EventsAsync(taskId!, "question.auto"));
    }
}
