using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.AgentKeyDirectoryState")]
public sealed class AgentKeyDirectoryState
{
    // Null until a hash is registered; ResolveAsync keys "registered" off UserId being present.
    [Id(0)]
    public string? UserId { get; set; }

    [Id(1)]
    public string? AgentId { get; set; }
}

/// <summary>
/// Global key-hash → owner directory (ADR 0022). Grain key is the key-hash; state is the owner it
/// points at. Rides the shared "agent-state" storage provider (Orleans partitions by grain type, so
/// no dedicated Cosmos container is needed — see <see cref="AgentRegistryGrain"/> and the
/// pre-declared-containers gotcha).
/// </summary>
public sealed class AgentKeyDirectoryGrain : Grain, IAgentKeyDirectoryGrain
{
    private readonly IPersistentState<AgentKeyDirectoryState> _state;

    public AgentKeyDirectoryGrain(
        [PersistentState("agent-key-directory", AgentGrain.StorageProviderName)] IPersistentState<AgentKeyDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string agentId)
    {
        _state.State.UserId = userId;
        _state.State.AgentId = agentId;
        await _state.WriteStateAsync();
    }

    public Task<AgentKeyOwner?> ResolveAsync()
        => Task.FromResult(
            _state.State is { UserId: { Length: > 0 } userId, AgentId: { Length: > 0 } agentId }
                ? new AgentKeyOwner(userId, agentId)
                : null);

    public async Task DeregisterAsync()
    {
        // Clear the persisted item entirely so an unregistered hash leaves no residue; ResolveAsync
        // then reads a default (null) state and returns null.
        _state.State.UserId = null;
        _state.State.AgentId = null;
        await _state.ClearStateAsync();
    }
}
