using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The per-user Google credential store (ADR 0053). What matters here is not "a grain persists a
/// record" — it is that the grain holds <b>ciphertext</b>, that a disconnect really removes it, and
/// that one user's connection is not visible to another.
/// </summary>
public sealed class UserGoogleGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static GoogleConnection Connection(string suffix = "") => new(
        AccessTokenCiphertext: $"cipher-access{suffix}",
        RefreshTokenCiphertext: $"cipher-refresh{suffix}",
        AccessTokenExpiresAt: DateTimeOffset.UtcNow.AddHours(1),
        AccountEmail: "someone@example.com",
        ConnectedAt: DateTimeOffset.UtcNow);

    [Fact]
    public async Task NeverConnected_ReturnsNull()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-never");
        Assert.Null(await grain.GetAsync());
    }

    [Fact]
    public async Task Set_ThenGet_RoundTrips()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-roundtrip");
        await grain.SetAsync(Connection());

        var stored = await grain.GetAsync();
        Assert.NotNull(stored);
        Assert.Equal("cipher-access", stored!.AccessTokenCiphertext);
        Assert.Equal("cipher-refresh", stored.RefreshTokenCiphertext);
        Assert.Equal("someone@example.com", stored.AccountEmail);
    }

    // A refresh replaces the bundle rather than accumulating one — the grain holds at most one
    // connection per user, so a stale refresh token cannot linger behind a newer one.
    [Fact]
    public async Task Set_ReplacesTheStoredConnection()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-replace");
        await grain.SetAsync(Connection());
        await grain.SetAsync(Connection("-2"));

        Assert.Equal("cipher-refresh-2", (await grain.GetAsync())!.RefreshTokenCiphertext);
    }

    [Fact]
    public async Task Clear_RemovesTheConnection_AndIsIdempotent()
    {
        var grain = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-clear");
        await grain.SetAsync(Connection());

        await grain.ClearAsync();
        Assert.Null(await grain.GetAsync());

        await grain.ClearAsync(); // disconnecting when not connected is a no-op, not an error
        Assert.Null(await grain.GetAsync());
    }

    [Fact]
    public async Task IsIsolatedPerUser()
    {
        var alice = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-alice");
        var bob = Cluster.GrainFactory.GetGrain<IUserGoogleGrain>("google-bob");
        await alice.SetAsync(Connection());

        Assert.Null(await bob.GetAsync());
        Assert.NotNull(await alice.GetAsync());
    }
}
