using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The global email-token-hash → owner directory (ADR 0095): the same round-trip and persistence contract as
/// <see cref="AgentKeyDirectoryGrainTests"/>, plus the property the separate grain type exists for — a hash
/// registered here never resolves in the API-key directory.
/// </summary>
public sealed class EmailAddressDirectoryGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    [Fact]
    public async Task Register_Resolve_Deregister_RoundTrips()
    {
        var hash = EmailAddress.Hash("agent-directory-test");
        var directory = Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(hash);

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
        var hash = EmailAddress.Hash("agent-directory-persist");
        var directory = Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(hash);
        await directory.RegisterAsync("user-2", "agent-2");

        await directory.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var owner = await Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(hash).ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal("user-2", owner!.UserId);
        Assert.Equal("agent-2", owner.AgentId);
    }

    [Fact]
    public async Task Registration_NeverResolvesInTheApiKeyDirectory()
    {
        var hash = EmailAddress.Hash("agent-directory-disjoint");
        await Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(hash).RegisterAsync("user-3", "agent-3");

        Assert.Null(await Cluster.GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash).ResolveAsync());
    }
}
