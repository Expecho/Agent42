using OpenTelemetry;

namespace Agent42.Backend;

/// <summary>
/// Drops the outbound OpenTelemetry span for the Google Calendar client (ADR 0053).
///
/// <para><b>Why a whole handler for this.</b> The gotcha that applies here is the one
/// <see cref="GoogleGeocoder"/> records: OTel's HttpClient instrumentation redacts a span's <b>query
/// string</b> by default — it does <b>not</b> redact the path. Calendar REST v3 puts the calendar id
/// and the event id in the path: <c>/calendars/{calendarId}/events/{eventId}</c>. A calendar id is an
/// email address, and for a <i>shared</i> calendar it is somebody else's. ADR 0010 stamps
/// <c>user.email</c>/<c>user.full_name</c> onto every span started under a request identity, so an
/// unhandled span would join one named human to another named human's calendar, in the collector,
/// permanently.</para>
///
/// <para>Suppressing rather than rewriting is deliberate: a rewrite has to keep working as the URL
/// shapes grow (a new endpoint, a new id-bearing path segment), and the value of these spans — timing
/// for a third-party GET — does not justify defending that. What is lost is visible timing for calendar
/// calls; what is kept is that no personal identifier reaches the collector by accident.</para>
///
/// <para>This suppresses instrumentation for the <i>duration of the send</i> only, so it affects this
/// client and nothing else on the thread.</para>
/// </summary>
public sealed class CalendarSpanSuppressingHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var suppressed = SuppressInstrumentationScope.Begin();
        return await base.SendAsync(request, cancellationToken);
    }
}
