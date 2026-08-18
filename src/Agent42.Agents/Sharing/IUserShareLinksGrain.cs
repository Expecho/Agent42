namespace Agent42.Agents.Sharing;

/// <summary>
/// A read-only share link the owner minted for one of their conversations (ADR 0035). Unlike an agent API
/// key (only the hash is stored), the RAW <paramref name="Token"/> is persisted here: the owner re-displays
/// and copies the link repeatedly, and it is a low-sensitivity, single-conversation, read-only capability
/// the owner already fully controls.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ShareLink")]
public sealed record ShareLink(
    [property: Id(0)] string ConversationId,
    [property: Id(1)] string Token,
    [property: Id(2)] DateTimeOffset CreatedAt);

/// <summary>
/// Per-user store of the read-only share links the user has minted (ADR 0035), keyed by the user id. Rides
/// the shared "agent-state" storage provider on a distinct "share-links" state name — the same per-user
/// overlay-grain shape as <see cref="UserPushGrain"/> ("push-subs") / <see cref="AgentRegistryGrain"/>
/// ("agents"), so no new Cosmos container. This is the OWNER-facing half (list/create/revoke + display); the
/// public resolve path goes through the global <see cref="IShareLinkDirectoryGrain"/>, which this grain keeps
/// in sync per-operation.
/// </summary>
[Alias("Agent42.Agents.IUserShareLinksGrain")]
public interface IUserShareLinksGrain : IGrainWithStringKey
{
    /// <summary>The link for <paramref name="conversationId"/>, or <c>null</c> when it is not shared.</summary>
    [Alias("GetAsync")]
    Task<ShareLink?> GetAsync(string conversationId);

    /// <summary>
    /// Returns the existing link for <paramref name="conversationId"/>, or mints one (registering the global
    /// directory) when there is none. Idempotent: one link per conversation, so a repeat call returns the
    /// same token — the URL is stable and re-copyable.
    /// </summary>
    [Alias("CreateOrGetAsync")]
    Task<ShareLink> CreateOrGetAsync(string conversationId);

    /// <summary>
    /// Revokes the link for <paramref name="conversationId"/> (no-op if not shared). Deregisters the global
    /// directory FIRST, then drops the list entry, so a mid-failure leaves the link DEAD, never a live
    /// revoked link (the "revoked key stays live" gotcha — unsharing has no conversation-deletion backstop).
    /// </summary>
    [Alias("RemoveAsync")]
    Task RemoveAsync(string conversationId);
}
