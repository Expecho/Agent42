namespace Agent42.Agents;

/// <summary>
/// Retention bounds for the single global rollup grain (ADR 0031). The grain serializes its whole
/// state into one <c>agent-state</c> Cosmos item per write, hard-capped at 2 MB (ADR 0004), so aged
/// buckets are pruned on every write. Hourly buckets serve the 24h range; daily buckets serve
/// 7d/30d/all — so <c>all</c> means "the retained daily window", not literally all-time.
/// </summary>
public static class UsageRetention
{
    public static readonly TimeSpan Hourly = TimeSpan.FromHours(48);
    public static readonly TimeSpan Daily = TimeSpan.FromDays(92);
}

/// <summary>A token rollup for one (bucket, application, provider, model, user) cell.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.TokenBucket")]
public sealed class TokenBucket
{
    [Id(0)] public long BucketUnixSeconds { get; set; }
    [Id(1)] public string Application { get; set; } = "";
    [Id(2)] public string Provider { get; set; } = "";
    [Id(3)] public string Model { get; set; } = "";
    [Id(4)] public string UserKey { get; set; } = "";
    [Id(5)] public long InputTokens { get; set; }
    [Id(6)] public long OutputTokens { get; set; }
    [Id(7)] public long CachedInputTokens { get; set; }
    [Id(8)] public long ReasoningOutputTokens { get; set; }
    [Id(9)] public long CallCount { get; set; }
    [Id(10)] public long ErrorCount { get; set; }

    /// <summary>
    /// The serving provider's kind (ADR 0110). <c>""</c> on every bucket written before the tag existed, which prices
    /// as unpriced: those calls are honestly unknown, and no migration is needed (cross-cutting rule 2).
    /// </summary>
    [Id(11)] public string ProviderKind { get; set; } = "";

    /// <summary>
    /// The post-alias object id the call belongs to (ADR 0121). <c>""</c> on every bucket written before it existed:
    /// those belong to an owner (the only users that could sign in) and match by <see cref="UserKey"/> for an owner
    /// only. No migration (cross-cutting rule 2).
    /// </summary>
    [Id(12)] public string UserId { get; set; } = "";
}

/// <summary>A tool-call rollup for one (bucket, application, tool, user) cell.</summary>
[GenerateSerializer]
[Alias("Agent42.Agents.ToolBucket")]
public sealed class ToolBucket
{
    [Id(0)] public long BucketUnixSeconds { get; set; }
    [Id(1)] public string Application { get; set; } = "";
    [Id(2)] public string ToolName { get; set; } = "";
    [Id(3)] public string UserKey { get; set; } = "";
    [Id(4)] public long Count { get; set; }
    [Id(5)] public long ErrorCount { get; set; }

    /// <summary>See <see cref="TokenBucket.UserId"/>.</summary>
    [Id(6)] public string UserId { get; set; } = "";
}

/// <summary>
/// The global usage rollup (ADR 0031). Pure logic — no Orleans, no I/O — so it is unit-testable
/// without a cluster; <see cref="UsageRollupGrain"/> is the thin persistence wrapper. Holds hourly +
/// daily token and tool buckets keyed by a composite string (dedup only); each bucket value carries
/// its own dimensions so <see cref="Query"/> filters/groups off the values, never the keys.
/// </summary>
[GenerateSerializer]
[Alias("Agent42.Agents.UsageRollupState")]
public sealed class UsageRollupState
{
    // The composite bucket-key delimiter. A control char (unit separator) that cannot appear in a
    // model/provider/email/application value, so distinct cells never collide into one bucket.
    private const char KeyDelimiter = '';

    [Id(0)] public Dictionary<string, TokenBucket> HourlyTokens { get; set; } = new();
    [Id(1)] public Dictionary<string, TokenBucket> DailyTokens { get; set; } = new();
    [Id(2)] public Dictionary<string, ToolBucket> HourlyTools { get; set; } = new();
    [Id(3)] public Dictionary<string, ToolBucket> DailyTools { get; set; } = new();

    /// <summary>Folds one projected event into both the hourly and daily rollups.</summary>
    public void Apply(UsageEvent e)
    {
        if (e.Kind == UsageEventKind.Token)
        {
            ApplyToken(HourlyTokens, HourStart(e.Timestamp), e);
            ApplyToken(DailyTokens, DayStart(e.Timestamp), e);
        }
        else
        {
            ApplyTool(HourlyTools, HourStart(e.Timestamp), e);
            ApplyTool(DailyTools, DayStart(e.Timestamp), e);
        }
    }

