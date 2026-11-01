using System.Text;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The real <see cref="FleetStore"/> over <see cref="InMemoryFleetBlobs"/> (ADR 0097): the layout, escaping, path
/// refusal, per-user isolation and delete semantics all live in the store, not the Azure adapter.
/// </summary>
public class FleetStoreTests
{
    private readonly InMemoryFleetBlobs _blobs = new();
    private readonly FleetStore _store;

    public FleetStoreTests() => _store = new FleetStore(_blobs);

    private static FleetTaskRecord Task(string user, string id, string workspace, DateTimeOffset startedAt, string? followUpOf = null) => new()
    {
        Id = id,
        UserId = user,
        Request = $"request {id}",
        WorkspaceId = workspace,
        PlannerModel = "foundry:gpt-5",
        StartedAt = startedAt,
        FollowUpOf = followUpOf,
    };

    [Fact]
    public async Task ListTasks_NewestFirst_AndRoundTrips()
    {
        var t0 = DateTimeOffset.Parse("2026-10-12T10:00:00Z");
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", t0));
        await _store.SaveTaskAsync(Task("alice", "a2", "w2", t0.AddMinutes(5)) with
        {
            Status = FleetTaskStatus.Done,
            Result = "the result",
            Notes = new Dictionary<string, string> { ["k"] = "v" },
        });

        var tasks = await _store.ListTasksAsync("alice");

        Assert.Equal(["a2", "a1"], tasks.Select(t => t.Id));
        Assert.Equal("the result", tasks[0].Result);
        Assert.Equal("v", tasks[0].Notes["k"]);
        Assert.True(tasks[0].CanFollowUp);
        Assert.False(tasks[1].CanFollowUp);
    }

    /// <summary>A record written before ADR 0099 has no <c>enabledTools</c>: it loads with null, so it follows the
    /// user's default tool selection, and "Run again" keeps it that way.</summary>
    [Fact]
    public async Task LegacyRecord_WithoutEnabledTools_LoadsAsNull()
    {
        await _blobs.PutAsync("alice/tasks/old.json", Encoding.UTF8.GetBytes(
            """{"id":"old","userId":"alice","request":"r","workspaceId":"w","plannerModel":"foundry:gpt-5","status":"done","result":"x"}"""));

        var task = await _store.GetTaskAsync("alice", "old");

        Assert.NotNull(task);
        Assert.Null(task.EnabledTools);
        Assert.True(task.CanFollowUp);
    }

    // ADR 0115: a task started from a chat remembers its conversation.
    [Fact]
    public async Task Record_OriginConversationId_RoundTrips()
    {
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", DateTimeOffset.Parse("2026-11-01T10:00:00Z")) with { OriginConversationId = "conv-1" });

        var task = await _store.GetTaskAsync("alice", "a1");

        Assert.Equal("conv-1", task!.OriginConversationId);
    }

    // ADR 0115: a record written before chat-started tasks existed has no origin, so it is a page task.
    [Fact]
    public async Task Record_WithoutOrigin_ReadsNull()
    {
        await _blobs.PutAsync("alice/tasks/old.json", Encoding.UTF8.GetBytes(
            """{"id":"old","userId":"alice","request":"r","workspaceId":"w","plannerModel":"foundry:gpt-5","status":"done","result":"x"}"""));

        var task = await _store.GetTaskAsync("alice", "old");

        Assert.NotNull(task);
        Assert.Null(task.OriginConversationId);
    }

    // ADR 0101: records written before attached files existed load with empty lists, and the lists round-trip.
    [Fact]
    public async Task LegacyRecord_WithoutInputs_LoadsEmpty()
    {
        await _blobs.PutAsync("alice/tasks/old.json", Encoding.UTF8.GetBytes(
            """{"id":"old","userId":"alice","request":"r","workspaceId":"w","plannerModel":"foundry:gpt-5","status":"done","result":"x"}"""));

        var task = await _store.GetTaskAsync("alice", "old");

        Assert.NotNull(task);
        Assert.Empty(task.Inputs);
        Assert.Empty(task.Documents);
        Assert.Empty(task.TextCopies);

        await _store.SaveTaskAsync(task with { Inputs = ["a.pdf"], Documents = ["a.pdf"], TextCopies = ["inputs/a.pdf.txt"] });
        var saved = await _store.GetTaskAsync("alice", "old");
        Assert.Equal(["a.pdf"], saved!.Inputs);
        Assert.Equal(["a.pdf"], saved.Documents);
        Assert.Equal(["inputs/a.pdf.txt"], saved.TextCopies);
    }

