using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using static Agent42.Tests.ScriptedDreamClient;

namespace Agent42.Tests;

/// <summary>The lesson service (ADR 0124): the nightly gate, manual runs, locking, interrupted runs and lesson edits.</summary>
public class FleetLessonServiceTests
{
    // 01:30 UTC is inside the default nightly window (01:00, four hours).
    private static readonly DateTimeOffset Night = new(2026, 11, 10, 1, 30, 0, TimeSpan.Zero);
    private const string User = "alice";

    private readonly InMemoryFleetBlobs _blobs = new();
    private readonly FleetStore _store;
    private readonly SettableTime _time = new(Night);
    private readonly LessonCatalogs _catalogs = new();

    public FleetLessonServiceTests() => _store = new FleetStore(_blobs);

    private static readonly UserTelemetryContext Telemetry = new(User, "alice", null, null, null, null);

    private FleetLessonService Service(ScriptedDreamClient client, FleetOptions? options = null, IFleetStore? store = null, bool userSettings = false)
    {
        options ??= new FleetOptions();
        return new(store ?? _store, options,
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(client, LessonCatalogs.ModelA)),
            _catalogs, NullLogger<FleetLessonService>.Instance, _time,
            settings: userSettings ? new FleetSettingsService(options, store ?? _store, _catalogs) : null);
    }

    /// <summary>A client whose first call submits one lesson built from the first two tasks of the digest.</summary>
    private static ScriptedDreamClient LearnsOne(string text = "Stage the work in two steps.")
        => new(c => c.Number == 1 ? Submit(Proposed(text, c.Tasks.Take(2).Select(t => t.Id))) : Say("done"));

    private async Task SeedTasks(int count, TimeSpan? finishedAgo = null, string prefix = "t")
    {
        for (var i = 0; i < count; i++)
        {
            var finished = Night - (finishedAgo ?? TimeSpan.FromHours(3)) - TimeSpan.FromMinutes(i);
            await _store.SaveTaskAsync(new FleetTaskRecord
            {
                Id = $"{prefix}{i}",
                UserId = User,
                Request = $"request {i}",
                WorkspaceId = $"w-{prefix}{i}",
                PlannerModel = LessonCatalogs.ModelA,
                Status = FleetTaskStatus.Done,
                StartedAt = finished.AddMinutes(-5),
                FinishedAt = finished,
            });
        }
    }

    private async Task Enable(string? model = null, DateTimeOffset? lastRunAt = null)
    {
        var doc = await _store.GetLessonsAsync(User);
        await _store.SetLessonsAsync(User, doc with { Settings = doc.Settings with { Enabled = true, Model = model, LastRunAt = lastRunAt } });
    }

    private static FleetLesson Lesson(string id, string text, string status = FleetLessonStatus.Active, DateTimeOffset? at = null)
        => new() { Id = id, Text = text, Status = status, CreatedAt = at ?? Night.AddDays(-1), LastSupportedAt = at ?? Night.AddDays(-1) };

    private static async Task Until(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out waiting");
            await Task.Delay(10);
        }
    }

    private async Task<FleetLessonsDocument> Settled(FleetLessonService service)
    {
        await service.WhenIdleAsync(User);
        return await service.GetAsync(User);
    }

    // ---- The user's Advanced settings (ADR 0135) ----

    [Fact]
    public async Task Dream_UsesUserWindow()
    {
        await SeedTasks(5);
        await Enable();
        await _store.SetSettingsAsync(User, new FleetUserSettings { DreamAtUtc = "12:00", DreamWindowHours = 2 });
        var service = Service(LearnsOne(), userSettings: true);

        // 01:30 is inside the operator's window (01:00-05:00) but not the user's (12:00-14:00).
        Assert.Equal(FleetDreamTick.NotYet, await service.TickAsync(User));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero), service.WindowStart(new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.Zero), await service.EffectiveOptionsAsync(User)));

        _time.Now = new DateTimeOffset(2026, 11, 10, 12, 30, 0, TimeSpan.Zero);
        Assert.Equal(FleetDreamTick.Started, await service.TickAsync(User));
        await service.WhenIdleAsync(User);
        Assert.Equal(FleetDreamStatus.Ran, Assert.Single((await _store.GetLessonsAsync(User)).Runs).Status);
    }

    [Fact]
    public async Task Dream_UsesUserThresholds()
    {
        await SeedTasks(5);
        await Enable();
        await _store.SetSettingsAsync(User, new FleetUserSettings { DreamMinNewTasks = 10 });

        var tick = await Service(LearnsOne(), userSettings: true).TickAsync(User);

        Assert.Equal(FleetDreamTick.Skipped, tick);
        Assert.Contains("at least 10", Assert.Single((await _store.GetLessonsAsync(User)).Runs).Reason);


        // Without the override the same five tasks are enough.
        await _store.SetSettingsAsync(User, new FleetUserSettings());
        Assert.Equal(FleetDreamTick.Started, await Service(LearnsOne(), userSettings: true).TickAsync(User));
    }

    [Fact]
    public async Task Lessons_UseUserCaps()
    {
        await SeedTasks(4);
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("a", "One."), Lesson("b", "Two.")] });
        await _store.SetSettingsAsync(User, new FleetUserSettings { LessonsInPrompt = 1, LessonsMaxStored = 2 });
        var service = Service(LearnsOne(), userSettings: true);

        // A planner gets the user's number of lessons, not the operator's 8.
        Assert.Single(await service.ActiveAsync(User, effective: await service.EffectiveOptionsAsync(User)));
        Assert.Equal(2, (await service.ActiveAsync(User)).Count);

        // The store is full at the user's cap, so a new lesson is refused.
        var result = await service.RunManualAsync(User, null, Telemetry);
        Assert.Equal(1, result.Run!.Refused);
        Assert.Equal(2, (await _store.GetLessonsAsync(User)).Lessons.Count);
    }

    // ---- The nightly tick ----

    [Fact]
    public async Task Tick_OutsideTheWindow_DoesNothing_AndWritesNothing()
    {
        await SeedTasks(5);
        await Enable();
        var client = LearnsOne();
        _time.Now = Night.AddHours(10);

        var tick = await Service(client).TickAsync(User);

        Assert.Equal(FleetDreamTick.NotYet, tick);
        Assert.Equal(0, client.Calls);
        Assert.Empty((await _store.GetLessonsAsync(User)).Runs);
    }

    [Fact]
    public async Task Tick_OperatorSwitchOff_IsUnavailable()
    {
        await SeedTasks(5);
        await Enable();

        var tick = await Service(LearnsOne(), new FleetOptions { DreamEnabled = false }).TickAsync(User);

        Assert.Equal(FleetDreamTick.Unavailable, tick);
    }

    [Fact]
    public async Task Tick_ToggleOff_IsDisabled_RecordedOncePerWindow()
    {
        await SeedTasks(5);
        var service = Service(LearnsOne());

        Assert.Equal(FleetDreamTick.Disabled, await service.TickAsync(User));
        _time.Now = Night.AddHours(1);
        Assert.Equal(FleetDreamTick.Disabled, await service.TickAsync(User));

        var run = Assert.Single((await _store.GetLessonsAsync(User)).Runs);
        Assert.Equal(FleetDreamStatus.Skipped, run.Status);
        Assert.Contains("switched off", run.Reason);
    }

    [Fact]
    public async Task Tick_TooFewNewTasks_SkipsRecorded_ThenStartsWhenTasksArrive()
    {
        await SeedTasks(3, TimeSpan.FromDays(2));
        await Enable(lastRunAt: Night.AddDays(-1));
        await SeedTasks(1, TimeSpan.FromHours(2), "n");
        var client = LearnsOne();
        var service = Service(client);

        Assert.Equal(FleetDreamTick.Skipped, await service.TickAsync(User));
        _time.Now = Night.AddMinutes(30);
        Assert.Equal(FleetDreamTick.Skipped, await service.TickAsync(User));
        Assert.Equal(0, client.Calls);
        var run = Assert.Single((await _store.GetLessonsAsync(User)).Runs);
        Assert.Contains("1 task(s) finished since the last run", run.Reason);

        await SeedTasks(1, TimeSpan.FromMinutes(10), "m");
        Assert.Equal(FleetDreamTick.Started, await service.TickAsync(User));
        var doc = await Settled(service);
        Assert.Equal(FleetDreamStatus.Ran, doc.Runs[0].Status);
    }

    [Fact]
    public async Task Tick_FewerThanMinTasksInTotal_Skips_WithoutAModelCall()
    {
        await SeedTasks(2);
        await Enable();
        var client = LearnsOne();

        Assert.Equal(FleetDreamTick.Skipped, await Service(client).TickAsync(User));

        Assert.Equal(0, client.Calls);
        Assert.Contains("at least 3", (await _store.GetLessonsAsync(User)).Runs[0].Reason);
    }

    [Fact]
    public async Task Tick_RunsTheDream_StoresTheLesson_AndASecondTickInTheWindowSkips()
    {
        await SeedTasks(4);
        await Enable();
        var client = LearnsOne();
        var service = Service(client);

        Assert.Equal(FleetDreamTick.Started, await service.TickAsync(User));
        var doc = await Settled(service);

        var lesson = Assert.Single(doc.Lessons);
        Assert.Equal(("Stage the work in two steps.", FleetLessonStatus.Active), (lesson.Text, lesson.Status));
        var run = Assert.Single(doc.Runs);
        Assert.Equal((FleetDreamStatus.Ran, FleetDreamTrigger.Nightly, 1, 4, 4), (run.Status, run.Trigger, run.Learned, run.TasksRead, run.NewTasks));
        Assert.Equal(Night, doc.Settings.LastRunAt);

        _time.Now = Night.AddHours(1);
        await SeedTasks(3, TimeSpan.FromMinutes(5), "later");
        Assert.Equal(FleetDreamTick.Skipped, await service.TickAsync(User));
        Assert.Single((await _store.GetLessonsAsync(User)).Runs);
    }

    [Fact]
    public async Task Tick_NextNightRunsAgain()
    {
        await SeedTasks(4);
        await Enable();
        var service = Service(LearnsOne());
        await service.TickAsync(User);
        await Settled(service);

        await SeedTasks(3, TimeSpan.FromMinutes(-60), "fresh"); // finished after the first run started
        _time.Now = Night.AddDays(1);
        Assert.Equal(FleetDreamTick.Started, await service.TickAsync(User));
        Assert.Equal(2, (await Settled(service)).Runs.Count);
    }

    [Fact]
    public async Task Tick_VanishedModel_FailsTheNightlyRun_WithThatReason()
    {
        await SeedTasks(4);
        await Enable(model: "fake:gone");
        var client = LearnsOne();
        var service = Service(client);

        Assert.Equal(FleetDreamTick.Started, await service.TickAsync(User));
        var run = (await Settled(service)).Runs[0];

        Assert.Equal(FleetDreamStatus.Failed, run.Status);
        Assert.Contains("no longer one of your models", run.Reason);
        Assert.Equal(0, client.Calls);
    }

    [Fact]
    public async Task Run_IsAttributedToTheUserAndTheDreamsOwnApplication()
    {
        await SeedTasks(4);
        await Enable();
        var client = LearnsOne();
        var service = Service(client);

        await service.TickAsync(User, Telemetry);
        await Settled(service);

        Assert.Equal(Telemetry, client.SeenUser);
        Assert.Equal(FleetDreamer.ApplicationName, client.SeenApplication);
    }

    [Fact]
    public void WindowStart_HandlesAWindowThatCrossesMidnight_AndABadSetting()
    {
        var service = Service(LearnsOne(), new FleetOptions { DreamAtUtc = "23:00", DreamWindowHours = 4 });

        Assert.Equal(new DateTimeOffset(2026, 11, 9, 23, 0, 0, TimeSpan.Zero), service.WindowStart(new DateTimeOffset(2026, 11, 10, 1, 0, 0, TimeSpan.Zero)));
        Assert.Equal(new DateTimeOffset(2026, 11, 10, 23, 0, 0, TimeSpan.Zero), service.WindowStart(new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero)));
        Assert.Null(service.WindowStart(new DateTimeOffset(2026, 11, 10, 3, 0, 0, TimeSpan.Zero)));
        Assert.Null(service.WindowStart(new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero)));

        var fallback = Service(LearnsOne(), new FleetOptions { DreamAtUtc = "not a time" });
        Assert.NotNull(fallback.WindowStart(Night)); // falls back to 01:00
    }

    // ---- Manual runs ----

    [Fact]
    public async Task Manual_IgnoresTheWindowAndTheNewTaskCheck_AndUsesUpTheNight()
    {
        await SeedTasks(3, TimeSpan.FromDays(3));
        await Enable(lastRunAt: Night.AddDays(-1));
        _time.Now = Night.AddHours(10);
        var client = LearnsOne();
        var service = Service(client);

        var result = await service.RunManualAsync(User, null, Telemetry);

        Assert.Equal(FleetDreamResultKind.Ran, result.Kind);
        Assert.Equal((FleetDreamTrigger.Manual, FleetDreamStatus.Ran, 1), (result.Run!.Trigger, result.Run.Status, result.Run.Learned));
        Assert.Equal(_time.Now, (await service.GetAsync(User)).Settings.LastRunAt);
    }

    [Fact]
    public async Task Manual_FewerThanMinTasks_NoModelCall_AndDoesNotUseUpTheNight()
    {
        await SeedTasks(2);
        var client = LearnsOne();
        var service = Service(client);

        var result = await service.RunManualAsync(User, null, Telemetry);

        Assert.Equal(FleetDreamStatus.Skipped, result.Run!.Status);
        Assert.Equal(0, client.Calls);
        Assert.Null((await service.GetAsync(User)).Settings.LastRunAt);
    }

    [Theory]
    [InlineData(LessonCatalogs.NoToolsModel)]
    [InlineData(LessonCatalogs.CopilotModel)]
    [InlineData("fake:unknown")]
    public async Task Manual_BadModel_IsRefused_WithoutAModelCall(string model)
    {
        await SeedTasks(4);
        var client = LearnsOne();

        var result = await Service(client).RunManualAsync(User, model, Telemetry);

        Assert.Equal(FleetDreamResultKind.BadModel, result.Kind);
        Assert.Contains("supports tools", result.Error);
        Assert.Equal(0, client.Calls);
        Assert.Empty((await _store.GetLessonsAsync(User)).Runs);
    }

    [Fact]
    public async Task Manual_OperatorSwitchOff_IsUnavailable()
    {
        await SeedTasks(4);

        var result = await Service(LearnsOne(), new FleetOptions { DreamEnabled = false }).RunManualAsync(User, null, Telemetry);

        Assert.Equal(FleetDreamResultKind.Unavailable, result.Kind);
    }

    [Fact]
    public async Task ASecondConcurrentDream_IsRefusedAsBusy_AndATickSkips()
    {
        await SeedTasks(4);
        await Enable();
        var gate = new TaskCompletionSource();
        var client = new ScriptedDreamClient(c => c.Number == 1 ? Submit(Proposed("Stage the work in two steps.", c.Tasks.Take(2).Select(t => t.Id))) : Say("done")) { Gate = gate };
        var service = Service(client);

        var first = service.RunManualAsync(User, null, Telemetry);
        await Until(() => client.Calls == 1);
        var second = await service.RunManualAsync(User, null, Telemetry);
        var tick = await service.TickAsync(User);

        Assert.Equal(FleetDreamResultKind.Busy, second.Kind);
        Assert.Equal(FleetDreamTick.Skipped, tick);
        Assert.True(service.IsRunning(User));
        gate.SetResult();
        Assert.Equal(FleetDreamResultKind.Ran, (await first).Kind);
        Assert.False(service.IsRunning(User));
        Assert.Single((await _store.GetLessonsAsync(User)).Runs);
    }

    [Fact]
    public async Task ADisableOrDeleteMadeWhileTheDreamRuns_Survives_AndADisabledLessonIsNotLearnedAgain()
    {
        await SeedTasks(4);
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("x", "Stage the work."), Lesson("y", "Another lesson here.")] });
        var gate = new TaskCompletionSource();
        var client = new ScriptedDreamClient(c => c.Number == 1
            ? Submit(Proposed("Stage the work.", c.Tasks.Take(2).Select(t => t.Id)), Proposed("Review twice.", c.Tasks.Take(2).Select(t => t.Id)))
            : Say("done"))
        { Gate = gate };
        var service = Service(client);

        var run = service.RunManualAsync(User, null, Telemetry);
        await Until(() => client.Calls == 1);
        Assert.True(await service.DisableAsync(User, "x"));
        Assert.True(await service.DeleteAsync(User, "y"));
        gate.SetResult();
        var result = await run;

        var doc = await service.GetAsync(User);
        Assert.Equal(FleetLessonStatus.Disabled, doc.Lessons.Single(l => l.Id == "x").Status);
        Assert.DoesNotContain(doc.Lessons, l => l.Id == "y");
        Assert.Contains(doc.Lessons, l => l.Text == "Review twice." && l.Status == FleetLessonStatus.Active);
        Assert.Equal((1, 0, 1), (result.Run!.Learned, result.Run.Reinforced, result.Run.Refused));
    }

    [Fact]
    public async Task ADreamThatRederivesAnActiveLesson_ReinforcesIt()
    {
        await SeedTasks(4);
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("x", "Stage the work in two steps.")] });

        var result = await Service(LearnsOne()).RunManualAsync(User, null, Telemetry);

        Assert.Equal((0, 1), (result.Run!.Learned, result.Run.Reinforced));
        var lesson = Assert.Single((await _store.GetLessonsAsync(User)).Lessons);
        Assert.Equal(Night, lesson.LastSupportedAt);
    }

    [Fact]
    public async Task AFailingModel_RecordsAFailedRun_AndTheDocumentSurvives()
    {
        await SeedTasks(4);
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("x", "Keep me.")] });
        var client = new ScriptedDreamClient(_ => throw new HttpRequestException("provider down"));

        var result = await Service(client).RunManualAsync(User, null, Telemetry);

        Assert.Equal(FleetDreamStatus.Failed, result.Run!.Status);
        Assert.Contains("provider down", result.Run.Reason);
        Assert.Equal("Keep me.", Assert.Single((await _store.GetLessonsAsync(User)).Lessons).Text);
    }

    [Fact]
    public async Task TheDreamTimeout_FailsTheRun_WithoutThrowing()
    {
        await SeedTasks(4);
        var client = new ScriptedDreamClient(_ => Say("never")) { Gate = new TaskCompletionSource() };

        var result = await Service(client, new FleetOptions { DreamTimeout = TimeSpan.FromMilliseconds(150) }).RunManualAsync(User, null, Telemetry);

        Assert.Equal(FleetDreamStatus.Failed, result.Run!.Status);
        Assert.Contains("timed out", result.Run.Reason);
    }

    [Fact]
    public async Task ExpiredLessons_AreDroppedByTheNextRun()
    {
        await SeedTasks(4);
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("old", "Ancient lesson.", at: Night.AddDays(-200))] });

        await Service(new ScriptedDreamClient(_ => Say("nothing"))).RunManualAsync(User, null, Telemetry);

        Assert.Empty((await _store.GetLessonsAsync(User)).Lessons);
    }

    // ---- Interrupted runs ----

    [Fact]
    public async Task ARunningEntryNotInTheRegistry_ReadsAsInterrupted_AndIsPersisted()
    {
        var running = new FleetDreamRun { Id = "r1", StartedAt = Night.AddHours(-1), Status = FleetDreamStatus.Running };
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Runs = [running] });
        var service = Service(LearnsOne());

        var doc = await service.GetAsync(User);

        Assert.Equal(FleetDreamStatus.Interrupted, doc.Runs[0].Status);
        Assert.Equal(Night, doc.Runs[0].FinishedAt);
        Assert.Equal(FleetDreamStatus.Interrupted, (await _store.GetLessonsAsync(User)).Runs[0].Status);
    }

    [Fact]
    public async Task ALiveRun_IsNotTouched_AndItsFinalWriteStands()
    {
        await SeedTasks(4);
        var gate = new TaskCompletionSource();
        var client = new ScriptedDreamClient(c => c.Number == 1 ? Submit(Proposed("Stage the work in two steps.", c.Tasks.Take(2).Select(t => t.Id))) : Say("done")) { Gate = gate };
        var service = Service(client);

        var run = service.RunManualAsync(User, null, Telemetry);
        await Until(() => client.Calls == 1);
        Assert.Equal(FleetDreamStatus.Running, (await service.GetAsync(User)).Runs[0].Status);
        gate.SetResult();
        await run;

        var doc = await service.GetAsync(User);
        Assert.Equal(FleetDreamStatus.Ran, doc.Runs[0].Status);
        Assert.Single(doc.Lessons);
    }

    [Fact]
    public async Task AStaleRead_DoesNotOverwriteAFinishedRunWithInterrupted()
    {
        var running = new FleetDreamRun { Id = "r1", StartedAt = Night.AddHours(-1), Status = FleetDreamStatus.Running };
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Runs = [running] });
        var stale = new StaleFirstLessonRead(_store);
        var service = Service(LearnsOne(), store: stale);
        // The run finished between the first (stale) read and the locked re-read.
        stale.BeforeSecondRead = () => _store.SetLessonsAsync(User, new FleetLessonsDocument { Runs = [running with { Status = FleetDreamStatus.Ran }] });

        var doc = await service.GetAsync(User);

        Assert.Equal(FleetDreamStatus.Ran, doc.Runs[0].Status);
        Assert.Equal(FleetDreamStatus.Ran, (await _store.GetLessonsAsync(User)).Runs[0].Status);
    }

    // ---- Settings, edits and applications ----

    [Fact]
    public async Task Settings_DefaultOff_AreSaved_AndABadModelIsRefused()
    {
        var service = Service(LearnsOne());
        Assert.False((await service.GetAsync(User)).Settings.Enabled);

        var (saved, error) = await service.SetSettingsAsync(User, true, LessonCatalogs.ModelB);
        Assert.Null(error);
        Assert.Equal((true, LessonCatalogs.ModelB), (saved!.Enabled, saved.Model));

        var (none, refusal) = await service.SetSettingsAsync(User, true, LessonCatalogs.CopilotModel);
        Assert.Null(none);
        Assert.Contains("supports tools", refusal);
        Assert.Equal(LessonCatalogs.ModelB, (await service.GetAsync(User)).Settings.Model);

        var (cleared, _) = await service.SetSettingsAsync(User, false, "  ");
        Assert.Equal((false, (string?)null), (cleared!.Enabled, cleared.Model));
    }

    [Fact]
    public async Task DisableEnableDelete_AreScopedToTheCaller()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("a", "Mine.")] });
        await _store.SetLessonsAsync("bob", new FleetLessonsDocument { Lessons = [Lesson("b", "His.")] });
        var service = Service(LearnsOne());

        Assert.False(await service.DisableAsync(User, "b"));
        Assert.False(await service.DeleteAsync(User, "b"));
        Assert.False(await service.EnableAsync(User, "b"));
        Assert.Equal(FleetLessonStatus.Active, Assert.Single((await _store.GetLessonsAsync("bob")).Lessons).Status);

        Assert.True(await service.DisableAsync(User, "a"));
        Assert.Equal(FleetLessonStatus.Disabled, (await service.GetAsync(User)).Lessons[0].Status);
        Assert.True(await service.EnableAsync(User, "a"));
        Assert.Equal((FleetLessonStatus.Active, Night), ((await service.GetAsync(User)).Lessons[0].Status, (await service.GetAsync(User)).Lessons[0].LastSupportedAt));
        Assert.True(await service.DeleteAsync(User, "a"));
        Assert.Empty((await service.GetAsync(User)).Lessons);
    }

    [Fact]
    public async Task NewCount_CountsLessonsSinceTheDialogWasLastOpened()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument
        {
            Lessons = [Lesson("a", "Old one.", at: Night.AddDays(-2)), Lesson("b", "New one.", at: Night.AddHours(-1)), Lesson("c", "Off.", FleetLessonStatus.Disabled, Night.AddHours(-1))],
            Settings = new FleetLessonSettings { SeenAt = Night.AddDays(-1) },
        });
        var service = Service(LearnsOne());

        Assert.Equal(1, FleetLessonService.NewCount(await service.GetAsync(User)));
        await service.MarkSeenAsync(User);
        Assert.Equal(0, FleetLessonService.NewCount(await service.GetAsync(User)));
    }

    [Fact]
    public async Task RecordApplication_CountsOncePerTask_SkipsDeletedLessons_AndKeepsTheLast20()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("a", "One."), Lesson("b", "Two.")] });
        var service = Service(LearnsOne());

        await service.RecordApplicationAsync(User, "task-1", ["a", "gone"]);
        await service.RecordApplicationAsync(User, "task-1", ["a"]);
        for (var i = 2; i <= 25; i++)
        {
            await service.RecordApplicationAsync(User, $"task-{i}", ["b"]);
        }

        var doc = await service.GetAsync(User);
        var a = doc.Lessons.Single(l => l.Id == "a");
        var b = doc.Lessons.Single(l => l.Id == "b");
        Assert.Equal((1, "task-1"), (a.AppliedCount, Assert.Single(a.Applications).TaskId));
        Assert.Equal(24, b.AppliedCount);
        Assert.Equal(20, b.Applications.Count);
        Assert.Equal("task-25", b.Applications[^1].TaskId);
        Assert.Equal(2, doc.Lessons.Count);
    }

    [Fact]
    public async Task Active_ReturnsTheLessonsAPlannerGets_AndFailsOpenWhenTheReadFails()
    {
        await _store.SetLessonsAsync(User, new FleetLessonsDocument { Lessons = [Lesson("a", "Fine."), Lesson("b", "Off.", FleetLessonStatus.Disabled)] });
        Assert.Equal(["a"], (await Service(LearnsOne()).ActiveAsync(User)).Select(l => l.Id));

        var throwing = new FleetStore(new ThrowingLessonBlobs(_blobs));
        Assert.Empty(await Service(LearnsOne(), store: throwing).ActiveAsync(User));
    }

    // ---- Doubles ----

    private sealed class SettableTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>Reads the document once as it was, then runs a hook, so a later read sees what happened in between.</summary>
    private sealed class StaleFirstLessonRead(FleetStore inner) : IFleetStore
    {
        private int _reads;

        public Func<Task>? BeforeSecondRead { get; set; }

        public async Task<FleetLessonsDocument> GetLessonsAsync(string userId, CancellationToken cancellationToken = default)
        {
            if (++_reads == 2 && BeforeSecondRead is { } hook)
            {
                await hook();
            }

            return await inner.GetLessonsAsync(userId, cancellationToken);
        }

        public Task SetLessonsAsync(string userId, FleetLessonsDocument document, CancellationToken cancellationToken = default) => inner.SetLessonsAsync(userId, document, cancellationToken);
        public Task<FleetUserSettings> GetSettingsAsync(string userId, CancellationToken cancellationToken = default) => inner.GetSettingsAsync(userId, cancellationToken);
        public Task SetSettingsAsync(string userId, FleetUserSettings settings, CancellationToken cancellationToken = default) => inner.SetSettingsAsync(userId, settings, cancellationToken);
        public Task<IReadOnlyList<FleetTaskRecord>> ListTasksAsync(string userId, CancellationToken cancellationToken = default) => inner.ListTasksAsync(userId, cancellationToken);
        public Task<FleetTaskRecord?> GetTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default) => inner.GetTaskAsync(userId, taskId, cancellationToken);
        public Task SaveTaskAsync(FleetTaskRecord task, CancellationToken cancellationToken = default) => inner.SaveTaskAsync(task, cancellationToken);
        public Task DeleteTaskAsync(string userId, string taskId, CancellationToken cancellationToken = default) => inner.DeleteTaskAsync(userId, taskId, cancellationToken);
        public Task AppendEventsAsync(string userId, string taskId, string lines, CancellationToken cancellationToken = default) => inner.AppendEventsAsync(userId, taskId, lines, cancellationToken);
        public Task<IReadOnlyList<string>> ReadEventsAsync(string userId, string taskId, CancellationToken cancellationToken = default) => inner.ReadEventsAsync(userId, taskId, cancellationToken);
        public Task PutWorkspaceFileAsync(string userId, string workspaceId, string path, byte[] content, CancellationToken cancellationToken = default) => inner.PutWorkspaceFileAsync(userId, workspaceId, path, content, cancellationToken);
        public Task<byte[]?> GetWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default) => inner.GetWorkspaceFileAsync(userId, workspaceId, path, cancellationToken);
        public Task<IReadOnlyList<FleetWorkspaceFile>> ListWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.ListWorkspaceAsync(userId, workspaceId, cancellationToken);
        public Task DeleteWorkspaceFileAsync(string userId, string workspaceId, string path, CancellationToken cancellationToken = default) => inner.DeleteWorkspaceFileAsync(userId, workspaceId, path, cancellationToken);
        public Task DeleteWorkspaceAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.DeleteWorkspaceAsync(userId, workspaceId, cancellationToken);
        public Task<FleetCanvasManifest> GetCanvasManifestAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.GetCanvasManifestAsync(userId, workspaceId, cancellationToken);
        public Task PutCanvasManifestAsync(string userId, string workspaceId, FleetCanvasManifest manifest, CancellationToken cancellationToken = default) => inner.PutCanvasManifestAsync(userId, workspaceId, manifest, cancellationToken);
        public Task PutCanvasVersionAsync(string userId, string workspaceId, string name, int version, byte[] content, CancellationToken cancellationToken = default) => inner.PutCanvasVersionAsync(userId, workspaceId, name, version, content, cancellationToken);
        public Task<byte[]?> GetCanvasVersionAsync(string userId, string workspaceId, string name, int version, CancellationToken cancellationToken = default) => inner.GetCanvasVersionAsync(userId, workspaceId, name, version, cancellationToken);
        public Task DeleteCanvasVersionAsync(string userId, string workspaceId, string name, int version, CancellationToken cancellationToken = default) => inner.DeleteCanvasVersionAsync(userId, workspaceId, name, version, cancellationToken);
        public Task DeleteCanvasAsync(string userId, string workspaceId, CancellationToken cancellationToken = default) => inner.DeleteCanvasAsync(userId, workspaceId, cancellationToken);
        public Task<IReadOnlyDictionary<string, string>> GetPromptsAsync(string userId, CancellationToken cancellationToken = default) => inner.GetPromptsAsync(userId, cancellationToken);
        public Task SetPromptsAsync(string userId, IReadOnlyDictionary<string, string> prompts, CancellationToken cancellationToken = default) => inner.SetPromptsAsync(userId, prompts, cancellationToken);
    }

    /// <summary>A blob store whose lessons blob can't be read, like a storage outage.</summary>
    private sealed class ThrowingLessonBlobs(IFleetBlobs inner) : IFleetBlobs
    {
        public Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
            => key.EndsWith("lessons.json", StringComparison.Ordinal) ? throw new IOException("storage down") : inner.GetAsync(key, cancellationToken);

        public Task PutAsync(string key, byte[] content, CancellationToken cancellationToken = default) => inner.PutAsync(key, content, cancellationToken);
        public Task AppendAsync(string key, byte[] content, CancellationToken cancellationToken = default) => inner.AppendAsync(key, content, cancellationToken);
        public Task<IReadOnlyList<FleetBlobItem>> ListAsync(string prefix, CancellationToken cancellationToken = default) => inner.ListAsync(prefix, cancellationToken);
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => inner.DeleteAsync(key, cancellationToken);
    }
}
