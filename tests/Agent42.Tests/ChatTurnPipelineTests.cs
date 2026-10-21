using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The chat turn pipeline end to end on the test cluster's canned model (ADR 0111), through a fake platform session:
/// who may talk, which conversation a message lands in, the queue and its bypass, commands, groups, and the drain
/// discipline (the turn persists whatever the platform does).
/// </summary>
public sealed class ChatTurnPipelineTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string Owner = "111111111";
    private const string Stranger = "222222222";
    private const string Reply = "Hello! How can I help you today?";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private int _messageId;

    [Fact]
    public async Task Pipeline_Stranger_IgnoredWithoutState()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("hello", sender: Stranger, chat: Stranger));
        await Task.Delay(200);

        Assert.Empty(h.Session.Sent);
        Assert.Null((await Route(h, Stranger).GetAsync()).ConversationId);
        Assert.Empty(await Index(h).GetConversationsAsync());
    }

    [Fact]
    public async Task Pairing_InADm_BindsTheOwner_AndNeverStartsAConversation()
    {
        var h = await CreateAsync();
        var code = (await Store(h).GetAsync(h.ConnectionId))!.PairingCode!;

        await h.Session.InjectAsync(Dm("wrong code"));
        await h.Session.InjectAsync(Dm(code.ToLowerInvariant()));

        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("Linked to Agent42", StringComparison.Ordinal)));
        Assert.Equal(Owner, (await Store(h).GetAsync(h.ConnectionId))!.OwnerPlatformUserId);
        Assert.Single(h.Session.VisibleMessages());
        Assert.Empty(await Index(h).GetConversationsAsync());
    }

    [Fact]
    public async Task Pairing_FromAGroup_IsIgnored()
    {
        var h = await CreateAsync();
        var code = (await Store(h).GetAsync(h.ConnectionId))!.PairingCode!;

        await h.Session.InjectAsync(Group(code, mentions: true));
        await Task.Delay(200);

        Assert.False((await Store(h).GetAsync(h.ConnectionId))!.IsPaired);
        Assert.Empty(h.Session.Sent);
    }

    // AC1 + AC11 + AC10b: an owner DM lists a conversation and streams the reply; the turn runs as the owner (the
    // canned model echoes the RequestContext identity) and non-interactively (no follow-up suggestion call).
    [Fact]
    public async Task Pipeline_OwnerDm_Replies_AsTheOwner_NonInteractively()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("What's on today?"));

        var reply = await FinalReplyAsync(h, Reply);
        Assert.Contains($"[uctx:{h.UserId}]", reply);
        var conversation = Assert.Single(await Index(h).GetConversationsAsync());
        Assert.Equal("What's on today?", conversation.Title);
        Assert.True(ChatGatewayLimits.IsValidConversationId(conversation.Id));
        Assert.Equal(0, SuggestionCalls());
    }

    [Fact]
    public async Task Pipeline_OwnEchoAndBots_AreDropped()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("echo", sender: FakeChatPlatformAdapter.BotUserId));
        await h.Session.InjectAsync(Dm("from a bot") with { SenderIsBot = true });
        await Task.Delay(200);

        Assert.Empty(h.Session.Sent);
    }

    // AC4: the same platform message delivered twice, even through a new session after a reconnect, runs once.
    [Fact]
    public async Task Pipeline_DuplicateAcrossReconnect_RunsOnce()
    {
        var h = await PairedAsync();
        var message = Dm("only once");
        await h.Session.InjectAsync(message);
        await FinalReplyAsync(h, Reply);

        var reconnected = new FakeChatSession(h.Session.Runtime, h.Pipeline);
        h.Sessions.Set(h.ConnectionId, reconnected);
        await reconnected.InjectAsync(message);
        await Task.Delay(300);

        Assert.Empty(reconnected.Sent);
        Assert.Equal(1, (await Route(h).GetAsync()).TurnCount);
    }

    // AC5: two messages in one chat run one after the other in one conversation.
    [Fact]
    public async Task Pipeline_SameRoute_InOrder()
    {
        var h = await PairedAsync();
        var gates = OpenGates(afterChunks: 0);

        await h.Session.InjectAsync(Dm("first"));
        await h.Session.InjectAsync(Dm("second"));
        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        await Task.Delay(200);
        // The second has not started: only the first reply's placeholder exists.
        Assert.Single(h.Session.Sent, s => s.Op == "send");

        CloseGates();
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);
        var route = await Route(h).GetAsync();
        Assert.Equal(2, route.TurnCount);
        Assert.Equal(4, (await Agent(h, route.ConversationId!).GetHistoryAsync()).Count);
    }

    // AC5: different chats do not wait for each other.
    [Fact]
    public async Task Pipeline_TwoRoutes_Parallel()
    {
        var h = await PairedAsync();
        OpenGates(afterChunks: 0);

        await h.Session.InjectAsync(Dm("in the DM"));
        await h.Session.InjectAsync(Dm("in another chat", chat: "333333333"));

        // Both replies have their placeholder while the first turn is still parked.
        await Until(() => h.Session.Sent.Count(s => s.Op == "send") == 2);
        CloseGates();
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);
    }

    // AC5: /stop runs ahead of the queue, stops the running turn and drops what waits behind it.
    [Fact]
    public async Task Pipeline_Stop_BypassesQueue()
    {
        var h = await PairedAsync();
        var gates = OpenGates(afterChunks: 1);
        await h.Session.InjectAsync(Dm("tell me a long story"));
        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        await h.Session.InjectAsync(Dm("and another one"));

        await h.Session.InjectAsync(Dm("/stop"));

        await Until(() => h.Session.VisibleMessages().Contains("Stopped, and dropped 1 waiting message."));
        CloseGates();
        await Task.Delay(300);
        Assert.Equal(1, (await Route(h).GetAsync()).TurnCount);
    }

    // AC6: /new starts a fresh conversation for the next message.
    [Fact]
    public async Task Pipeline_Command_New()
    {
        var h = await PairedAsync();
        await h.Session.InjectAsync(Dm("first"));
        await FinalReplyAsync(h, Reply);
        var first = (await Route(h).GetAsync()).ConversationId;

        await h.Session.InjectAsync(Dm("/new"));
        await Until(() => h.Session.VisibleMessages().Contains("Started a new conversation."));
        await h.Session.InjectAsync(Dm("second"));
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);

        Assert.NotEqual(first, (await Route(h).GetAsync()).ConversationId);
        Assert.Equal(2, (await Index(h).GetConversationsAsync()).Count);
    }

    // AC6: /model picks one of the owner's models for later plain-chat turns; an unknown one is refused.
    [Fact]
    public async Task Pipeline_Command_Model()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("/model nope"));
        await h.Session.InjectAsync(Dm($"/model {TestSiloConfigurator.NoVisionModel}"));
        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("Model set to", StringComparison.Ordinal)));
        await h.Session.InjectAsync(Dm("hi"));
        await FinalReplyAsync(h, Reply);

        Assert.Contains(h.Session.VisibleMessages(), m => m.StartsWith("There's no model called \"nope\"", StringComparison.Ordinal));
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        Assert.Equal(TestSiloConfigurator.NoVisionModel, await Agent(h, conversationId).GetSelectedModelAsync());
    }

    // AC6 + AC7: /agent binds the NEXT conversation; a continued conversation is never re-bound.
    [Fact]
    public async Task Pipeline_Command_Agent_BindsTheNextConversationOnly()
    {
        var h = await PairedAsync();
        var agent = await NewAgentAsync(h, "Weather bot");
        await h.Session.InjectAsync(Dm("plain first"));
        await FinalReplyAsync(h, Reply);
        var plain = (await Route(h).GetAsync()).ConversationId!;

        await h.Session.InjectAsync(Dm("/agent weather BOT"));
        await Until(() => h.Session.VisibleMessages().Contains("Started a new conversation with Weather bot."));
        await h.Session.InjectAsync(Dm("now the agent"));
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);

        var withAgent = (await Route(h).GetAsync()).ConversationId!;
        Assert.NotEqual(plain, withAgent);
        Assert.Null(await Agent(h, plain).GetAgentBindingAsync());
        Assert.Equal(agent.Id, (await Agent(h, withAgent).GetAgentBindingAsync())!.AgentId);
    }

    // AC7: changing the connection's target never re-binds a running conversation.
    [Fact]
    public async Task Pipeline_Continue_NoRebind()
    {
        var first = await NewAgentAsync(null, "First");
        var h = await PairedAsync(targetAgentId: first.Id, userId: first.UserId);
        await h.Session.InjectAsync(Dm("hello"));
        await FinalReplyAsync(h, Reply);
        var second = await NewAgentAsync(h, "Second");

        await Store(h).UpdateSettingsAsync(h.ConnectionId, new ChatConnectionSettings { Name = "x", TargetAgentId = second.Id }, Now);
        await h.Session.InjectAsync(Dm("again"));
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);

        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        Assert.Equal(first.Id, (await Agent(h, conversationId).GetAgentBindingAsync())!.AgentId);
    }

    // AC7: a target agent that no longer exists gets a curated reply and no turn runs.
    [Fact]
    public async Task Pipeline_DeletedTarget_RepliesOnly()
    {
        var h = await PairedAsync(targetAgentId: "agent-that-was-deleted");

        await h.Session.InjectAsync(Dm("hello"));

        await Until(() => h.Session.VisibleMessages().Any(m => m.Contains("no longer exists", StringComparison.Ordinal)));
        Assert.Empty(await Index(h).GetConversationsAsync());
    }

    // AC10: a platform that hangs on every call cannot keep the turn from persisting.
    [Fact]
    public async Task Pipeline_HangingSession_StillPersists()
    {
        var h = await PairedAsync(callTimeout: TimeSpan.FromMilliseconds(100));
        h.Session.Hang = new TaskCompletionSource();

        await h.Session.InjectAsync(Dm("are you there?"));

        await Until(async () => (await Route(h).GetAsync()).ConversationId is { } id
            && (await Agent(h, id).GetHistoryAsync()).Count == 2);
        h.Session.Hang.TrySetResult();
    }

    // A model failure reaches the chat as curated text, never the exception.
    [Fact]
    public async Task Pipeline_ModelFailure_PostsCuratedText()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm(FakeChatClient.FaultingStreamMarker));

        var reply = await FinalReplyAsync(h, "I couldn't answer that.");
        Assert.DoesNotContain("Exception", reply);
    }

    // AC7b: past the turn cap the route rolls over to a fresh conversation, and says so.
    [Fact]
    public async Task Pipeline_Rollover_PostsNotice()
    {
        var h = await PairedAsync();
        await h.Session.InjectAsync(Dm("first"));
        await FinalReplyAsync(h, Reply);
        var route = Route(h);
        var before = (await route.GetAsync()).ConversationId;
        while ((await route.GetAsync()).TurnCount < ChatGatewayLimits.MaxTurnsPerConversation)
        {
            await route.BeginTurnAsync(forceNew: false);
        }

        await h.Session.InjectAsync(Dm("one more"));

        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("This conversation got long", StringComparison.Ordinal)));
        await Until(() => h.Session.VisibleMessages().Count(m => m.Contains(Reply, StringComparison.Ordinal)) == 2);
        Assert.NotEqual(before, (await route.GetAsync()).ConversationId);
    }

    // AC19: in a group, only the owner's messages that address the bot run, and only when groups are on.
    [Fact]
    public async Task Pipeline_Group_OwnerAndMentionOnly()
    {
        var off = await PairedAsync();
        await off.Session.InjectAsync(Group("@agent42_test_bot hi", mentions: true));

        var on = await PairedAsync(groups: true);
        await on.Session.InjectAsync(Group("no mention", mentions: false));
        await on.Session.InjectAsync(Group("@agent42_test_bot from a stranger", mentions: true, sender: Stranger));
        var addressed = Group("@agent42_test_bot summarize", mentions: true);
        await on.Session.InjectAsync(addressed);

        var reply = await FinalReplyAsync(on, Reply);
        Assert.Single(on.Session.VisibleMessages());
        Assert.All(on.Session.Sent.Where(s => s.Op == "send"), s => Assert.Equal(addressed.PlatformMessageId, s.Target.ReplyToMessageId));
        Assert.Equal("summarize", Assert.Single(await Index(on).GetConversationsAsync()).Title);
        Assert.Empty(off.Session.Sent);
        Assert.Contains(Reply, reply);
    }

    // AC6: /stop works in a group without the mention, or it would be unreachable in a busy group.
    [Fact]
    public async Task Pipeline_Group_StopWithoutMention()
    {
        var h = await PairedAsync(groups: true);

        await h.Session.InjectAsync(Group("/stop", mentions: false));

        await Until(() => h.Session.VisibleMessages().Contains("Stopped."));
    }

    // AC6: the read-only commands answer from the owner's own state, and /sethome stores the route.
    [Fact]
    public async Task Pipeline_Command_ListsStatusHelpAndHome()
    {
        var h = await PairedAsync();
        await NewAgentAsync(h, "Weather bot");

        await h.Session.InjectAsync(Dm("/model list"));
        await h.Session.InjectAsync(Dm("/agent list"));
        await h.Session.InjectAsync(Dm("/agent"));
        await h.Session.InjectAsync(Dm("/help"));
        await h.Session.InjectAsync(Dm("/sethome"));
        await h.Session.InjectAsync(Dm("/status"));
        await h.Session.InjectAsync(Dm("/model"));
        await h.Session.InjectAsync(Dm("/model default"));
        await h.Session.InjectAsync(Dm("/agent nobody"));

        await Until(() => h.Session.VisibleMessages().Count == 9);
        var replies = h.Session.VisibleMessages();
        Assert.Contains($"• {TestSiloConfigurator.NoVisionModel}", replies[0]);
        Assert.Contains("• Weather bot", replies[1]);
        Assert.Equal("New conversations here are plain chat.", replies[2]);
        Assert.Contains("/sethome", replies[3]);
        Assert.StartsWith("This chat is now home.", replies[4]);
        Assert.Contains("Home chat: this one", replies[5]);
        Assert.Equal("Model: your default model.", replies[6]);
        Assert.Equal("Back to your default model.", replies[7]);
        Assert.StartsWith("There's no agent called \"nobody\"", replies[8]);
        Assert.Equal(Owner, (await Store(h).GetAsync(h.ConnectionId))!.HomeRouteKey);
    }

    // Review M1: a sticker or an uncaptioned photo has no text, and must not become an empty model call.
    [Fact]
    public async Task Pipeline_NoText_RepliesOnce_AndRunsNoTurn()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm(""));

        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("I can read text, images and documents here", StringComparison.Ordinal)));
        Assert.Empty(await Index(h).GetConversationsAsync());
    }

    // Review M2: a turn that outlasts the wait cap frees the chat (the next message runs) while it runs on to persist.
    [Fact]
    public async Task Pipeline_LongTurn_IsCapped_AndTheQueueMovesOn()
    {
        var h = await PairedAsync(maxTurnWait: TimeSpan.FromMilliseconds(400));
        OpenGates(afterChunks: 1);
        await h.Session.InjectAsync(Dm("a very long research question"));

        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("This is taking a while", StringComparison.Ordinal)));
        CloseGates();
        await h.Session.InjectAsync(Dm("/status"));

        await Until(() => h.Session.VisibleMessages().Any(m => m.StartsWith("Model:", StringComparison.Ordinal)));
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        await Until(async () => (await Agent(h, conversationId).GetHistoryAsync()).Count == 2);
    }

    // AC17: an image rides onto the turn as an attachment; a document is ingested (and registered) before it runs.
    [Fact]
    public async Task Pipeline_Files_ImageAndDocumentReachTheTurn()
    {
        var h = await PairedAsync();
        h.Session.Files["img"] = new ChatDownloadedFile([1, 2, 3, 4], "pic.png", "image/png");
        h.Session.Files["doc"] = new ChatDownloadedFile(TestDocuments.SampleTwoPagePdf(), "guide.pdf", DocumentMediaTypes.Pdf);

        await h.Session.InjectAsync(Dm("look at these", files:
        [
            new ChatInboundFile("img", "pic.png", "image/png", 4, false),
            new ChatInboundFile("doc", "guide.pdf", null, null, false),
        ]));

        await FinalReplyAsync(h, Reply);
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        var user = Assert.Single(await Agent(h, conversationId).GetHistoryAsync(), t => t.Role == "user");
        Assert.Equal("look at these", user.Text);
        Assert.Contains(user.Attachments!, a => a.FileName == "pic.png");
        var query = (await new DeterministicEmbeddingGenerator().GenerateAsync(["magrathea"]))[0].Vector;
        Assert.Contains(await new InMemoryDocumentIndex().SearchAsync($"{h.UserId}/{conversationId}", query, topK: 3),
            hit => hit.FileName == "guide.pdf");
    }

    // AC17: a file over the cap is refused from its metadata, never downloaded; the owner and the model are both told.
    [Fact]
    public async Task Pipeline_Files_OversizedIsSkippedWithANote_AndNeverFetched()
    {
        var h = await PairedAsync();
        h.Session.Files["big"] = new ChatDownloadedFile([1], "huge.pdf", DocumentMediaTypes.Pdf);

        await h.Session.InjectAsync(Dm("read this", files: [new ChatInboundFile("big", "huge.pdf", DocumentMediaTypes.Pdf, 11L * 1024 * 1024, false)]));

        await FinalReplyAsync(h, Reply);
        Assert.Contains(h.Session.VisibleMessages(), m => m.Contains("'huge.pdf' exceeds the 10 MB limit.", StringComparison.Ordinal));
        Assert.Empty(h.Session.Downloads);
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        Assert.Contains("[Attachment skipped: 'huge.pdf' exceeds", Assert.Single(await Agent(h, conversationId).GetHistoryAsync(), t => t.Role == "user").Text);
    }

    // A message with only unusable files gets the reason and mints no conversation.
    [Fact]
    public async Task Pipeline_Files_OnlyUnusable_RepliesAndRunsNoTurn()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("", files: [new ChatInboundFile("exe", "setup.exe", "application/octet-stream", 10, false)]));

        await Until(() => h.Session.VisibleMessages().Any(m => m.Contains("'setup.exe' is not a supported", StringComparison.Ordinal)));
        await Task.Delay(200);
        Assert.Empty(await Index(h).GetConversationsAsync());
        Assert.Empty(h.Session.Downloads);
    }

    // A captionless photo is a turn of its own: titled by the file, prompted with what was sent.
    [Fact]
    public async Task Pipeline_Files_PhotoWithoutText_IsATurn()
    {
        var h = await PairedAsync();
        h.Session.Files["photo"] = new ChatDownloadedFile([9, 9, 9], "photo.jpg", "image/jpeg");

        await h.Session.InjectAsync(Dm("", files: [new ChatInboundFile("photo", "photo.jpg", "image/jpeg", 3, false)]));

        await FinalReplyAsync(h, Reply);
        Assert.Equal("photo.jpg", Assert.Single(await Index(h).GetConversationsAsync()).Title);
        var conversationId = (await Route(h).GetAsync()).ConversationId!;
        Assert.Equal("The user sent 'photo.jpg'.", Assert.Single(await Agent(h, conversationId).GetHistoryAsync(), t => t.Role == "user").Text);
    }

    // A download the adapter refuses (off its pinned host, gone, too large in fact) is skipped; the text still runs.
    [Fact]
    public async Task Pipeline_Files_FailedDownload_IsSkipped()
    {
        var h = await PairedAsync();

        await h.Session.InjectAsync(Dm("and this", files: [new ChatInboundFile("https://evil.example/x.pdf", "x.pdf", DocumentMediaTypes.Pdf, 100, false)]));

        await FinalReplyAsync(h, Reply);
        Assert.Contains(h.Session.VisibleMessages(), m => m.StartsWith("'x.pdf' couldn't be downloaded", StringComparison.Ordinal));
    }

    // ---- harness ----

    private sealed record Harness(
        ChatTurnPipeline Pipeline, FakeChatSession Session, ChatSessionRegistry Sessions, string UserId, string ConnectionId);

    private async Task<Harness> CreateAsync(
        string? targetAgentId = null, bool groups = false, TimeSpan? callTimeout = null, string? userId = null, TimeSpan? maxTurnWait = null)
    {
        userId ??= "pipe-user-" + Guid.NewGuid().ToString("N");
        var store = Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(userId);
        var created = await store.CreateAsync(ChatPlatformKind.Telegram,
            new ChatConnectionSettings { Name = "x", TargetAgentId = targetAgentId, GroupsEnabled = groups }, Now);
        var connection = (await store.SetCredentialsAsync(created!.Id, "cipher",
            new ChatBotIdentity(FakeChatPlatformAdapter.BotUserId, FakeChatPlatformAdapter.BotUserName), Now))!;

        var adapter = new FakeChatPlatformAdapter();
        var sessions = new ChatSessionRegistry();
        var pipeline = new ChatTurnPipeline(
            Cluster.GrainFactory, new InboundDeduper(TimeProvider.System), sessions,
            new ChatReplyStreamer(TimeProvider.System, NullLogger<ChatReplyStreamer>.Instance, callTimeout),
            [adapter], TimeProvider.System, NullLogger<ChatTurnPipeline>.Instance,
            new SingleUserModelCatalogProvider(new StaticModelCatalog(TestSiloConfigurator.DefaultModel,
            [
                new ModelInfo(TestSiloConfigurator.DefaultModel, "fake", null, null, null, true, true),
                new ModelInfo(TestSiloConfigurator.NoVisionModel, "fake", null, null, null, true, false),
            ])), maxTurnWait,
            SiloServices.GetRequiredService<IAttachmentStore>(), SiloServices.GetRequiredService<DocumentIngestionService>());
        var runtime = new ChatConnectionRuntime(userId, connection, new ChatCredentials("token"), _ => Task.CompletedTask);
        var session = new FakeChatSession(runtime, pipeline);
        sessions.Set(connection.Id, session);
        return new Harness(pipeline, session, sessions, userId, connection.Id);
    }

    private async Task<Harness> PairedAsync(
        string? targetAgentId = null, bool groups = false, TimeSpan? callTimeout = null, string? userId = null, TimeSpan? maxTurnWait = null)
    {
        var h = await CreateAsync(targetAgentId, groups, callTimeout, userId, maxTurnWait);
        var code = (await Store(h).GetAsync(h.ConnectionId))!.PairingCode!;
        Assert.Equal(ChatPairingOutcome.Paired, await Store(h).TryPairAsync(h.ConnectionId, Owner, null, code, Now));
        return h;
    }

    private IServiceProvider SiloServices => Cluster.GetSiloServiceProvider(Cluster.Silos[0].SiloAddress);

    private ChatInboundMessage Dm(string text, string sender = Owner, string chat = Owner, IReadOnlyList<ChatInboundFile>? files = null)
        => new(NextId(), chat, ChatType.Direct, null, sender, null, false, text, false, false, files ?? [], Now);

    private ChatInboundMessage Group(string text, bool mentions, string sender = Owner)
        => new(NextId(), "-1001234567890", ChatType.Group, null, sender, null, false, text, mentions, false, [], Now);

    private string NextId() => Interlocked.Increment(ref _messageId).ToString();

    private async Task<(string Id, string UserId)> NewAgentAsync(Harness? h, string name)
    {
        var userId = h?.UserId ?? "pipe-user-" + Guid.NewGuid().ToString("N");
        var saved = await Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(userId).SaveAsync(new AgentDefinition(
            "", name, "", TestSiloConfigurator.DefaultModel, "You are " + name, Now, Now));
        return (saved.Agent.Id, userId);
    }

    private static async Task<string> FinalReplyAsync(Harness h, string contains)
    {
        string? reply = null;
        await Until(() => (reply = h.Session.VisibleMessages().LastOrDefault(m => m.Contains(contains, StringComparison.Ordinal)
            && !m.EndsWith(" ▍", StringComparison.Ordinal))) is not null);
        return reply!;
    }

    private List<FakeChatClient.StreamGate> OpenGates(int afterChunks)
        => Cluster.Silos.Select(s => Cluster.GetSiloServiceProvider(s.SiloAddress).GetRequiredService<FakeChatClient>()
            .OpenStreamGate(afterChunks)).ToList();

    private void CloseGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>().CloseStreamGate();
        }
    }

    private int SuggestionCalls()
        => Cluster.Silos.Sum(s => Cluster.GetSiloServiceProvider(s.SiloAddress).GetRequiredService<FakeChatClient>().SuggestionCalls);

    private IUserChatConnectionsGrain Store(Harness h) => Cluster.GrainFactory.GetGrain<IUserChatConnectionsGrain>(h.UserId);

    private IChatRouteGrain Route(Harness h, string chat = Owner)
        => Cluster.GrainFactory.GetGrain<IChatRouteGrain>(ChatRouteKey.GrainKey(h.UserId, h.ConnectionId, ChatRouteKey.For(chat, null)));

    private IConversationIndexGrain Index(Harness h) => Cluster.GrainFactory.GetGrain<IConversationIndexGrain>(h.UserId);

    private IAgentGrain Agent(Harness h, string conversationId) => Cluster.GrainFactory.GetGrain<IAgentGrain>($"{h.UserId}/{conversationId}");

    private static Task Until(Func<bool> condition) => ChatGatewayServiceTests.Until(condition);

    private static Task Until(Func<Task<bool>> condition) => ChatGatewayServiceTests.Until(condition);
}
