namespace Agent42.Agents;

/// <summary>
/// A user-created, user-named group of conversations (ADR 0073). A first-class entity rather than a
/// label denormalized onto <see cref="ConversationInfo"/>, so an EMPTY group can exist (it is a valid
/// drop target) and so the user's manual ordering has somewhere to live.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ChatGroup")]
public sealed record ChatGroup(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    // Position in the user's manual ordering, ascending. ADR 0045 rejected manual drag ordering of
    // CONVERSATIONS and that rejection stands — chats still sort by recency everywhere, including
    // inside a group. This orders the groups themselves, which have no recency of their own.
    [property: Id(2)] int Order);

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
    [property: Id(5)] bool Pinned = false,
    // The custom group this conversation is filed under, or null (ADR 0073). At most one — a
    // conversation is in one group or none. Same trailing-optional shape as Pinned above, for the
    // same reason: an existing document carries no "groupId" key and deserializes to ungrouped.
    // A value naming a group that no longer exists renders as ungrouped rather than disappearing.
    [property: Id(6)] string? GroupId = null);

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

    /* ————— custom conversation groups (ADR 0073) ————— */

    /// <summary>Returns the user's groups in their manual order.</summary>
    [Alias("GetGroupsAsync")]
    Task<IReadOnlyList<ChatGroup>> GetGroupsAsync();

    /// <summary>
    /// Creates a group with a trimmed, length-capped name and appends it to the end of the manual
    /// order. Returns the created group — the caller needs the minted id — or <c>null</c> when the
    /// per-user cap is already reached.
    /// </summary>
    [Alias("CreateGroupAsync")]
    Task<ChatGroup?> CreateGroupAsync(string name);

    /// <summary>
    /// Renames a group. No-op when the group isn't known or the name is unchanged.
    /// </summary>
    [Alias("RenameGroupAsync")]
    Task RenameGroupAsync(string groupId, string name);

    /// <summary>
    /// Deletes a group and UNGROUPS its conversations in the same write. Conversations are never
    /// deleted by this. No-op when the group isn't known.
    /// </summary>
    [Alias("DeleteGroupAsync")]
    Task DeleteGroupAsync(string groupId);

    /// <summary>
    /// Applies a manual order from a list of group ids: recognised ids take positions in the order
    /// given, ids that name no group are ignored, and any group the caller omitted keeps its
    /// relative order AFTER the named ones — a stale client list must never drop a group.
    /// </summary>
    [Alias("ReorderGroupsAsync")]
    Task ReorderGroupsAsync(IReadOnlyList<string> groupIds);

    /// <summary>
    /// Files a conversation under a group, or clears its group when <paramref name="groupId"/> is
    /// null. A non-null id naming no existing group is a NO-OP, deliberately not a silent clear:
    /// clearing is what null means, and conflating the two would let a stale client wipe a filing
    /// the user still has. A no-op too when the conversation isn't listed.
    /// </summary>
    [Alias("SetGroupAsync")]
    Task SetGroupAsync(string conversationId, string? groupId);
}
