namespace Agent42.Agents;

/// <summary>
/// The owner an API-key hash resolves to: which user's registry holds the agent, and which agent
/// (ADR 0022). Crosses the grain boundary (returned from <see cref="IAgentKeyDirectoryGrain.ResolveAsync"/>),
/// so it is Orleans-serializable with explicit member ids.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.AgentKeyOwner")]
public sealed record AgentKeyOwner(
    [property: Id(0)] string UserId,
    [property: Id(1)] string AgentId);

/// <summary>
/// A global, cross-user directory mapping an API-key hash to the agent it authenticates (ADR 0022).
/// The grain key <b>is</b> the key-hash, so resolving an incoming <c>X-Api-Key</c> is an O(1) grain
/// get — no enumeration over users. This is the one deliberately global structure in an otherwise
/// per-user model: <see cref="IAgentRegistryGrain"/> is keyed per user and has no cross-user lookup,
/// but the run endpoint has no signed-in user and no agent id in the request, so it must resolve a
/// bare key to its owner.
///
/// <para>The registry grain owns the hash lifecycle and keeps this directory in sync on every hash
/// change (create/regenerate/delete). A hash with no registered grain resolves to <c>null</c> →
/// the endpoint fails closed (401). Rides the shared <c>agent-state</c> provider (Orleans partitions
/// by grain type, so no dedicated Cosmos container is needed — same reasoning as the registry grain).</para>
/// </summary>
[Alias("Agent42.Agents.IAgentKeyDirectoryGrain")]
public interface IAgentKeyDirectoryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Points this hash at <paramref name="userId"/>/<paramref name="agentId"/>. Overwrites any prior
    /// registration (a hash collision is cryptographically impossible for 256-bit keys, so this only
    /// ever re-registers the same owner).
    /// </summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string agentId);

    /// <summary>Returns the owner this hash points at, or <c>null</c> when the hash is unregistered.</summary>
    [Alias("ResolveAsync")]
    Task<AgentKeyOwner?> ResolveAsync();

    /// <summary>
    /// Removes this hash's registration, so it stops resolving. Called when a key is regenerated (on
    /// the OLD hash) or its agent is deleted — a missed call would leave a revoked key live.
    /// </summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync();
}
