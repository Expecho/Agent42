using System.Collections.Concurrent;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>How an explicit dream run ended, for the route that asked for it.</summary>
public enum FleetDreamResultKind
{
    /// <summary>The run happened (it may have been recorded as skipped or failed; see the run).</summary>
    Ran,

    /// <summary>A dream for this user is already running.</summary>
    Busy,

    /// <summary>The model is not one of the user's tool-capable models.</summary>
    BadModel,

    /// <summary><c>Fleet:DreamEnabled</c> is off.</summary>
    Unavailable,
}

/// <summary>The outcome of <see cref="FleetLessonService.RunManualAsync"/>: its kind, an error text when it did not run, and the run.</summary>
public sealed record FleetDreamResult(FleetDreamResultKind Kind, string? Error = null, FleetDreamRun? Run = null);

/// <summary>How an approve or dismiss of a prompt proposal ended (ADR 0143).</summary>
public enum FleetProposalOutcome
{
    Done,

    /// <summary>No such proposal for this user.</summary>
    NotFound,

    /// <summary>Already decided, or the prompt changed so the edit no longer applies.</summary>
    Conflict,
}

/// <summary>The result of <see cref="FleetLessonService.ApproveProposalAsync"/> or <see cref="FleetLessonService.DismissProposalAsync"/>:
/// the outcome, an error text, and the prompt key an approve changed.</summary>
public sealed record FleetProposalDecision(FleetProposalOutcome Outcome, string? Error = null, string? PromptKey = null, string? SkillName = null);

