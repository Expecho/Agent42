using System.Net;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// The Google Calendar adapter (ADR 0053), tested against a recording HTTP handler that asserts on the
/// <b>outbound request lines</b> — the <c>GoogleGeocoderTests</c>/<c>ContextWebPageFetcherTests</c>
/// shape. The parameters that matter here (<c>singleEvents</c>, <c>orderBy</c>, <c>sendUpdates=none</c>)
/// are invisible in a response-shaped test and irreversible in production, so they are pinned on the
/// request.
/// </summary>
public sealed class GoogleCalendarServiceTests(SharedClusterFixture fixture) : SharedClusterTests(fixture)
{
    // Unique per TEST, not per class. xUnit builds a new instance of this class for each test, so this
    // gives every test its own IUserGoogleGrain — which is what pairs it with the per-test key ring below:
    // a shared key would leave a previous test's ciphertext, protected by a DIFFERENT ring, live in the
    // grain, and a test that read without connecting would fail as an unexplained NotConnected.
    private readonly string UserId = $"user-{Guid.NewGuid():N}@example.com";

    // A key ring per TEST, not per class: what a test protects must stay undecryptable to the rotated ring
    // UndecryptableCredential_DegradesToNotConnected builds, and a shared ring would blur that line.
    private readonly IGoogleSecretProtector _protector = new GoogleSecretProtector(
        new EphemeralDataProtectionProvider(), NullLogger<GoogleSecretProtector>.Instance);

    // ── fixture plumbing ────────────────────────────────────────────────────────────────────────────

    private async Task ConnectAsync(string userId, TimeSpan expiresIn)
    {
        await Cluster.GrainFactory.GetGrain<IUserGoogleGrain>(userId).SetAsync(new GoogleConnection(
            AccessTokenCiphertext: _protector.Protect("access-current"),
            RefreshTokenCiphertext: _protector.Protect("refresh-current"),
            AccessTokenExpiresAt: DateTimeOffset.UtcNow.Add(expiresIn),
            AccountEmail: userId,
            ConnectedAt: DateTimeOffset.UtcNow));
    }

    private GoogleCalendarService Service(
        RecordingHttpHandler handler, string clientId = "client-1", string clientSecret = "secret-1")
    {
        var http = new HttpClient(handler) { BaseAddress = new Uri(GoogleCalendarService.DefaultBaseAddress) };
        var oauth = new GoogleOAuthClient(http, "https://as.test/authorize", "https://as.test/token");

        return new GoogleCalendarService(
            http, Cluster.GrainFactory, _protector, oauth, clientId, clientSecret,
            NullLogger<GoogleCalendarService>.Instance);
    }

    private static RecordingHttpHandler Responding(string json) => new()
    {
        Responder = _ => RecordingHttpHandler.Json(json),
    };

    private const string OneEvent =
        """{"id":"evt-1","summary":"Standup","description":"Daily","location":"Room 3","start":{"dateTime":"2026-08-27T09:00:00+02:00"},"end":{"dateTime":"2026-08-27T09:15:00+02:00"},"attendees":[{"email":"a@x"},{"email":"b@x"}]}""";

    // ── AC2 ─────────────────────────────────────────────────────────────────────────────────────────

    // The window is passed through, and recurrences are EXPANDED. Without singleEvents+orderBy a
    // recurring standup is one resource with an RRULE, which is not an answer to "what's on tomorrow".
    [Fact]
    public async Task ListEvents_PassesTimeWindowAndExpandsRecurrences()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding($$"""{"items":[{{OneEvent}}]}""");

        var result = await Service(handler).ListEventsAsync(
            UserId, calendarId: null,
            new DateTimeOffset(2026, 8, 27, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero),
            maxResults: 10);

        var call = Assert.Single(handler.Recorded);
        Assert.Equal("GET", call.Method);
        Assert.Contains("singleEvents=true", call.Url, StringComparison.Ordinal);
        Assert.Contains("orderBy=startTime", call.Url, StringComparison.Ordinal);
        Assert.Contains("timeMin=2026-08-27T00%3A00%3A00Z", call.Url, StringComparison.Ordinal);
        Assert.Contains("timeMax=2026-08-28T00%3A00%3A00Z", call.Url, StringComparison.Ordinal);
        Assert.Equal("Bearer access-current", call.Authorization);

