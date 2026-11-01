namespace Agent42.Backend;

/// <summary>
/// The interaction of a fleet task started from a chat (ADR 0115). Nobody watches such a task on the page, so a
/// question that still reaches the interaction (in practice an MCP elicitation: the plan approval and the Copilot
/// permissions are approved before they ask) is declined at once with <c>"n"</c>, and logged as a <c>question.auto</c>
/// event so the page shows it. Both elicitation modes read <c>"n"</c> as a decline.
/// </summary>
public sealed class DecliningFleetInteraction(IFleetEventSink events) : IFleetInteraction
{
    public const string Answer = "n";

    public Task<string> AskAsync(string question, IReadOnlyList<FleetChoice> choices, string freeTextHint, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        events.Publish("question.auto", new { question = question.Trim(), answer = "declined" });
        return Task.FromResult(Answer);
    }
}
