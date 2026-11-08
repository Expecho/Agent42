using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace Agent42.Backend;

/// <summary>
/// Self-review for a fleet task that asked for it (ADR 0119): the agent(s) that write a team's result run again, with a
/// judge's gap analysis as feedback, until the judge is satisfied or <see cref="FleetOptions.SelfReviewMaxIterations"/>
/// is reached. Agent Framework's <c>LoopAgent</c> does the looping, shaped like the research agent's
/// (<c>AgentGrain.WithResearchLoop</c>, ADR 0082).
///
/// <para><b>Who loops.</b> Only the agents that produce the team's result: the last agent of a Sequential team and the
/// agents of the last stage of a Staged team, and only on this runtime's chat-client agents (a Copilot agent has its own
/// loop). The other patterns depend on one turn per agent, a moderator or a manager, and an earlier pipeline agent is
/// refined by the later ones. Teams deeper than <see cref="FleetOptions.JudgeDepth"/> are not wrapped: they are
/// deliberately unjudged and single-attempt for cost.</para>
///
/// <para><b>Shape, measured on 1.25.0 (ADR 0119).</b> The agent must see its own previous draft, so
/// <c>FreshContextPerIteration</c> stays off. The injected feedback stays in the output, because it keeps two drafts
/// apart when their updates carry no message id; <see cref="FleetEventMapper"/> skips it, or the page would show it as the
/// agent's own text. A judge that throws would fail the whole team, so the judge is wrapped to fail open, like
/// <see cref="FleetTeamRunner.JudgeAsync"/>; a judge that gives no verdict means "more" and loops to the cap, which is
/// the bound.</para>
/// </summary>
public static class FleetSelfReview
{
    /// <summary>The event published after a run the judge found incomplete, before the agent runs again.</summary>
    public const string IterationEvent = "selfreview.iteration";

    /// <summary>The event published when the judge failed and the result was kept as it was.</summary>
    public const string FailedEvent = "selfreview.failed";

    /// <summary>The author name of the feedback the loop sends to the agent on the caller's behalf.</summary>
    public const string LoopAuthorName = "self-review";

    /// <summary>How much of the judge's gap analysis goes into an event.</summary>
    public const int MaxEventTextLength = 500;

    /// <summary>
    /// Whether a team at this depth gets self-review: the task asked for it, the cap leaves room for a second run, and the
    /// team is judged (<see cref="FleetOptions.JudgeDepth"/>).
    /// </summary>
    public static bool Enabled(FleetRun run, int depth)
        => run.SelfReview && run.Options.SelfReviewMaxIterations >= 2 && depth <= run.Options.JudgeDepth;

    /// <summary>The names of the agents whose output is the team's result, by pattern. None for the patterns that never loop.</summary>
    public static IReadOnlySet<string> Producers(FleetTeamPlan plan)
    {
        IEnumerable<string> names = plan.Pattern switch
        {
            FleetPattern.Sequential when plan.Agents.Count > 0 => [plan.Agents[^1].Name],
            FleetPattern.Staged when plan.Stages.Count > 0 => plan.Stages[^1],
            _ => [],
        };
        return names.ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns <paramref name="agents"/> with the result-producing ones wrapped in the loop, in the same order. The list is
    /// returned as it is when the task did not ask for self-review. <paramref name="models"/> is the model id each agent was
    /// created with, by name; a <c>copilot</c> one is left alone.
    /// </summary>
    public static List<AIAgent> Apply(FleetRun run, FleetTeamPlan plan, List<AIAgent> agents, IReadOnlyDictionary<string, string> models, FleetTeamContext context)
    {
        if (!Enabled(run, context.Depth))
        {
            return agents;
        }

        var producers = Producers(plan);
        return [.. agents.Select(agent => producers.Contains(agent.Name ?? "") && CanLoop(models, agent) ? Wrap(run, agent, context) : agent)];
    }

    // Both checks on purpose: only this runtime's chat-client agents (the type the spawn metric also tells apart), and
    // not one that asked for a Copilot model, whatever type it ends up as.
    private static bool CanLoop(IReadOnlyDictionary<string, string> models, AIAgent agent)
        => agent is ChatClientAgent
           && !(models.TryGetValue(agent.Name ?? "", out var model) && model.StartsWith("copilot", StringComparison.OrdinalIgnoreCase));

#pragma warning disable MAAI001 // LoopAgent and its evaluators are [Experimental] in Agent Framework 1.25; ADR 0082 accepts the
    // exposure for the research agent and ADR 0119 extends it here. The scope is this region, nothing else.
    private static LoopAgent Wrap(FleetRun run, AIAgent agent, FleetTeamContext context)
    {
        // The judge calls the task's own budgeted client, so its tokens count and a budget cancel reaches it.
        var judge = new AIJudgeLoopEvaluator(run.DefaultClient);
        return new LoopAgent(
            agent,
            new ReviewEvaluator(judge, run, context.TeamId, agent.Name ?? ""),
            new LoopAgentOptions
            {
                MaxIterations = run.Options.SelfReviewMaxIterations,
                OnBehalfOfAuthorName = LoopAuthorName,
                // The feedback stays in the output on purpose: it is a user-role message between the drafts, and that role
                // change is what keeps two drafts apart in the team's output. Excluded, drafts whose updates carry no
                // MessageId merge into one message ("draft onedraft two", measured). The event mapper skips it instead.
                ExcludeOnBehalfOfMessages = false,
                NonStreamingReturnsLastResponseOnly = true,
            });
    }

    /// <summary>Asks the judge, publishes what it found, and fails open: a judge that errors keeps the result.</summary>
    private sealed class ReviewEvaluator(LoopEvaluator judge, FleetRun run, string? teamId, string agent) : LoopEvaluator
    {
        public override async ValueTask<LoopEvaluation> EvaluateAsync(LoopContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                var evaluation = await judge.EvaluateAsync(context, cancellationToken);
                if (evaluation.ShouldReinvoke)
                {
                    run.Events.Publish(IterationEvent, new
                    {
                        teamId,
                        agent,
                        iteration = context.Iteration,
                        feedback = FleetTeamRunner.Cut(evaluation.Feedback ?? "", MaxEventTextLength),
                    });
                }

                return evaluation;
            }
            // A stop or a used-up budget must still end the run; only the judge's own failure is swallowed.
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                run.Logger.LogWarning(ex, "Fleet task {TaskId}: the self-review judge failed, the result is kept", run.TaskId);
                run.Events.Publish(FailedEvent, new { teamId, agent, iteration = context.Iteration, error = ex.Message });
                return LoopEvaluation.Stop();
            }
        }
    }
#pragma warning restore MAAI001
}
