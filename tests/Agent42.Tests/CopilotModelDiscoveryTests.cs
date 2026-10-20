using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// GitHub Copilot model discovery (ADR 0108): the per-user directory (cache, timeout, failure), the catalog contribution
/// that flags the models copilot-only, the per-user catalog wiring, and the fleet keeping them off every chat client.
/// </summary>
public sealed class CopilotModelDiscoveryTests
{
    private const string Token = "gho_discovery";

    // AC9: one listing per token within the TTL, a fresh one after it.
    [Fact]
    public async Task Directory_CachesPerToken_UntilTheTtl()
    {
        var lister = new CountingLister();
        var time = new ManualTime();
        var directory = new CopilotModelDirectory(lister, time, NullLogger<CopilotModelDirectory>.Instance);

        await directory.GetAsync(Token, CancellationToken.None);
        await directory.GetAsync(Token, CancellationToken.None);
        Assert.Equal(1, lister.Calls);

        await directory.GetAsync("gho_another-account", CancellationToken.None);
        Assert.Equal(2, lister.Calls);

        time.Advance(CopilotModelDirectory.Ttl);
        await directory.GetAsync(Token, CancellationToken.None);
        Assert.Equal(3, lister.Calls);
    }

    // AC9: a hung listing is cut off, and a failed one (runtime missing, GitHub down, revoked token) gives no models and
    // is not cached.
    [Fact]
    public async Task Directory_TimeoutAndFailure_AreEmpty_AndBrieflyCached()
    {
        var hung = new CopilotModelDirectory(new HungLister(), TimeProvider.System, NullLogger<CopilotModelDirectory>.Instance,
            TimeSpan.FromMilliseconds(50));
        Assert.Empty(await hung.GetAsync(Token, CancellationToken.None));

        // A failure is remembered for FailureTtl only, so a failing account can't stall every picker load.
        var failing = new CountingLister { Fail = true };
        var time = new ManualTime();
        var directory = new CopilotModelDirectory(failing, time, NullLogger<CopilotModelDirectory>.Instance);
        Assert.Empty(await directory.GetAsync(Token, CancellationToken.None));
        Assert.Empty(await directory.GetAsync(Token, CancellationToken.None));
        Assert.Equal(1, failing.Calls);

        time.Advance(CopilotModelDirectory.FailureTtl);
        Assert.Empty(await directory.GetAsync(Token, CancellationToken.None));
        Assert.Equal(2, failing.Calls);
    }

    // AC9: the catalog qualifies each id under the reserved key and flags it copilot-only, with fixed capabilities.
    [Fact]
    public async Task Catalog_FlagsModelsCopilotOnly()
    {
        var directory = new CopilotModelDirectory(new CountingLister(), TimeProvider.System, NullLogger<CopilotModelDirectory>.Instance);

        var result = await new CopilotModelCatalog(directory, Token).GetAsync();

        var model = Assert.Single(result.Models);
        Assert.Equal("github-copilot:claude-sonnet-5", model.Id);
        Assert.Equal("Claude Sonnet 5", model.DisplayName);
        Assert.True(model.CopilotOnly);
        Assert.True(model.SupportsTools);
        Assert.False(model.SupportsVision);
        Assert.Equal("", result.Default);
    }

    // AC9: /api/models' per-user catalog lists a connected plan's models next to the declared ones, and never makes one
    // the default; with no directory (no Copilot runtime on the host) there are none.
    [Fact]
    public async Task UserCatalog_ListsConnectedPlanModels_NeverAsDefault()
    {
        var resolver = new FixedResolver();
        var directory = new CopilotModelDirectory(new CountingLister(), TimeProvider.System, NullLogger<CopilotModelDirectory>.Instance);

        var withRuntime = await (await new UserModelCatalogProvider(resolver, null!, NullLoggerFactory.Instance, directory)
            .GetForUserAsync("alice")).GetAsync();
        var withoutRuntime = await (await new UserModelCatalogProvider(resolver, null!, NullLoggerFactory.Instance)
            .GetForUserAsync("alice")).GetAsync();

        Assert.Equal("github:openai/gpt-4o-mini", withRuntime.Default);
        Assert.Contains(withRuntime.Models, m => m.Id == "github-copilot:claude-sonnet-5" && m.CopilotOnly);
        Assert.Equal("github:openai/gpt-4o-mini", withRuntime.Models[0].Id);
        Assert.DoesNotContain(withoutRuntime.Models, m => m.CopilotOnly);
    }

