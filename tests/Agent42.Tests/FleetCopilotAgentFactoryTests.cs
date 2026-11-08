using Agent42.Agents;
using Agent42.Backend;
using GitHub.Copilot;
using Microsoft.Agents.AI;
using ModelInfo = Agent42.Backend.ModelInfo;
using Microsoft.Extensions.Logging.Abstractions;

#pragma warning disable GHCP001 // The SDK's permission RPC types are [Experimental]; these tests pin them.

namespace Agent42.Tests;

/// <summary>
/// The fleet's Copilot agents (ADR 0097) without a runtime process: the kill switch, model mapping, fall-through for
/// other models, and the default-mode client and session options that differ from ADR 0085's.
/// </summary>
public sealed class FleetCopilotAgentFactoryTests : IDisposable
{
    private readonly string _fakeRuntime = Path.Combine(Path.GetTempPath(), "fleet-copilot-" + Guid.NewGuid().ToString("N"));
    private readonly FleetWorkspace _workspace = new(Guid.NewGuid().ToString("N"));
    private readonly FleetCanvas _canvas = new(new FleetStore(new InMemoryFleetBlobs()), "alice", "w1", new RecordingFleetSink());

    public FleetCopilotAgentFactoryTests() => File.WriteAllText(_fakeRuntime, "");

    public void Dispose()
    {
        File.Delete(_fakeRuntime);
        _workspace.Dispose();
        _canvas.Dispose();
    }

    private FleetCopilotHost Host(bool enabled, string? runtime = null, string defaultCopilotModel = "")
        => new(new CopilotRuntimeOptions { RuntimePath = runtime ?? _fakeRuntime },
            new FleetOptions { CopilotAgentsEnabled = enabled, DefaultCopilotModel = defaultCopilotModel },
            NullLoggerFactory.Instance);

    private FleetCopilotAgentFactory Factory(FleetCopilotHost host, IChatClientFactory? userFactory = null)
        => new(host,
            userFactory ?? new SingleChatClientFactory(new FakeChatClient(), "fake:model-a"),
            [FleetRunHarness.ModelA, FleetRunHarness.ModelB],
            FleetRunHarness.ModelA,
            _workspace,
            _canvas,
            FleetPromptSet.Defaults,
            new FleetCopilotPermissionPolicy(_workspace.Root, new ScriptedFleetInteraction(), CancellationToken.None));

    [Fact]
    public async Task CopilotDisabled_LoadErrorForPlanner()
    {
        var factory = Factory(Host(enabled: false));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Dev", model: "copilot")));

        Assert.Equal(FleetCopilotAgentFactory.Unavailable, error.Message);
    }

    [Fact]
    public async Task RuntimeMissing_IsUnavailable()
    {
        var host = Host(enabled: true, runtime: Path.Combine(_fakeRuntime + "-missing", "copilot"));

        Assert.False(host.Available);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Factory(host).CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Dev", model: "copilot")));
    }

    [Fact]
    public async Task NonCopilotModel_FallsThrough()
    {
        // Behind the aggregator the runner builds: a null from the Copilot factory hands the agent to the next one.
        var aggregator = new AggregatorPromptAgentFactory(Factory(Host(enabled: false)), new ChatClientPromptAgentFactory(new FakeChatClient()));

        var agent = await aggregator.CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Writer", model: FleetRunHarness.ModelA));

        Assert.IsType<ChatClientAgent>(agent);
    }

    [Fact]
    public async Task UnknownModel_AndProviderWithoutCopilotMapping_AreLoadErrors()
    {
        var factory = Factory(Host(enabled: true));

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Dev", model: "copilot/claude-opus-5-5")));
        Assert.Contains("Unknown model 'claude-opus-5-5'", unknown.Message);

        // SingleChatClientFactory is not an ICopilotProviderSource: the runtime can't reach its provider.
        var unmapped = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Dev", model: $"copilot/{FleetRunHarness.ModelB}")));
        Assert.StartsWith(FleetCopilotAgentFactory.Unavailable, unmapped.Message);
    }

    // ADR 0103 AC13: a Copilot agent on a Claude model is a load error (the InvalidOperationException LoadAgentsAsync
    // hands the planner as plan.rejected), because an Anthropic provider has no Copilot mapping. Not a crash.
    [Fact]
    public async Task CopilotAgent_OnAnthropicModel_IsRejectedToPlanner()
    {
        const string claude = "claude:claude-test";
        var factory = new FleetCopilotAgentFactory(
            Host(enabled: true),
            AnthropicProviderTests.Factory(new RecordingHttpHandler()),
            [claude],
            claude,
            _workspace,
            _canvas,
            FleetPromptSet.Defaults,
            new FleetCopilotPermissionPolicy(_workspace.Root, new ScriptedFleetInteraction(), CancellationToken.None));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => factory.CreateFromYamlAsync(FakeChatClient.FleetAgentYaml("Dev", model: $"copilot/{claude}")));

