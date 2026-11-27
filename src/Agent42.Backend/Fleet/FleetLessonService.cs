using Agent42.Agents;

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
/// A facade over three collaborators, each with one job: <see cref="FleetLessonStore"/> (the document and the per-user lock),
/// <see cref="FleetProposalWorkflow"/> (approving and dismissing prompt and skill proposals, ADR 0143/0146) and
/// <see cref="FleetDreamRunner"/> (the schedule, the run and the model pick). The facade keeps the public API the routes, the
/// launcher and the task service call; the lock rules are on <see cref="FleetLessonStore"/>.
/// </para>
/// </summary>
public sealed class FleetLessonService
{
    private readonly FleetLessonStore _lessons;
    private readonly FleetProposalWorkflow _proposals;
    private readonly FleetDreamRunner _dreams;
    private readonly FleetDreamRegistry _running = new();

    public FleetLessonService(
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
        var clock = time ?? TimeProvider.System;
        _lessons = new FleetLessonStore(store, options, settings, _running, logger, clock);
        _proposals = new FleetProposalWorkflow(store, _lessons, skills, logger, clock);
        _dreams = new FleetDreamRunner(store, options, chatClients, catalogs, _lessons, _proposals, _running, logger, clock, lifetime);
    }

    public FleetOptions Options => _lessons.Options;

    /// <summary>The options a user's dream runs on (ADR 0135): the operator's with the user's overrides. The operator-only
    /// fields (<c>DreamEnabled</c>, <c>DreamTimeout</c>) are read from <see cref="Options"/> on purpose.</summary>
    public Task<FleetOptions> EffectiveOptionsAsync(string userId, CancellationToken cancellationToken = default)
        => _lessons.EffectiveOptionsAsync(userId, cancellationToken);

    // ---- Reads ----

    /// <summary>The user's lessons document. A run entry that was running when the app stopped reads as interrupted.</summary>
    public Task<FleetLessonsDocument> GetAsync(string userId, CancellationToken cancellationToken = default)
        => _lessons.GetAsync(userId, cancellationToken);

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
    public Task<IReadOnlyList<FleetLesson>> ActiveAsync(string userId, CancellationToken cancellationToken = default, FleetOptions? effective = null)
        => _lessons.ActiveAsync(userId, cancellationToken, effective);

    /// <summary>Whether a dream of this user is running in this process.</summary>
    public bool IsRunning(string userId) => _running.IsRunning(userId);

    /// <summary>Completes when the user's running dream, if any, has finished and written its final state.</summary>
    public Task WhenIdleAsync(string userId) => _running.WhenIdleAsync(userId);

    // ---- Settings and lesson edits ----

    /// <summary>Saves the toggle and the model. A model that is not one of the user's tool-capable models is refused.</summary>
    public async Task<(FleetLessonSettings? Settings, string? Error)> SetSettingsAsync(
        string userId, bool enabled, string? model, CancellationToken cancellationToken = default)
    {
        var chosen = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (chosen is not null && await _dreams.PickModelAsync(userId, chosen, null, cancellationToken) is { Error: { } error })
        {
            return (null, error);
        }

        return (await _lessons.SaveSettingsAsync(userId, enabled, chosen, cancellationToken), null);
    }

    public Task MarkSeenAsync(string userId, CancellationToken cancellationToken = default)
        => _lessons.MarkSeenAsync(userId, cancellationToken);

    /// <summary>Turns a lesson off; it stays as a tombstone so it is not proposed again. False when it is not the user's.</summary>
    public Task<bool> DisableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => _lessons.DisableAsync(userId, lessonId, cancellationToken);

    /// <summary>Turns a disabled lesson back on, and renews it so it does not expire at once. False when it is not the user's.</summary>
    public Task<bool> EnableAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => _lessons.EnableAsync(userId, lessonId, cancellationToken);

    /// <summary>Deletes a lesson (a deleted one can be learned again). False when it is not the user's.</summary>
    public Task<bool> DeleteAsync(string userId, string lessonId, CancellationToken cancellationToken = default)
        => _lessons.DeleteAsync(userId, lessonId, cancellationToken);

    /// <summary>
    /// Records that a task's planner got these lessons: appends the task to each lesson's applications (the last 20) and bumps
    /// its count, once per task. A lesson that was deleted meanwhile is skipped.
    /// </summary>
    public Task RecordApplicationAsync(string userId, string taskId, IReadOnlyList<string> lessonIds, CancellationToken cancellationToken = default)
        => _lessons.RecordApplicationAsync(userId, taskId, lessonIds, cancellationToken);

    // ---- Proposals (ADR 0143, 0146) ----

    /// <summary>The user's prompts as they are now: the defaults with the user's overrides.</summary>
    public Task<FleetPromptSet> PromptsAsync(string userId, CancellationToken cancellationToken = default)
        => _proposals.PromptsAsync(userId, cancellationToken);

    /// <summary>The user's installed skills with their current text, or null when this deployment cannot install skills.</summary>
    internal Task<IReadOnlyList<FleetSkillSnapshot>?> SkillSnapshotsAsync(string userId, CancellationToken cancellationToken)
        => _proposals.SkillSnapshotsAsync(userId, cancellationToken);

    /// <summary>Approves a pending prompt proposal: re-applies its edit to the user's CURRENT prompt under the user's lock.</summary>
    public Task<FleetProposalDecision> ApproveProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => _proposals.ApproveProposalAsync(userId, proposalId, cancellationToken);

    /// <summary>Dismisses a pending prompt proposal; it stays as a tombstone so the same change is not proposed again.</summary>
    public Task<FleetProposalDecision> DismissProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => _proposals.DismissProposalAsync(userId, proposalId, cancellationToken);

    /// <summary>Approves a pending skill proposal: an edit is re-applied to the skill's current <c>SKILL.md</c> and installed
    /// without re-enabling it; a creation is installed and turned on. A proposal that no longer applies is marked outdated.</summary>
    public Task<FleetProposalDecision> ApproveSkillProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => _proposals.ApproveSkillProposalAsync(userId, proposalId, cancellationToken);

    /// <summary>Dismisses a pending skill proposal; it stays as a tombstone so the same change is not proposed again.</summary>
    public Task<FleetProposalDecision> DismissSkillProposalAsync(string userId, string proposalId, CancellationToken cancellationToken = default)
        => _proposals.DismissSkillProposalAsync(userId, proposalId, cancellationToken);

    // ---- The dream ----

    /// <summary>Runs a dream now, for a click on "Dream now": ignores the window and the new-task check, still needs
    /// <c>DreamMinTasks</c>, and uses up tonight's run. Awaits the run.</summary>
    public Task<FleetDreamResult> RunManualAsync(string userId, string? model, UserTelemetryContext? telemetry, CancellationToken cancellationToken = default)
        => _dreams.RunManualAsync(userId, model, telemetry, cancellationToken);

    /// <summary>
    /// One hourly tick of the user's reminder: decides whether it is time for tonight's dream, and STARTS it on a background
    /// task, returning at once (a long reminder callback would pass the Orleans call timeout). Outside the window it does
    /// nothing and writes nothing.
    /// </summary>
    public Task<FleetDreamTick> TickAsync(string userId, UserTelemetryContext? telemetry = null, CancellationToken cancellationToken = default)
        => _dreams.TickAsync(userId, telemetry, cancellationToken);

    /// <summary>The start of the nightly window that contains <paramref name="now"/>, or null outside every window.</summary>
    public DateTimeOffset? WindowStart(DateTimeOffset now, FleetOptions? effective = null)
        => _dreams.WindowStart(now, effective);
}
