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
    /// <paramref name="ephemeral"/> is the incognito flag (ADR 0034): when true the turn skips its
    /// session-state write, so the conversation leaves no trace in Cosmos. It
    /// is turn-scoped and never persisted. The default keeps existing callers working.
    ///
    /// <paramref name="interactive"/> is whether a live client is watching this stream (ADR 0070). It
    /// gates the follow-up suggestion call (ADR 0036), which costs one extra model call per turn. A
    /// caller that <b>drains</b> the stream for its text — the MCP surface — passes false, because that
    /// call's result could never be read. Turn-scoped like the two above; the default keeps the browser
    /// path and every existing caller unchanged.
    ///
    /// <paramref name="clientTools"/> names the client tools (ADR 0089) the browser streaming this turn can run —
    /// already intersected with <see cref="ClientToolLimits.ToolNames"/> by the endpoint. Only an interactive turn
    /// with a non-empty list gets a broker; every other run answers a client tool "unavailable" at once.
    ///
    /// <paramref name="replacesLastTurn"/> says this turn re-runs one <see cref="TruncateLastTurnAsync"/> just
    /// removed (ADR 0092), so the outbound webhook event is marked as superseding the previous one (ADR 0094). A
    /// parameter, never a field the truncate sets: the two are separate calls, and a background turn running between
    /// them would consume a field.
    /// </summary>
    [Alias("ChatAsync")]
    IAsyncEnumerable<AgentStreamEvent> ChatAsync(
        string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null,
        bool ephemeral = false, bool interactive = true,
        IReadOnlyList<string>? clientTools = null, bool replacesLastTurn = false);

    /// <summary>
    /// Completes a client tool call the interactive turn is waiting on (ADR 0089) with the browser's result, and
    /// returns false when no such call is waiting (unknown, already answered, timed out or stopped).
    ///
    /// <para><see cref="AlwaysInterleaveAttribute"/> for the <see cref="StopAsync"/> reason, and more so: the turn
    /// is suspended INSIDE a tool invocation waiting for exactly this call, so a queued call would deadlock until
    /// the wait times out. Not <see cref="OneWay"/>, so the endpoint can answer 404 for a late result.</para>
    /// </summary>
    [Alias("SubmitClientToolResultAsync")]
    [AlwaysInterleave]
    Task<bool> SubmitClientToolResultAsync(string toolCallId, string result);

    /// <summary>
    /// The client tool calls the interactive turn is waiting on (ADR 0089), so a browser that lost its stream can
    /// answer them (ADR 0046 recovery). Never a background run's calls: those have no broker. Interleaves, because
    /// it is asked exactly while a turn is suspended in a wait.
    /// </summary>
    [Alias("GetPendingClientToolCallsAsync")]
    [AlwaysInterleave]
    Task<IReadOnlyList<PendingClientToolCall>> GetPendingClientToolCallsAsync();

    /// <summary>The conversation's shared state (ADR 0089): its JSON object as text (null when empty) and version.</summary>
    [Alias("GetSharedStateAsync")]
    [AlwaysInterleave]
    Task<SharedStateSnapshot> GetSharedStateAsync();

    /// <summary>
    /// Replaces the conversation's shared state with a user edit (ADR 0089), if <paramref name="baseVersion"/> is
    /// still current. Interleaves so an edit during a running turn does not wait for the turn; it is written at
    /// once only when no run is in flight, and otherwise persisted at the end of the run (see
    /// <c>AgentGrain.SharedState.cs</c>).
    /// </summary>
    [Alias("SetSharedStateAsync")]
    [AlwaysInterleave]
    Task<SharedStateWriteResult> SetSharedStateAsync(string json, long baseVersion);

    /// <summary>
    /// Stops the turn a browser is currently streaming from this activation (ADR 0078), and is a
    /// silent no-op when there is none. The turn does not vanish: the model run is cancelled, and
    /// whatever text had already streamed is written to the session as a normal turn — so what the
    /// user saw is what the conversation keeps.
    ///
    /// <para><see cref="AlwaysInterleaveAttribute"/> is load-bearing, and it belongs on this
    /// declaration rather than the implementation (Orleans reads it here). A streaming grain method
    /// runs as a series of interleaving <c>MoveNext</c> requests, but a turn has long gaps where the
    /// activation is genuinely busy — before the first token, and across a tool call — and a plain
    /// call would queue behind those, arriving after the turn it was meant to stop.</para>
    ///
    /// <para><b>Deliberately NOT <see cref="OneWay"/></b>, unlike
    /// <see cref="EnqueuePromptAsync"/>: a one-way call returns before the grain runs the method and
    /// swallows failures, which would make the client's "the stop call failed, fall back to aborting
    /// the fetch" branch unreachable.</para>
    ///
    /// <para>Reaches a live browser turn only, and background turns are out of reach for two different
    /// reasons worth keeping straight. <c>/api/agent-run</c> and ADR 0070's <c>ask_agent</c> drain the same
    /// core as a browser turn, so their model run does carry a token — they simply never publish the
    /// handle. A fired reminder does not come through that core at all (it calls <c>RunAsync</c> directly,
    /// with no token), so it is not cancellable even in principle. Both are deliberate: nobody is watching
    /// those turns, and cancelling one would be a turn lost for no one's benefit.</para>
    /// </summary>
    [Alias("StopAsync")]
    [AlwaysInterleave]
    Task StopAsync();

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
    /// Returns this conversation's artifacts, oldest first, with every version's metadata. The artifact routes
    /// read it to serve only ids that exist in this conversation, whatever id a client asks for.
    /// </summary>
    [Alias("GetArtifactsAsync")]
    Task<IReadOnlyList<ArtifactRecord>> GetArtifactsAsync();

    /// <summary>
    /// Re-projects this conversation into the chat search index (ADR 0061) and returns how many
    /// turns were indexed. Idempotent — ordinals come from the same <see cref="GetHistoryAsync"/>
    /// projection the live write path uses, so a re-run overwrites rather than duplicates.
    /// </summary>
    [Alias("ReindexAsync")]
    Task<int> ReindexAsync();

    /// <summary>
    /// Removes the conversation's last whole turn (its user message and everything after it) so the caller can
    /// re-run it: edit and regenerate (ADR 0092). <paramref name="expectedUserTurnCount"/> and
    /// <paramref name="expectedLastUserTextHash"/> are the concurrency token the client read from
    /// <see cref="GetHistoryAsync"/>: the number of user turns and the hex SHA-256 of the last one's text. Refused
    /// with no change while a run is in flight, for a Copilot chat, on a stale token, when no model is configured
    /// and when there is nothing to remove. <paramref name="ephemeral"/> keeps the removal in memory (ADR 0034).
    /// </summary>
    [Alias("TruncateLastTurnAsync")]
    Task<HistoryEditOutcome> TruncateLastTurnAsync(
        int expectedUserTurnCount, string expectedLastUserTextHash, bool ephemeral);

    /// <summary>
    /// Builds a fork of this conversation through the exchange that starts at projected user turn
    /// <paramref name="throughUserTurn"/> (0-based), without changing this conversation (ADR 0092). Same token and
    /// refusals as <see cref="TruncateLastTurnAsync"/>.
    /// </summary>
    [Alias("CreateForkAsync")]
    Task<ConversationForkResult> CreateForkAsync(
        int throughUserTurn, int expectedUserTurnCount, string expectedLastUserTextHash);

    /// <summary>
    /// Initializes this (new, empty) conversation from a fork payload: copies every kept blob under this
    /// conversation's key, binds the persona snapshot and writes the session (ADR 0092). Returns false, changing
    /// nothing, when this conversation already has a session or a persona.
    /// </summary>
    [Alias("InitializeFromForkAsync")]
    Task<bool> InitializeFromForkAsync(ConversationFork fork);
}
