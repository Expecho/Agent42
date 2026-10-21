using Agent42.Agents;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>
/// The Signal number claim (ADR 0111 phase 3, SC2; AC28): one number belongs to one connection, so no user can run
/// <c>register</c> over another's number; a pending claim expires so an abandoned registration can't squat a number.
/// </summary>
public sealed class SignalNumberDirectoryGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private ISignalNumberDirectoryGrain Number(string number) => Cluster.GrainFactory.GetGrain<ISignalNumberDirectoryGrain>(number);

    [Fact]
    public async Task Claim_RefusesAnotherConnection_WhilePendingOrActive()
    {
        var number = Number("+31600000101");

        Assert.True(await number.TryClaimAsync("user-a", "conn-a", Now));
        Assert.False(await number.TryClaimAsync("user-b", "conn-b", Now.AddMinutes(1)));

        Assert.True(await number.ActivateAsync("conn-a"));
        Assert.False(await number.TryClaimAsync("user-b", "conn-b", Now.AddDays(30)));
        Assert.Equal(new SignalNumberClaim("user-a", "conn-a", SignalNumberClaimState.Active, Now), await number.GetAsync());
    }

    [Fact]
    public async Task ExpiredPendingClaim_CanBeTakenOver()
    {
        var number = Number("+31600000102");
        await number.TryClaimAsync("user-a", "conn-a", Now);

        Assert.True(await number.TryClaimAsync("user-b", "conn-b", Now + ChatGatewayLimits.SignalPendingClaimLifetime));

        var claim = await number.GetAsync();
        Assert.Equal(("conn-b", SignalNumberClaimState.Pending), (claim!.ConnectionId, claim.State));
        // The old connection can no longer activate or release what is no longer its own.
        Assert.False(await number.ActivateAsync("conn-a"));
        await number.ReleaseAsync("conn-a");
        Assert.NotNull(await number.GetAsync());
    }

    [Fact]
    public async Task SameConnection_ReClaims_AndKeepsAnActiveClaimActive()
    {
        var number = Number("+31600000103");
        await number.TryClaimAsync("user-a", "conn-a", Now);
        await number.ActivateAsync("conn-a");

        Assert.True(await number.TryClaimAsync("user-a", "conn-a", Now.AddHours(1)));

        Assert.Equal(SignalNumberClaimState.Active, (await number.GetAsync())!.State);
    }

    [Fact]
    public async Task Release_FreesTheNumber_AndSurvivesReactivation()
    {
        var number = Number("+31600000104");
        await number.TryClaimAsync("user-a", "conn-a", Now);
        await number.ActivateAsync("conn-a");
        await number.AsReference<IGrainManagementExtension>().DeactivateOnIdle();
        Assert.Equal("conn-a", (await Number("+31600000104").GetAsync())!.ConnectionId);

        await Number("+31600000104").ReleaseAsync("conn-a");

        Assert.Null(await Number("+31600000104").GetAsync());
        Assert.True(await Number("+31600000104").TryClaimAsync("user-b", "conn-b", Now));
    }
}
