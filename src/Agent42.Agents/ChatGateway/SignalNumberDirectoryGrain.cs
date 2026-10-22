using Orleans.Runtime;

namespace Agent42.Agents;

/// <summary>
/// Persisted claim on one Signal number. A class with property initialisers, never a positional record (gotcha "A record
/// parameter a Cosmos document lacks binds to <c>null</c>"). <see cref="Active"/> defaults to false, the Cosmos JSON rule
/// for bools; an empty <see cref="ConnectionId"/> means unclaimed.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.SignalNumberDirectoryState")]
public sealed class SignalNumberDirectoryState
{
    [Id(0)]
    public string UserId { get; set; } = "";

    [Id(1)]
    public string ConnectionId { get; set; } = "";

    [Id(2)]
    public bool Active { get; set; }

    [Id(3)]
    public DateTimeOffset ClaimedAt { get; set; }
}

/// <summary>
/// The Signal number claim (ADR 0111, phase 3); see <see cref="ISignalNumberDirectoryGrain"/>. Rides the shared
/// "agent-state" storage provider on its own state name.
/// </summary>
public sealed class SignalNumberDirectoryGrain : Grain, ISignalNumberDirectoryGrain
{
    private readonly IPersistentState<SignalNumberDirectoryState> _state;

    public SignalNumberDirectoryGrain(
        [PersistentState("signal-number-directory", AgentGrain.StorageProviderName)] IPersistentState<SignalNumberDirectoryState> state)
    {
        _state = state;
    }

    public async Task<bool> TryClaimAsync(string userId, string connectionId, DateTimeOffset now)
    {
        var current = _state.State;
        var free = current.ConnectionId.Length == 0
            || current.ConnectionId == connectionId
            || (!current.Active && now - current.ClaimedAt >= ChatGatewayLimits.SignalPendingClaimLifetime);
        if (!free)
        {
            return false;
        }

        // A re-claim by the same connection keeps an active claim active (a re-register of a working number).
        var keepActive = current.ConnectionId == connectionId && current.Active;
        _state.State = new SignalNumberDirectoryState
        {
            UserId = userId,
            ConnectionId = connectionId,
            Active = keepActive,
            ClaimedAt = now,
        };
        await _state.WriteStateAsync();
        return true;
    }

    public async Task<bool> ActivateAsync(string connectionId)
    {
        if (_state.State.ConnectionId != connectionId || connectionId.Length == 0)
        {
            return false;
        }

        _state.State.Active = true;
        await _state.WriteStateAsync();
        return true;
    }

    public async Task ReleaseAsync(string connectionId)
    {
        if (_state.State.ConnectionId.Length == 0 || _state.State.ConnectionId != connectionId)
        {
            return;
        }

        _state.State = new SignalNumberDirectoryState();
        await _state.ClearStateAsync();
    }

    public Task<SignalNumberClaim?> GetAsync()
        => Task.FromResult(
            _state.State is { ConnectionId.Length: > 0 } s
                ? new SignalNumberClaim(s.UserId, s.ConnectionId, s.Active ? SignalNumberClaimState.Active : SignalNumberClaimState.Pending, s.ClaimedAt)
                : null);
}
