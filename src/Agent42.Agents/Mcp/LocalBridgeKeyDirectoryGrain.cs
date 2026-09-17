using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.LocalBridgeKeyDirectoryState")]
public sealed class LocalBridgeKeyDirectoryState
{
    // Null until a hash is registered; ResolveAsync keys "registered" off both being present.
    [Id(0)]
    public string? UserId { get; set; }

    [Id(1)]
    public string? ServerId { get; set; }
}

/// <summary>
/// Global bridge-key-hash → owner directory (ADR 0076). Grain key is the hash. Rides the shared
/// "agent-state" provider on its own state name, so no Cosmos container is added — the
/// <see cref="AgentKeyDirectoryGrain"/> pattern.
/// </summary>
public sealed class LocalBridgeKeyDirectoryGrain : Grain, ILocalBridgeKeyDirectoryGrain
{
    private readonly IPersistentState<LocalBridgeKeyDirectoryState> _state;

    public LocalBridgeKeyDirectoryGrain(
        [PersistentState("local-bridge-key-directory", AgentGrain.StorageProviderName)] IPersistentState<LocalBridgeKeyDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string serverId)
    {
        _state.State.UserId = userId;
        _state.State.ServerId = serverId;
        await _state.WriteStateAsync();
    }

    public Task<LocalBridgeKeyOwner?> ResolveAsync()
        => Task.FromResult(
            _state.State is { UserId: { Length: > 0 } userId, ServerId: { Length: > 0 } serverId }
                ? new LocalBridgeKeyOwner(userId, serverId)
                : null);

    public async Task DeregisterAsync()
    {
        _state.State.UserId = null;
        _state.State.ServerId = null;
        await _state.ClearStateAsync();
    }
}
