namespace Agent42.Agents;

/// <summary>
/// The budget the six calendar tools' <b>return values</b> must fit in — the same discipline, and
/// for the same reason, as <see cref="WebToolLimits"/> and <see cref="GeoToolLimits"/>: a tool's
/// <c>FunctionResultContent</c> is <b>persisted</b> into the session's single <c>agent-state</c>
/// Cosmos item, hard-capped at 2 MB (ADR 0004), and re-sent to the model every later turn.
///
/// A raw Calendar v3 <c>events.list</c> page is large — up to 250 events, each with organizer,
/// creator, every attendee, reminder overrides, conferencing data and HTML links. None of that is
/// returned. The tools hand back <see cref="CalendarEvent"/> records and the three free-form fields
/// on them are capped here.
///
/// A calendar is also the one place where an unbounded return is easy to trigger by accident: "list
/// everything this year" is a natural thing to ask, and it is exactly the request that would fill the
/// session item. <see cref="MaxEvents"/> binds it regardless of what window the model asks for.
/// </summary>
public static class CalendarToolLimits
{
    /// <summary>Maximum number of calendars returned by <c>list_calendars</c>.</summary>
    public const int MaxCalendars = 25;

    /// <summary>
    /// Maximum number of events returned by one <c>list_calendar_events</c> call. A model-supplied
    /// count is clamped to this — selection can only narrow it, never raise it.
    /// </summary>
    public const int MaxEvents = 25;

    /// <summary>Maximum length of an event summary or calendar name, in characters.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>
    /// Maximum length of an event description, in characters. The single largest free-form field on
    /// the resource, and the one an attacker-authored invitation would use to smuggle instructions.
    /// </summary>
    public const int MaxDescriptionLength = 500;

    /// <summary>Maximum length of an event location, in characters.</summary>
    public const int MaxLocationLength = WebToolLimits.MaxSnippetLength;

    /// <summary>
    /// Appended to a field that was cut off at its cap, so the model can tell "no location" from
    /// "a location too long to carry". Deliberately <b>not</b> <see cref="WebToolLimits.TruncationMarker"/>:
    /// that one leads with two newlines, which is right in the middle of a page of scraped text and
    /// wrong in the middle of a one-line event title.
    /// </summary>
    public const string TruncationMarker = "…";
}
