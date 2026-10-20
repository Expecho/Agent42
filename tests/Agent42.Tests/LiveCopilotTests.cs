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

    public static CopilotAgentFactory Current { get; private set; } = NewFactory(new CopilotRuntimeOptions());

    /// <summary>Restarts the runtime, optionally with other options (the built-in tools switch, the workspace root).</summary>
    public static async Task RestartRuntimeAsync(CopilotRuntimeOptions? options = null)
    {
        var old = Current;
        Current = NewFactory(options ?? new CopilotRuntimeOptions());
        await old.DisposeAsync();
    }

    public AIAgent CreateAgent(CopilotAgentRequest request) => Current.CreateAgent(request);

    public Task DeleteConversationAsync(string userId, string conversationId, CancellationToken cancellationToken = default)
        => Current.DeleteConversationAsync(userId, conversationId, cancellationToken);

    private static CopilotAgentFactory NewFactory(CopilotRuntimeOptions options)
        => new(Store, options, NullLoggerFactory.Instance);
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

    // ADR 0107 AC8: with built-in tools on, a file the runtime's file tool creates is what its shell reads, in the
    // conversation's own workspace on disk; the transcript stays in the store (nothing but the state path is stored);
    // and after a runtime restart the next turn resumes with the file and the memory of turn 1.
    [LiveModelFact]
    public async Task LiveCopilot_BuiltIns_ShellSeesFileToolWrite_AndResumes()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-copilot-ws-" + Guid.NewGuid().ToString("N"));
        await SwappableCopilotAgentFactory.RestartRuntimeAsync(new CopilotRuntimeOptions { WorkspaceRoot = root });
        try
        {
            await using var cluster = await DeployAsync();
            var agent = await BindAsync(cluster, $"live-copilot-ws-{Guid.NewGuid():n}", [ToolCatalog.WebSearch]);
            var token = $"FILE-{Guid.NewGuid():n}"[..13];

            var first = await CollectAsync(agent.ChatAsync(
                $"Remember the code word GUAVA-3. Use your file create tool to create note.txt containing exactly {token}. "
                + "Then use your shell to print the contents of note.txt and the current directory, and give me both.", null));
            Log("turn 1", first);
            Assert.Contains(token, Reply(first), StringComparison.Ordinal);
            var note = Assert.Single(Directory.GetFiles(root, "note.txt", SearchOption.AllDirectories));
            Assert.Equal(token, (await File.ReadAllTextAsync(note)).Trim());
            Assert.Contains(Path.GetDirectoryName(note)!, Reply(first).Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            Assert.All(await SwappableCopilotAgentFactory.Store.ListAsync(""),
                k => Assert.Contains("/state/", k, StringComparison.Ordinal));
            Assert.Empty(Directory.GetDirectories(root, "state", SearchOption.AllDirectories));

            await SwappableCopilotAgentFactory.RestartRuntimeAsync(new CopilotRuntimeOptions { WorkspaceRoot = root });
            await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

            var second = await CollectAsync(agent.ChatAsync(
                "Use your shell to print note.txt again, then tell me its contents and the code word from earlier.", null));
            Log("turn 2", second);
            Assert.Contains(token, Reply(second), StringComparison.Ordinal);
            Assert.Contains("GUAVA-3", Reply(second), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SwappableCopilotAgentFactory.RestartRuntimeAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    // ADR 0107 AC9: a chat created before built-in tools (Mode.Empty, cwd '/work' in its workspace.yaml) resumes with
    // them on, and its shell works in the conversation's new workspace. Measured on Linux too: the runtime stats
    // '/work', finds nothing there, and takes the session's WorkingDirectory.
    [LiveModelFact]
    public async Task LiveCopilot_PreexistingSession_ResumesWithWorkspaceShell()
    {
        var root = Path.Combine(Path.GetTempPath(), "live-copilot-ws-" + Guid.NewGuid().ToString("N"));
        await SwappableCopilotAgentFactory.RestartRuntimeAsync(new CopilotRuntimeOptions { BuiltInToolsEnabled = false, WorkspaceRoot = root });
        try
        {
            await using var cluster = await DeployAsync();
            var agent = await BindAsync(cluster, $"live-copilot-legacy-{Guid.NewGuid():n}", [ToolCatalog.WebSearch]);

            var first = await CollectAsync(agent.ChatAsync("Remember the code word MANGO-7. Reply with just 'ok'.", null));
            Log("legacy", first);
            Assert.Equal(AgentStreamEventType.Done, first[^1].Type);
            Assert.False(Directory.Exists(root));

            await SwappableCopilotAgentFactory.RestartRuntimeAsync(new CopilotRuntimeOptions { WorkspaceRoot = root });
            await agent.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

            var second = await CollectAsync(agent.ChatAsync(
                "Use your shell to print the current directory, then give me its output and the code word from earlier.", null));
            Log("resumed", second);
            var workspace = Assert.Single(Directory.GetDirectories(root, "*", SearchOption.AllDirectories), d => Directory.GetParent(d)!.Parent!.FullName == CopilotWorkspacePaths.Root(root));
            Assert.Contains(workspace, Reply(second).Replace('/', Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MANGO-7", Reply(second), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await SwappableCopilotAgentFactory.RestartRuntimeAsync();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
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
