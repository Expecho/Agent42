
using Agent42.Agents;


namespace Agent42.Backend;

/// <summary>
/// The token-usage dashboard reads (ADR 0031). Global and cross-user by design.
/// </summary>
public static partial class AgentApi
{
    private static void MapTelemetryEndpoints(IEndpointRouteBuilder app)
    {
        // Global, cross-user token/tool usage aggregated from OpenTelemetry gen_ai spans. Behind Easy
        // Auth like every /api/* read; deliberately GLOBAL (no GetUserId scoping) so any signed-in user
        // sees aggregate spend, matching the reference dashboard — an accepted cross-user exposure of
        // other users' email/usage to the ~2 allowlisted users (no role system). range ∈ 24h|7d|30d|all
        // (unknown → 400; omitted → 30d default; 24h reads hourly, else daily). application/user accept
        // 'all' or omitted = no filter. Injects only IGrainFactory (both-hosts-resolvable — never the
        // Backend-only ingestion channel, which would break DevHost route param inference).
        app.MapGet("/api/usage", async (string? application, string? user, string? range, IGrainFactory grains) =>
        {
            var normalizedRange = string.IsNullOrWhiteSpace(range) ? "30d" : range.ToLowerInvariant();
            if (normalizedRange is not ("24h" or "7d" or "30d" or "all"))
            {
                return Results.Text(
                    $"unknown range '{range}' (expected 24h|7d|30d|all)", "text/plain",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            var report = await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey)
                .QueryAsync(normalizedRange, NormalizeFilter(application), NormalizeFilter(user));
            return Results.Json(report);
        });

        // Distinct application/user values for the filter dropdowns (kept separate so a dropdown doesn't
        // reset when a filter narrows the main payload).
        app.MapGet("/api/usage/applications", async (IGrainFactory grains) =>
            Results.Json(await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey).ListApplicationsAsync()));

        app.MapGet("/api/usage/users", async (IGrainFactory grains) =>
            Results.Json(await grains.GetGrain<IUsageRollupGrain>(UsageRollupGrain.GlobalKey).ListUsersAsync()));
    }

    // A usage filter of null, blank, or "all" means "no filter" (ADR 0031) — collapse them so the
    // grain query sees a single sentinel.
    private static string? NormalizeFilter(string? value)
        => string.IsNullOrWhiteSpace(value) || string.Equals(value, "all", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
}
