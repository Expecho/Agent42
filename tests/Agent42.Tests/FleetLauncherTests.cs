using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The Backend side of the chat's fleet port (ADR 0115): the launcher picks the model with the fleet's own predicate
/// (the conversation's model when it can run the fleet, else the catalog default) and ties the task to the
/// conversation. Over a real <see cref="FleetTaskService"/>, so what is asserted is the stored record.
/// </summary>
public sealed class FleetLauncherTests : IAsyncDisposable
{
    private const string Alice = "alice";
    private const string NoToolsModel = "fake:text-only";
    private const string CopilotModel = "copilot:gpt-5";

    private static readonly string OnePlan =
        FakeChatClient.FleetTask("Write a haiku about lighthouses", new([FleetRunHarness.Plan("Sequential", "Writer")]));

    private readonly FleetStore _store = new(new InMemoryFleetBlobs());
    private readonly FleetTaskService _service;
    private readonly FleetLauncher _launcher;

    public FleetLauncherTests()
    {
        var catalogs = new Catalogs();
        _service = new FleetTaskService(
            _store,
            new FleetOptions { ApprovalDepth = -1, JudgeDepth = -1 },
            new SingleUserChatClientFactoryProvider(new SingleChatClientFactory(new FakeChatClient(), FleetRunHarness.ModelA)),
            catalogs,
            NullLogger<FleetTaskService>.Instance);
        _launcher = new FleetLauncher(_service, catalogs, null!, new ServiceCollection().BuildServiceProvider());
    }

    public async ValueTask DisposeAsync() => await _service.StopAsync(CancellationToken.None);

    [Fact]
    public async Task ToolCapableConversationModel_Used()
        => Assert.Equal(FleetRunHarness.ModelB, (await StartAsync(FleetRunHarness.ModelB)).PlannerModel);

    [Fact]
    public async Task NotToolCapableModel_UsesDefault()
        => Assert.Equal(FleetRunHarness.ModelA, (await StartAsync(NoToolsModel)).PlannerModel);

    [Fact]
    public async Task CopilotOnlyModel_UsesDefault()
        => Assert.Equal(FleetRunHarness.ModelA, (await StartAsync(CopilotModel)).PlannerModel);

    [Fact]
    public async Task PassesOriginConversation()
    {
        var record = await StartAsync(model: null);

        Assert.Equal("conv-1", record.OriginConversationId);
        Assert.Equal(FleetRunHarness.ModelA, record.PlannerModel);
        Assert.Equal(record.Id, (await _launcher.GetAsync(Alice, record.Id))!.Id);
        Assert.Null(await _launcher.GetAsync("bob", record.Id));
    }

    private async Task<FleetTaskRecord> StartAsync(string? model)
    {
        var (taskId, error) = await _launcher.StartAsync(Alice, "conv-1", OnePlan, model, followUpOf: null);
        Assert.Null(error);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (await _service.GetAsync(Alice, taskId!, timeout.Token) is not { Status: not FleetTaskStatus.Running })
        {
            await Task.Delay(20, timeout.Token);
        }

        return (await _store.GetTaskAsync(Alice, taskId!))!;
    }

    private sealed class Catalogs : IUserModelCatalogProvider, IModelCatalog
    {
        public Task<IModelCatalog> GetForUserAsync(string userId, CancellationToken cancellationToken = default)
            => Task.FromResult<IModelCatalog>(this);

        public Task<ModelCatalogResult> GetAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ModelCatalogResult(FleetRunHarness.ModelA,
            [
                new ModelInfo(FleetRunHarness.ModelA, "fake", "Model A", "a", null, SupportsTools: true, SupportsVision: false),
                new ModelInfo(FleetRunHarness.ModelB, "fake", null, "b", null, SupportsTools: true, SupportsVision: false),
                new ModelInfo(NoToolsModel, "fake", null, "t", null, SupportsTools: false, SupportsVision: false),
                new ModelInfo(CopilotModel, "copilot", null, "gpt-5", null, SupportsTools: true, SupportsVision: false, CopilotOnly: true),
            ]));
    }
}
