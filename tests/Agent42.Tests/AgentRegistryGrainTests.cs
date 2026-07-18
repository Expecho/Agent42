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
        var createResult = await registry.SaveAsync(new AgentDefinition(
            Id: "", Name: "Marvin", Description: "A paranoid android",
            Model: "gpt-4o", SystemPrompt: "You are perpetually depressed.",
            CreatedAt: default, UpdatedAt: default, Continuous: true));
        var created = createResult.Agent;

        Assert.False(string.IsNullOrEmpty(created.Id), "SaveAsync should assign an id on insert.");
        Assert.NotEqual(default, created.CreatedAt);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);
        Assert.True(created.Continuous, "the history-mode flag should persist on insert.");

        // List + get.
        Assert.Equal(created, Assert.Single(await registry.GetAllAsync()));
        Assert.Equal(created, await registry.GetAsync(created.Id));

        // Update: same id replaces in place, preserving CreatedAt and refreshing UpdatedAt.
        var updated = (await registry.SaveAsync(created with
        {
            Name = "Marvin the Paranoid Android",
            SystemPrompt = "You are brilliant but morose.",
        })).Agent;

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

        var first = (await registry.SaveAsync(new AgentDefinition(
            "", "First", "", "gpt-4o", "sys", default, default))).Agent;
        var second = (await registry.SaveAsync(new AgentDefinition(
            "", "Second", "", "gpt-4o", "sys", default, default))).Agent;

        // Touch the first one so it becomes the most recently updated.
        var firstTouched = (await registry.SaveAsync(first with { Description = "touched" })).Agent;

        var all = await registry.GetAllAsync();
        Assert.Equal(2, all.Count);
        Assert.Equal(firstTouched.Id, all[0].Id);
        Assert.Equal(second.Id, all[1].Id);
    }

    // AC1: creating an agent returns a non-empty raw key ONCE; only its hash is stored; the hash
    // resolves in the global directory to (user, agentId).
    [Fact]
    public async Task AgentRegistry_Create_MintsKey_StoresHashOnly()
    {
        const string user = "registry-mintkey-user";
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var result = await registry.SaveAsync(new AgentDefinition(
            "", "Keyed", "", "gpt-4o", "sys", default, default));

        Assert.False(string.IsNullOrEmpty(result.PlaintextApiKey), "insert should mint a raw key.");
        Assert.StartsWith("sk_agent_", result.PlaintextApiKey!);
        // Only the hash is stored, and it is the hash OF the returned raw key.
        Assert.Equal(ApiKey.Hash(result.PlaintextApiKey!), result.Agent.ApiKeyHash);

        // The hash resolves to this agent in the global directory.
        var owner = await _cluster.GrainFactory
            .GetGrain<IAgentKeyDirectoryGrain>(result.Agent.ApiKeyHash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(user, owner!.UserId);
        Assert.Equal(result.Agent.Id, owner.AgentId);

        // A plain update (no regenerate) mints no key and keeps the same hash.
        var updated = await registry.SaveAsync(result.Agent with { Name = "Renamed" });
        Assert.Null(updated.PlaintextApiKey);
        Assert.Equal(result.Agent.ApiKeyHash, updated.Agent.ApiKeyHash);
    }

    // AC6: regenerating a key invalidates the old one — new hash resolves, old hash no longer does.
    [Fact]
    public async Task AgentKey_Regenerate_InvalidatesOldKey()
    {
        const string user = "registry-regen-user";
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Rotatable", "", "gpt-4o", "sys", default, default));
        var oldHash = created.Agent.ApiKeyHash;

        var regenerated = await registry.SaveAsync(created.Agent, regenerateKey: true);

        Assert.False(string.IsNullOrEmpty(regenerated.PlaintextApiKey), "regenerate should mint a new key.");
        Assert.NotEqual(oldHash, regenerated.Agent.ApiKeyHash);
        Assert.Equal(ApiKey.Hash(regenerated.PlaintextApiKey!), regenerated.Agent.ApiKeyHash);

        // Old hash no longer resolves; new hash does.
        Assert.Null(await _cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(oldHash).ResolveAsync());
        var owner = await _cluster.GrainFactory
            .GetGrain<IAgentKeyDirectoryGrain>(regenerated.Agent.ApiKeyHash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(created.Agent.Id, owner!.AgentId);
    }

    // AC7: deleting an agent deregisters its key (the hash no longer resolves).
    [Fact]
    public async Task AgentDelete_DeregistersKey()
    {
        const string user = "registry-delete-user";
        var registry = _cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Doomed", "", "gpt-4o", "sys", default, default));
        var hash = created.Agent.ApiKeyHash;
        Assert.NotNull(await _cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());

        await registry.DeleteAsync(created.Agent.Id);

        Assert.Null(await _cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());
    }
}
