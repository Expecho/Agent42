namespace Agent42.Agents;

/// <summary>
/// Summary of a conversation shown in the conversation list.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ConversationInfo")]
public sealed record ConversationInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] DateTimeOffset UpdatedAt,
    // Identity of the agent that produced this conversation, snapshotted at creation so the
    // sidebar can group agent chats under a collapsible header (ADR 0003 snapshot rationale:
    // a later rename/delete of the source agent must not blank the group label). Null for a
    // regular (non-agent) chat. Trailing optional [Id(3)]/[Id(4)] → no state migration.
    [property: Id(3)] string? AgentId = null,
    [property: Id(4)] string? AgentName = null,
    // Whether the user pinned this conversation to the top of the sidebar (ADR 0045).
    // Trailing optional [Id(5)] with a false default → no state migration: an existing
    // persisted document carries no "pinned" key and deserializes to unpinned.
    [property: Id(5)] bool Pinned = false);

/// <summary>
/// Tracks the conversations of a user (the grain key) so the UI can list them
/// and let the user resume a chat later.
/// </summary>
[Alias("Agent42.Agents.IConversationIndexGrain")]
public interface IConversationIndexGrain : IGrainWithStringKey
{
    /// <summary>
    /// Registers activity on a conversation. New conversations get a title
    /// derived from <paramref name="firstPrompt"/>, and record <paramref name="agentId"/>/
    /// <paramref name="agentName"/> when the conversation was started by a stored agent.
    /// The agent identity is written only when the entry is first created; later touches
    /// (which pass null) preserve it and just bump the timestamp.
    /// </summary>
    [Alias("TouchAsync")]
    Task TouchAsync(string conversationId, string firstPrompt, string? agentId = null, string? agentName = null);

    /// <summary>
    /// Sets or clears the pinned flag on a conversation. Idempotent, and a no-op
    /// when the conversation isn't listed.
    /// </summary>
    [Alias("SetPinnedAsync")]
    Task SetPinnedAsync(string conversationId, bool pinned);

    /// <summary>Returns all conversations, pinned first, then most recently used first.</summary>
    [Alias("GetConversationsAsync")]
    Task<IReadOnlyList<ConversationInfo>> GetConversationsAsync();

    /// <summary>
    /// Removes a conversation from the index. No-op if it isn't listed.
    /// </summary>
    [Alias("RemoveAsync")]
    Task RemoveAsync(string conversationId);
}
