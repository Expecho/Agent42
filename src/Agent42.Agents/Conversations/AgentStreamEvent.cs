namespace Agent42.Agents;

/// <summary>
/// The kind of event streamed back while the agent works on a turn.
/// </summary>
public enum AgentStreamEventType
{
    /// <summary>A fragment of the assistant's reply text.</summary>
    TextDelta,

    /// <summary>The model invoked a tool; input is on its way to the function.</summary>
    ToolCallStart,

    /// <summary>A tool finished and returned its result to the model.</summary>
    ToolCallEnd,

    /// <summary>
    /// The assistant produced a generated image (ADR 0020). Image generation is not token-streamed —
    /// one <see cref="Image"/> event carrying the stored attachment id + media type, then <see cref="Done"/>.
    /// The client renders it through the attachment proxy (by id), exactly like a user-uploaded image.
    /// </summary>
    Image,

    /// <summary>The turn finished; carries the aggregated token usage.</summary>
    Done,

    /// <summary>
    /// Up to 3 suggested follow-up prompts the user can click to continue the conversation. Generated
    /// best-effort by a second, non-streaming model call after the reply (interactive path only), streamed
    /// just before <see cref="Done"/>. The list rides on <see cref="AgentStreamEvent.Suggestions"/>.
    /// </summary>
    Suggestions,

    /// <summary>
    /// A fragment of the model's own reasoning ("thinking"), for a reasoning model that emits it
    /// (ADR 0071). Carried on <see cref="AgentStreamEvent.Text"/> — the same property
    /// <see cref="TextDelta"/> uses, because reasoning is only ever text and a dedicated field would
    /// buy nothing.
    ///
    /// <para><b>Live-only, like <see cref="Suggestions"/>.</b> It is streamed and then discarded: never
    /// appended to the reply, never written to the serialized session (the history provider strips it —
    /// see <c>StripReasoningFromStorage</c>), never replayed by <c>renderTurns</c> on reload. Unlike
    /// <see cref="Suggestions"/> it is NOT gated on the interactive path, because it costs no extra
    /// model call — it already rides the turn's own stream — so there is no turn-scoped flag to
    /// clear.</para>
    /// </summary>
    Reasoning,

    /// <summary>
    /// An InteractiveOAuth MCP server this conversation uses needs a human to sign in again (ADR 0081): its grant
    /// is dead (<c>invalid_grant</c>), or its tool call was refused with 401 after the bearer's own renew-and-retry.
    /// <see cref="AgentStreamEvent.McpServerId"/> names the server and <see cref="AgentStreamEvent.Text"/> carries
    /// its display name; the client renders a Reconnect button that opens the OAuth flow in a new tab.
    ///
    /// <para><b>Live-only, like <see cref="Suggestions"/> and <see cref="Reasoning"/>.</b> Never appended to the
    /// reply, never persisted, never replayed from history (ADR 0080 replays tool calls only). Emitted on the
    /// interactive path only, at most once per server per activation.</para>
    /// </summary>
    McpReauthRequired,

    /// <summary>
    /// The research agent's live todo checklist changed (ADR 0082): the whole list rides on
    /// <see cref="AgentStreamEvent.Todos"/>, so the client replaces rather than patches. Emitted after a tool
    /// result whenever the list differs from the last one sent.
    ///
    /// <para><b>Live-only, like <see cref="Suggestions"/>.</b> Never persisted and never replayed on reload:
    /// the todo state lives in the session only for the turn that uses it, and is reset when the next turn
    /// starts.</para>
    /// </summary>
    Todos,

    /// <summary>
    /// A research loop's later iteration started speaking, so the text streamed so far is superseded
    /// (ADR 0082): the client clears the current bubble's text and keeps its tool calls. The answer of a
    /// research turn is the last iteration that produced text, and the stored session is folded to match,
    /// so a reload shows what the live bubble ended with. Carries no payload.
    /// </summary>
    TextReset,

