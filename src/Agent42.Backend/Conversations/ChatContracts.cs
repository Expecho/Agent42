using Agent42.Agents;



namespace Agent42.Backend;

// AgentId is the stored agent to bind on the first turn of an agent chat; AttachmentIds are
// the ids returned by the upload endpoint for images to send with this turn; Location is the
// browser's current position, when the user has authorized sharing it (ADR 0012) — it rides the
// chat request because a server-side tool cannot ask the browser for a fix mid-turn. Ephemeral
// is the incognito flag (ADR 0034): when set, the turn is neither persisted into the session
// item nor listed in the sidebar. All are trailing defaults so existing positional callers
// (new ChatRequest("hi", null)) keep compiling.
public record ChatRequest(
    string Message, string? Model, string? AgentId = null, IReadOnlyList<string>? AttachmentIds = null,
    UserLocation? Location = null, bool Ephemeral = false);
