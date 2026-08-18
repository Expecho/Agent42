using System.Diagnostics;
using System.Threading.Channels;
using Agent42.Backend;
using Orleans.Runtime;
using Agent42.Agents.Telemetry;

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
