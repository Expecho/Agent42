namespace Agent42.Agents;

/// <summary>
/// The built-in agent shapes (<see cref="AgentDefinition.Kind"/>). <c>""</c> is an ordinary user agent.
/// </summary>
public static class AgentKinds
{
    /// <summary>The seeded research agent (ADR 0082): a plan/search/reflect loop with a live checklist.</summary>
    public const string Research = "research";

    /// <summary>True for the research kind. A null or unknown kind is an ordinary agent.</summary>
    public static bool IsResearch(string? kind) => string.Equals(kind, Research, StringComparison.Ordinal);
}
