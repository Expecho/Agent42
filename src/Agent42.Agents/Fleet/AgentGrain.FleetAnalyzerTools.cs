using System.ComponentModel;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The chat's fleet-analyzer tools (ADR 0122): the agent in this conversation diagnoses a fleet task and can act on it,
/// without a new team, grain or page. The fleet is reached through the <see cref="IFleetLauncher"/> port, and the user
/// id is always this grain's <c>_userId</c>, never an argument, so another user's task reads as not found.
///
/// <para>Everything the read tools return is fleet or telemetry output, already capped and framed as data by the port.
/// The two actions are guarded: <c>stop_fleet_task</c> is the safe one and is not limited; <c>rerun_fleet_task</c> starts
/// a task, so it shares <see cref="FleetStartRefusal"/> with <c>start_fleet_task</c> (incognito, fleet-result turn, one
/// per turn). A tool never throws into the turn: failures come back as text.</para>
/// </summary>
public sealed partial class AgentGrain
{
    private AITool[] FleetAnalyzerTools() =>
    [
        AIFunctionFactory.Create(ListFleetTasksAsync, name: FleetAnalyzerLimits.ListToolName, description: FleetAnalyzerLimits.ListDescription),
        AIFunctionFactory.Create(AnalyzeFleetTaskAsync, name: FleetAnalyzerLimits.AnalyzeToolName, description: FleetAnalyzerLimits.AnalyzeDescription),
        AIFunctionFactory.Create(ReadFleetEventsAsync, name: FleetAnalyzerLimits.EventsToolName, description: FleetAnalyzerLimits.EventsDescription),
        AIFunctionFactory.Create(ReadFleetWorkspaceAsync, name: FleetAnalyzerLimits.WorkspaceToolName, description: FleetAnalyzerLimits.WorkspaceDescription),
        AIFunctionFactory.Create(GetFleetConfig, name: FleetAnalyzerLimits.ConfigToolName, description: FleetAnalyzerLimits.ConfigDescription),
        AIFunctionFactory.Create(ReadRecentSpansAsync, name: FleetAnalyzerLimits.SpansToolName, description: FleetAnalyzerLimits.SpansDescription),
        AIFunctionFactory.Create(StopFleetTaskAsync, name: FleetAnalyzerLimits.StopToolName, description: FleetAnalyzerLimits.StopDescription),
        AIFunctionFactory.Create(RerunFleetTaskAsync, name: FleetAnalyzerLimits.RerunToolName, description: FleetAnalyzerLimits.RerunDescription),
    ];

    private Task<string> ListFleetTasksAsync([Description("How many tasks, newest first. Default 10.")] int? limit = null)
        => FleetAnalyzerCallAsync(FleetAnalyzerLimits.ListToolName, () => _fleet.ListRecentAsync(
            _userId, FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultListLimit, FleetAnalyzerLimits.MaxListLimit)));

    private Task<string> AnalyzeFleetTaskAsync([Description("The fleet task id, from list_fleet_tasks or the fleet page.")] string taskId)
        => TaskCallAsync(FleetAnalyzerLimits.AnalyzeToolName, taskId, id => _fleet.AnalyzeAsync(_userId, id));

    private Task<string> ReadFleetEventsAsync(
        [Description("The fleet task id.")] string taskId,
        [Description("Optional comma-separated event types to keep, e.g. 'agent.failed,verdict'.")] string? types = null,
        [Description("Only events with a seq above this. Default 0; use the seq the previous page ended on.")] long? afterSeq = null,
        [Description("Events per page. Default 40, at most 100.")] int? limit = null)
        => TaskCallAsync(FleetAnalyzerLimits.EventsToolName, taskId, id => _fleet.ReadEventsAsync(
            _userId, id, types, afterSeq ?? 0,
            FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultEventLimit, FleetAnalyzerLimits.MaxEventLimit)));

    private Task<string> ReadFleetWorkspaceAsync(
        [Description("The fleet task id.")] string taskId,
        [Description("A workspace file path to read. Leave empty to list the files.")] string? path = null)
        => TaskCallAsync(FleetAnalyzerLimits.WorkspaceToolName, taskId, id => _fleet.ReadWorkspaceAsync(_userId, id, path));

    private string GetFleetConfig() => _fleet.DescribeConfig();

    private Task<string> ReadRecentSpansAsync(
        [Description("Optional text to look for in span names, errors and tag values.")] string? search = null,
        [Description("Spans to return, newest last. Default 15, at most 40.")] int? limit = null)
        => FleetAnalyzerCallAsync(FleetAnalyzerLimits.SpansToolName, () => _fleet.ReadSpansAsync(
            _userId, search, FleetAnalyzerLimits.Clamp(limit, FleetAnalyzerLimits.DefaultSpanLimit, FleetAnalyzerLimits.MaxSpanLimit)));

    private Task<string> StopFleetTaskAsync([Description("The id of the running fleet task to stop.")] string taskId)
        => TaskCallAsync(FleetAnalyzerLimits.StopToolName, taskId, id => _fleet.StopAsync(_userId, id));

    private async Task<string> RerunFleetTaskAsync(
        [Description("The id of the task to repeat.")] string taskId,
        [Description("Optional short correction for the new run, e.g. what to avoid. At most 1,000 characters.")] string? note = null,
        [Description("Optional tool keys to turn OFF for the new run. It can only remove tools.")] string[]? disableTools = null,
        [Description("Optional token cap for the new run. Default: the original task's cap.")] long? maxTokens = null)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return "Pass the id of the fleet task to re-run.";
        }

        if (FleetStartRefusal() is { } refusal)
        {
            return refusal;
        }

        try
        {
            var (newId, error) = await _fleet.RerunAsync(_userId, taskId.Trim(), note, disableTools, maxTokens);
            if (newId is null)
            {
                return $"The re-run did not start: {error ?? "no reason was given"}";
            }

            TurnContext.CurrentOrClosed.RecordFleetStart();
            return $"Started fleet task {newId} as a re-run of {taskId.Trim()}. The user can follow it at {FleetToolLimits.TaskLink(newId)}. "
                + "It is not approved automatically: if it needs an answer it asks on that page, so tell the user to open the link. "
                + "Its result is not sent to this conversation; tell the user it started, with the link.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent grain {GrainKey} failed to re-run fleet task {TaskId}", this.GetPrimaryKeyString(), taskId);
            return $"I could not re-run the fleet task: {ex.Message}";
        }
    }

    private Task<string> TaskCallAsync(string tool, string taskId, Func<string, Task<string>> call)
        => string.IsNullOrWhiteSpace(taskId)
            ? Task.FromResult("Pass the fleet task id.")
            : FleetAnalyzerCallAsync(tool, () => call(taskId.Trim()));

    // A tool must never brick the turn (the ReadDocumentAsync shape): any failure is text for the model.
    private async Task<string> FleetAnalyzerCallAsync(string tool, Func<Task<string>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent grain {GrainKey} failed in fleet analyzer tool {Tool}", this.GetPrimaryKeyString(), tool);
            return $"{tool} failed: {ex.Message}";
        }
    }
}
