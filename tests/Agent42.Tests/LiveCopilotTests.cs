using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Core.Internal;
using Orleans.TestingHost;
using Xunit.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The test's handle on the silo's REAL Copilot factory, so a test can restart the runtime: dispose the factory (which
/// ends the runtime process) and put a new one in its place over the SAME session store. Test-only. Production has
/// exactly one factory per silo.
/// </summary>
public sealed class SwappableCopilotAgentFactory : ICopilotAgentFactory
{
    public static readonly InMemoryCopilotSessionStore Store = new();

    public static CopilotAgentFactory Current { get; private set; } = NewFactory();

    public static async Task RestartRuntimeAsync()
    {
        var old = Current;
        Current = NewFactory();
        await old.DisposeAsync();
    }

    public AIAgent CreateAgent(CopilotAgentRequest request) => Current.CreateAgent(request);

    public Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
        => Current.DeleteConversationAsync(userId, conversationId, cancellationToken);

    private static CopilotAgentFactory NewFactory()
        => new(Store, new CopilotRuntimeOptions(), NullLoggerFactory.Instance);
}

/// <summary>The live silo plus the real Copilot factory (behind the swappable handle).</summary>
public sealed class LiveCopilotSiloConfigurator : ISiloConfigurator
{
    public void Configure(ISiloBuilder siloBuilder)
    {
        new LiveModelSiloConfigurator().Configure(siloBuilder);
        siloBuilder.ConfigureServices(services =>
            services.AddSingleton<ICopilotAgentFactory, SwappableCopilotAgentFactory>());
    }
}

/// <summary>
/// The copilot agent kind against a REAL Copilot runtime and a real Foundry model. It is opt-in, like
/// <see cref="LiveModelChatTests"/>: it costs tokens and needs network and a key. The runtime binary comes from the
/// SDK's build targets in this project's output. Cluster-per-test, because it brings its own configurator
/// (ADR 0055's exception).
/// </summary>
public sealed class LiveCopilotTests(ITestOutputHelper output)
{
    private static readonly string Model = ModelId.Qualify(LiveModelSettings.Provider, LiveModelSettings.Model);

    // AC14: a real copilot turn calls a GRAIN tool (web_search, marshalled onto the activation), and after the runtime
    // is restarted, with nothing on local disk (SessionFs is the only state), the next turn resumes the same
    // transcript: the model still knows what turn 1 told it.
    [LiveModelFact]
    public async Task LiveCopilot_ToolTurn_ResumesFromSessionFs()
    {
        await using var cluster = await DeployAsync();
        var agent = await BindAsync(cluster, $"live-copilot-{Guid.NewGuid():n}", [ToolCatalog.WebSearch]);

        var first = await CollectAsync(agent.ChatAsync(
            "Remember the code word PINEAPPLE-42. Then use web_search to look up 'orleans grains' and tell me the "
            + "title of the first result in one sentence.", null));
        Log("turn 1", first);
        Assert.Contains(first, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == "web_search");
        Assert.Equal(AgentStreamEventType.Done, first[^1].Type);
        Assert.NotNull(first[^1].InputTokens);

        await SwappableCopilotAgentFactory.RestartRuntimeAsync();
        await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var second = await CollectAsync(agent.ChatAsync("What was the code word? Answer with just the word.", null));
        Log("turn 2", second);
        Assert.Contains("PINEAPPLE-42", Reply(second), StringComparison.OrdinalIgnoreCase);

        var history = await agent.GetHistoryAsync();
        Assert.Equal(["user", "assistant", "user", "assistant"], history.Select(t => t.Role));
        Assert.Contains(history[1].ToolCalls!, c => c.ToolName == "web_search");
    }

    // AC25: a selected Agent42 skill is loaded natively by the runtime (its `skill` tool), with no file tool.
    [LiveModelFact]
    public async Task LiveCopilot_LoadsSelectedSkill()
    {
        await using var cluster = await DeployAsync();
        var agent = await BindAsync(cluster, $"live-copilot-skill-{Guid.NewGuid():n}",
            [ToolCatalog.WebSearch, ToolCatalog.SkillKey("websearch")]);

        var events = await CollectAsync(agent.ChatAsync(
            "Load your websearch skill with the skill tool, then tell me in one sentence when it says to fetch a page "
            + "after searching.", null));
        Log("skill", events);

        Assert.Contains(events, e => e.Type == AgentStreamEventType.ToolCallStart && e.ToolName == CopilotLimits.SkillToolName);
        Assert.False(string.IsNullOrWhiteSpace(Reply(events)));
    }

    private static async Task<TestCluster> DeployAsync()
    {
        var builder = new TestClusterBuilder { Options = { InitialSilosCount = 1 } };
        builder.AddSiloBuilderConfigurator<LiveCopilotSiloConfigurator>();
        var cluster = builder.Build();
        await cluster.DeployAsync();
        return cluster;
    }

    private static async Task<IAgentGrain> BindAsync(TestCluster cluster, string key, IReadOnlyList<string> tools)
    {
        var agent = cluster.GrainFactory.GetGrain<IAgentGrain>(key);
        await agent.BindAgentAsync(new AgentBinding("live-copilot", "Pilot", "You are a concise assistant.", Model, tools, AgentKinds.Copilot));
        return agent;
    }

    private void Log(string label, IEnumerable<AgentStreamEvent> events)
    {
        output.WriteLine($"{label}: {Reply(events)}");
        foreach (var e in events.Where(e => e.Type != AgentStreamEventType.TextDelta))
        {
            output.WriteLine($"  {e.Type} {e.ToolName} {e.ToolInput}");
        }
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
