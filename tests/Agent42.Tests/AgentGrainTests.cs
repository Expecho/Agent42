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
}
