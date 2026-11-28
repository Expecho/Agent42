using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The generic pushes a fleet task sends on its own (ADR 0154): one when it ends for a reason the user did not cause, one when
/// it goes quiet. The texts are constants on purpose: no request, result, reason or exception text ever goes to the push
/// service (ADR 0013). Both methods swallow everything: a faulty sender must not change how a task ended, and the stuck
/// push is fire-and-forget, so an exception there would be an unobserved faulted task.
/// </summary>
public sealed class FleetTaskNotifier(IPushNotificationSender sender, ILogger logger)
{
    public const string Title = "Agent 42 fleet";
    public const string DoneText = "A fleet task finished.";
    public const string FailedText = "A fleet task failed.";
    public const string LimitText = "A fleet task stopped: its limit was reached.";
    public const string StuckText = "A fleet task has gone quiet. It may be stuck.";

    /// <summary>The text for a final task, or null when no push is due (the rules of ADR 0154).</summary>
    public static string? TerminalText(FleetTaskRecord final, int modelPushes)
    {
        if (final.OriginConversationId is not null)
        {
            return null; // its background turn pushes (ADR 0115)
        }

        return final.Status switch
        {
            FleetTaskStatus.Done => modelPushes > 0 ? null : DoneText,
            FleetTaskStatus.Failed => FailedText,
            FleetTaskStatus.Stopped => final.Reason == FleetBudget.UserStopReason ? null : LimitText,
            _ => null, // interrupted, or not final
        };
    }

    public async Task NotifyFinishedAsync(FleetTaskRecord final, int modelPushes)
    {
        if (TerminalText(final, modelPushes) is { } text)
        {
            await SendAsync(final.UserId, final.Id, text);
        }
    }

    public Task NotifyStuckAsync(string userId, string taskId) => SendAsync(userId, taskId, StuckText);

    private async Task SendAsync(string userId, string taskId, string text)
    {
        try
        {
            await sender.SendAsync(userId, new PushMessage(Title, text, "", FleetPushTools.TaskUrl(taskId)));
        }
        catch (Exception ex)
        {
            // The task id only, never text.
            logger.LogWarning(ex, "Fleet task {TaskId}: its push could not be sent", taskId);
        }
    }
}

/// <summary>
/// One running task's stuck state (ADR 0154): quiet for <c>Fleet:StuckAfter</c> with no open question fires once per quiet
/// spell, an event re-arms it, and a task fires at most <see cref="MaxPerTask"/> times. Called from the task's one flush loop.
/// </summary>
public sealed class FleetStuckWatch
{
    public const int MaxPerTask = 2;

    private DateTimeOffset _firedFor = DateTimeOffset.MinValue;
    private int _fired;

    public bool ShouldFire(DateTimeOffset now, DateTimeOffset lastEventAt, TimeSpan after, bool questionOpen)
    {
        if (after <= TimeSpan.Zero || questionOpen || _fired >= MaxPerTask || lastEventAt == _firedFor || now - lastEventAt < after)
        {
            return false;
        }

        _firedFor = lastEventAt;
        _fired++;
        return true;
    }
}
