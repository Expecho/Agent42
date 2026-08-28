using System.Diagnostics;
using Agent42.Backend;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;

namespace Agent42.Tests;

/// <summary>
/// The log half of the live console capture (ADR 0054).
///
/// <para><b><see cref="LogProcessor_DoesNotRetainThePooledRecord"/> is the point of this file.</b> The
/// OpenTelemetry SDK pools and reuses <c>LogRecord</c> instances, so a processor that stores the
/// reference and reads it later gets the <i>latest</i> message back for every buffered entry. It
/// compiles, it runs, nothing throws — the console just shows the same line N times. This test is
/// written to fail against that naive implementation, not merely to pass against the correct one, which
/// is why it drives real log calls through a real <c>LoggerFactory</c> rather than hand-constructing
/// records the SDK would never have pooled.</para>
/// </summary>
[Collection("LiveTelemetry")]
public sealed class LiveLogProcessorTests
{
    private static DiagnosticsConfig Config() => DiagnosticsConfig.Default;

    /// <summary>
    /// Builds the real OTel logging pipeline around a live buffer, exactly as
    /// <c>AddLiveTelemetryCapture</c> does — including <c>IncludeFormattedMessage</c> and
    /// <c>IncludeScopes</c>, without which <c>FormattedMessage</c> is null and scopes yield nothing.
    /// </summary>
    private static (ILogger Logger, LiveTelemetryBuffer Buffer, ILoggerFactory Factory) BuildPipeline(
        string category = "Test.Category")
    {
        var buffer = new LiveTelemetryBuffer(Config());
        var factory = LoggerFactory.Create(logging =>
        {
            logging.SetMinimumLevel(LogLevel.Trace);
            logging.AddOpenTelemetry(options =>
            {
                options.IncludeFormattedMessage = true;
                options.IncludeScopes = true;
                options.AddProcessor(new LiveLogProcessor(buffer));
            });
        });

        return (factory.CreateLogger(category), buffer, factory);
    }

    [Fact]
    public void LogProcessor_DoesNotRetainThePooledRecord()
    {
        var (logger, buffer, factory) = BuildPipeline();
        using (factory)
        {
            logger.LogWarning("first message");
            logger.LogWarning("second message");
        }

        var logs = buffer.LogsSince(0, null, null, "Trace").Logs;

        Assert.Equal(2, logs.Count);

        // The failure this catches reads back as ["second message", "second message"] — two entries, one
        // pooled record, every field overwritten by the later call.
        Assert.Equal(["first message", "second message"], logs.Select(l => l.Message));
    }

    [Fact]
    public void LogProcessor_CopiesAttributesAndException()
    {
        var (logger, buffer, factory) = BuildPipeline();
        using (factory)
        {
            using (logger.BeginScope("outer-scope"))
            {
                logger.LogError(new InvalidOperationException("boom"), "failed for {Widget}", "w-42");
            }

            // A second, plainer record: if the first entry's attributes/scopes were aliased into the
            // pooled buffer rather than copied, this call is what overwrites them.
            logger.LogInformation("unrelated");
        }

        var first = buffer.LogsSince(0, null, null, "Trace").Logs[0];

        Assert.Equal("Error", first.Level);
        Assert.Contains("w-42", first.Attributes.Select(a => a.Value));
        Assert.NotNull(first.Exception);
        Assert.Contains("boom", first.Exception);
        Assert.Contains("outer-scope", first.Scopes);
    }

    [Fact]
    public void LogProcessor_CapturesTraceCorrelationSoTheViewerCanJumpToTheTrace()
    {
        using var source = new ActivitySource("Agent42.Tests.Logs");
        using var listener = Listen(source.Name);

        var (logger, buffer, factory) = BuildPipeline();
        using (factory)
        {
            using var activity = source.StartActivity("work");
            logger.LogWarning("inside a trace");
        }

        var log = buffer.LogsSince(0, null, null, "Trace").Logs.Single();

        Assert.NotNull(log.TraceId);
        Assert.NotNull(log.SpanId);
    }

    [Fact]
    public void LogProcessor_DropsItsOwnDiagnosticsRecords()
    {
        var (logger, buffer, factory) = BuildPipeline("Microsoft.AspNetCore.Hosting.Diagnostics");
        using (factory)
        {
            // The exact shape of the hosting logger's request logging: the path rides as a named state
            // attribute. Every /logs poll produces two of these, which is why the console would
            // otherwise fill with itself.
            logger.LogInformation("Request finished {Method} {Path}", "GET", "/api/diagnostics/logs");

            // ...while the SAME category logging a genuine request must survive. This is why the filter
            // cannot be keyed off the category: it is shared by every request in the app.
            logger.LogInformation("Request finished {Method} {Path}", "POST", "/api/chat/abc");
        }

        var logs = buffer.LogsSince(0, null, null, "Trace").Logs;

        Assert.Single(logs);
        Assert.Contains("/api/chat/abc", logs[0].Message);
    }

    [Fact]
    public void LogProcessor_DropsRecordsWrittenUnderADiagnosticsActivity()
    {
        using var source = new ActivitySource("Agent42.Tests.Logs.Ambient");
        using var listener = Listen(source.Name);

        var (logger, buffer, factory) = BuildPipeline();
        using (factory)
        {
            // The second, independent route to the same fact: no path in the record's own state, but the
            // ambient server activity carries it. Needed because url.path is not set at every moment a
            // record may be written.
            using var activity = source.StartActivity("GET /api/diagnostics/traces");
            activity?.SetTag("url.path", "/api/diagnostics/traces");
            logger.LogInformation("some framework chatter");
        }

        Assert.Empty(buffer.LogsSince(0, null, null, "Trace").Logs);
    }

    /// <summary>Sampling listener, so <c>StartActivity</c> actually returns an activity in a test host.</summary>
    private static ActivityListener Listen(string sourceName)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == sourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}
