namespace Agent42.Agents.StoredAgents;

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
///
/// <see cref="ApiKeyHash"/> is the SHA-256 hash of the agent's external run key (ADR 0022) — the
/// digest only; the raw key is returned once at mint time and never persisted. Empty (the default)
/// means no key has been minted, so agents persisted before ADR 0022 deserialize keyless with no
/// migration and get a key on their next regenerate. Keyed as <c>[Id(9)]</c> because 7 and 8 are
/// already taken (<see cref="Continuous"/>, <see cref="EnabledTools"/>).
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
    [property: Id(8)] IReadOnlyList<string>? EnabledTools = null,
    [property: Id(9)] string ApiKeyHash = "");
