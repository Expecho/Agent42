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

    public async Task<AgentSaveResult> SaveAsync(AgentDefinition agent, bool regenerateKey = false)
    {
        var agents = _state.State.Agents;
        var now = DateTimeOffset.UtcNow;
        var userId = this.GetPrimaryKeyString();

        var existing = string.IsNullOrEmpty(agent.Id)
            ? -1
            : agents.FindIndex(a => a.Id == agent.Id);

        if (existing < 0)
        {
            // ── Insert ── mint a key. Create order (constraint 2): write registry state (carrying the
            // new hash) FIRST, THEN register the hash. A crash in between leaves the agent with a hash
            // but no directory entry — the key is simply unusable (user regenerates), never a live
            // orphan pointing at a stranger's agent.
            var (raw, hash) = ApiKey.Generate();
            var inserted = agent with
            {
                Id = Guid.NewGuid().ToString(),
                CreatedAt = now,
                UpdatedAt = now,
                ApiKeyHash = hash,
            };
            agents.Add(inserted);
            await _state.WriteStateAsync();

            await Directory(hash).RegisterAsync(userId, inserted.Id);
            return new AgentSaveResult(inserted, raw);
        }

        // ── Update ── keep the existing hash by default; the request never carries it (the grain is the
        // authority). Only an explicit regenerate mints a new key.
        var previous = agents[existing];
        var storedHash = previous.ApiKeyHash;
        string? plaintext = null;

        if (regenerateKey)
        {
            // Regenerate order (constraint 2) — the OPPOSITE of create/delete, deliberately: deregister
            // the OLD hash FIRST, then write the new hash, then register it. Any crash then leaves the
            // old key DEAD (worst case a brief window where neither resolves), never a permanently-live
            // revoked key. Writing the registry first would strand a live old-hash entry the stored new
            // hash could never clear.
            if (!string.IsNullOrEmpty(previous.ApiKeyHash))
            {
                await Directory(previous.ApiKeyHash).DeregisterAsync();
            }

            var (raw, newHash) = ApiKey.Generate();
            plaintext = raw;
            storedHash = newHash;
        }

        var updated = agent with
        {
            CreatedAt = previous.CreatedAt,
            UpdatedAt = now,
            ApiKeyHash = storedHash,
        };
        agents[existing] = updated;
        await _state.WriteStateAsync();

        if (regenerateKey)
        {
            await Directory(storedHash).RegisterAsync(userId, updated.Id);
        }

        return new AgentSaveResult(updated, plaintext);
    }

    public async Task DeleteAsync(string id)
    {
        var agent = _state.State.Agents.FirstOrDefault(a => a.Id == id);
        if (agent is null)
        {
            return;
        }

        // Delete order (constraint 2): remove from the registry (write) FIRST, THEN deregister the hash.
        // A mid-failure leaves at worst an orphan directory entry, and the run endpoint re-checks the
        // agent exists after resolving — so a resolved-but-deleted agent still fails closed (401).
        _state.State.Agents.RemoveAll(a => a.Id == id);
        await _state.WriteStateAsync();

        if (!string.IsNullOrEmpty(agent.ApiKeyHash))
        {
            await Directory(agent.ApiKeyHash).DeregisterAsync();
        }
    }

    // The global key-hash → owner directory grain for a given hash (ADR 0022). Keyed by the hash, so
    // this is an O(1) grain reference — the registry keeps it in sync on every hash change.
    private IAgentKeyDirectoryGrain Directory(string hash)
        => GrainFactory.GetGrain<IAgentKeyDirectoryGrain>(hash);
}
