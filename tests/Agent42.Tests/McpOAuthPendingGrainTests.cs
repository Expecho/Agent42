using Agent42.Agents;

namespace Agent42.Tests;

public sealed class McpOAuthPendingGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static McpOAuthPending Pending(string state) => new(
        State: state, ServerId: "srv1", CodeVerifier: "verifier", ClientId: "client-1",
        TokenEndpoint: "https://as/token", RedirectUri: "https://app/callback", Scopes: ["mcp.read"],
        Resource: "https://mcp/mcp");

    // Take returns the entry once and removes it — a replayed state finds nothing (CSRF/replay defence).
    [Fact]
    public async Task Take_IsOneTime()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>("pending-onetime");
        await grain.PutAsync(Pending("state-1"));

        var first = await grain.TakeAsync("state-1");
        Assert.NotNull(first);
        Assert.Equal("client-1", first!.ClientId);

        Assert.Null(await grain.TakeAsync("state-1")); // already consumed
    }

    [Fact]
    public async Task Take_UnknownState_ReturnsNull()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>("pending-unknown");
        Assert.Null(await grain.TakeAsync("nope"));
    }

    // The pending list is bounded: starting more than the cap evicts the oldest.
    [Fact]
    public async Task Put_BoundsPendingList()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>("pending-bounded");
        for (var i = 0; i < 12; i++)
        {
            await grain.PutAsync(Pending($"state-{i}"));
        }

        Assert.Null(await grain.TakeAsync("state-0")); // oldest evicted
        Assert.NotNull(await grain.TakeAsync("state-11")); // newest kept
    }

    [Fact]
    public async Task IsIsolatedPerUser()
    {
        var alice = Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>("pending-alice");
        var bob = Cluster.GrainFactory.GetGrain<IUserMcpOAuthPendingGrain>("pending-bob");
        await alice.PutAsync(Pending("shared-state"));

        Assert.Null(await bob.TakeAsync("shared-state"));
        Assert.NotNull(await alice.TakeAsync("shared-state"));
    }
}
