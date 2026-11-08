using System.ComponentModel;
using System.Globalization;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// The clarify phase of a fleet task started with "Ask me questions first" (ADR 0118). An interviewer agent on the task's
/// model questions the user, one question at a time through the task's question slot (<see cref="IFleetInteraction"/>),
/// until it calls <c>requirements_clear</c> with a brief. The brief replaces the request as the planner's input. The
/// result arrives through tool calls, like the planner's plan, so a provider that ignores structured output still works.
/// It fails open: a model that never clears, or fails, leaves the user's request as it was.
/// </summary>
public sealed class FleetClarifier(FleetRun run, string request, string files, string? previousRequest)
{
    /// <summary>The answer of the fixed "Enough, start the task" choice every question carries.</summary>
    public const string StartAnswer = "[start-now]";

    /// <summary>The longest brief accepted. A longer one is refused, never truncated.</summary>
    public const int MaxBriefLength = 6000;

    private const int MaxChoices = 4;

    private readonly Lock _gate = new();
    private int _asked;
    private bool _startNow;
    private string? _brief;
    private CancellationToken _cancellation;

    private int MaxQuestions => Math.Max(1, run.Options.MaxClarifyingQuestions);

    /// <summary>Interviews the user. Returns the brief, or <c>request</c> when none came out of it.</summary>
    public async Task<string> RunAsync(CancellationToken cancellationToken)
    {
        _cancellation = cancellationToken;
        run.Events.Publish("clarify.started", new { maxQuestions = MaxQuestions });
        var agent = run.DefaultClient.AsAIAgent(new ChatClientAgentOptions
        {
            Name = "Clarifier",
            ChatOptions = new()
            {
                Instructions = run.Prompts.Render("clarify", ("maxQuestions", MaxQuestions.ToString(CultureInfo.InvariantCulture))),
                Tools = [AIFunctionFactory.Create(AskUserAsync, "ask_user"), AIFunctionFactory.Create(RequirementsClear, "requirements_clear")],
            },
        });

        try
        {
            await agent.RunAsync(UserPrompt(), cancellationToken: cancellationToken);
        }
        // A failing interviewer leaves the task as it was; a stop must still stop it.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            run.Logger.LogWarning(ex, "Fleet task {TaskId}: the clarify phase failed", run.TaskId);
        }

        var brief = _brief ?? request;
        run.Events.Publish("clarify.done", new { brief, questions = _asked, startedNow = _startNow, cleared = _brief is not null });
        return brief;
    }

    private string UserPrompt()
        => $"The user's task:\n\n{request}"
            + (previousRequest is null ? "" : $"\n\nThis is a follow-up on an earlier task of the user: {previousRequest}")
            + files;

    [Description("Ask the user ONE question about the task and wait for the answer. Returns the answer.")]
    private async Task<string> AskUserAsync(
        [Description("The question. Required.")] string question = "",
        [Description("Up to four short answers the user can pick, when the answer is one of a few options. Optional.")] string[]? choices = null,
        [Description("A hint for the free text box, for example 'Or describe it'. Optional.")] string freeTextHint = "")
    {
        if (string.IsNullOrWhiteSpace(question))
        {
            return "The question is empty. Call ask_user again with a question.";
        }

        if (TakeQuestion() is { } refusal)
        {
            return refusal;
        }

        var offered = (choices ?? []).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).Take(MaxChoices)
            .Select(c => new FleetChoice(c, c)).Append(new FleetChoice(StartAnswer, "Enough, start the task")).ToList();
        var answer = await run.Interaction.AskAsync(
            question, offered, string.IsNullOrWhiteSpace(freeTextHint) ? "Your answer" : (freeTextHint ?? "").Trim(), _cancellation);
        if (answer == StartAnswer)
        {
            lock (_gate)
            {
                _startNow = true;
            }

            return "The user wants to start now. Call requirements_clear with the brief so far, and state what you assumed.";
        }

        return $"The user answered: {answer}";
    }

    // Counts the question, or says why none may be asked: the interview is over, and only requirements_clear is left.
    private string? TakeQuestion()
    {
        lock (_gate)
        {
            if (_startNow || _asked >= MaxQuestions)
            {
                return "No more questions are allowed. Call requirements_clear now, and state what you assumed.";
            }

            _asked++;
            return null;
        }
    }

    [Description("End the interview: give the brief that restates the task with every decision and assumption. Required.")]
    private string RequirementsClear([Description("The brief: goal, decisions, constraints, deliverables and assumptions.")] string brief = "")
    {
        if (string.IsNullOrWhiteSpace(brief))
        {
            return "The brief is missing. Call requirements_clear again with it.";
        }

        if (brief.Length > MaxBriefLength)
        {
            return $"The brief is {brief.Length:N0} characters, over the limit of {MaxBriefLength:N0}. Call requirements_clear again with a shorter brief.";
        }

        lock (_gate)
        {
            _brief = brief.Trim();
        }

        return "Requirements recorded. Reply with one line.";
    }
}
