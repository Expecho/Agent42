using Orleans.Runtime;

namespace Agent42.Agents.Sharing;

[GenerateSerializer]
[Alias("Agent42.Agents.ShareLinkDirectoryState")]
public sealed class ShareLinkDirectoryState
{
    // Null until a hash is registered; ResolveAsync keys "registered" off UserId being present.
    [Id(0)]
    public string? UserId { get; set; }

    [Id(1)]
    public string? ConversationId { get; set; }
}

/// <summary>
/// Global share-token-hash → conversation directory (ADR 0035). Grain key is the token hash; state is the
/// conversation it points at. Rides the shared "agent-state" storage provider (Orleans partitions by grain
/// type, so no dedicated Cosmos container is needed — see <see cref="AgentKeyDirectoryGrain"/>, the ADR 0022
/// exemplar this mirrors, and the pre-declared-containers gotcha).
/// </summary>
public sealed class ShareLinkDirectoryGrain : Grain, IShareLinkDirectoryGrain
{
    private readonly IPersistentState<ShareLinkDirectoryState> _state;

    public ShareLinkDirectoryGrain(
        [PersistentState("share-link-directory", AgentGrain.StorageProviderName)] IPersistentState<ShareLinkDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string conversationId)
    {
        _state.State.UserId = userId;
        _state.State.ConversationId = conversationId;
        await _state.WriteStateAsync();
    }

    public Task<ShareLinkTarget?> ResolveAsync()
        => Task.FromResult(
            _state.State is { UserId: { Length: > 0 } userId, ConversationId: { Length: > 0 } conversationId }
                ? new ShareLinkTarget(userId, conversationId)
                : null);

    public async Task DeregisterAsync()
    {
        // Clear the persisted item entirely so a revoked hash leaves no residue; ResolveAsync then reads a
        // default (null) state and returns null.
        _state.State.UserId = null;
        _state.State.ConversationId = null;
        await _state.ClearStateAsync();
    }
}
