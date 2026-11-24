using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The global email-token-hash → owner directory (ADR 0096): the same round-trip and persistence contract as
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

    private IEmailAddressDirectoryGrain Dir(string slug) =>
        Cluster.GrainFactory.GetGrain<IEmailAddressDirectoryGrain>(EmailAddress.Hash(slug));

    [Fact]
    public async Task TryClaim_Empty_Succeeds_AndResolves()
    {
        var dir = Dir("claim-empty");
        Assert.True(await dir.TryClaimAsync("u1", "a1"));

        var owner = await dir.ResolveAsync();
        Assert.NotNull(owner);
        Assert.Equal("u1", owner.UserId);
        Assert.Equal("a1", owner.AgentId);
    }

    [Fact]
    public async Task TryClaim_SameHolder_IsIdempotent()
    {
        var dir = Dir("claim-same");
        Assert.True(await dir.TryClaimAsync("u1", "a1"));
        Assert.True(await dir.TryClaimAsync("u1", "a1"));
    }

    [Fact]
    public async Task TryClaim_OtherHolder_Refused_AndChangesNothing()
    {
        var dir = Dir("claim-other");
        Assert.True(await dir.TryClaimAsync("u1", "a1"));
        Assert.False(await dir.TryClaimAsync("u2", "a2"));
        Assert.False(await dir.TryClaimAsync("u1", "a-other"));

        var owner = await dir.ResolveAsync();
        Assert.Equal("u1", owner!.UserId);
        Assert.Equal("a1", owner.AgentId);
    }

    [Fact]
    public async Task TryClaim_AfterDeregister_Succeeds()
    {
        var dir = Dir("claim-after-dereg");
        Assert.True(await dir.TryClaimAsync("u1", "a1"));
        await dir.DeregisterAsync();

        Assert.True(await dir.TryClaimAsync("u2", "a2"));
        Assert.Equal("u2", (await dir.ResolveAsync())!.UserId);
    }

    [Fact]
    public async Task TryClaim_Concurrent_OneWinner()
    {
        var dir = Dir("claim-race");
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => dir.TryClaimAsync($"u{i}", $"a{i}")));

        Assert.Equal(1, results.Count(r => r));
    }

    [Fact]
    public async Task DeregisterIfHeldBy_ClearsOnlyForTheMatchingHolder()
    {
        var dir = Dir("dereg-if-held");
        Assert.True(await dir.TryClaimAsync("u1", "a1"));

        Assert.False(await dir.DeregisterIfHeldByAsync("u2", "a1"));
        Assert.False(await dir.DeregisterIfHeldByAsync("u1", "a2"));
        Assert.NotNull(await dir.ResolveAsync());

        Assert.True(await dir.DeregisterIfHeldByAsync("u1", "a1"));
        Assert.Null(await dir.ResolveAsync());
        Assert.False(await dir.DeregisterIfHeldByAsync("u1", "a1"));
    }
}
