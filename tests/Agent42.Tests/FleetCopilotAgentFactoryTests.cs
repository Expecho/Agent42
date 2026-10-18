using Agent42.Agents;
using Agent42.Backend;
using GitHub.Copilot;
using Microsoft.Agents.AI;
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

    public FleetCopilotAgentFactoryTests() => File.WriteAllText(_fakeRuntime, "");

    public void Dispose()
    {
        File.Delete(_fakeRuntime);
        _workspace.Dispose();
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

    // ADR 0102 AC13: a Copilot agent on a Claude model is a load error (the InvalidOperationException LoadAgentsAsync
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
}
