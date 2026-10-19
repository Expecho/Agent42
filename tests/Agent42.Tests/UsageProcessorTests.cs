using System.Diagnostics;
using System.Threading.Channels;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Orleans.Runtime;

namespace Agent42.Tests;

/// <summary>
/// The Backend <see cref="UsageSpanProcessor"/> adapter (ADR 0031): its <c>OnEnd</c> projects a
/// finished <see cref="Activity"/> and enqueues the resulting <see cref="UsageEvent"/> onto the
/// ingestion channel, ignoring non-usage spans. The projection rules themselves are covered by
/// <see cref="UsageSpanProjectorTests"/>.
/// </summary>
public sealed class UsageProcessorTests
{
    private const string App = "Agent42";

    // AC11: the processor projects a finished chat Activity and enqueues the event onto the channel.
    [Fact]
    public void Processor_EnqueuesProjectedEvent()
    {
        var channel = Channel.CreateUnbounded<UsageEvent>();
        var processor = new UsageSpanProcessor(channel.Writer, App);

        var activity = new Activity("chat");
        activity.SetTag("gen_ai.operation.name", "chat");
        activity.SetTag("gen_ai.usage.input_tokens", 77);
        activity.SetTag("gen_ai.usage.output_tokens", 11);
        activity.SetTag("gen_ai.response.model", "gpt-5.4");

        processor.OnEnd(activity);

        Assert.True(channel.Reader.TryRead(out var e));
        Assert.Equal(UsageEventKind.Token, e!.Kind);
        Assert.Equal(77, e.InputTokens);
        Assert.Equal(11, e.OutputTokens);
        Assert.Equal("gpt-5.4", e.Model);
    }

    // AC9 (ADR 0104): the REAL M.E.AI OTel emitter tags cached and reasoning tokens when the provider reports them,
    // and the projector carries them into the event. ADR 0031's "always 0" was the spike provider's, not the emitter's.
    [Fact]
    public async Task ChatSpan_CarriesCachedAndReasoningTokens()
    {
        var source = "Agent42.Backend.usage-test-" + Guid.NewGuid().ToString("n");
        Activity? span = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == source,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => span = a,
        };
        ActivitySource.AddActivityListener(listener);

        var model = new RecordingChatClient
        {
            Reply = new ChatMessage(ChatRole.Assistant, "ok"),
        };
        using var client = new ChatClientBuilder(new UsageReportingChatClient(model))
            .UseOpenTelemetry(sourceName: source)
            .Build();
        await client.GetResponseAsync("hi");

        var channel = Channel.CreateUnbounded<UsageEvent>();
        new UsageSpanProcessor(channel.Writer, App).OnEnd(Assert.IsType<Activity>(span));

        Assert.True(channel.Reader.TryRead(out var e));
        Assert.Equal(100, e!.InputTokens);
        Assert.Equal(40, e.CachedInputTokens);
        Assert.Equal(7, e.ReasoningOutputTokens);
    }

    private sealed class UsageReportingChatClient(IChatClient inner)
        : DelegatingChatClient(inner)
    {
        public override async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            response.Usage = new UsageDetails
            {
                InputTokenCount = 100,
                OutputTokenCount = 20,
                CachedInputTokenCount = 40,
                ReasoningTokenCount = 7,
            };
            return response;
        }
    }

    // A non-usage span (no gen_ai.operation.name) enqueues nothing.
    [Fact]
    public void Processor_IgnoresNonUsageSpan()
    {
        var channel = Channel.CreateUnbounded<UsageEvent>();
        var processor = new UsageSpanProcessor(channel.Writer, App);

        processor.OnEnd(new Activity("GET /api/whatever"));

        Assert.False(channel.Reader.TryRead(out _));
    }

    // End-to-end per-agent application (ADR 0031): the grain publishes the agent name into RequestContext;
    // UserTelemetrySpanProcessor stamps it onto the span at OnStart; the usage processor projects it into
    // the event's Application. Proves the whole RequestContext → stamp → project chain.
    [Fact]
    public void RequestContextApplication_FlowsThroughToEvent()
    {
        RequestContext.Set(UsageSpanProjector.ApplicationContextKey, "WeatherAgent");
        try
        {
            var channel = Channel.CreateUnbounded<UsageEvent>();
            var usage = new UsageSpanProcessor(channel.Writer, App);

            var activity = new Activity("chat");
            new UserTelemetrySpanProcessor().OnStart(activity); // stamps agent42.application from RequestContext
            activity.SetTag("gen_ai.operation.name", "chat");
            activity.SetTag("gen_ai.usage.input_tokens", 10);
            activity.SetTag("gen_ai.usage.output_tokens", 2);

            usage.OnEnd(activity);

            Assert.True(channel.Reader.TryRead(out var e));
            Assert.Equal("WeatherAgent", e!.Application);
        }
        finally
        {
            RequestContext.Clear();
        }
    }
}
