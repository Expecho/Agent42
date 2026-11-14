
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// The token-usage dashboard reads (ADR 0031), scoped to the signed-in user (ADR 0121).
/// </summary>
public static partial class AgentApi
{
    private static void MapTelemetryEndpoints(IEndpointRouteBuilder app)
    {
        // Token/tool usage aggregated from OpenTelemetry gen_ai spans, scoped to the SIGNED-IN USER ONLY
        // (ADR 0121, superseding ADR 0031's global view): once anyone can sign in, "every user's spend and
        // email" is no longer an acceptable read. Behind Easy Auth like every /api/* read. The caller is
        // derived HERE from the request, never from a query parameter: a `user` parameter is ignored, so
        // `?user=<someone else>` cannot widen the scope. The scope is the post-alias object id, not the
        // email, because an email claim is mutable and not unique across tenants (see UsageUserKey). A
        // caller with no identity gets an empty report, never "all". range ∈ 24h|7d|30d|all (unknown →
        // 400; omitted → 30d default; 24h reads hourly, else daily); application accepts 'all' or omitted
        // = no filter. Resolves services from the request (both-hosts-resolvable, optional), never the
        // Backend-only ingestion channel, which would break DevHost route param inference.
        app.MapGet("/api/usage", async (string? application, string? range, IGrainFactory grains, HttpContext http) =>
        {
            var normalizedRange = string.IsNullOrWhiteSpace(range) ? "30d" : range.ToLowerInvariant();
            if (normalizedRange is not ("24h" or "7d" or "30d" or "all"))
            {
                return Results.Text(
                    $"unknown range '{range}' (expected 24h|7d|30d|all)", "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var (userId, legacyKey) = UsageCaller(http);
            var report = await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
                .QueryForUserAsync(normalizedRange, NormalizeFilter(application), userId, legacyKey);
            return Results.Json(report);
        });

        // Distinct application values in the CALLER'S scope for the filter dropdown (kept separate so a
        // dropdown doesn't reset when a filter narrows the main payload). The old /api/usage/users list
        // is gone (ADR 0121): there is no user picker when you can only see yourself.
        app.MapGet("/api/usage/applications", async (IGrainFactory grains, HttpContext http) =>
        {
            var (userId, legacyKey) = UsageCaller(http);
            return Results.Json(await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
                .ListApplicationsForUserAsync(userId, legacyKey));
        });
    }

    /// <summary>
    /// The caller's usage scope: their post-alias object id (the same <c>GetUserId</c> that stamps
    /// <c>user.id</c> on spans, gotchas: one function feeds the stamp and the filter) and, for an OWNER only,
    /// the legacy <see cref="UsageUserKey"/> that matches buckets recorded before the id was stored. Every
    /// pre-change bucket belongs to an owner (the only people who could sign in), so a non-owner, including one
    /// whose email equals an owner's, gets <c>null</c> and sees none of them.
    /// </summary>
    private static (string UserId, string? LegacyKey) UsageCaller(HttpContext http)
    {
        var userId = http.GetUserId();
        if (http.RequestServices.GetService<IOwnerPolicy>() is not { } policy || !policy.IsOwner(userId))
        {
            return (userId, null);
        }

        var context = http.Request.Headers.GetUserTelemetryContext(http.RequestServices.GetService<UserIdAliases>());
        return (userId, UsageUserKey.From(context.Email, context.Name, context.Id));
    }

    // A usage filter of null, blank, or "all" means "no filter" (ADR 0031) — collapse them so the
    // grain query sees a single sentinel.
    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
}
