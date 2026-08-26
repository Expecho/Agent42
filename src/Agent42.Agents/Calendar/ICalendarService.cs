namespace Agent42.Agents;

/// <summary>
/// The user's real calendar (ADR 0053). Backs the six model-invoked calendar tools —
/// <c>list_calendars</c>, <c>list_calendar_events</c>, <c>get_calendar_event</c>,
/// <c>create_calendar_event</c>, <c>update_calendar_event</c>, <c>delete_calendar_event</c>.
///
/// Production talks to the Google Calendar REST API v3 (<c>GoogleCalendarService</c>); DevHost and
/// the tests use <see cref="InMemoryCalendarService"/> — the same real/in-memory split as
/// <see cref="IGeocoder"/>, <see cref="IWebSearch"/> and <see cref="IAttachmentStore"/>.
///
/// <para><b>Every method takes <c>userId</c> first, and no method takes a credential.</b>
/// <see cref="IGeocoder"/> is user-agnostic; a calendar is not. The implementation resolves,
/// decrypts and refreshes the user's OAuth tokens itself — <c>AgentGrain</c> reads its own grain key
/// for the user id and never sees a token. Do not add a credential to this port, to grain state, or
/// to a tool return value; <c>CalendarPort_ExposesNoCredential</c> pins that.</para>
///
/// <para><b>Implementations never throw into the middle of a turn.</b> Not connected, not
/// configured, a 4xx from Google, a transport failure — all of them come back as a
/// <see cref="CalendarResult{T}"/> carrying a curated <see cref="CalendarOutcome"/> and a constant
/// message. A provider's error body echoes account and event detail, so it never reaches the model
/// or the log (the ConfigHealth probe discipline).</para>
///
/// <para><b>The read side leaves the tenant.</b> Event titles, descriptions, locations, attendee
/// counts and shared-calendar ids (which are other people's email addresses) land in the transcript,
/// and from there they are subject to ADR 0013 egress, ADR 0027's followed-link exfiltration channel,
/// ADR 0035 share links and ADR 0032's unencrypted IndexedDB mirror. That is recorded in ADR 0053,
/// not hidden by this interface.</para>
/// </summary>
public interface ICalendarService
{
    /// <summary>Lists the calendars the user can see, capped at <see cref="CalendarToolLimits.MaxCalendars"/>.</summary>
    Task<CalendarResult<IReadOnlyList<CalendarSummary>>> ListCalendarsAsync(
        string userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists events in <paramref name="calendarId"/> between the two instants, capped at
    /// <see cref="CalendarToolLimits.MaxEvents"/>.
    ///
    /// <b>Recurrences are expanded</b> (Google's <c>singleEvents=true</c> + <c>orderBy=startTime</c>),
    /// which is what makes "what's on my calendar tomorrow" work — and which means every returned
    /// <see cref="CalendarEvent.Id"/> is an <i>instance</i> id. An update or delete against one
    /// therefore touches that single occurrence, never the series.
    /// </summary>
    Task<CalendarResult<IReadOnlyList<CalendarEvent>>> ListEventsAsync(
        string userId, string? calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax,
        int maxResults, CancellationToken cancellationToken = default);

    /// <summary>Reads one event by id.</summary>
    Task<CalendarResult<CalendarEvent>> GetEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Creates an event. No attendees are ever written (out of scope, ADR 0053).</summary>
    Task<CalendarResult<CalendarEvent>> CreateEventAsync(
        string userId, string? calendarId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Patches an event: every null member of <paramref name="draft"/> is left unchanged.
    /// Pins <c>sendUpdates=none</c> — a pre-existing event may already carry attendees, and a bare
    /// patch would email real people.
    /// </summary>
    Task<CalendarResult<CalendarEvent>> UpdateEventAsync(
        string userId, string? calendarId, string eventId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Deletes an event. Pins <c>sendUpdates=none</c>, for the same reason as the patch.</summary>
    Task<CalendarResult<bool>> DeleteEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default);
}

/// <summary>
/// How a calendar call ended. Anything that is not <see cref="Ok"/> carries a curated constant
/// message and no provider detail.
/// </summary>
public enum CalendarOutcome
{
    /// <summary>The call succeeded.</summary>
    Ok,

    /// <summary>
    /// No usable calendar connection. Deliberately <b>one</b> value covering both "the user has not
    /// connected their Google account" and "the deployment has no <c>GoogleCalendar:ClientId</c> /
    /// <c>ClientSecret</c>" — the user-facing remedy is identical (connect in Preferences → Calendar),
    /// so two values would only duplicate one message.
    /// </summary>
    NotConnected,

    /// <summary>The calendar or event does not exist, or the user cannot see it.</summary>
    NotFound,

    /// <summary>The provider refused or the call failed. The reason is deliberately not carried.</summary>
    Failed,
}

/// <summary>
/// The result of one calendar call: an outcome, a curated message, and the value when there is one.
/// </summary>
/// <typeparam name="T">The payload type of a successful call.</typeparam>
/// <param name="Outcome">How the call ended.</param>
/// <param name="Message">A curated constant — never a provider message, body or target URL.</param>
/// <param name="Value">The payload on <see cref="CalendarOutcome.Ok"/>; <c>default</c> otherwise.</param>
public sealed record CalendarResult<T>(CalendarOutcome Outcome, string Message, T? Value)
{
    /// <summary>A successful call carrying <paramref name="value"/>.</summary>
    public static CalendarResult<T> Success(T value) => new(CalendarOutcome.Ok, "", value);

    /// <summary>No usable connection — see <see cref="CalendarOutcome.NotConnected"/>.</summary>
    public static CalendarResult<T> NotConnected() =>
        new(CalendarOutcome.NotConnected, CalendarMessages.NotConnected, default);

    /// <summary>The calendar or event was not found.</summary>
    public static CalendarResult<T> NotFound() =>
        new(CalendarOutcome.NotFound, CalendarMessages.NotFound, default);

    /// <summary>The call failed; the provider's reason is deliberately discarded.</summary>
    public static CalendarResult<T> Failed() =>
        new(CalendarOutcome.Failed, CalendarMessages.Failed, default);
}

/// <summary>
/// The curated messages a failed calendar call may return. These are constants on purpose: a Google
/// API error body echoes the request URL, the calendar id (an email address) and event detail, and
/// none of that may reach the model, the transcript or the log.
/// </summary>
public static class CalendarMessages
{
    /// <summary>Covers both "user not connected" and "deployment not configured" — see <see cref="CalendarOutcome.NotConnected"/>.</summary>
    public const string NotConnected =
        "No Google Calendar connection is available. Ask the user to connect their Google account in Preferences and then Calendar.";

    /// <summary>The calendar or event does not exist or is not visible to this user.</summary>
    public const string NotFound = "That calendar or event could not be found.";

    /// <summary>Something went wrong upstream; deliberately says nothing about what.</summary>
    public const string Failed = "The calendar request could not be completed.";
}

/// <summary>
/// One calendar in the user's list.
/// </summary>
/// <param name="Id">
/// The calendar id, which for the primary calendar is the account's email address — this is what the
/// UI displays as the connected identity, and it is why no <c>openid</c>/<c>email</c> scope is needed.
/// For a <i>shared</i> calendar it is somebody else's email address.
/// </param>
/// <param name="Name">The calendar's display name, capped at <see cref="CalendarToolLimits.MaxTitleLength"/>.</param>
/// <param name="IsPrimary">Whether this is the user's primary calendar (the <c>primary</c> alias).</param>
/// <param name="TimeZone">The calendar's IANA time zone, when the provider reported one.</param>
public sealed record CalendarSummary(string Id, string Name, bool IsPrimary, string? TimeZone);

/// <summary>
/// One event, reduced to the fields worth persisting into the 2 MB session (ADR 0004) — not the
/// provider's whole resource. Deliberately carries no attendee <i>list</i>: the count is enough for
/// the model to say "you have three people on this", and the addresses are other people's data.
/// </summary>
/// <param name="Id">
/// The event id. After a listing this is an <b>instance</b> id (recurrences are expanded), so an
/// update or delete against it touches that occurrence only.
/// </param>
/// <param name="Title">The summary line, capped at <see cref="CalendarToolLimits.MaxTitleLength"/>.</param>
/// <param name="Description">The body, capped at <see cref="CalendarToolLimits.MaxDescriptionLength"/>.</param>
/// <param name="Location">The location, capped at <see cref="CalendarToolLimits.MaxLocationLength"/>.</param>
/// <param name="Start">Start as RFC 3339 (<c>2026-08-26T09:00:00+02:00</c>), or a bare date when all-day.</param>
/// <param name="End">End, in the same shape as <paramref name="Start"/>.</param>
/// <param name="AllDay">Whether this is an all-day event (Google's <c>date</c> rather than <c>dateTime</c>).</param>
/// <param name="AttendeeCount">How many attendees the event already has. Writing attendees is out of scope.</param>
public sealed record CalendarEvent(
    string Id, string Title, string? Description, string? Location,
    string Start, string End, bool AllDay, int AttendeeCount);

/// <summary>
/// What to write when creating or patching an event. On a patch every <c>null</c> member means
/// "leave this alone"; on a create, <see cref="Title"/>, <see cref="Start"/> and <see cref="End"/>
/// are required and the rest are optional.
/// </summary>
/// <param name="Title">The summary line.</param>
/// <param name="Description">The body.</param>
/// <param name="Location">A free-text location.</param>
/// <param name="Start">Start as RFC 3339, or a bare <c>yyyy-MM-dd</c> date when <paramref name="AllDay"/> is true.</param>
/// <param name="End">End, in the same shape as <paramref name="Start"/>.</param>
/// <param name="AllDay">Whether to write an all-day event.</param>
public sealed record CalendarEventDraft(
    string? Title = null, string? Description = null, string? Location = null,
    string? Start = null, string? End = null, bool? AllDay = null);
