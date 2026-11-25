using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>A whole fleet task with the user's lessons (ADR 0124): what the planner gets, what the stored record keeps, and that
/// a failing or slow lessons store never fails or delays planning.</summary>
public sealed class FleetTaskLessonsTests : IAsyncDisposable
{
    private const string User = "alice";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private readonly GatedLessonBlobs _blobs = new();
    private readonly FleetStore _store;
    private readonly List<FleetTaskService> _services = [];

    public FleetTaskLessonsTests() => _store = new FleetStore(_blobs);

    public async ValueTask DisposeAsync()
    {
        foreach (var service in _services)
        {
            await service.StopAsync(CancellationToken.None);
        }
    }

    private (FleetTaskService Tasks, FleetLessonService Lessons) Services()
    {
        var options = new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 };
        var provider = new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), LessonCatalogs.ModelA));
        var catalogs = new LessonCatalogs();
        var lessons = new FleetLessonService(_store, options, provider, catalogs, NullLogger<FleetLessonService>.Instance);
        var tasks = new FleetTaskService(_store, options, provider, catalogs, NullLogger<FleetTaskService>.Instance, lessons: lessons);
        _services.Add(tasks);
        return (tasks, lessons);
    }

    private static FleetLesson Lesson(string id, string text, string status = FleetLessonStatus.Active, int ageDays = 1)
        => new() { Id = id, Text = text, Status = status, CreatedAt = Now.AddDays(-ageDays), LastSupportedAt = Now.AddDays(-ageDays) };

    private static async Task<FleetTaskRecord> Finished(FleetTaskService tasks, string taskId)
    {
        for (var i = 0; i < 500; i++)
        {
            if (await tasks.GetAsync(User, taskId) is { Status: not FleetTaskStatus.Running } record)
            {
                return record;
            }

            await Task.Delay(20);
        }

        throw new TimeoutException("the task did not finish");
    }

    private async Task<(string TaskId, FakeChatClient.FleetScript Script)> Submit(FleetTaskService tasks)
    {
        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")]);
        var (id, error) = await tasks.SubmitAsync(User, new(FakeChatClient.FleetTask("Write a haiku", script), Model: LessonCatalogs.ModelA));
        Assert.Null(error);
        return (id!, script);
    }

    [Theory]
    [InlineData(LessonCatalogs.ModelB, null, LessonCatalogs.ModelB)]
    [InlineData(LessonCatalogs.ModelB, LessonCatalogs.ModelA, LessonCatalogs.ModelA)]
    [InlineData("fake:gone", null, LessonCatalogs.ModelA)]
    [InlineData(null, null, LessonCatalogs.ModelA)]
    public async Task ATasksModel_IsTheRequests_ElseTheUsersFleetDefault_ElseTheCatalogDefault(string? saved, string? requested, string expected)
    {
        var options = new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 };
        var provider = new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), LessonCatalogs.ModelA));
        var catalogs = new LessonCatalogs();
        var settings = new FleetSettingsService(options, _store, catalogs);
        var tasks = new FleetTaskService(_store, options, provider, catalogs, NullLogger<FleetTaskService>.Instance, settings: settings);
        _services.Add(tasks);
        // Stored directly: a hand-edited or stale value must fall back, never fail the task.
        await _store.SetSettingsAsync(User, new FleetUserSettings { DefaultModel = saved });

        var script = new FakeChatClient.FleetScript([FleetRunHarness.Plan("Sequential", "Writer")]);
        var (id, error) = await tasks.SubmitAsync(User, new(FakeChatClient.FleetTask("Write a haiku", script), Model: requested));

        Assert.Null(error);
        Assert.Equal(expected, (await Finished(tasks, id!)).PlannerModel);
    }

    [Fact]
    public async Task APlannerGetsOnlyActiveUnexpiredLessons_AndTheStoredRecordKeepsWhatWasApplied()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument
        {
            Lessons = [Lesson("l1", "Stage research first."), Lesson("l2", "Disabled advice.", FleetLessonStatus.Disabled), Lesson("l3", "Expired advice.", ageDays: 200)],
        });
        var (tasks, lessons) = Services();

        var (taskId, script) = await Submit(tasks);
        var done = await Finished(tasks, taskId);

        var input = Assert.Single(script.PlannerInputs);
        Assert.Contains("Stage research first.", input);
        Assert.DoesNotContain("Disabled advice.", input);
        Assert.DoesNotContain("Expired advice.", input);
        // 8b: the final save of the task must not overwrite what the first application recorded.
        Assert.Equal(FleetTaskStatus.Done, done.Status);
        var applied = Assert.Single(done.AppliedLessons);
        Assert.Equal(("l1", "Stage research first."), (applied.Id, applied.Text));
        Assert.Equal(["l1"], (await _store.GetTaskAsync(User, taskId))!.AppliedLessons.Select(l => l.Id));
        // 7: one application per task, counted once.
        var lesson = (await lessons.GetAsync(User)).Lessons.Single(l => l.Id == "l1");
        Assert.Equal((1, taskId), (lesson.AppliedCount, Assert.Single(lesson.Applications).TaskId));
        Assert.Contains((await _store.ReadEventsAsync(User, taskId)), line => line.Contains("lessons.applied", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATaskWithoutLessons_RecordsNothing()
    {
        var (tasks, _) = Services();

        var (taskId, script) = await Submit(tasks);
        var done = await Finished(tasks, taskId);

        Assert.Empty(done.AppliedLessons);
        Assert.DoesNotContain("Lessons from", Assert.Single(script.PlannerInputs));
    }

    [Fact]
    public async Task AFailingLessonsRead_DoesNotFailTheTask()
    {
        _blobs.FailReads = true;
        var (tasks, _) = Services();

        var (taskId, script) = await Submit(tasks);
        var done = await Finished(tasks, taskId);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.DoesNotContain("Lessons from", Assert.Single(script.PlannerInputs));
    }

    [Fact]
    public async Task AThrowingCounterWrite_NeitherFailsTheTask_NorLosesTheRecordedLessons()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("l1", "Stage research first.")] });
        _blobs.FailWrites = true;
        var (tasks, _) = Services();

        var (taskId, _) = await Submit(tasks);
        var done = await Finished(tasks, taskId);

        Assert.Equal(FleetTaskStatus.Done, done.Status);
        Assert.Equal(["l1"], done.AppliedLessons.Select(l => l.Id));
    }

    [Fact]
    public async Task ASlowCounterWrite_DoesNotDelayPlanning()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("l1", "Stage research first.")] });
        var gate = new TaskCompletionSource();
        _blobs.WriteGate = gate;
        var (tasks, _) = Services();

        var (taskId, script) = await Submit(tasks);

        // The planner is called while the counters' write is still blocked: planning did not wait for it.
        for (var i = 0; i < 250 && script.PlannerInputs.IsEmpty; i++)
        {
            await Task.Delay(20);
        }

        Assert.Contains("Stage research first.", Assert.Single(script.PlannerInputs));
        Assert.Equal(FleetTaskStatus.Running, (await tasks.GetAsync(User, taskId))!.Status);
        gate.SetResult();
        Assert.Equal(FleetTaskStatus.Done, (await Finished(tasks, taskId)).Status);
    }

    /// <summary>In-memory blobs whose lessons blob can fail to read, fail to write, or block its write.</summary>
    private sealed class GatedLessonBlobs : IFleetBlobs
    {
        private readonly InMemoryFleetBlobs _inner = new();
        private int _lessonWrites;

        public bool FailReads { get; set; }
        public bool FailWrites { get; set; }
        public TaskCompletionSource? WriteGate { get; set; }

        private static bool IsLessons(string key) => key.EndsWith("lessons.json", StringComparison.Ordinal);

        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
            => FailReads && IsLessons(key) ? throw new IOException("storage down") : _inner.GetAsync(key, cancellationToken);

        public async Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default)
        {
            if (IsLessons(key) && (FailWrites || WriteGate is not null))
            {
                // The test's own seeding write happens before the flag is set, so only the run's counter write is held back.
                Interlocked.Increment(ref _lessonWrites);
                if (FailWrites)
                {
                    throw new IOException("storage down");
                }

                await WriteGate!.Task.WaitAsync(cancellationToken);
            }

            await _inner.PutAsync(key, content, cancellationToken);
        }

        public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default) => _inner.AppendAsync(key, content, cancellationToken);
        public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => _inner.ListAsync(prefix, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
    }
}
