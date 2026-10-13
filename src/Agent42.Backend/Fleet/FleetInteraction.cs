namespace Agent42.Backend;

/// <summary>
/// Where a fleet task publishes what it does (ADR 0097). Every UI state on <c>/fleet/</c> is rebuilt from these events,
/// and the task service persists and fans them out. <paramref name="data"/> is serialized as JSON.
/// </summary>
public interface IFleetEventSink
{
    void Publish(string type, object data);
}

/// <summary>A choice the page shows as a button: the answer it sends, and its label.</summary>
public sealed record FleetChoice(string Answer, string Label);

/// <summary>
/// Asks the task's user a question (plan approval, a Copilot permission, an MCP elicitation) and waits for the answer.
/// Questions of one task take turns. A stopped task cancels its open question.
/// </summary>
public interface IFleetInteraction
{
    Task<string> AskAsync(string question, IReadOnlyList<FleetChoice> choices, string freeTextHint, CancellationToken cancellationToken);
}
