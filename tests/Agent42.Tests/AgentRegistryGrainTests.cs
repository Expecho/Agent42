using Agent42.Agents;
using Orleans.Core.Internal;
using Orleans.TestingHost;

namespace Agent42.Tests;

public sealed class AgentRegistryGrainTests : IAsyncLifetime
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

    // AC1: create → list → get → update → delete, and the store survives grain deactivation.
    [Fact]
    public async Task AgentRegistry_CrudRoundTrips_AndSurvivesReactivation()
    {
        const string user = "registry-crud-user";
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        // Create: an empty id means insert; the grain assigns the id + timestamps.
        // Continuous: true exercises the history-mode flag (ADR 0016) through the whole
        // create → get → update → reactivate cycle.
        var created = await registry.SaveAsync(new AgentDefinition(
            Id: "", Name: "Marvin", Description: "A paranoid android",
            Model: "gpt-4o", SystemPrompt: "You are perpetually depressed.",
            CreatedAt: default, UpdatedAt: default, Continuous: true));

        Assert.False(string.IsNullOrEmpty(created.Id), "SaveAsync should assign an id on insert.");
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.True(created.Continuous, "the history-mode flag should persist on insert.");

        // List + get.
        Assert.Equal(created, Assert.Single(await registry.GetAllAsync()));
        Assert.Equal(created, await registry.GetAsync(created.Id));

        // Update: same id replaces in place, preserving CreatedAt and refreshing UpdatedAt.
        var updated = await registry.SaveAsync(created with
        {
            Name = "Marvin the Paranoid Android",
            SystemPrompt = "You are brilliant but morose.",
        });

        Assert.Equal(created.Id, updated.Id);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.True(updated.UpdatedAt >= created.UpdatedAt);
        Assert.Equal("Marvin the Paranoid Android", updated.Name);
        Assert.Single(await registry.GetAllAsync());

        // Force deactivation so the next call re-reads persisted state (Cosmos in prod).
        await registry.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        var afterReactivation = await reactivated.GetAsync(updated.Id);
        Assert.Equal(updated, afterReactivation);
        Assert.True(afterReactivation!.Continuous, "the history-mode flag should survive reactivation.");

        // Delete.
        await reactivated.DeleteAsync(updated.Id);
        Assert.Empty(await reactivated.GetAllAsync());
        Assert.Null(await reactivated.GetAsync(updated.Id));
    }

    [Fact]
    public async Task AgentRegistry_ListsMostRecentlyUpdatedFirst()
    {
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-order-user");

        var first = await registry.SaveAsync(new AgentDefinition(
            "", "First", "", "gpt-4o", "sys", default, default));
        var second = await registry.SaveAsync(new AgentDefinition(
            "", "Second", "", "gpt-4o", "sys", default, default));

        // Touch the first one so it becomes the most recently updated.
        var firstTouched = await registry.SaveAsync(first with { Description = "touched" });

        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(firstTouched.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);
    }
}
