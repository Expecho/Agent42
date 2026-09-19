using System.Collections.Frozen;
using System.Diagnostics;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;

namespace Microsoft.Extensions.Hosting;

/// <summary>
/// Sends traces to Application Insights with chat content stripped, while every OTLP collector keeps the
/// full span (the ADR on App Insights export). Everything here is inert unless
/// <see cref="ConnectionStringKey"/> is set.
///
/// <para><b>Why a fan-out, and not a second batch processor:</b> Application Insights has no attribute
/// filter hook, so the content has to come off the <see cref="Activity"/> itself. Every processor exports
/// the SAME instances, each batch processor on its own worker thread, so an in-place strip is race-free only
/// when every other reader has already finished with the span on the same thread. So one exporter runs
/// the OTLP exporters, then strips the spans, then runs Azure Monitor, all in sequence.</para>
///
/// <para><b>Ordering:</b> <see cref="AddTraceExportFanOut{TBuilder}"/> must be the LAST trace processor
/// registered. A batch worker can export a span while the ending thread is still running <c>OnEnd</c> of
/// processors registered after it, and those would see the stripped copy. Registration order is pipeline
/// order only while nothing uses <c>UseOtlpExporter</c>, which sorts itself to the end.</para>
/// </summary>
public static class AzureMonitorTraceExport
{
    /// <summary>The connection string of the operator's own Application Insights resource.</summary>
    public const string ConnectionStringKey = "APPLICATIONINSIGHTS_CONNECTION_STRING";

    /// <summary>A span carrying this tag is a gen_ai span, and is the only kind that gets stripped.</summary>
    public const string GenAiOperationNameTag = "gen_ai.operation.name";

    private const string EnvEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
    private const string EnvHeadersKey = "OTEL_EXPORTER_OTLP_HEADERS";
    private const string EnvProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";
    private const string TracesPath = "v1/traces";

    /// <summary>
    /// The gen_ai metadata keys a gen_ai span may keep on its way to Application Insights. This is an
    /// EXACT-KEY allowlist, deliberately not a prefix rule: with sensitive data on, Microsoft.Extensions.AI
    /// copies every <c>AdditionalProperties</c> entry onto the span under its raw key, so neither a
    /// <c>gen_ai.</c> denylist nor a wildcard fails closed. A key a library upgrade adds is dropped until
    /// someone adds it here by name. Taken from the Microsoft.Extensions.AI 10.9.0 and
    /// Microsoft.Agents.AI 1.19.0 assemblies.
    ///
    /// <para>Left out on purpose: the message, instruction, tool-definition and tool-I/O content keys;
    /// <c>gen_ai.request.stop_sequences</c> (free text); and <c>gen_ai.tool.description</c> and
    /// <c>gen_ai.agent.description</c>, which users write themselves through uploaded skills and stored
    /// agents.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> GenAiMetadataKeys =
    [
        GenAiOperationNameTag,
        "gen_ai.provider.name",
        "gen_ai.conversation.id",
        "gen_ai.output.type",
        "gen_ai.token.type",
        "gen_ai.embeddings.dimension.count",
        "gen_ai.request.model",
        "gen_ai.request.choice.count",
        "gen_ai.request.frequency_penalty",
        "gen_ai.request.max_tokens",
        "gen_ai.request.presence_penalty",
        "gen_ai.request.seed",
        "gen_ai.request.stream",
        "gen_ai.request.temperature",
        "gen_ai.request.top_k",
        "gen_ai.request.top_p",
        "gen_ai.request.image.height",
        "gen_ai.request.image.width",
        "gen_ai.response.model",
        "gen_ai.response.id",
        "gen_ai.response.finish_reasons",
        "gen_ai.response.time_to_first_chunk",
        "gen_ai.usage.input_tokens",
        "gen_ai.usage.output_tokens",
        "gen_ai.usage.cache_read.input_tokens",
        "gen_ai.usage.reasoning.output_tokens",
        "gen_ai.usage.input_audio_tokens",
        "gen_ai.usage.input_text_tokens",
        "gen_ai.usage.output_audio_tokens",
        "gen_ai.usage.output_text_tokens",
        "gen_ai.tool.name",
        "gen_ai.tool.type",
        "gen_ai.tool.call.id",
        "gen_ai.agent.id",
        "gen_ai.agent.name",
        "gen_ai.realtime.output_modalities",
        "gen_ai.realtime.received_modalities",
        "gen_ai.realtime.session_kind",
        "gen_ai.realtime.voice",
        "server.address",
        "server.port",
        "error.type",
    ];

