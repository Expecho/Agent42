using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The result turn of a fleet task started from a chat (ADR 0115): <see cref="IAgentGrain.ReceiveFleetResultAsync"/>
/// runs the agent-run background body, so the reply is stored, pushed and delivered to the chat app, and a deleted
/// conversation drops the result without running a turn. One-way, so the tests poll the recorders.
/// </summary>
public sealed class FleetResultDeliveryTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static FleetTaskRecord Done(string id) => new()
    {
        Id = id,
        UserId = "u",
        Request = "Write a haiku",
        WorkspaceId = "w",
        PlannerModel = TestSiloConfigurator.DefaultModel,
        Status = FleetTaskStatus.Done,
        Result = "Light turns on the rock",
    };

    [Fact]
    public async Task ResultTurn_RepliesAndPushes()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("fleet-result-user/fleet-result-conv");
        await CollectAsync(agent.ChatAsync("hi", null));
        var before = (await agent.GetHistoryAsync()).Count;

        await agent.ReceiveFleetResultAsync("task-1", FleetToolLimits.FrameResult(Done("task-1")));

        await PollUntilAsync(() => PushesFor("fleet-result-user").Count >= 1 && DeliveriesFor("fleet-result-conv").Count >= 1);
        var history = await agent.GetHistoryAsync();
        Assert.Equal(before + 2, history.Count);
        Assert.StartsWith("[Agent42 fleet]", history[^2].Text, StringComparison.Ordinal);
        Assert.Contains("Light turns on the rock", history[^2].Text, StringComparison.Ordinal);
        Assert.Equal("assistant", history[^1].Role);
        Assert.Equal("fleet-result-conv", Assert.Single(PushesFor("fleet-result-user")).Message.ConversationId);
        Assert.Equal(ChatDeliveryKind.AwayReply, Assert.Single(DeliveriesFor("fleet-result-conv")).Kind);
    }

    [Fact]
    public async Task ResultTurn_DeletedConversation_Dropped()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("fleet-gone-user/fleet-gone-conv");
        await CollectAsync(agent.ChatAsync("hi", null));
        await agent.PurgeAsync();

        await agent.ReceiveFleetResultAsync("task-1", FleetToolLimits.FrameResult(Done("task-1")));
        await Task.Delay(500);

        Assert.Empty(await agent.GetHistoryAsync());
        Assert.Empty(PushesFor("fleet-gone-user"));
        Assert.Empty(DeliveriesFor("fleet-gone-conv"));
    }

    // Both recorders are per-silo singletons and the grain may activate on any silo, so read them all.
    private IReadOnlyList<(string UserId, PushMessage Message)> PushesFor(string userId)
        => [.. Cluster.Silos
            .SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<RecordingPushNotificationSender>().Sends)
            .Where(s => s.UserId == userId)];

    private IReadOnlyList<ChatDelivery> DeliveriesFor(string conversationId)
        => [.. Cluster.Silos
            .SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<RecordingChatDeliverySender>().For(conversationId))];

    private static async Task PollUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 300 && !condition(); attempt++)
        {
            await Task.Delay(100);
        }

        Assert.True(condition(), "The result turn never completed.");
    }

    private static async Task CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }
}
