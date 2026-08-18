namespace Agent42.Agents.Telemetry;

/// <summary>
/// The single GLOBAL usage rollup grain (ADR 0031) — one well-known key
/// (<see cref="UsageRollupGrain.GlobalKey"/>), cross-user by design so any signed-in user's
/// dashboard shows aggregate spend. Rides the shared "agent-state" storage provider on a distinct
/// "usage-rollups" state name — the <see cref="IUserPushGrain"/> pattern, so no new Cosmos container.
///
/// <para>It is fed purely from OpenTelemetry gen_ai spans (no manual counter in <c>AgentGrain</c>):
/// a Backend span processor projects finished spans to <see cref="UsageEvent"/>s and a background
/// service batches them into <see cref="ApplyAsync"/>. Reads are served to <c>GET /api/usage</c>.</para>
/// </summary>
[Alias("Agent42.Agents.IUsageRollupGrain")]
public interface IUsageRollupGrain : IGrainWithStringKey
{
    /// <summary>Folds a batch of projected events into the hourly+daily rollups, prunes aged buckets, persists.</summary>
    [Alias("ApplyAsync")]
    Task ApplyAsync(IReadOnlyList<UsageEvent> events);

    /// <summary>
    /// Aggregates the rollups for a range (<c>24h</c>/<c>7d</c>/<c>30d</c>/<c>all</c>) with optional
    /// application/user filters (null = no filter). The caller validates the range; an unrecognized
    /// range is treated as "all".
    /// </summary>
    [Alias("QueryAsync")]
    Task<UsageReport> QueryAsync(string range, string? application, string? user);

    /// <summary>Distinct application names seen (for the filter dropdown).</summary>
    [Alias("ListApplicationsAsync")]
    Task<IReadOnlyList<string>> ListApplicationsAsync();

    /// <summary>Distinct user keys seen (for the filter dropdown).</summary>
    [Alias("ListUsersAsync")]
    Task<IReadOnlyList<string>> ListUsersAsync();
}