    /// <summary>
    /// Registers the operator's Azure Monitor settings once for every signal that exports there, as
    /// default-named options, so a test host can layer its own <c>Configure</c> on top.
    /// </summary>
    internal static void AddAzureMonitorOptions(IServiceCollection services, string connectionString) =>
        services.Configure<AzureMonitorExporterOptions>(options =>
        {
            options.ConnectionString = connectionString;

            // Live Metrics streams documents to Microsoft outside this pipeline, so they would skip the strip.
            options.EnableLiveMetrics = false;

            // Statsbeat is NOT switched off here: its option is internal in 1.9.0. It reports SDK health to
            // Microsoft-owned ingestion keys, egress outside the operator's boundary, so the container env
            // sets APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true, and so does every test that builds an exporter.

            // Only the trace exporter's own extension installs a sampler. The fan-out builds the exporter
            // directly and must keep every span, so the ratio stays at 1.
            options.SamplingRatio = 1.0F;
        });

    /// <summary>
    /// Registers the trace export fan-out: the configured OTLP exporters get every span unchanged, then
    /// gen_ai spans are stripped to <see cref="GenAiMetadataKeys"/> plus <paramref name="additionalAllowedKeys"/>,
    /// then Application Insights gets them. Does nothing when <see cref="ConnectionStringKey"/> is unset,
    /// because ServiceDefaults then registers its trace OTLP exporters itself.
    /// </summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="additionalAllowedKeys">The host's own metadata tags that gen_ai spans may keep, such as
    /// <c>user.*</c>. They are passed in because these shared defaults stay free of app types.</param>
    /// <param name="processorType"><see cref="ExportProcessorType.Batch"/> in production.
    /// <see cref="ExportProcessorType.Simple"/> strips synchronously inside <c>OnEnd</c>, which is what
    /// lets a test prove that nothing registered after the fan-out still reads the span.</param>
    public static TBuilder AddTraceExportFanOut<TBuilder>(
        this TBuilder builder,
        IEnumerable<string> additionalAllowedKeys,
        ExportProcessorType processorType = ExportProcessorType.Batch)
        where TBuilder : IHostApplicationBuilder
    {
        var connectionString = builder.Configuration[ConnectionStringKey];
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return builder;
        }

        var allowlist = GenAiMetadataKeys.Concat(additionalAllowedKeys).ToFrozenSet(StringComparer.Ordinal);
        var configuration = builder.Configuration;

        AddAzureMonitorOptions(builder.Services, connectionString);
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddProcessor(services =>
        {
            var azureMonitorOptions = services.GetRequiredService<IOptionsMonitor<AzureMonitorExporterOptions>>()
                .Get(Options.Options.DefaultName);
            BaseExporter<Activity>[] otlpExporters =
                [.. OtlpTraceTargets(configuration).Select(options => new OtlpTraceExporter(options))];

            return new TraceExportFanOutProcessor(
                otlpExporters, new AzureMonitorTraceExporter(azureMonitorOptions), allowlist, processorType);
        }));

        return builder;
    }

    /// <summary>
    /// Removes every tag outside <paramref name="allowlist"/> from a gen_ai span, in place. Any other span
    /// (HTTP server or client, Orleans) is left untouched.
    /// </summary>
    public static void StripContent(Activity activity, IReadOnlySet<string> allowlist)
    {
        if (activity.GetTagItem(GenAiOperationNameTag) is null)
        {
            return;
        }

        // Collected first: removing a tag while enumerating TagObjects is not supported.
        string[] dropped = [.. activity.TagObjects.Select(tag => tag.Key).Where(key => !allowlist.Contains(key))];
        foreach (var key in dropped)
        {
            activity.SetTag(key, null);
        }
    }

    /// <summary>
    /// The trace OTLP exporters ServiceDefaults would otherwise register, rebuilt by hand because the
    /// fan-out has to own them. Bound from <see cref="IConfiguration"/>, the source <c>AddOtlpExporter</c>
    /// reads too, so the gRPC pin in the container env still applies. <c>OTEL_BSP_*</c>, timeout and
    /// retry variables are NOT bound here; nothing in this repo sets them. Over <c>http/protobuf</c> the
    /// exporter uses a plain <c>HttpClient</c>, not the <c>IHttpClientFactory</c> client (and so not the
    /// ServiceDefaults resilience handler) that <c>AddOtlpExporter</c> would use; gRPC never used it.
    /// </summary>
    internal static IEnumerable<OtlpExporterOptions> OtlpTraceTargets(IConfiguration configuration)
    {
        var endpoint = configuration[EnvEndpointKey];
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            yield return EnvEndpointOptions(configuration, endpoint);
        }

        var localEndpoint = configuration[Extensions.LocalCollectorEndpointKey];
        if (!string.IsNullOrWhiteSpace(localEndpoint))
        {
            var options = new OtlpExporterOptions();
            Extensions.ConfigureLocalCollector(options, localEndpoint, configuration[Extensions.LocalCollectorHeadersKey]);
            yield return options;
        }
    }

    private static OtlpExporterOptions EnvEndpointOptions(IConfiguration configuration, string endpoint)
    {
        var options = new OtlpExporterOptions();
        switch (configuration[EnvProtocolKey])
        {
            case "grpc":
                options.Protocol = OtlpExportProtocol.Grpc;
                break;
            case "http/protobuf":
                options.Protocol = OtlpExportProtocol.HttpProtobuf;
                break;
        }

        // AddOtlpExporter appends the signal path to an endpoint that came from OTEL_EXPORTER_OTLP_ENDPOINT,
        // but not to one assigned in code. Over gRPC there is no path to append.
        options.Endpoint = options.Protocol == OtlpExportProtocol.HttpProtobuf
            ? AppendTracesPath(new Uri(endpoint))
            : new Uri(endpoint);

        var headers = configuration[EnvHeadersKey];
        if (!string.IsNullOrWhiteSpace(headers))
        {
            options.Headers = headers;
        }

        return options;
    }

    private static Uri AppendTracesPath(Uri endpoint)
    {
        var path = endpoint.AbsolutePath.TrimEnd('/');
        return path.EndsWith("/" + TracesPath, StringComparison.Ordinal)
            ? endpoint
            : new UriBuilder(endpoint) { Path = path + "/" + TracesPath }.Uri;
    }
}

