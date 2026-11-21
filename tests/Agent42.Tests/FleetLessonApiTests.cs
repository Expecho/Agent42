using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Runtime;
using static Agent42.Tests.ScriptedDreamClient;

namespace Agent42.Tests;

/// <summary>The API host with the fleet in memory and a scripted dream model: the first call submits one lesson built from the digest.</summary>
public sealed class FleetLessonApiFixture : SharedApiFixture
{
    public ScriptedDreamClient DreamClient { get; } = new(c => c.LastToolResult is null ? Submit(Proposed("Stage the work in two steps.", c.Tasks.Take(2).Select(t => t.Id))) : Say("done"));

    protected override void Configure(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog(LessonCatalogs.ModelA,
        [
            new ModelInfo(LessonCatalogs.ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
            new ModelInfo(LessonCatalogs.NoToolsModel, "fake", null, "t", null, SupportsTools: false, SupportsVision: false),
        ]));
        builder.Services.AddSingleton<IUserChatClientFactoryProvider>(
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(DreamClient, LessonCatalogs.ModelA)));
        builder.Services.AddAgentFleetInMemory(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Fleet:ApprovalDepth"] = "-1",
        }).Build());
    }
}

/// <summary>The lessons routes (ADR 0124): scoping to the caller, the settings and their schedule, the manual dream and its refusals.</summary>
public sealed class FleetLessonApiTests(FleetLessonApiFixture fixture) : SharedClusterTests<FleetLessonApiFixture>(fixture)
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private IFleetStore Store => Fixture.App.Services.GetRequiredService<IFleetStore>();

    private Task<HttpResponseMessage> Send(HttpMethod method, string uri, string user, object? body = null)
    {
        var request = new HttpRequestMessage(method, uri);
        request.Headers.Add(IdHeader, user);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        return Fixture.Client.SendAsync(request);
    }

    private static string NewUser() => $"lessons-{Guid.NewGuid():N}";

    private async Task<JsonElement> Get(string user)
        => await (await Send(HttpMethod.Get, "/api/fleet/lessons", user)).Content.ReadFromJsonAsync<JsonElement>();

    private async Task<int> ReminderCount(string user)
    {
        var table = Cluster.GetSiloServiceProvider(Cluster.Silos[0].SiloAddress).GetRequiredService<IReminderTable>();
        return (await table.ReadRows(Cluster.GrainFactory.GetGrain<IFleetDreamGrain>(user).GetGrainId())).Reminders.Count;
    }

    private async Task SeedTasks(string user, int count)
    {
        for (var i = 0; i < count; i++)
        {
            await Store.SaveTaskAsync(new FleetTaskRecord
            {
                Id = $"{user}-t{i}",
                UserId = user,
                Request = "r",
                WorkspaceId = $"{user}-w{i}",
                PlannerModel = LessonCatalogs.ModelA,
                Status = FleetTaskStatus.Done,
                StartedAt = DateTimeOffset.UtcNow.AddHours(-3 - i),
                FinishedAt = DateTimeOffset.UtcNow.AddHours(-2 - i),
            });
        }
    }

    private async Task SeedLesson(string user, string id)
        => await Store.SetLessonsAsync(user, new FleetLessonsDocument
        {
            Lessons = [new FleetLesson { Id = id, Text = "A lesson.", CreatedAt = DateTimeOffset.UtcNow, LastSupportedAt = DateTimeOffset.UtcNow }],
        });

    // ADR 0137: the dream runs on a real model; `auto` is a chat-picker value, on both routes that take one.
    [Fact]
    public async Task AutoOutsideChat_LessonSettingsAndDream_AreRefused()
    {
        var user = NewUser();

        var settings = await Send(HttpMethod.Put, "/api/fleet/lessons/settings", user, new { enabled = true, model = "auto" });
        var dream = await Send(HttpMethod.Post, "/api/fleet/lessons/dream", user, new { model = "auto" });

        Assert.Equal(HttpStatusCode.BadRequest, settings.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, dream.StatusCode);
        Assert.False((await Get(user)).GetProperty("settings").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Get_Default_IsOff_Empty_AndCarriesTheSchedule()
    {
        var body = await Get(NewUser());

        Assert.False(body.GetProperty("settings").GetProperty("enabled").GetBoolean());
        Assert.Equal(0, body.GetProperty("lessons").GetArrayLength());
        Assert.Equal(0, body.GetProperty("newCount").GetInt32());
        Assert.Equal("01:00", body.GetProperty("schedule").GetProperty("atUtc").GetString());
        Assert.Equal(4, body.GetProperty("schedule").GetProperty("windowHours").GetInt32());
        Assert.True(body.GetProperty("dreamEnabled").GetBoolean());
    }

    [Fact]
    public async Task LessonsGet_ShowsEffective()
    {
        var user = NewUser();
        await Store.SetSettingsAsync(user, new FleetUserSettings { DreamAtUtc = "03:30", DreamWindowHours = 2, DreamMinNewTasks = 5, DreamMinTasks = 6, LessonTtlDays = 30 });

        var body = await Get(user);

        Assert.Equal("03:30", body.GetProperty("schedule").GetProperty("atUtc").GetString());
        Assert.Equal(2, body.GetProperty("schedule").GetProperty("windowHours").GetInt32());
        Assert.Equal(5, body.GetProperty("minNewTasks").GetInt32());
        Assert.Equal(6, body.GetProperty("minTasks").GetInt32());
        Assert.Equal(30, body.GetProperty("ttlDays").GetInt32());
        Assert.Equal("01:00", (await Get(NewUser())).GetProperty("schedule").GetProperty("atUtc").GetString());
    }

    [Fact]
    public async Task EnablingSchedulesTheGrain_DisablingUnschedulesIt()
    {
        var user = NewUser();

        var on = await Send(HttpMethod.Put, "/api/fleet/lessons/settings", user, new { enabled = true, model = LessonCatalogs.ModelA });
        Assert.Equal(HttpStatusCode.OK, on.StatusCode);
        Assert.Equal(1, await ReminderCount(user));
        Assert.True((await Get(user)).GetProperty("settings").GetProperty("enabled").GetBoolean());

        await Send(HttpMethod.Put, "/api/fleet/lessons/settings", user, new { enabled = false });
        Assert.Equal(0, await ReminderCount(user));
    }

    [Fact]
    public async Task GetWithTheToggleOn_ReRegistersAMissingReminder()
    {
        var user = NewUser();
        await Send(HttpMethod.Put, "/api/fleet/lessons/settings", user, new { enabled = true });
        await Cluster.GrainFactory.GetGrain<IFleetDreamGrain>(user).UnscheduleAsync();
        Assert.Equal(0, await ReminderCount(user));

        await Get(user);

        Assert.Equal(1, await ReminderCount(user));
    }

    [Fact]
    public async Task Settings_BadModel_Is400_AndNothingIsScheduled()
    {
        var user = NewUser();

        var response = await Send(HttpMethod.Put, "/api/fleet/lessons/settings", user, new { enabled = true, model = LessonCatalogs.NoToolsModel });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0, await ReminderCount(user));
        Assert.False((await Get(user)).GetProperty("settings").GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Dream_RunsAndReturnsTheRun_AndTheLessonIsListed()
    {
        var user = NewUser();
        await SeedTasks(user, 4);
        Fixture.DreamClient.Gate = null;

        var response = await Send(HttpMethod.Post, "/api/fleet/lessons/dream", user);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var run = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ran", run.GetProperty("status").GetString());
        Assert.Equal("manual", run.GetProperty("trigger").GetString());
        Assert.Equal(1, run.GetProperty("learned").GetInt32());
        var body = await Get(user);
        Assert.Equal("Stage the work in two steps.", body.GetProperty("lessons")[0].GetProperty("text").GetString());
        Assert.Equal(1, body.GetProperty("newCount").GetInt32());

        await Send(HttpMethod.Post, "/api/fleet/lessons/seen", user);
        Assert.Equal(0, (await Get(user)).GetProperty("newCount").GetInt32());
    }

    [Fact]
    public async Task Dream_BadModel_Is400()
    {
        var user = NewUser();
        await SeedTasks(user, 4);

        var response = await Send(HttpMethod.Post, "/api/fleet/lessons/dream", user, new { model = LessonCatalogs.NoToolsModel });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Dream_WhileOneRuns_Is409()
    {
        var user = NewUser();
        await SeedTasks(user, 4);
        var calls = Fixture.DreamClient.Calls;
        var gate = new TaskCompletionSource();
        Fixture.DreamClient.Gate = gate;
        try
        {
            var first = Send(HttpMethod.Post, "/api/fleet/lessons/dream", user);
            for (var i = 0; i < 250 && Fixture.DreamClient.Calls == calls; i++)
            {
                await Task.Delay(20);
            }

            var second = await Send(HttpMethod.Post, "/api/fleet/lessons/dream", user);
            Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
            Assert.True((await Get(user)).GetProperty("running").GetBoolean());
            gate.SetResult();
            Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        }
        finally
        {
            Fixture.DreamClient.Gate = null;
            gate.TrySetResult();
        }
    }

    [Fact]
    public async Task DisableEnableDelete_WorkForTheOwner()
    {
        var user = NewUser();
        await SeedLesson(user, "l1");

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/fleet/lessons/l1/disable", user)).StatusCode);
        Assert.Equal("disabled", (await Get(user)).GetProperty("lessons")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Post, "/api/fleet/lessons/l1/enable", user)).StatusCode);
        Assert.Equal("active", (await Get(user)).GetProperty("lessons")[0].GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Delete, "/api/fleet/lessons/l1", user)).StatusCode);
        Assert.Equal(0, (await Get(user)).GetProperty("lessons").GetArrayLength());
    }

    [Fact]
    public async Task AnotherUsersLesson_Is404_NeverListed_AndUntouched()
    {
        var alice = NewUser();
        var bob = NewUser();
        await SeedLesson(alice, "mine");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/fleet/lessons/mine/disable", bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Post, "/api/fleet/lessons/mine/enable", bob)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Delete, "/api/fleet/lessons/mine", bob)).StatusCode);
        Assert.Equal(0, (await Get(bob)).GetProperty("lessons").GetArrayLength());
        Assert.Equal("active", (await Get(alice)).GetProperty("lessons")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task TheDreamReadsOnlyTheCallersTasks()
    {
        var alice = NewUser();
        var bob = NewUser();
        await SeedTasks(bob, 4);

        var run = await (await Send(HttpMethod.Post, "/api/fleet/lessons/dream", alice)).Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("skipped", run.GetProperty("status").GetString());
        Assert.Equal(0, run.GetProperty("tasksRead").GetInt32());
    }
}
