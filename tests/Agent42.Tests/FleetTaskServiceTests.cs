using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The fleet's runner service (ADR 0097): submit refusals and caps, follow-ups, stop, shutdown and restart handling,
/// usage attribution, and the event log's coalescing, cap and replay-then-live ordering.
/// </summary>
public sealed class FleetTaskServiceTests : IAsyncDisposable
{
    private const string Alice = "alice";
    private const string Bob = "bob";

    private readonly FleetStore _store = new(new InMemoryFleetBlobs());
    private readonly List<FleetTaskService> _services = [];

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private FleetTaskService Service(FleetOptions? options = null)
    {
        var service = new FleetTaskService(
            _store,
            options ?? new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 },
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA)),
            new FixedCatalogs(),
            NullLogger<FleetTaskService>.Instance);
        _services.Add(service);
        return service;
    }

    // Plan approval holds a task open, waiting for its first question, until a test answers or stops it.
    private static FleetOptions Held => new() { ApprovalDepth = 0, JudgeDepth = -1, MaxConcurrentTasks = 5 };

    private static string Task(params object[] plans) => FakeChatClient.FleetTask("Write a haiku about lighthouses", new(plans));

    private static string OnePlan => Task(FleetRunHarness.Plan("Sequential", "Writer"));

    [Fact]
    public async Task SequentialTask_RunsToDone_AndPersists()
    {
        var service = Service();

        var (taskId, error) = await service.SubmitAsync(Alice, new(OnePlan, Model: FleetRunHarness.ModelA));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Contains("help you today", done.Result);
        Assert.True(done.CanFollowUp);
        Assert.Equal(1, done.Agents);
        var types = (await _store.ReadEventsAsync(Alice, taskId!)).Select(TypeOf).ToList();
        Assert.Equal("task.started", types[0]);
        Assert.Equal("task.finished", types[^1]);
    }

    /// <summary>Found in the DevHost smoke run: a task whose planner never produces a team is a failure, not a done
    /// task with the fallback text as its result, and it can't be followed up.</summary>
    [Fact]
    public async Task NoTeam_EndsFailed_NotDone()
    {
        var service = Service();

        var (taskId, _) = await service.SubmitAsync(Alice, new(Task()));
        var failed = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Failed, failed.Status);
        Assert.Equal(FleetTeamRunner.NoTeamResult, failed.Reason);
        Assert.Null(failed.Result);
        Assert.False(failed.CanFollowUp);
    }

    [Fact]
    public async Task Run_SetsUserTelemetryContext()
    {
        var service = Service();
        var telemetry = new UserTelemetryContext(Alice, "Alice", null, null, null, null);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan), telemetry);
        var done = await FinishedAsync(service, Alice, taskId!);

        // FakeChatClient echoes the RequestContext identity into its replies: the background run carried it.
        Assert.Contains($"[uctx:{Alice}]", done.Result);
    }

    [Fact]
    public async Task SecondSubmit_SameUser_Refused_OtherUserAccepted()
    {
        var service = Service(Held);
        var (first, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, first!);

        var (_, again) = await service.SubmitAsync(Alice, new(OnePlan));
        var (other, otherError) = await service.SubmitAsync(Bob, new(OnePlan));

        Assert.Contains("already running", again);
        Assert.NotNull(other);
        Assert.Null(otherError);
    }

    [Fact]
    public async Task GlobalCap_Refused()
    {
        var service = Service(new FleetOptions { ApprovalDepth = 0, MaxConcurrentTasks = 1 });
        var (first, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, first!);

        var (_, error) = await service.SubmitAsync(Bob, new(OnePlan));

        Assert.Contains("busy", error);
    }

    public static TheoryData<string, FleetSubmit> Refusals() => new()
    {
        { "empty", new FleetSubmit("   ") },
        { "over the cap", new FleetSubmit("x", MaxTokens: new FleetOptions().MaxTokensPerTaskLimit + 1) },
        { "zero cap", new FleetSubmit("x", MaxTokens: 0) },
        { "unknown model", new FleetSubmit("x", Model: "fake:nope") },
        { "no tools", new FleetSubmit("x", Model: FixedCatalogs.NoToolsModel) },
        { "embedding model", new FleetSubmit("x", Model: FixedCatalogs.EmbeddingModel) },
        { "unknown follow-up", new FleetSubmit("x", FollowUpOf: "missing") },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task Submit_Refusals(string why, FleetSubmit request)
    {
        var (taskId, error) = await Service().SubmitAsync(Alice, request);

        Assert.Null(taskId);
        Assert.False(string.IsNullOrWhiteSpace(error), why);
        Assert.Empty(await _store.ListTasksAsync(Alice));
    }

    [Fact]
    public async Task FollowUp_OfANotDoneTask_Refused()
    {
        await _store.SaveTaskAsync(Record("stopped-1", Alice, FleetTaskStatus.Stopped));

        var (_, error) = await Service().SubmitAsync(Alice, new("more", FollowUpOf: "stopped-1"));

        Assert.Contains("finished well", error);
    }

    [Fact]
    public async Task FollowUp_OtherUsersTask_Refused()
    {
        await _store.SaveTaskAsync(Record("done-1", Alice, FleetTaskStatus.Done) with { Result = "r" });

        var (taskId, error) = await Service().SubmitAsync(Bob, new("more", FollowUpOf: "done-1"));

        Assert.Null(taskId);
        Assert.Contains("finished well", error);
    }

    [Fact]
    public async Task FollowUp_UsesChainAndWorkspace()
    {
        var service = Service();
        var (firstId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        var first = await FinishedAsync(service, Alice, firstId!);
        await _store.PutWorkspaceFileAsync(Alice, first.WorkspaceId, "notes.md", Encoding.UTF8.GetBytes("hello"));

        var (secondId, error) = await service.SubmitAsync(Alice, new(Task(FleetRunHarness.Plan("Sequential", "Editor")), FollowUpOf: firstId));
        var second = await FinishedAsync(service, Alice, secondId!);

        Assert.Null(error);
        Assert.Equal(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(firstId, second.FollowUpOf);
        var input = (await _store.ReadEventsAsync(Alice, secondId!)).First(l => TypeOf(l) == "team.started");
        var task = JsonDocument.Parse(input).RootElement.GetProperty("data").GetProperty("task").GetString();
        Assert.Contains("Earlier task 1: ", task);
        Assert.Contains("- notes.md", task);
        // The materialized file was synced back, so it is still in the shared workspace.
        Assert.Contains(await _store.ListWorkspaceAsync(Alice, first.WorkspaceId), f => f.Path == "notes.md");
    }

    [Fact]
    public async Task Stop_PersistsStoppedWithReason()
    {
        var service = Service(Held);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, taskId!);

        Assert.Equal(FleetOutcome.Ok, await service.StopAsync(Alice, taskId!));
        var stopped = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Stopped, stopped.Status);
        Assert.Equal("stopped by the user", stopped.Reason);
        Assert.False(stopped.CanFollowUp);
        Assert.Equal(FleetOutcome.Conflict, await service.StopAsync(Alice, taskId!));
    }

    [Fact]
    public async Task Answer_RunsTheHeldTask()
    {
        var service = Service(Held);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, taskId!);

        Assert.Equal(FleetOutcome.NotFound, await service.AnswerAsync(Bob, taskId!, "y"));
        Assert.Equal(FleetOutcome.Ok, await service.AnswerAsync(Alice, taskId!, "y"));

        Assert.Equal(FleetTaskStatus.Done, (await FinishedAsync(service, Alice, taskId!)).Status);
        Assert.Equal(FleetOutcome.Conflict, await service.AnswerAsync(Alice, taskId!, "y"));
    }

    [Fact]
    public async Task Delete_RefusedWhileRunning_ThenRemoves()
    {
        var service = Service(Held);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, taskId!);

        Assert.Equal(FleetOutcome.Conflict, await service.DeleteAsync(Alice, taskId!));
        await service.StopAsync(Alice, taskId!);
        await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetOutcome.NotFound, await service.DeleteAsync(Bob, taskId!));
        Assert.Equal(FleetOutcome.Ok, await service.DeleteAsync(Alice, taskId!));
        Assert.Null(await service.GetAsync(Alice, taskId!));
    }

    [Fact]
    public async Task Shutdown_PersistsInterrupted()
    {
        var service = Service(Held);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, taskId!);

        await service.StopAsync(CancellationToken.None);

        var record = await _store.GetTaskAsync(Alice, taskId!);
        Assert.Equal(FleetTaskStatus.Interrupted, record!.Status);
        var (_, error) = await service.SubmitAsync(Alice, new(OnePlan));
        Assert.Contains("shutting down", error);
    }

    [Fact]
    public async Task RunningNotInRegistry_IsInterrupted_AndPersisted()
    {
        await _store.SaveTaskAsync(Record("lost-1", Alice, FleetTaskStatus.Running));
        var service = Service();

        var listed = Assert.Single(await service.ListAsync(Alice));

        Assert.Equal(FleetTaskStatus.Interrupted, listed.Status);
        Assert.Equal(FleetTaskStatus.Interrupted, (await _store.GetTaskAsync(Alice, "lost-1"))!.Status);
        Assert.False(listed.CanFollowUp);
    }

    [Fact]
    public async Task Subscribe_OtherUser_IsNull_FinishedTask_ReplaysTheLog()
    {
        var service = Service();
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, taskId!);

        Assert.Null(await service.SubscribeAsync(Bob, taskId!));
        var lines = await ToListAsync((await service.SubscribeAsync(Alice, taskId!))!);

        Assert.Equal(await _store.ReadEventsAsync(Alice, taskId!), lines);
    }

    [Fact]
    public async Task Prompts_SetAndReset()
    {
        var service = Service();

        Assert.True(await service.SetPromptAsync(Alice, "judge", "be strict"));
        Assert.False(await service.SetPromptAsync(Alice, "nope", "x"));
        Assert.Equal("be strict", (await service.GetPromptsAsync(Alice)).Get("judge"));
        Assert.False((await service.GetPromptsAsync(Bob)).IsEdited("judge"));

        await service.SetPromptAsync(Alice, "judge", null);
        Assert.False((await service.GetPromptsAsync(Alice)).IsEdited("judge"));
    }

    [Fact]
    public async Task Events_TextCoalesced_Complete()
    {
        var log = new FleetEventLog(_store, Alice, "t1", maxBytes: 1_000_000);
        foreach (var delta in new[] { "a", "b", "c" })
        {
            log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = "Writer_1", text = delta });
        }

        log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = "Editor_2", text = "x" });
        log.Publish("tool.call", new { teamId = "team-1", attempt = 1, name = "web_search" });
        log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = "Writer_1", text = "d" });
        await log.FlushAsync();

        var lines = await _store.ReadEventsAsync(Alice, "t1");
        Assert.Equal(["agent.text", "agent.text", "tool.call", "agent.text"], lines.Select(TypeOf));
        Assert.Equal(("abc", 3L), (TextOf(lines[0]), FleetEvent.SeqOf(lines[0])));
        Assert.Equal(("d", 6L), (TextOf(lines[3]), FleetEvent.SeqOf(lines[3])));
    }

    [Fact]
    public async Task LogCap_DropsTextButKeepsFinished()
    {
        var log = new FleetEventLog(_store, Alice, "t1", maxBytes: 400);
        for (var i = 0; i < 50; i++)
        {
            log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = $"Writer_{i}", text = new string('x', 40) });
        }

        log.Publish("task.finished", new { taskId = "t1", status = "done", result = new string('r', 500) });
        await log.FlushAsync();

        var lines = await _store.ReadEventsAsync(Alice, "t1");
        Assert.True(lines.Count(l => TypeOf(l) == "agent.text") < 50);
        Assert.Equal("task.finished", TypeOf(lines[^1]));
    }

    /// <summary>
    /// A subscriber that joins while events are being published and flushed sees every delta exactly once and in
    /// order: the concatenated text of what it gets is exactly the text that was published.
    /// </summary>
    [Fact]
    public async Task Subscribe_ReplaysThenLive_ExactlyOnce()
    {
        for (var round = 0; round < 20; round++)
        {
            var log = new FleetEventLog(_store, Alice, $"race-{round}", maxBytes: 10_000_000);
            const int count = 400;
            var expected = string.Concat(Enumerable.Range(0, count).Select(i => $"<{i}>"));
            var joinAt = Random.Shared.Next(count);
            var joined = new TaskCompletionSource<Task<List<string>>>();

            var publisher = System.Threading.Tasks.Task.Run(async () =>
            {
                for (var i = 0; i < count; i++)
                {
                    log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = "Writer_1", text = $"<{i}>" });
                    if (i % 7 == 0)
                    {
                        await log.FlushAsync();
                    }

                    if (i == joinAt)
                    {
                        // Subscribe first, then read the store, as the service does. The read runs WHILE publishing
                        // and flushing go on, so it may see events the subscription's snapshot also holds.
                        var subscription = log.Subscribe();
                        var taskId = $"race-{round}";
                        joined.SetResult(System.Threading.Tasks.Task.Run(async () =>
                        {
                            await System.Threading.Tasks.Task.Yield();
                            var stored = await _store.ReadEventsAsync(Alice, taskId);
                            return await ToListAsync(FleetEventLog.ReplayAsync(stored, subscription));
                        }));
                    }
                }

                await log.FlushAsync();
                log.Complete();
            });

            var lines = await (await joined.Task);
            await publisher;

            var seqs = lines.Select(FleetEvent.SeqOf).ToList();
            Assert.Equal(seqs.Order(), seqs);
            Assert.Equal(seqs.Distinct().Count(), seqs.Count);
            Assert.Equal(expected, string.Concat(lines.Select(TextOf)));
        }
    }

    private static FleetTaskRecord Record(string id, string user, string status) => new()
    {
        Id = id,
        UserId = user,
        Request = "earlier",
        WorkspaceId = "w-" + id,
        PlannerModel = FleetRunHarness.ModelA,
        Status = status,
        StartedAt = DateTimeOffset.UtcNow,
    };

    private static string TypeOf(string line) => JsonDocument.Parse(line).RootElement.GetProperty("type").GetString()!;

    private static string TextOf(string line) => JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("text").GetString()!;

    private static async Task<List<string>> ToListAsync(IAsyncEnumerable<string> lines)
    {
        var list = new List<string>();
        await foreach (var line in lines)
        {
            list.Add(line);
        }

        return list;
    }

    private static async Task<FleetTaskRecord> FinishedAsync(FleetTaskService service, string user, string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var record = await service.GetAsync(user, taskId, timeout.Token);
            // Interrupted can't happen here: the task is in the registry until its final state is saved.
            if (record is { Status: not FleetTaskStatus.Running })
            {
                return record;
            }

            await System.Threading.Tasks.Task.Delay(20, timeout.Token);
        }
    }

    // Waits on the live stream until the task asks its first question.
    private static async Task QuestionAsync(FleetTaskService service, string user, string taskId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await foreach (var line in (await service.SubscribeAsync(user, taskId, timeout.Token))!.WithCancellation(timeout.Token))
        {
            if (TypeOf(line) == "prompt")
            {
                return;
            }
        }

        Assert.Fail("The task ended without asking a question.");
    }

    /// <summary>A catalog with two tool-capable models, one without tools and an embedding model.</summary>
    private sealed class FixedCatalogs : IUserModelCatalogProvider, IModelCatalog
    {
        public const string NoToolsModel = "fake:text-only";
        public const string EmbeddingModel = "fake:embed";

        public Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult<IModelCatalog>(this);

        public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
            => System.Threading.Tasks.Task.FromResult(new ModelCatalogResult(FleetRunHarness.ModelA,
            [
                new ModelInfo(FleetRunHarness.ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
                new ModelInfo(FleetRunHarness.ModelB, "fake", null, "b", null, SupportsTools: true, SupportsVision: false),
                new ModelInfo(NoToolsModel, "fake", null, "t", null, SupportsTools: false, SupportsVision: false),
                new ModelInfo(EmbeddingModel, "fake", null, "e", null, SupportsTools: true, SupportsVision: false, SupportsEmbeddings: true),
            ]));
    }
}