        Assert.StartsWith(FleetCopilotAgentFactory.Unavailable, error.Message);
        Assert.Contains($"The provider of '{claude}' can't be reached", error.Message);
    }

    [Fact]
    public void ModelFor_MapsCopilotHints()
    {
        Assert.Equal(FleetRunHarness.ModelB, Factory(Host(true)).ModelFor($"copilot/{FleetRunHarness.ModelB}"));
        Assert.Equal(FleetRunHarness.ModelA, Factory(Host(true)).ModelFor("copilot"));
        Assert.Equal(FleetRunHarness.ModelB, Factory(Host(true, defaultCopilotModel: FleetRunHarness.ModelB)).ModelFor("copilot"));
    }

    [Fact]
    public async Task Disabled_PlannerSeesLoadError_AndNoCopilotSection()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 });
        var run = new FleetRun
        {
            TaskId = h.Run.TaskId,
            Options = h.Run.Options,
            Prompts = h.Run.Prompts,
            Budget = h.Run.Budget,
            Workspace = h.Run.Workspace,
            Canvas = h.Run.Canvas,
            DefaultModel = h.Run.DefaultModel,
            Clients = h.Run.Clients,
            Events = h.Events,
            Interaction = h.Interaction,
            Logger = h.Run.Logger,
            CopilotFactory = Factory(Host(enabled: false)),
            CopilotEnabled = false,
        };
        var runner = new FleetTeamRunner(run);
        var copilotPlan = new { rationale = "x", pattern = "Sequential", agents = new[] { new { name = "Dev", yaml = FakeChatClient.FleetAgentYaml("Dev", model: "copilot") } } };

        Assert.DoesNotContain("GitHub Copilot", runner.PlannerInstructions(h.Root));
        await runner.RunAsync(FakeChatClient.FleetTask("Build a tool", new([copilotPlan, FleetRunHarness.Plan("Sequential", "Writer")])));

        Assert.Contains(FleetCopilotAgentFactory.Unavailable, Assert.Single(h.Events.Of("plan.rejected")).GetProperty("reason").GetString());
    }

    [Fact]
    public void ClientOptions_DefaultMode_NoSessionFs_MinimalEnvironmentWithPath()
    {
        var options = FleetCopilotHost.BuildClientOptions(new CopilotRuntimeOptions { RuntimePath = _fakeRuntime });

        Assert.Equal(CopilotClientMode.CopilotCli, options.Mode);
        Assert.Null(options.SessionFs);
        Assert.False(options.UseLoggedInUser);
        var allowed = CopilotAgentFactory.MinimalEnvironment().Keys.Append("PATH").ToHashSet();
        Assert.All(options.Environment!.Keys, key => Assert.Contains(key, allowed));
    }

    [Fact]
    public void SessionConfig_WorksInTheTaskWorkspace_WithThePolicy()
    {
        var factory = Factory(Host(true));
        var settings = CopilotProviderSettings.OpenAICompatible(new Uri("https://api.example/v1"), "key");

        var config = factory.BuildSessionConfig(settings, "gpt-5", "You build.\n\nreport");

        Assert.Equal(_workspace.Root, config.WorkingDirectory);
        Assert.Equal("gpt-5", config.Model);
        Assert.NotNull(config.OnPermissionRequest);
        Assert.False(config.LargeOutput!.Enabled);
        Assert.Equal(SystemMessageMode.Append, config.SystemMessage!.Mode);
        Assert.Null(config.CreateSessionFsProvider);
    }

    /// <summary>ADR 0102: the canvas tools are custom tools, and AvailableTools stays null. Setting it would restrict the
    /// default-mode session to the custom tools and hide the shell and file built-ins.</summary>
    [Fact]
    public void SessionConfig_CarriesCanvasTools_KeepsBuiltins()
    {
        var settings = CopilotProviderSettings.OpenAICompatible(new Uri("https://api.example/v1"), "key");

        var config = Factory(Host(true)).BuildSessionConfig(settings, "gpt-5", "You build.");

        Assert.Equal(FleetCanvas.ToolNames, config.Tools!.Select(t => t.Name));
        Assert.Null(config.AvailableTools);
    }

    private static MultiProviderChatClientFactory MixedFactory()
    {
        var mine = new ProviderOptions { Kind = ProviderKind.OpenAICompatible, Endpoint = "https://api.example/v1", ApiKey = "key" };
        var claude = new ProviderOptions { Kind = ProviderKind.Anthropic, Endpoint = "https://anthropic.stub", ApiKey = "key" };
        return new MultiProviderChatClientFactory(
            new Dictionary<string, MultiProviderChatClientFactory.Provider>
            {
                ["mine"] = MultiProviderChatClientFactory.Provider.OpenAICompatible(mine, new Uri(mine.Endpoint), mine.ApiKey),
                ["claude"] = MultiProviderChatClientFactory.Provider.Anthropic(claude, new Uri(claude.Endpoint), claude.ApiKey, new RecordingHttpHandler()),
            },
            "claude:claude-test", "test", NullLogger<MultiProviderChatClientFactory>.Instance);
    }

    private static ModelInfo Model(string id, bool copilotOnly = false) => new(id, id.Split(':')[0], id, id.Split(':')[1], null, true, false, CopilotOnly: copilotOnly);

    [Fact]
    public void ReachableModels_KeepsByokAndGitHub_ExcludesAnthropic()
    {
        var models = new[] { Model("mine:gpt-5"), Model("claude:claude-test"), Model("github-copilot:gpt-6", copilotOnly: true) };

        var reachable = FleetCopilotAgentFactory.ReachableModels(MixedFactory(), models);

        // GitHub is not connected in this factory, so its id is refused; Anthropic has no Copilot mapping.
        Assert.Equal(["mine:gpt-5"], reachable.Select(m => m.Id));
        Assert.Empty(FleetCopilotAgentFactory.ReachableModels(new SingleChatClientFactory(new FakeChatClient(), "fake:model-a"), models));
    }

    private string PlannerPrompt(string defaultModel, string? copilotDefault, string defaultCopilotModel = "", bool withByok = true)
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 });
        var host = Host(enabled: true, defaultCopilotModel: defaultCopilotModel);
        var run = new FleetRun
        {
            TaskId = h.Run.TaskId,
            Options = h.Run.Options,
            Prompts = h.Run.Prompts,
            Budget = h.Run.Budget,
            Workspace = h.Run.Workspace,
            Canvas = h.Run.Canvas,
            DefaultModel = h.Run.DefaultModel,
            Clients = h.Run.Clients,
            Events = h.Events,
            Interaction = h.Interaction,
            Logger = h.Run.Logger,
            CopilotFactory = Factory(host),
            CopilotEnabled = true,
            CopilotByokModelIds = withByok ? new Dictionary<string, string?> { ["mine:gpt-5"] = "GPT 5" } : new Dictionary<string, string?>(),
            CopilotDefaultModel = copilotDefault ?? defaultModel,
        };
        return new FleetTeamRunner(run).PlannerInstructions(h.Root);
    }

    [Fact]
    public void Planner_ListsReachableByokModels_NotUnreachableOnes()
    {
        var prompt = PlannerPrompt("claude:claude-test", null);

        Assert.Contains("- mine:gpt-5 (GPT 5)", prompt);
        Assert.Contains("cannot run as a Copilot agent", prompt);
        Assert.DoesNotContain("copilot/claude", prompt);
        Assert.DoesNotContain("- claude:claude-test", prompt);
    }

    [Fact]
    public void Planner_UnreachableDefault_SaysPlainCopilotCannotRun()
    {
        var prompt = PlannerPrompt("claude:claude-test", null);

        Assert.Contains("plain 'copilot' would run on claude:claude-test, which a Copilot agent cannot run on", prompt);
        Assert.Contains("Name 'copilot/<model id>'", prompt);
    }

    [Fact]
    public void Planner_ReachableDefault_SaysPlainCopilotRunsOnIt()
    {
        var prompt = PlannerPrompt("mine:gpt-5", null);

        Assert.Contains("plain 'copilot' runs on mine:gpt-5.", prompt);
        Assert.DoesNotContain("cannot run on.", prompt);
    }

    [Fact]
    public void Planner_ConfiguredDefaultCopilotModel_IsWhatThePromptSays()
    {
        // The task's model is unreachable, but the operator's default is: the prompt names the configured one.
        var reachable = PlannerPrompt("claude:claude-test", "mine:gpt-5", defaultCopilotModel: "mine:gpt-5");
        Assert.Contains("plain 'copilot' runs on mine:gpt-5.", reachable);
        Assert.DoesNotContain("claude:claude-test, which", reachable);

        // The factory matches catalog ids case-insensitively, so the prompt must too.
        var cased = PlannerPrompt("claude:claude-test", "mine:GPT-5", defaultCopilotModel: "mine:GPT-5");
        Assert.Contains("plain 'copilot' runs on mine:GPT-5.", cased);

        var unreachable = PlannerPrompt("mine:gpt-5", "claude:claude-test", defaultCopilotModel: "claude:claude-test");
        Assert.Contains("would run on claude:claude-test, which a Copilot agent cannot run on", unreachable);
    }

    [Fact]
    public void NoReachableModel_CopilotIsNotOffered()
    {
        var host = Host(enabled: true);

        Assert.True(host.Available);
        Assert.False(FleetCopilotAgentFactory.CopilotOffered(host, []));
        Assert.False(FleetCopilotAgentFactory.CopilotOffered(null, [Model("mine:gpt-5")]));
        Assert.True(FleetCopilotAgentFactory.CopilotOffered(host, [Model("mine:gpt-5")]));
    }
}
