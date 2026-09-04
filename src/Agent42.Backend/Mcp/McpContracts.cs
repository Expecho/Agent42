

namespace Agent42.Backend;

// Client payload for creating (POST) or updating (PUT) a remote MCP server. HeaderValue and ClientSecret are
// WRITE-ONLY — encrypted server-side and never echoed back; blank on an update means "keep the stored
// secret". Auth is "None"/"Header" (Phase 1) or "ClientCredentials" (Phase 2, MSAL: Authority/ClientId/
// ClientSecret/Scopes). Enabled toggles the server without deleting it. Phase 3 adds the OAuth fields.
public record McpServerUpsertRequest(
    string Name, string Endpoint, string Auth, bool Enabled,
    string? HeaderName = null, string? HeaderValue = null,
    string? Authority = null, string? ClientId = null, string? ClientSecret = null,
    IReadOnlyList<string>? Scopes = null);

// What GET/POST/PUT return for an MCP server: everything EXCEPT the secrets, which are masked to
// HasHeaderValue/HasClientSecret so no ciphertext (or plaintext) reaches the client. Authority/ClientId/
// Scopes are non-secret and returned so the edit form can prefill them.
public record McpServerResponse(
    string Id, string Name, string Endpoint, string Auth, bool Enabled, string? HeaderName, bool HasHeaderValue,
    string? Authority = null, string? ClientId = null, IReadOnlyList<string>? Scopes = null, bool HasClientSecret = false,
    bool HasToken = false);

// The GET /api/mcp-servers payload: the user's overlay servers (masked).
public record McpServersResponse(IReadOnlyList<McpServerResponse> Servers);

// The POST /api/mcp-servers/{id}/test payload. A reachable-but-failing test is a 200 with Ok:false — the
// request succeeded and the diagnosis IS the payload (the config-health convention); only an unknown id
// (404) or an unconfigured host (503) is an HTTP error. Reason is a CURATED string, never an exception
// message and never the MCP server's own response body.
public record McpServerTestResponse(bool Ok, int ToolCount, IReadOnlyList<string> ToolNames, string Reason);
