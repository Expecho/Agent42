using Agent42.Backend;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.AI;
using OpenTelemetry;
using OpenTelemetry.Metrics;

namespace Agent42.Tests;

/// <summary>
/// The chat client's gen_ai metrics ride a meter named after the <c>sourceName</c> passed to
/// <c>UseOpenTelemetry</c> — <b>not</b> after the Microsoft.Extensions.AI default — and a
/// <c>MeterProvider</c> collects only the meters it was told about by name. Those two facts together are
/// what make <c>AddMeter(OtelSourceName)</c> in the Backend's <c>Program.cs</c> the correct registration
/// and <c>AddMeter("Microsoft.Extensions.AI")</c> the wrong one.
///
/// <para><b>Why this is worth a test rather than a comment:</b> getting it wrong is completely silent.
/// The instruments are still created, every measurement is still recorded, and the SDK simply never
/// subscribes — no exception, no warning, just a permanently empty token-usage series. That is exactly
/// how the app shipped before this file existed. A Microsoft.Extensions.AI bump that moved the meter onto
/// a fixed name would reintroduce it just as quietly, so the naming rule is asserted against the real
/// library rather than trusted.</para>
///
/// <para>Both directions are asserted on purpose. The positive alone would still pass if the library
/// published the instruments on <i>both</i> names, which would make the negative case in
/// <see cref="ChatClientMetrics_AreNotOnTheLibraryDefaultMeter"/> a false reassurance.</para>
///
/// <para>Nothing here exercises the Agent Framework agent layer, because there is nothing to exercise:
/// Microsoft.Agents.AI 1.19.0 creates no instruments at all (it is trace-only — see
/// microsoft/agent-framework#2688). Every gen_ai metric this app has comes from the
/// Microsoft.Extensions.AI chat client underneath it.</para>
/// </summary>
public sealed class MeterNamingTests
{
    /// <summary>Stands in for <c>LiveTelemetryCaptureExtensions.OtelSourceName</c>, kept distinct so a
    /// stray real-app meter cannot make the assertions pass by accident.</summary>
    private const string SourceName = "Agent42.Tests.MeterNaming";

    /// <summary>The name the library falls back to when no <c>sourceName</c> is supplied — the name it is
    /// tempting, and wrong, to register.</summary>
    private const string LibraryDefaultMeterName = "Microsoft.Extensions.AI";

    private const string TokenUsageMetric = "gen_ai.client.token.usage";
    private const string OperationDurationMetric = "gen_ai.client.operation.duration";

    [Fact]
    public async Task ChatClientMetrics_RideTheMeterNamedAfterTheSourceName()
    {
        var names = await CollectMetricNamesAsync(collectMeter: SourceName);

        Assert.Contains(TokenUsageMetric, names);
        Assert.Contains(OperationDurationMetric, names);
    }

    [Fact]
    public async Task ChatClientMetrics_AreNotOnTheLibraryDefaultMeter()
    {
        var names = await CollectMetricNamesAsync(collectMeter: LibraryDefaultMeterName);

        Assert.DoesNotContain(TokenUsageMetric, names);
        Assert.DoesNotContain(OperationDurationMetric, names);
    }

    /// <summary>
    /// The end-to-end version of the two tests above, run through the real registration rather than a
    /// hand-built provider: a host that calls <c>AddLiveTelemetryCapture</c> must actually land the chat
    /// client's token histogram in the live buffer.
    ///
    /// <para><b>Both <c>addInstrumentation</c> values are exercised, and that is the point of the
    /// theory.</b> <c>false</c> is the <i>Backend's</i> shape — the only host that ever talks to a real
    /// model, and the one whose metrics actually matter. Subscribing the meter inside the
    /// <c>addInstrumentation</c> branch, which reads as the natural home for it next to the matching
    /// <c>AddSource</c>, would leave that host exactly as broken as before while the <c>true</c> case kept
    /// passing. The <c>false</c> case is what makes the subscription's placement outside the gate
    /// falsifiable.</para>
    ///
    /// <para>Confirmed to fail against the version of the method with no <c>AddMeter</c> at all.</para>
    /// </summary>
    [Theory]
    [InlineData(false)] // the Backend: AddServiceDefaults() already brought the instrumentation
    [InlineData(true)]  // DevHost: no OpenTelemetry pipeline of its own
    public async Task CaptureWiring_CollectsTheChatClientTokenHistogram(bool addInstrumentation)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddLiveTelemetryCapture(addInstrumentation);

        using var host = builder.Build();
        var buffer = host.Services.GetRequiredService<LiveTelemetryBuffer>();

        // Resolving the MeterProvider is what starts it; without this the readers are never built and the
        // meter below is published into a world with no listener.
        var provider = host.Services.GetRequiredService<MeterProvider>();

        using (var client = new UsageReportingChatClient()
            .AsBuilder()
            .UseOpenTelemetry(sourceName: LiveTelemetryCaptureExtensions.OtelSourceName)
            .Build())
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        }

        provider.ForceFlush();

        Assert.Contains(TokenUsageMetric, buffer.Metrics.Metrics.Select(m => m.Name));
    }

    /// <summary>
    /// Drives one instrumented chat call and returns the metric names a <c>MeterProvider</c> subscribed to
    /// <paramref name="collectMeter"/> actually collected. The provider is built <b>before</b> the client so
    /// the meter is published into a listener that already exists.
    /// </summary>
    private static async Task<IReadOnlyList<string>> CollectMetricNamesAsync(string collectMeter)
    {
        var buffer = new LiveTelemetryBuffer(DiagnosticsConfig.Default);

        // Reuses the live console's own exporter (ADR 0054) rather than a bespoke one: it is already the
        // thing in this repo that turns exported Metrics into inspectable records.
        var provider = Sdk.CreateMeterProviderBuilder()
            .AddMeter(collectMeter)
            .AddReader(new PeriodicExportingMetricReader(new LiveMetricExporter(buffer)))
            .Build();

        using (provider)
        {
            using var client = new UsageReportingChatClient()
                .AsBuilder()
                .UseOpenTelemetry(sourceName: SourceName)
                .Build();

            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

            provider.ForceFlush();
        }

        return [.. buffer.Metrics.Metrics.Select(m => m.Name)];
    }

    /// <summary>
    /// Minimal <see cref="IChatClient"/> that reports token usage, which is what makes the token-usage
    /// histogram record a measurement at all — a response with no <see cref="UsageDetails"/> leaves that
    /// instrument untouched and the positive assertion would fail for the wrong reason.
    /// </summary>
    private sealed class UsageReportingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok"))
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
