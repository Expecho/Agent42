using Orleans.Runtime;

namespace Agent42.Agents.Telemetry;

/// <summary>
/// The single global usage rollup grain (ADR 0031). Thin persistence wrapper over the pure
/// <see cref="UsageRollupState"/>; rides the shared "agent-state" storage provider on a distinct
/// "usage-rollups" state name (the <see cref="UserPushGrain"/> pattern — no new Cosmos container).
///
/// <para>The grain is non-reentrant, which is correct at this app's scale: ingestion writes come from
/// one background service on a ≥30 s cadence and reads from ≤2 users' auto-refresh — no meaningful
/// contention. If it ever grows, mark <see cref="QueryAsync"/> reentrant rather than shrinking the flush.</para>
/// </summary>
public sealed class UsageRollupGrain : Grain, IUsageRollupGrain
{
    /// <summary>The one well-known key every caller uses — this grain is a process-wide singleton.</summary>
    public const string GlobalKey = "global";

    private readonly IPersistentState<UsageRollupState> _state;

    public UsageRollupGrain(
        [PersistentState("usage-rollups", AgentGrain.StorageProviderName)] IPersistentState<UsageRollupState> state)
    {
        _state = state;
    }

    public async Task ApplyAsync(IReadOnlyList<UsageEvent> events)
    {
        if (events.Count == 0)
        {
            return;
        }

        foreach (var e in events)
        {
            _state.State.Apply(e);
        }

        _state.State.Prune(DateTimeOffset.UtcNow);
        await _state.WriteStateAsync();
    }

    public Task<UsageReport> QueryAsync(string range, string? application, string? user)
        => Task.FromResult(_state.State.Query(range, application, user, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<string>> ListApplicationsAsync()
        => Task.FromResult(_state.State.Applications());

    public Task<IReadOnlyList<string>> ListUsersAsync()
        => Task.FromResult(_state.State.Users());
}
