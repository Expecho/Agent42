using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.FleetShareLinkDirectoryState")]
public sealed class FleetShareLinkDirectoryState
{
    // Null until a hash is registered; ResolveAsync keys "registered" off UserId being present.
    [Id(0)]
    public string? UserId { get; set; }

    [Id(1)]
    public string? TaskId { get; set; }
}

/// <summary>
/// Global fleet share-token-hash → task directory (ADR 0120), the twin of <see cref="ShareLinkDirectoryGrain"/> with a
/// state name of its own. Rides the shared "agent-state" provider, so no dedicated Cosmos container is needed.
/// </summary>
public sealed class FleetShareLinkDirectoryGrain : Grain, IFleetShareLinkDirectoryGrain
{
    private readonly IPersistentState<FleetShareLinkDirectoryState> _state;

    public FleetShareLinkDirectoryGrain(
        [PersistentState("fleet-share-link-directory", AgentGrain.StorageProviderName)] IPersistentState<FleetShareLinkDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string taskId)
    {
        _state.State.UserId = userId;
        _state.State.TaskId = taskId;
        await _state.WriteStateAsync();
    }

    public Task<FleetShareLinkTarget?> ResolveAsync()
        => Task.FromResult(
            _state.State is { UserId: { Length: > 0 } userId, TaskId: { Length: > 0 } taskId }
                ? new FleetShareLinkTarget(userId, taskId)
                : null);

    public async Task DeregisterAsync()
    {
        _state.State.UserId = null;
        _state.State.TaskId = null;
        await _state.ClearStateAsync();
    }
}
