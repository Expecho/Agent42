using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

internal sealed partial class FleetDreamRunner
{
    private async Task<FleetDreamResult> DreamAsync(string userId, string trigger, string? requestedModel, UserTelemetryContext telemetry, CancellationToken cancellationToken)
    {
        var effective = await lessonStore.EffectiveOptionsAsync(userId, cancellationToken);
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
        var installed = await proposals.SkillSnapshotsAsync(userId, cancellationToken);
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
        var now = time.GetUtcNow();
        var run = new FleetDreamRun { Id = NewRunId(), Trigger = trigger, StartedAt = now, FinishedAt = now, Status = FleetDreamStatus.Skipped, Reason = reason };
        await lessonStore.MutateAsync(userId, d => d with { Runs = AddRun(d.Runs, run) }, cancellationToken);
        return run;
    }

    // Writes the run as running and sets LastRunAt, under the lock, before any model call, so "interrupted" can see it.
    // Returns the LastRunAt it replaced, which is what "new tasks" counts from.
    private async Task<(FleetDreamRun Run, DateTimeOffset? PreviousRunAt)> StartRunAsync(string userId, string trigger, string? model)
    {
        var now = time.GetUtcNow();
        var run = new FleetDreamRun { Id = NewRunId(), Trigger = trigger, StartedAt = now, Status = FleetDreamStatus.Running, Model = model };
        DateTimeOffset? previous = null;
        await lessonStore.MutateAsync(userId, d =>
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
        var now = time.GetUtcNow();
        var final = run;
        await lessonStore.MutateAsync(userId, d =>
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
    internal async Task<(string? Model, string? Error)> PickModelAsync(string userId, string? requested, string? configured, CancellationToken cancellationToken)
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
}
