namespace Agent42.Agents;

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
    /// Aggregates the rollups for a range (<c>24h</c>/<c>7d</c>/<c>30d</c>/<c>all</c>) with an optional application
    /// filter, scoped to ONE user (ADR 0121): the caller's post-alias object id, plus, for an owner only, the legacy
    /// <see cref="UsageUserKey"/> that matches buckets written before <see cref="TokenBucket.UserId"/> existed.
    /// A blank <paramref name="userId"/> returns an empty report, never everyone's. The caller validates the range;
    /// an unrecognized range is treated as "all".
    /// </summary>
    [Alias("QueryForUserAsync")]
    Task<UsageReport> QueryForUserAsync(string range, string? application, string userId, string? legacyUserKey);

    /// <summary>Distinct application names in the caller's scope (for the filter dropdown), see <see cref="QueryForUserAsync"/>.</summary>
    [Alias("ListApplicationsForUserAsync")]
    Task<IReadOnlyList<string>> ListApplicationsForUserAsync(string userId, string? legacyUserKey);
}
