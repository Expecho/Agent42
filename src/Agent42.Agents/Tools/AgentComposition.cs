using Microsoft.Agents.AI;

namespace Agent42.Agents;

/// <summary>
/// What one <c>BuildAgent</c> produced (ADR 0106): the agent and the switches that describe it. The grain assigns all
/// four together in <c>ApplyComposition</c>, so the build itself writes no field and the switches can never describe a
/// different agent from the one in use.
/// </summary>
/// <param name="Agent">The agent the conversation runs.</param>
/// <param name="ResearchTodos">The research loop's todo provider (ADR 0082), or null for any other composition.</param>
/// <param name="CopilotChat">True when the agent is the Copilot composition (ADR 0085).</param>
/// <param name="CopilotUnavailable">Why the Copilot composition could not be built, shown instead of a reply, or null.</param>
public sealed record AgentComposition(
    AIAgent Agent, TodoProvider? ResearchTodos, bool CopilotChat, string? CopilotUnavailable)
{
    /// <summary>The Copilot composition, which has no research loop.</summary>
    public static AgentComposition Copilot(AIAgent agent, string? unavailable)
        => new(agent, ResearchTodos: null, CopilotChat: true, CopilotUnavailable: unavailable);
}
