namespace Agent42.Agents;

/// <summary>
/// A stored, user-defined agent persona: a reusable name, description, model, and
/// system prompt the user can start a chat from. Kept in the user's
/// <see cref="IAgentRegistryGrain"/>. Distinct from a conversation — starting a chat
/// from an agent snapshots this persona into the conversation grain at bind time.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentDefinition")]
public sealed record AgentDefinition(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Description,
    [property: Id(3)] string Model,
    [property: Id(4)] string SystemPrompt,
    [property: Id(5)] DateTimeOffset CreatedAt,
    [property: Id(6)] DateTimeOffset UpdatedAt);
