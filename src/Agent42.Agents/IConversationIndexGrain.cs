namespace Agent42.Agents;

/// <summary>
/// Summary of a conversation shown in the conversation list.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ConversationInfo")]
public sealed record ConversationInfo(
    [property: Id(0)] string Id,
    [property: Id(1)] string Title,
    [property: Id(2)] DateTimeOffset UpdatedAt);

/// <summary>
/// Tracks the conversations of a user (the grain key) so the UI can list them
/// and let the user resume a chat later.
/// </summary>
[Alias("Agent42.Agents.IConversationIndexGrain")]
public interface IConversationIndexGrain : IGrainWithStringKey
{
    /// <summary>
    /// Registers activity on a conversation. New conversations get a title
    /// derived from <paramref name="firstPrompt"/>.
    /// </summary>
    [Alias("TouchAsync")]
    Task TouchAsync(string conversationId, string firstPrompt);

    /// <summary>Returns all conversations, most recently used first.</summary>
    [Alias("GetConversationsAsync")]
    Task<IReadOnlyList<ConversationInfo>> GetConversationsAsync();
}