/// <summary>
/// The one trace processor that feeds every exporter. It is a composite because the exporters it runs
/// by hand need the provider's resource (<c>service.name</c>, App Insights' <c>ai.cloud.role</c>), and the
/// provider only hands its resource to exporters that sit under a processor it knows about. So each inner
/// exporter gets a <see cref="SimpleActivityExportProcessor"/> child that never sees a span and exists to
/// receive the resource and to own that exporter's shutdown and disposal.
///
/// <para><c>OnEnd</c> goes to the export processor only. The children shut down in order, so the export
/// processor drains its last batch before any inner exporter is shut down.</para>
/// </summary>
public sealed class TraceExportFanOutProcessor : CompositeProcessor<Activity>
{
    private readonly BaseProcessor<Activity> exportProcessor;

    public TraceExportFanOutProcessor(
        IReadOnlyList<BaseExporter<Activity>> unfiltered,
        BaseExporter<Activity> filtered,
        IReadOnlySet<string> allowlist,
        ExportProcessorType processorType = ExportProcessorType.Batch)
        : this(CreateChildren(unfiltered, filtered, allowlist, processorType))
    {
    }

    private TraceExportFanOutProcessor(BaseProcessor<Activity>[] children)
        : base(children) => exportProcessor = children[0];

    public override void OnStart(Activity data)
    {
    }

    public override void OnEnd(Activity data) => exportProcessor.OnEnd(data);

    private static BaseProcessor<Activity>[] CreateChildren(
        IReadOnlyList<BaseExporter<Activity>> unfiltered,
        BaseExporter<Activity> filtered,
        IReadOnlySet<string> allowlist,
        ExportProcessorType processorType)
    {
        var fanOut = new TraceExportFanOutExporter(unfiltered, filtered, allowlist);
        BaseProcessor<Activity> export = processorType == ExportProcessorType.Simple
            ? new SimpleActivityExportProcessor(fanOut)
            : new BatchActivityExportProcessor(fanOut);

        return [export, .. unfiltered.Append(filtered).Select(exporter => new SimpleActivityExportProcessor(exporter))];
    }
}

/// <summary>
/// Exports a batch to every unfiltered exporter, strips it, then exports it to the filtered one. It never
/// shuts down or disposes the inner exporters: <see cref="TraceExportFanOutProcessor"/> owns them.
/// </summary>
public sealed class TraceExportFanOutExporter(
    IReadOnlyList<BaseExporter<Activity>> unfiltered,
    BaseExporter<Activity> filtered,
    IReadOnlySet<string> allowlist) : BaseExporter<Activity>
{
    public override ExportResult Export(in Batch<Activity> batch)
    {
        // A Batch drains a circular buffer, so it can be enumerated only once: a second pass yields nothing
        // even though Count is unchanged, and the exporter reports Success. Each exporter gets a fresh
        // batch over the same array.
        var activities = new Activity[batch.Count];
        var count = 0;
        foreach (var activity in batch)
        {
            activities[count++] = activity;
        }

        var result = ExportResult.Success;
        foreach (var exporter in unfiltered)
        {
            result = Worst(result, exporter.Export(new Batch<Activity>(activities, count)));
        }

        for (var i = 0; i < count; i++)
        {
            AzureMonitorTraceExport.StripContent(activities[i], allowlist);
        }

        return Worst(result, filtered.Export(new Batch<Activity>(activities, count)));
    }

    private static ExportResult Worst(ExportResult left, ExportResult right) =>
        left == ExportResult.Success ? right : left;
}