/// <summary>
/// The fleet's lessons (ADR 0124): the nightly "dream" that distills a user's recent finished tasks into a few lessons
/// about team design, and every read and write of the lessons document. In-process like the fleet (ADR 0097, rule 12): the
/// per-user lock and the registry of running dreams rely on the single replica.
/// <para>
/// A per-user <see cref="SemaphoreSlim"/> guards short read-modify-writes ONLY, with one exception: approving a prompt
/// proposal (ADR 0143) holds it across reading and writing both <c>lessons.json</c> and <c>prompts.json</c>. A run reads the document without the lock,
/// calls the model unlocked, and then takes the lock, RE-READS and merges into the fresh document, so a disable or delete the
/// user made during the run survives it and nothing blocks for minutes. The store has no ETag, so a write on the other
/// replica of a rollover can still be lost (ADR 0124 says so). A run entry stored as <c>running</c> that is not in the
/// registry was running when the app stopped: it is rewritten as <c>interrupted</c> when the document is read, under the
/// lock with a re-read (the pattern of <see cref="FleetTaskService"/>).
/// </para>
/// </summary>
public sealed partial class FleetLessonService(
    IFleetStore store,
    FleetOptions options,
    IUserChatClientFactoryProvider chatClients,
    IUserModelCatalogProvider catalogs,
    ILogger<FleetLessonService> logger,
    TimeProvider? time = null,
    Microsoft.Extensions.Hosting.IHostApplicationLifetime? lifetime = null,
    FleetSettingsService? settings = null,
    UserSkillInstaller? skills = null)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    // The running dreams: the value completes when the run has written its final state and left the registry.
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _running = new(StringComparer.Ordinal);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public FleetOptions Options => options;

    /// <summary>The options a user's dream runs on (ADR 0135): the operator's with the user's overrides. The operator-only
    /// fields (<c>DreamEnabled</c>, <c>DreamTimeout</c>) are read from <see cref="Options"/> on purpose.</summary>
    public async Task<FleetOptions> EffectiveOptionsAsync(string userId, CancellationToken cancellationToken = default)
        => settings is null ? options : await settings.ResolveAsync(userId, cancellationToken);

    // ---- Reads ----

    /// <summary>The user's lessons document. A run entry that was running when the app stopped reads as interrupted.</summary>
    public async Task<FleetLessonsDocument> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetLessonsAsync(userId, cancellationToken);
        return !HasOrphanedRun(userId, document) ? document : await NormalizeAsync(userId, cancellationToken);
    }

    /// <summary>Lessons and pending prompt proposals created after the user last opened the dialog, for the badge. A proposal
    /// that stays pending stops counting once seen; the Lessons view shows how many are waiting.</summary>
    public static int NewCount(FleetLessonsDocument document)
    {
        var seen = document.Settings.SeenAt ?? DateTimeOffset.MinValue;
        return document.Lessons.Count(l => l.Status == FleetLessonStatus.Active && l.CreatedAt > seen)
            + document.PromptProposals.Count(p => p.Status == FleetPromptProposalStatus.Pending && p.CreatedAt > seen)
            + document.SkillProposals.Count(p => p.Status == FleetPromptProposalStatus.Pending && p.CreatedAt > seen);
    }

    /// <summary>The lessons a planner of this user gets now. Fails open: a failing read logs a warning and returns none.</summary>
    public async Task<IReadOnlyList<FleetLesson>> ActiveAsync(string userId, CancellationToken cancellationToken = default, FleetOptions? effective = null)
    {
        try
        {
            return FleetLessons.Active(await store.GetLessonsAsync(userId, cancellationToken), _time.GetUtcNow(), effective ?? options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleet lessons could not be read; the run goes without them");
            return [];
        }
    }

    /// <summary>Whether a dream of this user is running in this process.</summary>
    public bool IsRunning(string userId) => _running.ContainsKey(userId);

    /// <summary>Completes when the user's running dream, if any, has finished and written its final state.</summary>
    public Task WhenIdleAsync(string userId) => _running.TryGetValue(userId, out var run) ? run.Task : Task.CompletedTask;

    private bool HasOrphanedRun(string userId, FleetLessonsDocument document)
        => !_running.ContainsKey(userId) && document.Runs.Any(r => r.Status == FleetDreamStatus.Running);

    private async Task<FleetLessonsDocument> NormalizeAsync(string userId, CancellationToken cancellationToken)
        => await MutateAsync(userId, document =>
        {
            // Re-read under the lock: a run that finished just now has written its final state, and a stale read must not
            // overwrite that with interrupted.
            if (!HasOrphanedRun(userId, document))
            {
                return document;
            }

            var now = _time.GetUtcNow();
            return document with
            {
                Runs = [.. document.Runs.Select(r => r.Status != FleetDreamStatus.Running ? r
                    : r with { Status = FleetDreamStatus.Interrupted, Reason = "The app stopped while this dream ran.", FinishedAt = now })],
            };
        }, cancellationToken);

    // ---- Settings and lesson edits (short read-modify-writes under the lock) ----

    /// <summary>Saves the toggle and the model. A model that is not one of the user's tool-capable models is refused.</summary>
    public async Task<(FleetLessonSettings? Settings, string? Error)> SetSettingsAsync(
        string userId, bool enabled, string? model, CancellationToken cancellationToken = default)
    {
        var chosen = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (chosen is not null && await PickModelAsync(userId, chosen, null, cancellationToken) is { Error: { } error })
        {
            return (null, error);
        }

        var saved = await MutateAsync(userId, d => d with { Settings = d.Settings with { Enabled = enabled, Model = chosen } }, cancellationToken);
        return (saved.Settings, null);
    }

    public async Task MarkSeenAsync(string userId, CancellationToken cancellationToken = default)
        => await MutateAsync(userId, d => d with { Settings = d.Settings with { SeenAt = _time.GetUtcNow() } }, cancellationToken);

    /// <summary>Turns a lesson off; it stays as a tombstone so it is not proposed again. False when it is not the user's.</summary>
    public Task<bool> DisableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, l => l with { Status = FleetLessonStatus.Disabled }, cancellationToken);

    /// <summary>Turns a disabled lesson back on, and renews it so it does not expire at once. False when it is not the user's.</summary>
    public Task<bool> EnableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, l => l with { Status = FleetLessonStatus.Active, LastSupportedAt = _time.GetUtcNow() }, cancellationToken);

    /// <summary>Deletes a lesson (a deleted one can be learned again). False when it is not the user's.</summary>
    public Task<bool> DeleteAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, null, cancellationToken);

    /// <summary>
    /// Records that a task's planner got these lessons: appends the task to each lesson's applications (the last 20) and bumps
    /// its count, once per task. A lesson that was deleted meanwhile is skipped.
    /// </summary>
    public async Task RecordApplicationAsync(string userId, string taskId, IReadOnlyList<string> lessonIds, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        await MutateAsync(userId, d => d with
        {
            Lessons = [.. d.Lessons.Select(l => lessonIds.Contains(l.Id) && l.Applications.All(a => a.TaskId != taskId)
                ? l with
                {
                    AppliedCount = l.AppliedCount + 1,
                    Applications = [.. l.Applications.Append(new FleetLessonApplication(taskId, now)).TakeLast(FleetLessons.MaxApplications)],
                }
                : l)],
        }, cancellationToken);
    }

    private async Task<bool> EditLessonAsync(string userId, string lessonId, Func<FleetLesson, FleetLesson>? edit, CancellationToken cancellationToken)
    {
        var found = false;
        await MutateAsync(userId, d =>
        {
            found = d.Lessons.Any(l => l.Id == lessonId);
            return d with
            {
                Lessons = [.. d.Lessons.Where(l => l.Id != lessonId || edit is not null).Select(l => l.Id == lessonId && edit is not null ? edit(l) : l)],
            };
        }, cancellationToken);
        return found;
    }

    // ---- Prompt proposals (ADR 0143) ----

    /// <summary>The user's prompts as they are now: the defaults with the user's overrides.</summary>
    public async Task<FleetPromptSet> PromptsAsync(string userId, CancellationToken cancellationToken = default)
        => new(await store.GetPromptsAsync(userId, cancellationToken));

    /// <summary>
    /// Approves a pending prompt proposal: re-applies its edit to the user's CURRENT prompt and saves that as the user's own
    /// copy (<see cref="FleetPrompts.WithOverride"/>), then marks it approved. A proposal whose passage is gone is marked
    /// outdated and is a conflict. The whole body runs under the user's lock, so a double click gets a conflict and the prompt
    /// changes once. <c>prompts.json</c> itself has no lock: a prompt-editor save at the same moment can still race (ADR 0143).
    /// </summary>
    public Task<FleetProposalDecision> ApproveProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => WithUserLockAsync(userId, async () =>
        {
            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (PendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            var proposal = document.PromptProposals.First(p => p.Id == proposalId);
            var overrides = await store.GetPromptsAsync(userId, cancellationToken);
            var applied = FleetPromptProposals.Apply(new FleetPromptSet(overrides).Get(proposal.PromptKey), proposal);
            if (!applied.Ok)
            {
                await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Outdated), cancellationToken);
                return new FleetProposalDecision(FleetProposalOutcome.Conflict, $"The prompt changed since this was proposed: {applied.Problem}");
            }

            if (!applied.AlreadyApplied)
            {
                await store.SetPromptsAsync(userId, FleetPrompts.WithOverride(overrides, proposal.PromptKey, applied.Text), cancellationToken);
            }

            await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Approved), cancellationToken);
            return new FleetProposalDecision(FleetProposalOutcome.Done, null, proposal.PromptKey);
        }, cancellationToken);

    /// <summary>Dismisses a pending prompt proposal; it stays as a tombstone so the same change is not proposed again.</summary>
    public Task<FleetProposalDecision> DismissProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => WithUserLockAsync(userId, async () =>
        {
            var document = await store.GetLessonsAsync(userId, cancellationToken);
            if (PendingProblem(document, proposalId) is { } problem)
            {
                return problem;
            }

            await store.SetLessonsAsync(userId, Decide(document, proposalId, FleetPromptProposalStatus.Dismissed), cancellationToken);
            return new FleetProposalDecision(FleetProposalOutcome.Done);
        }, cancellationToken);

    private static FleetProposalDecision? PendingProblem(FleetLessonsDocument document, string proposalId)
        => document.PromptProposals.FirstOrDefault(p => p.Id == proposalId) switch
        {
            null => new FleetProposalDecision(FleetProposalOutcome.NotFound, "No such proposal."),
            { Status: FleetPromptProposalStatus.Pending } => null,
            var decided => new FleetProposalDecision(FleetProposalOutcome.Conflict, $"This proposal was already {decided.Status}."),
        };

    private FleetLessonsDocument Decide(FleetLessonsDocument document, string proposalId, string status)
    {
        var now = _time.GetUtcNow();
        return document with
        {
            PromptProposals = [.. document.PromptProposals.Select(p => p.Id == proposalId ? p with { Status = status, DecidedAt = now } : p)],
        };
    }

    // Reads, applies and writes the document under the user's lock. Writes nothing when nothing changed.
    private Task<FleetLessonsDocument> MutateAsync(string userId, Func<FleetLessonsDocument, FleetLessonsDocument> change, CancellationToken cancellationToken)
        => WithUserLockAsync(userId, async () =>
        {
            var current = await store.GetLessonsAsync(userId, cancellationToken);
            var next = change(current);
            if (!ReferenceEquals(next, current))
            {
                await store.SetLessonsAsync(userId, next, cancellationToken);
            }

            return next;
        }, cancellationToken);

    // The user's lock around body. MutateAsync's short read-modify-writes use it, and approve, which is the one caller that
    // holds it across two Blob reads and two writes.
    private async Task<T> WithUserLockAsync<T>(string userId, Func<Task<T>> body, CancellationToken cancellationToken)
    {
        var gate = _locks.GetOrAdd(userId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await body();
        }
        finally
        {
            gate.Release();
        }
    }

    // ---- The dream ----

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
        var now = _time.GetUtcNow();
        if (!options.DreamEnabled)
        {
            return FleetDreamTick.Unavailable;
        }

        // One small blob read per user per hourly tick, even outside the window: the window is the user's own setting.
        var effective = await EffectiveOptionsAsync(userId, cancellationToken);
        if (WindowStart(now, effective) is not { } windowStart)
        {
            return FleetDreamTick.NotYet;
        }

        var document = await GetAsync(userId, cancellationToken);
        if (!document.Settings.Enabled)
        {
            await RecordSkippedOncePerWindowAsync(userId, windowStart, "The nightly dream is switched off.", cancellationToken);
            return FleetDreamTick.Disabled;
        }

        if (document.Settings.LastRunAt >= windowStart || IsRunning(userId))
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
        if (!_running.TryAdd(userId, registered))
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
        => await MutateAsync(userId, d =>
        {
            if (d.Runs.FirstOrDefault() is { Status: FleetDreamStatus.Skipped } last && last.StartedAt >= windowStart && last.Reason == reason)
            {
                return d;
            }

            var now = _time.GetUtcNow();
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
        if (registered is null && !_running.TryAdd(userId, finished))
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
            _running.TryRemove(userId, out _);
            finished.TrySetResult();
        }
    }

    private async Task<FleetDreamResult> DreamAsync(string userId, string trigger, string? requestedModel, UserTelemetryContext telemetry, CancellationToken cancellationToken)
    {
        var effective = await EffectiveOptionsAsync(userId, cancellationToken);
        var lessonSettings = (await store.GetLessonsAsync(userId, cancellationToken)).Settings;
        var (model, modelError) = await PickModelAsync(userId, requestedModel, lessonSettings.Model, cancellationToken);
        if (modelError is not null && trigger == FleetDreamTrigger.Manual)
        {
            return new FleetDreamResult(FleetDreamResultKind.BadModel, modelError);
        }

        var finished = await FinishedTasksAsync(userId, cancellationToken);
        var minTasks = Math.Max(1, effective.DreamMinTasks);
        if (modelError is null && finished.Count < minTasks)
        {
            // Too little to learn from: recorded, but it does not use up the night (LastRunAt stays).
            var skipped = await RecordSkippedAsync(userId, trigger, $"Only {finished.Count} finished task(s); at least {minTasks} are needed.", cancellationToken);
            return new FleetDreamResult(FleetDreamResultKind.Ran, Run: skipped);
        }

        var (run, previousRunAt) = await StartRunAsync(userId, trigger, model);
        try
        {
            var progress = modelError is not null
                ? new DreamProgress(FleetDreamStatus.Failed, modelError)
                : await DreamOverAsync(userId, finished, previousRunAt, model!, telemetry, effective, cancellationToken);
            return new FleetDreamResult(FleetDreamResultKind.Ran, Run: await FinishRunAsync(userId, run, progress, effective));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            logger.LogWarning(ex, "Fleet dream for a user failed");
            var failed = new DreamProgress(FleetDreamStatus.Failed, ex is OperationCanceledException ? "The dream was cancelled or timed out." : $"The dream failed: {FleetAnalyzerLimits.Short(ex.Message, 200)}");
            return new FleetDreamResult(FleetDreamResultKind.Ran, Run: await FinishRunAsync(userId, run, failed, effective));
        }
    }

    private async Task<List<FleetTaskRecord>> FinishedTasksAsync(string userId, CancellationToken cancellationToken)
        => [.. (await store.ListTasksAsync(userId, cancellationToken)).Where(FleetDigest.IsFinished).OrderByDescending(t => t.FinishedAt ?? t.StartedAt)];

    // The digest and the model run, after the run is recorded as running.
    private async Task<DreamProgress> DreamOverAsync(
        string userId, List<FleetTaskRecord> finished, DateTimeOffset? previousRunAt, string model, UserTelemetryContext telemetry, FleetOptions effective, CancellationToken cancellationToken)
    {
        var digest = await FleetDigest.BuildAsync(store, userId, finished, effective, logger, cancellationToken);
        var prompts = new FleetPromptSet(await store.GetPromptsAsync(userId, cancellationToken));
        // Unlocked, like the rest of the run: the merge re-checks the stored proposals under the lock (ADR 0143).
        var lessons = await store.GetLessonsAsync(userId, cancellationToken);
        var installed = await SkillSnapshotsAsync(userId, cancellationToken);
        var client = (await chatClients.GetForUserAsync(userId, cancellationToken)).GetClient(model);
        var outcome = await new FleetDreamer(effective, prompts, digest, logger, lessons.PromptProposals, installed, lessons.SkillProposals)
            .RunAsync(client, telemetry, cancellationToken);
        var since = previousRunAt ?? DateTimeOffset.MinValue;
        return new DreamProgress(outcome.Status, outcome.Reason)
        {
            TasksRead = digest.TasksRead,
            NewTasks = finished.Count(t => (t.FinishedAt ?? t.StartedAt) > since),
            Outcome = outcome,
        };
    }

    private sealed record DreamProgress(string Status, string? Reason)
    {
        public int TasksRead { get; init; }
        public int NewTasks { get; init; }
        public FleetDreamOutcome? Outcome { get; init; }
    }

    // Appends a skipped run without touching LastRunAt.
    private async Task<FleetDreamRun> RecordSkippedAsync(string userId, string trigger, string reason, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        var run = new FleetDreamRun { Id = NewRunId(), Trigger = trigger, StartedAt = now, FinishedAt = now, Status = FleetDreamStatus.Skipped, Reason = reason };
        await MutateAsync(userId, d => d with { Runs = AddRun(d.Runs, run) }, cancellationToken);
        return run;
    }

    // Writes the run as running and sets LastRunAt, under the lock, before any model call, so "interrupted" can see it.
    // Returns the LastRunAt it replaced, which is what "new tasks" counts from.
    private async Task<(FleetDreamRun Run, DateTimeOffset? PreviousRunAt)> StartRunAsync(string userId, string trigger, string? model)
    {
        var now = _time.GetUtcNow();
        var run = new FleetDreamRun { Id = NewRunId(), Trigger = trigger, StartedAt = now, Status = FleetDreamStatus.Running, Model = model };
        DateTimeOffset? previous = null;
        await MutateAsync(userId, d =>
        {
            previous = d.Settings.LastRunAt;
            return d with { Settings = d.Settings with { LastRunAt = now }, Runs = AddRun(d.Runs, run) };
        }, CancellationToken.None);
        return (run, previous);
    }

    // Takes the lock, RE-READS, and merges the accepted lessons into the fresh document: a disable or delete the user made
    // while the model ran survives, and a lesson the user disabled is not learned again. The prompt proposals merge the same
    // way, with or without lessons and on a failed run too (fail open: what was accepted still counts), re-checked against
    // the fresh document so a dismiss made during the run survives (ADR 0143).
    private async Task<FleetDreamRun> FinishRunAsync(string userId, FleetDreamRun run, DreamProgress progress, FleetOptions effective)
    {
        var now = _time.GetUtcNow();
        var final = run;
        await MutateAsync(userId, d =>
        {
            var (document, merged) = MergeRun(d, run, progress, now, effective);
            final = merged;
            return document;
        }, CancellationToken.None);
        return final;
    }

    // The run's accepted lessons, prompt proposals and skill proposals merged into the fresh document, and the finished run entry.
    private static (FleetLessonsDocument Document, FleetDreamRun Run) MergeRun(
        FleetLessonsDocument d, FleetDreamRun run, DreamProgress progress, DateTimeOffset now, FleetOptions effective)
    {
        var merge = progress.Outcome is { Accepted.Count: > 0 } outcome
            ? FleetLessons.Merge(d, outcome.Accepted, now, effective)
            : new FleetLessonMerge(FleetLessons.Expire(d, now, effective), 0, 0, 0);
        var proposals = FleetPromptProposals.Merge(merge.Document, progress.Outcome?.Proposals ?? [], now, effective);
        var skillProposals = FleetSkillProposals.Merge(proposals.Document, progress.Outcome?.SkillProposals ?? [], now, effective);
        var final = run with
        {
            Status = progress.Status,
            Reason = progress.Reason,
            FinishedAt = now,
            TasksRead = progress.TasksRead,
            NewTasks = progress.NewTasks,
            Learned = merge.Learned,
            Reinforced = merge.Reinforced,
            Refused = merge.Refused + (progress.Outcome?.Refused ?? 0),
            InputTokens = progress.Outcome?.InputTokens,
            OutputTokens = progress.Outcome?.OutputTokens,
            Proposed = proposals.Proposed,
            SkillsProposed = skillProposals.Proposed,
        };
        return (skillProposals.Document with { Runs = [.. skillProposals.Document.Runs.Select(r => r.Id == run.Id ? final : r)] }, final);
    }

    // The model: the request's, else the setting's, else the catalog default; it must take tools and be a chat model, never a
    // Copilot-only one (the rule FleetTaskService.SubmitAsync applies).
    private async Task<(string? Model, string? Error)> PickModelAsync(string userId, string? requested, string? configured, CancellationToken cancellationToken)
    {
        var catalog = await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken);
        var model = !string.IsNullOrWhiteSpace(requested) ? requested : !string.IsNullOrWhiteSpace(configured) ? configured : catalog.Default;
        if (catalog.Models.Any(m => m.Id.Equals(model, StringComparison.Ordinal) && FleetTaskService.IsToolCapable(m)))
        {
            return (model, null);
        }

        return (model, string.IsNullOrWhiteSpace(requested) && !string.IsNullOrWhiteSpace(configured)
            ? $"The dream's model '{model}' is no longer one of your models that supports tools."
            : "Pick one of your models that supports tools.");
    }

    private static string NewRunId() => Guid.NewGuid().ToString("N")[..12];

    private static IReadOnlyList<FleetDreamRun> AddRun(IReadOnlyList<FleetDreamRun> runs, FleetDreamRun run)
        => [.. runs.Prepend(run).Take(FleetLessons.MaxRuns)];
}
