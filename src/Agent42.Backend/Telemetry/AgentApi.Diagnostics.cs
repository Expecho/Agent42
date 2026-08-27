
namespace Agent42.Backend;

/// <summary>
/// The reads behind the live telemetry console at <c>/logs</c> (ADR 0054). Global and cross-user by
/// design, like the usage dashboard next door.
///
/// <para><b>Its own partial rather than three more routes on <c>MapTelemetryEndpoints</c>.</b> The
/// cognitive-complexity gate (ADR 0050, S3776) measures a <c>Map*Endpoints</c> method as the sum of its
/// independent registration lambdas, which is exactly how those methods historically blew the
/// threshold — so a new endpoint group gets a new partial. It must be called from the composition root
/// in <c>Api/AgentApi.cs</c>: a group that exists but is never registered compiles cleanly and silently
/// deletes every endpoint in it (ADR 0040).</para>
/// </summary>
public static partial class AgentApi
{
    /// <summary>
    /// The framework's own default minimum level, used when nothing configures one — which is the normal
    /// state offline, since <c>tools/Agent42.DevHost</c> has no <c>appsettings.json</c> at all.
    /// </summary>
    private const string DefaultMinimumLogLevel = "Information";

    private static void MapDiagnosticsEndpoints(IEndpointRouteBuilder app)
    {
        // All three are behind Easy Auth like every other /api/* read and are deliberately NOT in the
        // container-app.bicep excludedPaths list — /logs is authenticated-only, so leaving that list
        // untouched is the fix, not an omission (ADR 0042).
        //
        // They are also GLOBAL: no GetUserId scoping, the same posture as /api/usage. With no role
        // system, any signed-in user sees every user's telemetry — and because the chat client runs with
        // EnableSensitiveData = true, that includes full prompt and completion text. That exposure is the
        // headline consequence of ADR 0054 and is recorded there rather than softened here.
        //
        // The buffer is resolved OPTIONALLY from IServiceProvider rather than taken as a handler
        // parameter: MapAgentApi infers every endpoint's parameters up front, so an unresolvable
        // parameter breaks routing for the WHOLE host. Absent capture degrades to an empty payload.
        app.MapGet("/api/diagnostics/traces", (long? since, int? limit, IServiceProvider services) =>
        {
            var buffer = services.GetService<LiveTelemetryBuffer>();
            return Results.Json(buffer is null
                ? new TracesResponse([], since ?? 0, 0)
                : buffer.SpansSince(since ?? 0, limit));
        });

        app.MapGet("/api/diagnostics/logs", (
            long? since, int? limit, string? minLevel, IConfiguration configuration, IServiceProvider services) =>
        {
            var buffer = services.GetService<LiveTelemetryBuffer>();
            var effective = EffectiveMinimumLogLevel(configuration);

            return Results.Json(buffer is null
                ? new LogsResponse([], since ?? 0, 0, effective)
                : buffer.LogsSince(since ?? 0, limit, minLevel, effective));
        });

        app.MapGet("/api/diagnostics/metrics", (IServiceProvider services) =>
        {
            var buffer = services.GetService<LiveTelemetryBuffer>();
            return Results.Json(buffer?.Metrics ?? new MetricsResponse([], null));
        });
    }

    /// <summary>
    /// The minimum level records must clear to reach the buffer at all.
    ///
    /// <para>Reported to the console because the Logs tab is <b>legitimately empty in production</b>:
    /// <c>appsettings.json</c> sets this to <c>Warning</c>, and <c>ILogger</c> category filtering runs
    /// <i>before</i> the OpenTelemetry logging processor — so Information and Debug records never reach
    /// <see cref="LiveLogProcessor"/> and no buffer setting can recover them. Without this an empty tab
    /// reads as broken rather than as correct.</para>
    ///
    /// <para><b>This is the default rule only, not a fully resolved effective level.</b> Per-category
    /// overrides (<c>Logging:LogLevel:Microsoft.AspNetCore</c>, also <c>Warning</c> today) and
    /// per-provider sections (<c>Logging:OpenTelemetry:LogLevel:*</c>) can filter a given category
    /// differently. Reporting the default is what makes an empty tab legible; the console names the
    /// configuration key alongside the value so the number is not mistaken for a per-category
    /// guarantee.</para>
    /// </summary>
    private static string EffectiveMinimumLogLevel(IConfiguration configuration)
    {
        var configured = configuration["Logging:LogLevel:Default"];
        return string.IsNullOrWhiteSpace(configured) ? DefaultMinimumLogLevel : configured;
    }
}
