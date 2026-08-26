using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="ICalendarService"/> over the Google Calendar REST API v3 (ADR 0053).
///
/// <para><b>The credential never crosses the port.</b> This class resolves the calling user's stored
/// tokens from <c>IUserGoogleGrain</c>, decrypts them with <see cref="IGoogleSecretProtector"/>, and
/// refreshes them near expiry — the <c>UserMcpToolProvider.ResolveOAuthAccessTokenAsync</c> shape,
/// including its skew and its persist-the-refreshed-bundle step. <c>AgentGrain</c> passes a user id
/// and gets records back.</para>
///
/// <para><b>One adapter, self-reporting.</b> There is deliberately no <c>NotConfiguredCalendarService</c>
/// twin of <see cref="CoordinatesOnlyGeocoder"/>: a blank <c>GoogleCalendar:ClientId</c>/<c>ClientSecret</c>
/// and an un-connected user produce the <i>same</i> curated result, so splitting them into two types
/// would only duplicate one message. This is the <c>ContextWebPageFetcher</c> shape — a blank key
/// degrades per call, it does not fail startup.</para>
///
/// <para><b>Nothing from Google reaches the model or the log.</b> An error body echoes the request URL,
/// the calendar id and event detail; the catch discards it and returns a constant (the ConfigHealth
/// probe discipline). The outbound span is suppressed for a related reason —
/// see <see cref="CalendarSpanSuppressingHandler"/>.</para>
///
/// <para>The <see cref="HttpClient"/> is a named client with an explicit timeout and <b>no</b>
/// resilience pipeline (see Program.cs): <c>AgentGrain</c> is non-reentrant, so a retried calendar call
/// multiplies the stall on the user's turn — and a retried <c>POST</c> could double-book them.</para>
/// </summary>
public sealed class GoogleCalendarService : ICalendarService
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "google-calendar";

    /// <summary>The API's base address. Config-pinned, never model-supplied.</summary>
    public const string DefaultBaseAddress = "https://www.googleapis.com/calendar/v3/";

    /// <summary>The alias Google accepts for the signed-in user's own calendar.</summary>
    public const string PrimaryCalendarId = "primary";

    /// <summary>
    /// Never notify anybody. Attendees are out of scope for <i>writing</i>, but a pre-existing event may
    /// already carry them and a bare patch or delete would email real people — the one irreversible
    /// outward-facing side effect in this feature, so it is pinned here and by a test.
    /// </summary>
    private const string NoNotifications = "sendUpdates=none";

    // Refresh this far before the access token actually expires, so a call does not race the clock.
    private static readonly TimeSpan RefreshSkew = TimeSpan.FromMinutes(2);

    private readonly HttpClient _http;
    private readonly IGrainFactory _grains;
    private readonly IGoogleSecretProtector _protector;
    private readonly GoogleOAuthClient _oauth;
    private readonly string _clientId;
    private readonly string _clientSecret;
    private readonly ILogger<GoogleCalendarService> _logger;

    public GoogleCalendarService(
        HttpClient http, IGrainFactory grains, IGoogleSecretProtector protector, GoogleOAuthClient oauth,
        string clientId, string clientSecret, ILogger<GoogleCalendarService> logger)
    {
        _http = http;
        _grains = grains;
        _protector = protector;
        _oauth = oauth;
        _clientId = clientId;
        _clientSecret = clientSecret;
        _logger = logger;
    }

    public async Task<CalendarResult<IReadOnlyList<CalendarSummary>>> ListCalendarsAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            userId, HttpMethod.Get,
            $"users/me/calendarList?maxResults={CalendarToolLimits.MaxCalendars}&minAccessRole=reader",
            body: null, cancellationToken);

        if (response.Outcome != CalendarOutcome.Ok)
        {
            return Carry<IReadOnlyList<CalendarSummary>>(response);
        }

        using var json = response.Value!;
        IReadOnlyList<CalendarSummary> calendars =
        [
            .. Items(json.RootElement).Select(ReadCalendar).Take(CalendarToolLimits.MaxCalendars),
        ];

        return CalendarResult<IReadOnlyList<CalendarSummary>>.Success(calendars);
    }

    public async Task<CalendarResult<IReadOnlyList<CalendarEvent>>> ListEventsAsync(
        string userId, string? calendarId, DateTimeOffset timeMin, DateTimeOffset timeMax,
        int maxResults, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(maxResults, 1, CalendarToolLimits.MaxEvents);

        // singleEvents + orderBy=startTime is what makes "what's on tomorrow" work: it expands a
        // recurring series into its occurrences. The consequence — every returned id is an INSTANCE id,
        // so a later edit or delete touches one occurrence, not the series — is stated on the port and
        // in the tool descriptions, because the model would otherwise claim it edited the series.
        var query = $"calendars/{Segment(calendarId)}/events"
            + $"?timeMin={Uri.EscapeDataString(Rfc3339(timeMin))}"
            + $"&timeMax={Uri.EscapeDataString(Rfc3339(timeMax))}"
            + $"&singleEvents=true&orderBy=startTime&maxResults={count}";

        var response = await SendAsync(userId, HttpMethod.Get, query, body: null, cancellationToken);
        if (response.Outcome != CalendarOutcome.Ok)
        {
            return Carry<IReadOnlyList<CalendarEvent>>(response);
        }

        using var json = response.Value!;
        IReadOnlyList<CalendarEvent> events = [.. Items(json.RootElement).Select(ReadEvent).Take(count)];

        return CalendarResult<IReadOnlyList<CalendarEvent>>.Success(events);
    }

    public async Task<CalendarResult<CalendarEvent>> GetEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            userId, HttpMethod.Get, EventPath(calendarId, eventId), body: null, cancellationToken);

        return SingleEvent(response);
    }

    public async Task<CalendarResult<CalendarEvent>> CreateEventAsync(
        string userId, string? calendarId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            userId, HttpMethod.Post, $"calendars/{Segment(calendarId)}/events?{NoNotifications}",
            WriteDraft(draft), cancellationToken);

        return SingleEvent(response);
    }

    public async Task<CalendarResult<CalendarEvent>> UpdateEventAsync(
        string userId, string? calendarId, string eventId, CalendarEventDraft draft,
        CancellationToken cancellationToken = default)
    {
        // PATCH, not PUT: a null member of the draft means "leave this alone", and a PUT would blank
        // every field the model did not mention.
        var response = await SendAsync(
            userId, HttpMethod.Patch, $"{EventPath(calendarId, eventId)}?{NoNotifications}",
            WriteDraft(draft), cancellationToken);

        return SingleEvent(response);
    }

    public async Task<CalendarResult<bool>> DeleteEventAsync(
        string userId, string? calendarId, string eventId, CancellationToken cancellationToken = default)
    {
        var response = await SendAsync(
            userId, HttpMethod.Delete, $"{EventPath(calendarId, eventId)}?{NoNotifications}",
            body: null, cancellationToken);

        response.Value?.Dispose();
        return response.Outcome == CalendarOutcome.Ok
            ? CalendarResult<bool>.Success(true)
            : new CalendarResult<bool>(response.Outcome, response.Message, false);
    }

    // ── the one place that talks to Google ──────────────────────────────────────────────────────────

    // The single HTTP path. Returns a JsonDocument on success (the caller disposes it) and a curated
    // outcome otherwise — no status text, no body, no URL ever escapes this method.
    private async Task<CalendarResult<JsonDocument>> SendAsync(
        string userId, HttpMethod method, string relativeUrl, string? body, CancellationToken cancellationToken)
    {
        var accessToken = await ResolveAccessTokenAsync(userId, cancellationToken);
        if (accessToken is null)
        {
            return CalendarResult<JsonDocument>.NotConnected();
        }

        try
        {
            using var request = new HttpRequestMessage(method, relativeUrl);
            request.Headers.Authorization = new("Bearer", accessToken);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            }

            using var response = await _http.SendAsync(request, cancellationToken);

            // 410 Gone as well as 404: Calendar v3 answers Gone for an event deleted since it was
            // listed, which is a realistic race on the list -> delete path (ids are instance ids). Both
            // mean "not there"; reporting Failed instead would have the model retry a doomed call.
            if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone)
            {
                return CalendarResult<JsonDocument>.NotFound();
            }

            if (!response.IsSuccessStatusCode)
            {
                // The status code only. A Google error body echoes the request URL, the calendar id
                // (an email address) and event detail — none of which may reach the log, and none of
                // which reaches the model either (the caller returns CalendarMessages.Failed).
                _logger.LogWarning(
                    "A Google Calendar request failed with HTTP {StatusCode}.", (int)response.StatusCode);
                return CalendarResult<JsonDocument>.Failed();
            }

            // A 204 (delete) has no body; anything else parses.
            var payload = await response.Content.ReadAsStringAsync(cancellationToken);
            return CalendarResult<JsonDocument>.Success(
                string.IsNullOrWhiteSpace(payload) ? JsonDocument.Parse("{}") : JsonDocument.Parse(payload));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // ex.Message can carry the request URI on some HttpRequestException paths, and the URI
            // carries the calendar id. Log the exception TYPE only — never the message.
            _logger.LogWarning("A Google Calendar request threw {ExceptionType}.", ex.GetType().Name);
            return CalendarResult<JsonDocument>.Failed();
        }
    }

    // Resolves the user's current access token, refreshing it when it is within the skew of expiry and
    // persisting the refreshed bundle. Returns null for BOTH "the deployment has no client credentials"
    // and "this user has not connected" — the two cases the port folds into one outcome.
    private async Task<string?> ResolveAccessTokenAsync(string userId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_clientSecret))
        {
            return null;
        }

        var grain = _grains.GetGrain<IUserGoogleGrain>(userId);
        var connection = await grain.GetAsync();
        if (connection is null)
        {
            return null;
        }

        // A null here is a rotated Data Protection key ring, not a bug: the user's calendar goes quiet
        // and they reconnect. See GoogleSecretProtector.
        var refreshToken = _protector.Unprotect(connection.RefreshTokenCiphertext);
        var accessToken = _protector.Unprotect(connection.AccessTokenCiphertext);
        if (refreshToken is null)
        {
            return null;
        }

        var nearExpiry = connection.AccessTokenExpiresAt - DateTimeOffset.UtcNow <= RefreshSkew;
        if (!nearExpiry && accessToken is not null)
        {
            return accessToken;
        }

        try
        {
            var refreshed = await _oauth.RefreshAsync(_clientId, _clientSecret, refreshToken, cancellationToken);

            await grain.SetAsync(connection with
            {
                AccessTokenCiphertext = _protector.Protect(refreshed.AccessToken),
                // The refresh response carries no refresh_token, so the client carried the old one
                // forward. Re-protecting it keeps the stored bundle self-consistent.
                RefreshTokenCiphertext = _protector.Protect(refreshed.RefreshToken ?? refreshToken),
                AccessTokenExpiresAt = refreshed.ExpiresAt,
            });

            return refreshed.AccessToken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed refresh is best-effort: fall back to the existing token (it may still be inside
            // the skew). If it too is rejected the call returns the curated failure.
            _logger.LogWarning("A Google token refresh threw {ExceptionType}.", ex.GetType().Name);
            return accessToken;
        }
    }

    // ── request/response shaping ────────────────────────────────────────────────────────────────────

    private static string EventPath(string? calendarId, string eventId) =>
        $"calendars/{Segment(calendarId)}/events/{Uri.EscapeDataString(eventId)}";

    // The calendar id is an email address, so it MUST be escaped into the path segment.
    private static string Segment(string? calendarId) => Uri.EscapeDataString(
        string.IsNullOrWhiteSpace(calendarId) ? PrimaryCalendarId : calendarId);

    private static string Rfc3339(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    // Builds the event resource. Only members the caller actually set are written, which is what gives
    // PATCH its "leave the rest alone" semantics. Attendees are never written — out of scope.
    private static string WriteDraft(CalendarEventDraft draft)
    {
        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            WriteIfSet(writer, "summary", draft.Title);
            WriteIfSet(writer, "description", draft.Description);
            WriteIfSet(writer, "location", draft.Location);
            WriteTime(writer, "start", draft.Start, draft.AllDay);
            WriteTime(writer, "end", draft.End, draft.AllDay);
            writer.WriteEndObject();
        }

        // An empty object is left as-is: Google rejects a create with no summary/start/end, and that
        // clean 400 becomes the curated failure the model can act on.
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteIfSet(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is not null)
        {
            writer.WriteString(name, value);
        }
    }

    // Google models an all-day event as {"date":"2026-08-26"} and a timed one as {"dateTime":"…"}.
    private static void WriteTime(Utf8JsonWriter writer, string name, string? value, bool? allDay)
    {
        if (value is null)
        {
            return;
        }

        writer.WriteStartObject(name);
        writer.WriteString(allDay == true ? "date" : "dateTime", value);
        writer.WriteEndObject();
    }

    private static IEnumerable<JsonElement> Items(JsonElement root) =>
        root.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array
            ? items.EnumerateArray()
            : [];

    private static CalendarSummary ReadCalendar(JsonElement item) => new(
        Id: GetString(item, "id") ?? "",
        Name: Truncate(GetString(item, "summary") ?? "(unnamed)", CalendarToolLimits.MaxTitleLength),
        IsPrimary: item.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True,
        TimeZone: GetString(item, "timeZone"));

    private static CalendarEvent ReadEvent(JsonElement item)
    {
        var (start, startIsDate) = ReadTime(item, "start");
        var (end, _) = ReadTime(item, "end");

        // Everything else on the resource — organizer, creator, the attendee LIST, reminder overrides,
        // conferencing data, htmlLink, every etag — is dropped. This value is persisted into the 2 MB
        // session and re-sent to the model every later turn (ADR 0004, CalendarToolLimits).
        return new CalendarEvent(
            Id: GetString(item, "id") ?? "",
            Title: Truncate(GetString(item, "summary") ?? "(no title)", CalendarToolLimits.MaxTitleLength),
            Description: Truncate(GetString(item, "description"), CalendarToolLimits.MaxDescriptionLength),
            Location: Truncate(GetString(item, "location"), CalendarToolLimits.MaxLocationLength),
            Start: start,
            End: end,
            AllDay: startIsDate,
            AttendeeCount: item.TryGetProperty("attendees", out var a) && a.ValueKind == JsonValueKind.Array
                ? a.GetArrayLength()
                : 0);
    }

    private static (string Value, bool IsDate) ReadTime(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object)
        {
            return ("", false);
        }

        var dateTime = GetString(node, "dateTime");
        return dateTime is not null ? (dateTime, false) : (GetString(node, "date") ?? "", true);
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int max) => value is null || value.Length <= max
        ? value
        : value[..max] + CalendarToolLimits.TruncationMarker;

    // Re-shapes a non-Ok transport result into the caller's payload type, disposing the document.
    private static CalendarResult<T> Carry<T>(CalendarResult<JsonDocument> response)
    {
        response.Value?.Dispose();
        return new CalendarResult<T>(response.Outcome, response.Message, default);
    }

    private static CalendarResult<CalendarEvent> SingleEvent(CalendarResult<JsonDocument> response)
    {
        if (response.Outcome != CalendarOutcome.Ok)
        {
            return Carry<CalendarEvent>(response);
        }

        using var json = response.Value!;
        return CalendarResult<CalendarEvent>.Success(ReadEvent(json.RootElement));
    }
}
