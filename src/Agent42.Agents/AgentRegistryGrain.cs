using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.AgentRegistryState")]
public sealed class AgentRegistryState
{
    [Id(0)]
    public List<AgentDefinition> Agents { get; set; } = [];
}

/// <summary>
/// Per-user registry of stored agent personas. Rides the shared "agent-state" storage
/// provider (Orleans partitions by grain type, so no dedicated Cosmos container is
/// needed — see ADR 0001 and the pre-declared-containers gotcha).
/// </summary>
public sealed class AgentRegistryGrain : Grain, IAgentRegistryGrain
{
    private readonly IPersistentState<AgentRegistryState> _state;

    public AgentRegistryGrain(
        [PersistentState("agents", AgentGrain.StorageProviderName)] IPersistentState<AgentRegistryState> state)
    {
        _state = state;
    }

    public Task<IReadOnlyList<AgentDefinition>> GetAllAsync()
        => Task.FromResult<IReadOnlyList<AgentDefinition>>(
            [.. _state.State.Agents.OrderByDescending(a => a.UpdatedAt)]);

    public Task<AgentDefinition?> GetAsync(string id)
        => Task.FromResult(_state.State.Agents.FirstOrDefault(a => a.Id == id));

    public async Task<AgentDefinition> SaveAsync(AgentDefinition agent)
    {
        var agents = _state.State.Agents;
        var now = DateTimeOffset.UtcNow;

        var existing = string.IsNullOrEmpty(agent.Id)
            ? -1
            : agents.FindIndex(a => a.Id == agent.Id);

        AgentDefinition stored;
        if (existing >= 0)
        {
            // Replace in place, preserving the original creation time.
            stored = agent with { CreatedAt = agents[existing].CreatedAt, UpdatedAt = now };
            agents[existing] = stored;
        }
        else
        {
            // Insert a new persona, assigning a fresh id and timestamps.
            stored = agent with { Id = Guid.NewGuid().ToString(), CreatedAt = now, UpdatedAt = now };
            agents.Add(stored);
        }

        await _state.WriteStateAsync();
        return stored;
    }

    public async Task DeleteAsync(string id)
    {
        if (_state.State.Agents.RemoveAll(a => a.Id == id) > 0)
        {
            await _state.WriteStateAsync();
        }
    }
}
