using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The fleet's runner service (ADR 0097): submit refusals and caps, follow-ups, stop, shutdown and restart handling,
/// usage attribution, and the event log's coalescing, cap and replay-then-live ordering.
/// </summary>
public sealed partial class FleetTaskServiceTests : IAsyncDisposable
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

    private FleetTaskService Service(
        FleetOptions? options = null, FleetToolCatalog? tools = null, IChatClient? client = null,
        IDocumentIndex? documents = null, IEmbeddingGenerator<string, Embedding<float>>? embeddings = null,
        IFleetCompletionNotifier? notifier = null, FleetStore? store = null, bool userSettings = false)
    {
        store ??= _store;
        var effective = options ?? new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 };
        var provider = new SingleUserChatClientFactoryProvider(
            new SingleChatClientFactory(client ?? new FakeChatClient(), FleetRunHarness.ModelA, embeddingGenerator: embeddings));
        var service = new FleetTaskService(
            store,
            effective,
            provider,
            new FixedCatalogs(),
            NullLogger<FleetTaskService>.Instance,
            tools,
            documents: documents,
            ingestion: documents is null ? null : new DocumentIngestionService(new DocumentTextExtractor(), new DocumentChunker(), provider, documents),
            notifier: notifier,
            settings: userSettings ? new FleetSettingsService(effective, store, new FixedCatalogs()) : null);
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

    /// <summary>ADR 0099: no override stores null, so the run follows the user's default when it starts.</summary>
    [Fact]
    public async Task Submit_WithoutOverride_StoresNull()
    {
        var service = Service();

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, taskId!);

        Assert.Null((await _store.GetTaskAsync(Alice, taskId!))!.EnabledTools);
    }

    /// <summary>ADR 0099: an override is sanitized like a stored default (unknown keys dropped, catalog order), and
    /// an empty list stays empty: it means no tools, never "the default".</summary>
    [Fact]
    public async Task Submit_WithOverride_StoresSanitizedSelection()
    {
        var service = Service();

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan,
            EnabledTools: ["mcp:s1:lookup", "invented", ToolCatalog.WebSearch, "mcp:s2", ToolCatalog.WebSearch]));
        await FinishedAsync(service, Alice, taskId!);
        var (emptyId, _) = await service.SubmitAsync(Alice, new(OnePlan, EnabledTools: []));
        await FinishedAsync(service, Alice, emptyId!);

        Assert.Equal([ToolCatalog.WebSearch, "mcp:s1:lookup", "mcp:s2"], (await _store.GetTaskAsync(Alice, taskId!))!.EnabledTools!);
        Assert.Empty((await _store.GetTaskAsync(Alice, emptyId!))!.EnabledTools!);
    }

    /// <summary>ADR 0099 end to end: the record's override reaches the run's tools (record → <c>BuildRunAsync</c> →
    /// catalog), seen in the planner's tool list. Without one the run follows the profile default, here web search
    /// only.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Run_UsesTaskOverride_ElseProfileDefault(bool withOverride, bool expectWebSearch)
    {
        var profile = FakeProfileGrainFactory.Create([ToolCatalog.WebSearch]);
        var catalog = new FleetToolCatalog(profile.Factory, NullLogger<FleetToolCatalog>.Instance, new InMemoryWebSearch());
        var client = new SystemPromptRecorder(new FakeChatClient());
        var service = Service(tools: catalog, client: client);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, EnabledTools: withOverride ? [] : null));
        await FinishedAsync(service, Alice, taskId!);

        var planner = client.SystemPrompts.First(p => p.Contains("- write_artifact:", StringComparison.Ordinal));
        Assert.Equal(expectWebSearch, planner.Contains("- web_search:", StringComparison.Ordinal));
        Assert.Equal(withOverride ? 0 : 1, profile.Reads);
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

    // The user's Advanced settings (ADR 0135): a task runs on its user's options, a neighbour's are untouched.
    private static string TwoThenOneAgentPlan => Task(FleetRunHarness.Plan("Sequential", "Writer", "Editor"), FleetRunHarness.Plan("Sequential", "Writer"));

    private static async Task<List<string>> RejectedPlanReasonsAsync(FleetStore store, string user, string taskId)
        => [.. (await store.ReadEventsAsync(user, taskId)).Where(l => TypeOf(l) == "plan.rejected").Select(l => System.Text.Json.JsonDocument.Parse(l).RootElement.GetProperty("data").GetProperty("reason").GetString()!)];

    [Fact]
    public async Task Task_UsesUserSettings_AndOtherUserUnaffected()
    {
        var service = Service(userSettings: true);
        await _store.SetSettingsAsync(Alice, new FleetUserSettings { MaxAgentsPerTask = 1 });

        var (aliceTask, _) = await service.SubmitAsync(Alice, new(TwoThenOneAgentPlan));
        await FinishedAsync(service, Alice, aliceTask!);
        var (bobTask, _) = await service.SubmitAsync(Bob, new(TwoThenOneAgentPlan));
        await FinishedAsync(service, Bob, bobTask!);

        Assert.Contains("only 1 agent(s) are left", Assert.Single(await RejectedPlanReasonsAsync(_store, Alice, aliceTask!)));
        Assert.Empty(await RejectedPlanReasonsAsync(_store, Bob, bobTask!));
    }

    [Fact]
    public async Task Task_WithoutSettingsService_UsesOperatorOptions()
    {
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1, MaxAgentsPerTask = 1 });

        var (taskId, _) = await service.SubmitAsync(Alice, new(TwoThenOneAgentPlan));
        await FinishedAsync(service, Alice, taskId!);

        Assert.Single(await RejectedPlanReasonsAsync(_store, Alice, taskId!));
    }

    [Fact]
    public async Task Running_KeepsSnapshot()
    {
        var blobs = new CountingBlobs();
        var store = new FleetStore(blobs);
        var service = Service(new FleetOptions { ApprovalDepth = 0, JudgeDepth = -1, MaxConcurrentTasks = 5 }, store: store, userSettings: true);
        await store.SetSettingsAsync(Alice, new FleetUserSettings { MaxAgentsPerTask = 1 });

        var (taskId, _) = await service.SubmitAsync(Alice, new(TwoThenOneAgentPlan));
        await QuestionAsync(service, Alice, taskId!);
        // A save while the task waits for its approval changes the NEXT task, not this one.
        await store.SetSettingsAsync(Alice, new FleetUserSettings { MaxAgentsPerTask = 40 });
        await service.StopAsync(Alice, taskId!);
        await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(1, blobs.SettingsReads);
        Assert.Contains("only 1 agent(s) are left", Assert.Single(await RejectedPlanReasonsAsync(store, Alice, taskId!)));
    }

    private sealed class CountingBlobs : IFleetBlobs
    {
        private readonly InMemoryFleetBlobs _inner = new();
        public int SettingsReads { get; private set; }

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
        {
            SettingsReads += key.EndsWith("/settings.json", StringComparison.Ordinal) ? 1 : 0;
            return _inner.GetAsync(key, cancellationToken);
        }

        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default) => _inner.PutAsync(key, content, cancellationToken);
        public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default) => _inner.AppendAsync(key, content, cancellationToken);
        public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
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

    /// <summary>ADR 0102 end to end: an agent's canvas_write is stored under the canvas prefix and logged as
    /// canvas.updated, and a follow-up (same workspace) loads the page, so its agent's canvas_edit makes version 2.</summary>
    [Fact]
    public async Task Run_CanvasWrite_PersistsAndLogs_FollowUpSeesIt()
    {
        var service = Service();
        var write = new FakeChatClient.FleetScript(
            [CanvasPlan("Writer")],
            agentCalls: [("canvas_write", new() { ["name"] = "report", ["kind"] = "markdown", ["content"] = "# Draft", ["expectedVersion"] = 0, ["title"] = "Report" })]);
        var (firstId, _) = await service.SubmitAsync(Alice, new(FakeChatClient.FleetTask("Write a report", write)));
        var first = await FinishedAsync(service, Alice, firstId!);

        Assert.Equal(FleetTaskStatus.Done, first.Status);
        Assert.Equal("# Draft", Encoding.UTF8.GetString((await _store.GetCanvasVersionAsync(Alice, first.WorkspaceId, "report", 1))!));
        var updated = (await _store.ReadEventsAsync(Alice, firstId!)).Single(l => TypeOf(l) == FleetCanvas.UpdatedEvent);
        var data = JsonDocument.Parse(updated).RootElement.GetProperty("data");
        Assert.Equal(("report", 1, "Report"), (data.GetProperty("name").GetString(), data.GetProperty("version").GetInt32(), data.GetProperty("title").GetString()));

        var edit = new FakeChatClient.FleetScript(
            [CanvasPlan("Editor")],
            agentCalls: [("canvas_edit", new() { ["name"] = "report", ["oldText"] = "Draft", ["newText"] = "Final" })]);
        var (secondId, _) = await service.SubmitAsync(Alice, new(FakeChatClient.FleetTask("Finish the report", edit), FollowUpOf: firstId));
        await FinishedAsync(service, Alice, secondId!);

        var page = Assert.Single((await _store.GetCanvasManifestAsync(Alice, first.WorkspaceId)).Pages);
        Assert.Equal([1, 2], page.Versions);
        Assert.Equal("# Final", Encoding.UTF8.GetString((await _store.GetCanvasVersionAsync(Alice, first.WorkspaceId, "report", 2))!));
    }

    private static object CanvasPlan(string agent) => new
    {
        rationale = "fits",
        pattern = "Sequential",
        agents = new[] { new { name = agent, yaml = FakeChatClient.FleetAgentYaml(agent, null, "canvas_write", "canvas_edit") } },
    };

    private static FleetInputFile File(string name, string text = "Magrathea builds luxury planets for the very rich.")
        => new(name, Encoding.UTF8.GetBytes(text));

    private async Task<List<string>> PathsAsync(string user, string workspaceId)
        => [.. (await _store.ListWorkspaceAsync(user, workspaceId)).Select(f => f.Path).Order(StringComparer.Ordinal)];

    /// <summary>ADR 0101: attached files land under inputs/ before the task starts, and the record names them.</summary>
    [Fact]
    public async Task Submit_WithFiles_StoresInputs()
    {
        var service = Service();

        var (taskId, error) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File(@"C:\docs\notes.md"), File("data.csv", "a,b\n1,2")]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(["notes.md", "data.csv"], done.Inputs);
        var paths = await PathsAsync(Alice, done.WorkspaceId);
        Assert.Contains("inputs/notes.md", paths);
        Assert.Contains("inputs/data.csv", paths);
    }

    public static TheoryData<string, FleetSubmit> InputRefusals() => new()
    {
        { "too many files", new FleetSubmit("x", Files: [.. Enumerable.Range(0, 3).Select(i => File($"f{i}.md"))]) },
        { "over the file cap", new FleetSubmit("x", Files: [File("big.md", new string('x', 200))]) },
        { "empty name", new FleetSubmit("x", Files: [File("folder/")]) },
        { "dot name", new FleetSubmit("x", Files: [File("..")]) },
        { "control character", new FleetSubmit("x", Files: [File("a\u0001.md")]) },
        { "duplicate name", new FleetSubmit("x", Files: [File("a.md"), File("x/A.md")]) },
        { "over the workspace bytes", new FleetSubmit("x", Files: [File("a.md", new string('x', 90)), File("b.md", new string('x', 90))]) },
        { "follow-up and copy", new FleetSubmit("x", FollowUpOf: "done-1", CopyInputsOf: "done-1") },
        { "copy of an unknown task", new FleetSubmit("x", CopyInputsOf: "missing") },
    };

    /// <summary>ADR 0101: every file refusal happens before anything is written, so a refused task leaves no blobs.</summary>
    [Theory]
    [MemberData(nameof(InputRefusals))]
    public async Task Submit_InvalidFiles_RefusedWithoutBlobs(string why, FleetSubmit request)
    {
        await _store.SaveTaskAsync(Record("done-1", Alice, FleetTaskStatus.Done) with { Result = "r" });
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1, MaxInputFiles = 2, MaxInputFileBytes = 100, MaxWorkspaceBytes = 150 });

        var (taskId, error) = await service.SubmitAsync(Alice, request);

        Assert.Null(taskId);
        Assert.False(string.IsNullOrWhiteSpace(error), why);
        Assert.Single(await _store.ListTasksAsync(Alice));
        Assert.Empty(await _store.ListWorkspaceAsync(Alice, "w-done-1"));
    }

    /// <summary>ADR 0101: a follow-up's existing workspace files count against the caps too.</summary>
    [Fact]
    public async Task Submit_FollowUp_ExistingFilesCountAgainstTheCaps()
    {
        await _store.SaveTaskAsync(Record("done-1", Alice, FleetTaskStatus.Done) with { Result = "r" });
        await _store.PutWorkspaceFileAsync(Alice, "w-done-1", "report.md", new byte[120]);
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1, MaxWorkspaceBytes = 150 });

        var (_, error) = await service.SubmitAsync(Alice, new("x", FollowUpOf: "done-1", Files: [File("a.md", new string('x', 40))]));

        Assert.Contains("don't fit", error);
        Assert.Equal(["report.md"], await PathsAsync(Alice, "w-done-1"));
    }

    /// <summary>ADR 0101: a submit refused by the registry (a task already runs) writes no input blobs.</summary>
    [Fact]
    public async Task Submit_Refused_LeavesNoInputs()
    {
        var service = Service(Held);
        var (first, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await QuestionAsync(service, Alice, first!);

        var (taskId, error) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File("a.md")]));

        Assert.Null(taskId);
        Assert.Contains("already running", error);
        var tasks = await _store.ListTasksAsync(Alice);
        Assert.Single(tasks);
        Assert.DoesNotContain(await PathsAsync(Alice, tasks[0].WorkspaceId), p => p.StartsWith("inputs/", StringComparison.Ordinal));
    }

    /// <summary>ADR 0101: Run again of a new task copies its inputs (never its generated text copies) into a new
    /// workspace; a new file wins on a name clash; another user's task can't be copied.</summary>
    [Fact]
    public async Task Submit_CopyInputsOf_CopiesInputs_OwnOnly()
    {
        await _store.SaveTaskAsync(Record("src", Alice, FleetTaskStatus.Done) with
        {
            Result = "r",
            Inputs = ["a.md", "b.pdf"],
            TextCopies = ["inputs/b.pdf.txt"],
        });
        await _store.PutWorkspaceFileAsync(Alice, "w-src", "inputs/a.md", Encoding.UTF8.GetBytes("old a"));
        await _store.PutWorkspaceFileAsync(Alice, "w-src", "inputs/b.pdf", [1, 2, 3]);
        await _store.PutWorkspaceFileAsync(Alice, "w-src", "inputs/b.pdf.txt", Encoding.UTF8.GetBytes("copy"));
        await _store.PutWorkspaceFileAsync(Alice, "w-src", "result.md", Encoding.UTF8.GetBytes("out"));
        var service = Service();

        var (_, bobError) = await service.SubmitAsync(Bob, new(OnePlan, CopyInputsOf: "src"));
        var (taskId, error) = await service.SubmitAsync(Alice, new(OnePlan, CopyInputsOf: "src", Files: [File("a.md", "new a")]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Contains("of yours", bobError);
        Assert.Null(error);
        Assert.NotEqual("w-src", done.WorkspaceId);
        Assert.Equal(["a.md", "b.pdf"], done.Inputs.Order(StringComparer.Ordinal));
        Assert.Equal(["inputs/a.md", "inputs/b.pdf"], (await PathsAsync(Alice, done.WorkspaceId)).Where(p => p.StartsWith("inputs/", StringComparison.Ordinal)));
        Assert.Equal("new a", Encoding.UTF8.GetString((await _store.GetWorkspaceFileAsync(Alice, done.WorkspaceId, "inputs/a.md"))!));
    }

    /// <summary>ADR 0101: deleting a workspace's last task drops its document chunks BEFORE the workspace; a shared
    /// workspace keeps them; an index failure leaves the task and the workspace intact for a retry.</summary>
    [Theory]
    [InlineData("last")]
    [InlineData("shared")]
    [InlineData("failing")]
    public async Task Delete_LastTask_DropsIndexChunksFirst(string scenario)
    {
        var index = new OrderedDeleteIndex(_store, Alice, "w-t1") { Fail = scenario == "failing" };
        await _store.SaveTaskAsync(Record("t1", Alice, FleetTaskStatus.Done) with { WorkspaceId = "w-t1" });
        if (scenario == "shared")
        {
            await _store.SaveTaskAsync(Record("t2", Alice, FleetTaskStatus.Done) with { WorkspaceId = "w-t1" });
        }

        await _store.PutWorkspaceFileAsync(Alice, "w-t1", "inputs/a.md", [1]);
        var service = Service(documents: index);

        if (scenario == "failing")
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DeleteAsync(Alice, "t1"));
            Assert.NotNull(await _store.GetTaskAsync(Alice, "t1"));
            Assert.NotEmpty(await PathsAsync(Alice, "w-t1"));
            return;
        }

        Assert.Equal(FleetOutcome.Ok, await service.DeleteAsync(Alice, "t1"));
        Assert.Equal(scenario == "last" ? [FleetTaskService.DocumentOwnerKey(Alice, "w-t1")] : [], index.Deleted);
        Assert.True(index.WorkspaceExistedAtDelete);
        Assert.Equal(scenario == "shared", (await PathsAsync(Alice, "w-t1")).Count > 0);
    }

    private static FleetInputFile Docx(string name, params string[] paragraphs)
        => new(name, DocumentTextExtractorTests.BuildDocx(paragraphs));

    // The documents.ingested event's files, by name.
    private async Task<Dictionary<string, JsonElement>> IngestedAsync(string user, string taskId)
    {
        var line = (await _store.ReadEventsAsync(user, taskId)).Single(l => TypeOf(l) == "documents.ingested");
        return JsonDocument.Parse(line).RootElement.GetProperty("data").GetProperty("files").EnumerateArray()
            .ToDictionary(f => f.GetProperty("name").GetString()!, f => f.Clone());
    }

    /// <summary>ADR 0101: at run start a document is extracted, gets a text copy when it isn't text already, and is
    /// indexed under the workspace's owner key; a non-document is stored only; task.started stays the first event.</summary>
    [Fact]
    public async Task Run_IngestsDocuments_AndStoresOthers()
    {
        var index = new InMemoryDocumentIndex();
        var service = Service(documents: index, embeddings: new DeterministicEmbeddingGenerator());

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan,
            Files: [Docx("report.docx", "Magrathea builds luxury planets."), File("notes.md"), File("data.csv", "a,b")]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Equal(["report.docx", "notes.md"], done.Documents);
        Assert.Equal(["inputs/report.docx.txt"], done.TextCopies);
        Assert.Contains("Magrathea", Encoding.UTF8.GetString((await _store.GetWorkspaceFileAsync(Alice, done.WorkspaceId, "inputs/report.docx.txt"))!));
        var ownerKey = FleetTaskService.DocumentOwnerKey(Alice, done.WorkspaceId);
        Assert.Equal($"{Alice}/fleet/{done.WorkspaceId}", ownerKey);
        Assert.True((await index.ReadChunksAsync(ownerKey, "report.docx", 0, 10)).TotalChunks > 0);
        Assert.True((await index.ReadChunksAsync(ownerKey, "notes.md", 0, 10)).TotalChunks > 0);

        var files = await IngestedAsync(Alice, taskId!);
        Assert.Equal(FleetDocumentIngest.Indexed, files["report.docx"].GetProperty("status").GetString());
        Assert.Equal("inputs/report.docx.txt", files["report.docx"].GetProperty("textCopy").GetString());
        Assert.Equal(FleetDocumentIngest.StoredOnly, files["data.csv"].GetProperty("status").GetString());
        var types = (await _store.ReadEventsAsync(Alice, taskId!)).Select(TypeOf).ToList();
        Assert.Equal("task.started", types[0]);
        Assert.True(types.IndexOf("documents.ingested") < types.IndexOf("team.started"));
    }

    /// <summary>ADR 0101: with indexed documents the planner's tool list has the document tools, even with an empty
    /// tool override, and its input the "Attached files" section; a task without documents gets neither.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Run_PlannerSeesDocumentTools(bool withFile)
    {
        var client = new SystemPromptRecorder(new FakeChatClient());
        var service = Service(client: client, documents: new InMemoryDocumentIndex(), embeddings: new DeterministicEmbeddingGenerator());

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, EnabledTools: [], Files: withFile ? [File("notes.md")] : null));
        await FinishedAsync(service, Alice, taskId!);

        var planner = client.SystemPrompts.First(p => p.Contains("- write_artifact:", StringComparison.Ordinal));
        Assert.Equal(withFile, planner.Contains("- search_documents:", StringComparison.Ordinal));
        Assert.Equal(withFile, planner.Contains("- read_document:", StringComparison.Ordinal));
        var input = JsonDocument.Parse((await _store.ReadEventsAsync(Alice, taskId!)).First(l => TypeOf(l) == "team.started"))
            .RootElement.GetProperty("data").GetProperty("task").GetString();
        Assert.Equal(withFile, input!.Contains("Attached files, in the shared workspace:\n- inputs/notes.md", StringComparison.Ordinal));
    }

    /// <summary>ADR 0101: without an embedding model the task runs with its files (and text copies), nothing is
    /// indexed, and the event says why.</summary>
    [Fact]
    public async Task Run_NoEmbeddingModel_FilesOnly()
    {
        var service = Service(documents: new InMemoryDocumentIndex());

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File("notes.md"), Docx("report.docx", "Text")]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Empty(done.Documents);
        Assert.Contains("inputs/notes.md", await PathsAsync(Alice, done.WorkspaceId));
        Assert.Contains("inputs/report.docx.txt", await PathsAsync(Alice, done.WorkspaceId));
        var notes = (await IngestedAsync(Alice, taskId!))["notes.md"];
        Assert.Equal(FleetDocumentIngest.NotIndexed, notes.GetProperty("status").GetString());
        Assert.Contains("embedding model", notes.GetProperty("reason").GetString());
    }

    /// <summary>ADR 0101: no ingest failure fails the task: a throwing provider or index is that file's reason.</summary>
    [Theory]
    [InlineData("embedder")]
    [InlineData("index")]
    public async Task Run_IngestFailure_DoesNotFailTask(string failing)
    {
        var index = new FailingIndex { FailUpsert = failing == "index" };
        var embeddings = new SwitchableEmbeddings { Fail = failing == "embedder" };
        var service = Service(documents: index, embeddings: embeddings);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File("notes.md")]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Empty(done.Documents);
        var notes = (await IngestedAsync(Alice, taskId!))["notes.md"];
        Assert.Equal(FleetDocumentIngest.NotIndexed, notes.GetProperty("status").GetString());
        Assert.Contains("down", notes.GetProperty("reason").GetString());
    }

    /// <summary>ADR 0101: a text copy that doesn't fit the workspace caps is skipped and reported; the document is
    /// still indexed.</summary>
    [Fact]
    public async Task Run_TextCopy_SkippedWhenWorkspaceFull()
    {
        var docx = Docx("report.docx", string.Join(' ', Enumerable.Repeat("Magrathea builds luxury planets.", 50)));
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1, MaxWorkspaceBytes = docx.Content.Length + 10 },
            documents: new InMemoryDocumentIndex(), embeddings: new DeterministicEmbeddingGenerator());

        var (taskId, error) = await service.SubmitAsync(Alice, new(OnePlan, Files: [docx]));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Null(error);
        Assert.Equal(["report.docx"], done.Documents);
        Assert.Empty(done.TextCopies);
        var report = (await IngestedAsync(Alice, taskId!))["report.docx"];
        Assert.Equal("workspace full", report.GetProperty("textCopySkipped").GetString());
        Assert.DoesNotContain("inputs/report.docx.txt", await PathsAsync(Alice, done.WorkspaceId));
    }

    /// <summary>ADR 0101: a follow-up inherits the workspace's documents without re-ingesting them, and its input says
    /// which files the document tools reach.</summary>
    [Fact]
    public async Task FollowUp_InheritsDocuments()
    {
        var service = Service(documents: new InMemoryDocumentIndex(), embeddings: new DeterministicEmbeddingGenerator());
        var (firstId, _) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File("notes.md")]));
        await FinishedAsync(service, Alice, firstId!);

        var (secondId, _) = await service.SubmitAsync(Alice, new(Task(FleetRunHarness.Plan("Sequential", "Editor")), FollowUpOf: firstId));
        var second = await FinishedAsync(service, Alice, secondId!);

        Assert.Equal(["notes.md"], second.Documents);
        Assert.Empty(second.Inputs);
        var events = await _store.ReadEventsAsync(Alice, secondId!);
        Assert.DoesNotContain(events, l => TypeOf(l) == "documents.ingested");
        var input = JsonDocument.Parse(events.First(l => TypeOf(l) == "team.started")).RootElement.GetProperty("data").GetProperty("task").GetString();
        Assert.Contains("search_documents", input);
        Assert.Contains("'notes.md'", input);
    }

    /// <summary>ADR 0101: a follow-up that re-uploads an indexed name and fails to re-index it drops the stale chunks
    /// and the name, so no tool returns the old version of changed bytes.</summary>
    [Fact]
    public async Task FollowUp_FailedReingest_DropsStaleDocument()
    {
        var index = new InMemoryDocumentIndex();
        var embeddings = new SwitchableEmbeddings();
        var service = Service(documents: index, embeddings: embeddings);
        var (firstId, _) = await service.SubmitAsync(Alice, new(OnePlan, Files: [File("notes.md")]));
        var first = await FinishedAsync(service, Alice, firstId!);
        var ownerKey = FleetTaskService.DocumentOwnerKey(Alice, first.WorkspaceId);
        Assert.True((await index.ReadChunksAsync(ownerKey, "notes.md", 0, 10)).TotalChunks > 0);

        embeddings.Fail = true;
        var (secondId, _) = await service.SubmitAsync(Alice, new(Task(FleetRunHarness.Plan("Sequential", "Editor")),
            FollowUpOf: firstId, Files: [File("notes.md", "Changed text about Arthur Dent.")]));
        var second = await FinishedAsync(service, Alice, secondId!);

        Assert.Empty(second.Documents);
        Assert.Equal(0, (await index.ReadChunksAsync(ownerKey, "notes.md", 0, 10)).TotalChunks);
    }

    /// <summary>ADR 0101 (critic finding): a follow-up's user file that takes a generated copy's path is the user's from
    /// then on, and a re-uploaded document whose extraction fails loses its old, now stale, text copy.</summary>
    [Theory]
    [InlineData("user file claims the path")]
    [InlineData("re-upload fails to extract")]
    public async Task FollowUp_TextCopies_StayAccurate(string scenario)
    {
        var service = Service(documents: new InMemoryDocumentIndex(), embeddings: new DeterministicEmbeddingGenerator());
        var (firstId, _) = await service.SubmitAsync(Alice, new(OnePlan, Files: [Docx("report.docx", "Magrathea builds planets.")]));
        var first = await FinishedAsync(service, Alice, firstId!);
        Assert.Equal(["inputs/report.docx.txt"], first.TextCopies);

        FleetInputFile upload = scenario == "user file claims the path"
            ? File("report.docx.txt", "The user's own notes.")
            : new("report.docx", [1, 2, 3]);
        var (secondId, _) = await service.SubmitAsync(Alice, new(Task(FleetRunHarness.Plan("Sequential", "Editor")), FollowUpOf: firstId, Files: [upload]));
        var second = await FinishedAsync(service, Alice, secondId!);

        Assert.Empty(second.TextCopies);
        var stored = await _store.GetWorkspaceFileAsync(Alice, first.WorkspaceId, "inputs/report.docx.txt");
        if (scenario == "user file claims the path")
        {
            Assert.Equal("The user's own notes.", Encoding.UTF8.GetString(stored!));
        }
        else
        {
            Assert.Null(stored);
            Assert.Empty(second.Documents);
        }
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

    /// <summary>ADR 0115: a task started from a chat approves its plan without opening a question, and logs it.</summary>
    [Fact]
    public async Task OriginTask_NeverOpensQuestion()
    {
        var service = Service(Held);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Equal("conv-1", done.OriginConversationId);
        var types = (await _store.ReadEventsAsync(Alice, taskId!)).Select(TypeOf).ToList();
        Assert.DoesNotContain("prompt", types);
        Assert.Contains("question.auto", types);
    }

    /// <summary>Regression for ADR 0115: a page task (no origin) still asks before it runs a team.</summary>
    [Fact]
    public async Task PageTask_StillAsks()
    {
        var service = Service(Held);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan));

        await QuestionAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Running, (await service.GetAsync(Alice, taskId!))!.Status);
        await service.StopAsync(Alice, taskId!);
    }

    [Fact]
    public async Task Answer_AutoTask_Conflict()
    {
        var gate = new GatedChatClient(new FakeChatClient());
        var service = Service(Held, client: gate);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FleetOutcome.Conflict, await service.AnswerAsync(Alice, taskId!, "y"));
        await service.StopAsync(Alice, taskId!);
    }

    [Fact]
    public async Task Stop_AutoTask_Stops()
    {
        var gate = new GatedChatClient(new FakeChatClient());
        var service = Service(Held, client: gate);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FleetOutcome.Ok, await service.StopAsync(Alice, taskId!));

        var stopped = await FinishedAsync(service, Alice, taskId!);
        Assert.Equal(FleetTaskStatus.Stopped, stopped.Status);
    }

    /// <summary>ADR 0115: a chat-started task that finishes reports to its conversation once, after its final save.</summary>
    [Fact]
    public async Task Finish_Done_WithOrigin_NotifiesOnce()
    {
        var notifier = new RecordingNotifier(_store);
        var service = Service(notifier: notifier);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        var notified = await notifier.Next.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(taskId, notified.Id);
        Assert.Equal(FleetTaskStatus.Done, notified.Status);
        Assert.Equal("conv-1", notified.OriginConversationId);
        Assert.Contains("help you today", notified.Result);
        Assert.Equal(FleetTaskStatus.Done, notifier.StoredStatusAtNotify);
        Assert.Single(notifier.Records);
    }

    [Fact]
    public async Task Finish_Failed_NotifiesWithReason()
    {
        var notifier = new RecordingNotifier(_store);
        var service = Service(notifier: notifier);

        await service.SubmitAsync(Alice, new(Task(), OriginConversationId: "conv-1"));
        var notified = await notifier.Next.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FleetTaskStatus.Failed, notified.Status);
        Assert.Equal(FleetTeamRunner.NoTeamResult, notified.Reason);
        Assert.Contains(FleetTeamRunner.NoTeamResult, FleetToolLimits.FrameResult(notified));
    }

    [Fact]
    public async Task Finish_Stopped_NotifiesWithReason()
    {
        var notifier = new RecordingNotifier(_store);
        var gate = new GatedChatClient(new FakeChatClient());
        var service = Service(client: gate, notifier: notifier);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        await service.StopAsync(Alice, taskId!);
        var notified = await notifier.Next.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FleetTaskStatus.Stopped, notified.Status);
        Assert.Equal("stopped by the user", notified.Reason);
    }

    /// <summary>A page task (no origin) has nobody to report to. A chat task submitted after it is the only one told.</summary>
    [Fact]
    public async Task Finish_NoOrigin_DoesNotNotify()
    {
        var notifier = new RecordingNotifier(_store);
        var service = Service(notifier: notifier);

        var (pageTask, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, pageTask!);
        var (chatTask, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await notifier.Next.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal([chatTask], notifier.Records.Select(r => r.Id));
    }

    [Fact]
    public async Task Finish_NotifierThrows_TaskStillDone()
    {
        var notifier = new RecordingNotifier(_store, throws: true);
        var service = Service(notifier: notifier);

        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await notifier.Next.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(FleetTaskStatus.Done, (await FinishedAsync(service, Alice, taskId!)).Status);
        var (next, error) = await service.SubmitAsync(Alice, new(OnePlan));
        Assert.Null(error);
        await FinishedAsync(service, Alice, next!);
    }

    [Fact]
    public async Task Shutdown_Interrupted_DoesNotNotify()
    {
        var notifier = new RecordingNotifier(_store);
        var gate = new GatedChatClient(new FakeChatClient());
        var service = Service(client: gate, notifier: notifier);
        var (taskId, _) = await service.SubmitAsync(Alice, new(OnePlan, OriginConversationId: "conv-1"));
        await gate.Entered.WaitAsync(TimeSpan.FromSeconds(30));

        // StopAsync awaits each running task's completion, which includes the notifier call.
        await service.StopAsync(CancellationToken.None);

        Assert.Equal(FleetTaskStatus.Interrupted, (await _store.GetTaskAsync(Alice, taskId!))!.Status);
        Assert.Empty(notifier.Records);
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

    /// <summary>ADR 0117: <c>todos.updated</c> carries the whole list, so every one is kept in order (never coalesced, and
    /// not dropped at the cap like text), and the page rebuilds the latest list from a replay.</summary>
    [Fact]
    public async Task TodosUpdated_IsPersistedAndReplayed()
    {
        var log = new FleetEventLog(_store, Alice, "t1", maxBytes: 300);
        var todos = new FleetTodos(log, "team-1", 1);
        await todos.Tools.Single(t => t.Name == "todo_add").InvokeAsync(new() { ["titles"] = new[] { "one", "two" } });
        for (var i = 0; i < 20; i++)
        {
            log.Publish("agent.text", new { teamId = "team-1", attempt = 1, executorId = $"Writer_{i}", text = new string('x', 40) });
        }

        await todos.Tools.Single(t => t.Name == "todo_complete").InvokeAsync(new() { ["ids"] = new[] { 2 } });
        await log.FlushAsync();

        var lines = (await _store.ReadEventsAsync(Alice, "t1")).Where(l => TypeOf(l) == "todos.updated").ToList();
        Assert.Equal(2, lines.Count);
        using var last = System.Text.Json.JsonDocument.Parse(lines[^1]);
        var items = last.RootElement.GetProperty("data").GetProperty("todos");
        Assert.Equal([false, true], items.EnumerateArray().Select(i => i.GetProperty("done").GetBoolean()));
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
        // Interrupted can't happen here: the task is in the registry until its final state is saved.
        var record = await TestWait.WaitForAsync(
            () => service.GetAsync(user, taskId), r => r is { Status: not FleetTaskStatus.Running }, "the task to finish");
        return record!;
    }

    private static (string Name, Dictionary<string, object?> Arguments) AskCall(string question, params string[] choices)
        => ("ask_user", choices.Length == 0
            ? new() { ["question"] = question }
            : new() { ["question"] = question, ["choices"] = choices });

    private static (string Name, Dictionary<string, object?> Arguments) ClearCall(string brief)
        => ("requirements_clear", new() { ["brief"] = brief });

    private static FakeChatClient.FleetScript ClarifyScript(params (string Name, Dictionary<string, object?> Arguments)[] calls)
        => new([FleetRunHarness.Plan("Sequential", "Writer")], scores: [9], clarifyCalls: calls);

    /// <summary>ADR 0119: self-review is stored only for a page task that asked for it, and never for a chat-started one.</summary>
    [Fact]
    public async Task Submit_SelfReview_IsStoredOnlyForPageTasks()
    {
        var service = Service();

        var (plain, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, plain!);
        var (reviewing, _) = await service.SubmitAsync(Alice, new(OnePlan, SelfReview: true));
        await FinishedAsync(service, Alice, reviewing!);
        var (chat, _) = await service.SubmitAsync(Alice, new(OnePlan, SelfReview: true, OriginConversationId: "conv-1"));
        await FinishedAsync(service, Alice, chat!);

        Assert.False((await _store.GetTaskAsync(Alice, plain!))!.SelfReview);
        Assert.True((await _store.GetTaskAsync(Alice, reviewing!))!.SelfReview);
        Assert.False((await _store.GetTaskAsync(Alice, chat!))!.SelfReview);
    }

    /// <summary>ADR 0118: the flag is stored only for a page task that asked for it, and never for a chat-started one.</summary>
    [Fact]
    public async Task Submit_Clarify_IsStoredOnlyForPageTasks()
    {
        var service = Service();

        var (plain, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, plain!);
        // A clarify task whose interviewer asks nothing runs straight through (the canned interviewer just replies).
        var (clarifying, _) = await service.SubmitAsync(Alice, new(OnePlan, Clarify: true));
        await FinishedAsync(service, Alice, clarifying!);
        var (chat, _) = await service.SubmitAsync(Alice, new(OnePlan, Clarify: true, OriginConversationId: "conv-1"));
        await FinishedAsync(service, Alice, chat!);

        Assert.False((await _store.GetTaskAsync(Alice, plain!))!.Clarify);
        Assert.True((await _store.GetTaskAsync(Alice, clarifying!))!.Clarify);
        Assert.False((await _store.GetTaskAsync(Alice, chat!))!.Clarify);
        Assert.DoesNotContain("clarify.started", (await _store.ReadEventsAsync(Alice, chat!)).Select(TypeOf));
    }

    /// <summary>The whole feature through the service: the interviewer's question reaches the page as a prompt event,
    /// the answer comes back through AnswerAsync, and the planner and judge get the brief while the record keeps the
    /// user's own words.</summary>
    [Fact]
    public async Task Run_Clarified_PlannerGetsBrief_RecordKeepsRequest()
    {
        var script = ClarifyScript(AskCall("Who reads it?", "Engineers", "Children"), ClearCall("Haiku for engineers"));
        var request = FakeChatClient.FleetTask("Write a haiku", script);
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 1 });
        var (taskId, _) = await service.SubmitAsync(Alice, new(request, Clarify: true));

        await QuestionAsync(service, Alice, taskId!);
        Assert.Equal(FleetOutcome.Ok, await service.AnswerAsync(Alice, taskId!, "Engineers"));
        var done = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Equal(request, done.Request);
        Assert.Contains("Haiku for engineers", Assert.Single(script.JudgeInputs));
        Assert.Contains("Write a haiku", Assert.Single(script.ClarifyInputs));
        var types = (await _store.ReadEventsAsync(Alice, taskId!)).Select(TypeOf).ToList();
        Assert.True(types.IndexOf("clarify.started") < types.IndexOf("prompt"));
        Assert.True(types.IndexOf("clarify.done") < types.IndexOf("plan.submitted"));
    }

    /// <summary>A follow-up clarifies only its own request: the brief goes into the follow-up input, and the earlier
    /// request is the interviewer's one line of context.</summary>
    [Fact]
    public async Task Run_FollowUpClarified_UsesBriefInFollowUpInput()
    {
        var service = Service(new FleetOptions { ApprovalDepth = -1, JudgeDepth = 1 });
        var (firstId, _) = await service.SubmitAsync(Alice, new(OnePlan));
        await FinishedAsync(service, Alice, firstId!);
        var script = ClarifyScript(ClearCall("Now in Dutch, five lines"));
        var request = FakeChatClient.FleetTask("Translate it", script);

        var (secondId, _) = await service.SubmitAsync(Alice, new(request, FollowUpOf: firstId, Clarify: true));
        await FinishedAsync(service, Alice, secondId!);

        var judged = Assert.Single(script.JudgeInputs);
        Assert.Contains("Now in Dutch, five lines", judged);
        Assert.Contains("follow-up on earlier work", judged);
        Assert.Contains("Write a haiku about lighthouses", Assert.Single(script.ClarifyInputs));
    }

    /// <summary>Stopping while the interviewer waits for an answer ends the task as stopped, not failed.</summary>
    [Fact]
    public async Task Run_StopDuringClarify_IsStopped()
    {
        var script = ClarifyScript(AskCall("Who reads it?"), ClearCall("never reached"));
        var service = Service();
        var (taskId, _) = await service.SubmitAsync(Alice, new(FakeChatClient.FleetTask("Write a haiku", script), Clarify: true));
        await QuestionAsync(service, Alice, taskId!);

        await service.StopAsync(Alice, taskId!);
        var stopped = await FinishedAsync(service, Alice, taskId!);

        Assert.Equal(FleetTaskStatus.Stopped, stopped.Status);
        Assert.Equal(0, script.PlannerCalls);
    }

    /// <summary>A reload mid-interview replays the question and the interview's events from the persisted log.</summary>
    [Fact]
    public async Task Events_Clarify_ReplayedAfterReload()
    {
        var script = ClarifyScript(AskCall("Who reads it?"), ClearCall("Haiku for engineers"));
        var service = Service();
        var (taskId, _) = await service.SubmitAsync(Alice, new(FakeChatClient.FleetTask("Write a haiku", script), Clarify: true));
        await QuestionAsync(service, Alice, taskId!);
        await service.AnswerAsync(Alice, taskId!, "Engineers");
        await FinishedAsync(service, Alice, taskId!);

        var replay = new List<string>();
        await foreach (var line in (await service.SubscribeAsync(Alice, taskId!))!)
        {
            replay.Add(TypeOf(line));
        }

        Assert.Equal(
            ["clarify.started", "prompt", "prompt.answered", "clarify.done"],
            replay.Where(t => t.StartsWith("clarify", StringComparison.Ordinal) || t.StartsWith("prompt", StringComparison.Ordinal)));
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

    // The deterministic generator, or a provider that is down.
    private sealed class SwitchableEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
    {
        private readonly DeterministicEmbeddingGenerator _inner = new();

        public bool Fail { get; set; }

        public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
            => Fail ? throw new HttpRequestException("provider down") : _inner.GenerateAsync(values, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => _inner.Dispose();
    }

    // The in-memory index, with an upsert that can fail like a Cosmos outage.
    private sealed class FailingIndex : IDocumentIndex
    {
        private readonly InMemoryDocumentIndex _inner = new();

        public bool FailUpsert { get; init; }

        public int? ExpectedDimensions => null;

        public Task UpsertChunksAsync(string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => FailUpsert ? throw new InvalidOperationException("index down") : _inner.UpsertChunksAsync(ownerKey, chunks, cancellationToken);

        public Task<IReadOnlyList<DocumentChunk>> SearchAsync(string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
            => _inner.SearchAsync(ownerKey, queryEmbedding, topK, cancellationToken);

        public Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
            => _inner.DeleteAsync(ownerKey, cancellationToken);

        public Task<DocumentChunkRange> ReadChunksAsync(string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default)
            => _inner.ReadChunksAsync(ownerKey, fileName, startChunkIndex, count, cancellationToken);

        public Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default)
            => _inner.DeleteFileAsync(ownerKey, fileName, cancellationToken);
    }

    // Records index deletes, and whether the workspace still had its files at that moment (the delete-order check).
    private sealed class OrderedDeleteIndex(FleetStore store, string user, string workspaceId) : IDocumentIndex
    {
        private readonly InMemoryDocumentIndex _inner = new();

        public bool Fail { get; init; }
        public List<string> Deleted { get; } = [];
        public bool WorkspaceExistedAtDelete { get; private set; } = true;

        public int? ExpectedDimensions => null;

        public Task UpsertChunksAsync(string ownerKey, IReadOnlyList<EmbeddedChunk> chunks, CancellationToken cancellationToken = default)
            => _inner.UpsertChunksAsync(ownerKey, chunks, cancellationToken);

        public Task<IReadOnlyList<DocumentChunk>> SearchAsync(string ownerKey, ReadOnlyMemory<float> queryEmbedding, int topK, CancellationToken cancellationToken = default)
            => _inner.SearchAsync(ownerKey, queryEmbedding, topK, cancellationToken);

        public async Task DeleteAsync(string ownerKey, CancellationToken cancellationToken = default)
        {
            if (Fail)
            {
                throw new InvalidOperationException("index down");
            }

            WorkspaceExistedAtDelete = (await store.ListWorkspaceAsync(user, workspaceId, cancellationToken)).Count > 0;
            Deleted.Add(ownerKey);
        }

        public Task<DocumentChunkRange> ReadChunksAsync(string ownerKey, string fileName, int startChunkIndex, int count, CancellationToken cancellationToken = default)
            => _inner.ReadChunksAsync(ownerKey, fileName, startChunkIndex, count, cancellationToken);

        public Task DeleteFileAsync(string ownerKey, string fileName, CancellationToken cancellationToken = default)
            => _inner.DeleteFileAsync(ownerKey, fileName, cancellationToken);
    }

    // Records every system prompt the run's chat calls carry, as instructions or as system messages.
    /// <summary>Records the finished tasks it is told about, and what the store held at that moment.</summary>
    private sealed class RecordingNotifier(FleetStore store, bool throws = false) : IFleetCompletionNotifier
    {
        private readonly TaskCompletionSource<FleetTaskRecord> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ConcurrentQueue<FleetTaskRecord> Records { get; } = new();

        public string? StoredStatusAtNotify { get; private set; }

        public Task<FleetTaskRecord> Next => _next.Task;

        public async Task NotifyAsync(FleetTaskRecord record, CancellationToken cancellationToken = default)
        {
            StoredStatusAtNotify = (await store.GetTaskAsync(record.UserId, record.Id, cancellationToken))?.Status;
            Records.Enqueue(record);
            _next.TrySetResult(record);
            if (throws)
            {
                throw new InvalidOperationException("the grain is gone");
            }
        }
    }

    /// <summary>Holds every model call until the call's token is cancelled (a stop), so a task that never asks a
    /// question stays running for as long as a test needs.</summary>
    private sealed class GatedChatClient(IChatClient inner) : DelegatingChatClient(inner)
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _entered.TrySetResult();
            await System.Threading.Tasks.Task.Delay(Timeout.Infinite, cancellationToken);
            await foreach (var update in base.GetStreamingResponseAsync(messages, options, cancellationToken))
            {
                yield return update;
            }
        }
    }

    private sealed class SystemPromptRecorder(IChatClient inner) : DelegatingChatClient(inner)
    {
        public System.Collections.Concurrent.ConcurrentQueue<string> SystemPrompts { get; } = new();

        public override Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            return base.GetResponseAsync(messages, options, cancellationToken);
        }

        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Record(messages, options);
            return base.GetStreamingResponseAsync(messages, options, cancellationToken);
        }

        private void Record(IEnumerable<ChatMessage> messages, ChatOptions? options)
        {
            if (options?.Instructions is { } instructions)
            {
                SystemPrompts.Enqueue(instructions);
            }

            foreach (var message in messages.Where(m => m.Role == ChatRole.System))
            {
                SystemPrompts.Enqueue(message.Text);
            }
        }
    }
}
