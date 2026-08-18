
namespace Agent42.Backend;

/// <summary>
/// The read-only configuration health report (ADR 0029). The report never contains a secret value — only a status and a curated message.
/// </summary>
public static partial class AgentApi
{
    private static void MapConfigurationEndpoints(IEndpointRouteBuilder app)
    {
        // A read-only diagnostic of which config inputs are present/malformed/(opt-in) unreachable — so a
        // silently degraded feature (geocoding off, push off, context.dev failing) is visible. Behind Easy
        // Auth like every /api/* read; all allowlisted users may view (presence booleans are low-sensitivity,
        // no role system). NEVER leaks a secret — ConfigHealth exposes a status + curated message only.
        //
        // Static by default; ?probe=true also runs the opt-in LIVE probes (a geocoding/context.dev probe
        // costs a billable call, so the static call must run ZERO probes). Injects IServiceProvider (probe
        // deps are resolved OPTIONALLY inside ProbeAsync) plus the both-hosts-registered IConfiguration /
        // IHostEnvironment / PushConfig — deliberately NOT IHttpClientFactory as a parameter: it is not
        // registered in DevHost, and a minimal-API param it can't resolve breaks routing for the whole host.
        app.MapGet("/api/config-health", async (
            bool? probe, IConfiguration configuration, IHostEnvironment environment, PushConfig push,
            IServiceProvider services, CancellationToken cancellationToken) =>
        {
            var report = ConfigHealth.BuildStatic(configuration, environment, push);
            if (probe == true)
            {
                report = await ConfigHealth.ProbeAsync(report, services, configuration, cancellationToken);
            }

            return Results.Json(report);
        });

        // ————— usage dashboard (token telemetry, ADR 0031) —————
    }
}
