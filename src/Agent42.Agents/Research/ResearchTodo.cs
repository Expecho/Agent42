namespace Agent42.Agents;

/// <summary>
/// One item of the research agent's live checklist (ADR 0082), projected from Agent Framework's
/// <c>TodoItem</c> for the <see cref="AgentStreamEventType.Todos"/> event. A record of our own rather than the
/// framework type, because this one crosses the Orleans boundary and needs a stable serializer contract.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ResearchTodo")]
public sealed record ResearchTodo(
    [property: Id(0)] int Id,
    [property: Id(1)] string Title,
    [property: Id(2)] bool Done);
