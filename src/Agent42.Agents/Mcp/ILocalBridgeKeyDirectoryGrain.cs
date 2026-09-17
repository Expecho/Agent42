namespace Agent42.Agents;

/// <summary>
/// The owner a bridge-key hash resolves to: which user's overlay holds the server, and which server
/// (ADR 0076). Crosses the grain boundary, so it is Orleans-serializable with explicit member ids.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.LocalBridgeKeyOwner")]
public sealed record LocalBridgeKeyOwner(
    [property: Id(0)] string UserId,
    [property: Id(1)] string ServerId);

/// <summary>
/// A global, cross-user directory mapping a bridge-key hash to the MCP server it authenticates
/// (ADR 0076) — a copy of <see cref="IAgentKeyDirectoryGrain"/>'s shape. The grain key <b>is</b> the
/// hash, so resolving the <c>X-Api-Key</c> on a <c>/api/local-bridge</c> upgrade is an O(1) grain get;
/// that endpoint is on Easy Auth's <c>excludedPaths</c> and has no signed-in user to key on.
///
/// <para><see cref="IUserMcpConfigGrain"/> owns the hash lifecycle and keeps this in sync per
/// operation. A resolved owner is <b>not</b> sufficient on its own: the endpoint re-reads the overlay
/// and requires the stored hash to match, so a stale entry cannot authenticate.</para>
/// </summary>
[Alias("Agent42.Agents.ILocalBridgeKeyDirectoryGrain")]
public interface ILocalBridgeKeyDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Points this hash at <paramref name="userId"/>/<paramref name="serverId"/>.</summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string serverId);

    /// <summary>Returns the owner this hash points at, or <c>null</c> when unregistered.</summary>
    [Alias("ResolveAsync")]
    Task<LocalBridgeKeyOwner?> ResolveAsync();

    /// <summary>Removes this hash's registration — a missed call would leave a revoked key live.</summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync();
}
