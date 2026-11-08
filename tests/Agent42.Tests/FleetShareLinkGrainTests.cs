using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The fleet share-link grains (ADR 0120), the twins of the chat ones (ADR 0035). The load-bearing properties: a
/// revoked link stops resolving, and the two token namespaces never resolve in each other's directory.
/// </summary>
public sealed class FleetShareLinkGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    [Fact]
    public async Task Directory_Register_Resolve_Deregister_RoundTrips()
    {
        var directory = Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash("shr_fleet-directory-test"));

        Assert.Null(await directory.ResolveAsync());

        await directory.RegisterAsync("user-1", "task-1");
        var target = await directory.ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("user-1", target!.UserId);
        Assert.Equal("task-1", target.TaskId);

        await directory.DeregisterAsync();
        Assert.Null(await directory.ResolveAsync());
    }

    [Fact]
    public async Task Directory_Registration_SurvivesReactivation()
    {
        var hash = ShareToken.Hash("shr_fleet-directory-persist");
        var directory = Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(hash);
        await directory.RegisterAsync("user-2", "task-2");

        await directory.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var target = await Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(hash).ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("user-2", target!.UserId);
        Assert.Equal("task-2", target.TaskId);
    }

    [Fact]
    public async Task UserGrain_Create_IsIdempotent_AndRegistersTheDirectory()
    {
        var links = Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>("fleet-owner-1");

        Assert.Null(await links.GetAsync("task-a"));

        var first = await links.CreateOrGetAsync("task-a");
        Assert.StartsWith("shr_", first.Token);
        Assert.Equal("task-a", first.TaskId);

        Assert.Equal(first.Token, (await links.CreateOrGetAsync("task-a")).Token);
        Assert.Equal(first.Token, (await links.GetAsync("task-a"))!.Token);

        var target = await Cluster.GrainFactory
            .GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash(first.Token)).ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("fleet-owner-1", target!.UserId);
        Assert.Equal("task-a", target.TaskId);
    }

    [Fact]
    public async Task UserGrain_Revoke_DeregistersTheDirectory_SoTheLinkGoesDead()
    {
        var links = Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>("fleet-owner-2");
        var link = await links.CreateOrGetAsync("task-b");
        var directory = Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash(link.Token));
        Assert.NotNull(await directory.ResolveAsync());

        await links.RemoveAsync("task-b");

        Assert.Null(await links.GetAsync("task-b"));
        Assert.Null(await directory.ResolveAsync());
    }

    [Fact]
    public async Task UserGrain_Revoke_UnknownTask_IsNoOp()
    {
        var links = Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>("fleet-owner-3");
        await links.RemoveAsync("never-shared");
        Assert.Null(await links.GetAsync("never-shared"));
    }

    // A credential namespace gets its own directory: a fleet token must not open a chat, and a chat token must not open a task.
    [Fact]
    public async Task Directories_AreDisjoint()
    {
        var fleet = await Cluster.GrainFactory.GetGrain<IUserFleetShareLinksGrain>("fleet-owner-4").CreateOrGetAsync("task-c");
        var chat = await Cluster.GrainFactory.GetGrain<IUserShareLinksGrain>("fleet-owner-4").CreateOrGetAsync("conv-c");

        Assert.Null(await Cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(fleet.Token)).ResolveAsync());
        Assert.Null(await Cluster.GrainFactory.GetGrain<IFleetShareLinkDirectoryGrain>(ShareToken.Hash(chat.Token)).ResolveAsync());
    }
}
