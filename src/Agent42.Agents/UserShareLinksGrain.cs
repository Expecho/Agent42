using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.UserShareLinksState")]
public sealed class UserShareLinksState
{
    [Id(0)]
    public List<ShareLink> Links { get; set; } = [];
}

/// <summary>
/// Per-user read-only share-link store (ADR 0035). Rides the shared "agent-state" storage provider on a
/// distinct "share-links" state name — the same overlay-grain shape as <see cref="UserPushGrain"/>, so no
/// new Cosmos container. Keeps the global <see cref="IShareLinkDirectoryGrain"/> in sync on every change so
/// the public resolve path (which has no signed-in user) stays authoritative.
/// </summary>
public sealed class UserShareLinksGrain : Grain, IUserShareLinksGrain
{
    private readonly IPersistentState<UserShareLinksState> _state;

    public UserShareLinksGrain(
        [PersistentState("share-links", AgentGrain.StorageProviderName)] IPersistentState<UserShareLinksState> state)
    {
        _state = state;
    }

    public Task<ShareLink?> GetAsync(string conversationId)
        => Task.FromResult(_state.State.Links.FirstOrDefault(l => l.ConversationId == conversationId));

    public async Task<ShareLink> CreateOrGetAsync(string conversationId)
    {
        var existing = _state.State.Links.FirstOrDefault(l => l.ConversationId == conversationId);
        if (existing is not null)
        {
            return existing;
        }

        var userId = this.GetPrimaryKeyString();
        var (raw, hash) = ShareToken.Generate();
        var link = new ShareLink(conversationId, raw, DateTimeOffset.UtcNow);

        // Create order (mirrors AgentRegistryGrain): write the list (carrying the token) FIRST, THEN register
        // the directory. A crash in between leaves a listed link whose directory isn't registered — the link
        // simply 404s (dead), never a live orphan. The safe direction is fail-toward-dead.
        _state.State.Links.Add(link);
        await _state.WriteStateAsync();

        await Directory(hash).RegisterAsync(userId, conversationId);
        return link;
    }

    public async Task RemoveAsync(string conversationId)
    {
        var existing = _state.State.Links.FirstOrDefault(l => l.ConversationId == conversationId);
        if (existing is null)
        {
            return;
        }

        // Revoke order — the OPPOSITE of create, deliberately: deregister the directory FIRST (that IS the
        // revocation; unsharing does not delete the conversation, so there is no existence re-check to
        // backstop a missed deregister), THEN drop the list entry. A crash then leaves the link DEAD with a
        // stale list entry (cosmetic, self-heals on the next toggle), never a live revoked link.
        await Directory(ShareToken.Hash(existing.Token)).DeregisterAsync();

        _state.State.Links.RemoveAll(l => l.ConversationId == conversationId);
        await _state.WriteStateAsync();
    }

    // The global token-hash → conversation directory grain for a given hash (ADR 0035). Keyed by the hash, so
    // this is an O(1) grain reference — this grain keeps it in sync on every change.
    private IShareLinkDirectoryGrain Directory(string hash)
        => GrainFactory.GetGrain<IShareLinkDirectoryGrain>(hash);
}
