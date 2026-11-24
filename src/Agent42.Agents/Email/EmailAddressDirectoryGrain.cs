using Orleans.Runtime;

namespace Agent42.Agents;

[GenerateSerializer]
[Alias("Agent42.Agents.EmailAddressDirectoryState")]
public sealed class EmailAddressDirectoryState
{
    // Null until a hash is registered; ResolveAsync keys "registered" off UserId being present.
    [Id(0)]
    public string? UserId { get; set; }

    [Id(1)]
    public string? AgentId { get; set; }
}

/// <summary>
/// Global email-token-hash → owner directory (ADR 0096). A near-copy of <see cref="AgentKeyDirectoryGrain"/>'s shape
/// but its own grain type and state name, so an email token can never resolve as an API key (see
/// <see cref="IEmailAddressDirectoryGrain"/>). Rides the shared "agent-state" storage provider.
/// </summary>
public sealed class EmailAddressDirectoryGrain : Grain, IEmailAddressDirectoryGrain
{
    private readonly IPersistentState<EmailAddressDirectoryState> _state;

    public EmailAddressDirectoryGrain(
        [PersistentState("email-address-directory", AgentGrain.StorageProviderName)] IPersistentState<EmailAddressDirectoryState> state)
    {
        _state = state;
    }

    public async Task RegisterAsync(string userId, string agentId)
    {
        _state.State.UserId = userId;
        _state.State.AgentId = agentId;
        await _state.WriteStateAsync();
    }

    public async Task<bool> TryClaimAsync(string userId, string agentId)
    {
        if (_state.State is { UserId: { Length: > 0 }, AgentId: { Length: > 0 } })
        {
            return _state.State.UserId == userId && _state.State.AgentId == agentId;
        }

        _state.State.UserId = userId;
        _state.State.AgentId = agentId;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task<bool> DeregisterIfHeldByAsync(string userId, string agentId)
    {
        if (_state.State.UserId != userId || _state.State.AgentId != agentId)
        {
            return false;
        }

        await DeregisterAsync();
        return true;
    }

    public Task<AgentKeyOwner?> ResolveAsync()
        => Task.FromResult(
            _state.State is { UserId: { Length: > 0 } userId, AgentId: { Length: > 0 } agentId }
                ? new AgentKeyOwner(userId, agentId)
                : null);

    public async Task DeregisterAsync()
    {
        _state.State.UserId = null;
        _state.State.AgentId = null;
        await _state.ClearStateAsync();
    }
}
