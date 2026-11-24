using System.Text;
using Agent42.Agents;
using Microsoft.Extensions.AI;
using Orleans.Runtime;

namespace Agent42.Backend;

public sealed partial class FleetTaskService
{
    /// <summary>How often a running task's events are persisted, and its budget published.</summary>
    public static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(1);

    // How deep a follow-up chain is followed back for the history of a follow-up.
    private const int MaxFollowUpChain = 20;

    private async Task RunAsync(RunningFleetTask task, IReadOnlyList<ModelInfo> models, FleetTaskRecord? previous, UserTelemetryContext telemetry)
    {
        // The usage dashboard attributes tokens from Orleans RequestContext (ADR 0010/0031), which a background task
        // doesn't have: set the user and the application here, for every model call of this run.
        RequestContext.Set(UserTelemetryContext.RequestContextKey, telemetry);
        RequestContext.Set(UsageSpanProjector.ApplicationContextKey, ApplicationName);

        var record = task.Record;
        using var flushing = new CancellationTokenSource();
        var flushLoop = FlushLoopAsync(task, flushing.Token);
        FleetWorkspace? workspace = null;
        using var canvas = new FleetCanvas(store, record.UserId, record.WorkspaceId, task.Log);
        string? result = null;
        string? failure = null;
        try
        {
            workspace = new FleetWorkspace(record.WorkspaceId, record.Notes);
            if (previous is not null || record.Inputs.Count > 0)
            {
                await workspace.MaterializeAsync(store, record.UserId, task.Budget.Token);
            }

            // Always loaded: a follow-up reuses the workspace id, so it sees the pages of the tasks before it (ADR 0102).
            await canvas.LoadAsync(task.Budget.Token);

            // task.started stays the log's first event; the ingest reports after it, and the run is built from the
            // record the ingest updated (its Documents decide the document tools).
            task.Log.Publish("task.started", new
            {
                taskId = record.Id,
                task = record.Request,
                followUpOf = record.FollowUpOf,
                model = record.PlannerModel,
                maxTokens = record.MaxTokens,
                workspace = record.WorkspaceId,
                startedAt = record.StartedAt,
                inputs = record.Inputs,
            });
            record = await IngestInputsAsync(task, workspace);
            var run = await BuildRunAsync(task, workspace, canvas, models);
            var input = await ComposeInputAsync(task, record, previous, workspace, run);
            result = await new FleetTeamRunner(run).RunAsync(input);
            failure = result is null ? FleetTeamRunner.NoTeamResult : null;
        }
        catch (OperationCanceledException) when (task.Budget.IsCancelled)
        {
            // Stopped by the user, the token cap or the shutdown; the status says which.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet task {TaskId} failed", record.Id);
            failure = ex.Message;
        }
        finally
        {
            await flushing.CancelAsync();
            await flushLoop;
            await FinishAsync(task, workspace, result, failure);
        }
    }

    // Indexes this task's attached files (ADR 0101), reports each file's outcome, and saves the record with the
    // workspace's new Documents and TextCopies. A task without inputs skips it all.
    private async Task<FleetTaskRecord> IngestInputsAsync(RunningFleetTask task, FleetWorkspace workspace)
    {
        var record = task.Record;
        if (record.Inputs.Count == 0)
        {
            return record;
        }

        var result = ingestion is null || documents is null
            ? FleetDocumentIngest.FilesOnly(record, "documents can't be searched on this server")
            : await new FleetDocumentIngest(ingestion, documents, options, logger).IngestAsync(record, workspace, task.Budget.Token);
        task.Log.Publish("documents.ingested", new { files = result.Files });

        task.Record = record with { Documents = result.Documents, TextCopies = result.TextCopies };
        await store.SaveTaskAsync(task.Record, task.Budget.Token);
        return task.Record;
    }

