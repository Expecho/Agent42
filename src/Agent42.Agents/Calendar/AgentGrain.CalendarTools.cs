using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The six Google Calendar tools (ADR 0053): <c>list_calendars</c>, <c>list_calendar_events</c>,
/// <c>get_calendar_event</c>, <c>create_calendar_event</c>, <c>update_calendar_event</c> and
/// <c>delete_calendar_event</c>.
///
/// <para>Each method is deliberately <b>thin</b>: resolve the user id from the grain key, call the
/// port, serialize. Every branch that could grow — auth, refresh, error mapping, budgeting — lives in
/// the adapter, which keeps these methods well under the cognitive-complexity gate and keeps the
/// "credential never crosses the port" property (derived decision (a)) obvious at a glance.</para>
///
/// <para><b>Writes carry no confirmation gate.</b> The model creates, updates and deletes directly, at
/// the user's explicit request — and the accepted residual is that a prompt injection arriving through
/// a fetched page (ADR 0011/0027) or an uploaded skill (ADR 0049) can do the same. That is recorded in
/// ADR 0053 rather than mitigated here.</para>
///
/// <para><b>These tools are live on the asynchronous paths too</b> — <c>ReceiveReminder</c> and
/// <c>EnqueuePromptAsync</c> (<c>/api/agent-run</c>, ADR 0022) — because <c>BuildAgent</c> registers
/// them there as well. A model-invoked delete can therefore fire with no user present, from a
/// webhook-triggered run whose prompt came from outside the tenant.</para>
///
/// <para><b>An incognito chat (ADR 0034) may still write to the calendar, deliberately.</b> ADR 0034
/// blocks documents and refuses scheduling because both create durable records <i>inside our system</i>.
/// A calendar event is different in kind: the durable record is outside our system and is the user's own
/// explicit intent, exactly like a web search leaving the tenant. The ephemeral guarantee is "not
/// persisted by us, not listed", not "no side effects".
/// <c>EphemeralChat_MayStillWriteToTheCalendar</c> pins the non-refusal so it is not later "fixed" by
/// analogy with <c>SchedulePromptAsync</c>.</para>
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// The default window <c>list_calendar_events</c> uses when the model supplies no dates: from now
    /// to a week out, which is what "what's coming up" means in practice.
    /// </summary>
    private const int DefaultEventWindowDays = 7;

    /// <summary>
    /// Agent tool: lists the user's calendars. Exposed to the model as <c>list_calendars</c>.
    /// </summary>
    private async Task<string> ListCalendarsAsync(CancellationToken cancellationToken = default)
    {
        var result = await _calendar.ListCalendarsAsync(CalendarUserId(), cancellationToken);
        LogCalendarTool("list_calendars", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { calendars = result.Value });
    }

    /// <summary>
    /// Agent tool: lists events in a window. Exposed to the model as <c>list_calendar_events</c>.
    /// </summary>
    /// <param name="startDate">Inclusive start of the window, RFC 3339 or <c>yyyy-MM-dd</c>; defaults to now.</param>
    /// <param name="endDate">Exclusive end of the window; defaults to a week after the start.</param>
    /// <param name="calendarId">Which calendar; defaults to the user's primary one.</param>
    /// <param name="maxResults">How many events at most; clamped to the return budget.</param>
    private async Task<string> ListCalendarEventsAsync(
        [Description("Optional. Inclusive start of the window, RFC 3339 (2026-08-27T00:00:00+02:00) or yyyy-MM-dd. Defaults to now.")] string? startDate = null,
        [Description("Optional. Exclusive end of the window, same format. Defaults to a week after the start.")] string? endDate = null,
        [Description("Optional. Which calendar to read, as an id from list_calendars. Defaults to the user's primary calendar.")] string? calendarId = null,
        [Description("Optional. The most events to return. The server caps this regardless of what you ask for.")] int? maxResults = null,
        CancellationToken cancellationToken = default)
    {
        var from = ParseInstant(startDate) ?? DateTimeOffset.UtcNow;
        var to = ParseInstant(endDate) ?? from.AddDays(DefaultEventWindowDays);

        var result = await _calendar.ListEventsAsync(
            CalendarUserId(), calendarId, from, to,
            maxResults ?? CalendarToolLimits.MaxEvents, cancellationToken);

        LogCalendarTool("list_calendar_events", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { events = result.Value });
    }

    /// <summary>
    /// Agent tool: reads one event. Exposed to the model as <c>get_calendar_event</c>.
    /// </summary>
    /// <param name="eventId">The event's id, as returned by <c>list_calendar_events</c>.</param>
    /// <param name="calendarId">Which calendar; defaults to the user's primary one.</param>
    private async Task<string> GetCalendarEventAsync(
        [Description("The event's id, exactly as returned by list_calendar_events.")] string eventId,
        [Description("Optional. Which calendar the event is on. Defaults to the user's primary calendar.")] string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _calendar.GetEventAsync(CalendarUserId(), calendarId, eventId, cancellationToken);
        LogCalendarTool("get_calendar_event", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { calendarEvent = result.Value });
    }

    /// <summary>
    /// Agent tool: creates an event. Exposed to the model as <c>create_calendar_event</c>.
    /// </summary>
    /// <param name="title">The event's title.</param>
    /// <param name="start">Start, RFC 3339 (<c>2026-08-27T09:00:00+02:00</c>) or <c>yyyy-MM-dd</c> when all-day.</param>
    /// <param name="end">End, in the same shape as <paramref name="start"/>.</param>
    /// <param name="description">Optional body text.</param>
    /// <param name="location">Optional free-text location.</param>
    /// <param name="allDay">Whether this is an all-day event.</param>
    /// <param name="calendarId">Which calendar; defaults to the user's primary one.</param>
    private async Task<string> CreateCalendarEventAsync(
        [Description("The event's title, e.g. 'Lunch with Sam'.")] string title,
        [Description("Start time, RFC 3339 (2026-08-27T12:00:00+02:00), or a yyyy-MM-dd date when allDay is true.")] string start,
        [Description("End time, in the same format as start.")] string end,
        [Description("Optional. Body text for the event.")] string? description = null,
        [Description("Optional. Where the event is, as free text.")] string? location = null,
        [Description("Optional. True for an all-day event, in which case start and end are yyyy-MM-dd dates.")] bool? allDay = null,
        [Description("Optional. Which calendar to write to, as an id from list_calendars. Defaults to the user's primary calendar.")] string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _calendar.CreateEventAsync(
            CalendarUserId(), calendarId,
            new CalendarEventDraft(title, description, location, start, end, allDay),
            cancellationToken);

        LogCalendarTool("create_calendar_event", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { created = result.Value });
    }

    /// <summary>
    /// Agent tool: patches an event. Exposed to the model as <c>update_calendar_event</c>.
    /// Anything left out is left unchanged.
    /// </summary>
    /// <param name="eventId">The event's id, as returned by <c>list_calendar_events</c>.</param>
    /// <param name="title">New title, or null to leave it alone.</param>
    /// <param name="start">New start, or null to leave it alone.</param>
    /// <param name="end">New end, or null to leave it alone.</param>
    /// <param name="description">New body text, or null to leave it alone.</param>
    /// <param name="location">New location, or null to leave it alone.</param>
    /// <param name="allDay">Whether the event is all-day, or null to leave it alone.</param>
    /// <param name="calendarId">Which calendar; defaults to the user's primary one.</param>
    private async Task<string> UpdateCalendarEventAsync(
        [Description("The event's id, exactly as returned by list_calendar_events.")] string eventId,
        [Description("Optional. A new title. Leave this out to keep the current one.")] string? title = null,
        [Description("Optional. A new start time. Leave this out to keep the current one.")] string? start = null,
        [Description("Optional. A new end time. Leave this out to keep the current one.")] string? end = null,
        [Description("Optional. New body text. Leave this out to keep the current one.")] string? description = null,
        [Description("Optional. A new location. Leave this out to keep the current one.")] string? location = null,
        [Description("Optional. Whether the event is all-day. Leave this out to keep it as it is.")] bool? allDay = null,
        [Description("Optional. Which calendar the event is on. Defaults to the user's primary calendar.")] string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _calendar.UpdateEventAsync(
            CalendarUserId(), calendarId, eventId,
            new CalendarEventDraft(title, description, location, start, end, allDay),
            cancellationToken);

        LogCalendarTool("update_calendar_event", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { updated = result.Value });
    }

    /// <summary>
    /// Agent tool: deletes an event. Exposed to the model as <c>delete_calendar_event</c>.
    /// </summary>
    /// <param name="eventId">The event's id, as returned by <c>list_calendar_events</c>.</param>
    /// <param name="calendarId">Which calendar; defaults to the user's primary one.</param>
    private async Task<string> DeleteCalendarEventAsync(
        [Description("The event's id, exactly as returned by list_calendar_events.")] string eventId,
        [Description("Optional. Which calendar the event is on. Defaults to the user's primary calendar.")] string? calendarId = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _calendar.DeleteEventAsync(CalendarUserId(), calendarId, eventId, cancellationToken);
        LogCalendarTool("delete_calendar_event", result.Outcome);

        return result.Outcome != CalendarOutcome.Ok
            ? Notice(result.Message)
            : Serialize(new { deleted = true, eventId });
    }

    // The user id comes from the grain's OWN key (resolved into _userId on activation), never from the
    // model. A tool cannot be talked into reading somebody else's calendar, and it never sees a
    // credential at all — the adapter resolves and decrypts one behind the port.
    private string CalendarUserId() => _userId;

    // A tool result is persisted into the 2 MB session (ADR 0004), so both shapes below stay compact.
    private static string Serialize(object payload) => JsonSerializer.Serialize(payload);

    private static string Notice(string message) => JsonSerializer.Serialize(new { notice = message });

    // Accepts RFC 3339 and a bare yyyy-MM-dd (what a model produces for "tomorrow"), and returns null
    // for anything else so the caller falls back to its default rather than the turn failing.
    private static DateTimeOffset? ParseInstant(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : DateTimeOffset.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
                ? parsed
                : null;

    // Logs that a calendar tool ran and how it ended — never WHAT it found. Event titles, locations and
    // shared-calendar ids are personal data belonging to the user and to third parties, and this repo
    // stamps user identity onto every span (ADR 0010).
    private void LogCalendarTool(string tool, CalendarOutcome outcome) => _logger.LogInformation(
        "Agent grain {GrainKey} ran calendar tool {Tool} with outcome {Outcome}",
        this.GetPrimaryKeyString(), tool, outcome);
}