    [Fact]
    public async Task OtherUser_SeesNothing()
    {
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", DateTimeOffset.UtcNow));
        await _store.AppendEventsAsync("alice", "a1", "{\"type\":\"x\"}\n");
        await _store.PutWorkspaceFileAsync("alice", "w1", "report.md", [1, 2, 3]);
        await _store.SetPromptsAsync("alice", new Dictionary<string, string> { ["judge"] = "be strict" });

        Assert.Empty(await _store.ListTasksAsync("bob"));
        Assert.Null(await _store.GetTaskAsync("bob", "a1"));
        Assert.Empty(await _store.ReadEventsAsync("bob", "a1"));
        Assert.Null(await _store.GetWorkspaceFileAsync("bob", "w1", "report.md"));
        Assert.Empty(await _store.ListWorkspaceAsync("bob", "w1"));
        Assert.Empty(await _store.GetPromptsAsync("bob"));
    }

    /// <summary>A user id holding '/' must not add a level: "alice/tasks" can't read alice's tasks, and the layout
    /// stays one escaped segment per id.</summary>
    [Fact]
    public async Task UserId_IsEscaped()
    {
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", DateTimeOffset.UtcNow));
        await _store.SaveTaskAsync(Task("al/ice", "x1", "w1", DateTimeOffset.UtcNow));

        Assert.Equal(["a1"], (await _store.ListTasksAsync("alice")).Select(t => t.Id));
        Assert.Equal(["x1"], (await _store.ListTasksAsync("al/ice")).Select(t => t.Id));
        var keys = (await _blobs.ListAsync("")).Select(b => b.Key).ToList();
        Assert.Contains("al%2Fice/tasks/x1.json", keys);
        Assert.Contains("alice/tasks/a1.json", keys);
    }

    [Theory]
    [InlineData("../secret.txt")]
    [InlineData("a/../../b.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\windows\\win.ini")]
    [InlineData("C:/x.txt")]
    [InlineData("")]
    [InlineData("./.")]
    public async Task WorkspacePath_TraversalAndRooted_Refused(string path)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutWorkspaceFileAsync("alice", "w1", path, [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetWorkspaceFileAsync("alice", "w1", path));
    }

