using Agent42.Agents;



namespace Agent42.Backend;

// Client payload for creating (POST) or updating (PUT) a stored agent. The id and
// timestamps are owned by the registry grain, so they aren't part of the request.
// Continuous is the history-mode flag (ADR 0016); EnabledTools is the agent's selectable-tools
// set (ADR 0021, a list of ToolCatalog keys, null = all). RegenerateKey (ADR 0022, PUT only)
// rotates the agent's API key. All are trailing defaults so an old-shape client that omits them
// stores false = per-chat, null = all tools, and never regenerates.
public record AgentUpsertRequest(string Name, string Description, string Model, string SystemPrompt,
    bool Continuous = false, IReadOnlyList<string>? EnabledTools = null, bool RegenerateKey = false);

// What POST/PUT /api/agents return (ADR 0022): the stored agent, plus the raw API key IFF one was just
// minted (create, or update with regenerateKey) — otherwise null. The raw key is shown exactly once and
// never returned again; GET /api/agents carries only the definition (its ApiKeyHash is non-sensitive but
// never a raw key). Additive to the old bare-AgentDefinition body: existing clients that ignore it still work.
public record AgentSaveResponse(AgentDefinition Agent, string? ApiKey);
