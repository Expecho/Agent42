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
///
/// <see cref="ApiKeyHash"/> is the SHA-256 hash of the agent's external run key (ADR 0022) — the
/// digest only; the raw key is returned once at mint time and never persisted. Empty (the default)
/// means no key has been minted, so agents persisted before ADR 0022 deserialize keyless with no
/// migration and get a key on their next regenerate. Keyed as <c>[Id(9)]</c> because 7 and 8 are
/// already taken (<see cref="Continuous"/>, <see cref="EnabledTools"/>).
///
/// <see cref="McpExposed"/> opts the agent into the server-side MCP surface (ADR 0070): with it set,
/// a holder of the agent's ADR 0022 key can call it as a tool over <c>/api/agent-mcp</c> and read the
/// reply back. Trailing and defaulted, so every agent stored before it deserializes unexposed with no
/// migration. Like <see cref="Continuous"/> it is <b>routing, not persona</b>, so it is deliberately
/// NOT snapshotted into <see cref="AgentBinding"/> — the call ADR 0016 made for the same reason.
///
/// <para><b>Turning it on upgrades an already-distributed key from write-only to read.</b> ADR 0022's
/// key could only push a prompt in; this surface hands the reply back, which can carry RAG excerpts
/// (ADR 0007), the user's street address (ADR 0012), tool and web content, and prior turns. A key
/// handed out months ago silently gains that reach the moment this flips on, which is why the editor
/// offers a regenerate alongside it.</para>
///
/// <para><see cref="Kind"/> marks a built-in agent shape: <c>null</c> (the default) is an ordinary user
/// agent, <see cref="AgentKinds.Research"/> is the seeded research agent, and <see cref="AgentKinds.Copilot"/>
/// runs on the GitHub Copilot SDK. Research is set ONLY by seeding
/// (<c>IAgentRegistryGrain.EnsureResearchAgentAsync</c>). Copilot is the one kind a create request may
/// claim. Every update preserves the stored kind, so no request can change or clear it. Unlike <see cref="Continuous"/> it IS persona: it changes how the
/// model runs, so it is snapshotted into <see cref="AgentBinding"/>. Trailing and defaulted, so every
/// stored agent deserializes as an ordinary one with no migration. Nullable rather than <c>""</c> on
/// purpose: the Cosmos JSON path binds a key a document lacks to <c>null</c>, not to the C# default, so a
/// non-null declaration would be a lie about every agent stored before it.</para>
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
    [property: Id(9)] string ApiKeyHash = "",
    [property: Id(10)] bool McpExposed = false,
    [property: Id(11)] string? Kind = null);
