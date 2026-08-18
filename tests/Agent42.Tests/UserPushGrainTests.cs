using Orleans.Core.Internal;
using Orleans.TestingHost;
using Agent42.Agents;

namespace Agent42.Tests;

public sealed class UserPushGrainTests : IAsyncLifetime
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

    private static PushSubscription Sub(string endpoint, string p256dh = "p256", string auth = "auth") =>
        new(endpoint, p256dh, auth);

    // AC5: subscription add/get/remove roundtrips and survives reactivation.
    [Fact]
    public async Task Add_RoundTrips_AndSurvivesReactivation()
    {
        const string user = "push-roundtrip-user";
        var grain = _cluster.GrainFactory.GetGrain<IUserPushGrain>(user);

        // Never subscribed → empty.
        Assert.Empty(await grain.GetAllAsync());

        await grain.AddAsync(Sub("https://fcm.example/a"));

        // Force deactivation so the next call re-reads persisted state.
        await grain.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        var reactivated = _cluster.GrainFactory.GetGrain<IUserPushGrain>(user);
        var stored = Assert.Single(await reactivated.GetAllAsync());
        Assert.Equal("https://fcm.example/a", stored.Endpoint);
        Assert.Equal("p256", stored.P256dh);
        Assert.Equal("auth", stored.Auth);
    }

    // AC5: upsert dedupes by endpoint — the same device re-subscribing replaces, never duplicates.
    [Fact]
    public async Task Add_UpsertsByEndpoint()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserPushGrain>("push-upsert-user");

        await grain.AddAsync(Sub("https://fcm.example/dev1", p256dh: "old", auth: "old"));
        await grain.AddAsync(Sub("https://fcm.example/dev2"));
        await grain.AddAsync(Sub("https://fcm.example/dev1", p256dh: "new", auth: "new"));

        var all = await grain.GetAllAsync();
        Assert.Equal(2, all.Count);
        var dev1 = Assert.Single(all, s => s.Endpoint == "https://fcm.example/dev1");
        Assert.Equal("new", dev1.P256dh);
        Assert.Equal("new", dev1.Auth);
    }

    [Fact]
    public async Task Remove_DropsByEndpoint()
    {
        var grain = _cluster.GrainFactory.GetGrain<IUserPushGrain>("push-remove-user");

        await grain.AddAsync(Sub("https://fcm.example/keep"));
        await grain.AddAsync(Sub("https://fcm.example/drop"));

        await grain.RemoveAsync("https://fcm.example/drop");

        var remaining = Assert.Single(await grain.GetAllAsync());
        Assert.Equal("https://fcm.example/keep", remaining.Endpoint);

        // Removing an absent endpoint is a harmless no-op.
        await grain.RemoveAsync("https://fcm.example/nope");
        Assert.Single(await grain.GetAllAsync());
    }

    [Fact]
    public async Task IsIsolatedPerUser()
    {
        var alice = _cluster.GrainFactory.GetGrain<IUserPushGrain>("push-alice");
        var bob = _cluster.GrainFactory.GetGrain<IUserPushGrain>("push-bob");

        await alice.AddAsync(Sub("https://fcm.example/alice"));

        Assert.Single(await alice.GetAllAsync());
        Assert.Empty(await bob.GetAllAsync());
    }
}