    // AC7: no planner, judge, manager or ordinary fleet agent is ever built on a GitHub Copilot model, and a task can't
    // be submitted with one as its model.
    [Fact]
    public void Fleet_CopilotOnlyModel_IsNotToolCapable()
    {
        var model = new ModelInfo("github-copilot:gpt-6-sol", GitHubCopilotProvider.Key, "GPT 6", "gpt-6-sol", null,
            SupportsTools: true, SupportsVision: false, CopilotOnly: true);

        Assert.False(FleetTaskService.IsToolCapable(model));
        Assert.True(FleetTaskService.IsToolCapable(model with { CopilotOnly = false }));
    }

    // AC7: the planner is told the GitHub Copilot models run only as 'copilot/<id>', separately from its chat models.
    [Fact]
    public void Planner_ListsCopilotModels_OnlyAsCopilot()
    {
        using var h = new FleetRunHarness(new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 });
        var installed = Path.GetTempFileName();
        try
        {
            var host = new FleetCopilotHost(new CopilotRuntimeOptions { RuntimePath = installed },
                new FleetOptions { CopilotAgentsEnabled = true }, NullLoggerFactory.Instance);
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
                ModelNames = h.Run.ModelNames,
                Events = h.Events,
                Interaction = h.Interaction,
                Logger = h.Run.Logger,
                CopilotFactory = new FleetCopilotAgentFactory(host, new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA),
                    [FleetRunHarness.ModelA, "github-copilot:claude-sonnet-5"], FleetRunHarness.ModelA, h.Workspace, h.Canvas,
                    FleetPromptSet.Defaults, new FleetCopilotPermissionPolicy(h.Workspace.Root, h.Interaction, CancellationToken.None)),
                CopilotEnabled = true,
                CopilotModelNames = new Dictionary<string, string?> { ["github-copilot:claude-sonnet-5"] = "Claude Sonnet 5" },
            };

            var instructions = new FleetTeamRunner(run).PlannerInstructions(h.Root);

            var marker = instructions.IndexOf("run only as a Copilot agent", StringComparison.Ordinal);
            Assert.True(marker > 0);
            Assert.True(marker < instructions.IndexOf("- github-copilot:claude-sonnet-5 (Claude Sonnet 5)", StringComparison.Ordinal));
            Assert.DoesNotContain("github-copilot", instructions[..marker], StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(installed);
        }
    }

    private sealed class CountingLister : ICopilotModelLister
    {
        public int Calls { get; private set; }

        public bool Fail { get; init; }

        public Task<IReadOnlyList<CopilotListedModel>> ListAsync(string gitHubToken, CancellationToken cancellationToken)
        {
            Calls++;
            return Fail
                ? Task.FromException<IReadOnlyList<CopilotListedModel>>(new IOException("401 Unauthorized"))
                : Task.FromResult<IReadOnlyList<CopilotListedModel>>([new("claude-sonnet-5", "Claude Sonnet 5")]);
        }
    }

    private sealed class HungLister : ICopilotModelLister
    {
        public Task<IReadOnlyList<CopilotListedModel>> ListAsync(string gitHubToken, CancellationToken cancellationToken)
            => new TaskCompletionSource<IReadOnlyList<CopilotListedModel>>().Task;
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    // An ordinary provider plus a connected GitHub Copilot one, resolved the way the overlay would.
    private sealed class FixedResolver : IUserProviderResolver
    {
        public Task<ResolvedProviderSet> ResolveAsync(string userId, CancellationToken cancellationToken = default)
        {
            var setup = ModelProviderBuilder.Build(new ModelProvidersOptions
            {
                Providers =
                {
                    ["github"] = new ProviderOptions
                    {
                        Kind = ProviderKind.OpenAICompatible,
                        Endpoint = "https://models.github.ai/inference",
                        ApiKey = "pat",
                        Models = [new ConfiguredModel { Id = "openai/gpt-4o-mini", SupportsTools = true }],
                    },
                    [GitHubCopilotProvider.Key] = new ProviderOptions { Kind = ProviderKind.GitHubCopilot, ApiKey = Token },
                },
            }, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
            return Task.FromResult(new ResolvedProviderSet(
                setup.Providers, setup.Discovering, setup.DeclaredModels, "github:openai/gpt-4o-mini", 1));
        }
    }
}
