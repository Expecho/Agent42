using Agent42.Agents;



namespace Agent42.Backend;

// What GET/POST /api/conversations/{id}/share return (ADR 0035): the public read-only URL, or null when the
// conversation is not shared. The raw token rides the URL fragment; only the owner (behind Easy Auth) ever
// sees this.
public record ShareLinkResponse(string? Url);

// The public GET /api/share/{token} payload (ADR 0035): the conversation title, the agent name when it is an
// agent chat (else null), and the full current read-only history (the same ChatTurn shape the authenticated
// /messages route returns).
public record SharedConversationResponse(string Title, string? AgentName, IReadOnlyList<ChatTurn> Turns);
