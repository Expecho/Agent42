namespace Agent42.Agents;

/// <summary>
/// A conversational AI agent hosted as an Orleans grain.
/// The grain key identifies the conversation (e.g. a user or session id).
/// </summary>
[Alias("Agent42.Agents.IAgentGrain")]
public interface IAgentGrain : IGrainWithStringKey
{
    /// <summary>
    /// Sends a message to the agent and streams the assistant's reply back chunk by chunk.
    /// </summary>
    [Alias("ChatAsync")]
    IAsyncEnumerable<string> ChatAsync(string message);

    /// <summary>
    /// Returns the visible conversation history (user and assistant turns), so a
    /// client can resume a conversation later.
    /// </summary>
    [Alias("GetHistoryAsync")]
    Task<IReadOnlyList<ChatTurn>> GetHistoryAsync();

    /// <summary>
    /// Returns the prompt currently scheduled for this conversation, oldest first.
    /// </summary>
    [Alias("GetScheduledPromptsAsync")]
    Task<IReadOnlyList<ReminderInfo>> GetScheduledPromptsAsync();
}
