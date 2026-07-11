using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Agent42.Tests;

/// <summary>
/// ServiceDefaults wires OpenTelemetry with per-signal OTLP exporters so it can fan telemetry out to
/// two endpoints at once — the env-var endpoint (Aspire dashboard / Bicep otlpEndpoint) plus an
/// optional config-driven local collector (<c>Otel:LocalCollector:*</c>). That combination is only
/// legal via <c>AddOtlpExporter()</c>: <c>UseOtlpExporter()</c> is one-shot and throws
/// <see cref="NotSupportedException"/> if mixed with it. These tests build the real pipeline and force
/// the providers to construct, which is where such a conflict would surface. This is the only coverage
/// of that wiring: the Backend is the sole host that calls <c>AddServiceDefaults</c>, so DevHost
/// booting never exercises it.
/// </summary>
public sealed class OtelConfigurationTests
{
    private static IHost BuildHost(Dictionary<string, string?> config)
    {
        // Empty builder = no ambient env vars / appsettings, so the test fully controls configuration.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(config);
        builder.ConfigureOpenTelemetry();
        return builder.Build();
    }

    [Fact]
    public void ConfigureOpenTelemetry_WithBothEndpoints_BuildsDualExporterPipeline()
    {
        using var host = BuildHost(new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4318",
            ["Otel:LocalCollector:Endpoint"] = "http://localhost:4317",
            ["Otel:LocalCollector:Headers"] = "x-telemetry-ingest-key=test",
        });

        // Resolving the providers forces the trace + metric pipelines (two OTLP exporters each) to
        // build; a UseOtlpExporter/AddOtlpExporter conflict would throw here, not at registration.
        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
    }

    [Fact]
    public void ConfigureOpenTelemetry_LocalCollectorOnly_BuildsWithHeaders()
    {
        using var host = BuildHost(new()
        {
            ["Otel:LocalCollector:Endpoint"] = "http://localhost:4317",
            ["Otel:LocalCollector:Headers"] = "x-telemetry-ingest-key=test",
        });

        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
    }

    [Fact]
    public void ConfigureOpenTelemetry_NoEndpoints_BuildsWithoutExporters()
    {
        using var host = BuildHost([]);

        // Nothing configured: the pipeline still builds — exporters are simply not added.
        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
    }
}
