namespace Agent42.Agents;

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
/// <param name="KnownToolNames">
/// The tool names last discovered from this server, or null when it has never connected. <b>Not a
/// setting</b> — the user cannot type it and no request carries it; the tool provider records it after a
/// SUCCESSFUL connect, which is what lets <c>GET /api/tools</c> render per-tool checkboxes
/// (<c>mcp:{id}:{toolName}</c>) without connecting to anything itself.
///
/// <para><b>Never erased.</b> A skipped or unreachable server leaves the previous names standing, and
/// <see cref="IUserMcpConfigGrain.SaveAsync"/> preserves this field across an edit exactly as it preserves
/// the secret ciphertexts — <c>ToStored</c> builds a fresh record from the request and would otherwise
/// destroy it on every rename or header rotation. Both halves are load-bearing: together they are why the
/// picker can never mint a per-tool key it has no checkbox for.</para>
///
/// <para>Trailing and optional, so an overlay persisted before it existed deserializes with no
/// migration.</para>
/// </param>
/// <param name="SeedsFetchableUrls">
/// Whether a URL appearing in one of this server's tool results may be fetched by <c>fetch_web_page</c>
/// and read by <c>read_web_document</c> in the conversation that received it (ADR 0067). Off unless the
/// user turns it on, per server.
///
/// <para><b>A SETTING, not discovered metadata — the exact opposite of <see cref="KnownToolNames"/>.</b>
/// It rides the upsert request through <c>ToStored</c> and must NEVER gain a <c>Keep</c> clause in
/// <see cref="IUserMcpConfigGrain.SaveAsync"/>: with one, the user could turn seeding on and never off
/// again.</para>
///
/// <para><b>What turning it on gives up</b> is the property ADR 0011's allowlist rests on — that the
/// model may only fetch a string a search engine or the user produced <i>verbatim</i>. A seeded URL was
/// authored on whatever page the server scraped. It is off by default and per server for that reason;
/// see ADR 0067 for the full cost, including that our own container then dials such URLs (ADR 0066).</para>
///
/// <para>Trailing and optional, so an overlay persisted before it existed deserializes with seeding
/// disabled — no migration, and every existing server ships unchanged.</para>
/// </param>
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
    [property: Id(11)] string? TokenCiphertext = null,
    // ── Discovered metadata, never a user selection — see the param doc above ──
    [property: Id(12)] IReadOnlyList<string>? KnownToolNames = null,
    // ── A user SETTING (ADR 0067), not metadata — must not gain a Keep clause ──
    [property: Id(13)] bool SeedsFetchableUrls = false);

/// <summary>
/// A user's remote-MCP-server overlay: the servers they added and a monotonically-increasing
/// <see cref="Version"/> the grain bumps on every <b>configuration</b> write. The version is the cache key
/// the per-user tool provider stamps on (like <c>UserChatClientFactoryProvider</c>), so a save is reflected
/// on the next grain activation without an app restart. An empty overlay (the default) means "no MCP
/// servers" — the egress surface only opens once the user deliberately adds one.
///
/// <para><b>Not every write bumps, and that exception is deliberate.</b>
/// <see cref="IUserMcpConfigGrain.RecordToolNamesAsync"/> persists discovered
/// <see cref="StoredMcpServer.KnownToolNames"/> <i>without</i> bumping: names are picker metadata, not
/// connection config, and bumping would invalidate the live-socket tool cache for every warm grain of that
/// user on what is merely a re-discovery of the same tools.</para>
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
