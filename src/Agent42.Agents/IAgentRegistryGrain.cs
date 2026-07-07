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
    /// Returns the stored definition, so the caller gets the assigned id and timestamps.
    /// </summary>
    [Alias("SaveAsync")]
    Task<AgentDefinition> SaveAsync(AgentDefinition agent);

    /// <summary>Removes a stored agent. No-op if it isn't registered.</summary>
    [Alias("DeleteAsync")]
    Task DeleteAsync(string id);
}
