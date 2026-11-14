using System.Collections.Frozen;
using System.Diagnostics;
using Agent42.Agents;
using OpenTelemetry;
using OpenTelemetry.Trace;

namespace Agent42.Backend;

/// <summary>
/// Removes prompt, completion, instruction and tool-I/O text from a gen_ai span unless the span belongs to an
/// owner (ADR 0121). The chat client runs with <c>EnableSensitiveData = true</c>, so without this every
/// signed-in stranger's conversation would sit in the OTLP collector and in the <c>/logs</c> ring that owners
/// read. The same exact-key allowlist the Application Insights fan-out uses decides what stays (metadata:
/// model, tokens, tool name, <c>user.*</c>), so a key a library upgrade adds is dropped until named.
///
/// <para><b>Registered BEFORE every exporter</b>, through <c>ConfigureOpenTelemetry</c>'s
/// <c>beforeTraceExporters</c> hook. Processors run in registration order and a batch exporter's worker may read a
/// span as soon as its <c>OnEnd</c> has run, so a redactor registered after the exporters would redact nothing that
/// was already exported. <c>TelemetryRedactionTests</c> proves it on the real composition (OTLP collector and live buffer), and pins the hook order as source text because no test builds <c>Program.cs</c>.</para>
///
/// <para><b>Fail closed.</b> A span with no <c>user.id</c> (reminder, gateway, email-in, fleet runs may carry no
/// <c>RequestContext</c>), and any span when no <see cref="IOwnerPolicy"/> is registered, is treated as a
/// non-owner's and redacted. "Has an id and it is not an owner" would leak exactly those.</para>
///
/// <para><b>Residual, stated not hidden.</b> This is span content only. <c>ILogger</c> records and http-client span
/// URLs can still carry model-chosen search queries and URLs; production logs are Warning-level, which bounds it. Span EVENTS are not stripped (an Activity cannot drop them); the chat client emits content as tags, which the existing App Insights test (`FanOutRegisteredLast_LiveConsoleKeepsContent`) would catch otherwise, but a library upgrade that moved content to events would bypass this.
/// And the owner tier is advisory (<see cref="IOwnerPolicy"/>): this stops accidental reads, not a determined user.</para>
/// </summary>
public sealed class NonOwnerContentRedactor : BaseProcessor<Activity>
{
    // The Application Insights allowlist, plus the usage rollup's provider-kind tag: UsageSpanProcessor reads it in
    // OnEnd, AFTER this processor, to price the call. Stripping it here would make every non-owner call unpriced.
    // None of these carries conversation content.
    private static readonly FrozenSet<string> Allowlist = AzureMonitorTraceExport.GenAiMetadataKeys
        .Concat(BackendTelemetryExtensions.AppInsightsAllowedTags)
        .Append(UsageSpanProjector.ProviderKindTag)
        .ToFrozenSet(StringComparer.Ordinal);

    private readonly IOwnerPolicy? _policy;

    /// <param name="policy">Who is an owner; <c>null</c> redacts every span.</param>
    public NonOwnerContentRedactor(IOwnerPolicy? policy) => _policy = policy;

    /// <summary>
    /// The <c>beforeTraceExporters</c> hook for <c>AddServiceDefaults</c>: registers the redactor ahead of every
    /// exporter. The policy is resolved when the tracer provider is built, because the hook runs before the host's own
    /// registrations.
    /// </summary>
    public static void Register(TracerProviderBuilder tracing)
        => tracing.AddProcessor(services => new NonOwnerContentRedactor(services.GetService<IOwnerPolicy>()));

    /// <inheritdoc />
    public override void OnEnd(Activity activity)
    {
        // Only gen_ai spans carry conversation content; StripContent also ignores any other span.
        if (activity.GetTagItem(AzureMonitorTraceExport.GenAiOperationNameTag) is null)
        {
            return;
        }

        var userId = activity.GetTagItem(UserTelemetryContext.UserIdTag) as string;
        if (_policy is not null && _policy.IsOwner(userId))
        {
            return;
        }

        AzureMonitorTraceExport.StripContent(activity, Allowlist);
    }
}
