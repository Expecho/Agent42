using Microsoft.Extensions.Logging;
using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// One user's nightly dream schedule (ADR 0124), keyed by user id: an Orleans reminder that ticks HOURLY. The grain holds no
/// state, the reminder IS the state. A reminder fires once cluster-wide and is persisted, which a timer in each replica of a
/// revision rollover would not be (CLAUDE.md rule 12). It ticks hourly, not nightly, because Orleans skips ticks missed while
/// the app was down and every push to main deploys: the Backend decides on each tick whether it is time.
/// </summary>
[Alias("Agent42.Agents.IFleetDreamGrain")]
public interface IFleetDreamGrain : IGrainWithStringKey
{
    /// <summary>Registers the hourly reminder. Idempotent: calling it again changes nothing.</summary>
    [Alias("ScheduleAsync")]
    Task ScheduleAsync();

    [Alias("UnscheduleAsync")]
    Task UnscheduleAsync();
}

/// <summary>The grain behind <see cref="IFleetDreamGrain"/>. It reaches the Backend only through <see cref="IFleetLauncher"/>.</summary>
public sealed class FleetDreamGrain(IFleetLauncher launcher, ILogger<FleetDreamGrain> logger) : Grain, IFleetDreamGrain, IRemindable
{
    public const string ReminderName = "fleet-dream";

    public static readonly TimeSpan Period = TimeSpan.FromHours(1);

    public async Task ScheduleAsync()
        => await this.RegisterOrUpdateReminder(ReminderName, TimeSpan.FromMinutes(1), Period);

    public async Task UnscheduleAsync()
    {
        if (await this.GetReminder(ReminderName) is { } reminder)
        {
            await this.UnregisterReminder(reminder);
        }
    }

    public async Task ReceiveReminder(string reminderName, TickStatus status)
    {
        if (reminderName != ReminderName)
        {
            return;
        }

        try
        {
            var tick = await launcher.RunNightlyDreamAsync(this.GetPrimaryKeyString());
            // A launcher that can't run the dream, or a user who switched it off, ends the schedule: no ticking forever.
            if (tick is FleetDreamTick.Disabled or FleetDreamTick.Unavailable)
            {
                await UnscheduleAsync();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The fleet dream tick failed for a user; the reminder stays");
        }
    }
}