    // The input's "Attached files" section (ADR 0101): the inputs, their text copies, and which files the document
    // tools reach. Empty when the workspace has neither inputs nor documents.
    private static string AttachedFiles(FleetTaskRecord record)
    {
        if (record.Inputs.Count == 0 && record.Documents.Count == 0)
        {
            return "";
        }

        var text = new StringBuilder("\n\nAttached files, in the shared workspace:");
        foreach (var name in record.Inputs)
        {
            var copy = InputPath(name + ".txt");
            text.Append($"\n- {InputPath(name)}").Append(record.TextCopies.Contains(copy, StringComparer.Ordinal) ? $" (as text: {copy})" : "");
        }

        text.Append(record.Documents.Count == 0
            ? "\nNo document can be searched."
            : "\nThese documents can be searched with search_documents and read part by part with read_document: "
                + string.Join(", ", record.Documents.Select(d => $"'{d}'")) + ".");
        return text.ToString();
    }

    private async Task<FleetRun> BuildRunAsync(RunningFleetTask task, FleetWorkspace workspace, FleetCanvas canvas, IReadOnlyList<ModelInfo> models)
    {
        var record = task.Record;
        var factory = await chatClients.GetForUserAsync(record.UserId, task.Budget.Token);
        var prompts = new FleetPromptSet(await store.GetPromptsAsync(record.UserId, task.Budget.Token));
        var documentTools = documents is null || record.Documents.Count == 0
            ? []
            : new FleetDocumentTools(chatClients, documents, record.UserId, record.WorkspaceId, record.Documents, logger).Tools;
        var toolSet = tools is null
            ? new FleetToolSet(documentTools, [])
            : await tools.BuildAsync(
                new FleetToolContext(record.UserId, record.Id, record.Request, record.EnabledTools, task.Questions)
                {
                    DocumentTools = documentTools,
                    Workspace = workspace,
                    Models = factory,
                    ImageModels = await ImageModelsAsync(record.UserId, task.Budget.Token),
                },
                task.Budget.Token);
        // GitHub Copilot models (ADR 0108) are offered to Copilot agents only; every chat client below is built from the rest.
        var chatModels = models.Where(m => !m.CopilotOnly).ToList();
        var reachable = FleetCopilotAgentFactory.ReachableModels(factory, models);
        // The user's lessons (ADR 0124): a failing read is logged and the run goes without them.
        var activeLessons = lessons is null ? [] : await lessons.ActiveAsync(record.UserId, task.Budget.Token, task.Options);
        var copilotFactory = copilot is null
            ? null
            : new FleetCopilotAgentFactory(copilot, factory, [.. models.Select(m => m.Id)], record.PlannerModel, workspace, canvas, prompts,
                new FleetCopilotPermissionPolicy(workspace.Root, task.Questions, task.Budget.Token, task.AutoApprove ? task.Log : null),
                task.Options.DefaultCopilotModel, toolSet.Tools, toolSet.Skills);
        return new FleetRun
        {
            TaskId = record.Id,
            Options = task.Options,
            Prompts = prompts,
            Budget = task.Budget,
            Workspace = workspace,
            Canvas = canvas,
            DefaultModel = record.PlannerModel,
            Clients = chatModels.ToDictionary(m => m.Id, m => (IChatClient)new FleetBudgetChatClient(factory.GetClient(m.Id), task.Budget)),
            ModelNames = chatModels.ToDictionary(m => m.Id, m => m.DisplayName),
            CopilotModelNames = models.Where(m => m.CopilotOnly).ToDictionary(m => m.Id, m => m.DisplayName),
            Tools = toolSet.Tools,
            CopilotByokModelIds = reachable.Where(m => !m.CopilotOnly).ToDictionary(m => m.Id, m => m.DisplayName),
            CopilotDefaultModel = copilotFactory?.ModelFor("copilot"),
            CopilotFactory = copilotFactory,
            CopilotEnabled = FleetCopilotAgentFactory.CopilotOffered(copilot, reachable),
            Events = task.Log,
            Interaction = task.Questions,
            AutoApprove = task.AutoApprove,
            SelfReview = record.SelfReview,
            Lessons = activeLessons,
            OnLessonsApplied = applied => ApplyLessonsAsync(task, applied),
            Logger = logger,
            AfterAttempt = ct => SyncWorkspaceAsync(record, workspace, ct),
        };
    }

