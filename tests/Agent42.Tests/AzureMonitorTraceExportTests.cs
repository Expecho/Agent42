using System.Diagnostics;
using System.Threading.Channels;
using Agent42.Agents;
using Agent42.Backend;
using Azure.Monitor.OpenTelemetry.Exporter;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Agent42.Tests;

/// <summary>
/// The trace fan-out that sends spans to Application Insights with chat content stripped, while the
/// OTLP collectors keep the whole span. The strip mutates the shared <see cref="Activity"/>, so these
/// tests pin the parts that make that safe: every exporter sees every span, content is gone only from
/// the App Insights copy, and the strip is an exact-key allowlist that fails closed.
/// </summary>
[Collection("LiveTelemetry")]
public sealed class AzureMonitorTraceExportTests
{
    private const string ServiceName = "agent42-fanout-test";
    private const string TestUserId = "test-user-oid-42";

    [Fact]
    public async Task StripsChatContent_OtlpKeepsIt()
    {
        await using var loopback = await TelemetryLoopback.StartAsync();
        var sourceName = "Agent42.Tests.FanOut." + Guid.NewGuid().ToString("N");
        var otlp = new CapturingExporter();

        using (var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(sourceName)
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .AddProcessor(new TagOnStartProcessor("user.id", TestUserId))
            .AddProcessor(new TraceExportFanOutProcessor(
                [otlp], NewAzureMonitorExporter(loopback), Allowlist(), ExportProcessorType.Simple))
            .Build())
        {
            using var client = new SentinelChatClient()
                .AsBuilder()
                .UseOpenTelemetry(sourceName: sourceName, configure: telemetry => telemetry.EnableSensitiveData = true)
                .Build();

            await client.GetResponseAsync(SentinelChatClient.Messages, SentinelChatClient.Options);
        }

        var otlpSpan = Assert.Single(otlp.Spans);
        foreach (var sentinel in SentinelChatClient.Sentinels)
        {
            Assert.Contains(sentinel, otlpSpan.Text, StringComparison.Ordinal);
        }

        // Scoped to this test's own trace: the ingestion endpoint is private to the test, but the scoping
        // keeps the assertion honest if that ever changes.
        var appInsights = Assert.Single(loopback.AppInsightsItems, item => IsSpanItem(item) && item.Contains(otlpSpan.TraceId, StringComparison.Ordinal));
        foreach (var sentinel in SentinelChatClient.Sentinels)
        {
            Assert.DoesNotContain(sentinel, appInsights, StringComparison.Ordinal);
        }

        Assert.Contains("gen_ai.usage.input_tokens", appInsights, StringComparison.Ordinal);
        Assert.Contains("gen_ai.request.model", appInsights, StringComparison.Ordinal);
        Assert.Contains(TestUserId, appInsights, StringComparison.Ordinal);
    }

    [Fact]
    public void Strip_IsExactKeyAllowlist_OnGenAiSpansOnly()
    {
        var allowlist = Allowlist();

        using var genAi = new Activity("chat fake-model");
        foreach (var key in allowlist)
        {
            genAi.SetTag(key, "kept");
        }

        // Explicitly not "gen_ai.operation.name" = "kept": that tag is what marks a gen_ai span.
        genAi.SetTag(AzureMonitorTraceExport.GenAiOperationNameTag, "chat");
        string[] dropped =
        [
            "gen_ai.foo",
            "EndUserId",
            "gen_ai.request.stop_sequences",
            "gen_ai.tool.description",
            "gen_ai.agent.description",
            "gen_ai.input.messages",
            "gen_ai.system_instructions",
        ];
        foreach (var key in dropped)
        {
            genAi.SetTag(key, "content");
        }

        using var http = new Activity("GET /api/conversations");
        http.SetTag("url.full", "https://example.test/?q=secret");
        http.SetTag("gen_ai.input.messages", "not a gen_ai span, so untouched");

        AzureMonitorTraceExport.StripContent(genAi, allowlist);
        AzureMonitorTraceExport.StripContent(http, allowlist);

        Assert.Equal(allowlist.Order(StringComparer.Ordinal), genAi.TagObjects.Select(t => t.Key).Order(StringComparer.Ordinal));
        Assert.Equal(["url.full", "gen_ai.input.messages"], http.TagObjects.Select(t => t.Key));
    }

