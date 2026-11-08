using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Specialized.Magentic;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Turns Agent Framework workflow events into fleet events (ported from the engine's dashboard): the agents' streamed
/// text and thinking, the tools they call and the results they get. Tool arguments and results are cut to
/// <c>Fleet:MaxToolTextLength</c>, since they can be whole files.
/// </summary>
public sealed class FleetEventMapper(IFleetEventSink events, int maxToolTextLength)
{
    /// <summary>Executor ids are <c>&lt;AgentName&gt;_&lt;guid&gt;</c>.</summary>
    public static string AgentName(string executorId) => executorId.Split('_')[0];

    public void Publish(WorkflowEvent evt, FleetTeamContext context)
    {
        var (teamId, attempt) = (context.TeamId, context.Attempt);
        switch (evt)
        {
            // The self-review loop's feedback to the agent (ADR 0119) is not the agent's own text.
            case AgentResponseUpdateEvent { Update.AuthorName: FleetSelfReview.LoopAuthorName }:
                break;

            case AgentResponseUpdateEvent update:
                foreach (var content in update.Update.Contents)
                {
                    PublishContent(content, teamId, attempt, update.ExecutorId);
                }
                break;

            case ExecutorInvokedEvent invoked:
                events.Publish("agent.started", new { teamId, attempt, executorId = invoked.ExecutorId, agent = AgentName(invoked.ExecutorId) });
                break;

            case ExecutorCompletedEvent completed:
                events.Publish("agent.done", new { teamId, attempt, executorId = completed.ExecutorId, agent = AgentName(completed.ExecutorId) });
                break;

            case ExecutorFailedEvent failed:
                events.Publish("agent.failed", new { teamId, attempt, executorId = failed.ExecutorId, agent = AgentName(failed.ExecutorId), error = ErrorText(failed.Data) });
                break;

            case MagenticPlanCreatedEvent planCreated:
                events.Publish("magentic.plan", new { teamId, attempt, text = planCreated.FullTaskLedger.Text, replanned = false });
                break;

            case MagenticReplannedEvent replanned:
                events.Publish("magentic.plan", new { teamId, attempt, text = replanned.FullTaskLedger.Text, replanned = true });
                break;

            case WorkflowErrorEvent error:
                events.Publish("workflow.error", new { teamId, attempt, error = ErrorText(error.Data) });
                break;
        }
    }

    private void PublishContent(AIContent content, string? teamId, int attempt, string executorId)
    {
        var agent = AgentName(executorId);
        switch (content)
        {
            case TextContent { Text: { Length: > 0 } text }:
                events.Publish("agent.text", new { teamId, attempt, executorId, agent, text });
                break;
            case TextReasoningContent { Text: { Length: > 0 } text }:
                events.Publish("agent.thinking", new { teamId, attempt, executorId, agent, text });
                break;
            case FunctionCallContent call:
                events.Publish("tool.call", new { teamId, attempt, executorId, agent, callId = call.CallId, name = call.Name, arguments = ToText(call.Arguments) });
                break;
            case FunctionResultContent result:
                events.Publish("tool.result", new
                {
                    teamId,
                    attempt,
                    executorId,
                    agent,
                    callId = result.CallId,
                    result = ToText(result.Exception?.Message ?? result.Result),
                    failed = result.Exception is not null,
                });
                break;
        }
    }

    private static string? ErrorText(object? error) => error is Exception exception ? exception.Message : error?.ToString();

    private string ToText(object? value)
    {
        string text;
        try
        {
            text = value switch
            {
                null => "",
                string s => s,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? "",
                _ => JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            text = value?.ToString() ?? "";
        }

        return Truncate(text, maxToolTextLength);
    }

    public static string Truncate(string text, int max)
        => text.Length <= max ? text : $"{text[..max]}… ({text.Length:N0} characters)";
}
