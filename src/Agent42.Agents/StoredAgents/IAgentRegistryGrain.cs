namespace Agent42.Agents;

/// <summary>
/// Stores the agent personas of a user (the grain key), so the UI can list them and
/// let the user create, edit, delete, and start chats from them. Keyed per user like
/// <see cref="IConversationIndexGrain"/>; a persona is snapshotted into a conversation
/// when a chat is started from it, so editing or deleting one never disturbs an
/// existing chat.
/// </summary>
[Alias("Agent42.Agents.IAgentRegistryGrain")]
public interface IAgentRegistryGrain : IGrainWithStringKey
{
    /// <summary>Returns all of the user's stored agents, most recently updated first.</summary>
    [Alias("GetAllAsync")]
    Task<IReadOnlyList<AgentDefinition>> GetAllAsync();

    /// <summary>Returns a single stored agent by id, or null if it isn't registered.</summary>
    [Alias("GetAsync")]
    Task<AgentDefinition?> GetAsync(string id);

    /// <summary>
    /// Upserts an agent: inserts a new one (assigning a fresh id and timestamps) when
    /// <see cref="AgentDefinition.Id"/> is empty or unknown, otherwise replaces the
    /// existing one in place (refreshing <see cref="AgentDefinition.UpdatedAt"/>).
    ///
    /// <para>Owns the agent's API-key lifecycle (ADR 0022): mints a key on insert and, when
    /// <paramref name="regenerateKey"/> is set, on update — keeping the global
    /// <see cref="IAgentKeyDirectoryGrain"/> in sync. The raw key rides back in the result
    /// <b>once</b> (only when just minted); only its hash is persisted. A plain update keeps the
    /// existing hash. The incoming <paramref name="agent"/>'s <see cref="AgentDefinition.ApiKeyHash"/>
    /// is ignored — the grain is the authority on it.</para>
    /// </summary>
    [Alias("SaveAsync")]
    Task<AgentSaveResult> SaveAsync(AgentDefinition agent, bool regenerateKey = false);

    /// <summary>Removes a stored agent (and deregisters its key). No-op if it isn't registered.</summary>
    [Alias("DeleteAsync")]
    Task DeleteAsync(string id);
}

/// <summary>
/// The outcome of <see cref="IAgentRegistryGrain.SaveAsync"/>: the stored definition plus the raw API
/// key <b>iff</b> one was just minted (insert, or update with regenerate), else <c>null</c> (ADR 0022).
/// The raw key is never persisted and never returned again, so this is the single moment it exists.
/// Crosses the grain boundary, so it is Orleans-serializable with explicit member ids.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentSaveResult")]
public sealed record AgentSaveResult(
    [property: Id(0)] AgentDefinition Agent,
    [property: Id(1)] string? PlaintextApiKey);
