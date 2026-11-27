using Agent42.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// <see cref="ChatDeliveryRelay"/> in isolation (ADR 0148): the skip rules and the live origin read, without a silo.
/// The end-to-end delivery is pinned by <see cref="ChatDeliveryTests"/>.
/// </summary>
public sealed class ChatDeliveryRelayTests
{
    private static readonly ChatOrigin Origin = new() { ConnectionId = "c1", RouteKey = "r1" };

    private static (ChatDeliveryRelay Relay, RecordingChatDeliverySender Sender) Build(Func<ChatOrigin?> origin)
    {
        var sender = new RecordingChatDeliverySender();
        return (new ChatDeliveryRelay(sender, "u", "conv", "u/conv", NullLogger.Instance, origin, () => null), sender);
    }

    [Fact]
    public async Task AwayReply_IsSkipped_ForAnEphemeralRun()
    {
        var (relay, sender) = Build(() => Origin);

        await relay.DeliverAwayReplyAsync(TurnContext.ForChat(ephemeral: true, interactive: false, broker: null), "hi");

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task AwayReply_IsSkipped_ForEmptyText()
    {
        var (relay, sender) = Build(() => Origin);

        await relay.DeliverAwayReplyAsync(TurnContext.ForBackground(), "  ");

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task AwayReply_IsSkipped_WithoutOriginWhenTheModelAlreadyPushed()
    {
        var (relay, sender) = Build(() => null);
        var turn = TurnContext.ForBackground();
        turn.RecordPushSent();

        await relay.DeliverAwayReplyAsync(turn, "hi");

        Assert.Empty(sender.Sends);
    }

    [Fact]
    public async Task AwayReply_ReadsTheOriginLive_AndDefaultsTheAgentName()
    {
        ChatOrigin? current = null;
        var (relay, sender) = Build(() => current);
        current = Origin;

        await relay.DeliverAwayReplyAsync(TurnContext.ForBackground(), "hi");

        var delivery = Assert.Single(sender.Sends).Delivery;
        Assert.Same(Origin, delivery.Origin);
        Assert.Equal("Agent 42", delivery.Title);
        Assert.Equal(ChatDeliveryKind.AwayReply, delivery.Kind);
    }

    [Fact]
    public async Task Notification_IsSkipped_ForAnEphemeralRun_AndSentOtherwise()
    {
        var (relay, sender) = Build(() => Origin);

        await relay.DeliverNotificationAsync(
            TurnContext.ForChat(ephemeral: true, interactive: false, broker: null), "t", "b", CancellationToken.None);
        Assert.Empty(sender.Sends);

        await relay.DeliverNotificationAsync(TurnContext.ForBackground(), "t", "b", CancellationToken.None);
        Assert.Equal(ChatDeliveryKind.Notification, Assert.Single(sender.Sends).Delivery.Kind);
    }
}
