using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The dream's schedule and run (ADR 0124): the hourly tick that decides whether it is time for tonight's dream, the manual
/// "Dream now", and the run itself: record it as running, digest the finished tasks, call the model unlocked, then take the
/// lessons lock, RE-READ and merge, so a disable or delete the user made during the run survives it.
/// </summary>
internal sealed partial class FleetDreamRunner(
    IFleetStore store,
    FleetOptions options,
    IUserChatClientFactoryProvider chatClients,
    IUserModelCatalogProvider catalogs,
    FleetLessonStore lessonStore,
    FleetProposalWorkflow proposals,
    FleetDreamRegistry running,
    ILogger logger,
    TimeProvider time,
    Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime)
{
    /// <summary>Runs a dream now, for a click on "Dream now": ignores the window and the new-task check, still needs
    /// <c>DreamMinTasks</c>, and uses up tonight's run. Awaits the run.</summary>
    public Task<FleetDreamResult> RunManualAsync(string userId, string? model, UserTelemetryContext? telemetry, CancellationToken cancellationToken = default)
        => ExecuteAsync(userId, FleetDreamTrigger.Manual, model, telemetry, cancellationToken);

    /// <summary>
    /// One hourly tick of the user's reminder: decides whether it is time for tonight's dream, and STARTS it on a background
    /// task, returning at once (a long reminder callback would pass Orleans' call timeout). Outside the window it does nothing
    /// and writes nothing.
    /// </summary>
    public async Task<FleetDreamTick> TickAsync(string userId, UserTelemetryContext? telemetry = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (!options.DreamEnabled)
        {
            return FleetDreamTick.Unavailable;
        }

        // One small blob read per user per hourly tick, even outside the window: the window is the user's own setting.
        var effective = await lessonStore.EffectiveOptionsAsync(userId, cancellationToken);
        if (WindowStart(now, effective) is not { } windowStart)
        {
            return FleetDreamTick.NotYet;
        }

        var document = await lessonStore.GetAsync(userId, cancellationToken);
        if (!document.Settings.Enabled)
        {
            await RecordSkippedOncePerWindowAsync(userId, windowStart, "The nightly dream is switched off.", cancellationToken);
            return FleetDreamTick.Disabled;
        }

        if (document.Settings.LastRunAt >= windowStart || running.IsRunning(userId))
        {
            return FleetDreamTick.Skipped;
        }

        var since = document.Settings.LastRunAt ?? DateTimeOffset.MinValue;
        var finished = (await store.ListTasksAsync(userId, cancellationToken)).Where(FleetDigest.IsFinished).ToList();
        if (NothingToLearn(finished, since, effective) is { } why)
        {
            await RecordSkippedOncePerWindowAsync(userId, windowStart, why, cancellationToken);
            return FleetDreamTick.Skipped;
        }

        // Registered here, not inside the run, so the check above and the registration cannot interleave with another tick.
        var registered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!running.TryAdd(userId, registered))
        {
            return FleetDreamTick.Skipped;
        }

        _ = Task.Run(() => ExecuteQuietlyAsync(userId, telemetry, registered), CancellationToken.None);
        return FleetDreamTick.Started;
    }

    // Why a nightly run would be pointless, or null: too few tasks finished since the last run, or too few to learn from at all.
    private static string? NothingToLearn(List<FleetTaskRecord> finished, DateTimeOffset since, FleetOptions effective)
    {
        var minNew = Math.Max(1, effective.DreamMinNewTasks);
        var fresh = finished.Count(t => (t.FinishedAt ?? t.StartedAt) > since);
        return fresh < minNew ? $"Only {fresh} task(s) finished since the last run; at least {minNew} are needed."
            : finished.Count < Math.Max(1, effective.DreamMinTasks) ? $"Only {finished.Count} finished task(s); at least {Math.Max(1, effective.DreamMinTasks)} are needed."
            : null;
    }

    // The nightly run has no caller to see an exception: a blob error before the run is recorded would otherwise vanish. The next
    // hourly tick retries (LastRunAt is not set yet).
    private async Task ExecuteQuietlyAsync(string userId, UserTelemetryContext? telemetry, TaskCompletionSource registered)
    {
        try
        {
            await ExecuteAsync(userId, FleetDreamTrigger.Nightly, null, telemetry, CancellationToken.None, registered);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The nightly fleet dream failed before it could be recorded");
        }
    }

    /// <summary>The start of the nightly window that contains <paramref name="now"/>, or null outside every window.</summary>
    public DateTimeOffset? WindowStart(DateTimeOffset now, FleetOptions? effective = null)
    {
        var window = effective ?? options;
        var at = TimeOnly.TryParseExact(window.DreamAtUtc, "HH:mm", null, System.Globalization.DateTimeStyles.None, out var parsed) ? parsed : new TimeOnly(1, 0);
        var hours = TimeSpan.FromHours(Math.Max(1, window.DreamWindowHours));
        var utc = now.ToUniversalTime();
        foreach (var day in new[] { utc.Date, utc.Date.AddDays(-1) })
        {
            var start = new DateTimeOffset(day.Add(at.ToTimeSpan()), TimeSpan.Zero);
            if (start <= utc && utc < start + hours)
            {
                return start;
            }
        }

        return null;
    }

    // An idle night is recorded once per window, not once per hourly tick.
    private async Task RecordSkippedOncePerWindowAsync(string userId, DateTimeOffset windowStart, string reason, CancellationToken cancellationToken)
        => await lessonStore.MutateAsync(userId, d =>
        {
            if (d.Runs.FirstOrDefault() is { Status: FleetDreamStatus.Skipped } last && last.StartedAt >= windowStart && last.Reason == reason)
            {
                return d;
            }

            var now = time.GetUtcNow();
            var run = new FleetDreamRun { Id = NewRunId(), Trigger = FleetDreamTrigger.Nightly, StartedAt = now, FinishedAt = now, Status = FleetDreamStatus.Skipped, Reason = reason };
            return d with { Runs = AddRun(d.Runs, run) };
        }, cancellationToken);

    private async Task<FleetDreamResult> ExecuteAsync(
        string userId, string trigger, string? requestedModel, UserTelemetryContext? telemetry, CancellationToken cancellationToken,
        TaskCompletionSource? registered = null)
    {
        if (!options.DreamEnabled)
        {
            return new FleetDreamResult(FleetDreamResultKind.Unavailable, "The dream is switched off on this server.");
        }

        var finished = registered ?? new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (registered is null && !running.TryAdd(userId, finished))
        {
            return new FleetDreamResult(FleetDreamResultKind.Busy, "A dream is already running.");
        }

        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime?.ApplicationStopping ?? CancellationToken.None);
            if (options.DreamTimeout > TimeSpan.Zero)
            {
                deadline.CancelAfter(options.DreamTimeout);
            }

            return await DreamAsync(userId, trigger, requestedModel, telemetry ?? new UserTelemetryContext(userId, null, null, null, null, null), deadline.Token);
        }
        finally
        {
            // The final state is written before the run leaves the registry, so a read that finds it gone sees the result.
            running.Remove(userId);
            finished.TrySetResult();
        }
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    private static IReadOnlyList<FleetDreamRun> AddRun(IReadOnlyList<FleetDreamRun> runs, FleetDreamRun run)
        => [.. runs.Prepend(run).Take(FleetLessons.MaxRuns)];
}
