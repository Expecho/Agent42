namespace Agent42.Agents.Mcp;

/// <summary>
/// How a remote MCP server authenticates (config-driven remote MCP servers). Stored on
/// <see cref="StoredMcpServer.Auth"/> as a <b>string</b> (parsed by the Backend connection factory),
/// so an overlay persisted before a future mode existed still deserializes — the same forward-compat
/// discipline as <c>StoredProvider.Kind</c> (ADR 0018).
/// </summary>
public enum McpAuthMode
{
    /// <summary>No auth (public server).</summary>
    None,

    /// <summary>Static header: API key or long-lived bearer (Phase 1).</summary>
    Header,

    /// <summary>OAuth2 client_credentials via MSAL (service principal, non-interactive) — e.g. Entra ID → Dataverse (Phase 2).</summary>
    ClientCredentials,

    /// <summary>OAuth2 authorization_code + PKCE, app-driven, requires a human (Phase 3).</summary>
    InteractiveOAuth
}

/// <summary>
/// One remote MCP server in a user's overlay (config-driven remote MCP servers — the MCP analogue of
/// ADR 0018's model-provider overlay). Adding a server is a config entry, not a code change: every MCP
/// server exposes the same discovery interface and its tools convert to <c>AITool</c>.
///
/// <para>Every secret field (<see cref="HeaderValueCiphertext"/>, <see cref="ClientSecretCiphertext"/>,
/// <see cref="TokenCiphertext"/>) is stored <b>already encrypted</b> — the API layer protects it via
/// <c>IMcpSecretProtector</c> before the grain ever sees it, so the grain stores ciphertext, never
/// plaintext, and it rides a distinct <c>"mcp-servers"</c> state, never the 2 MB session (ADR 0004).
/// A blank ciphertext on an update means "keep the stored secret" (see <see cref="IUserMcpConfigGrain"/>).</para>
///
/// <para>The endpoint comes from the authenticated user's own persisted config, never a model or live
/// request, so — exactly like a user provider endpoint (ADR 0018) — it carries no address guard and may
/// be <c>localhost</c> (a self-hosted MCP server is a valid target). See the egress/injection ADR.</para>
///
/// <para>Trailing <c>[Id]</c>s from <see cref="Authority"/> onward have defaults so a Phase-1 overlay
/// deserializes the Phase-2/3 fields as null with no migration.</para>
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.StoredMcpServer")]
public sealed record StoredMcpServer(
    [property: Id(0)] string Id,
    [property: Id(1)] string Name,
    [property: Id(2)] string Endpoint,
    [property: Id(3)] string Auth,
    [property: Id(4)] bool Enabled,
    // ── Header mode ─────────────────────────────────────────────────
    [property: Id(5)] string? HeaderName = null,
    [property: Id(6)] string? HeaderValueCiphertext = null,
    // ── ClientCredentials mode (MSAL) — Phase 2 ─────────────────────
    [property: Id(7)] string? Authority = null,
    [property: Id(8)] string? ClientId = null,
    [property: Id(9)] string? ClientSecretCiphertext = null,
    [property: Id(10)] IReadOnlyList<string>? Scopes = null,
    // ── InteractiveOAuth mode — Phase 3 ─────────────────────────────
    [property: Id(11)] string? TokenCiphertext = null);

/// <summary>
/// A user's remote-MCP-server overlay: the servers they added and a monotonically-increasing
/// <see cref="Version"/> the grain bumps on every write. The version is the cache key the per-user tool
/// provider stamps on (like <c>UserChatClientFactoryProvider</c>), so a save is reflected on the next
/// grain activation without an app restart. An empty overlay (the default) means "no MCP servers" — the
/// egress surface only opens once the user deliberately adds one.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UserMcpConfig")]
public sealed record UserMcpConfig(
    [property: Id(0)] IReadOnlyList<StoredMcpServer> Servers,
    [property: Id(1)] int Version)
{
    /// <summary>
    /// A factory (fresh instance per access), never a cached singleton — a shared static default is
    /// mutated by Orleans' copier and leaks across grains (the same discipline as <c>UserProviderConfig.Empty</c>).
    /// </summary>
    public static UserMcpConfig Empty => new([], 0);
}
