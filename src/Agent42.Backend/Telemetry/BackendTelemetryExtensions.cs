using System.Threading.Channels;
using Agent42.Agents;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Agent42.Backend;

/// <summary>
/// The Backend's trace pipeline on top of <c>AddServiceDefaults()</c>, in the one order that is safe.
/// It lives here rather than inline in <c>Program.cs</c> because nothing in the suite runs
/// <c>Program.cs</c>, and the order is the part that has to be tested.
///
/// <para>The processors that read a span's content in <c>OnEnd</c>, <see cref="UsageSpanProcessor"/> and
/// the live console's <see cref="LiveSpanProcessor"/>, are registered first. The Application Insights
/// fan-out comes LAST: it strips chat content from the same <c>Activity</c> instances, and a batch worker
/// can export a span while the ending thread is still running the <c>OnEnd</c> of processors registered
/// after it.</para>
/// </summary>
public static class BackendTelemetryExtensions
{
    /// <summary>
    /// The application dimension of the usage dashboard (ADR 0031). Agent42 is one service and agent name
    /// is not on the span, so this is a constant; per-agent attribution is a future OTEL-native add.
    /// </summary>
    public const string UsageApplication = "Agent42";

    /// <summary>
    /// The Backend's own span tags a gen_ai span keeps on its way to Application Insights. ADR 0009
    /// accepts <c>user.*</c> in every telemetry sink; none of these carries conversation content.
    /// </summary>
    public static readonly IReadOnlyList<string> AppInsightsAllowedTags =
    [
        UserTelemetryContext.UserIdTag,
        UserTelemetryContext.UserNameTag,
        UserTelemetryContext.UserEmailTag,
        UserTelemetryContext.UserFullNameTag,
        UserTelemetryContext.UserRolesTag,
        UserTelemetryContext.UserIdpTag,
        UsageSpanProjector.ApplicationTag,
    ];

    /// <summary>
    /// Registers the Backend's span processors, the live telemetry console and, when a connection string
    /// is set, the Application Insights fan-out, in that order. Call it right after
    /// <c>AddServiceDefaults()</c>.
    /// </summary>
    /// <param name="builder">The host being built.</param>
    /// <param name="usageWriter">Where <see cref="UsageSpanProcessor"/> enqueues projected usage events.</param>
    /// <param name="fanOutProcessorType"><see cref="ExportProcessorType.Batch"/> in production. Tests pass
    /// <see cref="ExportProcessorType.Simple"/>, which makes a misordered fan-out fail deterministically.</param>
    public static TBuilder AddBackendTelemetry<TBuilder>(
        this TBuilder builder,
        ChannelWriter<UsageEvent> usageWriter,
        ExportProcessorType fanOutProcessorType = ExportProcessorType.Batch)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddOpenTelemetry()
            .WithTracing(tracing => tracing
                .AddSource(LiveTelemetryCaptureExtensions.OtelSourceName)
                // Carry the user.* identity to downstream/in-grain spans (ADR 0010): the processor stamps
                // every started span from the identity a request-scoped middleware writes into Orleans
                // RequestContext. Complements — does not replace — the server-span EnrichWithHttpRequest
                // in Program.cs, which owns the one span that starts before that middleware.
                .AddProcessor(new UserTelemetrySpanProcessor())
                // Project the gen_ai chat/execute_tool spans (already on OtelSourceName) into the usage
                // rollups (ADR 0031). OnEnd-only, non-blocking — enqueues to the usage channel.
                .AddProcessor(new UsageSpanProcessor(usageWriter, UsageApplication)));

        // The live telemetry console at /logs (ADR 0054). addInstrumentation stays FALSE here —
        // AddServiceDefaults() already registered the AspNetCore/Http/Runtime instrumentation, and
        // registering it twice risks duplicate spans. Capture is otherwise purely additive, with one
        // deliberate exception it owns for both hosts: the unconditional AddMeter(OtelSourceName) that
        // subscribes the chat client's gen_ai histograms, which reach every metric exporter from here.
        builder.AddLiveTelemetryCapture();

        // LAST, and only while nothing registers UseOtlpExporter, which sorts itself after every other
        // processor. A no-op without an Application Insights connection string.
        builder.AddTraceExportFanOut(AppInsightsAllowedTags, fanOutProcessorType);

        return builder;
    }
}
