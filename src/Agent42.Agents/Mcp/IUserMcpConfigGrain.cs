namespace Agent42.Agents.Mcp;

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

    /// <summary>The overlay's current version — bumped on every write. Cheap poll for cache invalidation.</summary>
    [Alias("GetVersionAsync")]
    Task<int> GetVersionAsync();

    /// <summary>
    /// Upserts one server by <see cref="StoredMcpServer.Id"/> and bumps the version. A blank ciphertext
    /// on any secret field of an existing server keeps that server's stored ciphertext. Returns the
    /// stored server (with any retained ciphertext).
    /// </summary>
    [Alias("SaveAsync")]
    Task<StoredMcpServer> SaveAsync(StoredMcpServer server);

    /// <summary>Removes the server with <paramref name="id"/>, bumping the version only if one was removed.</summary>
    [Alias("DeleteAsync")]
    Task DeleteAsync(string id);
}
