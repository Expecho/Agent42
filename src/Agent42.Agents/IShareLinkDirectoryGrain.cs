namespace Agent42.Agents;

/// <summary>
/// The conversation a share token resolves to: which user owns it and which conversation to render
/// (ADR 0035). Crosses the grain boundary (returned from <see cref="IShareLinkDirectoryGrain.ResolveAsync"/>),
/// so it is Orleans-serializable with explicit member ids.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ShareLinkTarget")]
public sealed record ShareLinkTarget(
    [property: Id(0)] string UserId,
    [property: Id(1)] string ConversationId);

/// <summary>
/// A global, cross-user directory mapping a share-token hash to the conversation it grants read-only
/// access to (ADR 0035). The grain key <b>is</b> the token hash, so resolving an incoming
/// <c>/api/share/{token}</c> is an O(1) grain get — no enumeration over users. This is the read-only
/// analogue of <see cref="IAgentKeyDirectoryGrain"/> (ADR 0022): the public share endpoint has no
/// signed-in user and no conversation id in the request, so it must resolve a bare token to its owner.
///
/// <para><see cref="UserShareLinksGrain"/> owns the token lifecycle and keeps this directory in sync on
/// every change (create/revoke). A hash with no registered entry resolves to <c>null</c> → the endpoint
/// fails closed (404 — a browser-visitable link, so "not found or revoked" is the right shape). Rides the
/// shared <c>agent-state</c> provider (Orleans partitions by grain type, so no dedicated Cosmos container
/// is needed — same reasoning as <see cref="IAgentKeyDirectoryGrain"/>).</para>
/// </summary>
[Alias("Agent42.Agents.IShareLinkDirectoryGrain")]
public interface IShareLinkDirectoryGrain : IGrainWithStringKey
{
    /// <summary>
    /// Points this token hash at <paramref name="userId"/>/<paramref name="conversationId"/>. Overwrites
    /// any prior registration (a hash collision is cryptographically impossible for 256-bit tokens, so
    /// this only ever re-registers the same target).
    /// </summary>
    [Alias("RegisterAsync")]
    Task RegisterAsync(string userId, string conversationId);

    /// <summary>Returns the conversation this hash points at, or <c>null</c> when it is unregistered.</summary>
    [Alias("ResolveAsync")]
    Task<ShareLinkTarget?> ResolveAsync();

    /// <summary>
    /// Removes this hash's registration, so the link stops resolving. Called when a link is revoked. Unlike
    /// an agent-key delete (where the agent-existence re-check backstops a missed deregister), unsharing does
    /// NOT delete the conversation, so this IS the revocation — <see cref="UserShareLinksGrain"/> calls it
    /// FIRST (before dropping the list entry) so a mid-failure leaves the link DEAD, never live.
    /// </summary>
    [Alias("DeregisterAsync")]
    Task DeregisterAsync();
}