    /// <summary>
    /// The conversation's shared state changed (ADR 0089): the WHOLE state rides on
    /// <see cref="AgentStreamEvent.State"/> (a JSON object, as text) with its version on
    /// <see cref="AgentStreamEvent.StateVersion"/>, so the client replaces rather than patches. Emitted after a tool
    /// result whenever the version differs from the last one this run sent.
    ///
    /// <para><b>Live-only on the wire</b>, like <see cref="Todos"/>, but the state itself is persisted in
    /// <see cref="AgentGrainState.SharedStateJson"/> and the client loads it with a GET when a conversation opens.
    /// A snapshot rather than a delta, deliberately: it is idempotent under recovery and costs nothing at the 16 KB
    /// cap.</para>
    /// </summary>
    StateSnapshot,

    /// <summary>
    /// An MCP server asks the user for input during a tool call (ADR 0095). <see cref="AgentStreamEvent.Elicitation"/>
    /// carries the request as JSON (<c>{id, mode, server, message, schema?, url?, host?}</c>),
    /// <see cref="AgentStreamEvent.ToolCallId"/> the waiting MCP tool's call id and <see cref="AgentStreamEvent.Text"/>
    /// the server's display name. The browser answers through the client-tool result endpoint with the request's
    /// <c>id</c>.
    ///
    /// <para><b>Live-only</b>, and not a <see cref="ToolCallStart"/>: never a tool chip, never folded (ADR 0072),
    /// never persisted. A browser whose stream died finds it through the pending-tool-calls GET.</para>
    /// </summary>
    McpElicitation,
}

/// <summary>
/// A single event in the agent's streamed response: reply text, tool-call
/// progress, or the closing token-usage summary. Only the fields relevant to
/// the <see cref="Type"/> are set; the rest stay null.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentStreamEvent")]
public sealed record AgentStreamEvent(
    [property: Id(0)] AgentStreamEventType Type,
    [property: Id(1)] string? Text = null,
    [property: Id(2)] string? ToolName = null,
    [property: Id(3)] string? ToolCallId = null,
    [property: Id(4)] string? ToolInput = null,
    [property: Id(5)] string? ToolResult = null,
    [property: Id(6)] long? InputTokens = null,
    [property: Id(7)] long? OutputTokens = null,
    [property: Id(8)] long? CachedTokens = null,
    // Set on an Image event (ADR 0020): the stored attachment id the browser fetches via the proxy, plus
    // its media type. Trailing defaults so every existing positional construction keeps compiling.
    [property: Id(9)] string? AttachmentId = null,
    [property: Id(10)] string? MediaType = null,
    // Set on a Suggestions event: up to 3 short follow-up prompts. Trailing default so every existing
    // positional construction keeps compiling.
    [property: Id(11)] IReadOnlyList<string>? Suggestions = null,
    // Set on the Done event of a turn the user STOPPED (ADR 0078), so the client can mark that bubble as
    // interrupted rather than finished. Nullable, and null rather than false when the turn ran to
    // completion — not a style choice: the NDJSON writer ignores nulls (AgentApi.Conversations.cs), so a
    // plain bool would add "stopped":false to every per-token delta of every turn. Trailing default so
    // every existing positional construction keeps compiling.
    [property: Id(12)] bool? Stopped = null,
    // Set on a McpReauthRequired event (ADR 0081): the server the Reconnect button opens the OAuth flow for.
    // Trailing default so every existing positional construction keeps compiling.
    [property: Id(13)] string? McpServerId = null,
    // Set on a Todos event (ADR 0082): the research agent's whole checklist, in the order the model added it.
    // Trailing default so every existing positional construction keeps compiling.
    [property: Id(14)] IReadOnlyList<ResearchTodo>? Todos = null,
    // Set on a ToolCallStart whose tool the BROWSER runs (ADR 0089): true only for a client tool on a run that has
    // a browser to answer it. Null rather than false for the same NDJSON reason as Stopped. Trailing default so
    // every existing positional construction keeps compiling.
    [property: Id(15)] bool? ClientTool = null,
    // Set on a StateSnapshot event (ADR 0089): the whole shared state as JSON text, and its version, which the
    // client sends back as the base of an edit. Trailing defaults so every existing construction keeps compiling.
    [property: Id(16)] string? State = null,
    [property: Id(17)] long? StateVersion = null,
    // Set on a McpElicitation event (ADR 0095): the request as JSON text. Trailing default so every existing
    // construction keeps compiling, and null elsewhere so the NDJSON writer omits it.
    [property: Id(18)] string? Elicitation = null);
