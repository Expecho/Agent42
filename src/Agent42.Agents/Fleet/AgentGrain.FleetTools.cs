using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The chat's fleet tools (ADR 0115): <c>start_fleet_task</c> starts an agent fleet task tied to this conversation,
/// and <c>get_fleet_task</c> reads one. The fleet runs in-process in the Backend (ADR 0097), reached through the
/// <see cref="IFleetLauncher"/> port. When the task finishes, the result comes back through
/// <see cref="ReceiveFleetResultAsync"/> as a background turn.
///
/// <para>Three refusals, all inside the tool and read from the run's <see cref="TurnContext"/> (ADR 0106), never from
/// a field: an incognito turn (a task outlives the turn and would leave a trace, the <c>schedule_prompt</c> rule), a
/// fleet-result turn (the loop guard), and a second start in one turn.</para>
/// </summary>
public sealed partial class AgentGrain
{
    // The descriptions are the policy the model obeys (rule 11); they live in FleetToolLimits so a test can pin them.
    private AITool[] FleetTools() =>
    [
        AIFunctionFactory.Create(StartFleetTaskAsync, name: FleetToolLimits.StartToolName, description: FleetToolLimits.StartDescription),
        AIFunctionFactory.Create(GetFleetTaskAsync, name: FleetToolLimits.GetToolName, description: FleetToolLimits.GetDescription),
    ];

    private async Task<string> StartFleetTaskAsync(
        [Description("The whole job for the fleet, with everything the team needs to know: it can't see this conversation.")] string task,
        [Description("Optional: the id of an earlier fleet task that finished well, to continue in its workspace.")] string? followUpOf = null)
    {
        if (string.IsNullOrWhiteSpace(task))
        {
            return "Describe the job for the fleet in 'task'.";
        }

        if (task.Length > FleetToolLimits.MaxTaskChars)
        {
            return $"The task is too long; keep it under {FleetToolLimits.MaxTaskChars.ToString("N0", CultureInfo.InvariantCulture)} characters.";
        }

        if (FleetStartRefusal() is { } refusal)
        {
            return refusal;
        }

        try
        {
            return await StartFleetTaskCoreAsync(task.Trim(), string.IsNullOrWhiteSpace(followUpOf) ? null : followUpOf.Trim());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool must never brick the turn (the ReadDocumentAsync shape).
            _logger.LogError(ex, "Agent grain {GrainKey} failed to start a fleet task", this.GetPrimaryKeyString());
            return $"I could not start the fleet task: {ex.Message}";
        }
    }

    private async Task<string> StartFleetTaskCoreAsync(string task, string? followUpOf)
    {
        var conversationId = ConversationIdFromGrainKey(this.GetPrimaryKeyString());
        var model = string.IsNullOrEmpty(_modelId) ? null : _modelId;
        var (taskId, error) = await _fleet.StartAsync(_userId, conversationId, task, model, followUpOf);
        if (taskId is null)
        {
            return $"The fleet task did not start: {error ?? "no reason was given"}";
        }

        TurnContext.CurrentOrClosed.RecordFleetStart();
        return $"Started fleet task {taskId}. The user can follow it at {FleetToolLimits.TaskLink(taskId)}. "
            + "It runs on its own, without asking anything. You'll get the result in this conversation when it finishes; "
            + "don't poll for it. Tell the user it started, with the link.";
    }

    // The run's refusals, in order: incognito, the loop guard, then the per-turn cap. Outside a run the context is
    // Closed, which is ephemeral, so the tool fails closed.
    private static string? FleetStartRefusal()
    {
        var context = TurnContext.CurrentOrClosed;
        if (context.Ephemeral)
        {
            return "Fleet tasks can't be started in an incognito chat: the task would outlive it. Ask the user to start "
                + "it from a normal chat.";
        }

        if (context.FleetResultTurn)
        {
            return "This turn delivers a fleet task's result, so it can't start another fleet task. Report the result "
                + "to the user, and let them ask for more.";
        }

        return context.FleetStarts >= FleetToolLimits.MaxStartsPerTurn
            ? $"You already started {FleetToolLimits.MaxStartsPerTurn} fleet task this turn, the limit. Tell the user, "
                + "and start the next one on their next message if they want it."
            : null;
    }

    private async Task<string> GetFleetTaskAsync([Description("The id start_fleet_task returned.")] string taskId)
    {
        if (string.IsNullOrWhiteSpace(taskId))
        {
            return "Pass the id that start_fleet_task returned.";
        }

        try
        {
            // Ownership: the launcher reads the task of THIS conversation's user only, so another user's id reads as
            // not found.
            return await _fleet.GetAsync(_userId, taskId.Trim()) is { } record
                ? FormatFleetTask(record)
                : $"No fleet task with id '{taskId.Trim()}' was found.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Agent grain {GrainKey} failed to read fleet task {TaskId}", this.GetPrimaryKeyString(), taskId);
            return $"I could not read the fleet task: {ex.Message}";
        }
    }

    private static string FormatFleetTask(FleetTaskRecord record)
    {
        var text = new StringBuilder()
            .Append($"Fleet task {record.Id}: {record.Status}. Tokens: {record.Tokens.ToString("N0", CultureInfo.InvariantCulture)}. Agents: {record.Agents}. ")
            .Append($"Page: {FleetToolLimits.TaskLink(record.Id)}\n")
            .Append($"Task: {FleetToolLimits.Cut(record.Request, record.Id)}\n");
        // The reason and the result are fleet output: both go inside the data frame, never raw.
        if (record.Reason is { Length: > 0 } reason)
        {
            text.Append("Reason:\n").Append(FleetToolLimits.WrapAsData(reason, record.Id)).Append('\n');
        }

        if (record.Result is { } result)
        {
            text.Append("Result:\n").Append(FleetToolLimits.WrapAsData(result, record.Id));
        }

        return text.ToString();
    }
}
