namespace Agent42.Agents;

/// <summary>
/// A stored, user-defined agent persona: a reusable name, description, model, and
/// system prompt the user can start a chat from. Kept in the user's
/// <see cref="IAgentRegistryGrain"/>. Distinct from a conversation — starting a chat
/// from an agent snapshots this persona into the conversation grain at bind time.
/// </summary>
/// <remarks>
/// <see cref="Continuous"/> is the per-agent history mode: <c>false</c> (default) means
/// every start gets a fresh per-chat conversation; <c>true</c> routes every start onto
/// one shared, ongoing conversation (ADR 0016). It is a trailing defaulted field, so
/// agents persisted before it deserialize as per-chat with no migration.
///
/// <see cref="EnabledTools"/> is the agent's selectable-tools set (ADR 0021): a list of
/// <see cref="ToolCatalog"/> keys, or <c>null</c> for "all selectable tools" (the default, so
/// agents persisted before it keep every tool with no migration). It is snapshotted into the
/// conversation at bind time (materialized to a concrete set), so editing an agent's tools
/// affects only <i>future</i> chats — consistent with ADR 0003.
/// </remarks>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentDefinition")]
public sealed record AgentDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Description,
    [property: Id(3)] string Model,
    [property: Id(4)] string SystemPrompt,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset UpdatedAt,
    [property: Id(7)] bool Continuous = false,
    [property: Id(8)] IReadOnlyList<string>? EnabledTools = null);
