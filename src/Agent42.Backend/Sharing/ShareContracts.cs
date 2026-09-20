using Agent42.Agents;



namespace Agent42.Backend;

// What GET/POST /api/conversations/{id}/share return (ADR 0035): the public read-only URL, or null when the
// conversation is not shared. The raw token rides the URL fragment; only the owner (behind Easy Auth) ever
// sees this.
public record ShareLinkResponse(string? Url);

// The public GET /api/share/{token} payload (ADR 0035): the conversation title, the agent name when it is an
// agent chat (else null), and the full current read-only history. Reuses the ChatTurn DTO the authenticated
// /messages route returns, but NOT with the same contents: the endpoint strips ToolCalls before responding
// (ADR 0080 — tool arguments and results are withheld from a public link). Anything added to ChatTurn from
// now on has to say which side of that line it falls on.
public record SharedConversationResponse(string Title, string? AgentName, IReadOnlyList<ChatTurn> Turns);
