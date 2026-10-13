using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

public sealed partial class FleetTeamRunner
{
    /// <summary>The Agent Framework workflow for a team's pattern. One agent always runs as a sequence of one.</summary>
    public Workflow BuildWorkflow(FleetTeamPlan plan, List<AIAgent> agents) => plan.Pattern switch
    {
        _ when agents.Count == 1 => AgentWorkflowBuilder.BuildSequential(agents),
        FleetPattern.Concurrent => AgentWorkflowBuilder.BuildConcurrent(agents),
        FleetPattern.GroupChat => AgentWorkflowBuilder
            .CreateGroupChatBuilderWith(participants => new ModeratedGroupChatManager(participants, run.DefaultClient, run.Prompts, plan.Task, plan.TerminationCriteria)
            {
                MaximumIterationCount = Math.Max(plan.MaxGroupChatRounds, participants.Count),
            })
            .AddParticipants(agents)
            .Build(),
        FleetPattern.Magentic => AgentWorkflowBuilder
            .CreateMagenticBuilderWith(run.DefaultClient.AsAIAgent(name: "MagenticManager"))
            .AddParticipants(agents)
            .WithMaxRounds(plan.MaxGroupChatRounds)
            // The team is already approved, the manager's own plan doesn't need a second approval.
            .RequirePlanSignoff(false)
            .Build(),
        FleetPattern.Handoff => AgentWorkflowBuilder
            .CreateHandoffBuilderWith(agents[0])
            .WithHandoffs(agents[0], agents[1..])
            .WithHandoffs(agents[1..], agents[0])
            .Build(),
        _ => AgentWorkflowBuilder.BuildSequential(agents),
    };

    /// <summary>Runs the workflow, publishes what its agents do, and returns the team's final result.</summary>
    public async Task<string> RunWorkflowAsync(Workflow workflow, FleetTeamPlan plan, string task, FleetTeamContext context)
    {
        var mapper = new FleetEventMapper(run.Events, Options.MaxToolTextLength);
        await using var workflowRun = await InProcessExecution.RunStreamingAsync(
            workflow, new List<ChatMessage> { new(ChatRole.User, task) }, cancellationToken: context.CancellationToken);
        await workflowRun.TrySendMessageAsync(new TurnToken(emitEvents: true));

        await foreach (var evt in workflowRun.WatchStreamAsync(context.CancellationToken))
        {
            mapper.Publish(evt, context);
            if (FinalResult(evt, plan, context) is { } result)
            {
                return result;
            }
        }

        // The stream ends early when the task is stopped.
        context.CancellationToken.ThrowIfCancellationRequested();
        return "";
    }

    // The team's result when this event ends the run, or null. AgentResponseUpdateEvent DERIVES from WorkflowOutputEvent
    // (measured, AF Workflows 1.23.0), so it must be excluded before the output cases, which filter on their Data type.
    private static string? FinalResult(WorkflowEvent evt, FleetTeamPlan plan, FleetTeamContext context)
    {
        switch (evt)
        {
            case AgentResponseUpdateEvent:
                return null;

            case WorkflowErrorEvent error:
                // An agent stopped by the budget or the user fails the workflow: stop the whole task, don't report it.
                context.CancellationToken.ThrowIfCancellationRequested();
                return $"The team failed: {(error.Data is Exception ex ? ex.Message : error.Data)}";

            case WorkflowOutputEvent { Data: IEnumerable<ChatMessage> messages }:
                var answers = messages.Where(m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text)).ToList();
                // Concurrent agents each produce part of the result; in the other patterns the last answer is the result.
                return plan.Pattern == FleetPattern.Concurrent
                    ? string.Join("\n\n", answers.Select(m => $"[{m.AuthorName}]: {m.Text}"))
                    : answers.LastOrDefault()?.Text ?? "";

            case WorkflowOutputEvent { Data: ChatMessage message }:
                return message.Text;

            case WorkflowOutputEvent { Data: string text }:
                return text;

            default:
                return null;
        }
    }

    /// <summary>
    /// Runs the stages of a Staged team one after the other. The agents of a stage work in parallel on the task and the
    /// results of all earlier stages; the last stage's result is the team's.
    /// </summary>
    public async Task<string> RunStagesAsync(FleetTeamPlan plan, List<AIAgent> agents, string task, FleetTeamContext context)
    {
        var agentsByName = agents.ToDictionary(agent => agent.Name ?? "", StringComparer.OrdinalIgnoreCase);
        List<string> results = [];
        var result = "";
        for (var index = 0; index < plan.Stages.Count; index++)
        {
            var stage = plan.Stages[index];
            run.Events.Publish("stage.started", new { teamId = context.TeamId, attempt = context.Attempt, stage = index + 1, stages = plan.Stages.Count, agents = stage });

            var stageAgents = stage.Select(name => agentsByName[name]).ToList();
            var stagePlan = new FleetTeamPlan { Pattern = stageAgents.Count == 1 ? FleetPattern.Sequential : FleetPattern.Concurrent };
            var input = results.Count == 0 ? task : $"{task}\n\nResults of the previous stages:\n\n{string.Join("\n\n", results)}";
            result = await RunWorkflowAsync(BuildWorkflow(stagePlan, stageAgents), stagePlan, input, context);
            results.Add(stageAgents.Count == 1 ? $"[{stage[0]}]: {result}" : result);
        }

        return result;
    }

    /// <summary>Checks that the stages of a Staged plan name every agent exactly once; returns what is wrong or null.</summary>
    public static string? ValidateStages(FleetTeamPlan plan)
    {
        if (plan.Stages.Count == 0 || plan.Stages.Any(stage => stage.Count == 0))
        {
            return "set stages, with at least one agent per stage.";
        }

        var names = plan.Agents.Select(agent => agent.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var staged = plan.Stages.SelectMany(stage => stage).ToList();
        List<string> problems = [];
        if (staged.Where(name => !names.Contains(name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() is { Count: > 0 } unknown)
        {
            problems.Add($"stages name unknown agents: {string.Join(", ", unknown)}.");
        }

        if (names.Where(name => !staged.Contains(name, StringComparer.OrdinalIgnoreCase)).ToList() is { Count: > 0 } missing)
        {
            problems.Add($"agents missing from the stages: {string.Join(", ", missing)}.");
        }

        if (staged.GroupBy(name => name, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToList() is { Count: > 0 } duplicates)
        {
            problems.Add($"agents in more than one stage: {string.Join(", ", duplicates)}.");
        }

        return problems.Count == 0 ? null : string.Join(" ", problems);
    }
}
