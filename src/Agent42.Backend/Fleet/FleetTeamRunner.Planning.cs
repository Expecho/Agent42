using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Agents.ObjectModel;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

public sealed partial class FleetTeamRunner
{
    /// <summary>
    /// Lets the planner design a team for the task. <paramref name="previousAttempt"/> describes a team that already
    /// tried and how it fell short. Returns no agents when no working team came out of it.
    /// </summary>
    public async Task<(FleetTeamPlan Plan, List<AIAgent> Agents)> CreateTeamAsync(string task, FleetTeamContext context, string? previousAttempt = null)
    {
        if (run.Budget.RemainingAgents == 0)
        {
            return (new FleetTeamPlan(), []);
        }

        var submission = new PlanSubmission(this, task, context);

        // The plan arrives through a tool call rather than structured output, so a provider that ignores
        // ResponseFormat still works, and a rejected plan comes back to the planner as the tool's result.
        var planner = run.DefaultClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Planner",
            ChatOptions = new()
            {
                Instructions = PlannerInstructions(context),
                Tools = [AIFunctionFactory.Create(submission.SubmitAsync, "submit_team_plan")],
            },
        });

        var prompt = context.Depth == 0
            ? $"Design a team of agents for the following task:\n\n{task}"
            : $"Design a team of agents for the following sub-task, which another agent delegated as part of a larger task:\n\n{task}";
        prompt += $"\n\nThe budget of the task allows at most {run.Budget.RemainingAgents} more agent(s), including the agents of any sub-teams.";
        prompt += $"\n\nThe shared workspace of the task is the directory {run.Workspace.Root}.";
        if (previousAttempt is not null)
        {
            prompt += $"\n\n{previousAttempt}";
        }

        try
        {
            await planner.RunAsync(prompt, cancellationToken: context.CancellationToken);
        }
        // A failing planner means no team for this (sub-)task, and the caller decides what's next. A team it already
        // submitted still runs.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Logger.LogWarning(ex, "Fleet task {TaskId}: the planner failed", run.TaskId);
            run.Events.Publish("planner.failed", new { teamId = context.TeamId, attempt = context.Attempt, error = ex.Message });
        }

        return submission.Plan is null ? (new FleetTeamPlan(), []) : (submission.Plan, submission.Agents);
    }

    /// <summary>The planner's system prompt for a team at this depth: the tools, the user's models, Copilot and delegation.</summary>
    public string PlannerInstructions(FleetTeamContext context)
    {
        var tools = string.Join("\n", AgentFunctions(context)
            .Where(f => f.Name != "delegate_to_team")
            .Select(f => $"- {f.Name}: {f.Description}"));
        var models = string.Join("\n", run.Clients.Keys.Order(StringComparer.OrdinalIgnoreCase)
            .Select(id => run.ModelNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name) ? $"- {id} ({name})" : $"- {id}"));
        return run.Prompts.Render("planner",
            ("tools", tools),
            ("delegation", context.Depth < Options.MaxTeamDepth ? run.Prompts.Get("delegation") : ""),
            ("copilot", run.CopilotEnabled && run.CopilotFactory is not null ? run.Prompts.Get("copilot") : ""),
            ("models", models),
            ("defaultModel", run.DefaultModel));
    }

    /// <summary>Turns YAML into agents: Copilot agents first, then the chat client of the model in the YAML.</summary>
    public PromptAgentFactory CreateAgentFactory(FleetTeamContext context)
    {
        var functions = AgentFunctions(context);
        var modelFactories = run.Clients.ToDictionary(
            entry => entry.Key,
            entry => (PromptAgentFactory)new ChatClientPromptAgentFactory(entry.Value, functions));
        var routing = new ModelRoutingPromptAgentFactory(modelFactories, run.DefaultModel);
        return run.CopilotFactory is { } copilot ? new AggregatorPromptAgentFactory(copilot, routing) : routing;
    }

    /// <summary>
    /// The planner's <c>submit_team_plan</c> tool for one planning run. It validates the plan, loads every agent, asks
    /// for approval where configured, and reserves the agents in the budget. Every refusal goes back to the planner as
    /// the tool's result, so it can fix the plan.
    /// </summary>
    private sealed class PlanSubmission(FleetTeamRunner runner, string task, FleetTeamContext context)
    {
        private readonly FleetRun _run = runner.Run;

        public FleetTeamPlan? Plan { get; private set; }
        public List<AIAgent> Agents { get; private set; } = [];

        [Description("Submit the team plan. Every agent yaml is loaded; returns the errors to fix, or confirms the team is ready.")]
        // The required arguments have defaults, so a call that misses them gets a message back to fix it, instead of an
        // exception that ends the planner (and the task) after a few failed calls.
        public async Task<string> SubmitAsync(
            [Description("Why this team and pattern fit the task, in one or two sentences. Required.")] string rationale = "",
            [Description("The orchestration pattern. Required.")] FleetPattern? pattern = null,
            [Description("The agents in the team, in execution order. For Handoff the first agent is the triage agent. Required.")] FleetAgentDefinition[]? agents = null,
            [Description("Maximum number of turns, only used for the GroupChat and Magentic patterns.")] int maxGroupChatRounds = 0,
            [Description("When the group chat is done, for example 'the critic approved the final draft'. Only used for the GroupChat pattern.")] string terminationCriteria = "",
            [Description("The agent names per stage, in order. Agents in the same stage work in parallel. Only used for the Staged pattern.")] string[][]? stages = null)
        {
            if (Missing(rationale, pattern, agents) is { } missing)
            {
                return Rejected($"The plan was rejected: the call misses {missing}. Call submit_team_plan again with all required arguments.");
            }

            var plan = new FleetTeamPlan
            {
                Task = task,
                Rationale = rationale,
                Pattern = pattern!.Value,
                Agents = [.. agents!],
                MaxGroupChatRounds = maxGroupChatRounds > 0 ? maxGroupChatRounds : runner.Options.DefaultMaxGroupChatRounds,
                TerminationCriteria = terminationCriteria ?? "",
                Stages = stages?.Select(stage => stage.ToList()).ToList() ?? [],
            };

            if (Validate(plan) is { } invalid)
            {
                return Rejected(invalid);
            }

            var (loaded, models, errors) = await LoadAgentsAsync(plan);
            if (errors.Count > 0 || loaded.Count == 0)
            {
                return Rejected($"The plan was rejected, fix these agent definitions and submit the complete plan again:\n{string.Join("\n", errors)}");
            }

            PublishSubmitted(plan, loaded, models);
            if (await ApproveAsync() is { } refusal)
            {
                return Rejected(refusal);
            }

            if (!_run.Budget.TryReserveAgents(loaded.Count, context.Depth))
            {
                return Rejected($"The plan was rejected: only {_run.Budget.RemainingAgents} agent(s) are left in the budget of this task. Submit a smaller team.");
            }

            foreach (var agent in loaded)
            {
                FleetMetrics.AgentsSpawned.Add(1,
                    new("agent.team.level", context.Depth),
                    new("agent.runtime", agent is ChatClientAgent ? "chat-client" : "github-copilot"),
                    new("agent.model", models[agent.Name ?? ""]),
                    new("agent.team.pattern", plan.Pattern.ToString()));
            }

            Plan = plan;
            Agents = loaded;
            _run.Events.Publish("plan.accepted", new { teamId = context.TeamId, attempt = context.Attempt });
            return "The team is ready and will now execute the task. Reply with a one line confirmation.";
        }

        private static string? Missing(string rationale, FleetPattern? pattern, FleetAgentDefinition[]? agents)
        {
            List<string> missing = [];
            if (string.IsNullOrWhiteSpace(rationale))
            {
                missing.Add("rationale");
            }

            if (pattern is null)
            {
                missing.Add("pattern");
            }

            if (agents is not { Length: > 0 })
            {
                missing.Add("agents");
            }

            return missing.Count > 0 ? string.Join(", ", missing) : null;
        }

        private string? Validate(FleetTeamPlan plan)
        {
            if (plan.Pattern == FleetPattern.Staged && ValidateStages(plan) is { } stagesError)
            {
                return $"The plan was rejected: {stagesError} Submit the complete plan again.";
            }

            return plan.Agents.Count > _run.Budget.RemainingAgents
                ? $"The plan was rejected: it has {plan.Agents.Count} agents, but only {_run.Budget.RemainingAgents} agent(s) are left in the budget of this task. Submit a smaller team."
                : null;
        }

        private async Task<(List<AIAgent> Agents, Dictionary<string, string> Models, List<string> Errors)> LoadAgentsAsync(FleetTeamPlan plan)
        {
            var models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var factory = new ModelRecordingPromptAgentFactory(runner.CreateAgentFactory(context), _run.DefaultModel, models);
            List<AIAgent> loaded = [];
            List<string> errors = [];
            foreach (var definition in plan.Agents)
            {
                try
                {
                    var agent = await factory.CreateFromYamlAsync(definition.Yaml, context.CancellationToken);
                    if (CheckAgent(definition, agent) is { } error)
                    {
                        errors.Add(error);
                        continue;
                    }

                    loaded.Add(agent);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    errors.Add($"{definition.Name}: {ex.Message}");
                }
            }

            return (loaded, models, errors);
        }

        // What makes a loaded agent unusable. Malformed YAML does NOT throw (measured, AF Declarative 1.23.0-rc1): it
        // loads an agent with a wrong or empty name, so the name check is the real guard for it.
        private static string? CheckAgent(FleetAgentDefinition definition, AIAgent? agent)
        {
            if (agent is null)
            {
                return $"{definition.Name}: the yaml could not be loaded as a Prompt agent.";
            }

            var tools = agent.GetService<ChatOptions>()?.Tools ?? [];
            if (tools.Where(tool => tool is not AIFunctionDeclaration).Select(tool => tool.Name).ToList() is { Count: > 0 } hosted)
            {
                return $"{definition.Name}: tool(s) {string.Join(", ", hosted)} use a hosted kind, which is not available. Use the function kind, for example the web_search function.";
            }

            if (tools.Where(tool => tool is AIFunctionDeclaration and not AIFunction).Select(tool => tool.Name).ToList() is { Count: > 0 } unknown)
            {
                return $"{definition.Name}: unknown function tool(s) {string.Join(", ", unknown)}, use one of the available tools.";
            }

            // Stages and the group chat moderator find agents by the name in their yaml.
            return string.Equals(agent.Name, definition.Name, StringComparison.OrdinalIgnoreCase)
                ? null
                : $"{definition.Name}: the name in the yaml is '{agent.Name}', it must be '{definition.Name}'. Check that the yaml is valid.";
        }

        private void PublishSubmitted(FleetTeamPlan plan, List<AIAgent> loaded, Dictionary<string, string> models)
            => _run.Events.Publish("plan.submitted", new
            {
                teamId = context.TeamId,
                attempt = context.Attempt,
                pattern = plan.Pattern.ToString(),
                rationale = plan.Rationale,
                terminationCriteria = plan.TerminationCriteria,
                maxGroupChatRounds = plan.MaxGroupChatRounds,
                stages = plan.Stages,
                agents = loaded.Select(agent => new
                {
                    name = agent.Name,
                    description = agent.Description,
                    model = models.GetValueOrDefault(agent.Name ?? "", _run.DefaultModel),
                    yaml = plan.Agents.First(d => d.Name.Equals(agent.Name, StringComparison.OrdinalIgnoreCase)).Yaml,
                }),
                needsApproval = context.Depth <= runner.Options.ApprovalDepth,
            });

        // Null when the team may run; otherwise the reply for the planner.
        private async Task<string?> ApproveAsync()
        {
            if (context.Depth > runner.Options.ApprovalDepth)
            {
                return null;
            }

            var answer = await _run.Interaction.AskAsync(
                "Run this team?",
                [new("y", "Run this team"), new("n", "Stop the task")],
                "Or describe what to change in the team",
                context.CancellationToken);
            switch (answer.Trim().ToLowerInvariant())
            {
                case "" or "y" or "yes":
                    return null;
                case "n" or "no":
                    _run.Budget.Cancel("stopped by the user");
                    return "The user stopped the task. Do not submit a plan again.";
                default:
                    return $"The user rejected the plan with this feedback, revise the plan and submit it again:\n{answer}";
            }
        }

        private string Rejected(string reply)
        {
            _run.Events.Publish("plan.rejected", new { teamId = context.TeamId, attempt = context.Attempt, reason = reply });
            return reply;
        }
    }

    /// <summary>Records which model each agent of a plan asked for, by agent name, for the events and the metric.</summary>
    private sealed class ModelRecordingPromptAgentFactory(PromptAgentFactory inner, string defaultModel, Dictionary<string, string> models) : PromptAgentFactory
    {
        public override Task<AIAgent?> TryCreateAsync(GptComponentMetadata promptAgent, CancellationToken cancellationToken = default)
        {
            var hint = promptAgent.Model?.ModelNameHint;
            models[promptAgent.Name ?? ""] = string.IsNullOrWhiteSpace(hint) ? defaultModel : hint;
            return inner.TryCreateAsync(promptAgent, cancellationToken);
        }
    }
}
