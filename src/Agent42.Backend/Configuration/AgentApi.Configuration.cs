
namespace Agent42.Backend;

/// <summary>
/// The read-only configuration health report (ADR 0029). The report never contains a secret value — only a status and a curated message.
/// </summary>
public static partial class AgentApi
{
    private static void MapConfigurationEndpoints(IEndpointRouteBuilder app)
    {
        // A read-only diagnostic of which config inputs are present/malformed/(opt-in) unreachable — so a
        // silently degraded feature (geocoding off, push off, memory off) is visible. Behind Easy
        // Auth like every /api/* read, and owner-only on top via OwnerGate (ADR 0121; a non-owner gets 404).
        // NEVER leaks a secret — ConfigHealth exposes a status + curated message only.
        //
        // Static by default; ?probe=true also runs the opt-in LIVE probes (a geocoding probe
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

        // Whether the caller is an owner, so the UI can hide the owner-only Configuration tab instead of showing
        // an empty one (the gate above answers a non-owner 404, which the client swallows). Cosmetic and advisory
        // (ADR 0121): the gate, not this answer, is what protects /api/config-health. Deliberately NOT under
        // /api/config-health, which the gate covers. IOwnerPolicy is resolved optionally (the /api/speech
        // pattern): a host without one fails closed, as the gate does.
        app.MapGet("/api/access", (IServiceProvider services, HttpContext http) =>
            Results.Json(new AccessResponse(
                services.GetService<IOwnerPolicy>()?.IsOwner(http.GetUserId()) ?? false)));

        // ————— usage dashboard (token telemetry, ADR 0031) —————
    }
}
