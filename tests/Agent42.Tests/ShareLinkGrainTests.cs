using Orleans.Core.Internal;
using Orleans.TestingHost;
using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The read-only share-link grains (ADR 0035): the global token-hash → conversation directory (mirrors the
/// ADR 0022 key directory), and the per-user store that keeps that directory in sync. The load-bearing
/// property is that a revoked link STOPS resolving — unsharing has no conversation-deletion backstop, so the
/// per-user grain must deregister the directory, or a revoked link would stay live.
/// </summary>
public sealed class ShareLinkGrainTests : IAsyncLifetime
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

    [Fact]
    public async Task Directory_Register_Resolve_Deregister_RoundTrips()
    {
        var hash = ShareToken.Hash("shr_directory-test");
        var directory = _cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(hash);

        // Unregistered → null (fail closed → the endpoint 404s).
        Assert.Null(await directory.ResolveAsync());

        await directory.RegisterAsync("user-1", "conv-1");
        var target = await directory.ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("user-1", target!.UserId);
        Assert.Equal("conv-1", target.ConversationId);

        await directory.DeregisterAsync();
        Assert.Null(await directory.ResolveAsync());
    }

    [Fact]
    public async Task Directory_Registration_SurvivesReactivation()
    {
        var hash = ShareToken.Hash("shr_directory-persist");
        var directory = _cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(hash);
        await directory.RegisterAsync("user-2", "conv-2");

        // Force deactivation so the next call re-reads persisted state (Cosmos in prod).
        await directory.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(hash);
        var target = await reactivated.ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("user-2", target!.UserId);
        Assert.Equal("conv-2", target.ConversationId);
    }

    [Fact]
    public async Task UserGrain_Create_IsIdempotent_AndRegistersTheDirectory()
    {
        var links = _cluster.GrainFactory.GetGrain<IUserShareLinksGrain>("owner-1");

        // Not shared → null.
        Assert.Null(await links.GetAsync("conv-a"));

        var first = await links.CreateOrGetAsync("conv-a");
        Assert.StartsWith("shr_", first.Token);
        Assert.Equal("conv-a", first.ConversationId);

        // Idempotent — a second call returns the SAME token (one stable link per conversation).
        var again = await links.CreateOrGetAsync("conv-a");
        Assert.Equal(first.Token, again.Token);

        // The global directory resolves the token to this owner + conversation.
        var target = await _cluster.GrainFactory
            .GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(first.Token)).ResolveAsync();
        Assert.NotNull(target);
        Assert.Equal("owner-1", target!.UserId);
        Assert.Equal("conv-a", target.ConversationId);
    }

    [Fact]
    public async Task UserGrain_Revoke_DeregistersTheDirectory_SoTheLinkGoesDead()
    {
        var links = _cluster.GrainFactory.GetGrain<IUserShareLinksGrain>("owner-2");
        var link = await links.CreateOrGetAsync("conv-b");
        var directory = _cluster.GrainFactory.GetGrain<IShareLinkDirectoryGrain>(ShareToken.Hash(link.Token));
        Assert.NotNull(await directory.ResolveAsync());

        await links.RemoveAsync("conv-b");

        // The owner no longer lists it AND the public directory no longer resolves it (dead, not live).
        Assert.Null(await links.GetAsync("conv-b"));
        Assert.Null(await directory.ResolveAsync());
    }

    [Fact]
    public async Task UserGrain_Revoke_UnknownConversation_IsNoOp()
    {
        var links = _cluster.GrainFactory.GetGrain<IUserShareLinksGrain>("owner-3");
        await links.RemoveAsync("never-shared"); // must not throw
        Assert.Null(await links.GetAsync("never-shared"));
    }
}