    /// <summary>
    /// Driven through a real batch processor, because the failure it guards against only exists there: a
    /// <see cref="Batch{T}"/> that drains a circular buffer enumerates once, so a fan-out that re-enumerated
    /// it would hand every exporter after the first an empty batch and still report success. The provider
    /// also has to reach the inner exporters, or App Insights loses <c>ai.cloud.role</c> and OTLP loses
    /// <c>service.name</c>.
    /// </summary>
    [Fact]
    public async Task FanOut_DeliversEverySpanToEveryExporter_WithResource()
    {
        const int SpanCount = 5;
        await using var loopback = await TelemetryLoopback.StartAsync();
        using var source = new ActivitySource("Agent42.Tests.FanOut." + Guid.NewGuid().ToString("N"));
        var first = new CapturingExporter();
        var second = new CapturingExporter();

        using var provider = Sdk.CreateTracerProviderBuilder()
            .AddSource(source.Name)
            .ConfigureResource(resource => resource.AddService(ServiceName))
            .AddProcessor(new TraceExportFanOutProcessor([first, second], NewAzureMonitorExporter(loopback), Allowlist()))
            .Build();

        for (var i = 0; i < SpanCount; i++)
        {
            source.StartActivity("span-" + i)!.Dispose();
        }

        Assert.True(provider.ForceFlush());

        Assert.Equal(SpanCount, first.Spans.Count);
        Assert.Equal(SpanCount, second.Spans.Count);
        Assert.Equal(ServiceName, first.ServiceName);
        Assert.Equal(ServiceName, second.ServiceName);

        var items = loopback.AppInsightsItems.Where(item => item.Contains("span-", StringComparison.Ordinal)).ToArray();
        Assert.Equal(SpanCount, items.Length);
        Assert.All(items, item => Assert.Contains($"\"ai.cloud.role\":\"{ServiceName}\"", item, StringComparison.Ordinal));
    }

    /// <summary>
    /// The ordering guard. With a Simple fan-out the strip runs synchronously inside the fan-out's own
    /// <c>OnEnd</c>, so if the composition ever registered it before the live console's processor, the
    /// console would capture the stripped span every time, not just when a batch worker won a race.
    /// </summary>
    [Fact]
    public async Task FanOutRegisteredLast_LiveConsoleKeepsContent()
    {
        await using var loopback = await TelemetryLoopback.StartAsync();
        using var host = BuildBackendHost(
            new() { [AzureMonitorTraceExport.ConnectionStringKey] = loopback.ConnectionString },
            ExportProcessorType.Simple);
        var provider = host.Services.GetRequiredService<TracerProvider>();

        using (var client = new SentinelChatClient()
            .AsBuilder()
            .UseOpenTelemetry(
                sourceName: LiveTelemetryCaptureExtensions.OtelSourceName,
                configure: telemetry => telemetry.EnableSensitiveData = true)
            .Build())
        {
            await client.GetResponseAsync(SentinelChatClient.Messages, SentinelChatClient.Options);
        }

        provider.ForceFlush();

        var captured = host.Services.GetRequiredService<LiveTelemetryBuffer>().SpansSince(0, null).Spans
            .Single(span => span.Source == LiveTelemetryCaptureExtensions.OtelSourceName
                && span.Tags.Any(tag => tag.Value.Contains(SentinelChatClient.Prompt, StringComparison.Ordinal)));
        Assert.Contains(captured.Tags, tag => tag.Name == "gen_ai.input.messages");

        var appInsights = Assert.Single(loopback.AppInsightsItems, item => IsSpanItem(item) && item.Contains(captured.TraceId, StringComparison.Ordinal));
        Assert.DoesNotContain(SentinelChatClient.Prompt, appInsights, StringComparison.Ordinal);
        Assert.Contains("gen_ai.usage.input_tokens", appInsights, StringComparison.Ordinal);
    }

    // A metric item can carry the trace id as an exemplar when the periodic metric export fires mid-test, so match the span item only.
    private static bool IsSpanItem(string item) => !item.Contains("\"name\":\"Metric\"", StringComparison.Ordinal);

    /// <summary>
    /// Disposing the host with a span still queued in the batch delivers it to both sinks: the export
    /// processor is shut down, and drains, before the inner exporters are.
    /// </summary>
    [Fact]
    public async Task Shutdown_DrainsToBothSinks()
    {
        await using var loopback = await TelemetryLoopback.StartAsync();
        var sourceName = "Agent42.Tests.FanOut." + Guid.NewGuid().ToString("N");
        var host = BuildBackendHost(
            new()
            {
                [AzureMonitorTraceExport.ConnectionStringKey] = loopback.ConnectionString,
                ["OTEL_EXPORTER_OTLP_ENDPOINT"] = loopback.BaseAddress.ToString(),
                ["OTEL_EXPORTER_OTLP_PROTOCOL"] = "http/protobuf",
            },
            sourceName: sourceName);

        using (host)
        {
            host.Services.GetRequiredService<TracerProvider>();
            using var source = new ActivitySource(sourceName);
            source.StartActivity("queued-at-shutdown")!.Dispose();
        }

        Assert.Contains(loopback.OtlpTraceBodies, body => body.Contains("queued-at-shutdown", StringComparison.Ordinal));
        Assert.Contains(loopback.AppInsightsItems, item => item.Contains("queued-at-shutdown", StringComparison.Ordinal));
    }

