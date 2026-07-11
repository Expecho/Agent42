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
    private const string LocalCollectorEndpointKey = "Otel:LocalCollector:Endpoint";
    private const string LocalCollectorHeadersKey = "Otel:LocalCollector:Headers";

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
            .WithMetrics(metrics =>
            {
                metrics.AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation();
            })
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
        // Endpoint 1: the env-var endpoint (Aspire dashboard locally, the Bicep otlpEndpoint in prod).
        // AddOtlpExporter() with no options reads the standard OTEL_EXPORTER_OTLP_* env vars — the same
        // source the previous UseOtlpExporter() used — so this endpoint behaves exactly as before.
        var hasEnvEndpoint = !string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        // Endpoint 2: an optional secondary OTLP/gRPC collector, exported to ALONGSIDE endpoint 1 and
        // driven entirely from config (endpoint + optional headers). Added only when
        // Otel:LocalCollector:Endpoint is set, so it's absent in prod unless deliberately configured —
        // the dev values live in appsettings.Development.json, which the Production environment never loads.
        var localEndpoint = builder.Configuration[LocalCollectorEndpointKey];
        var localHeaders = builder.Configuration[LocalCollectorHeadersKey];
        var addLocalCollector = !string.IsNullOrWhiteSpace(localEndpoint);

        // OTLP headers are a comma-separated "key=value" string; omitted when none configured.
        void ConfigureLocalCollector(OtlpExporterOptions options)
        {
            options.Endpoint = new Uri(localEndpoint!);
            options.Protocol = OtlpExportProtocol.Grpc;
            if (!string.IsNullOrWhiteSpace(localHeaders))
            {
                options.Headers = localHeaders;
            }
        }

        // Two endpoints means per-signal AddOtlpExporter(): UseOtlpExporter() is one-shot and throws
        // NotSupportedException if combined with any signal-specific AddOtlpExporter().
        if (hasEnvEndpoint || addLocalCollector)
        {
            builder.Logging.AddOpenTelemetry(logging =>
            {
                if (hasEnvEndpoint) logging.AddOtlpExporter();
                if (addLocalCollector) logging.AddOtlpExporter(ConfigureLocalCollector);
            });

            builder.Services.AddOpenTelemetry()
                .WithMetrics(metrics =>
                {
                    if (hasEnvEndpoint) metrics.AddOtlpExporter();
                    if (addLocalCollector) metrics.AddOtlpExporter(ConfigureLocalCollector);
                })
                .WithTracing(tracing =>
                {
                    if (hasEnvEndpoint) tracing.AddOtlpExporter();
                    if (addLocalCollector) tracing.AddOtlpExporter(ConfigureLocalCollector);
                });
        }

        // Uncomment the following lines to enable the Azure Monitor exporter (requires the Azure.Monitor.OpenTelemetry.AspNetCore package)
        //if (!string.IsNullOrEmpty(builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"]))
        //{
        //    builder.Services.AddOpenTelemetry()
        //       .UseAzureMonitor();
        //}

        return builder;
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
