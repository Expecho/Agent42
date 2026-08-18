namespace Agent42.Agents.StoredAgents;

/// <summary>
/// A persona snapshot bound into a conversation: the source agent's id plus the name,
/// system prompt, and model captured at bind time. Used both as the input to
/// <see cref="IAgentGrain.BindAgentAsync"/> and the output of
/// <see cref="IAgentGrain.GetAgentBindingAsync"/>. Snapshotting (rather than referencing
/// the agent live) means editing or deleting the source agent never changes an existing
/// chat — see ADR 0003.
/// </summary>
/// <remarks>
/// <see cref="EnabledTools"/> carries the source agent's selectable-tools set (ADR 0021) into the bind, as
/// a list of <see cref="ToolCatalog"/> keys, or <c>null</c> for "all selectable tools". The grain
/// materializes it to a concrete set when snapshotting into conversation state, so an old agent (null)
/// pins all-selectable and no persona ever inherits the user's per-chat default.
/// </remarks>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentBinding")]
public sealed record AgentBinding(
    [property: Id(0)] string AgentId,
    [property: Id(1)] string Name,
    [property: Id(2)] string SystemPrompt,
    [property: Id(3)] string Model,
    [property: Id(4)] IReadOnlyList<string>? EnabledTools = null);