    /// <summary>
    /// A host shaped like the Backend's trace pipeline: ServiceDefaults' <c>ConfigureOpenTelemetry</c>, then
    /// the Backend composition method, exactly as <c>Program.cs</c> calls them. Offline storage is off so a
    /// failed test leaves no retry files behind.
    /// </summary>
    internal static IHost BuildBackendHost(
        Dictionary<string, string?> config,
        ExportProcessorType fanOutProcessorType = ExportProcessorType.Batch,
        string? sourceName = null,
        string? meterName = null)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration.AddInMemoryCollection(config);
        builder.Services.Configure<AzureMonitorExporterOptions>(options => options.DisableOfflineStorage = true);
        builder.ConfigureOpenTelemetry();
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing.AddSource(sourceName ?? LiveTelemetryCaptureExtensions.OtelSourceName))
            .WithMetrics(metrics => metrics.AddMeter(meterName ?? LiveTelemetryCaptureExtensions.OtelSourceName));
        builder.AddBackendTelemetry(Channel.CreateUnbounded<UsageEvent>().Writer, fanOutProcessorType);
        return builder.Build();
    }

    internal static IReadOnlySet<string> Allowlist() =>
        AzureMonitorTraceExport.GenAiMetadataKeys.Append("user.id").ToHashSet(StringComparer.Ordinal);

    internal static AzureMonitorTraceExporter NewAzureMonitorExporter(TelemetryLoopback loopback) =>
        new(new AzureMonitorExporterOptions
        {
            ConnectionString = loopback.ConnectionString,
            DisableOfflineStorage = true,
            EnableLiveMetrics = false,
        });

    /// <summary>
    /// Stands in for an OTLP exporter: it snapshots each span AT EXPORT TIME, which is when a real OTLP
    /// exporter serialises it, so a strip that ran too early shows up here as missing content.
    /// </summary>
    internal sealed class CapturingExporter : BaseExporter<Activity>
    {
        private readonly List<ExportedSpan> spans = [];

        public IReadOnlyList<ExportedSpan> Spans
        {
            get
            {
                lock (spans)
                {
                    return [.. spans];
                }
            }
        }

        public string? ServiceName { get; private set; }

        public override ExportResult Export(in Batch<Activity> batch)
        {
            ServiceName = ParentProvider?.GetResource().Attributes
                .FirstOrDefault(attribute => attribute.Key == "service.name").Value as string;

            lock (spans)
            {
                foreach (var activity in batch)
                {
                    var tags = string.Join('\n', activity.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
                    spans.Add(new ExportedSpan(activity.TraceId.ToHexString(), activity.DisplayName, tags));
                }
            }

            return ExportResult.Success;
        }
    }

    internal sealed record ExportedSpan(string TraceId, string Name, string Text);

    /// <summary>Stamps one tag at start, the way <c>UserTelemetrySpanProcessor</c> stamps <c>user.*</c>.</summary>
    internal sealed class TagOnStartProcessor(string key, string value) : BaseProcessor<Activity>
    {
        public override void OnStart(Activity data) => data.SetTag(key, value);
    }

    /// <summary>
    /// A chat client whose request and response put a distinct sentinel in every place content can reach a
    /// gen_ai span: prompt, reply, system instructions, tool definitions, tool-call arguments, tool result,
    /// stop sequences and an un-prefixed <c>AdditionalProperties</c> key.
    /// </summary>
    internal sealed class SentinelChatClient : IChatClient
    {
        public const string Prompt = "SENTINEL_PROMPT_7f3a";
        public const string Reply = "SENTINEL_REPLY_7f3a";
        public const string Instructions = "SENTINEL_INSTRUCTIONS_7f3a";
        public const string ToolDescription = "SENTINEL_TOOLDESC_7f3a";
        public const string ToolArguments = "SENTINEL_TOOLARGS_7f3a";
        public const string ToolResult = "SENTINEL_TOOLRESULT_7f3a";
        public const string StopSequence = "SENTINEL_STOP_7f3a";
        public const string AdditionalProperty = "SENTINEL_EXTRA_7f3a";

        public static readonly string[] Sentinels =
            [Prompt, Reply, Instructions, ToolDescription, ToolArguments, ToolResult, StopSequence, AdditionalProperty];

        public static readonly ChatMessage[] Messages =
        [
            new(ChatRole.User, Prompt),
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "lookup", new Dictionary<string, object?> { ["q"] = ToolArguments })]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", ToolResult)]),
        ];

        public static ChatOptions Options => new()
        {
            Instructions = Instructions,
            StopSequences = [StopSequence],
            Tools = [AIFunctionFactory.Create(() => "unused", "lookup", ToolDescription)],
            AdditionalProperties = new AdditionalPropertiesDictionary { ["sentinel_extra"] = AdditionalProperty },
        };

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Reply))
            {
                ModelId = "fake-model",
                Usage = new UsageDetails { InputTokenCount = 7, OutputTokenCount = 3 },
            });

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("This client only serves the non-streaming path.");

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceKey is null && serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("fake-provider", defaultModelId: "fake-model")
                : null;

        public void Dispose()
        {
        }
    }
}
