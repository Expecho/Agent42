namespace Agent42.Agents;

/// <summary>
/// Stores a signed-in user's remote-MCP-server overlay (config-driven remote MCP servers — the MCP
/// analogue of ADR 0018's provider overlay). The grain key is the user id, like
/// <see cref="IUserProviderConfigGrain"/>. A singleton per user — one overlay, not a per-conversation
/// thing — so it exposes upsert/delete-by-id plus a version read the per-user tool-provider layer polls
/// to invalidate its cache.
///
/// <para>The grain stores every secret as <b>ciphertext</b> only: the API layer encrypts a secret (via
/// <c>IMcpSecretProtector</c>) before calling <see cref="SaveAsync"/>, and the grain never sees or
/// returns plaintext. On update, a blank ciphertext on a secret field means "keep the stored secret", so
/// the user need not re-enter a secret to edit other fields.</para>
/// </summary>
[Alias("Agent42.Agents.IUserMcpConfigGrain")]
public interface IUserMcpConfigGrain : IGrainWithStringKey
{
    /// <summary>Returns the user's overlay, or <see cref="UserMcpConfig.Empty"/> when never saved.</summary>
    [Alias("GetAsync")]
    Task<UserMcpConfig> GetAsync();

    /// <summary>
    /// The overlay's current version — bumped on every <b>configuration</b> write (<see cref="SaveAsync"/>,
    /// <see cref="DeleteAsync"/>), but deliberately NOT by <see cref="RecordToolNamesAsync"/>. Cheap poll for
    /// cache invalidation.
    /// </summary>
    [Alias("GetVersionAsync")]
    Task<int> GetVersionAsync();

    /// <summary>
    /// Upserts one server by <see cref="StoredMcpServer.Id"/> and bumps the version. A blank ciphertext
    /// on any secret field of an existing server keeps that server's stored ciphertext, and a null
    /// <see cref="StoredMcpServer.KnownToolNames"/> keeps the stored names. The incoming
    /// <see cref="StoredMcpServer.BridgeKeyHash"/> is always ignored: an update carries the stored one
    /// forward and an insert stores none (ADR 0076 — see <see cref="MintBridgeKeyAsync"/>). Returns the
    /// stored server (with any retained ciphertext, names and hash).
    /// </summary>
    [Alias("SaveAsync")]
    Task<StoredMcpServer> SaveAsync(StoredMcpServer server);

    /// <summary>
    /// Removes the server with <paramref name="id"/>, bumping the version only if one was removed, and
    /// deregisters its bridge-key hash if it had one (ADR 0076).
    /// </summary>
    [Alias("DeleteAsync")]
    Task DeleteAsync(string id);

    /// <summary>
    /// Records the tool names discovered from a server on a successful connect, so the picker can offer a
    /// per-tool checkbox (<c>mcp:{id}:{toolName}</c>) without any endpoint having to connect. A no-op when the
    /// server is unknown or the names are unchanged.
    ///
    /// <para><b>Does NOT bump the version, and that is the point — not an oversight.</b> The version is the
    /// key the per-user tool provider caches <b>live MCP sockets</b> under. Names are picker metadata, not
    /// connection config, so re-discovering the same server on every activation must not invalidate that cache
    /// for every warm grain of the user. (Note the no-op-when-unchanged half alone would already stop this
    /// from recurring forever — so "it would bump on every activation" is NOT the reason; the reason is that
    /// even a single genuine name change should not tear down connections.) Do not reuse
    /// <see cref="SaveAsync"/> for this.</para>
    /// </summary>
    [Alias("RecordToolNamesAsync")]
    Task RecordToolNamesAsync(string serverId, IReadOnlyList<string> toolNames);

    /// <summary>
    /// Bumps the version without changing any configuration, so the per-user tool provider re-resolves this
    /// user's servers on the next grain activation. Called from a <b>user-pressed</b> connection test.
    ///
    /// <para><b>Why this is not a contradiction of <see cref="RecordToolNamesAsync"/>.</b> That method must
    /// not bump because it runs on <i>every activation</i>, from the provider itself — bumping there would
    /// tear down every warm grain's sockets as a side effect of ordinary discovery. This one runs only when a
    /// human pressed Test: once, deliberately, against one server. The same distinction ADR 0064 used to let a
    /// user-pressed POST connect where <c>GET /api/tools</c> may not.</para>
    ///
    /// <para><b>What it fixes.</b> The provider caches a whole <c>McpToolSet</c> under the version and a hit
    /// returns <i>without connecting</i>, so two states are otherwise unrecoverable without an unrelated
    /// config write: a server that gained or dropped a tool remotely, and — sharper — a server that was
    /// unreachable when the version was first resolved, whose <b>empty</b> group stays cached after it
    /// recovers. Without this, Test reports "connected, 12 tools" while the chat path still serves the stale
    /// list; the diagnostic and the behaviour disagree and both are correct.</para>
    ///
    /// <para>The version is per <b>overlay</b>, not per server, so this re-resolves all of the user's servers.
    /// That is the same blast radius any config save already has, and the provider retains the previous
    /// version's connections, so warm grains keep working until they fall out of retention.</para>
    /// </summary>
    [Alias("InvalidateToolCacheAsync")]
    Task InvalidateToolCacheAsync();

    /// <summary>
    /// Mints a fresh local-bridge key for <paramref name="serverId"/> (ADR 0076), stores only its hash,
    /// registers it in <see cref="ILocalBridgeKeyDirectoryGrain"/>, and returns the raw key — the only
    /// time it is ever visible. Returns <c>null</c> when the server is unknown or is not
    /// <see cref="McpAuthMode.LocalBridge"/>. Over an existing hash this is a <b>regenerate</b>: the old
    /// hash is deregistered FIRST (ADR 0022's order), so a crash leaves the old key dead, never live.
    ///
    /// <para>Does not bump the version: the hash is not connection config the tool provider reads, and a
    /// bump would tear down the user's other warm MCP sockets. Revoking a <i>live</i> socket is the
    /// caller's job (the API evicts it from the registry).</para>
    /// </summary>
    [Alias("MintBridgeKeyAsync")]
    Task<string?> MintBridgeKeyAsync(string serverId);

    /// <summary>
    /// Clears the server's bridge-key hash and deregisters it (write first, then deregister — a
    /// mid-failure leaves an orphan entry the endpoint's hash re-check still rejects). A no-op when the
    /// server is unknown or has no hash.
    /// </summary>
    [Alias("ClearBridgeKeyAsync")]
    Task ClearBridgeKeyAsync(string serverId);
}