    [Theory]
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    public async Task Ids_DotSegmentsAndEmpty_Refused(string id)
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ListWorkspaceAsync("alice", id));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.ListTasksAsync(id));
    }

    [Fact]
    public async Task Workspace_PutListGetDelete_WithNestedAndOddNames()
    {
        await _store.PutWorkspaceFileAsync("alice", "w1", "research\\lisbon notes.md", Encoding.UTF8.GetBytes("hi"));
        await _store.PutWorkspaceFileAsync("alice", "w1", "./a/%2F.txt", [1, 2]);

        var files = await _store.ListWorkspaceAsync("alice", "w1");

        Assert.Equal(
            [("a/%2F.txt", 2L), ("research/lisbon notes.md", 2L)],
            files.Select(f => (f.Path, f.Size)).OrderBy(f => f.Path, StringComparer.Ordinal));
        Assert.Equal("hi", Encoding.UTF8.GetString((await _store.GetWorkspaceFileAsync("alice", "w1", "research/lisbon notes.md"))!));

        await _store.DeleteWorkspaceFileAsync("alice", "w1", "a/%2F.txt");
        Assert.Single(await _store.ListWorkspaceAsync("alice", "w1"));
    }

    [Fact]
    public async Task Events_AppendAndRead_InOrder()
    {
        await _store.AppendEventsAsync("alice", "a1", "{\"seq\":1}\n{\"seq\":2}\n");
        await _store.AppendEventsAsync("alice", "a1", "{\"seq\":3}\n");

        Assert.Equal(["{\"seq\":1}", "{\"seq\":2}", "{\"seq\":3}"], await _store.ReadEventsAsync("alice", "a1"));
    }

    /// <summary>A follow-up chain shares one workspace: deleting one task keeps it, deleting the last removes it.</summary>
    [Fact]
    public async Task DeleteTask_KeepsSharedWorkspaceUntilLastTask()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", now));
        await _store.SaveTaskAsync(Task("alice", "a2", "w1", now.AddMinutes(1), followUpOf: "a1"));
        await _store.AppendEventsAsync("alice", "a1", "{}\n");
        await _store.PutWorkspaceFileAsync("alice", "w1", "out.md", [1]);

        await _store.DeleteTaskAsync("alice", "a1");

        Assert.Null(await _store.GetTaskAsync("alice", "a1"));
        Assert.Empty(await _store.ReadEventsAsync("alice", "a1"));
        Assert.Single(await _store.ListWorkspaceAsync("alice", "w1"));

        await _store.DeleteTaskAsync("alice", "a2");

        Assert.Empty(await _store.ListWorkspaceAsync("alice", "w1"));
        Assert.Empty(await _store.ListTasksAsync("alice"));
    }

    /// <summary>The canvas lives under its own escaped prefix (outside the workspace), per user, and a page name
    /// that isn't one safe segment is refused before any blob is touched.</summary>
    [Theory]
    [InlineData("../x")]
    [InlineData("a/b")]
    [InlineData("A")]
    [InlineData("-a")]
    [InlineData("manifest.json")]
    [InlineData("a\n")]
    [InlineData("")]
    public async Task Canvas_PutGetList_PerUser_NamesValidated(string badName)
    {
        await _store.PutCanvasVersionAsync("al/ice", "w1", "report", 2, Encoding.UTF8.GetBytes("<p>v2</p>"));
        await _store.PutCanvasManifestAsync("al/ice", "w1", new FleetCanvasManifest
        {
            Pages = [new FleetCanvasPage { Name = "report", Kind = ArtifactKind.Html, Title = "Report", Versions = [1, 2] }],
        });

        var page = Assert.Single((await _store.GetCanvasManifestAsync("al/ice", "w1")).Pages);
        Assert.Equal(("report", 2), (page.Name, page.LatestVersion));
        Assert.Equal("<p>v2</p>", Encoding.UTF8.GetString((await _store.GetCanvasVersionAsync("al/ice", "w1", "report", 2))!));
        Assert.Null(await _store.GetCanvasVersionAsync("al/ice", "w1", "report", 1));

        Assert.Empty((await _store.GetCanvasManifestAsync("alice", "w1")).Pages);
        Assert.Null(await _store.GetCanvasVersionAsync("alice", "w1", "report", 2));
        Assert.Empty(await _store.ListWorkspaceAsync("al/ice", "w1"));
        var keys = (await _blobs.ListAsync("")).Select(b => b.Key).ToList();
        Assert.Contains("al%2Fice/canvas/w1/manifest.json", keys);
        Assert.Contains("al%2Fice/canvas/w1/report/v2", keys);

        await Assert.ThrowsAsync<ArgumentException>(() => _store.PutCanvasVersionAsync("alice", "w1", badName, 1, [1]));
        await Assert.ThrowsAsync<ArgumentException>(() => _store.GetCanvasVersionAsync("alice", "w1", badName, 1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => _store.GetCanvasVersionAsync("alice", "w1", "report", 0));
    }

    /// <summary>The canvas follows the workspace's lifetime: it goes with the last task that shares it.</summary>
    [Fact]
    public async Task DeleteTask_KeepsSharedCanvasUntilLastTask()
    {
        var now = DateTimeOffset.UtcNow;
        await _store.SaveTaskAsync(Task("alice", "a1", "w1", now));
        await _store.SaveTaskAsync(Task("alice", "a2", "w1", now.AddMinutes(1), followUpOf: "a1"));
        await _store.PutCanvasVersionAsync("alice", "w1", "page", 1, [1]);
        await _store.PutCanvasManifestAsync("alice", "w1", new FleetCanvasManifest
        {
            Pages = [new FleetCanvasPage { Name = "page", Kind = ArtifactKind.Markdown, Title = "Page", Versions = [1] }],
        });

        await _store.DeleteTaskAsync("alice", "a1");

        Assert.Single((await _store.GetCanvasManifestAsync("alice", "w1")).Pages);
        Assert.NotNull(await _store.GetCanvasVersionAsync("alice", "w1", "page", 1));

        await _store.DeleteTaskAsync("alice", "a2");

        Assert.Empty((await _store.GetCanvasManifestAsync("alice", "w1")).Pages);
        Assert.Empty(await _blobs.ListAsync("alice/canvas/"));
    }

    [Fact]
    public async Task DeleteTask_Unknown_IsNoOp()
        => await _store.DeleteTaskAsync("alice", "missing");

    [Fact]
    public async Task Prompts_RoundTrip()
    {
        Assert.Empty(await _store.GetPromptsAsync("alice"));

        await _store.SetPromptsAsync("alice", new Dictionary<string, string> { ["planner"] = "custom" });

        Assert.Equal("custom", (await _store.GetPromptsAsync("alice"))["planner"]);
    }
}
