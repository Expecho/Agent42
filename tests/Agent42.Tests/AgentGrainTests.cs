using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Core.Internal;
using Orleans.TestingHost;

namespace Agent42.Tests;

public sealed class AgentGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.DisposeAsync();

    [Fact]
    public async Task SendingHi_ReturnsAnAnswer()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("test-conversation");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("hi", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.False(string.IsNullOrWhiteSpace(answer), "The agent should answer when it receives \"hi\".");
        Assert.Equal(AgentStreamEventType.Done, events[^1].Type);
    }

    [Fact]
    public async Task History_ContainsUserAndAssistantTurns_SoAConversationCanBeResumed()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("history-conversation");

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        var history = await agent.GetHistoryAsync();

        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    [Fact]
    public async Task History_SurvivesGrainReactivation()
    {
        const string key = "reactivation-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        // Force the grain to deactivate, so reopening the conversation goes through
        // OnActivateAsync → DeserializeSessionAsync — the path a user hits when they
        // reselect an older conversation whose grain has idled out. The persisted
        // session must round-trip the visible history.
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var history = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();

        Assert.Contains(history, t => t.Role == "user" && t.Text == "hi");
        Assert.Contains(history, t => t.Role == "assistant" && !string.IsNullOrWhiteSpace(t.Text));
    }

    [Fact]
    public async Task ConversationIndex_ListsTouchedConversations_MostRecentFirst()
    {
        var index = _cluster.GrainFactory.GetGrain<IConversationIndexGrain>("test-user");

        await index.TouchAsync("conv-1", "What is the answer to everything?");
        await index.TouchAsync("conv-2", "Plan a trip to Magrathea");
        await index.TouchAsync("conv-1", "follow-up");

        var conversations = await index.GetConversationsAsync();

        Assert.Equal(2, conversations.Count);
        Assert.Equal("conv-1", conversations[0].Id);
        Assert.Equal("What is the answer to everything?", conversations[0].Title);
        Assert.Equal("conv-2", conversations[1].Id);
    }

    [Fact]
    public async Task AskingToBeReminded_SchedulesAReminderViaTheTool()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-conversation");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please remind me to drink water", null))
        {
            events.Add(evt);
        }

        // The tool round-trip is surfaced to the client as start/end events.
        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("schedule_prompt", start.ToolName);
        Assert.Contains(events, e =>
            e.Type == AgentStreamEventType.ToolCallEnd && e.ToolCallId == start.ToolCallId);

        var reminders = await agent.GetScheduledPromptsAsync();

        var reminder = Assert.Single(reminders);
        Assert.Equal("drink-water", reminder.Name);
        Assert.Equal("Tell the user to drink some water.", reminder.Prompt);
        Assert.True(reminder.DueInMinutes >= 1 && reminder.RepeatEveryMinutes >= 1);
    }

    [Fact]
    public async Task Purge_ClearsHistoryAndUnregistersReminders()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("purge-conversation");

        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Precondition: the conversation has both history and a durable reminder.
        Assert.NotEmpty(await agent.GetHistoryAsync());
        Assert.Single(await agent.GetScheduledPromptsAsync());

        // Purge iterates the grain's real registered reminders and unregisters each, so
        // this call exercises the UnregisterReminder path against a live reminder.
        await agent.PurgeAsync();

        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>("purge-conversation");
        Assert.Empty(await reactivated.GetHistoryAsync());
        Assert.Empty(await reactivated.GetScheduledPromptsAsync());
    }

    [Fact]
    public async Task ConversationIndex_RemoveAsync_DropsTheConversation()
    {
        var index = _cluster.GrainFactory.GetGrain<IConversationIndexGrain>("remove-user");

        await index.TouchAsync("conv-1", "What is the answer to everything?");
        await index.TouchAsync("conv-2", "Plan a trip to Magrathea");

        await index.RemoveAsync("conv-1");

        var conversations = await index.GetConversationsAsync();
        Assert.Equal("conv-2", Assert.Single(conversations).Id);
    }

    [Fact]
    public async Task WhenAReminderFires_ItsPromptIsSentToTheAgent()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-firing-conversation");

        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Fire the reminder the way the Orleans runtime would.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "Tell the user to drink some water.");
        Assert.Equal("assistant", history[^1].Role);
        Assert.False(string.IsNullOrWhiteSpace(history[^1].Text));

        var reminder = Assert.Single(await agent.GetScheduledPromptsAsync());
        Assert.Equal(1, reminder.TimesFired);
    }

    [Fact]
    public async Task AskingToCancel_RemovesTheReminderViaTheTool()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-conversation");

        // Schedule a real, durable reminder first, so cancelling exercises the live
        // GetReminder / UnregisterReminder path — not just a state-dict edit (cf. the
        // Purge test, which can only assert the state, since GetReminders isn't on the grain).
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        Assert.Single(await agent.GetScheduledPromptsAsync());

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel the drink water reminder", null))
        {
            events.Add(evt);
        }

        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("cancel_scheduled_prompt", start.ToolName);
        Assert.Contains(events, e =>
            e.Type == AgentStreamEventType.ToolCallEnd && e.ToolCallId == start.ToolCallId);

        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    [Fact]
    public async Task AskingToCancelAll_RemovesEveryReminder()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-all-conversation");

        // Schedule a real reminder so cancel-all iterates and unregisters a live reminder.
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        Assert.Single(await agent.GetScheduledPromptsAsync());

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel all reminders", null))
        {
            events.Add(evt);
        }

        var start = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallStart);
        Assert.Equal("cancel_all_scheduled_prompts", start.ToolName);
        Assert.Empty(await agent.GetScheduledPromptsAsync());

        // Cancelling again with nothing scheduled hits the empty-state branch and must
        // still return cleanly, leaving the (already empty) list untouched.
        await foreach (var _ in agent.ChatAsync("please cancel all reminders", null))
        {
        }

        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    // AC2: binding pins the persona + model into the conversation and survives reactivation.
    [Fact]
    public async Task Binding_PinsPersonaAndModel_SurvivesReactivation()
    {
        const string key = "binding-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);

        await agent.BindAgentAsync(new AgentBinding("agent-1", "Marvin", "You are Marvin.", "persona-model"));

        var binding = await agent.GetAgentBindingAsync();
        Assert.NotNull(binding);
        Assert.Equal("agent-1", binding!.AgentId);
        Assert.Equal("Marvin", binding.Name);
        Assert.Equal("You are Marvin.", binding.SystemPrompt);
        Assert.Equal("persona-model", binding.Model);
        // The bound model becomes the conversation's model — the picker is hidden, so it never changes.
        Assert.Equal("persona-model", await agent.GetSelectedModelAsync());

        // Force deactivation so the binding is rehydrated from persisted state (Cosmos in prod).
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        Assert.Equal(binding, await reactivated.GetAgentBindingAsync());
        Assert.Equal("persona-model", await reactivated.GetSelectedModelAsync());
    }

    // AC3: the bound system prompt actually reaches the model (FakeChatClient echoes what the
    // terminal IChatClient receives as a [sys:…] marker — this is a real delivery assertion).
    [Fact]
    public async Task AgentChat_AppliesBoundSystemPrompt()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("agent-chat-conversation");
        const string persona = "You are Zaphod, always speak in exclamations!";

        await agent.BindAgentAsync(new AgentBinding("agent-z", "Zaphod", persona, "persona-model"));

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("hi", null))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        // The fake echoes the instructions the terminal client received as [sys:…]. The
        // Agent Framework skills provider appends its catalog to those instructions, so
        // assert the persona is present in the echoed system prompt rather than an exact
        // bracketed match — this still proves the bound prompt was delivered to the model.
        Assert.Contains("[sys:", answer);
        Assert.Contains(persona, answer);
    }

    // AC4: binding is immutable once set — a second bind can't flip the persona mid-conversation.
    [Fact]
    public async Task BindAgent_IsIdempotent_KeepsFirstPersona()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("idempotent-binding-conversation");

        await agent.BindAgentAsync(new AgentBinding("agent-1", "First", "First prompt.", "model-a"));
        await agent.BindAgentAsync(new AgentBinding("agent-2", "Second", "Second prompt.", "model-b"));

        var binding = await agent.GetAgentBindingAsync();
        Assert.NotNull(binding);
        Assert.Equal("agent-1", binding!.AgentId);
        Assert.Equal("First", binding.Name);
        Assert.Equal("First prompt.", binding.SystemPrompt);
        Assert.Equal("model-a", binding.Model);
        Assert.Equal("model-a", await agent.GetSelectedModelAsync());
    }

    // A normal chat (no bind) reports no persona — the default Agent 42 persona is implicit.
    [Fact]
    public async Task NormalChat_HasNoBinding()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("unbound-conversation");

        await foreach (var _ in agent.ChatAsync("hi", null))
        {
        }

        Assert.Null(await agent.GetAgentBindingAsync());
    }

    [Fact]
    public async Task CancellingAnUnknownReminder_ReportsItGracefully()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("cancel-unknown-conversation");

        // Nothing was ever scheduled, so the cancel tool hits its "no such reminder" branch.
        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("please cancel the drink water reminder", null))
        {
            events.Add(evt);
        }

        var end = Assert.Single(events, e => e.Type == AgentStreamEventType.ToolCallEnd);
        Assert.Contains("no scheduled reminder", end.ToolResult ?? "", StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await agent.GetScheduledPromptsAsync());
    }

    // AC3: a turn carrying an attachment actually delivers an image content part to the model —
    // the fake echoes [img:N] built from the non-text parts it received, so this proves delivery,
    // not just that the ref was stored.
    [Fact]
    public async Task Chat_WithAttachment_DeliversImagePartToModel()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("attachment-delivery-conversation");
        var reference = new AttachmentRef("att-1", "k/att-1", "image/png", "pic.png");

        var events = new List<AgentStreamEvent>();
        await foreach (var evt in agent.ChatAsync("what is in this picture", null, [reference]))
        {
            events.Add(evt);
        }

        var answer = string.Concat(events
            .Where(e => e.Type == AgentStreamEventType.TextDelta)
            .Select(e => e.Text));
        Assert.Contains("[img:1]", answer);
    }

    // AC4c: an image-only user turn (blank text, image attached) is kept in history — the old
    // filter dropped every blank-text message, which would hide such a turn on reload.
    [Fact]
    public async Task History_KeepsImageOnlyTurn()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("image-only-conversation");
        var reference = new AttachmentRef("only-1", "k/only-1", "image/png", "pic.png");

        await foreach (var _ in agent.ChatAsync("", null, [reference]))
        {
        }

        var history = await agent.GetHistoryAsync();
        var userTurn = Assert.Single(history, t => t.Role == "user");
        Assert.NotNull(userTurn.Attachments);
        var att = Assert.Single(userTurn.Attachments!);
        Assert.Equal("only-1", att.Id);
    }

    // AC4b: refs stay joined to the correct turn across a text → schedule → reminder-fired → image
    // sequence. The reminder-fired turn is a user turn too, so it must push an empty entry or the
    // index join drifts and the image lands on the wrong turn (constraint 11).
    [Fact]
    public async Task History_KeepsAttachmentRefsAlignedAcrossTurns()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("aligned-conversation");

        // Turn 1: plain text.
        await foreach (var _ in agent.ChatAsync("first message", null))
        {
        }

        // Turn 2: schedules a reminder (tool call), still a plain user turn.
        await foreach (var _ in agent.ChatAsync("please remind me to drink water", null))
        {
        }

        // Turn 3: the reminder fires — a user turn injected by the runtime, no attachments.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        // Turn 4: the image turn.
        var reference = new AttachmentRef("img-4", "k/img-4", "image/png", "p.png");
        await foreach (var _ in agent.ChatAsync("look at this", null, [reference]))
        {
        }

        var userTurns = (await agent.GetHistoryAsync()).Where(t => t.Role == "user").ToList();

        // The image ref is on the "look at this" turn and nowhere else.
        var imageTurn = Assert.Single(userTurns, t => t.Text == "look at this");
        Assert.NotNull(imageTurn.Attachments);
        Assert.Equal("img-4", Assert.Single(imageTurn.Attachments!).Id);
        Assert.All(userTurns.Where(t => t.Text != "look at this"), t => Assert.Null(t.Attachments));
    }

    // Attachment refs survive a grain deactivation/reactivation — the join list is persisted
    // state, so a reopened conversation still re-renders its images.
    [Fact]
    public async Task History_AttachmentRefs_SurviveReactivation()
    {
        const string key = "attachment-reactivation-conversation";
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        var reference = new AttachmentRef("react-1", "k/react-1", "image/png", "pic.png");

        await foreach (var _ in agent.ChatAsync("keep this image", null, [reference]))
        {
        }

        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var history = await _cluster.GrainFactory.GetGrain<IAgentGrain>(key).GetHistoryAsync();
        var userTurn = Assert.Single(history, t => t.Role == "user" && t.Text == "keep this image");
        Assert.NotNull(userTurn.Attachments);
        Assert.Equal("react-1", Assert.Single(userTurn.Attachments!).Id);
    }
}
