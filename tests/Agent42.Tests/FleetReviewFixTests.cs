using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Regression tests for the critic findings on the fleet (ADR 0097): a stale read must not mark a finished task
/// interrupted, symbolic links never leave the workspace through a sync, and an answer can't land on another question.
/// </summary>
public sealed class FleetReviewFixTests
{
    /// <summary>
    /// A read that saw the task as running before its final save, and checks the registry only after the task left it,
    /// must return the final state, not overwrite it with interrupted.
    /// </summary>
    [Fact]
    public async Task StaleRunningRead_DoesNotOverwriteFinishedTask()
    {
        var store = new StaleFirstReadStore(new FleetStore(new InMemoryFleetBlobs()));
        var done = new FleetTaskRecord
        {
            Id = "t1",
            UserId = "alice",
            Request = "r",
            WorkspaceId = "w1",
            PlannerModel = FleetRunHarness.ModelA,
            Status = FleetTaskStatus.Done,
            Result = "the result",
            StartedAt = DateTimeOffset.UtcNow,
        };
        await store.SaveTaskAsync(done);
        var service = new FleetTaskService(store, new FleetOptions(),
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA)),
            new SingleUserModelCatalogProvider(new StaticModelCatalog(FleetRunHarness.ModelA, [])),
            NullLogger<FleetTaskService>.Instance);

        var read = await service.GetAsync("alice", "t1");

        Assert.Equal(FleetTaskStatus.Done, read!.Status);
        Assert.Equal(FleetTaskStatus.Done, (await store.Inner.GetTaskAsync("alice", "t1"))!.Status);
    }

    [Fact]
    public async Task Sync_SkipsSymbolicLinks()
    {
        var outside = Path.Combine(Path.GetTempPath(), "fleet-outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "secret");
        using var workspace = new FleetWorkspace(Guid.NewGuid().ToString("N"));
        File.WriteAllText(Path.Combine(workspace.Root, "real.txt"), "ok");
        try
        {
            File.CreateSymbolicLink(Path.Combine(workspace.Root, "link.txt"), outside);
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException)
        {
            // Creating a symlink on Windows needs developer mode or elevation; CI (Linux) always runs this.
            File.Delete(outside);
            return;
        }

        var store = new FleetStore(new InMemoryFleetBlobs());
        await workspace.SyncToStoreAsync(store, "alice", 1_000_000, 100, default);

        Assert.Equal(["real.txt"], workspace.ListFiles());
        Assert.Equal(["real.txt"], (await store.ListWorkspaceAsync("alice", workspace.WorkspaceId)).Select(f => f.Path));
        File.Delete(outside);
    }

    [Fact]
    public async Task Answer_ForAnotherQuestion_Refused()
    {
        var sink = new RecordingFleetSink();
        var questions = new FleetQuestions(sink);

        var asked = questions.AskAsync("Run this team?", [new("y", "Run")], "", default);
        var id = sink.Of("prompt").Single().GetProperty("id").GetInt32();

        Assert.False(questions.Answer("y", id + 1));
        Assert.True(questions.HasOpenQuestion);
        Assert.True(questions.Answer("y", id));
        Assert.Equal("y", await asked);
        Assert.False(questions.Answer("a", id));
    }

    [Fact]
    public async Task Approval_Yes_Runs()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = 0, JudgeDepth = -1 }, answers: ["y"]);

        var result = await h.Runner.RunAsync(FakeChatClient.FleetTask("Write a haiku about lighthouses", new([FleetRunHarness.Plan("Sequential", "Writer")])));

        Assert.Single(h.Interaction.Questions);
        Assert.Single(h.Events.Of("plan.accepted"));
        Assert.Contains("help you today", result);
    }

    // Returns a stale "running" copy on the first read of each task, then the real record.
    private sealed class StaleFirstReadStore(FleetStore inner) : IFleetStore
    {
        private readonly HashSet<string> _seen = [];

        public FleetStore Inner => inner;

        public async Task<FleetTaskRecord?> GetTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        {
            var record = await inner.GetTaskAsync(userId, taskId, cancellationToken);
            return _seen.Add(taskId) && record is not null ? record with { Status = FleetTaskStatus.Running, Result = null } : record;
        }

        public Task<IReadOnlyList<FleetTaskRecord>> ListTasksAsync(string userId, CancellationToken cancellationToken = default) => inner.ListTasksAsync(userId, cancellationToken);
        public Task SaveTaskAsync(FleetTaskRecord task, CancellationToken cancellationToken = default) => inner.SaveTaskAsync(task, cancellationToken);
        public Task DeleteTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default) => inner.DeleteTaskAsync(userId, taskId, cancellationToken);
        public Task AppendEventsAsync(string userId, string taskId, string lines, CancellationToken cancellationToken = default) => inner.AppendEventsAsync(userId, taskId, lines, cancellationToken);
        public Task<IReadOnlyList<string>> ReadEventsAsync(string userId, string taskId, CancellationToken cancellationToken = default) => inner.ReadEventsAsync(userId, taskId, cancellationToken);
        public Task PutWorkspaceFileAsync(string userId, string workspaceId, string path, byte[] content, CancellationToken cancellationToken = default) => inner.PutWorkspaceFileAsync(userId, workspaceId, path, content, cancellationToken);
        public Task<byte[]?> GetWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default) => inner.GetWorkspaceFileAsync(userId, workspaceId, path, cancellationToken);
        public Task<IReadOnlyList<FleetWorkspaceFile>> ListWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.ListWorkspaceAsync(userId, workspaceId, cancellationToken);
        public Task DeleteWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default) => inner.DeleteWorkspaceFileAsync(userId, workspaceId, path, cancellationToken);
        public Task DeleteWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.DeleteWorkspaceAsync(userId, workspaceId, cancellationToken);
        public Task<IReadOnlyDictionary<string, string>> GetPromptsAsync(string userId, CancellationToken cancellationToken = default) => inner.GetPromptsAsync(userId, cancellationToken);
        public Task SetPromptsAsync(string userId, IReadOnlyDictionary<string, string> prompts, CancellationToken cancellationToken = default) => inner.SetPromptsAsync(userId, prompts, cancellationToken);
    }
}
