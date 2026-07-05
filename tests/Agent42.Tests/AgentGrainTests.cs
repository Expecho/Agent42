using Agent42.Agents;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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

        var chunks = new List<string>();
        await foreach (var chunk in agent.ChatAsync("hi"))
        {
            chunks.Add(chunk);
        }

        var answer = string.Concat(chunks);
        Assert.False(string.IsNullOrWhiteSpace(answer), "The agent should answer when it receives \"hi\".");
    }

    [Fact]
    public async Task History_ContainsUserAndAssistantTurns_SoAConversationCanBeResumed()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("history-conversation");

        await foreach (var _ in agent.ChatAsync("hi"))
        {
        }

        var history = await agent.GetHistoryAsync();

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

        await foreach (var _ in agent.ChatAsync("please remind me to drink water"))
        {
        }

        var reminders = await agent.GetRemindersAsync();

        var reminder = Assert.Single(reminders);
        Assert.Equal("drink-water", reminder.Name);
        Assert.Equal("Tell the user to drink some water.", reminder.Prompt);
        Assert.True(reminder.DueInMinutes >= 1 && reminder.RepeatEveryMinutes >= 1);
    }

    [Fact]
    public async Task WhenAReminderFires_ItsPromptIsSentToTheAgent()
    {
        var agent = _cluster.GrainFactory.GetGrain<IAgentGrain>("reminder-firing-conversation");

        await foreach (var _ in agent.ChatAsync("please remind me to drink water"))
        {
        }

        // Fire the reminder the way the Orleans runtime would.
        await agent.AsReference<IRemindable>().ReceiveReminder("drink-water", default);

        var history = await agent.GetHistoryAsync();
        Assert.Contains(history, t => t.Role == "user" && t.Text == "Tell the user to drink some water.");
        Assert.Equal("assistant", history[^1].Role);
        Assert.False(string.IsNullOrWhiteSpace(history[^1].Text));

        var reminder = Assert.Single(await agent.GetRemindersAsync());
        Assert.Equal(1, reminder.TimesFired);
    }

    private sealed class TestSiloConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            // In-memory stand-ins for the Cosmos DB grain storage and reminder service
            // used in production.
            siloBuilder.AddMemoryGrainStorage(AgentGrain.StorageProviderName);
            siloBuilder.UseInMemoryReminderService();
            siloBuilder.ConfigureServices(services => services.AddSingleton<IChatClient, FakeChatClient>());
        }
    }
}
