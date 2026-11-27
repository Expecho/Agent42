using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Every read and write of the user's lessons document (ADR 0124), and the per-user lock that serialises them. A
/// <see cref="SemaphoreSlim"/> per user guards short read-modify-writes ONLY, with one exception: approving a proposal
/// (ADR 0143/0146, <see cref="FleetProposalWorkflow"/>) holds it across reading and writing both <c>lessons.json</c> and
/// <c>prompts.json</c>, through <see cref="WithUserLockAsync{T}"/>. The store has no ETag, so a write on the other replica
/// of a rollover can still be lost. A run entry stored as <c>running</c> that is not in the registry was running when the app
/// stopped: it is rewritten as <c>interrupted</c> when the document is read, under the lock with a re-read.
/// </summary>
internal sealed class FleetLessonStore(
    IFleetStore store,
    FleetOptions options,
    FleetSettingsService? settings,
    FleetDreamRegistry running,
    ILogger logger,
    TimeProvider time)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    public FleetOptions Options => options;

    /// <summary>The options a user's dream runs on (ADR 0135): the operator's with the user's overrides.</summary>
    public async Task<FleetOptions> EffectiveOptionsAsync(string userId, CancellationToken cancellationToken = default)
        => settings is null ? options : await settings.ResolveAsync(userId, cancellationToken);

    /// <summary>The user's lessons document. A run entry that was running when the app stopped reads as interrupted.</summary>
    public async Task<FleetLessonsDocument> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var document = await store.GetLessonsAsync(userId, cancellationToken);
        return !HasOrphanedRun(userId, document) ? document : await NormalizeAsync(userId, cancellationToken);
    }

    /// <summary>The lessons a planner of this user gets now. Fails open: a failing read logs a warning and returns none.</summary>
    public async Task<IReadOnlyList<FleetLesson>> ActiveAsync(string userId, CancellationToken cancellationToken = default, FleetOptions? effective = null)
    {
        try
        {
            return FleetLessons.Active(await store.GetLessonsAsync(userId, cancellationToken), time.GetUtcNow(), effective ?? options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleet lessons could not be read; the run goes without them");
            return [];
        }
    }

    private bool HasOrphanedRun(string userId, FleetLessonsDocument document)
        => !running.IsRunning(userId) && document.Runs.Any(r => r.Status == FleetDreamStatus.Running);

    private async Task<FleetLessonsDocument> NormalizeAsync(string userId, CancellationToken cancellationToken)
        => await MutateAsync(userId, document =>
        {
            // Re-read under the lock: a run that finished just now has written its final state, and a stale read must not
            // overwrite that with interrupted.
            if (!HasOrphanedRun(userId, document))
            {
                return document;
            }

            var now = time.GetUtcNow();
            return document with
            {
                Runs = [.. document.Runs.Select(r => r.Status != FleetDreamStatus.Running ? r
                    : r with { Status = FleetDreamStatus.Interrupted, Reason = "The app stopped while this dream ran.", FinishedAt = now })],
            };
        }, cancellationToken);

    /// <summary>Saves the toggle and the (already validated) model.</summary>
    public async Task<FleetLessonSettings> SaveSettingsAsync(string userId, bool enabled, string? model, CancellationToken cancellationToken = default)
        => (await MutateAsync(userId, d => d with { Settings = d.Settings with { Enabled = enabled, Model = model } }, cancellationToken)).Settings;

    public async Task MarkSeenAsync(string userId, CancellationToken cancellationToken = default)
        => await MutateAsync(userId, d => d with { Settings = d.Settings with { SeenAt = time.GetUtcNow() } }, cancellationToken);

    /// <summary>Turns a lesson off; it stays as a tombstone so it is not proposed again. False when it is not the user's.</summary>
    public Task<bool> DisableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, l => l with { Status = FleetLessonStatus.Disabled }, cancellationToken);

    /// <summary>Turns a disabled lesson back on, and renews it so it does not expire at once. False when it is not the user's.</summary>
    public Task<bool> EnableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, l => l with { Status = FleetLessonStatus.Active, LastSupportedAt = time.GetUtcNow() }, cancellationToken);

    /// <summary>Deletes a lesson (a deleted one can be learned again). False when it is not the user's.</summary>
    public Task<bool> DeleteAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => EditLessonAsync(userId, lessonId, null, cancellationToken);

    /// <summary>
    /// Records that a task's planner got these lessons: appends the task to each lesson's applications (the last 20) and bumps
    /// its count, once per task. A lesson that was deleted meanwhile is skipped.
    /// </summary>
    public async Task RecordApplicationAsync(string userId, string taskId, IReadOnlyList<string> lessonIds, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
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

    // Reads, applies and writes the document under the user's lock. Writes nothing when nothing changed.
    internal Task<FleetLessonsDocument> MutateAsync(string userId, Func<FleetLessonsDocument, FleetLessonsDocument> change, CancellationToken cancellationToken)
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
    internal async Task<T> WithUserLockAsync<T>(string userId, Func<Task<T>> body, CancellationToken cancellationToken)
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
}