        var one = Assert.Single(result.Value!);
        Assert.Equal("Standup", one.Title);
        Assert.Equal("Room 3", one.Location);
        Assert.Equal(2, one.AttendeeCount);
        Assert.False(one.AllDay);
    }

    // A model asking for "everything this year" must not be able to raise the cap — the return value is
    // persisted into the 2 MB session (ADR 0004).
    [Fact]
    public async Task ListEvents_ClampsMaxResultsToTheBudget()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding("""{"items":[]}""");

        await Service(handler).ListEventsAsync(
            UserId, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(365), maxResults: 2500);

        Assert.Contains(
            $"maxResults={CalendarToolLimits.MaxEvents}",
            Assert.Single(handler.Recorded).Url,
            StringComparison.Ordinal);
    }

    // ── AC16 ────────────────────────────────────────────────────────────────────────────────────────

    // The one irreversible outward-facing side effect in the feature. A bare PATCH or DELETE on an event
    // that already carries attendees emails real people; sendUpdates=none is what stops that.
    [Fact]
    public async Task UpdateAndDelete_NeverNotifyAttendees()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding(OneEvent);
        var service = Service(handler);

        await service.UpdateEventAsync(UserId, null, "evt-1", new CalendarEventDraft(Title: "Renamed"));
        await service.DeleteEventAsync(UserId, null, "evt-1");

        Assert.Equal(2, handler.Recorded.Count);
        Assert.All(handler.Recorded, c => Assert.Contains("sendUpdates=none", c.Url, StringComparison.Ordinal));

        // PATCH, not PUT: a null draft member means "leave this alone", and only what was set is sent.
        Assert.Equal("PATCH", handler.Recorded[0].Method);
        Assert.Contains("\"summary\":\"Renamed\"", handler.Recorded[0].Body, StringComparison.Ordinal);
        Assert.DoesNotContain("description", handler.Recorded[0].Body, StringComparison.Ordinal);
        Assert.Equal("DELETE", handler.Recorded[1].Method);
    }

    [Fact]
    public async Task CreateEvent_WritesTheDraft_AndNeverWritesAttendees()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding(OneEvent);

        await Service(handler).CreateEventAsync(UserId, null, new CalendarEventDraft(
            Title: "Lunch", Start: "2026-08-27T12:00:00+02:00", End: "2026-08-27T13:00:00+02:00"));

        var call = Assert.Single(handler.Recorded);
        Assert.Equal("POST", call.Method);
        Assert.DoesNotContain("attendees", call.Body, StringComparison.Ordinal);

        // Asserted on the PARSED body, not the raw text: Utf8JsonWriter's default (strictest) encoder
        // escapes the '+' of a UTC offset to +. That is valid JSON decoding to the same string, so
        // Google reads it correctly — but a substring assertion on the raw text would not.
        using var body = System.Text.Json.JsonDocument.Parse(call.Body);
        Assert.Equal("Lunch", body.RootElement.GetProperty("summary").GetString());
        Assert.Equal(
            "2026-08-27T12:00:00+02:00",
            body.RootElement.GetProperty("start").GetProperty("dateTime").GetString());
    }

    [Fact]
    public async Task CreateEvent_AllDay_WritesADateNotADateTime()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding(OneEvent);

        await Service(handler).CreateEventAsync(UserId, null, new CalendarEventDraft(
            Title: "Holiday", Start: "2026-08-27", End: "2026-08-28", AllDay: true));

        var body = Assert.Single(handler.Recorded).Body;
        Assert.Contains("\"date\":\"2026-08-27\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("dateTime", body, StringComparison.Ordinal);
    }

    // ── AC9 ─────────────────────────────────────────────────────────────────────────────────────────

    // A near-expiry token refreshes BEFORE the call and the new bundle is persisted. Without the persist
    // step every single call would refresh, and the refresh would eventually rate-limit.
    [Fact]
    public async Task RefreshesNearExpiry_AndPersistsTheNewBundle()
    {
        const string user = "refresh@example.com";
        await ConnectAsync(user, TimeSpan.FromSeconds(30)); // inside the 2-minute skew

        var handler = new RecordingHttpHandler
        {
            Responder = call => call.Url.Contains("as.test/token", StringComparison.Ordinal)
                ? RecordingHttpHandler.Json("""{"access_token":"access-refreshed","expires_in":3600}""")
                : RecordingHttpHandler.Json("""{"items":[]}"""),
        };

        await Service(handler).ListCalendarsAsync(user);

        Assert.Equal(2, handler.Recorded.Count);
        Assert.Contains("grant_type=refresh_token", handler.Recorded[0].Body, StringComparison.Ordinal);
        // The refreshed token is the one the calendar call actually uses.
        Assert.Equal("Bearer access-refreshed", handler.Recorded[1].Authorization);

        var stored = await Cluster.GrainFactory.GetGrain<IUserGoogleGrain>(user).GetAsync();
        Assert.Equal("access-refreshed", _protector.Unprotect(stored!.AccessTokenCiphertext));
        // A refresh response carries no refresh_token; the original must survive, or the connection
        // dies at the next expiry.
        Assert.Equal("refresh-current", _protector.Unprotect(stored.RefreshTokenCiphertext));
        Assert.True(stored.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));
    }

    [Fact]
    public async Task ValidToken_IsNotRefreshed()
    {
        const string user = "valid@example.com";
        await ConnectAsync(user, TimeSpan.FromHours(1));
        var handler = Responding("""{"items":[]}""");

        await Service(handler).ListCalendarsAsync(user);

        var call = Assert.Single(handler.Recorded);
        Assert.Equal("Bearer access-current", call.Authorization);
        Assert.DoesNotContain("as.test/token", call.Url, StringComparison.Ordinal);
    }

    // ── AC11 ────────────────────────────────────────────────────────────────────────────────────────

    // Blank client credentials degrade PER CALL — the app is not expected to fail startup, and the two
    // "no usable connection" cases (no config, no connection) deliberately produce the same result.
    [Fact]
    public async Task BlankClientConfig_ReportsNotConfigured()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = Responding("""{"items":[]}""");

        var result = await Service(handler, clientId: "", clientSecret: "").ListCalendarsAsync(UserId);

        Assert.Equal(CalendarOutcome.NotConnected, result.Outcome);
        Assert.Equal(CalendarMessages.NotConnected, result.Message);
        Assert.Empty(handler.Recorded); // nothing was even attempted
    }

    [Fact]
    public async Task UserNeverConnected_ReportsTheSameNotConnectedResult()
    {
        var handler = Responding("""{"items":[]}""");

        var result = await Service(handler).ListCalendarsAsync("never-connected@example.com");

        Assert.Equal(CalendarOutcome.NotConnected, result.Outcome);
        Assert.Empty(handler.Recorded);
    }

    // A rotated Data Protection key ring makes the stored refresh token undecryptable. The user goes
    // quiet rather than the turn throwing — this is the silent failure GoogleSecretProtector documents.
    [Fact]
    public async Task UndecryptableCredential_DegradesToNotConnected()
    {
        const string user = "rotated@example.com";
        await ConnectAsync(user, TimeSpan.FromHours(1));

        var otherRing = new GoogleSecretProtector(
            new EphemeralDataProtectionProvider(), NullLogger<GoogleSecretProtector>.Instance);
        var handler = Responding("""{"items":[]}""");
        var http = new HttpClient(handler) { BaseAddress = new Uri(GoogleCalendarService.DefaultBaseAddress) };
        var service = new GoogleCalendarService(
            http, Cluster.GrainFactory, otherRing,
            new GoogleOAuthClient(http, "https://as.test/authorize", "https://as.test/token"),
            "client-1", "secret-1", NullLogger<GoogleCalendarService>.Instance);

        var result = await service.ListCalendarsAsync(user);

        Assert.Equal(CalendarOutcome.NotConnected, result.Outcome);
        Assert.Empty(handler.Recorded);
    }

    // ── AC15 ────────────────────────────────────────────────────────────────────────────────────────

    // A Google error body echoes the request URL, the calendar id (an email) and event detail. None of
    // it may reach the model — the result carries a constant, and the call does not throw.
    [Fact]
    public async Task ApiError_ReturnsCuratedMessage_WithoutBodyOrUrl()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent(
                    """{"error":{"message":"Insufficient scope for user@example.com on calendar boss@example.com"}}"""),
            },
        };

        var result = await Service(handler).ListEventsAsync(
            UserId, "boss@example.com", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1), 10);

        Assert.Equal(CalendarOutcome.Failed, result.Outcome);
        Assert.Equal(CalendarMessages.Failed, result.Message);
        Assert.DoesNotContain("boss@example.com", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Insufficient", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("googleapis", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TransportFailure_ReturnsCuratedMessage_AndDoesNotThrow()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = new RecordingHttpHandler
        {
            Responder = _ => throw new HttpRequestException(
                "No such host is known (www.googleapis.com/calendar/v3/calendars/boss@example.com/events)"),
        };

        var result = await Service(handler).ListCalendarsAsync(UserId);

        Assert.Equal(CalendarOutcome.Failed, result.Outcome);
        Assert.Equal(CalendarMessages.Failed, result.Message);
    }

    [Fact]
    public async Task MissingEvent_ReportsNotFound_NotFailure()
    {
        await ConnectAsync(UserId, TimeSpan.FromHours(1));
        var handler = new RecordingHttpHandler
        {
            Responder = _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };

        var result = await Service(handler).GetEventAsync(UserId, null, "gone");

        Assert.Equal(CalendarOutcome.NotFound, result.Outcome);
        Assert.Equal(CalendarMessages.NotFound, result.Message);
    }

    // ── AC19 ────────────────────────────────────────────────────────────────────────────────────────

    // OTel redacts an HttpClient span's query string by default but NOT its path, and Calendar v3 puts
    // the calendar id (an email address) and the event id in the path. ADR 0010 stamps user identity on
    // every span, so an unhandled span joins one named human to another's calendar in the collector.
    [Fact]
    public async Task CalendarSpan_DoesNotCarryTheCalendarIdInThePath()
    {
        var inner = new SuppressionProbeHandler();
        var client = new HttpClient(new CalendarSpanSuppressingHandler { InnerHandler = inner })
        {
            BaseAddress = new Uri(GoogleCalendarService.DefaultBaseAddress),
        };

        // The path really does carry the identifiers — that is the premise, asserted rather than assumed.
        var response = await client.GetAsync("calendars/boss%40example.com/events/evt-1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("boss%40example.com", inner.LastUrl!, StringComparison.Ordinal);

        // …and instrumentation is suppressed for the duration of the send, so no span records it.
        Assert.True(inner.WasSuppressed);
    }

    // ...and the handler is actually ATTACHED to the production calendar client. The test above owns
    // the mechanism; this owns the wiring, and without it deleting one line from Program.cs would
    // leave the whole suite green while every calendar id reached the collector. Source text is the
    // only option available: ProjectLayoutTests records that nothing in the suite runs Program.cs,
    // which is the same reason ComposerAssetsTests and VoiceConversationAssetsTests are source-text
    // guards.
    [Fact]
    public void CalendarHttpClient_IsRegisteredWithTheSpanSuppressingHandler()
    {
        var program = File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Agent42.Backend", "Program.cs"));

        var at = program.IndexOf("GoogleCalendarService.HttpClientName", StringComparison.Ordinal);
        Assert.True(at >= 0, "The calendar HttpClient is no longer registered by name in Program.cs.");

        // The registration chain, from AddHttpClient to the statement that ends it.
        var end = program.IndexOf(";", at, StringComparison.Ordinal);
        var registration = program[at..program.IndexOf("#pragma warning restore", at, StringComparison.Ordinal)];
        Assert.True(end > at, "Unterminated AddHttpClient registration?");

        Assert.Contains(
            "AddHttpMessageHandler(() => new CalendarSpanSuppressingHandler())",
            registration,
            StringComparison.Ordinal);

        // And the no-retry discipline the named-client gotcha requires: AgentGrain is non-reentrant,
        // and a retried POST could double-book the user.
        Assert.Contains("RemoveAllResilienceHandlers()", registration, StringComparison.Ordinal);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repository root.");
    }

    // Records whether OpenTelemetry instrumentation was suppressed at the moment of the send.
    private sealed class SuppressionProbeHandler : HttpMessageHandler
    {
        public bool WasSuppressed { get; private set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasSuppressed = OpenTelemetry.Sdk.SuppressInstrumentation;
            LastUrl = request.RequestUri!.AbsoluteUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            });
        }
    }
}
