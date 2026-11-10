using Agent42.Agents;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;
using Orleans.Core.Internal;

namespace Agent42.Tests;

/// <summary>The nightly dream's schedule grain (ADR 0124): one hourly reminder, and a typed tick result that decides whether it lives on.
/// The port is the per-silo <see cref="FakeFleetLauncher"/>, so every silo is set and counted.</summary>
public sealed class FleetDreamGrainTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    private IEnumerable<FakeFleetLauncher> Launchers()
        => Cluster.Silos.Select(silo => Cluster.GetSiloServiceProvider(silo.SiloAddress).GetRequiredService<FakeFleetLauncher>());

    private void SetTick(FleetDreamTick tick)
    {
        foreach (var launcher in Launchers())
        {
            launcher.NextTick = tick;
        }
    }

    private int Ticks() => Launchers().Sum(l => l.DreamTicks);

    private IFleetDreamGrain Grain(string user) => Cluster.GrainFactory.GetGrain<IFleetDreamGrain>(user);

    [Fact]
    public async Task Schedule_RegistersExactlyOneHourlyReminder_Idempotently_AndUnscheduleRemovesIt()
    {
        var user = $"dream-{Guid.NewGuid():N}";

        await Grain(user).ScheduleAsync();
        await Grain(user).ScheduleAsync();
        Assert.Equal(1, await ReminderCount(user));

        await Grain(user).UnscheduleAsync();
        Assert.Equal(0, await ReminderCount(user));
        await Grain(user).UnscheduleAsync(); // nothing to remove, no error
    }

    [Fact]
    public async Task ATick_CallsTheLauncherOnce()
    {
        var user = $"dream-{Guid.NewGuid():N}";
        SetTick(FleetDreamTick.NotYet);
        var before = Ticks();

        await Grain(user).AsReference<IRemindable>().ReceiveReminder(FleetDreamGrain.ReminderName, default);

        Assert.Equal(1, Ticks() - before);
    }

    [Theory]
    [InlineData(FleetDreamTick.Started, 1)]
    [InlineData(FleetDreamTick.NotYet, 1)]
    [InlineData(FleetDreamTick.Skipped, 1)]
    [InlineData(FleetDreamTick.Disabled, 0)]
    [InlineData(FleetDreamTick.Unavailable, 0)]
    public async Task OnlyDisabledAndUnavailableUnregisterTheReminder(FleetDreamTick tick, int remaining)
    {
        var user = $"dream-{Guid.NewGuid():N}";
        await Grain(user).ScheduleAsync();
        SetTick(tick);

        await Grain(user).AsReference<IRemindable>().ReceiveReminder(FleetDreamGrain.ReminderName, default);

        Assert.Equal(remaining, await ReminderCount(user));
    }

    [Fact]
    public async Task AnotherReminderName_IsIgnored()
    {
        var user = $"dream-{Guid.NewGuid():N}";
        var before = Ticks();

        await Grain(user).AsReference<IRemindable>().ReceiveReminder("something-else", default);

        Assert.Equal(before, Ticks());
    }

    // The grain's reminders, read from the silo's reminder table (in-memory in every test silo).
    private async Task<int> ReminderCount(string user)
    {
        var table = Cluster.GetSiloServiceProvider(Cluster.Silos[0].SiloAddress).GetRequiredService<IReminderTable>();
        var rows = await table.ReadRows(Grain(user).GetGrainId());
        return rows.Reminders.Count;
    }
}