    /// <summary>Drops buckets older than the retention bounds — called on every write to hold the 2 MB cap.</summary>
    public void Prune(DateTimeOffset now)
    {
        var hourlyFloor = now.Subtract(UsageRetention.Hourly).ToUnixTimeSeconds();
        var dailyFloor = now.Subtract(UsageRetention.Daily).ToUnixTimeSeconds();
        Drop(HourlyTokens, b => b.BucketUnixSeconds < hourlyFloor);
        Drop(DailyTokens, b => b.BucketUnixSeconds < dailyFloor);
        Drop(HourlyTools, b => b.BucketUnixSeconds < hourlyFloor);
        Drop(DailyTools, b => b.BucketUnixSeconds < dailyFloor);
    }

    /// <summary>
    /// Aggregates the rollups for a range (<c>24h</c> reads hourly; <c>7d</c>/<c>30d</c>/<c>all</c>
    /// read daily) with an optional application filter into the wire contract, scoped to ONE user (ADR 0121).
    ///
    /// <para><b>Always scoped, never "all".</b> A blank <paramref name="userId"/> yields an empty report. A bucket
    /// is in scope when its <see cref="TokenBucket.UserId"/> equals <paramref name="userId"/>, or, only when
    /// <paramref name="legacyUserKey"/> is non-null (the caller is an owner), when it predates the id (empty
    /// <c>UserId</c>) and its <see cref="TokenBucket.UserKey"/> equals that key.</para>
    /// </summary>
    public UsageReport Query(
        string range, string? application, string userId, string? legacyUserKey, DateTimeOffset now)
    {
        var useHourly = range == "24h";
        var cutoff = range switch
        {
            "24h" => now.AddHours(-24).ToUnixTimeSeconds(),
            "7d" => now.AddDays(-7).ToUnixTimeSeconds(),
            "30d" => now.AddDays(-30).ToUnixTimeSeconds(),
            _ => long.MinValue, // "all" — the whole retained window
        };

        var tokenBuckets = (useHourly ? HourlyTokens : DailyTokens).Values
            .Where(b => b.BucketUnixSeconds >= cutoff)
            .Where(b => application is null || b.Application == application)
            .Where(b => InScope(b.UserId, b.UserKey, userId, legacyUserKey))
            .ToList();

        var toolBuckets = (useHourly ? HourlyTools : DailyTools).Values
            .Where(b => b.BucketUnixSeconds >= cutoff)
            .Where(b => application is null || b.Application == application)
            .Where(b => InScope(b.UserId, b.UserKey, userId, legacyUserKey))
            .ToList();

        var totalCost = CostSum.Of(tokenBuckets);
        var totals = new UsageTotals(
            InputTokens: tokenBuckets.Sum(b => b.InputTokens),
            OutputTokens: tokenBuckets.Sum(b => b.OutputTokens),
            CachedInputTokens: tokenBuckets.Sum(b => b.CachedInputTokens),
            CallCount: tokenBuckets.Sum(b => b.CallCount),
            ErrorCount: tokenBuckets.Sum(b => b.ErrorCount) + toolBuckets.Sum(b => b.ErrorCount),
            ToolCallCount: toolBuckets.Sum(b => b.Count),
            CostUsd: totalCost.Usd,
            UnpricedCallCount: totalCost.UnpricedCalls);

        var models = tokenBuckets
            .GroupBy(b => (b.Application, b.Model, b.Provider, b.ProviderKind))
            .Select(g =>
            {
                var cost = CostSum.Of(g);
                return new ModelUsage(
                    g.Key.Application, g.Key.Model, g.Key.Provider,
                    g.Sum(b => b.InputTokens), g.Sum(b => b.OutputTokens), g.Sum(b => b.CachedInputTokens),
                    g.Sum(b => b.ReasoningOutputTokens), g.Sum(b => b.CallCount), g.Sum(b => b.ErrorCount),
                    g.Key.ProviderKind, cost.Usd, cost.Status(g.Key.ProviderKind));
            })
            .OrderByDescending(m => m.InputTokens + m.OutputTokens)
            .ToList();

        var tools = toolBuckets
            .GroupBy(b => (b.Application, b.ToolName))
            .Select(g => new ToolUsage(
                g.Key.Application, g.Key.ToolName, g.Sum(b => b.Count), g.Sum(b => b.ErrorCount)))
            .OrderByDescending(t => t.Count)
            .ToList();

        var users = tokenBuckets.Select(b => b.UserKey).Concat(toolBuckets.Select(b => b.UserKey))
            .Distinct()
            .Select(u => new UserUsage(
                u,
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.InputTokens),
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.OutputTokens),
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.CachedInputTokens),
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.ReasoningOutputTokens),
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.CallCount),
                tokenBuckets.Where(b => b.UserKey == u).Sum(b => b.ErrorCount) + toolBuckets.Where(b => b.UserKey == u).Sum(b => b.ErrorCount),
                toolBuckets.Where(b => b.UserKey == u).Sum(b => b.Count),
                CostSum.Of(tokenBuckets.Where(b => b.UserKey == u)).Usd,
                CostSum.Of(tokenBuckets.Where(b => b.UserKey == u)).UnpricedCalls))
            .OrderByDescending(u => u.InputTokens + u.OutputTokens)
            .ToList();

        return new UsageReport(totals, models, tools, users);
    }

    private static bool InScope(string bucketUserId, string bucketUserKey, string userId, string? legacyUserKey)
        => userId.Length > 0
            && (string.Equals(bucketUserId, userId, StringComparison.OrdinalIgnoreCase)
                || (legacyUserKey is not null && bucketUserId.Length == 0 && bucketUserKey == legacyUserKey));

    /// <summary>Distinct application names seen in the caller's scope (for the filter dropdown), see <see cref="Query"/>.</summary>
    public IReadOnlyList<string> Applications(string userId, string? legacyUserKey)
        => DailyTokens.Values.Where(b => InScope(b.UserId, b.UserKey, userId, legacyUserKey)).Select(b => b.Application)
            .Concat(DailyTools.Values.Where(b => InScope(b.UserId, b.UserKey, userId, legacyUserKey)).Select(b => b.Application))
            .Where(a => a.Length > 0).Distinct().OrderBy(a => a, StringComparer.Ordinal).ToList();

    private static void ApplyToken(Dictionary<string, TokenBucket> map, long bucket, UsageEvent e)
    {
        var key = string.Join(KeyDelimiter, bucket, e.Application, e.Provider, e.Model, e.UserKey, e.ProviderKind, e.UserId);
        if (!map.TryGetValue(key, out var agg))
        {
            agg = new TokenBucket
            {
                BucketUnixSeconds = bucket,
                Application = e.Application,
                Provider = e.Provider,
                Model = e.Model,
                UserKey = e.UserKey,
                UserId = e.UserId,
                ProviderKind = e.ProviderKind,
            };
            map[key] = agg;
        }

        agg.InputTokens += e.InputTokens;
        agg.OutputTokens += e.OutputTokens;
        agg.CachedInputTokens += e.CachedInputTokens;
        agg.ReasoningOutputTokens += e.ReasoningOutputTokens;
        agg.CallCount += 1;
        if (e.IsError)
        {
            agg.ErrorCount += 1;
        }
    }

    private static void ApplyTool(Dictionary<string, ToolBucket> map, long bucket, UsageEvent e)
    {
        var key = string.Join(KeyDelimiter, bucket, e.Application, e.ToolName, e.UserKey, e.UserId);
        if (!map.TryGetValue(key, out var agg))
        {
            agg = new ToolBucket
            {
                BucketUnixSeconds = bucket,
                Application = e.Application,
                ToolName = e.ToolName,
                UserKey = e.UserKey,
                UserId = e.UserId,
            };
            map[key] = agg;
        }

        agg.Count += 1;
        if (e.IsError)
        {
            agg.ErrorCount += 1;
        }
    }

    /// <summary>
    /// The estimated cost of a set of token buckets (ADR 0110), derived at read time and never stored. Each bucket is
    /// priced at its own bucket start, so a reprice dates correctly. Included (Copilot) buckets add $0; unpriced ones add
    /// nothing and are counted instead, so a partial total is visibly partial. There is no cache-write term: no OTel
    /// tag carries Anthropic cache writes, so /usage bills them at the input rate (the turn line uses 1.25x).
    /// </summary>
    private readonly record struct CostSum(decimal? Usd, long UnpricedCalls)
    {
        public static CostSum Of(IEnumerable<TokenBucket> buckets)
        {
            decimal? usd = null;
            long unpriced = 0;
            foreach (var b in buckets)
            {
                var estimate = ModelPricing.Estimate(
                    b.ProviderKind, b.Model, DateTimeOffset.FromUnixTimeSeconds(b.BucketUnixSeconds),
                    new TokenCounts(b.InputTokens, b.OutputTokens, b.CachedInputTokens));
                if (estimate.Status == CostStatus.Unpriced)
                {
                    unpriced += b.CallCount;
                }
                else
                {
                    usd = (usd ?? 0m) + (estimate.Usd ?? 0m);
                }
            }

            return new CostSum(usd, unpriced);
        }

        /// <summary>A model row's status. One kind and model per row, so it is mixed only across an effective date.</summary>
        public string Status(string providerKind) => (Usd, UnpricedCalls) switch
        {
            (null, _) => CostStatus.Unpriced,
            (_, > 0) => CostStatus.Partial,
            _ when providerKind == ModelPricing.GitHubCopilotKind => CostStatus.Included,
            _ => CostStatus.Priced,
        };
    }

    private static void Drop<T>(Dictionary<string, T> map, Func<T, bool> aged)
    {
        foreach (var key in map.Where(kv => aged(kv.Value)).Select(kv => kv.Key).ToList())
        {
            map.Remove(key);
        }
    }

    private static long HourStart(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, u.Hour, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }

    private static long DayStart(DateTimeOffset t)
    {
        var u = t.ToUniversalTime();
        return new DateTimeOffset(u.Year, u.Month, u.Day, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
    }
}
