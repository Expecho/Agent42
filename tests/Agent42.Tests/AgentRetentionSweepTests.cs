using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.Storage;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The idle retention sweep (ADR 0140): a reminder registered at bind time that prunes an idle chat, deletes it whole
/// when everything in it has expired, never runs over a turn in flight, and is invisible to the model's reminder tools.
/// Ticks are driven directly (<c>ReceiveReminder("__retention", …)</c>) against the shared test clock.
/// </summary>
public sealed class AgentRetentionSweepTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private readonly InMemoryChatSearchIndex _search = new();

    private static AgentBinding Binding(int? minutes)
        => new("agent-ret", "Keeper", "You keep.", TestSiloConfigurator.DefaultModel, RetentionMinutes: minutes);

    private IAgentGrain Agent(string key) => Cluster.GrainFactory.GetGrain<IAgentGrain>(key);

    private static Task TickAsync(IAgentGrain agent)
        => agent.AsReference<IRemindable>().ReceiveReminder(RetentionLimits.SweepReminderName, default);

    private static async Task ChatAsync(IAgentGrain agent, string message)
    {
        await foreach (var _ in agent.ChatAsync(message, null))
        {
        }
    }

    // AC7: bind registers the sweep reminder iff a value is set; a fork of a chat with a value registers one too.
    [Fact]
    public async Task Bind_RegistersSweepReminder()
    {
        var with = Agent("sweep-bind-user/with");
        var without = Agent("sweep-bind-user/without");

        await with.BindAgentAsync(Binding(60));
        await without.BindAgentAsync(Binding(null));

        Assert.Equal([RetentionLimits.SweepReminderName], await ReminderNamesAsync(with));
        Assert.Empty(await ReminderNamesAsync(without));
    }

    [Fact]
    public async Task IncognitoBind_RegistersNone()
    {
        var agent = Agent("sweep-incognito-user/conv");

        await agent.BindAgentAsync(Binding(60), ephemeral: true);

        Assert.Empty(await ReminderNamesAsync(agent));
    }

    [Fact]
    public async Task Fork_RegistersSweepReminder()
    {
        var source = Agent("sweep-fork-user/src");
        await source.BindAgentAsync(Binding(60));
        await ChatAsync(source, "say hello");

        var fork = await ForkAsync(source, "sweep-fork-user/fork");

        Assert.Equal([RetentionLimits.SweepReminderName], await ReminderNamesAsync(fork));
    }

    // AC8: everything expired → the whole chat goes, with the Delete button's effect.
    [Fact]
    public async Task Sweep_AllExpired_DeletesWholeChat()
    {
        const string user = "sweep-delete-user";
        var agent = Agent($"{user}/conv");
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "the zebra question");
        await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user).TouchAsync("conv", "the zebra question");
        await Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>(user).CreateOrGetAsync("conv");
        Assert.NotEmpty(await _search.SearchAsync(user, "zebra", 10));

        Fixture.AdvanceClock(TimeSpan.FromMinutes(90));
        await TickAsync(agent);

        Assert.Null((await StoredAsync($"{user}/conv")).SerializedSession);
        Assert.Empty(await agent.GetHistoryAsync());
        Assert.DoesNotContain(await Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(user).GetConversationsAsync(),
            c => c.Id == "conv");
        Assert.Null(await Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>(user).GetAsync("conv"));
        Assert.Empty(await ReminderNamesAsync(agent));
        Assert.Empty(await _search.SearchAsync(user, "zebra", 10));
    }

    // AC9: only the leading turns are expired → they are pruned and persisted, the chat stays, and the index is reconciled.
    [Fact]
    public async Task Sweep_PartlyExpired_PrunesAndKeepsChat()
    {
        const string user = "sweep-prune-user";
        var agent = Agent($"{user}/conv");
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "the ancient zebra");
        Fixture.AdvanceClock(TimeSpan.FromMinutes(40));
        await ChatAsync(agent, "the recent giraffe");
        Assert.NotEmpty(await _search.SearchAsync(user, "zebra", 10));

        Fixture.AdvanceClock(TimeSpan.FromMinutes(30));
        await TickAsync(agent);

        var history = await agent.GetHistoryAsync();
        Assert.DoesNotContain(history, t => t.Text.Contains("zebra", StringComparison.Ordinal));
        Assert.Contains(history, t => t.Text.Contains("giraffe", StringComparison.Ordinal));
        Assert.DoesNotContain("zebra", (await StoredAsync($"{user}/conv")).SerializedSession, StringComparison.Ordinal);
        Assert.Empty(await _search.SearchAsync(user, "zebra", 10));
        Assert.NotEmpty(await _search.SearchAsync(user, "giraffe", 10));
        Assert.Equal([RetentionLimits.SweepReminderName], await ReminderNamesAsync(agent));
    }

    // AC10: a turn in flight is never swept. The enumerator is paused BETWEEN pulls (the grain is free then), which is
    // exactly where a tick can land.
    [Fact]
    public async Task Sweep_SkipsInFlightRun()
    {
        const string user = "sweep-inflight-user";
        var agent = Agent($"{user}/conv");
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "the first exchange");
        Fixture.AdvanceClock(TimeSpan.FromMinutes(90));

        await using var turn = agent.ChatAsync(FakeChatClient.SlowStreamMarker, null).GetAsyncEnumerator();
        Assert.True(await turn.MoveNextAsync());
        await TickAsync(agent);

        Assert.Contains("the first exchange", (await StoredAsync($"{user}/conv")).SerializedSession, StringComparison.Ordinal);
        while (await turn.MoveNextAsync())
        {
        }
    }

    // AC10: a message with no stamp (should not occur, but a provider path could leave one) has unknown age, and unknown
    // age keeps the turn: the sweep stamps it "now" instead of deleting the chat.
    [Fact]
    public async Task Sweep_UnstampedIsKept()
    {
        const string key = "sweep-unstamped-user/conv";
        var agent = Agent(key);
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "the kept exchange");
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await MutateStoredAsync(key, state =>
        {
            var node = JsonNode.Parse(state.SerializedSession!)!;
            StripCreatedAt(node);
            state.SerializedSession = node.ToJsonString();
        });
        Assert.DoesNotContain("createdAt", (await StoredAsync(key)).SerializedSession, StringComparison.Ordinal);

        Fixture.AdvanceClock(TimeSpan.FromMinutes(90));
        await TickAsync(Agent(key));

        Assert.Contains(await Agent(key).GetHistoryAsync(), t => t.Text == "the kept exchange");
        Assert.Contains("createdAt", (await StoredAsync(key)).SerializedSession, StringComparison.Ordinal);
    }

    // AC10: a tick on a chat with no retention value (the agent's setting never reached it) unregisters itself.
    [Fact]
    public async Task Sweep_StaleTickUnregisters()
    {
        const string key = "sweep-stale-user/conv";
        var agent = Agent(key);
        await agent.BindAgentAsync(Binding(60));
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        await MutateStoredAsync(key, state => state.RetentionMinutes = null);

        await TickAsync(Agent(key));

        Assert.Empty(await ReminderNamesAsync(Agent(key)));
    }

    // AC16: a bound chat whose first turn never persisted is empty — nothing to preserve, so the tick deletes it.
    [Fact]
    public async Task Sweep_EmptyBoundChat_Deletes()
    {
        const string key = "sweep-empty-user/conv";
        var agent = Agent(key);
        await agent.BindAgentAsync(Binding(60));
        Assert.Equal("agent-ret", (await agent.GetAgentBindingAsync())!.AgentId);

        await TickAsync(agent);

        Assert.Null(await Agent(key).GetAgentBindingAsync());
        Assert.Empty(await ReminderNamesAsync(Agent(key)));
    }

    // AC11: the sweep is not a scheduled prompt — it runs no model and sends no push.
    [Fact]
    public async Task Sweep_RunsNoModel()
    {
        const string user = "sweep-nomodel-user";
        var agent = Agent($"{user}/conv");
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "say hello");
        var before = (await agent.GetHistoryAsync()).Count;

        await TickAsync(agent);

        Assert.Equal(before, (await agent.GetHistoryAsync()).Count);
        Assert.DoesNotContain(Cluster.Silos.SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<RecordingPushNotificationSender>().Sends), s => s.UserId == user);
    }

    // AC11: the model's cancel-all tool must not take the sweep with it.
    [Fact]
    public async Task CancelAll_KeepsSweep()
    {
        var agent = Agent("sweep-cancelall-user/conv");
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "please remind me to drink water");
        Assert.Single(await agent.GetScheduledPromptsAsync());

        await ChatAsync(agent, "please cancel all reminders");

        Assert.Empty(await agent.GetScheduledPromptsAsync());
        Assert.Equal([RetentionLimits.SweepReminderName], await ReminderNamesAsync(agent));
    }

    // AC11: a model-chosen reminder name may not shadow the sweep.
    [Fact]
    public async Task SchedulePrompt_RefusesReservedName()
    {
        var agent = Agent("sweep-reserved-user/conv");

        await ChatAsync(agent, FakeChatClient.CallToolPrompt("schedule_prompt",
            new { name = "__retention", prompt = "hijack", dueInMinutes = 1, repeatEveryMinutes = 1 }));

        Assert.Empty(await agent.GetScheduledPromptsAsync());
        Assert.Empty(await ReminderNamesAsync(agent));
    }

    // AC14: deleting a chat-app conversation also resets its route, so the next inbound message mints a fresh bound chat.
    [Fact]
    public async Task Sweep_ResetsChatRoute()
    {
        const string user = "sweep-route-user";
        var route = Cluster.GrainFactory.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(user, "conn-1", "4242"));
        var minted = await route.BeginTurnAsync(forceNew: false);
        var agent = Agent($"{user}/{minted.ConversationId}");
        await agent.SetChatOriginAsync(new ChatOrigin { ConnectionId = "conn-1", RouteKey = "4242" });
        await agent.BindAgentAsync(Binding(60));
        await ChatAsync(agent, "say hello");

        Fixture.AdvanceClock(TimeSpan.FromMinutes(90));
        await TickAsync(agent);

        var next = await route.BeginTurnAsync(forceNew: false);
        Assert.True(next.Minted);
        Assert.NotEqual(minted.ConversationId, next.ConversationId);
    }

    // AC15: a fork's copied messages carry the SOURCE's stamps; it must start its own window, not be swept at its first tick.
    [Fact]
    public async Task Fork_WithRetention_RestampsMessages()
    {
        var source = Agent("sweep-restamp-user/src");
        await source.BindAgentAsync(Binding(60));
        await ChatAsync(source, "the early question");
        Fixture.AdvanceClock(TimeSpan.FromMinutes(30));
        await ChatAsync(source, "the later question");
        Fixture.AdvanceClock(TimeSpan.FromMinutes(40));

        var fork = await ForkAsync(source, "sweep-restamp-user/fork", throughUserTurn: 0);
        await TickAsync(fork);

        Assert.Contains(await fork.GetHistoryAsync(), t => t.Text == "the early question");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<IAgentGrain> ForkAsync(IAgentGrain source, string forkKey, int throughUserTurn = 0)
    {
        var users = (await source.GetHistoryAsync()).Where(t => t.Role == "user").Select(t => t.Text).ToList();
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(users[^1])));
        var result = await source.CreateForkAsync(throughUserTurn, users.Count, hash);
        Assert.Equal(HistoryEditOutcome.Ok, result.Outcome);
        var fork = Agent(forkKey);
        Assert.True(await fork.InitializeFromForkAsync(result.Fork!));
        return fork;
    }

    // The grain's reminder names, read from the silo's reminder table (in-memory in every test silo).
    private async Task<List<string>> ReminderNamesAsync(IAgentGrain agent)
    {
        var table = Cluster.GetSiloServiceProvider(Cluster.Silos[0].SiloAddress).GetRequiredService<IReminderTable>();
        return [.. (await table.ReadRows(agent.GetGrainId())).Reminders.Select(r => r.ReminderName)];
    }

    private async Task<AgentGrainState> StoredAsync(string grainKey)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", Agent(grainKey).GetGrainId(), state);
        return state.State;
    }

    // Edits the persisted state behind the grain's back; deactivate the grain first or its next write overwrites this.
    private async Task MutateStoredAsync(string grainKey, Action<AgentGrainState> mutate)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var grainId = Agent(grainKey).GetGrainId();
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", grainId, state);
        mutate(state.State);
        await storage.WriteStateAsync("agent", grainId, state);
    }

    private static void StripCreatedAt(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                obj.Remove("createdAt");
                foreach (var child in obj.Select(p => p.Value).ToList())
                {
                    StripCreatedAt(child);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    StripCreatedAt(child);
                }

                break;
        }
    }
}
