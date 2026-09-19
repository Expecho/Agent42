using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Agent42.Backend;

/// <summary>
/// The single registration entry point for the live telemetry console at <c>/logs</c> (ADR 0054). Both
/// hosts call it, with different arguments — the Backend already has an instrumented OpenTelemetry
/// pipeline from <c>AddServiceDefaults()</c>, while <c>tools/Agent42.DevHost</c> has none at all.
///
/// <para>Capture is deliberately <b>additive and exporter-independent</b>: it adds a processor to each
/// signal and a second metric reader. It touches nothing about what the app already ships to the OTLP
/// collector and Application Insights <b>with one deliberate exception</b> — the unconditional
/// <c>AddMeter(OtelSourceName)</c> below, which subscribes the chat client's gen_ai histograms and so
/// reaches every metric exporter (OTLP, and App Insights when configured) as well as the ring, because all
/// readers hang off the one <c>MeterProvider</c>. That is the
/// point of it, and it is the only registration here that is not gated on <c>addInstrumentation</c>.
/// Measured facts this rests on — a second
/// <c>PeriodicExportingMetricReader</c> alongside the OTLP one captures the full instrument set; calling
/// <c>builder.Logging.AddOpenTelemetry</c> a second time is <b>additive, not duplicating</b> (one
/// provider, two processors, each record seen exactly once); and span capture works with no exporter
/// registered at all, which is what makes the offline leg possible.</para>
/// </summary>
public static class LiveTelemetryCaptureExtensions
{
    /// <summary>
    /// Registers the buffer and the three capture components.
    /// </summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="addInstrumentation">
    /// <c>true</c> only for a host with no OpenTelemetry pipeline of its own (DevHost), which then also
    /// gets the sources and instrumentations to capture — and still <b>no exporter</b>, so nothing leaves
    /// the machine offline. The Backend passes <c>false</c>: <c>AddServiceDefaults()</c> has already
    /// registered AspNetCore/Http/Runtime instrumentation there, and registering it twice risks duplicate
    /// spans. Additivity is measured for the logging provider, not assumed for instrumentation.
    /// </param>
    public static TBuilder AddLiveTelemetryCapture<TBuilder>(this TBuilder builder, bool addInstrumentation = false)
        where TBuilder : IHostApplicationBuilder
    {
        var buffer = new LiveTelemetryBuffer(ReadConfig(builder.Configuration));

        // Constructed here and registered as the instance, exactly like the UsageSpanProcessor channel in
        // Program.cs: the logging pipeline is configured off builder.Logging, outside the service
        // provider, so the one buffer both halves share cannot be resolved from DI at this point.
        builder.Services.AddSingleton(buffer);

        builder.Logging.AddOpenTelemetry(logging =>
        {
            // Set HERE and not only in ServiceDefaults' ConfigureOpenTelemetry, which DevHost never
            // calls: without them LogRecord.FormattedMessage is null offline and scopes yield nothing,
            // so the Logs tab would be a column of blank rows. Both options are idempotent, so setting
            // them again in the Backend is harmless.
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
            logging.AddProcessor(new LiveLogProcessor(buffer));
        });

        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing =>
            {
                if (addInstrumentation)
                {
                    tracing.AddSource(OtelSourceName)
                        .AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation();
                }

                tracing.AddProcessor(new LiveSpanProcessor(buffer));
            })
            .WithMetrics(metrics =>
            {
                // UNCONDITIONAL — the one line in this method that is not gated on addInstrumentation, and
                // the one that is not purely about the /logs console.
                //
                // MultiProviderChatClientFactory wraps every chat client in
                // UseOpenTelemetry(sourceName: OtelSourceName), and Microsoft.Extensions.AI names the Meter
                // from the SAME string it names the ActivitySource from. So the four gen_ai histograms —
                // token.usage, operation.duration, time_to_first_chunk, time_per_output_chunk — are
                // published on a meter called "Agent42.Backend", NOT on "Microsoft.Extensions.AI". A
                // MeterProvider subscribes by meter name and nothing else, so without this line those
                // instruments are recorded and every measurement is dropped — with no exception, warning or
                // missing-meter diagnostic, just a permanently empty token series. MeterNamingTests pins the
                // naming rule in both directions so a library bump cannot reintroduce that quietly.
                //
                // It belongs HERE rather than beside the Backend's AddSource(OtelSourceName) in
                // BackendTelemetryExtensions so that both hosts subscribe from one place and cannot drift.
                // The price is stated in
                // the class summary: this single line does change what the Backend ships to its OTLP
                // collector and to Application Insights, since every reader hangs off the one MeterProvider.
                //
                // The Agent Framework layer adds nothing further to subscribe to — Microsoft.Agents.AI
                // 1.19.0 contains no System.Diagnostics.Metrics references at all and is trace-only.
                metrics.AddMeter(OtelSourceName);

                if (addInstrumentation)
                {
                    metrics.AddAspNetCoreInstrumentation()
                        .AddHttpClientInstrumentation()
                        .AddRuntimeInstrumentation();
                }

                // A SECOND reader beside the OTLP one in the Backend, and the only reader in DevHost. Its
                // interval is its own: the SDK default of 60s leaves a live console blank for a minute,
                // and this reader exports in-process to a ring rather than over the wire.
                metrics.AddReader(new PeriodicExportingMetricReader(
                    new LiveMetricExporter(buffer),
                    exportIntervalMilliseconds: buffer.Config.MetricExportIntervalMs));
            });

        return builder;
    }

    /// <summary>
    /// The activity source the Backend's own spans are emitted on. Named here as well as in
    /// <c>Program.cs</c> because DevHost has to subscribe to it without duplicating the literal.
    /// </summary>
    public const string OtelSourceName = "Agent42.Backend";

    /// <summary>
    /// Reads the <c>Diagnostics</c> section key by key, each falling back to its own default, rather than
    /// binding the record whole. A whole-record bind of a positional record fills every key the section
    /// omits with <c>default</c> — so a config file that set one value would silently zero every other
    /// key, and a zero <c>MaxSpans</c> is a console that captures nothing. It also means the section
    /// being absent entirely is a supported state, which is the normal case offline: DevHost has no
    /// <c>appsettings.json</c> at all.
    /// </summary>
    private static DiagnosticsConfig ReadConfig(IConfiguration configuration)
    {
        var section = configuration.GetSection(DiagnosticsConfig.SectionName);
        var defaults = DiagnosticsConfig.Default;

        return new DiagnosticsConfig(
            MaxSpans: section.GetValue("MaxSpans", defaults.MaxSpans),
            MaxLogs: section.GetValue("MaxLogs", defaults.MaxLogs),
            MaxTagValueLength: section.GetValue("MaxTagValueLength", defaults.MaxTagValueLength),
            MaxBufferBytes: section.GetValue("MaxBufferBytes", defaults.MaxBufferBytes),
            MaxItemsPerPoll: section.GetValue("MaxItemsPerPoll", defaults.MaxItemsPerPoll),
            MetricExportIntervalMs: section.GetValue("MetricExportIntervalMs", defaults.MetricExportIntervalMs));
    }
}
