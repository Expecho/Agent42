using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The per-user store of in-flight Google authorizations (ADR 0053). Mirrors
/// <see cref="McpOAuthPendingGrainTests"/> because the grain is a deliberate copy of the MCP one —
/// the properties that matter (one-time take, bounded list, per-user isolation) are the same, and a
/// copy that quietly lost one of them would be a real regression.
/// </summary>
public sealed class GoogleOAuthPendingGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static GoogleOAuthPending Pending(string state) => new(
        State: state, CodeVerifier: "verifier",
        RedirectUri: "https://chat.agent42.nl/api/google/oauth/callback",
        StartedAt: DateTimeOffset.UtcNow);

    // AC8: Take returns the entry once and removes it — a replayed callback finds nothing, which is
    // what makes `state` a CSRF/replay defence rather than a decoration.
    [Fact]
    public async Task Take_IsOneTime()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleOAuthPendingGrain>("google-pending-onetime");
        await grain.PutAsync(Pending("state-1"));

        var first = await grain.TakeAsync("state-1");
        Assert.NotNull(first);
        Assert.Equal("verifier", first!.CodeVerifier);

        Assert.Null(await grain.TakeAsync("state-1")); // already consumed
    }

    [Fact]
    public async Task Take_UnknownState_ReturnsNull()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleOAuthPendingGrain>("google-pending-unknown");
        Assert.Null(await grain.TakeAsync("nope"));
    }

    // The pending list is bounded: a user who starts flows they never finish cannot grow the state item.
    [Fact]
    public async Task Put_BoundsPendingList()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleOAuthPendingGrain>("google-pending-bounded");
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
        var alice = Cluster.GrainFactory.GetGrain<IUserGoogleOAuthPendingGrain>("google-pending-alice");
        var bob = Cluster.GrainFactory.GetGrain<IUserGoogleOAuthPendingGrain>("google-pending-bob");
        await alice.PutAsync(Pending("shared-state"));

        Assert.Null(await bob.TakeAsync("shared-state"));
        Assert.NotNull(await alice.TakeAsync("shared-state"));
    }
}
