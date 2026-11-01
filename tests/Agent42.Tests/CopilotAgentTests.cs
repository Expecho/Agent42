using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The copilot agent kind through a real grain, on the in-process twin (<see cref="ChatClientCopilotAgentFactory"/>
/// behind <see cref="RecordingCopilotAgentFactory"/>). The twin keeps its own transcript and calls tools off the
/// grain's scheduler, the two Copilot behaviours the grain must cope with. These tests prove the grain composes
/// the turn the way the copilot ADR says: the same tools and skills as an ordinary chat, no hosted tools, no
/// documents, no images, no incognito. They also prove the history mirror makes the rest of the grain work
/// unchanged.
/// </summary>
public sealed class CopilotAgentTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private IEnumerable<IServiceProvider> Silos
        => Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress));

    // The grain may activate on any silo, so a conversation's requests are read from all of them.
    private List<CopilotAgentRequest> RequestsFor(string key)
        => [.. Silos.SelectMany(s => s.GetRequiredService<RecordingCopilotAgentFactory>().RequestsFor(key))];

    private CopilotAgentRequest LastRequest(string key) => RequestsFor(key)[^1];

    private async Task<IAgentGrain> CopilotChatAsync(
        string key, string model = TestSiloConfigurator.DefaultModel, IReadOnlyList<string>? tools = null)
    {
        var agent = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.BindAgentAsync(new AgentBinding("copilot-agent", "Pilot", "You fly.", model, tools, AgentKinds.Copilot));
        return agent;
    }

    // AC3 (grain path): the kind is snapshotted at bind and survives reactivation, and the grain builds the
    // copilot composition from it.
    [Fact]
    public async Task Bind_CopilotKind_PersistsAndBuildsTheCopilotAgent()
    {
        var agent = await CopilotChatAsync("copilot-bind");
        Assert.NotEmpty(RequestsFor("copilot-bind"));

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reopened = Cluster.GrainFactory.GetGrain<IAgentGrain>("copilot-bind");

        Assert.Equal(AgentKinds.Copilot, (await reopened.GetAgentBindingAsync())!.Kind);
    }

    // ADR 0108: follow-up suggestions are a plain chat call on the conversation's model, which a GitHub Copilot model
    // refuses (CopilotOnlyChatClient). A copilot chat on one makes no such call; on a BYOK model it still does.
    [Fact]
    public async Task CopilotTurn_OnGitHubModel_MakesNoFollowUpCall()
    {
        int SuggestionCalls() => Silos.Sum(s => s.GetRequiredService<FakeChatClient>().SuggestionCalls);

        var byok = await CollectAsync((await CopilotChatAsync("copilot-followups-byok")).ChatAsync("hi", null));
        Assert.Contains(byok, e => e.Type == AgentStreamEventType.Suggestions);
        Assert.Equal(1, SuggestionCalls());

        var agent = await CopilotChatAsync("copilot-followups-github", model: "github-copilot:claude-sonnet-5");
        var events = await CollectAsync(agent.ChatAsync("hi", null));

        Assert.Equal("github-copilot:claude-sonnet-5", LastRequest("copilot-followups-github").ModelId);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.DoesNotContain(events, e => e.Type == AgentStreamEventType.Suggestions);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Equal(1, SuggestionCalls());
    }

    // AC4 + AC8: a turn streams its tool call and text through the ordinary event types and ends with Done. The twin
    // calls web_search from the thread pool, and the tool records the search URL into grain state. It could not do
    // that off the activation, so a working turn proves the marshalling end to end.
    [Fact]
    public async Task CopilotTurn_StreamsToolCallsAndText_ToolRunsOnTheGrain()
    {
        var agent = await CopilotChatAsync("copilot-stream");

        var events = await CollectAsync(agent.ChatAsync("look up the orleans docs", null));

        Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == "web_search");
        Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolCallEnd);
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // AC8: a tool that needs the grain's context (registering a durable reminder fails outside it) works when the
    // runtime invokes it from a foreign thread. Also the grain half of ADR 0106's AC5: the twin calls the tool with
    // no ExecutionContext flowed, so schedule_prompt would read the closed (ephemeral) context and refuse unless the
    // grain's Copilot run registry re-established this run's.
    [Fact]
    public async Task CopilotTool_InvokedFromForeignThread_RunsOnGrainScheduler()
    {
        var agent = await CopilotChatAsync("copilot-scheduler");

        await CollectAsync(agent.ChatAsync("remind me to stretch", null));

        Assert.Single(await agent.GetScheduledPromptsAsync());
    }

    // AC5: one assistant turn per user turn, carrying the turn's tool calls, and it survives reactivation.
    [Fact]
    public async Task CopilotTurn_HistoryMirrorsTurn_AcrossReactivation()
    {
        var agent = await CopilotChatAsync("copilot-history");
        await CollectAsync(agent.ChatAsync(FakeChatClient.NarratingCallTrigger + " orleans", null));
        await CollectAsync(agent.ChatAsync("thanks", null));

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var history = await Cluster.GrainFactory.GetGrain<IAgentGrain>("copilot-history").GetHistoryAsync();

        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(t => t.Role));
        Assert.Equal("web_search", Assert.Single(history[1].ToolCalls!).ToolName);
        Assert.StartsWith(FakeChatClient.NarratedCallText, history[1].Text, StringComparison.Ordinal);
    }

    // AC7 + rule 6: a scheduled prompt runs the copilot turn through RunAsync, mirrors it into history, and the
    // reminder path's own resets still run (a repeating reminder keeps pushing).
    [Fact]
    public async Task ReminderCopilotTurn_RunsMirrorsAndResets()
    {
        var agent = await CopilotChatAsync("copilot-reminder");
        await CollectAsync(agent.ChatAsync("remind me to stretch", null));
        var reminder = Assert.Single(await agent.GetScheduledPromptsAsync());

        await agent.AsReference<IRemindable>().ReceiveReminder(reminder.Name, default);
        await agent.AsReference<IRemindable>().ReceiveReminder(reminder.Name, default);

        var history = await agent.GetHistoryAsync();
        Assert.Equal(6, history.Count);
        Assert.Equal("assistant", history[^1].Role);
        Assert.Equal(2, Silos.Sum(s => s.GetRequiredService<RecordingPushNotificationSender>().Sends.Count));
    }

    // AC7b: a background (agent-run) copilot turn persists and pushes, which needs the core's per-turn resets.
    [Fact]
    public async Task AgentRunCopilotTurn_PersistsAndResetsTurnFields()
    {
        var agent = await CopilotChatAsync("copilot-agentrun");

        await agent.EnqueuePromptAsync("hello there", null);
        await agent.EnqueuePromptAsync("and again", null);

        // OneWay returns before the grain runs the turn, so poll for both persisted turns.
        Assert.Equal(4, (await PollHistoryAsync(agent, h => h.Count >= 4)).Count);
        Assert.Equal(2, Silos.Sum(s => s.GetRequiredService<RecordingPushNotificationSender>().Sends.Count));
    }

    private static async Task<IReadOnlyList<ChatTurn>> PollHistoryAsync(
        IAgentGrain agent, Func<IReadOnlyList<ChatTurn>, bool> until)
    {
        IReadOnlyList<ChatTurn> history = [];
        for (var attempt = 0; attempt < 50 && !until(history); attempt++)
        {
            history = await agent.GetHistoryAsync();
            if (!until(history))
            {
                await Task.Delay(100);
            }
        }

        return history;
    }

    // AC9: Copilot gets the tools selection grants, as functions only. A hosted-tools model still gets the SearXNG
    // shims rather than hosted tools, and a tool the user deselected is not offered.
    [Fact]
    public async Task CopilotTools_RespectSelection_AndDropHostedTools()
    {
        await CopilotChatAsync("copilot-tools-hosted", TestSiloConfigurator.HostedToolsModel);
        await CopilotChatAsync("copilot-tools-narrow", tools: [ToolCatalog.Scheduling]);

        var hosted = LastRequest("copilot-tools-hosted").Tools.Select(t => t.Name).ToList();
        Assert.Contains("web_search", hosted);
        Assert.Contains("fetch_web_page", hosted);
        Assert.DoesNotContain(ToolCatalog.CodeInterpreter, hosted);

        var narrow = LastRequest("copilot-tools-narrow").Tools.Select(t => t.Name).ToList();
        Assert.Contains("schedule_prompt", narrow);
        Assert.DoesNotContain("web_search", narrow);
        Assert.DoesNotContain("get_current_location", narrow);
    }

    // ADR 0089 AC9: a Copilot chat never gets the client tools or update_shared_state, even with every key selected:
    // they route through the grain's own run, which Copilot's RPC thread is not part of.
    [Fact]
    public async Task CopilotFunctions_ExcludeClientAndStateTools()
    {
        await CopilotChatAsync("copilot-client-tools", tools: ToolCatalog.AllSelectableToolKeys);

        var tools = LastRequest("copilot-client-tools").Tools.Select(t => t.Name).ToList();
        Assert.Contains("schedule_prompt", tools);
        // Named on its own as well (ADR 0093): a Copilot chat has no location, though its picker still shows the key.
        Assert.DoesNotContain(ClientToolLimits.GetCurrentLocation, tools);
        foreach (var excluded in ClientToolLimits.ToolNames.Append(SharedStateLimits.UpdateToolName))
        {
            Assert.DoesNotContain(excluded, tools);
        }
    }

    // ADR 0112 AC24: the past-chat tools reach a Copilot chat through SelectableTools with no exclusion. They need
    // no browser, document or run-only broker, only the index and a grain call on the activation's scheduler.
    [Fact]
    public async Task CopilotFunctions_IncludeThePastChatTools()
    {
        await CopilotChatAsync("copilot-past-chats", tools: ToolCatalog.AllSelectableToolKeys);

        var tools = LastRequest("copilot-past-chats").Tools.Select(t => t.Name).ToList();
        Assert.Contains(PastChatLimits.SearchToolName, tools);
        Assert.Contains(PastChatLimits.ReadToolName, tools);
    }

    // ADR 0115 AC11: the fleet tools reach a Copilot chat through SelectableTools with no exclusion. They need only the
    // launcher port and the run's TurnContext, which the Copilot path re-establishes (SchedulerBoundAIFunction).
    [Fact]
    public async Task CopilotFunctions_IncludeTheFleetTools()
    {
        await CopilotChatAsync("copilot-fleet", tools: ToolCatalog.AllSelectableToolKeys);

        var tools = LastRequest("copilot-fleet").Tools.Select(t => t.Name).ToList();
        Assert.Contains(FleetToolLimits.StartToolName, tools);
        Assert.Contains(FleetToolLimits.GetToolName, tools);
    }

    // AC10: a copilot agent on a no-tools model is a plain chat: no Copilot agent is built, and the turn still runs.
    [Fact]
    public async Task CopilotKind_NoToolsModel_IsPlainAgent()
    {
        var agent = await CopilotChatAsync("copilot-no-tools", TestSiloConfigurator.NoToolsNoVisionModel);

        var events = await CollectAsync(agent.ChatAsync("hello", null));

        Assert.Empty(RequestsFor("copilot-no-tools"));
        Assert.Contains(events, e => e.Type == AgentStreamEventType.TextDelta);
    }

    // AC17: an incognito turn is refused before anything runs or is written.
    [Fact]
    public async Task EphemeralCopilotTurn_IsRefused()
    {
        var agent = await CopilotChatAsync("copilot-incognito");

        var events = await CollectAsync(agent.ChatAsync("hello", null, ephemeral: true));

        Assert.Contains("Incognito", Reply(events), StringComparison.Ordinal);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
        Assert.Empty(await agent.GetHistoryAsync());
    }

    // AC18: a copilot chat takes no documents (the grain ignores a registration) and is never offered
    // read_web_document. Long-term memory, a context provider, is never consulted.
    [Fact]
    public async Task CopilotChat_RefusesDocuments_AndExcludesContextTools()
    {
        var agent = await CopilotChatAsync("copilot-docs");
        var builds = RequestsFor("copilot-docs").Count;

        await agent.RegisterDocumentAsync("manual.pdf");
        await CollectAsync(agent.ChatAsync("hello", null));

        Assert.Equal(builds, RequestsFor("copilot-docs").Count);
        Assert.DoesNotContain(LastRequest("copilot-docs").Tools, t => t.Name == ToolCatalog.ReadWebDocument);
        Assert.DoesNotContain(LastRequest("copilot-docs").Tools, t => t.Name == ToolCatalog.ReadDocument); // AC11 (ADR 0098)
        Assert.Empty(Silos.SelectMany(s => s.GetRequiredService<RecordingHindsightClient>().RecallsFor("copilot-docs")));
    }

    // AC19: an image is withheld from a copilot turn with the ordinary notice, even on a vision model.
    [Fact]
    public async Task CopilotTurn_WithholdsImages()
    {
        var agent = await CopilotChatAsync("copilot-image");

        var events = await CollectAsync(agent.ChatAsync(
            "what is in this picture", null, [new AttachmentRef("img-1", "k/img-1", "image/png", "pic.png")]));

        Assert.Contains("cannot see images", Reply(events), StringComparison.Ordinal);
        Assert.DoesNotContain("[img:1]", Reply(events), StringComparison.Ordinal);
    }

    // AC20: rebuilding the agent mid-chat (a tool-selection change) keeps the session: the mirror and the
    // runtime's own transcript both carry over, so the next turn continues the same conversation.
    [Fact]
    public async Task CopilotChat_Rebuild_KeepsTheSession()
    {
        var agent = await CopilotChatAsync("copilot-rebuild");
        await CollectAsync(agent.ChatAsync("hello", null));

        await agent.SetEnabledToolsAsync([ToolCatalog.Scheduling]);
        var events = await CollectAsync(agent.ChatAsync("again", null));

        Assert.Contains("schedule_prompt", LastRequest("copilot-rebuild").Tools.Select(t => t.Name));
        Assert.DoesNotContain(LastRequest("copilot-rebuild").Tools, t => t.Name == "web_search");
        Assert.Equal(4, (await agent.GetHistoryAsync()).Count);
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    // AC23: exactly the selected skills' folders go to Copilot's native loader, plus read_skill_resource for their
    // files. An empty selection hands over no folders and no read_skill_resource.
    [Fact]
    public async Task CopilotSkills_PassSelectedDirectories_Only()
    {
        await CopilotChatAsync("copilot-skills-one", tools: [ToolCatalog.Scheduling, ToolCatalog.SkillKey("websearch")]);
        await CopilotChatAsync("copilot-skills-none", tools: [ToolCatalog.Scheduling]);

        var one = LastRequest("copilot-skills-one");
        Assert.Equal("websearch", Path.GetFileName(Assert.Single(one.SkillDirectories)));
        Assert.Contains(one.Tools, t => t.Name == ReadSkillResourceTool.Name);

        var none = LastRequest("copilot-skills-none");
        Assert.Empty(none.SkillDirectories);
        Assert.DoesNotContain(none.Tools, t => t.Name == ReadSkillResourceTool.Name);
    }

    // The instructions are the persona plus rendering hints. A copilot chat has no documents, so no document
    // awareness is added.
    [Fact]
    public async Task CopilotAgent_GetsThePersonaAsInstructions()
    {
        await CopilotChatAsync("copilot-persona");

        var request = LastRequest("copilot-persona");
        Assert.StartsWith("You fly.", request.Instructions, StringComparison.Ordinal);
        Assert.Equal("Pilot", request.Name);
        Assert.Equal(TestSiloConfigurator.DefaultModel, request.ModelId);
    }

    // A copilot chat whose agent cannot be built refuses the turn with the factory's reason, writes nothing, keeps
    // its history readable, and skips its reminders.
    [Fact]
    public async Task CopilotChat_Unavailable_RefusesTurnAndKeepsHistory()
    {
        const string key = "copilot-unavailable";
        var agent = await CopilotChatAsync(key);
        await CollectAsync(agent.ChatAsync("remind me to stretch", null));
        var before = await agent.GetHistoryAsync();
        var reminder = Assert.Single(await agent.GetScheduledPromptsAsync());

        foreach (var silo in Silos)
        {
            silo.GetRequiredService<RecordingCopilotAgentFactory>().Refuse(key, "Your copilot provider is gone.");
        }

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reopened = Cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var events = await CollectAsync(reopened.ChatAsync("hello", null));
        await reopened.AsReference<IRemindable>().ReceiveReminder(reminder.Name, default);

        Assert.Equal("Your copilot provider is gone.", Reply(events));
        Assert.Equal(before.Select(t => t.Text), (await reopened.GetHistoryAsync()).Select(t => t.Text));
    }

    // AC13 (second half): deleting a copilot chat deletes the runtime's transcript too.
    [Fact]
    public async Task DeleteChat_RemovesCopilotSession()
    {
        var agent = await CopilotChatAsync("copilot-purge");
        await CollectAsync(agent.ChatAsync("hello", null));

        await agent.PurgeAsync();

        Assert.Contains("copilot-purge",
            Silos.SelectMany(s => s.GetRequiredService<RecordingCopilotAgentFactory>().Deleted));
    }

    private static string Reply(IEnumerable<AgentStreamEvent> events)
        => string.Concat(events.Where(e => e.Type == AgentStreamEventType.TextDelta).Select(e => e.Text));

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
