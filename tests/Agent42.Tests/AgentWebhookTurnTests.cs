using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// Where the outbound webhook fires (ADR 0094): every completed, persisted turn of a bound conversation, at the three
/// run sites, and nowhere else. The delivery is <c>[OneWay]</c>, so a positive assertion polls; a "never" assertion
/// runs a SENTINEL turn on a second webhook afterwards and waits for that one, so it never rests on a fixed sleep.
///
/// <para>Every test owns a userId and a webhook URL, and every assertion filters by the URL: the cluster lives for
/// the class, and a OneWay delivery of an earlier test can still be landing.</para>
/// </summary>
public sealed class AgentWebhookTurnTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private const string AgentId = "agent-wh";
    private const string AgentName = "Hooky";
    private const string ReplyHead = "Hello!";
    private const string ReplyTail = "help you today?";
    private const string LaterIterationMarker = "[loopfeedback:1]";

    // AC1: a bound interactive turn delivers once, with the agent, the conversation, the trigger, prompt and reply.
    [Fact]
    public async Task InteractiveTurn_BoundAgent_DeliversOnce()
    {
        var (agent, url, user) = await BoundAsync();

        await CollectAsync(agent.ChatAsync("hi", null));

        var evt = Assert.Single(await PollEventsAsync(url, 1));
        Assert.Equal(AgentWebhookEvent.TurnCompleted, evt.Type);
        Assert.Equal((AgentId, AgentName, "conv"), (evt.AgentId, evt.AgentName, evt.ConversationId));
        Assert.Equal(AgentWebhookEvent.Triggers.Interactive, evt.Trigger);
        Assert.Equal("hi", evt.Prompt);
        Assert.Contains(ReplyTail, evt.Reply, StringComparison.Ordinal);
        Assert.False(evt.Stopped || evt.ReplacesLastTurn || evt.PromptTruncated || evt.ReplyTruncated);
        Assert.Equal(0, evt.ImageCount);
        Assert.Null(evt.ReminderName);

        await SentinelAsync(user);
        Assert.Single(EventsTo(url));
    }

    // AC2: an ask_agent-style drain (interactive: false) is a background turn.
    [Fact]
    public async Task NonInteractiveChat_DeliversBackgroundTrigger()
    {
        var (agent, url, _) = await BoundAsync();

        await CollectAsync(agent.ChatAsync("hi", null, interactive: false));

        Assert.Equal(AgentWebhookEvent.Triggers.Background, Assert.Single(await PollEventsAsync(url, 1)).Trigger);
    }

    // AC2: an agent-run turn is a background turn.
    [Fact]
    public async Task EnqueuePrompt_DeliversBackgroundTrigger()
    {
        var (agent, url, _) = await BoundAsync();

        await agent.EnqueuePromptAsync("hi", null);

        var evt = Assert.Single(await PollEventsAsync(url, 1));
        Assert.Equal((AgentWebhookEvent.Triggers.Background, "hi"), (evt.Trigger, evt.Prompt));
    }

    // AC3: a fired reminder delivers its prompt, its name and the reply the conversation now ends with.
    [Fact]
    public async Task ReceiveReminder_DeliversReminderTrigger()
    {
        var (agent, url, _) = await BoundAsync();
        await CollectAsync(agent.ChatAsync("please remind me to drink water", null));

        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        var evt = (await PollEventsAsync(url, 2)).Single(e => e.Trigger == AgentWebhookEvent.Triggers.Reminder);
        Assert.Equal(("Tell the user to drink some water.", "drink-water"), (evt.Prompt, evt.ReminderName));
        var history = await agent.GetHistoryAsync();
        Assert.False(string.IsNullOrWhiteSpace(evt.Reply));
        Assert.Equal(history[^1].Text, evt.Reply);
    }

    // AC3: the reminder fires before the ADR 0075 push early return, so a turn whose model pushed still delivers.
    [Fact]
    public async Task ReceiveReminder_AfterPush_StillDelivers()
    {
        var (agent, url, _) = await BoundAsync();
        await CollectAsync(agent.ChatAsync("schedule a notification", null));

        await agent.AsReference<IRemindable>().ReceiveReminder("notify-me", default);

        var events = await PollEventsAsync(url, 2);
        Assert.Contains(events, e => e.Trigger == AgentWebhookEvent.Triggers.Reminder && e.ReminderName == "notify-me");
    }

    // AC3: a research reminder runs several loop iterations; the event carries the LAST assistant message, not the
    // concatenation of every iteration.
    [Fact]
    public async Task ResearchReminder_DeliversLastAssistantText()
    {
        var (agent, url, _) = await BoundAsync(
            kind: AgentKinds.Research, systemPrompt: ResearchAgentDefaults.SystemPrompt,
            tools: [ToolCatalog.WebSearch, ToolCatalog.Scheduling]);
        await CollectAsync(agent.ChatAsync(FakeChatClient.ResearchTrigger, null));
        await CollectAsync(agent.ChatAsync(FakeChatClient.ScheduleResearchTrigger, null));

        await agent.AsReference<IRemindable>().ReceiveReminder("research-again", default);

        var evt = (await PollEventsAsync(url, 3)).Single(e => e.Trigger == AgentWebhookEvent.Triggers.Reminder);
        Assert.StartsWith(LaterIterationMarker, evt.Reply, StringComparison.Ordinal);
        Assert.Equal(1, CountOf(evt.Reply!, LaterIterationMarker));
    }

    // AC4: an incognito turn never delivers, and the next normal turn on the same activation does.
    [Fact]
    public async Task EphemeralTurn_NeverDelivers_NextTurnDoes()
    {
        var (agent, url, _) = await BoundAsync();

        await CollectAsync(agent.ChatAsync("a secret", null, ephemeral: true));
        await CollectAsync(agent.ChatAsync("hi", null));

        Assert.Equal("hi", Assert.Single(await PollEventsAsync(url, 1)).Prompt);
    }

    // ADR 0134: an incognito agent chat (ephemeral bind + ephemeral turn) fires no webhook, because delivery is gated
    // on the persisted flag. The sentinel proves a delivery would have landed by now.
    [Fact]
    public async Task IncognitoAgentChat_DoesNotFireWebhook()
    {
        var user = NewUser();
        await Webhook(user, AgentId).SaveAsync(UrlFor(user), "secret");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/incognito");
        await agent.BindAgentAsync(
            new AgentBinding(AgentId, AgentName, "You are Hooky.", TestSiloConfigurator.DefaultModel), ephemeral: true);

        await CollectAsync(agent.ChatAsync("a secret", null, ephemeral: true));

        await SentinelAsync(user);
        Assert.Empty(EventsTo(UrlFor(user)));
    }

    // AC5: an unbound chat never activates a webhook, even for a user whose agent has one.
    [Fact]
    public async Task UnboundChat_NeverDelivers()
    {
        var (_, url, user) = await BoundAsync();
        var unbound = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/plain");

        await CollectAsync(unbound.ChatAsync("hi", null));

        await SentinelAsync(user);
        Assert.Empty(EventsTo(url));
    }

    // AC5: a faulted turn never reaches the persist, so it never delivers.
    [Fact]
    public async Task FaultedTurn_NeverDelivers()
    {
        var (agent, url, user) = await BoundAsync();

        await Assert.ThrowsAnyAsync<Exception>(
            () => CollectAsync(agent.ChatAsync($"hi, {FakeChatClient.FaultingStreamMarker}", null)));

        await SentinelAsync(user);
        Assert.Empty(EventsTo(url));
    }

    // AC5: the no-model path yields its notice before the persist, so it never delivers.
    [Fact]
    public async Task NoModelTurn_NeverDelivers()
    {
        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<NoModelSiloConfigurator>();
        await using var cluster = builder.Build();
        await cluster.DeployAsync();
        const string url = "https://hooks.example.com/no-model";
        await cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor("nm-user", AgentId))
            .SaveAsync(url, "secret");
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>("nm-user/conv");
        await agent.BindAgentAsync(new AgentBinding(AgentId, AgentName, "You are Hooky.", ""));

        var events = await CollectAsync(agent.ChatAsync("hi", null));

        Assert.Contains(events, e => e.Text?.Contains("No model provider is configured", StringComparison.Ordinal) == true);
        // No sentinel is possible here (no turn can complete), so this one waits: the OneWay hop is milliseconds.
        await Task.Delay(1_000);
        var recorder = cluster.GetSiloServiceProvider(cluster.Primary.SiloAddress).GetRequiredService<RecordingAgentWebhookSender>();
        Assert.DoesNotContain(recorder.Sends, s => s.Target.Url == url);
    }

    // AC6: a stopped turn persists, so it delivers — with the flag and the partial reply.
    [Fact]
    public async Task StoppedTurn_DeliversWithStoppedFlag()
    {
        var (agent, url, _) = await BoundAsync();
        var gates = OpenStreamGates();

        var turn = CollectAsync(agent.ChatAsync("tell me a long story", null));
        await Task.WhenAny(gates.Select(g => g.Reached.Task));
        await agent.StopAsync();
        CloseStreamGates();
        await turn;

        var evt = Assert.Single(await PollEventsAsync(url, 1));
        Assert.True(evt.Stopped);
        Assert.Contains(ReplyHead, evt.Reply, StringComparison.Ordinal);
        Assert.DoesNotContain(ReplyTail, evt.Reply, StringComparison.Ordinal);
    }

    // AC7: prompt and reply are cut at the caps, with the flags set. The fake echoes the system prompt in full, so a
    // 40 000-char persona makes the reply oversize.
    [Fact]
    public async Task OversizeReply_IsTruncatedWithFlag()
    {
        var (agent, url, _) = await BoundAsync(systemPrompt: new string('p', 40_000));

        await CollectAsync(agent.ChatAsync(new string('q', 5_000), null));

        var evt = Assert.Single(await PollEventsAsync(url, 1));
        Assert.Equal((WebhookLimits.MaxReplyChars, true), (evt.Reply!.Length, evt.ReplyTruncated));
        Assert.Equal((WebhookLimits.MaxPromptChars, true), (evt.Prompt!.Length, evt.PromptTruncated));
    }

    // AC8: an image turn delivers a count, an empty reply, and no URL anywhere in the event.
    [Fact]
    public async Task ImageTurn_DeliversCountWithoutUrls()
    {
        var (agent, url, _) = await BoundAsync();

        await CollectAsync(agent.ChatAsync("draw a cat", TestSiloConfigurator.ImageModel));

        var evt = Assert.Single(await PollEventsAsync(url, 1));
        Assert.Equal((1, "", "draw a cat"), (evt.ImageCount, evt.Reply, evt.Prompt));
        var json = JsonSerializer.Serialize(evt);
        Assert.DoesNotContain("http", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("blob", json, StringComparison.OrdinalIgnoreCase);
    }

    // AC9: deleting the agent clears its webhook, so the conversation it leaves behind no longer delivers.
    [Fact]
    public async Task OrphanedConversation_AfterAgentDelete_NeverDelivers()
    {
        var user = NewUser();
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var saved = await registry.SaveAsync(new AgentDefinition(
            "", AgentName, "", TestSiloConfigurator.DefaultModel, "You are Hooky.", default, default));
        var url = UrlFor(user);
        await Webhook(user, saved.Agent.Id).SaveAsync(url, "secret");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/conv");
        await agent.BindAgentAsync(new AgentBinding(saved.Agent.Id, AgentName, "You are Hooky.", TestSiloConfigurator.DefaultModel));

        await registry.DeleteAsync(saved.Agent.Id);
        await CollectAsync(agent.ChatAsync("hi", null));

        await SentinelAsync(user);
        Assert.Empty(EventsTo(url));
    }

    // AC21: a regenerate (truncate, then a chat that says it replaces) delivers once more, marked as replacing.
    [Fact]
    public async Task RegeneratedTurn_DeliversWithReplacesFlag()
    {
        var (agent, url, _) = await BoundAsync();
        await CollectAsync(agent.ChatAsync("say hello", null));
        await PollEventsAsync(url, 1);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        await CollectAsync(agent.ChatAsync("say hello", null, replacesLastTurn: true));

        var events = await PollEventsAsync(url, 2);
        Assert.Equal([false, true], events.Select(e => e.ReplacesLastTurn));
    }

    // AC21: the truncate itself is not a turn and delivers nothing — a following plain turn is the second event.
    [Fact]
    public async Task Truncate_AloneNeverDelivers()
    {
        var (agent, url, _) = await BoundAsync();
        await CollectAsync(agent.ChatAsync("first", null));
        await PollEventsAsync(url, 1);
        var (count, hash) = await TokenOf(agent);

        Assert.Equal(HistoryEditOutcome.Ok, await agent.TruncateLastTurnAsync(count, hash, ephemeral: false));
        await CollectAsync(agent.ChatAsync("second", null));

        var events = await PollEventsAsync(url, 2);
        Assert.Equal(["first", "second"], events.Select(e => e.Prompt));
        Assert.All(events, e => Assert.False(e.ReplacesLastTurn));
    }

    // AC23: a fork of a bound conversation is bound, and its turns deliver under the fork's conversation id.
    [Fact]
    public async Task ForkedConversation_TurnDeliversWithForkId()
    {
        var (agent, url, user) = await BoundAsync();
        await CollectAsync(agent.ChatAsync("first", null));
        await PollEventsAsync(url, 1);
        var (count, hash) = await TokenOf(agent);

        var result = await agent.CreateForkAsync(0, count, hash);
        Assert.Equal(HistoryEditOutcome.Ok, result.Outcome);
        var fork = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/fork");
        Assert.True(await fork.InitializeFromForkAsync(result.Fork!));
        await CollectAsync(fork.ChatAsync("on the fork", null));

        var events = await PollEventsAsync(url, 2);
        Assert.Equal(["conv", "fork"], events.Select(e => e.ConversationId));
    }

    // ---- helpers --------------------------------------------------------------------------------------------

    private static string NewUser() => $"wht-{Guid.NewGuid():N}";

    private static string UrlFor(string user, string suffix = "") => $"https://hooks.example.com/{user}{suffix}";

    private IAgentWebhookGrain Webhook(string user, string agentId)
        => Cluster.GrainFactory.GetGrain<IAgentWebhookGrain>(AgentWebhookKeys.KeyFor(user, agentId));

    // A fresh user, its agent's webhook configured, and a conversation bound to that agent.
    private async Task<(IAgentGrain Agent, string Url, string User)> BoundAsync(
        string? kind = null, string systemPrompt = "You are Hooky.", IReadOnlyList<string>? tools = null)
    {
        var user = NewUser();
        var url = UrlFor(user);
        await Webhook(user, AgentId).SaveAsync(url, "secret");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/conv");
        await agent.BindAgentAsync(new AgentBinding(
            AgentId, AgentName, systemPrompt, TestSiloConfigurator.DefaultModel, tools, kind));
        return (agent, url, user);
    }

    // A turn on a SECOND agent of the same user with its own webhook, awaited until it lands. Deliveries go through
    // the same OneWay hop, so once the sentinel has arrived, a delivery the turn under test had made would have too.
    private async Task SentinelAsync(string user)
    {
        const string sentinelAgent = "agent-sentinel";
        var url = UrlFor(user, "/sentinel");
        await Webhook(user, sentinelAgent).SaveAsync(url, "secret");
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>($"{user}/sentinel");
        await agent.BindAgentAsync(new AgentBinding(sentinelAgent, "Sentinel", "", TestSiloConfigurator.DefaultModel));
        await CollectAsync(agent.ChatAsync("hi", null));
        await PollEventsAsync(url, 1);
        await Task.Delay(100);
    }

    private IEnumerable<RecordingAgentWebhookSender> Recorders()
        => Cluster.Silos.Select(s => Cluster.GetSiloServiceProvider(s.SiloAddress)
            .GetRequiredService<RecordingAgentWebhookSender>());

    private List<AgentWebhookEvent> EventsTo(string url)
        => [.. Recorders().SelectMany(r => r.Sends).Where(s => s.Target.Url == url).Select(s => s.Event)
            .OrderBy(e => e.OccurredAt)];

    private async Task<List<AgentWebhookEvent>> PollEventsAsync(string url, int atLeast)
    {
        await TestWait.WaitUntilAsync(() => EventsTo(url).Count >= atLeast, $"{atLeast} webhook event(s) to {url}");
        return EventsTo(url);
    }

    private List<FakeChatClient.StreamGate> OpenStreamGates() =>
        [.. Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress)
            .GetRequiredService<FakeChatClient>().OpenStreamGate(1))];

    private void CloseStreamGates()
    {
        foreach (var silo in Cluster.Silos)
        {
            Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeChatClient>().CloseStreamGate();
        }
    }

    private static async Task<(int Count, string Hash)> TokenOf(IAgentGrain agent)
    {
        var users = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").Select(t => t.Text).ToList();
        var last = users.Count == 0 ? "" : users[^1] ?? "";
        return (users.Count, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(last))));
    }

    private static int CountOf(string text, string part)
        => (text.Length - text.Replace(part, "", StringComparison.Ordinal).Length) / part.Length;

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAsyncEnumerable<AgentStreamEvent> stream)
    {
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in stream)
        {
            events.Add(evt);
        }

        return events;
    }
}
