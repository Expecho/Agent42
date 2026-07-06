namespace Agent42.Agents;

/// <summary>
/// A conversational AI agent hosted as an Orleans grain.
/// The grain key identifies the conversation (e.g. a user or session id).
/// </summary>
[Alias("Agent42.Agents.IAgentGrain")]
public interface IAgentGrain : IGrainWithStringKey
{
    /// <summary>
    /// Sends a message to the agent and streams the turn back as typed events:
    /// reply-text deltas, tool-call start/end notifications, and a closing
    /// <see cref="AgentStreamEventType.Done"/> event with the turn's token usage.
    /// When <paramref name="model"/> is a deployment name it switches the conversation to
    /// that model for this and subsequent turns; null keeps the current model.
    /// </summary>
    [Alias("ChatAsync")]
    IAsyncEnumerable<AgentStreamEvent> ChatAsync(string message, string? model);

    /// <summary>
    /// Returns the model deployment this conversation is currently using, so the UI can
    /// restore the picker to the right selection when a conversation is reopened.
    /// </summary>
    [Alias("GetSelectedModelAsync")]
    Task<string> GetSelectedModelAsync();

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

    /// <summary>
    /// Permanently deletes this conversation: unregisters its durable reminders and
    /// clears the persisted session, so nothing is left to re-fire or resume.
    /// </summary>
    [Alias("PurgeAsync")]
    Task PurgeAsync();
}
