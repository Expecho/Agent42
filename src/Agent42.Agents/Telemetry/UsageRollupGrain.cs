using Orleans.Runtime;

namespace Agent42.Agents;

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

    public Task<UsageReport> QueryForUserAsync(string range, string? application, string userId, string? legacyUserKey)
        => Task.FromResult(_state.State.Query(range, application, userId, legacyUserKey, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<string>> ListApplicationsForUserAsync(string userId, string? legacyUserKey)
        => Task.FromResult(_state.State.Applications(userId, legacyUserKey));
}
