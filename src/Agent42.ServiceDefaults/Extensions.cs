using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ServiceDiscovery;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

// Adds common Aspire services: service discovery, resilience, health checks, and OpenTelemetry.
// This project should be referenced by each service project in your solution.
// To learn more about using this project, see https://aka.ms/aspire/service-defaults
public static class Extensions
{
    private const string HealthEndpointPath = "/health";
    private const string AlivenessEndpointPath = "/alive";
    internal const string LocalCollectorEndpointKey = "Otel:LocalCollector:Endpoint";
    internal const string LocalCollectorHeadersKey = "Otel:LocalCollector:Headers";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();

        builder.AddDefaultHealthChecks();

        builder.Services.AddServiceDiscovery();

        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Turn on resilience by default
            http.AddStandardResilienceHandler();

            // Turn on service discovery by default
            http.AddServiceDiscovery();
        });

        // Uncomment the following to restrict the allowed schemes for service discovery.
        // builder.Services.Configure<ServiceDiscoveryOptions>(options =>
        // {
        //     options.AllowedSchemes = ["https"];
        // });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics()
            .WithTracing(tracing =>
            {
                tracing.AddSource(builder.Environment.ApplicationName)
                    .AddAspNetCoreInstrumentation(tracing =>
                        // Exclude health check requests from tracing
                        tracing.Filter = context =>
                            !context.Request.Path.StartsWithSegments(HealthEndpointPath)
                            && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath)
                    )
                    // Uncomment the following line to enable gRPC instrumentation (requires the OpenTelemetry.Instrumentation.GrpcNetClient package)
                    //.AddGrpcClientInstrumentation()
                    .AddHttpClientInstrumentation();
            });

        builder.AddOpenTelemetryExporters();

        return builder;
    }

    private static TBuilder AddOpenTelemetryExporters<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        // Application Insights (the ADR on App Insights export) gets metrics here and traces through the
        // fan-out the host registers LAST (AzureMonitorTraceExport.AddTraceExportFanOut), which then owns the
        // trace OTLP exporters too. Never logs: Warning+ templates carry model-chosen search queries and URLs.
        // Registered BEFORE the early return below, so an App-Insights-only setup still exports.
        var connectionString = builder.Configuration[AzureMonitorTraceExport.ConnectionStringKey];
        var toAppInsights = !string.IsNullOrWhiteSpace(connectionString);
        if (toAppInsights)
        {
            AzureMonitorTraceExport.AddAzureMonitorOptions(builder.Services, connectionString!);
            builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddAzureMonitorMetricExporter());
        }

        // Endpoint 1: the env-var endpoint (Aspire dashboard locally, the Bicep otlpEndpoint in prod).
        // AddOtlpExporter() with no options binds the standard OTEL_EXPORTER_OTLP_* keys from IConfiguration
        // (which includes the env vars) — the same source the previous UseOtlpExporter() used — so this
        // endpoint behaves exactly as before.
        var hasEnvEndpoint = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // Endpoint 2: an optional secondary OTLP/gRPC collector, exported to ALONGSIDE endpoint 1 and
        // driven entirely from config (endpoint + optional headers). Added only when
        // Otel:LocalCollector:Endpoint is set, so it's absent in prod unless deliberately configured —
        // the dev values live in appsettings.Development.json, which the Production environment never loads.
        var localEndpoint = builder.Configuration[LocalCollectorEndpointKey];
        var localHeaders = builder.Configuration[LocalCollectorHeadersKey];
        var addLocalCollector = !string.IsNullOrWhiteSpace(localEndpoint);

        if (!hasEnvEndpoint && !addLocalCollector)
        {
            return builder;
        }

        // Which of the two exporters to add, decided ONCE rather than re-tested per signal (ADR 0050 —
        // this was six copies of the same pair of ifs, nested one lambda deep each). Behaviour is
        // unchanged: the same calls, under the same conditions, in the same order.
        //
        // Each signal's AddOtlpExporter is a separate extension method on a separate builder type with
        // no shared interface, so the signal hands its own two overloads in as delegates rather than
        // this being generic over the builder.
        void AddConfiguredExporters(Action addEnvExporter, Action<Action<OtlpExporterOptions>> addLocalExporter)
        {
            if (hasEnvEndpoint)
            {
                addEnvExporter();
            }

            if (addLocalCollector)
            {
                addLocalExporter(options => ConfigureLocalCollector(options, localEndpoint!, localHeaders));
            }
        }

        // Two endpoints means per-signal AddOtlpExporter(): UseOtlpExporter() is one-shot and throws
        // NotSupportedException if combined with any signal-specific AddOtlpExporter().
        builder.Logging.AddOpenTelemetry(logging =>
            AddConfiguredExporters(() => logging.AddOtlpExporter(), configure => logging.AddOtlpExporter(configure)));

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
                AddConfiguredExporters(() => metrics.AddOtlpExporter(), configure => metrics.AddOtlpExporter(configure)))
            .WithTracing(tracing =>
            {
                // With App Insights on, the fan-out owns the trace OTLP exporters: it must export each span
                // to OTLP BEFORE it strips the span for App Insights.
                if (!toAppInsights)
                {
                    AddConfiguredExporters(() => tracing.AddOtlpExporter(), configure => tracing.AddOtlpExporter(configure));
                }
            });

        return builder;
    }

    /// <summary>
    /// The optional secondary collector: always OTLP/gRPC. OTLP headers are a comma-separated
    /// "key=value" string, omitted when none are configured.
    /// </summary>
    internal static void ConfigureLocalCollector(OtlpExporterOptions options, string endpoint, string? headers)
    {
        options.Endpoint = new Uri(endpoint);
        options.Protocol = OtlpExportProtocol.Grpc;
        if (!string.IsNullOrWhiteSpace(headers))
        {
            options.Headers = headers;
        }
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder) where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            // Add a default liveness check to ensure app is responsive
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Adding health checks endpoints to applications in non-development environments has security implications.
        // See https://aka.ms/aspire/healthchecks for details before enabling these endpoints in non-development environments.
        if (app.Environment.IsDevelopment())
        {
            // All health checks must pass for app to be considered ready to accept traffic after starting
            app.MapHealthChecks(HealthEndpointPath);

            // Only health checks tagged with the "live" tag must pass for app to be considered alive
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = r => r.Tags.Contains("live")
            });
        }

        return app;
    }
}
