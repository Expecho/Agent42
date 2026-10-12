namespace Agent42.Agents;

/// <summary>
/// A global, cross-user directory mapping an inbound email token's hash to the agent it addresses (ADR 0095). The grain
/// key <b>is</b> the hash, so resolving an arriving address is an O(1) grain get, exactly like
/// <see cref="IAgentKeyDirectoryGrain"/>.
///
/// <para><b>This is deliberately a separate grain type, never a second use of <see cref="IAgentKeyDirectoryGrain"/>.</b>
/// Both directories are consulted by hash alone, so an email token registered in the key directory would authenticate as
/// an <c>X-Api-Key</c> on <c>/api/agent-run</c> and <c>/api/agent-mcp</c>. An address is far more exposed than a key — it
/// sits in mail headers, forwarding rules, provider logs and autocomplete — and one leaked address would become a live
/// run key. Separate grain types keep the two credential namespaces disjoint.</para>
///
/// <para>The registry grain owns the token lifecycle and keeps this directory in sync on every change
/// (create/regenerate/delete). An unregistered hash resolves to <c>null</c> and the endpoint fails closed.</para>
/// </summary>
[Alias("Agent42.Agents.IEmailAddressDirectoryGrain")]
public interface IEmailAddressDirectoryGrain : IGrainWithStringKey
{
    /// <summary>Points this hash at <paramref name="userId"/>/<paramref name="agentId"/>, overwriting any prior entry.</summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string agentId);

    /// <summary>Returns the owner this hash points at, or <c>null</c> when the hash is unregistered.</summary>
    [Alias("ResolveAsync")]
    Task<AgentKeyOwner?> ResolveAsync();

    /// <summary>
    /// Removes this hash's registration. Called on the OLD hash when an address is regenerated, and when its agent is
    /// deleted — a missed call leaves a revoked address live for anyone who ever saw it.
    /// </summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync();
}
