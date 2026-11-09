namespace Agent42.Agents;

/// <summary>
/// The agent shapes (<see cref="AgentDefinition.Kind"/>). <c>null</c> or <c>""</c> is an ordinary user agent.
/// </summary>
public static class AgentKinds
{
    /// <summary>The seeded research agent (ADR 0082): a plan/search/reflect loop with a live checklist.</summary>
    public const string Research = "research";

    /// <summary>
    /// A user-created agent that runs on the GitHub Copilot SDK's agent loop instead of a
    /// <c>ChatClientAgent</c>. Unlike <see cref="Research"/> a user may choose it, on create only.
    /// </summary>
    public const string Copilot = "copilot";

    /// <summary>
    /// The fleet analyzer panel's agent (ADR 0123): the kind itself fixes its tools, so there is no selection to widen.
    /// Never stored in the registry and never user-selectable.
    /// </summary>
    public const string FleetAnalyzer = "fleet-analyzer";

    /// <summary>True for the fleet-analyzer kind. A null or unknown kind is an ordinary agent.</summary>
    public static bool IsFleetAnalyzer(string? kind) => string.Equals(kind, FleetAnalyzer, StringComparison.Ordinal);

    /// <summary>True for the research kind. A null or unknown kind is an ordinary agent.</summary>
    public static bool IsResearch(string? kind) => string.Equals(kind, Research, StringComparison.Ordinal);

    /// <summary>True for the copilot kind. A null or unknown kind is an ordinary agent.</summary>
    public static bool IsCopilot(string? kind) => string.Equals(kind, Copilot, StringComparison.Ordinal);

    /// <summary>
    /// The kinds a create request may claim: none, or <see cref="Copilot"/>. <see cref="Research"/> stays
    /// seed-only (ADR 0082).
    /// </summary>
    public static bool IsUserSelectable(string? kind) => string.IsNullOrEmpty(kind) || IsCopilot(kind);

    /// <summary>
    /// A research or copilot agent is per-chat only (ADR 0016). Research turns are the largest there are, and a
    /// copilot chat's transcript lives outside the grain. The registry forces <c>Continuous</c> off for both.
    /// </summary>
    public static bool IsPerChatOnly(string? kind) => IsResearch(kind) || IsCopilot(kind);
}
