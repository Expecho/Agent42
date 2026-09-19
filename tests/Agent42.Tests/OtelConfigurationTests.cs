using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using Agent42.Agents;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Agent42.Backend;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
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
[Collection("LiveTelemetry")]
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

        // Scoped to THIS pipeline's own source, and that scoping is load-bearing — see the corrected
        // diagnosis on LiveTelemetryCollection. A bare Assert.Empty here is unfixably racy: this test
        // calls ConfigureOpenTelemetry(), which subscribes to the `Microsoft.AspNetCore` instrumentation,
        // and an ActivityListener is PROCESS-GLOBAL. So while this host is alive, every server span from
        // every other test class's TestServer lands in this buffer — measured, the contaminants were
        // `PUT /api/conversations/{conversationId}/pin` and `GET /api/conversations`, both Source =
        // Microsoft.AspNetCore. No xUnit collection can fix that, because the contaminators are the whole
        // API suite rather than the three classes that emit gen_ai spans.
        // This is NOT a weakened assertion: nothing in this test emits on OtelSourceName, and the classes
        // that do (MeterNamingTests, AzureMonitorTraceExportTests) are serialised with this one by the
        // LiveTelemetry collection, so a span appearing here would still be a real defect.
        var ownSpans = buffer.SpansSince(0, null).Spans
            .Where(s => s.Source == LiveTelemetryCaptureExtensions.OtelSourceName)
            .ToArray();

        Assert.Empty(ownSpans);
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

    /// <summary>
    /// Without a connection string nothing about Application Insights is registered at all, and a span
    /// still reaches OTLP with its content: today's pipeline, unchanged. Every Azure Monitor registration
    /// goes through the one options call, so no configured options means no exporter.
    /// </summary>
    [Fact]
    public async Task NoConnectionString_AddsNoAzureMonitorExport()
    {
        await using var otlp = await TelemetryLoopback.StartAsync();
        var sourceName = NewSourceName();
        using var host = BuildBackendHost(new()
        {
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = otlp.BaseAddress.ToString(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
        }, sourceName, withAzureMonitorTestOptions: false);

        EmitContentSpan(host, sourceName);

        Assert.Empty(host.Services.GetServices<IConfigureOptions<AzureMonitorExporterOptions>>());
        Assert.Empty(otlp.AppInsightsItems);
        Assert.Contains(otlp.OtlpTraceBodies, body => body.Contains(ContentSentinel, StringComparison.Ordinal));
    }

    /// <summary>
    /// The production shape: both OTLP endpoints and App Insights. The env endpoint speaks OTLP/HTTP and
    /// the local collector OTLP/gRPC, so both hand-built trace exporters in the fan-out are exercised. Both
    /// collectors get the content; App Insights gets the span and the metrics, without the content.
    /// </summary>
    [Fact]
    public async Task WithAppInsightsAndOtlp_SpansReachBoth()
    {
        await using var http = await TelemetryLoopback.StartAsync();
        await using var grpc = await TelemetryLoopback.StartAsync(http2: true);
        var sourceName = NewSourceName();
        using var host = BuildBackendHost(new()
        {
            [AzureMonitorTraceExport.ConnectionStringKey] = http.ConnectionString,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = http.BaseAddress.ToString(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            ["OTEL_SERVICE_NAME"] = TestServiceName,
            ["Otel:LocalCollector:Endpoint"] = grpc.BaseAddress.ToString(),
            ["Otel:LocalCollector:Headers"] = "x-telemetry-ingest-key=test",
        }, sourceName);

        EmitContentSpan(host, sourceName);

        Assert.Contains(http.OtlpTraceBodies, body => body.Contains(ContentSentinel, StringComparison.Ordinal));
        Assert.Contains(grpc.OtlpTraceBodies, body => body.Contains(ContentSentinel, StringComparison.Ordinal));
        Assert.Contains(grpc.Requests, request => request.Path == "/opentelemetry.proto.collector.trace.v1.TraceService/Export");

        var span = Assert.Single(http.AppInsightsItems, item => item.Contains(SpanName, StringComparison.Ordinal));
        Assert.DoesNotContain(ContentSentinel, span, StringComparison.Ordinal);
        Assert.Contains($"\"ai.cloud.role\":\"{TestServiceName}\"", span, StringComparison.Ordinal);
        Assert.Contains(http.AppInsightsItems, item => item.Contains(CounterName, StringComparison.Ordinal));
    }

    /// <summary>
    /// The production trace path to the home collector: the env endpoint over gRPC
    /// (<c>OTEL_EXPORTER_OTLP_PROTOCOL=grpc</c> in the container env), rebuilt by hand in the fan-out. Over gRPC
    /// no signal path may be appended, and the configured headers (Aspire's <c>x-otlp-api-key</c> locally)
    /// must still be sent. With App Insights on, this exporter is the home collector's ONLY trace path.
    /// </summary>
    [Fact]
    public async Task WithAppInsights_GrpcEnvEndpoint_ReachesTheCollectorWithHeaders()
    {
        await using var appInsights = await TelemetryLoopback.StartAsync();
        await using var grpc = await TelemetryLoopback.StartAsync(http2: true);
        var sourceName = NewSourceName();
        using var host = BuildBackendHost(new()
        {
            [AzureMonitorTraceExport.ConnectionStringKey] = appInsights.ConnectionString,
            ["OTEL_EXPORTER_OTLP_ENDPOINT"] = grpc.BaseAddress.ToString(),
            ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "grpc",
            ["OTEL_EXPORTER_OTLP_HEADERS"] = "x-otlp-api-key=test-key",
        }, sourceName);

        EmitContentSpan(host, sourceName);

        var export = Assert.Single(grpc.Requests, request => request.Body.Contains(ContentSentinel, StringComparison.Ordinal));
        // The exact gRPC method path. The client appends it to whatever path the endpoint carries, so an
        // endpoint with v1/traces appended would still reach this loopback, and a real collector would
        // answer Unimplemented.
        Assert.Equal("/opentelemetry.proto.collector.trace.v1.TraceService/Export", export.Path);
        Assert.Equal("test-key", export.Headers["x-otlp-api-key"]);
        Assert.Contains(appInsights.AppInsightsItems, item => item.Contains(SpanName, StringComparison.Ordinal));
    }

    /// <summary>
    /// App Insights with no OTLP endpoint at all still exports: its registration must not sit behind the
    /// early return ServiceDefaults takes when neither OTLP endpoint is configured.
    /// </summary>
    [Fact]
    public async Task AppInsightsOnly_StillExports()
    {
        await using var loopback = await TelemetryLoopback.StartAsync();
        var sourceName = NewSourceName();
        using var host = BuildBackendHost(
            new() { [AzureMonitorTraceExport.ConnectionStringKey] = loopback.ConnectionString }, sourceName);

        EmitContentSpan(host, sourceName);

        Assert.Empty(loopback.OtlpTraceBodies);
        Assert.Contains(loopback.AppInsightsItems, item => item.Contains(SpanName, StringComparison.Ordinal));
        Assert.Contains(loopback.AppInsightsItems, item => item.Contains(CounterName, StringComparison.Ordinal));
    }

    private const string TestServiceName = "agent42-otel-config-test";
    private const string SpanName = "chat otel-config-test";
    private const string ContentSentinel = "SENTINEL_OTELCONFIG_CONTENT_91c2";
    private const string CounterName = "agent42.tests.otel_config.counter";

    private static string NewSourceName() => "Agent42.Tests.OtelConfig." + Guid.NewGuid().ToString("N");

    private static IHost BuildBackendHost(
        Dictionary<string, string?> config, string sourceName, bool withAzureMonitorTestOptions = true)
    {
        if (!withAzureMonitorTestOptions)
        {
            // The same composition, minus the test's own Configure<AzureMonitorExporterOptions>, which
            // would otherwise be the one registration the AC1 assertion finds.
            var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
            builder.Configuration.AddInMemoryCollection(config);
            builder.ConfigureOpenTelemetry();
            builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(sourceName));
            builder.AddBackendTelemetry(Channel.CreateUnbounded<UsageEvent>().Writer);
            return builder.Build();
        }

        return AzureMonitorTraceExportTests.BuildBackendHost(config, sourceName: sourceName, meterName: sourceName);
    }

    /// <summary>
    /// Emits one gen_ai-shaped span carrying content, plus one counter measurement on a meter of the same
    /// name, then flushes both providers.
    /// </summary>
    private static void EmitContentSpan(IHost host, string sourceName)
    {
        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();

        using (var source = new ActivitySource(sourceName))
        using (var activity = source.StartActivity(SpanName)!)
        {
            activity.SetTag(AzureMonitorTraceExport.GenAiOperationNameTag, "chat");
            activity.SetTag("gen_ai.input.messages", ContentSentinel);
        }

        using (var meter = new Meter(sourceName))
        {
            meter.CreateCounter<long>(CounterName).Add(1);
            meterProvider.ForceFlush();
        }

        tracerProvider.ForceFlush();
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
