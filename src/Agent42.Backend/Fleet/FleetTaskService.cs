using System.Collections.Concurrent;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>What the page asks for when it starts a task: with attached files, and the task whose files a Run again
/// copies into the new workspace (ADR 0101). <paramref name="OriginConversationId"/> is set only by a chat's
/// <c>start_fleet_task</c> (ADR 0115), never by the page.</summary>
public sealed record FleetSubmit(
    string Task, string? FollowUpOf = null, string? Model = null, long? MaxTokens = null, IReadOnlyList<string>? EnabledTools = null,
    IReadOnlyList<FleetInputFile>? Files = null, string? CopyInputsOf = null, string? OriginConversationId = null);

/// <summary>How a stop, answer or delete went.</summary>
public enum FleetOutcome { Ok, NotFound, Conflict }

/// <summary>
/// Runs agent fleet tasks in-process (ADR 0097), NOT in a grain: each task runs on a background <see cref="Task"/>,
/// and its state, result and event log are persisted in <see cref="IFleetStore"/>. The registry of running tasks, their
/// open questions and their live subscribers is in-process, which relies on the single replica (CLAUDE.md rule 12).
/// Each user runs at most one task, and <c>Fleet:MaxConcurrentTasks</c> caps all users together. A task stored as
/// running that is not in the registry (the app restarted) is rewritten as <c>interrupted</c> when it is read. On
/// shutdown the running tasks are cancelled and persisted as <c>interrupted</c>, best effort.
/// </summary>
public sealed partial class FleetTaskService(
    IFleetStore store,
    FleetOptions options,
    IUserChatClientFactoryProvider chatClients,
    IUserModelCatalogProvider catalogs,
    ILogger<FleetTaskService> logger,
    FleetToolCatalog? tools = null,
    FleetCopilotHost? copilot = null,
    TimeProvider? time = null,
    IDocumentIndex? documents = null,
    DocumentIngestionService? ingestion = null,
    IFleetCompletionNotifier? notifier = null) : IHostedService
{
    /// <summary>The usage dashboard's application dimension for fleet tokens (ADR 0031).</summary>
    public const string ApplicationName = "Agent42 fleet";

    private readonly ConcurrentDictionary<string, RunningFleetTask> _running = new(StringComparer.Ordinal);
    private readonly Lock _submitGate = new();
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private volatile bool _stopping;

    public FleetOptions Options => options;

    /// <summary>Whether Copilot fleet agents can run here: the kill switch is on and the runtime is installed.</summary>
    public bool CopilotAvailable => copilot?.Available == true;

    /// <summary>Starts a task. Returns its id, or why it can't start.</summary>
    public async Task<(string? TaskId, string? Error)> SubmitAsync(
        string userId, FleetSubmit request, UserTelemetryContext? telemetry = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Task))
        {
            return (null, "The task is empty.");
        }

        var maxTokens = request.MaxTokens ?? options.DefaultMaxTokensPerTask;
        if (maxTokens <= 0 || maxTokens > options.MaxTokensPerTaskLimit)
        {
            return (null, $"The token cap must be between 1 and {options.MaxTokensPerTaskLimit:N0}.");
        }

        var catalog = await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken);
        var models = catalog.Models.Where(IsToolCapable).ToList();
        // A GitHub Copilot model (ADR 0108) is never the task's model, only a Copilot agent's: it rides along to the run
        // for the Copilot factory and the planner prompt, after the check below.
        var copilotOnly = catalog.Models.Where(m => m.CopilotOnly && m.SupportsTools).ToList();
        var model = string.IsNullOrWhiteSpace(request.Model) ? catalog.Default : request.Model;
        if (!models.Any(m => m.Id.Equals(model, StringComparison.Ordinal)))
        {
            return (null, "Pick one of your models that supports tools.");
        }

        FleetTaskRecord? previous = null;
        if (!string.IsNullOrWhiteSpace(request.FollowUpOf)
            && (previous = await GetAsync(userId, request.FollowUpOf, cancellationToken)) is not { CanFollowUp: true })
        {
            return (null, "Only a task of yours that finished well can be followed up.");
        }

        var (inputs, inputError) = await PrepareInputsAsync(userId, request, previous, cancellationToken);
        if (inputs is null || inputError is not null)
        {
            return (null, inputError);
        }

        var record = new FleetTaskRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            UserId = userId,
            Request = request.Task.Trim(),
            FollowUpOf = previous?.Id,
            OriginConversationId = string.IsNullOrWhiteSpace(request.OriginConversationId) ? null : request.OriginConversationId,
            WorkspaceId = previous?.WorkspaceId ?? Guid.NewGuid().ToString("N"),
            PlannerModel = model,
            StartedAt = _time.GetUtcNow(),
            MaxTokens = maxTokens,
            EnabledTools = ToolCatalog.Sanitize(request.EnabledTools),
            Notes = previous?.Notes ?? new Dictionary<string, string>(),
            Inputs = [.. inputs.Select(i => i.Name)],
            Documents = previous?.Documents ?? [],
            TextCopies = previous?.TextCopies ?? [],
        };

        if (Register(record) is { } refusal)
        {
            return (null, refusal);
        }

        var task = _running[record.Id];
        try
        {
            await StoreInputsAsync(record, inputs, cancellationToken);
            await store.SaveTaskAsync(record, cancellationToken);
        }
        catch
        {
            _running.TryRemove(record.Id, out _);
            task.Dispose();
            await DiscardInputsQuietlyAsync(record, ownWorkspace: previous is null);
            throw;
        }

        // Not the POST's ExecutionContext: the run sets its own telemetry identity, and must not be parented under,
        // or cancelled with, the request that started it.
        using (ExecutionContext.SuppressFlow())
        {
            task.Completion = Task.Run(() => RunAsync(task, [.. models, .. copilotOnly], previous, telemetry ?? new UserTelemetryContext(userId, null, null, null, null, null)));
        }

        return (record.Id, null);
    }

    // Takes the registry slot, or says why not. Under one lock, so two submits can't both pass the caps.
    private string? Register(FleetTaskRecord record)
    {
        lock (_submitGate)
        {
            if (_stopping)
            {
                return "The app is shutting down, try again in a moment.";
            }

            if (_running.Values.Any(t => t.Record.UserId == record.UserId))
            {
                return "A task is already running, stop it or wait until it's done.";
            }

            if (_running.Count >= options.MaxConcurrentTasks)
            {
                return "The fleet is busy with other tasks, try again later.";
            }

            var budget = new FleetBudget(record.MaxTokens, options.MaxAgentsPerTask, options.BudgetSoftLimit, options.MaxConcurrentSubTeamsPerLevel, options.MaxTeamDepth);
            var log = new FleetEventLog(store, record.UserId, record.Id, options.MaxEventLogBytes, _time);
            // A task started from a chat has nobody on the page to answer it (ADR 0115): it never opens a question.
            IFleetInteraction interaction = record.OriginConversationId is null ? new FleetQuestions(log) : new DecliningFleetInteraction(log);
            _running[record.Id] = new RunningFleetTask(record, budget, log, interaction);
            return null;
        }
    }

    /// <summary>The user's task, with a lost running task rewritten as interrupted; null when it is not theirs.</summary>
    public async Task<FleetTaskRecord?> GetAsync(string userId, string taskId, CancellationToken cancellationToken = default)
        => await store.GetTaskAsync(userId, taskId, cancellationToken) is { } record
            ? await NormalizeAsync(record, cancellationToken)
            : null;

    /// <summary>The user's tasks, newest first, with lost running tasks rewritten as interrupted.</summary>
    public async Task<IReadOnlyList<FleetTaskRecord>> ListAsync(string userId, CancellationToken cancellationToken = default)
    {
        var tasks = new List<FleetTaskRecord>();
        foreach (var record in await store.ListTasksAsync(userId, cancellationToken))
        {
            tasks.Add(await NormalizeAsync(record, cancellationToken));
        }

        return tasks;
    }

    public async Task<FleetOutcome> StopAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        if (RunningOf(userId, taskId) is { } task)
        {
            task.Budget.Cancel("stopped by the user");
            return FleetOutcome.Ok;
        }

        return await GetAsync(userId, taskId, cancellationToken) is null ? FleetOutcome.NotFound : FleetOutcome.Conflict;
    }

    public async Task<FleetOutcome> AnswerAsync(string userId, string taskId, string answer, int? questionId = null, CancellationToken cancellationToken = default)
    {
        if (RunningOf(userId, taskId) is { } task)
        {
            // An auto task (ADR 0115) has no question slot, so there is nothing to answer.
            return task.Questions is FleetQuestions questions && questions.Answer(answer, questionId) ? FleetOutcome.Ok : FleetOutcome.Conflict;
        }

        return await GetAsync(userId, taskId, cancellationToken) is null ? FleetOutcome.NotFound : FleetOutcome.Conflict;
    }

    public async Task<FleetOutcome> DeleteAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        if (RunningOf(userId, taskId) is not null)
        {
            return FleetOutcome.Conflict;
        }

        if (await GetAsync(userId, taskId, cancellationToken) is not { } record)
        {
            return FleetOutcome.NotFound;
        }

        await DeleteDocumentsIfLastAsync(record, cancellationToken);
        await store.DeleteTaskAsync(userId, taskId, cancellationToken);
        return FleetOutcome.Ok;
    }

    /// <summary>
    /// The task's events as JSON lines: the persisted log, then, while it runs, live, with no gap or duplicate between
    /// the two (see <see cref="FleetEventLog"/>). Null when the task is not the user's.
    /// </summary>
    public async Task<IAsyncEnumerable<string>?> SubscribeAsync(string userId, string taskId, CancellationToken cancellationToken = default)
    {
        // Subscribe BEFORE reading the store, so nothing published in between is missed.
        var running = RunningOf(userId, taskId);
        var subscription = running?.Log.Subscribe();
        if (subscription is null && await GetAsync(userId, taskId, cancellationToken) is null)
        {
            return null;
        }

        return Replay(userId, taskId, subscription, cancellationToken);
    }

    private async IAsyncEnumerable<string> Replay(
        string userId, string taskId, FleetEventLog.Subscription? subscription,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var stored = await store.ReadEventsAsync(userId, taskId, cancellationToken);
        await foreach (var line in FleetEventLog.ReplayAsync(stored, subscription, cancellationToken))
        {
            yield return line;
        }
    }

    /// <summary>The user's edited prompts.</summary>
    public async Task<FleetPromptSet> GetPromptsAsync(string userId, CancellationToken cancellationToken = default)
        => new(await store.GetPromptsAsync(userId, cancellationToken));

    /// <summary>Sets a prompt, or resets it to the default when <paramref name="text"/> is null. False for an unknown key.</summary>
    public async Task<bool> SetPromptAsync(string userId, string key, string? text, CancellationToken cancellationToken = default)
    {
        if (FleetPrompts.Find(key) is null)
        {
            return false;
        }

        var overrides = await store.GetPromptsAsync(userId, cancellationToken);
        await store.SetPromptsAsync(userId, FleetPrompts.WithOverride(overrides, key, text), cancellationToken);
        return true;
    }

    /// <summary>The store, for workspace downloads.</summary>
    public IFleetStore Store => store;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Cancels the running tasks and waits for them to persist themselves as interrupted, best effort.</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stopping = true;
        var running = _running.Values.ToList();
        foreach (var task in running)
        {
            task.Budget.Cancel("the app is shutting down");
        }

        try
        {
            await Task.WhenAll(running.Select(t => t.Completion)).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Fleet shutdown: {Count} task(s) did not finish persisting in time", running.Count(t => !t.Completion.IsCompleted));
        }
    }

    private RunningFleetTask? RunningOf(string userId, string taskId)
        => _running.TryGetValue(taskId, out var task) && task.Record.UserId == userId ? task : null;

    private async Task<FleetTaskRecord> NormalizeAsync(FleetTaskRecord record, CancellationToken cancellationToken)
    {
        if (record.Status != FleetTaskStatus.Running || _running.ContainsKey(record.Id))
        {
            return record;
        }

        // Re-read: the record may be from before a task's final save. A finishing task saves BEFORE it leaves the
        // registry, so once it is out of the registry the store has its final state, and a stale read must not
        // overwrite that with interrupted.
        var current = await store.GetTaskAsync(record.UserId, record.Id, cancellationToken);
        if (current is not { Status: FleetTaskStatus.Running })
        {
            return current ?? record;
        }

        var interrupted = current with
        {
            Status = FleetTaskStatus.Interrupted,
            Reason = "The app restarted while this task ran.",
            FinishedAt = record.FinishedAt ?? _time.GetUtcNow(),
        };
        await store.SaveTaskAsync(interrupted, cancellationToken);
        return interrupted;
    }

    /// <summary>
    /// A model the fleet can run on a chat client (planner, judge, manager, ordinary agents): one that takes tools and is
    /// a chat model. A GitHub Copilot model is not (ADR 0108): it runs only as a Copilot agent's <c>copilot/&lt;id&gt;</c>.
    /// </summary>
    public static bool IsToolCapable(ModelInfo model)
        => model.SupportsTools && !model.SupportsEmbeddings && !model.SupportsSpeech && !model.SupportsTranscription && !model.SupportsImageGeneration
            && !model.CopilotOnly;

    private sealed class RunningFleetTask(FleetTaskRecord record, FleetBudget budget, FleetEventLog log, IFleetInteraction questions) : IDisposable
    {
        // Replaced once, by the run-start ingest (ADR 0101), before anything else reads it for the run.
        public FleetTaskRecord Record { get; set; } = record;
        public FleetBudget Budget { get; } = budget;
        public FleetEventLog Log { get; } = log;
        public IFleetInteraction Questions { get; } = questions;

        /// <summary>Started from a chat (ADR 0115): every question is answered automatically.</summary>
        public bool AutoApprove => Record.OriginConversationId is not null;
        public Task Completion { get; set; } = Task.CompletedTask;

        public void Dispose() => Budget.Dispose();
    }
}
