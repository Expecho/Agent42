using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The local-bridge key and its global directory (ADR 0076) — copies of the ADR 0022 shapes, so these
/// pin the same two properties: the minter and the resolver hash identically, and an unregistered hash
/// resolves to null (which is what makes <c>/api/local-bridge</c> fail closed).
/// </summary>
public sealed class LocalBridgeKeyTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // AC1
    [Fact]
    public void BridgeKey_GeneratesPrefixedKeyAndStableHash()
    {
        var (raw, hash) = BridgeKey.Generate();

        Assert.StartsWith("sk_bridge_", raw);
        Assert.Equal(hash, BridgeKey.Hash(raw));
        Assert.Equal(64, hash.Length);
        Assert.All(hash, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.NotEqual(raw, BridgeKey.Generate().Raw);
    }

    // AC2
    [Fact]
    public async Task Directory_RegisterResolveDeregister()
    {
        var hash = BridgeKey.Hash("sk_bridge_directory-test");
        var directory = Cluster.GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(hash);

        Assert.Null(await directory.ResolveAsync());

        await directory.RegisterAsync("user-1", "server-1");
        await directory.AsReference<IGrainManagementExtension>().DeactivateOnIdle();

        // Survives reactivation (persisted state).
        var owner = await Cluster.GrainFactory.GetGrain<ILocalBridgeKeyDirectoryGrain>(hash).ResolveAsync();
        Assert.Equal(new LocalBridgeKeyOwner("user-1", "server-1"), owner);

        await directory.DeregisterAsync();
        Assert.Null(await directory.ResolveAsync());
    }
}
