using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

public sealed class AgentRegistryGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // AC1: create → list → get → update → delete, and the store survives grain deactivation.
    [Fact]
    public async Task AgentRegistry_CrudRoundTrips_AndSurvivesReactivation()
    {
        const string user = "registry-crud-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

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

        var reactivated = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
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
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-order-user");

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
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var result = await registry.SaveAsync(new AgentDefinition(
            "", "Keyed", "", "gpt-4o", "sys", default, default));

        Assert.False(string.IsNullOrEmpty(result.PlaintextApiKey), "insert should mint a raw key.");
        Assert.StartsWith("sk_agent_", result.PlaintextApiKey!);
        // Only the hash is stored, and it is the hash OF the returned raw key.
        Assert.Equal(ApiKey.Hash(result.PlaintextApiKey!), result.Agent.ApiKeyHash);

        // The hash resolves to this agent in the global directory.
        var owner = await Cluster.GrainFactory
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
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Rotatable", "", "gpt-4o", "sys", default, default));
        var oldHash = created.Agent.ApiKeyHash;

        var regenerated = await registry.SaveAsync(created.Agent, regenerateKey: true);

        Assert.False(string.IsNullOrEmpty(regenerated.PlaintextApiKey), "regenerate should mint a new key.");
        Assert.NotEqual(oldHash, regenerated.Agent.ApiKeyHash);
        Assert.Equal(ApiKey.Hash(regenerated.PlaintextApiKey!), regenerated.Agent.ApiKeyHash);

        // Old hash no longer resolves; new hash does.
        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(oldHash).ResolveAsync());
        var owner = await Cluster.GrainFactory
            .GetGrain<IAgentKeyDirectoryGrain>(regenerated.Agent.ApiKeyHash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal(created.Agent.Id, owner!.AgentId);
    }

    // AC7: deleting an agent deregisters its key (the hash no longer resolves).
    [Fact]
    public async Task AgentDelete_DeregistersKey()
    {
        const string user = "registry-delete-user";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);

        var created = await registry.SaveAsync(new AgentDefinition(
            "", "Doomed", "", "gpt-4o", "sys", default, default));
        var hash = created.Agent.ApiKeyHash;
        Assert.NotNull(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());

        await registry.DeleteAsync(created.Agent.Id);

        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());
    }

    // AC2 (ADR 0082): no concrete model means no seed. The flag stays false, so the first call that DOES carry a
    // model seeds, and a later one does not seed again.
    [Fact]
    public async Task NoModel_DoesNotSeed_ThenSeedsLater()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-research-nomodel");

        await registry.EnsureResearchAgentAsync("");
        Assert.Empty(await registry.GetAllAsync());

        await registry.EnsureResearchAgentAsync("foundry:gpt-5.4");
        await registry.EnsureResearchAgentAsync("foundry:gpt-5.4");

        var seeded = Assert.Single(await registry.GetAllAsync());
        Assert.Equal(AgentKinds.Research, seeded.Kind);
        Assert.Equal(ResearchAgentDefaults.Name, seeded.Name);
        Assert.Equal("foundry:gpt-5.4", seeded.Model);
        Assert.Equal([ToolCatalog.WebSearch], seeded.EnabledTools);
        Assert.False(seeded.Continuous);

        // It went through the normal insert path, so its ADR 0022 key hash is registered like any other agent's.
        var owner = await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(seeded.ApiKeyHash).ResolveAsync();
        Assert.Equal(seeded.Id, owner!.AgentId);
    }

    // AC3 (ADR 0082): a deleted research agent stays deleted, including across a reactivation, which proves the
    // flag was persisted rather than merely held in memory.
    [Fact]
    public async Task DeletedResearchAgent_IsNotReseeded()
    {
        const string user = "registry-research-deleted";
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        await registry.EnsureResearchAgentAsync("fake-model");
        await registry.DeleteAsync(Assert.Single(await registry.GetAllAsync()).Id);

        await registry.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        var reactivated = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>(user);
        await reactivated.EnsureResearchAgentAsync("fake-model");

        Assert.Empty(await reactivated.GetAllAsync());
    }

    // AC4 (ADR 0082): an update can change everything the user owns, but not Kind (the request never carries
    // it), and a research agent cannot be made continuous.
    [Fact]
    public async Task Update_PreservesKind_AndForcesPerChat()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-research-update");
        await registry.EnsureResearchAgentAsync("fake-model");
        var seeded = Assert.Single(await registry.GetAllAsync());

        var updated = (await registry.SaveAsync(seeded with
        {
            Name = "Deep dive",
            Kind = null,
            Continuous = true,
        })).Agent;

        Assert.Equal("Deep dive", updated.Name);
        Assert.Equal(AgentKinds.Research, updated.Kind);
        Assert.False(updated.Continuous);
        Assert.Equal(seeded.ApiKeyHash, updated.ApiKeyHash);
    }

    // Kind is set ONLY by seeding (ADR 0082): an insert that claims a kind stores an ordinary agent, and an ordinary
    // agent can still be continuous.
    [Fact]
    public async Task Insert_CannotClaimAKind()
    {
        var registry = Cluster.GrainFactory.GetGrain<IAgentRegistryGrain>("registry-research-insert");

        var created = (await registry.SaveAsync(new AgentDefinition(
            "", "Impostor", "", "fake-model", "sys", default, default, Continuous: true,
            Kind: AgentKinds.Research))).Agent;

        Assert.Null(created.Kind);
        Assert.True(created.Continuous);
    }
}
