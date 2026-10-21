using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Runtime;
using Orleans.Storage;

namespace Agent42.Tests;

/// <summary>
/// Proactive chat delivery (ADR 0111, C9): where a conversation's chat origin is written, when an away reply or the
/// model's notification reaches <see cref="IChatDeliverySender"/>, and which chat the gateway's implementation picks.
/// The grain rows run on the test cluster against <see cref="RecordingChatDeliverySender"/>; the sender rows run the
/// real <see cref="ChatDeliverySender"/> against fake platform sessions.
/// </summary>
public sealed class ChatDeliveryTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string Owner = "111111111";
    private const string Reply = "Hello! How can I help you today?";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private int _messageId;

    // ---- the write path ----

    // AC20: a pipeline-minted conversation has its origin before its first turn finishes (read from storage while the
    // turn is parked mid-stream, so only SetChatOriginAsync's own write can have put it there), and a continued
    // conversation keeps it.
    [Fact]
    public async Task Origin_IsWrittenOnMint_BeforeTheFirstTurn_AndKeptWhenContinued()
    {
        var h = await PairedAsync();
        var gates = OpenGates();

        await h.Session.InjectAsync(Dm("first"));
        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        var origin = await StoredOriginAsync(h.UserId, conversationId);
        Assert.NotNull(origin);
        Assert.Equal((h.ConnectionId, Owner), (origin.ConnectionId, origin.RouteKey));

        CloseGates();
        await FinalReplyAsync(h);
        await h.Session.InjectAsync(Dm("second"));
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);

        Assert.Equal(conversationId, (await Route(h).GetAsync()).ConversationId);
        var kept = await StoredOriginAsync(h.UserId, conversationId);
        Assert.Equal((h.ConnectionId, Owner), (kept!.ConnectionId, kept.RouteKey));
    }

    // The grain is write-once: a later caller can never move a conversation to another chat.
    [Fact]
    public async Task SetChatOrigin_IsIdempotent_AndWritesOnlyWhenUnset()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("deliv-origin-user/deliv-origin-conv");

        await agent.SetChatOriginAsync(new ChatOrigin { ConnectionId = "conn-a", RouteKey = "1" });
        await agent.SetChatOriginAsync(new ChatOrigin { ConnectionId = "conn-b", RouteKey = "2" });

        var origin = await StoredOriginAsync("deliv-origin-user", "deliv-origin-conv");
        Assert.Equal(("conn-a", "1"), (origin!.ConnectionId, origin.RouteKey));
    }

    // ---- the grain side ----

    // A queued run (EnqueuePromptAsync) in a chat-origin conversation delivers its reply text to that origin.
    [Fact]
    public async Task EnqueuePrompt_InAChatConversation_DeliversItsReplyWithTheOrigin()
    {
        var h = await PairedAsync();
        await h.Session.InjectAsync(Dm("hello"));
        await FinalReplyAsync(h);
        var conversationId = (await Route(h).GetAsync()).ConversationId!;

        await Agent(h.UserId, conversationId).EnqueuePromptAsync("hi again", null);

        await Until(() => DeliveriesFor(conversationId).Count >= 1);
        var delivery = Assert.Single(DeliveriesFor(conversationId));
        Assert.Equal(ChatDeliveryKind.AwayReply, delivery.Kind);
        Assert.Equal((h.ConnectionId, Owner), (delivery.Origin!.ConnectionId, delivery.Origin.RouteKey));
        Assert.Contains(Reply, delivery.Text, StringComparison.Ordinal);
        Assert.Equal("Agent 42", delivery.Title);
    }

    // A fired reminder delivers the last assistant text with the origin, on the reminder's own path.
    [Fact]
    public async Task Reminder_InAChatConversation_DeliversItsReplyWithTheOrigin()
    {
        var h = await PairedAsync();
        await h.Session.InjectAsync(Dm("please remind me to drink water"));
        await FinalReplyAsync(h);
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        var agent = Agent(h.UserId, conversationId);

        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        var delivery = Assert.Single(DeliveriesFor(conversationId));
        Assert.Equal(ChatDeliveryKind.AwayReply, delivery.Kind);
        Assert.Equal((h.ConnectionId, Owner), (delivery.Origin!.ConnectionId, delivery.Origin.RouteKey));
        var history = await agent.GetHistoryAsync();
        Assert.Equal(history[^1].Text, delivery.Text);
    }

    // A web conversation has no origin: the away reply still goes to the port, which routes it home.
    [Fact]
    public async Task AwayReply_InAWebConversation_DeliversWithNoOrigin()
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("deliv-web-user/deliv-web-conv");
        await agent.BindAgentAsync(new AgentBinding("agent-1", "Marvin", "You are Marvin.", TestSiloConfigurator.DefaultModel));

        await agent.EnqueuePromptAsync("hi", null);

        await Until(() => DeliveriesFor("deliv-web-conv").Count >= 1);
        var delivery = Assert.Single(DeliveriesFor("deliv-web-conv"));
        Assert.Null(delivery.Origin);
        Assert.Equal(ChatDeliveryKind.AwayReply, delivery.Kind);
        Assert.Equal("Marvin", delivery.Title);
        Assert.False(string.IsNullOrWhiteSpace(delivery.Text));
    }

    // The push tool delivers a Notification beside Web Push.
    [Fact]
    public async Task PushTool_DeliversANotification()
    {
        await SubscribeDeviceAsync("deliv-tool-user");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("deliv-tool-user/deliv-tool-conv");

        await CollectAsync(agent.ChatAsync("send me a notification", null));

        var delivery = Assert.Single(DeliveriesFor("deliv-tool-conv"));
        Assert.Equal(ChatDeliveryKind.Notification, delivery.Kind);
        Assert.Equal(FakeChatClient.PushTitle, delivery.Title);
        Assert.Equal(FakeChatClient.PushBody, delivery.Text);
    }

    // A web conversation whose model already notified the home chat does not also get the generic away reply there.
    [Fact]
    public async Task AwayReply_AfterTheModelNotified_IsNotDeliveredAsASecondHomeMessage()
    {
        await SubscribeDeviceAsync("deliv-both-user");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("deliv-both-user/deliv-both-conv");

        await agent.EnqueuePromptAsync("send me a notification", null);

        await Until(() => DeliveriesFor("deliv-both-conv").Count >= 1);
        await Task.Delay(300);
        Assert.Equal(ChatDeliveryKind.Notification, Assert.Single(DeliveriesFor("deliv-both-conv")).Kind);
    }

    // A chat-driven turn runs through ChatAsync, never the two away-reply sites, so it is never posted back.
    [Fact]
    public async Task ChatDrivenTurn_IsNeverDeliveredBack()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("hello"));
        await FinalReplyAsync(h);
        await Task.Delay(300);

        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        Assert.Empty(DeliveriesFor(conversationId));
        Assert.Single(h.Session.VisibleMessages(), m => m.Contains(Reply, StringComparison.Ordinal));
    }

    // Incognito never delivers: the tool refuses before it reaches the port, as it does for push.
    [Fact]
    public async Task Incognito_NeverDelivers()
    {
        await SubscribeDeviceAsync("deliv-incognito-user");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>("deliv-incognito-user/deliv-incognito-conv");

        await CollectAsync(agent.ChatAsync("send me a notification", null, null, ephemeral: true));
        await CollectAsync(agent.ChatAsync("hi", null, null, ephemeral: true));

        Assert.Empty(DeliveriesFor("deliv-incognito-conv"));
    }

    // ---- the gateway's implementation ----

    // Review M2: a group origin is delivered to only while groups are still on, since everyone there would see it. With
    // groups turned off since, the reply falls back to the home chat (when notifications are on) or nowhere.
    [Fact]
    public async Task Sender_GroupOrigin_OnlyWhileGroupsAreOn()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        var groupsOff = await ConnectAsync(user, sessions, notifications: true, home: "999", groups: false);

        await Sender(sessions).DeliverNowAsync(user, Delivery(ChatDeliveryKind.AwayReply, groupsOff.Connection.Id, "-100", group: true), CancellationToken.None);

        Assert.Equal(new ChatTarget("999"), Assert.Single(groupsOff.Session.Sent, s => s.Op == "send").Target);

        var otherUser = NewUser();
        var otherSessions = new ChatSessionRegistry();
        var groupsOn = await ConnectAsync(otherUser, otherSessions, notifications: false, home: null, groups: true);

        await Sender(otherSessions).DeliverNowAsync(otherUser, Delivery(ChatDeliveryKind.AwayReply, groupsOn.Connection.Id, "-100", group: true), CancellationToken.None);

        Assert.Equal(new ChatTarget("-100"), Assert.Single(groupsOn.Session.Sent, s => s.Op == "send").Target);
    }

    // The caller is a non-reentrant conversation grain: a hung platform must never hold it. The hand-off returns at
    // once, and the send still lands once the platform answers.
    [Fact]
    public async Task Sender_HandsOff_WithoutWaitingOnAHungPlatform()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        var origin = await ConnectAsync(user, sessions, notifications: false, home: null);
        origin.Session.Hang = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var handOff = Sender(sessions).DeliverAsync(user, Delivery(ChatDeliveryKind.AwayReply, origin.Connection.Id, "555"));

        Assert.True(handOff.IsCompleted);
        origin.Session.Hang.SetResult();
        await ChatGatewayServiceTests.Until(() => origin.Session.Sent.Any(s => s.Op == "send"));
    }

    [Fact]
    public async Task Sender_AwayReply_GoesToItsOrigin_AndKeepsTheThread()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        var origin = await ConnectAsync(user, sessions, notifications: false, home: null);
        var home = await ConnectAsync(user, sessions, notifications: true, home: "999");

        await Sender(sessions).DeliverNowAsync(user, Delivery(ChatDeliveryKind.AwayReply, origin.Connection.Id, "555#77"), CancellationToken.None);

        var send = Assert.Single(origin.Session.Sent, s => s.Op == "send");
        Assert.Equal(new ChatTarget("555", "77"), send.Target with { ReplyToMessageId = null });
        Assert.Contains("Marvin", send.Text, StringComparison.Ordinal);
        Assert.Contains("the reply", send.Text, StringComparison.Ordinal);
        Assert.Empty(home.Session.Sent);
    }

    [Fact]
    public async Task Sender_AwayReply_FallsBackToHome_OnlyWhenNotificationsAreEnabled()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        var origin = await ConnectAsync(user, sessions, notifications: false, home: null, live: false);
        var home = await ConnectAsync(user, sessions, notifications: true, home: "999");

        await Sender(sessions).DeliverNowAsync(user, Delivery(ChatDeliveryKind.AwayReply, origin.Connection.Id, "555"), CancellationToken.None);

        Assert.Equal(new ChatTarget("999"), Assert.Single(home.Session.Sent, s => s.Op == "send").Target);

        var quietUser = NewUser();
        var quietSessions = new ChatSessionRegistry();
        var quietOrigin = await ConnectAsync(quietUser, quietSessions, notifications: false, home: null, live: false);
        var quietHome = await ConnectAsync(quietUser, quietSessions, notifications: false, home: "999");

        await Sender(quietSessions).DeliverNowAsync(quietUser, Delivery(ChatDeliveryKind.AwayReply, quietOrigin.Connection.Id, "555"), CancellationToken.None);

        Assert.Empty(quietHome.Session.Sent);
    }

    [Fact]
    public async Task Sender_Notification_GoesHome_EvenWithALiveOrigin()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        var origin = await ConnectAsync(user, sessions, notifications: false, home: null);
        var home = await ConnectAsync(user, sessions, notifications: true, home: "999");

        await Sender(sessions).DeliverNowAsync(user, Delivery(ChatDeliveryKind.Notification, origin.Connection.Id, "555"), CancellationToken.None);

        Assert.Empty(origin.Session.Sent);
        Assert.Equal(new ChatTarget("999"), Assert.Single(home.Session.Sent, s => s.Op == "send").Target);
    }

    [Fact]
    public async Task Sender_NothingQualifies_SendsNothingAndDoesNotThrow()
    {
        var user = NewUser();
        var sessions = new ChatSessionRegistry();
        // Notifications on but no home route, and a home route with notifications off.
        var noRoute = await ConnectAsync(user, sessions, notifications: true, home: null);
        var off = await ConnectAsync(user, sessions, notifications: false, home: "999");
        // Notifications and a home route, but no live session.
        var offline = await ConnectAsync(user, sessions, notifications: true, home: "888", live: false);

        await Sender(sessions).DeliverNowAsync(user, Delivery(ChatDeliveryKind.Notification, null, null), CancellationToken.None);
        await Sender(sessions).DeliverNowAsync(NewUser(), Delivery(ChatDeliveryKind.AwayReply, "no-such-connection", "1"), CancellationToken.None);

        Assert.Empty(noRoute.Session.Sent);
        Assert.Empty(off.Session.Sent);
        Assert.Empty(offline.Session.Sent);
    }

    // ---- harness ----

    private ChatDeliverySender Sender(ChatSessionRegistry sessions)
        => new(
            Cluster.GrainFactory, sessions, new ChatReplyStreamer(TimeProvider.System, NullLogger<ChatReplyStreamer>.Instance),
            [new FakeChatPlatformAdapter()], NullLogger<ChatDeliverySender>.Instance);

    private static ChatDelivery Delivery(ChatDeliveryKind kind, string? originConnectionId, string? originRoute, bool group = false)
        => new(
            "conv-1",
            originConnectionId is null ? null : new ChatOrigin { ConnectionId = originConnectionId, RouteKey = originRoute ?? "", IsGroup = group },
            kind, "Marvin", "the reply");

    private static string NewUser() => "deliv-sender-user-" + Guid.NewGuid().ToString("N");

    // A paired connection with a fake session, registered as live unless told otherwise.
    private async Task<(ChatConnection Connection, FakeChatSession Session)> ConnectAsync(
        string userId, ChatSessionRegistry sessions, bool notifications, string? home, bool live = true, bool groups = false)
    {
        var store = Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(userId);
        var created = await store.CreateAsync(
            ChatPlatformKind.Telegram, new ChatConnectionSettings { Name = "x", NotificationsEnabled = notifications, GroupsEnabled = groups }, Now);
        await store.SetCredentialsAsync(
            created!.Id, "cipher", new ChatBotIdentity(FakeChatPlatformAdapter.BotUserId, FakeChatPlatformAdapter.BotUserName), Now);
        var code = (await store.GetAsync(created.Id))!.PairingCode!;
        Assert.Equal(ChatPairingOutcome.Paired, await store.TryPairAsync(created.Id, Owner, null, code, Now));
        if (home is not null)
        {
            await store.SetHomeRouteAsync(created.Id, home);
        }

        var connection = (await store.GetAsync(created.Id))!;
        var session = new FakeChatSession(
            new ChatConnectionRuntime(userId, connection, new ChatCredentials("token"), _ => Task.CompletedTask), new NullSink());
        if (live)
        {
            sessions.Set(connection.Id, session);
        }

        return (connection, session);
    }

    private sealed class NullSink : IChatInboundSink
    {
        public Task AcceptAsync(ChatConnectionRuntime runtime, ChatInboundMessage message, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed record Harness(FakeChatSession Session, string UserId, string ConnectionId);

    // The real pipeline over a fake session, for a paired owner.
    private async Task<Harness> PairedAsync()
    {
        var userId = "deliv-pipe-user-" + Guid.NewGuid().ToString("N");
        var store = Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(userId);
        var created = await store.CreateAsync(ChatPlatformKind.Telegram, new ChatConnectionSettings { Name = "x" }, Now);
        var connection = (await store.SetCredentialsAsync(created!.Id, "cipher",
            new ChatBotIdentity(FakeChatPlatformAdapter.BotUserId, FakeChatPlatformAdapter.BotUserName), Now))!;
        Assert.Equal(
            ChatPairingOutcome.Paired,
            await store.TryPairAsync(connection.Id, Owner, null, (await store.GetAsync(connection.Id))!.PairingCode!, Now));

        var sessions = new ChatSessionRegistry();
        var pipeline = new ChatTurnPipeline(
            Cluster.GrainFactory, new InboundDeduper(TimeProvider.System), sessions,
            new ChatReplyStreamer(TimeProvider.System, NullLogger<ChatReplyStreamer>.Instance),
            [new FakeChatPlatformAdapter()], TimeProvider.System, NullLogger<ChatTurnPipeline>.Instance);
        var session = new FakeChatSession(
            new ChatConnectionRuntime(userId, connection, new ChatCredentials("token"), _ => Task.CompletedTask), pipeline);
        sessions.Set(connection.Id, session);
        return new Harness(session, userId, connection.Id);
    }

    private ChatInboundMessage Dm(string text)
        => new(Interlocked.Increment(ref _messageId).ToString(), Owner, ChatType.Direct, null, Owner, null, false, text, false, false, [], Now);

    private static async Task FinalReplyAsync(Harness h)
        => await Until(() => h.Session.VisibleMessages().Any(m => m.Contains(Reply, StringComparison.Ordinal)
            && !m.EndsWith(" ▍", StringComparison.Ordinal)));

    private IChatRouteGrain Route(Harness h)
        => Cluster.GrainFactory.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(h.UserId, h.ConnectionId, ChatRouteKey.For(Owner, null)));

    private IAgentGrain Agent(string userId, string conversationId)
        => Cluster.GrainFactory.GetGrain<IAgentGrain>($"{userId}/{conversationId}");

    private async Task<ChatOrigin?> StoredOriginAsync(string userId, string conversationId)
    {
        var storage = Cluster.GetSiloServiceProvider(Cluster.Primary.SiloAddress)
            .GetRequiredKeyedService<IGrainStorage>(AgentGrain.StorageProviderName);
        var state = new GrainState<AgentGrainState>(new AgentGrainState());
        await storage.ReadStateAsync("agent", Agent(userId, conversationId).GetGrainId(), state);
        return state.State.Origin;
    }

    // The recorder is a per-silo singleton and the grain may activate on any silo, so read them all.
    private IReadOnlyList<ChatDelivery> DeliveriesFor(string conversationId)
        => [.. Cluster.Silos
            .SelectMany(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
                .GetRequiredService<RecordingChatDeliverySender>().For(conversationId))];

    private Task SubscribeDeviceAsync(string userId)
        => Cluster.GrainFactory.GetGrain<IUserPushGrain>(userId)
            .AddAsync(new PushSubscription($"https://push.example/{userId}", "p256dh-key", "auth-secret"));

    private List<FakeChatClient.StreamGate> OpenGates()
        => Cluster.Silos.Select(s => Cluster.GetSiloServiceProvider(s.SiloAddress).GetRequiredService<FakeChatClient>()
            .OpenStreamGate(afterChunks: 0)).ToList();

    private void CloseGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>().CloseStreamGate();
        }
    }

    private static async Task CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        await foreach (var _ in stream)
        {
        }
    }

    private static Task Until(Func<bool> condition) => ChatGatewayServiceTests.Until(condition);
}
