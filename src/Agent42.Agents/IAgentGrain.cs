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
    /// <paramref name="attachments"/> are images to send alongside the text as a multimodal
    /// turn; null or empty is a plain text turn. The default keeps existing 2-arg callers working.
    /// </summary>
    [Alias("ChatAsync")]
    IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null);

    /// <summary>
    /// Returns the model deployment this conversation is currently using, so the UI can
    /// restore the picker to the right selection when a conversation is reopened.
    /// </summary>
    [Alias("GetSelectedModelAsync")]
    Task<string> GetSelectedModelAsync();

    /// <summary>
    /// Binds a stored agent's persona (name, system prompt, model) into this conversation.
    /// Idempotent: once a conversation is bound the persona is immutable, so a second call
    /// is a no-op and can never flip the persona mid-conversation. Callers pass this on the
    /// first turn of a freshly-started agent chat; normal chats never call it.
    /// </summary>
    [Alias("BindAgentAsync")]
    Task BindAgentAsync(AgentBinding binding);

    /// <summary>
    /// Returns the persona bound to this conversation, or null for a normal chat. Lets the
    /// UI lock the model picker and label an agent chat when it is reopened.
    /// </summary>
    [Alias("GetAgentBindingAsync")]
    Task<AgentBinding?> GetAgentBindingAsync();

    /// <summary>
    /// Records that a document (by file name) has been ingested for this conversation, so the
    /// model is told the document exists and reliably calls <c>search_documents</c> to answer
    /// questions about it (documents are ingest-only and otherwise invisible to the model — ADR
    /// 0007). Idempotent per file name; called by the upload endpoint after a successful ingest.
    /// </summary>
    [Alias("RegisterDocumentAsync")]
    Task RegisterDocumentAsync(string fileName);

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
