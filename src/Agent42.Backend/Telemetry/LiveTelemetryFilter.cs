using System.Diagnostics;
using OpenTelemetry.Logs;

namespace Agent42.Backend;

/// <summary>
/// Keeps the live console at <c>/logs</c> from watching itself (ADR 0054). The page polls
/// <c>/api/diagnostics/*</c> every couple of seconds, and each tick produces a server span <b>and</b>
/// request log lines — so without this the buffer fills with its own traffic and the signal the console
/// exists to show is evicted by the act of looking at it.
///
/// <para><b>The span half is easy; the log half is the one that gets written wrong.</b> The obvious
/// filter — drop the noisy log category — cannot work here: the noise is
/// <c>Microsoft.AspNetCore.Hosting.Diagnostics</c> "Request starting"/"Request finished", a <b>shared</b>
/// category every other request also uses, so filtering it would blind the console to all request
/// logging. The filter is therefore keyed off the <i>request path</i>, reached two independent ways
/// because neither alone is reliable at the moment a record is written:</para>
/// <list type="number">
/// <item>the ambient <see cref="Activity"/>'s <c>url.path</c> / <c>http.route</c> tag — present once the
/// ASP.NET Core instrumentation has enriched the server activity; and</item>
/// <item>the record's own <c>Path</c> / <c>RequestPath</c> state attribute — which the hosting logger
/// puts on both "Request starting" and "Request finished" regardless of instrumentation.</item>
/// </list>
///
/// <para>This matters most <b>offline</b>: <c>tools/Agent42.DevHost</c> has no <c>appsettings.json</c> at
/// all, so its effective minimum level is <c>Information</c> with no <c>Microsoft.AspNetCore</c>
/// override — a 2-second poll would flood the Logs tab in the one environment where the feature is
/// demonstrated.</para>
/// </summary>
public static class LiveTelemetryFilter
{
    /// <summary>
    /// The route prefix the console polls. Shared with <c>MapDiagnosticsEndpoints</c> so the filter and
    /// the endpoints can never drift apart — a renamed route that only half of them knew about would
    /// reinstate the self-observation flood silently.
    /// </summary>
    public const string DiagnosticsPathPrefix = "/api/diagnostics";

    /// <summary>
    /// The <b>only</b> activity tags consulted for a request path. This allowlist is the primary
    /// protection, not the string match below it: it is why a <c>gen_ai.tool.call.result</c> that quotes
    /// the diagnostics route — a fetched page, a tool result, the content a reader opened the console to
    /// see — is never even examined, let alone dropped.
    /// </summary>
    private static readonly string[] PathTags = ["url.path", "http.route", "http.target"];

    /// <summary>
    /// The log-state key the ASP.NET Core hosting logger carries the request path in.
    ///
    /// <para>Measured against this app rather than assumed. Both request messages expose it as
    /// <c>Path</c>: "Request starting" carries
    /// <c>Protocol, Method, ContentType, ContentLength, Scheme, Host, PathBase, <b>Path</b>, QueryString,
    /// {OriginalFormat}</c> and "Request finished" the same plus <c>ElapsedMilliseconds, StatusCode</c>.
    /// <b><c>RequestPath</c> is deliberately NOT here</b> — it exists only inside a formatted <i>scope</i>
    /// string (<c>"RequestPath:/ RequestId:…"</c>), which this filter does not read, so listing it would
    /// have been dead code that made the filter look more thorough than it is.</para>
    /// </summary>
    private const string PathAttributeKey = "Path";

    /// <summary>True when this activity is the console polling its own API.</summary>
    public static bool IsDiagnosticsActivity(Activity activity) =>
        PathTags.Any(tag => IsDiagnosticsPath(activity.GetTagItem(tag) as string))
        || IsDiagnosticsDisplayName(activity.DisplayName);

    /// <summary>
    /// True when this log record was written while serving a diagnostics poll. Checks the record's own
    /// state first (it does not depend on instrumentation having enriched the activity yet) and the
    /// ambient activity second.
    /// </summary>
    public static bool IsDiagnosticsRecord(LogRecord record)
    {
        if (record.Attributes is not null && record.Attributes.Any(IsDiagnosticsAttribute))
        {
            return true;
        }

        return Activity.Current is { } current && IsDiagnosticsActivity(current);
    }

    private static bool IsDiagnosticsAttribute(KeyValuePair<string, object?> attribute) =>
        string.Equals(attribute.Key, PathAttributeKey, StringComparison.Ordinal)
        && IsDiagnosticsPath(attribute.Value as string);

    /// <summary>
    /// A <b>prefix</b> match, never a match-anywhere. A value that merely mentions the route must not be
    /// treated as being the route. Only ever applied to the allowlisted keys above, both of which hold a
    /// path and nothing else.
    /// </summary>
    private static bool IsDiagnosticsPath(string? value) =>
        value is not null && value.StartsWith(DiagnosticsPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The looser <c>"{method} {route}"</c> form, kept apart from <see cref="IsDiagnosticsPath"/> on
    /// purpose: a display name is framework-generated and never carries model or web content, so a
    /// space-delimited match is safe here and would not be safe on a tag value.
    /// </summary>
    private static bool IsDiagnosticsDisplayName(string displayName) =>
        IsDiagnosticsPath(displayName)
        || displayName.Contains(" " + DiagnosticsPathPrefix, StringComparison.OrdinalIgnoreCase);
}
