using System.Diagnostics;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>
/// The span half of the live console capture (ADR 0054). The two properties that matter are that a trace
/// TREE is reconstructable from what lands in the buffer — spans arrive child-first, so the viewer can
/// never rely on arrival order — and that the console's own polling never reaches the buffer.
/// </summary>
public sealed class LiveSpanProcessorTests : IDisposable
{
    private const string SourceName = "Agent42.Tests.Spans";

    private readonly ActivitySource _source = new(SourceName);
    private readonly ActivityListener _listener;
    private readonly LiveTelemetryBuffer _buffer = new(DiagnosticsConfig.Default);
    private readonly LiveSpanProcessor _processor;

    public LiveSpanProcessorTests()
    {
        _processor = new LiveSpanProcessor(_buffer);
        _listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            // Drive the processor from the listener so the test exercises the real OnEnd timing rather
            // than calling the method by hand on a synthetic activity.
            ActivityStopped = activity => _processor.OnEnd(activity),
        };

        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose()
    {
        _listener.Dispose();
        _source.Dispose();
    }

    private IReadOnlyList<CapturedSpan> Captured() => _buffer.SpansSince(0, limit: null).Spans;

    [Fact]
    public void SpanProcessor_CapturesParentChildLinkage()
    {
        using (var parent = _source.StartActivity("parent"))
        {
            using var child = _source.StartActivity("child");
        }

        var spans = Captured();
        Assert.Equal(2, spans.Count);

        // Arrival order is CHILD FIRST: the child completes, and is therefore captured, before its
        // parent. A viewer that assumed arrival order would render the tree upside down.
        Assert.Equal(["child", "parent"], spans.Select(s => s.Name));

        var captured = spans.ToDictionary(s => s.Name);
        Assert.Null(captured["parent"].ParentSpanId);
        Assert.Equal(captured["parent"].SpanId, captured["child"].ParentSpanId);
        Assert.Equal(captured["parent"].TraceId, captured["child"].TraceId);
        Assert.Equal(SourceName, captured["parent"].Source);
    }

    [Fact]
    public void SpanProcessor_CapturesTagsAndEvents()
    {
        using (var activity = _source.StartActivity("chat"))
        {
            activity?.SetTag("gen_ai.request.model", "gpt-5.4");
            activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
            {
                ["exception.message"] = "boom",
            }));
            activity?.SetStatus(ActivityStatusCode.Error, "it failed");
        }

        var span = Captured().Single();

        Assert.Equal("Error", span.Status);
        Assert.Equal("it failed", span.StatusDescription);
        Assert.Contains(span.Tags, t => t is { Name: "gen_ai.request.model", Value: "gpt-5.4" });

        // Most exceptions reach a span as an EVENT. Without projecting events the status says "Error"
        // and the reason is gone — which is precisely when someone opens /logs.
        var evt = Assert.Single(span.Events);
        Assert.Equal("exception", evt.Name);
        Assert.Contains(evt.Tags, t => t is { Name: "exception.message", Value: "boom" });
    }

    [Theory]
    [InlineData("url.path", "/api/diagnostics/traces")]
    [InlineData("http.route", "/api/diagnostics/logs")]
    public void SpanProcessor_DropsItsOwnDiagnosticsSpans(string tag, string value)
    {
        // Named neutrally on purpose: if the display name also matched, this would pass without the tag
        // ever being read and would prove nothing about PathTags.
        using (var poll = _source.StartActivity("server-request"))
        {
            poll?.SetTag(tag, value);
        }

        Assert.Empty(Captured());
    }

    [Theory]
    // A tag OUTSIDE the consulted allowlist, whose value quotes the route — a fetched web page or a tool
    // result. This is the content someone opened the console to read.
    [InlineData("gen_ai.tool.call.result", "see /api/diagnostics/logs for details")]
    // ...and the same value at position 0, which is what makes this test non-vacuous: it would be dropped
    // by a naive `value.StartsWith(prefix)` applied to EVERY tag instead of to the allowlist.
    [InlineData("gen_ai.tool.call.result", "/api/diagnostics/logs returned 200")]
    // An allowlisted tag whose value merely MENTIONS the route rather than being it — dropped by a
    // match-anywhere implementation, kept by the prefix match that shipped.
    [InlineData("url.path", "/api/chat/see-also/api/diagnostics")]
    public void SpanProcessor_KeepsSpansThatMerelyMentionTheDiagnosticsRoute(string tag, string value)
    {
        // Two rules protect this, and the ALLOWLIST is the load-bearing one: only url.path / http.route /
        // http.target are ever consulted, and they are matched by PREFIX, never anywhere-in-the-string.
        using (var activity = _source.StartActivity("execute_tool"))
        {
            activity?.SetTag(tag, value);
        }

        Assert.Single(Captured());
    }

    [Fact]
    public void SpanProcessor_DropsADiagnosticsSpanIdentifiedOnlyByItsDisplayName()
    {
        // The looser "{method} {route}" match is confined to the DISPLAY NAME, which is
        // framework-generated and never carries model or web content — the reason it is safe there and
        // deliberately not applied to tag values.
        using (_source.StartActivity("GET /api/diagnostics/metrics"))
        {
        }

        Assert.Empty(Captured());
    }
}
