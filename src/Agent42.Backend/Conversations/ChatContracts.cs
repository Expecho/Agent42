namespace Agent42.Backend;

// AgentId is the stored agent to bind on the first turn of an agent chat; AttachmentIds are
// the ids returned by the upload endpoint for images to send with this turn. There is no Location:
// the browser answers the get_current_location client tool instead (ADR 0093), and an old cached
// client that still posts one has it ignored as an unknown member. Ephemeral
// is the incognito flag (ADR 0034): when set, the turn is neither persisted into the session
// item nor listed in the sidebar. All are trailing defaults so existing positional callers
// (new ChatRequest("hi", null)) keep compiling.
//
// ReplaceLastTurn is edit and regenerate (ADR 0092): the last turn is removed before this one runs. It needs both
// halves of the concurrency token the client read from /messages — the user-turn count and the hex SHA-256 of the
// last user message's text — and is refused with 409 when either no longer matches.
public record ChatRequest(
    string Message, string? Model, string? AgentId = null, IReadOnlyList<string>? AttachmentIds = null,
    bool Ephemeral = false, IReadOnlyList<string>? ClientTools = null,
    bool ReplaceLastTurn = false, int? ExpectedUserTurnCount = null, string? ExpectedLastUserTextHash = null);

// Fork from here (ADR 0092): copy the conversation through the exchange that starts at projected user turn
// ThroughUserTurn (0-based) into a new conversation. Same concurrency token as ChatRequest.ReplaceLastTurn.
public record ForkRequest(int ThroughUserTurn, int ExpectedUserTurnCount, string? ExpectedLastUserTextHash);

// The new conversation a fork created, generated server-side.
public record ForkResponse(string ConversationId);

// The pinned flag to set on a conversation (ADR 0045). Pinned conversations lead the
// sidebar listing regardless of recency.
public record PinConversationRequest(bool Pinned);
