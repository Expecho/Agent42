using Orleans.Concurrency;

namespace Agent42.Agents;

/// <summary>
/// A conversational AI agent hosted as an Orleans grain.
/// The grain key identifies the conversation (e.g. a user or session id).
/// </summary>
[Alias("Agent42.Agents.IAgentGrain")]
public interface IAgentGrain : IGrainWithStringKey
{
    /// <summary>
    /// Runs a prompt (with optional image attachments) against this conversation in the background and
    /// persists the turn — the async run behind <c>POST /api/agent-run</c> (ADR 0022). <see cref="OneWay"/>:
    /// it returns immediately and the grain processes the turn on its own, so the HTTP handler can answer
    /// 202 without holding the connection. Best-effort — there is no caller to surface a failure to, so the
    /// background run logs and swallows errors. Reuses the full chat turn path (vision gate, image
    /// generation, document retrieval), so an image rides the same <see cref="AttachmentRef"/> path as a
    /// browser turn; <paramref name="attachments"/> null/empty is a text-only turn.
    /// </summary>
    [Alias("EnqueuePromptAsync")]
    [OneWay]
    Task EnqueuePromptAsync(string message, IReadOnlyList<AttachmentRef>? attachments);

    /// <summary>
    /// Sends a message to the agent and streams the turn back as typed events:
    /// reply-text deltas, tool-call start/end notifications, and a closing
    /// <see cref="AgentStreamEventType.Done"/> event with the turn's token usage.
    /// When <paramref name="model"/> is a qualified model id (<c>provider:model</c>, ADR 0013) it
    /// switches the conversation to that model for this and subsequent turns; null keeps the current
    /// model. A bare, unqualified id is a legacy one and resolves against the default provider.
    /// <paramref name="attachments"/> are images to send alongside the text as a multimodal
    /// turn; null or empty is a plain text turn. The default keeps existing 2-arg callers working.
    /// <paramref name="location"/> is the browser's current position, if it has one and the user has
    /// authorized sharing it — see <see cref="UserLocation"/> for why a position has to ride the chat
    /// request rather than be fetched by the tool. It is <b>turn-scoped</b>: the grain never persists
    /// it, so a later turn without one does not inherit this turn's fix (ADR 0012).
    /// <paramref name="ephemeral"/> is the incognito flag (ADR 0034): when true the turn skips its
    /// session-state write, so the conversation leaves no trace in Cosmos. Like <paramref name="location"/>
    /// it is turn-scoped and never persisted. The default keeps existing callers working.
    ///
    /// <paramref name="interactive"/> is whether a live client is watching this stream (ADR 0070). It
    /// gates the follow-up suggestion call (ADR 0036), which costs one extra model call per turn. A
    /// caller that <b>drains</b> the stream for its text — the MCP surface — passes false, because that
    /// call's result could never be read. Turn-scoped like the two above; the default keeps the browser
    /// path and every existing caller unchanged.
    /// </summary>
    [Alias("ChatAsync")]
    IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null,
        UserLocation? location = null, bool ephemeral = false, bool interactive = true);

    /// <summary>
    /// Returns the qualified model id this conversation is currently using, so the UI can
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
    /// Records that a document (by file name) has been ingested for this conversation, so the model is
    /// told the document exists and the always-on retrieval provider is attached to answer questions
    /// about it (ADR 0008; documents are ingest-only and otherwise invisible to the model — ADR 0007).
    /// Idempotent per file name; called by the upload endpoint after a successful ingest.
    /// </summary>
    [Alias("RegisterDocumentAsync")]
    Task RegisterDocumentAsync(string fileName);

    /// <summary>
    /// Sets the tools this conversation may use (ADR 0021) as a list of <see cref="ToolCatalog"/> keys —
    /// a per-conversation override that beats the user's default. <c>null</c> resets to inherit ("all
    /// selectable tools" via the user default). Unknown keys are dropped server-side. Rebuilds the agent
    /// so the change takes effect from the next turn and persists it, surviving deactivation.
    /// </summary>
    [Alias("SetEnabledToolsAsync")]
    Task SetEnabledToolsAsync(IReadOnlyList<string>? tools);

    /// <summary>
    /// Returns the <b>effective</b> selectable-tools set for this conversation — the concrete list of
    /// <see cref="ToolCatalog"/> keys after resolving override ?? user-default ?? all (ADR 0021). Never
    /// null, so the UI can render the current selection directly. Capability still gates what actually
    /// reaches the model; this is the user-intent layer only.
    /// </summary>
    [Alias("GetEnabledToolsAsync")]
    Task<IReadOnlyList<string>> GetEnabledToolsAsync();

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

    /// <summary>
    /// Re-projects this conversation into the chat search index (ADR 0061) and returns how many
    /// turns were indexed. Idempotent — ordinals come from the same <see cref="GetHistoryAsync"/>
    /// projection the live write path uses, so a re-run overwrites rather than duplicates.
    /// </summary>
    [Alias("ReindexAsync")]
    Task<int> ReindexAsync();
}
