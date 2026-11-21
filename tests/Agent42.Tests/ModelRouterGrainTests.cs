using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The Auto model on the grain (ADR 0137): the router decides, the grain applies it through the ordinary model switch,
/// and the reserved wire value <c>auto</c> is never persisted as a model.
/// </summary>
public sealed class ModelRouterGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string Blind = TestSiloConfigurator.NoVisionModel;

    private static int _conversation;

    private static string NewKey() => $"router-user/conv-{Interlocked.Increment(ref _conversation)}-{Guid.NewGuid():N}";

    // A grain may activate on any silo, so the router goes on all of them.
    private FakeModelRouter UseRouter(RouterDecision? decision, Exception? throws = null)
    {
        var router = new FakeModelRouter { Decision = decision, Throws = throws };
        SetRouter(router);
        return router;
    }

    private void SetRouter(IModelRouter? router)
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeModelRouterProvider>().Router = router;
        }
    }

    private static async Task<List<AgentStreamEvent>> Collect(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }

    private static RouterDecision To(string model, bool changed = true) => new(model, model, changed);

    [Fact]
    public async Task AutoTurn_PersistsRealModel_AndRestoresToAuto()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        var routed = Assert.Single(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal((Blind, Blind), (routed.Model, routed.Text));
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Single(router.Calls);
        // The picker restores to Auto, and the REAL model is what the conversation runs on.
        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());
        SetRouter(null);
        Assert.Equal(Blind, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task RouterFailure_KeepsModel_AndTheTurnCompletes()
    {
        UseRouter(null, throws: new InvalidOperationException("router down"));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task RouterReturnsNothing_KeepsModel()
    {
        UseRouter(null);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Sticky_LabelsOnlyTheFirstAutoTurnAndRealChanges()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var first = await Collect(agent.ChatAsync("one", AutoModelId.Value));
        router.Decision = To(Blind, changed: false);
        var second = await Collect(agent.ChatAsync("two", AutoModelId.Value));
        router.Decision = To(TestSiloConfigurator.DefaultModel);
        var third = await Collect(agent.ChatAsync("three", AutoModelId.Value));

        Assert.Contains(first, e => e.Type == AgentStreamEventType.Routed);
        Assert.DoesNotContain(second, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal(TestSiloConfigurator.DefaultModel, Assert.Single(third, e => e.Type == AgentStreamEventType.Routed).Model);
        Assert.Equal(3, router.Calls.Count);
        // The router is told the model the conversation is on, so it can be sticky.
        Assert.Equal([TestSiloConfigurator.DefaultModel, Blind, Blind], router.Calls.Select(c => c.CurrentModelId));
    }

    [Fact]
    public async Task ExplicitModel_ClearsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));
        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());

        await Collect(agent.ChatAsync("hello again", TestSiloConfigurator.DefaultModel));

        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task NullModel_FromABackgroundRun_KeepsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        await Collect(agent.ChatAsync("follow up", null));

        Assert.Equal(AutoModelId.Value, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task NoRouter_AutoIsIgnored_NeverPersisted()
    {
        SetRouter(null);
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task StaleAuto_AfterTheRouterIsGone_ReportsTheRealModelAndRunsOnIt()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        SetRouter(null);
        var events = await Collect(agent.ChatAsync("still sending auto", AutoModelId.Value));

        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(Blind, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Auto_SkipsEphemeralTurns()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        var events = await Collect(agent.ChatAsync("incognito", AutoModelId.Value, ephemeral: true));

        Assert.Empty(router.Calls);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Routed);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    // A turn that overlaps another run would swap the agent and session under it (ADR 0106): it never routes.
    [Fact]
    public async Task Auto_SkipsATurnWhileAnotherRunIsInFlight()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await using var slow = agent.ChatAsync(FakeChatClient.SlowStreamMarker, TestSiloConfigurator.DefaultModel).GetAsyncEnumerator();
        Assert.True(await slow.MoveNextAsync());
        var second = await Collect(agent.ChatAsync("route me", AutoModelId.Value)).WaitAsync(TimeSpan.FromSeconds(20));
        while (await slow.MoveNextAsync())
        {
        }

        Assert.Empty(router.Calls);
        Assert.DoesNotContain(second, e => e.Type == AgentStreamEventType.Routed);
        SetRouter(null);
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    // The switch below an explicit pick still runs on an incognito turn, so Auto must not outlive it.
    [Fact]
    public async Task ExplicitModel_OnAnIncognitoTurn_ClearsAuto()
    {
        UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        await Collect(agent.ChatAsync("hidden", TestSiloConfigurator.DefaultModel, ephemeral: true));

        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Auto_DoesNotRouteAnAgentPinnedChat()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());
        await agent.BindAgentAsync(new AgentBinding("agent-1", "Pinned", "prompt", "pinned-model"));

        await Collect(agent.ChatAsync("hello", AutoModelId.Value));

        Assert.Empty(router.Calls);
        Assert.Equal("pinned-model", await agent.GetSelectedModelAsync());
    }

    [Fact]
    public async Task Router_IsToldTheTurnFacts()
    {
        var router = UseRouter(To(Blind));
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Collect(agent.ChatAsync("a hard question", AutoModelId.Value));

        var call = Assert.Single(router.Calls);
        Assert.Equal(("a hard question", false, true), (call.Message, call.HasImages, call.ToolsEnabled));
    }

    [Fact]
    public async Task AutoOutsideChat_BindAgent_IsRefused_AndNothingIsPersisted()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(NewKey());

        await Assert.ThrowsAsync<ArgumentException>(
            () => agent.BindAgentAsync(new AgentBinding("agent-1", "Pinned", "prompt", AutoModelId.Value)));

        Assert.Null(await agent.GetAgentBindingAsync());
        Assert.Equal(TestSiloConfigurator.DefaultModel, await agent.GetSelectedModelAsync());
    }
}
