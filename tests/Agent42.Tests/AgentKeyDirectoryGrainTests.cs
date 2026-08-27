using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The global key-hash → owner directory (ADR 0022): register makes a hash resolve to its owner,
/// deregister makes it stop, and an unregistered hash resolves to null (which is what makes the run
/// endpoint fail closed). Registration survives grain deactivation (persisted state).
/// </summary>
public sealed class AgentKeyDirectoryGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    [Fact]
    public async Task Register_Resolve_Deregister_RoundTrips()
    {
        var hash = ApiKey.Hash("sk_agent_directory-test");
        var directory = Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash);

        // Unregistered → null (fail closed).
        Assert.Null(await directory.ResolveAsync());

        await directory.RegisterAsync("user-1", "agent-1");
        var owner = await directory.ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal("user-1", owner!.UserId);
        Assert.Equal("agent-1", owner.AgentId);

        await directory.DeregisterAsync();
        Assert.Null(await directory.ResolveAsync());
    }

    [Fact]
    public async Task Registration_SurvivesReactivation()
    {
        var hash = ApiKey.Hash("sk_agent_directory-persist");
        var directory = Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash);
        await directory.RegisterAsync("user-2", "agent-2");

        // Force deactivation so the next call re-reads persisted state (Cosmos in prod).
        await directory.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash);
        var owner = await reactivated.ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal("user-2", owner!.UserId);
        Assert.Equal("agent-2", owner.AgentId);
    }
}
