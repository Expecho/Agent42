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
    /// <see cref="StoredMcpServer.KnownToolNames"/> keeps the stored names. Returns the stored server (with
    /// any retained ciphertext and names).
    /// </summary>
    [Alias("SaveAsync")]
    Task<StoredMcpServer> SaveAsync(StoredMcpServer server);

    /// <summary>Removes the server with <paramref name="id"/>, bumping the version only if one was removed.</summary>
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
}