    // The user's image-generation models (ADR 0144), which the run's model list leaves out (they can't run an agent).
    private async Task<IReadOnlyList<string>> ImageModelsAsync(string userId, CancellationToken cancellationToken)
    {
        var catalog = await (await catalogs.GetForUserAsync(userId, cancellationToken)).GetAsync(cancellationToken);
        return [.. catalog.Models.Where(m => m.SupportsImageGeneration).Select(m => m.Id)];
    }

    // The first time a planner of the run gets lessons (ADR 0124). The in-memory record is assigned FIRST: FinishAsync
    // rebuilds the final record from task.Record, and RunAsync holds an earlier local copy, so saving only a copy would be
    // overwritten by the final save and the task view would show nothing applied. The runner calls this once per task, inside
    // its once-only guard, and catches whatever it throws. The lessons' own counters are recorded in the background: a slow
    // lock or blob never delays planning, and FinishAsync waits for it briefly.
    private async Task ApplyLessonsAsync(RunningFleetTask task, IReadOnlyList<FleetLesson> applied)
    {
        task.Record = task.Record with { AppliedLessons = [.. applied.Select(l => new FleetAppliedLesson(l.Id, l.Text))] };
        var (userId, taskId) = (task.Record.UserId, task.Record.Id);
        task.Applications = RecordApplicationQuietlyAsync(userId, taskId, [.. applied.Select(l => l.Id)]);
        await store.SaveTaskAsync(task.Record, task.Budget.Token);
    }

    private async Task RecordApplicationQuietlyAsync(string userId, string taskId, IReadOnlyList<string> ids)
    {
        try
        {
            if (lessons is not null)
            {
                await lessons.RecordApplicationAsync(userId, taskId, ids);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Fleet task {TaskId}: the applied lessons' counters could not be saved", taskId);
        }
    }

    // How long a finishing task waits for the lessons' counters before it moves on without them.
    private static readonly TimeSpan ApplicationsWait = TimeSpan.FromSeconds(5);

    // The input of the run: the request (or, when the user asked to be questioned first, the brief the clarify phase
    // wrote from it, ADR 0118), the follow-up history, and the attached files. The record keeps the user's own words.
    // A task started from a chat never clarifies: nobody is there to answer.
    private async Task<string> ComposeInputAsync(
        RunningFleetTask task, FleetTaskRecord record, FleetTaskRecord? previous, FleetWorkspace workspace, FleetRun run)
    {
        var files = AttachedFiles(record);
        var request = record.Clarify && !task.AutoApprove
            ? await new FleetClarifier(run, record.Request, files, previous is null ? null : FleetTeamRunner.Cut(previous.Request, 500))
                .RunAsync(task.Budget.Token)
            : record.Request;
        return (previous is null ? request : await FollowUpInputAsync(record, previous, workspace, run.Prompts, request)) + files;
    }

    // The input of a follow-up: the new request with the earlier requests and their results, oldest first, and the
    // files in the workspace. The latest result is given up to MaxRetryResultLength, earlier ones shorter.
    private async Task<string> FollowUpInputAsync(
        FleetTaskRecord record, FleetTaskRecord previous, FleetWorkspace workspace, FleetPromptSet prompts, string request)
    {
        var chain = new List<FleetTaskRecord>();
        for (var task = previous; task is not null && chain.Count < MaxFollowUpChain;
             task = task.FollowUpOf is { } id ? await store.GetTaskAsync(record.UserId, id) : null)
        {
            chain.Insert(0, task);
        }

        var history = string.Join("\n\n", chain.Select((task, index) =>
        {
            var limit = task.Id == previous.Id ? options.MaxRetryResultLength : options.MaxRetryResultLength / 5;
            return $"Earlier task {index + 1}: {task.Request}\n\nResult of earlier task {index + 1}:\n{FleetTeamRunner.Cut(task.Result ?? "", limit)}";
        }));
        var files = workspace.ListFiles();
        return prompts.Render("followup",
            ("request", request),
            ("history", history),
            ("files", files.Count == 0 ? "(none)" : string.Join("\n", files.Select(file => $"- {file}"))));
    }

    private async Task SyncWorkspaceAsync(FleetTaskRecord record, FleetWorkspace workspace, CancellationToken cancellationToken)
    {
        var skipped = await workspace.SyncToStoreAsync(store, record.UserId, options.MaxWorkspaceBytes, options.MaxWorkspaceFiles, cancellationToken);
        if (skipped.Count > 0)
        {
            logger.LogWarning("Fleet task {TaskId}: {Count} workspace file(s) over the caps were not saved", record.Id, skipped.Count);
        }
    }

    private async Task FlushLoopAsync(RunningFleetTask task, CancellationToken cancellationToken)
    {
        (long, int) lastBudget = default;
        using var timer = new PeriodicTimer(FlushInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var budget = task.Budget;
                if ((budget.Tokens, budget.Agents) != lastBudget)
                {
                    lastBudget = (budget.Tokens, budget.Agents);
                    PublishBudget(task);
                }

                await FlushQuietlyAsync(task);
            }
        }
        catch (OperationCanceledException)
        {
            // The task ended; FinishAsync does the last flush.
        }
    }

