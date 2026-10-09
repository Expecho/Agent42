namespace Agent42.Backend;

// AgentId is the stored agent to bind on the first turn of an agent chat; AttachmentIds are
// the ids returned by the upload endpoint for images to send with this turn. There is no Location:
// the browser answers the get_current_location client tool instead (ADR 0090), and an old cached
// client that still posts one has it ignored as an unknown member. Ephemeral
// is the incognito flag (ADR 0034): when set, the turn is neither persisted into the session
// item nor listed in the sidebar. All are trailing defaults so existing positional callers
// (new ChatRequest("hi", null)) keep compiling.
public record ChatRequest(
    string Message, string? Model, string? AgentId = null, IReadOnlyList<string>? AttachmentIds = null,
    bool Ephemeral = false, IReadOnlyList<string>? ClientTools = null);

// The pinned flag to set on a conversation (ADR 0045). Pinned conversations lead the
// sidebar listing regardless of recency.
public record PinConversationRequest(bool Pinned);
