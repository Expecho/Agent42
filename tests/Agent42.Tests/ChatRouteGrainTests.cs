using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// A chat route's current conversation (ADR 0111): minted on first use and on <c>/new</c>, kept across turns and
/// reactivation, rolled over (visibly, by the pipeline) after <see cref="ChatGatewayLimits.MaxTurnsPerConversation"/>.
/// </summary>
public sealed class ChatRouteGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private IChatRouteGrain NewRoute()
        => Cluster.GrainFactory.GetGrain<IChatRouteGrain>(
            ChatRouteKey.GrainKey("route-user", Guid.NewGuid().ToString("N"), "111111111"));

    [Fact]
    public async Task FirstTurn_Mints_ThenTheSameConversationContinues()
    {
        var route = NewRoute();

        var first = await route.BeginTurnAsync(forceNew: false);
        var second = await route.BeginTurnAsync(forceNew: false);

        Assert.True(first.Minted);
        Assert.True(ChatGatewayLimits.IsValidConversationId(first.ConversationId));
        Assert.False(second.Minted);
        Assert.Equal(first.ConversationId, second.ConversationId);
        Assert.Equal(2, (await route.GetAsync()).TurnCount);
    }

    [Fact]
    public async Task StartNew_AndForceNew_MintAFreshConversation()
    {
        var route = NewRoute();
        var first = await route.BeginTurnAsync(forceNew: false);

        await route.StartNewAsync();
        var afterNew = await route.BeginTurnAsync(forceNew: false);
        var forced = await route.BeginTurnAsync(forceNew: true);

        Assert.True(afterNew.Minted);
        Assert.NotEqual(first.ConversationId, afterNew.ConversationId);
        Assert.True(forced.Minted);
        Assert.False(forced.RolledOver);
        Assert.NotEqual(afterNew.ConversationId, forced.ConversationId);
    }

    [Fact]
    public async Task ReachingTheTurnCap_RollsOver()
    {
        var route = NewRoute();
        var first = await route.BeginTurnAsync(forceNew: false);
        for (var i = 1; i < ChatGatewayLimits.MaxTurnsPerConversation; i++)
        {
            Assert.False((await route.BeginTurnAsync(forceNew: false)).Minted);
        }

        var rolled = await route.BeginTurnAsync(forceNew: false);

        Assert.True(rolled.Minted);
        Assert.True(rolled.RolledOver);
        Assert.NotEqual(first.ConversationId, rolled.ConversationId);
        Assert.Equal(1, (await route.GetAsync()).TurnCount);
    }

    [Fact]
    public async Task ModelAndAgentOverrides_RideTheTurn_AndAgentStartsNew()
    {
        var route = NewRoute();
        var first = await route.BeginTurnAsync(forceNew: false);

        await route.SetModelAsync(" foundry:gpt-5.4 ");
        await route.SetAgentAsync("agent-123");
        var next = await route.BeginTurnAsync(forceNew: false);

        Assert.Equal("foundry:gpt-5.4", next.ModelOverride);
        Assert.Equal("agent-123", next.TargetAgentOverride);
        Assert.True(next.Minted);
        Assert.NotEqual(first.ConversationId, next.ConversationId);

        await route.SetModelAsync(null);
        Assert.Null((await route.GetAsync()).ModelOverride);
    }

    [Fact]
    public async Task State_SurvivesReactivation()
    {
        var route = NewRoute();
        var first = await route.BeginTurnAsync(forceNew: false);

        await route.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        Assert.Equal(first.ConversationId, (await route.GetAsync()).ConversationId);
    }

    [Fact]
    public void GrainKey_HashesTheRouteKey_AndLeadsWithTheUser()
    {
        var key = ChatRouteKey.GrainKey("user-1", "conn-1", ChatRouteKey.For("-1001234567890", "../evil"));

        Assert.StartsWith("user-1/conn-1/", key);
        Assert.Matches("^user-1/conn-1/[0-9a-f]{32}$", key);
        Assert.Equal("C1#173", ChatRouteKey.For("C1", "173"));
        Assert.Equal("C1", ChatRouteKey.For("C1", null));
    }
}
