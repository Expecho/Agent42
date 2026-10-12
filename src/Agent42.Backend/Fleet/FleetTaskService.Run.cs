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
        string? result = null;
        string? failure = null;
        try
        {
            workspace = new FleetWorkspace(record.WorkspaceId, record.Notes);
            if (previous is not null)
            {
                await workspace.MaterializeAsync(store, record.UserId, task.Budget.Token);
            }

            var run = await BuildRunAsync(task, workspace, models);
            task.Log.Publish("task.started", new
            {
                taskId = record.Id,
                task = record.Request,
                followUpOf = record.FollowUpOf,
                model = record.PlannerModel,
                maxTokens = record.MaxTokens,
                workspace = record.WorkspaceId,
                startedAt = record.StartedAt,
            });
            var input = previous is null ? record.Request : await FollowUpInputAsync(record, previous, workspace, run.Prompts);
            result = await new FleetTeamRunner(run).RunAsync(input);
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

    private async Task<FleetRun> BuildRunAsync(RunningFleetTask task, FleetWorkspace workspace, IReadOnlyList<ModelInfo> models)
    {
        var record = task.Record;
        var factory = await chatClients.GetForUserAsync(record.UserId, task.Budget.Token);
        var prompts = new FleetPromptSet(await store.GetPromptsAsync(record.UserId, task.Budget.Token));
        var functions = tools is null ? [] : await tools.BuildAsync(record.UserId, record.Request, task.Questions, task.Budget.Token);
        var copilotFactory = copilot is null
            ? null
            : new FleetCopilotAgentFactory(copilot, factory, [.. models.Select(m => m.Id)], record.PlannerModel, workspace, prompts,
                new FleetCopilotPermissionPolicy(workspace.Root, task.Questions, task.Budget.Token));
        return new FleetRun
        {
            TaskId = record.Id,
            Options = options,
            Prompts = prompts,
            Budget = task.Budget,
            Workspace = workspace,
            DefaultModel = record.PlannerModel,
            Clients = models.ToDictionary(m => m.Id, m => (IChatClient)new FleetBudgetChatClient(factory.GetClient(m.Id), task.Budget)),
            ModelNames = models.ToDictionary(m => m.Id, m => m.DisplayName),
            Tools = functions,
            CopilotFactory = copilotFactory,
            CopilotEnabled = copilot?.Available == true,
            Events = task.Log,
            Interaction = task.Questions,
            Logger = logger,
            AfterAttempt = ct => SyncWorkspaceAsync(record, workspace, ct),
        };
    }

    // The input of a follow-up: the new request with the earlier requests and their results, oldest first, and the
    // files in the workspace. The latest result is given up to MaxRetryResultLength, earlier ones shorter.
    private async Task<string> FollowUpInputAsync(FleetTaskRecord record, FleetTaskRecord previous, FleetWorkspace workspace, FleetPromptSet prompts)
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
            ("request", record.Request),
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

        try
        {
            await store.SaveTaskAsync(record with
            {
                Status = status,
                Result = status == FleetTaskStatus.Done ? result : null,
                Reason = reason,
                FinishedAt = _time.GetUtcNow(),
                Tokens = budget.Tokens,
                Agents = budget.Agents,
                Notes = workspace is null ? record.Notes : new Dictionary<string, string>(workspace.Notes),
            });
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
