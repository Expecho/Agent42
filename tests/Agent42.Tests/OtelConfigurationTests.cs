using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agent42.Backend;
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
    private static IHost BuildHost(
        Dictionary<string, string?> config, bool withLiveCapture = false, bool addInstrumentation = false)
    {
        // Empty builder = no ambient env vars / appsettings, so the test fully controls configuration.
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(config);
        builder.ConfigureOpenTelemetry();

        if (withLiveCapture)
        {
            // CALLED, not merely resolved: the point is to exercise the real registration path in
            // LiveTelemetryCaptureExtensions, exactly as Program.cs does.
            builder.AddLiveTelemetryCapture(addInstrumentation);
        }

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

    /// <summary>
    /// Live telemetry capture (ADR 0054) composes with the existing dual-exporter pipeline. Its metric
    /// half adds a SECOND <c>PeriodicExportingMetricReader</c> beside the OTLP one and its log half calls
    /// <c>builder.Logging.AddOpenTelemetry</c> a second time — both additive, both measured, and both the
    /// kind of thing that surfaces when the providers actually construct rather than at registration.
    /// </summary>
    [Fact]
    public void AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline()
    {
        using var host = BuildHost(new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = "http://localhost:4318",
            ["Otel:LocalCollector:Endpoint"] = "http://localhost:4317",
        }, withLiveCapture: true);

        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());

        // The buffer the endpoints resolve optionally is registered by the same call.
        var buffer = host.Services.GetRequiredService<LiveTelemetryBuffer>();
        Assert.Empty(buffer.SpansSince(0, null).Spans);
    }

    /// <summary>
    /// Capture works with NO exporter registered at all — which is what makes the DevHost leg possible,
    /// since it has no OTLP endpoint and deliberately never calls <c>AddServiceDefaults</c>.
    /// </summary>
    [Fact]
    public void AddLiveTelemetryCapture_WorksWithNoExporterAtAll()
    {
        using var host = BuildHost([], withLiveCapture: true);

        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
        Assert.NotNull(host.Services.GetRequiredService<LiveTelemetryBuffer>());
    }

    /// <summary>
    /// The <c>Diagnostics</c> section is read key by key, each falling back to its own default. A
    /// whole-record bind would zero every key the section omits — and a zero <c>MaxSpans</c> is a console
    /// that captures nothing, from a config file that looks like it only changed one number.
    /// </summary>
    [Fact]
    public void AddLiveTelemetryCapture_ReadsEachDiagnosticsKeyIndependently()
    {
        using var host = BuildHost(new() { ["Diagnostics:MaxSpans"] = "7" }, withLiveCapture: true);

        var config = host.Services.GetRequiredService<LiveTelemetryBuffer>().Config;

        Assert.Equal(7, config.MaxSpans);
        Assert.Equal(DiagnosticsConfig.Default.MaxLogs, config.MaxLogs);
        Assert.Equal(DiagnosticsConfig.Default.MaxBufferBytes, config.MaxBufferBytes);
        Assert.Equal(DiagnosticsConfig.Default.MaxItemsPerPoll, config.MaxItemsPerPoll);
    }

    /// <summary>
    /// The <c>addInstrumentation: true</c> branch — the one DevHost takes, and the whole of "capture is
    /// exercisable offline". Nothing else in the suite executes it: the wiring guard reads
    /// <c>Program.cs</c> as source text, and every other case here takes the Backend's <c>false</c> path.
    /// Building the providers is what proves the sources and instrumentations it adds actually construct.
    /// </summary>
    [Fact]
    public void AddLiveTelemetryCapture_WithInstrumentation_BuildsTheDevHostShapedPipeline()
    {
        using var host = BuildHost([], withLiveCapture: true, addInstrumentation: true);

        Assert.NotNull(host.Services.GetRequiredService<TracerProvider>());
        Assert.NotNull(host.Services.GetRequiredService<MeterProvider>());
        Assert.NotNull(host.Services.GetRequiredService<LiveTelemetryBuffer>());
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