    private async Task FinishAsync(RunningFleetTask task, FleetWorkspace? workspace, string? result, string? failure)
    {
        await Task.WhenAny(task.Applications, Task.Delay(ApplicationsWait));
        var record = task.Record;
        var budget = task.Budget;
        var status = _stopping ? FleetTaskStatus.Interrupted
            : budget.IsCancelled ? FleetTaskStatus.Stopped
            : failure is not null || result is null ? FleetTaskStatus.Failed
            : FleetTaskStatus.Done;
        var reason = status == FleetTaskStatus.Interrupted ? "The app shut down while this task ran."
            : budget.IsCancelled ? budget.CancelReason
            : failure;

        try
        {
            if (workspace is not null)
            {
                await SyncWorkspaceAsync(record, workspace, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet task {TaskId}: the final workspace sync failed", record.Id);
        }

        PublishBudget(task);
        task.Log.Publish("task.finished", new
        {
            taskId = record.Id,
            status,
            reason,
            result,
            canFollowUp = status == FleetTaskStatus.Done,
        });
        await FlushQuietlyAsync(task);

        var final = record with
        {
            Status = status,
            Result = status == FleetTaskStatus.Done ? result : null,
            Reason = reason,
            FinishedAt = _time.GetUtcNow(),
            Tokens = budget.Tokens,
            Agents = budget.Agents,
            Notes = workspace is null ? record.Notes : new Dictionary<string, string>(workspace.Notes),
        };
        try
        {
            await store.SaveTaskAsync(final);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Fleet task {TaskId}: its final state could not be saved", record.Id);
        }
        finally
        {
            task.Log.Complete();
            _running.TryRemove(record.Id, out _);
            workspace?.Dispose();
            task.Dispose();
        }

        await NotifyOriginAsync(final);
    }

    // ADR 0115: a task started from a chat reports back to it, AFTER the save, so the conversation's get_fleet_task
    // already reads the final state. An interrupted task (the app is shutting down) never reports. Best effort: a
    // failure here never changes how the task ended.
    private async Task NotifyOriginAsync(FleetTaskRecord final)
    {
        if (notifier is null || final.OriginConversationId is null || final.Status == FleetTaskStatus.Interrupted)
        {
            return;
        }

        try
        {
            await notifier.NotifyAsync(final);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fleet task {TaskId}: its conversation could not be told it finished", final.Id);
        }
    }

    private static void PublishBudget(RunningFleetTask task)
    {
        var budget = task.Budget;
        task.Log.Publish("budget", new
        {
            tokens = budget.Tokens,
            maxTokens = budget.MaxTokens,
            inputTokens = budget.InputTokens,
            outputTokens = budget.OutputTokens,
            cachedInputTokens = budget.CachedInputTokens,
            agents = budget.Agents,
            maxAgents = budget.MaxAgents,
            deepestLevel = budget.DeepestLevel,
        });
    }

    private async Task FlushQuietlyAsync(RunningFleetTask task)
    {
        try
        {
            await task.Log.FlushAsync();
        }
        catch (Exception ex)
        {
            // The live stream still has the events; the next flush retries them.
            logger.LogWarning(ex, "Fleet task {TaskId}: persisting its events failed", task.Record.Id);
        }
    }
}
