using Agent42.Agents;

namespace Agent42.Tests;

/// <summary>
/// The <see cref="ICalendarService"/> the test silos run on (ADR 0053). It delegates to the real
/// <see cref="InMemoryCalendarService"/> — so create → list → get → update → delete really round-trips
/// through the tools — and adds one thing the in-memory service deliberately does not have: a way to
/// exercise the <b>not connected</b> path.
///
/// <para><b>The switch is the user id, not mutable state.</b> A user whose id starts with
/// <see cref="NotConnectedUserPrefix"/> gets <see cref="CalendarOutcome.NotConnected"/> from every
/// method. That keeps the double stateless and therefore safe under xUnit's parallel test classes,
/// where a settable flag on a singleton registered in a shared silo would be a race — and it makes each
/// test say in its own grain key which world it is in.</para>
/// </summary>
public sealed class FakeCalendarService : ICalendarService
{
    /// <summary>A grain whose user id starts with this sees a calendar that is not connected.</summary>
    public const string NotConnectedUserPrefix = "no-calendar";

    private readonly InMemoryCalendarService _inner = new();

    private static bool NotConnected(string userId) =>
        userId.StartsWith(NotConnectedUserPrefix, StringComparison.Ordinal);

    public Task<CalendarResult<IReadOnlyList<CalendarSummary>>> ListCalendarsAsync(
        string userId, CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<IReadOnlyList<CalendarSummary>>.NotConnected())
            : _inner.ListCalendarsAsync(userId, cancellationToken);

    public Task<CalendarResult<IReadOnlyList<CalendarEvent>>> ListEventsAsync(
        string userId, string? calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax,
        int maxResults, CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<IReadOnlyList<CalendarEvent>>.NotConnected())
            : _inner.ListEventsAsync(userId, calendarId, timeMin, timeMax, maxResults, cancellationToken);

    public Task<CalendarResult<CalendarEvent>> GetEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<CalendarEvent>.NotConnected())
            : _inner.GetEventAsync(userId, calendarId, eventId, cancellationToken);

    public Task<CalendarResult<CalendarEvent>> CreateEventAsync(
        string userId, string? calendarId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<CalendarEvent>.NotConnected())
            : _inner.CreateEventAsync(userId, calendarId, draft, cancellationToken);

    public Task<CalendarResult<CalendarEvent>> UpdateEventAsync(
        string userId, string? calendarId, string eventId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<CalendarEvent>.NotConnected())
            : _inner.UpdateEventAsync(userId, calendarId, eventId, draft, cancellationToken);

    public Task<CalendarResult<bool>> DeleteEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default) =>
        NotConnected(userId)
            ? Task.FromResult(CalendarResult<bool>.NotConnected())
            : _inner.DeleteEventAsync(userId, calendarId, eventId, cancellationToken);
}
