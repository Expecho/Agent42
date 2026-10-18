using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Runs a fleet task (ported from the engine's top-level functions, ADR 0097): a planner designs a team, the team
/// runs as an Agent Framework workflow, agents may delegate sub-tasks to sub-teams, and a judge reviews the result,
/// which the same team then fixes or a new team redoes. One instance per task run: the budget, ids, events and
/// questions are all this run's own.
/// </summary>
public sealed partial class FleetTeamRunner(FleetRun run)
{
    private int _lastTeamId;

    private FleetOptions Options => run.Options;

    private FleetRun Run => run;

    /// <summary>The text a team without a result hands back, to a delegating agent.</summary>
    public const string NoTeamResult = "No team could be created for this task.";

    /// <summary>Runs the task as the root team and returns its result, or null when no team could produce one.</summary>
    public Task<string?> RunAsync(string task) => RunTeamCoreAsync(task, new FleetTeamContext(0, run.Budget.Token));

    /// <summary>
    /// Designs a team for the (sub-)task, runs it and returns the team's final result. Up to the judge depth a judge
    /// reviews the result; when it doesn't pass, the team first fixes it, and then a new team is designed that builds
    /// on it. The best scored result is kept.
    /// </summary>
    public async Task<string> RunTeamAsync(string task, FleetTeamContext context)
        => await RunTeamCoreAsync(task, context) ?? NoTeamResult;

    private async Task<string?> RunTeamCoreAsync(string task, FleetTeamContext context)
    {
        var teamId = $"team-{Interlocked.Increment(ref _lastTeamId)}";
        run.Events.Publish("team.started", new
        {
            taskId = run.TaskId,
            teamId,
            parentTeamId = context.TeamId,
            parentCallId = context.Depth > 0 ? FunctionInvokingChatClient.CurrentContext?.CallContent.CallId : null,
            depth = context.Depth,
            task,
        });

        var team = new TeamRun(task, context with { TeamId = teamId }, context.Depth <= Options.JudgeDepth ? Options.MaxAttempts : 1);
        for (var teamNumber = 1; teamNumber <= team.Teams && !team.Done; teamNumber++)
        {
            if (!await RunNewTeamAsync(team, teamNumber))
            {
                break;
            }
        }

        run.Events.Publish("team.finished", new { teamId, result = team.BestResult, failed = team.BestResult is null });
        return team.BestResult;
    }

    // One designed team: plan it, then run it and let it fix its result until it passes or runs out of fix rounds.
    // False when no team could be created, which ends the attempts.
    private async Task<bool> RunNewTeamAsync(TeamRun team, int teamNumber)
    {
        team.Attempt++;
        run.Events.Publish("attempt.started", new { teamId = team.Context.TeamId, attempt = team.Attempt, review = team.Review });
        var (plan, agents) = await CreateTeamAsync(team.Task, team.Context with { Attempt = team.Attempt }, team.PreviousAttempt);
        if (agents.Count == 0)
        {
            run.Events.Publish("attempt.failed", new
            {
                teamId = team.Context.TeamId,
                attempt = team.Attempt,
                reason = "The planner was unable to create a working team for this task.",
            });
            return false;
        }

        for (var fixRound = 0; ; fixRound++)
        {
            var attemptContext = team.Context with { Attempt = team.Attempt };
            var result = plan.Pattern == FleetPattern.Staged && agents.Count > 1
                ? await RunStagesAsync(plan, agents, team.Input, attemptContext)
                : await RunWorkflowAsync(BuildWorkflow(plan, agents), plan, team.Input, attemptContext);
            run.Events.Publish("attempt.result", new { teamId = team.Context.TeamId, attempt = team.Attempt, result });
            if (run.AfterAttempt is { } afterAttempt)
            {
                await afterAttempt(attemptContext.CancellationToken);
            }

            if (!await ReviewAsync(team, plan, result, fixRound, teamNumber))
            {
                return true;
            }

            // The same team, without planning or approval again, works on its result with the feedback.
            team.Attempt++;
            run.Events.Publish("attempt.started", new { teamId = team.Context.TeamId, attempt = team.Attempt, review = team.Review, fixOf = team.Attempt - 1 });
        }
    }

    // Judges a result and decides what's next. True when the same team fixes its result now; false when the team is
    // done (passed, not judged, or a new team or nothing is next).
    private async Task<bool> ReviewAsync(TeamRun team, FleetTeamPlan plan, string result, int fixRound, int teamNumber)
    {
        var context = team.Context with { Attempt = team.Attempt };
        if (context.Depth > Options.JudgeDepth)
        {
            team.BestResult = result;
            team.Done = true;
            return false;
        }

        run.Events.Publish("judge.started", new { teamId = context.TeamId, attempt = team.Attempt });
        var verdict = await JudgeAsync(team.Task, result, context);
        var next = verdict.Passed ? null : fixRound < Options.MaxFixRounds ? "fix" : teamNumber < team.Teams ? "newTeam" : null;
        run.Events.Publish("verdict", new
        {
            teamId = context.TeamId,
            attempt = team.Attempt,
            score = verdict.Score,
            passed = verdict.Passed,
            feedback = verdict.Feedback,
            passingScore = Options.PassingScore,
            willRetry = next is not null,
            next,
        });
        if (team.BestResult is null || verdict.Score > team.BestScore)
        {
            team.BestResult = result;
            team.BestScore = verdict.Score;
        }

        if (verdict.Passed)
        {
            team.Done = true;
            return false;
        }

        team.Input = RetryInput(team.Task, verdict, result);
        team.Review = new { fromAttempt = team.Attempt, score = verdict.Score, feedback = verdict.Feedback };
        if (next == "fix")
        {
            return true;
        }

        team.PreviousAttempt = $"""
            A previous team did not deliver a good enough result{(fixRound > 0 ? ", also not after fixing it" : "")}. Design a better team, keep
            what worked and fix what the reviewer pointed out.
            Previous pattern: {plan.Pattern}, agents: {string.Join(", ", plan.Agents.Select(a => a.Name))}.
            Reviewer score: {verdict.Score}/10. Reviewer feedback: {verdict.Feedback}
            The new team does not start from scratch: it receives the task together with the reviewer's feedback and the previous result,
            so it can improve that result. Design the agents to revise it rather than to redo the work.
            """;
        return false;
    }

    private string RetryInput(string task, FleetVerdict verdict, string result) => run.Prompts.Render("retry",
        ("task", task),
        ("score", verdict.Score.ToString()),
        ("passingScore", Options.PassingScore.ToString()),
        ("feedback", string.IsNullOrWhiteSpace(verdict.Feedback) ? "(none given)" : verdict.Feedback),
        ("result", Cut(result, Options.MaxRetryResultLength)));

    /// <summary>The start of <paramref name="text"/>, with a note of its full length when it was cut.</summary>
    public static string Cut(string text, int max)
        => text.Length <= max ? text : $"{text[..max]}\n… (cut off, {text.Length:N0} characters in total)";

    /// <summary>
    /// Reviews the result against the task. A judge that submits no verdict passes the result, so it is never retried
    /// for nothing.
    /// </summary>
    public async Task<FleetVerdict> JudgeAsync(string task, string result, FleetTeamContext context)
    {
        FleetVerdict? verdict = null;
        var passingScore = Options.PassingScore;

        string SubmitVerdict(
            [System.ComponentModel.Description("How well the result accomplishes the task, from 1 (not at all) to 10 (perfectly).")] int score,
            [System.ComponentModel.Description("Concrete, actionable feedback: what is missing, wrong or could be better. Empty when the result is perfect.")] string feedback)
        {
            score = Math.Clamp(score, 1, 10);
            verdict = new FleetVerdict(score, score >= passingScore, feedback ?? "");
            return "The verdict is recorded. Reply with a one line confirmation.";
        }

        var judge = run.DefaultClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Judge",
            ChatOptions = new()
            {
                Instructions = run.Prompts.Render("judge", ("passingScore", passingScore.ToString())),
                Tools = [AIFunctionFactory.Create(SubmitVerdict, "submit_verdict", "Submit the verdict on the result.")],
            },
        });

        // Agents often deliver their work as files or canvas pages, so the judge sees both next to the team's final message.
        await judge.RunAsync(
            $"Task:\n{task}\n\nResult of the team:\n{result}\n\n{run.Workspace.Describe()}\n\n{run.Canvas.Describe()}",
            cancellationToken: context.CancellationToken);

        return verdict ?? new FleetVerdict(passingScore, true, "The judge gave no verdict.");
    }

    /// <summary>Hands a sub-task of an agent to a new team one level deeper, as long as the budget allows it.</summary>
    public async Task<string> DelegateToTeamAsync(string task, FleetTeamContext parent)
    {
        var budget = run.Budget;
        if (budget.IsSoftLimitReached)
        {
            run.Events.Publish("delegation.refused", new { teamId = parent.TeamId, attempt = parent.Attempt, reason = "The budget of this task is almost used up." });
            return "No team was created: the budget of this task is almost used up. Finish the sub-task yourself with what you have.";
        }

        var context = parent with { Depth = parent.Depth + 1 };
        var slots = budget.SubTeamSlots(context.Depth);
        await slots.WaitAsync(context.CancellationToken);
        try
        {
            var result = await RunTeamAsync(task, context);
            var files = run.Workspace.ListFiles();
            return files.Count == 0 ? result : $"{result}\n\nFiles in the shared workspace: {string.Join(", ", files)}";
        }
        finally
        {
            slots.Release();
        }
    }

    /// <summary>
    /// The functions agents of a team can reference in their YAML: the workspace, the canvas, the catalog's tools and,
    /// below the maximum depth, <c>delegate_to_team</c>.
    /// </summary>
    public List<AIFunction> AgentFunctions(FleetTeamContext context)
    {
        List<AIFunction> functions = [.. run.Workspace.Tools, .. run.Canvas.Tools, .. run.Tools];
        if (context.Depth < Options.MaxTeamDepth)
        {
            functions.Add(AIFunctionFactory.Create(
                ([System.ComponentModel.Description("The complete, self-contained description of the sub-task, including all context the team needs.")] string task)
                    => DelegateToTeamAsync(task, context),
                "delegate_to_team",
                "Delegate a sub-task to a new team of specialist agents that is designed and run for you, and get its final result back."));
        }

        return functions;
    }

    private sealed class TeamRun(string task, FleetTeamContext context, int teams)
    {
        public string Task { get; } = task;
        public FleetTeamContext Context { get; } = context;
        public int Teams { get; } = teams;
        public int Attempt { get; set; }
        public bool Done { get; set; }
        public string? BestResult { get; set; }
        public int BestScore { get; set; }
        public string? PreviousAttempt { get; set; }

        // The team's input: the task, and after a rejected result the task with the feedback and that result.
        public string Input { get; set; } = task;
        public object? Review { get; set; }
    }
}
