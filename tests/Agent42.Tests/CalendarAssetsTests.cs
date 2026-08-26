namespace Agent42.Tests;

/// <summary>
/// Guards the Preferences → Calendar wiring (ADR 0053). Serves the real Backend <c>wwwroot</c> through
/// the shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="ConfigHealthAssetsTests"/> (the repo has no JS runner by
/// design, so these prove the code <i>says</i> the right thing, not that a browser <i>does</i> it).
/// </summary>
[Collection("StaticAssets")]
public sealed class CalendarAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public CalendarAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task CalendarTab_IsPresentInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"tabCalendar\"", index);
        Assert.Contains("id=\"panelCalendar\"", index);
        Assert.Contains("id=\"googleStatus\"", index);
        Assert.Contains("id=\"googleConnectBtn\"", index);
        Assert.Contains("id=\"googleDisconnectBtn\"", index);
    }

    // AC14. The tab is APPENDED, never inserted: the ADR 0025 MCP-OAuth return deep-links to
    // profileTabs.select(3), so anything placed before the MCP pair silently sends a returning user to
    // the wrong tab. Asserted on the ordering of the wireTabs array, which is where the index comes from.
    [Fact]
    public async Task CalendarTab_IsAppendedLast_AndMcpStaysThird()
    {
        var app = await _fixture.GetAppScriptAsync();

        var start = app.IndexOf("const profileTabs = wireTabs(", StringComparison.Ordinal);
        Assert.True(start >= 0, "profileTabs wireTabs call not found — renamed?");
        var end = app.IndexOf("]);", start, StringComparison.Ordinal);
        Assert.True(end > start, "profileTabs array not terminated");
        var wire = app[start..end];

        var mcpAt = wire.IndexOf("tabMcp", StringComparison.Ordinal);
        var configAt = wire.IndexOf("tabConfig", StringComparison.Ordinal);
        var skillsAt = wire.IndexOf("tabSkills", StringComparison.Ordinal);
        var calendarAt = wire.IndexOf("tabCalendar", StringComparison.Ordinal);

        Assert.True(mcpAt >= 0, "tabMcp missing from profileTabs");
        Assert.True(calendarAt > mcpAt, "tabCalendar must be appended AFTER tabMcp so MCP stays index 3");
        Assert.True(calendarAt > configAt, "tabCalendar must come after tabConfig (Configuration is 4)");
        Assert.True(calendarAt > skillsAt, "tabCalendar must come after tabSkills (Skills is 5)");

        // The MCP-OAuth return still deep-links to index 3, and Calendar's own return goes to 6.
        Assert.Contains("profileTabs.select(3)", app, StringComparison.Ordinal);
        Assert.Contains("profileTabs.select(6)", app, StringComparison.Ordinal);
    }

    // The connect flow is a full NAVIGATION, not a fetch. /api/google/oauth/start answers 302 to Google's
    // consent screen; fetching it would follow the redirect in the background and the user would never
    // see the screen they are supposed to act on.
    [Fact]
    public async Task Connect_NavigatesToTheStartEndpoint_RatherThanFetchingIt()
    {
        var calendar = await _client.GetStringAsync("/js/calendar.js");

        Assert.Contains("location.assign(\"/api/google/oauth/start\")", calendar, StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(\"/api/google/oauth/start\")", calendar, StringComparison.Ordinal);
    }

    // The status endpoint drives the two buttons, and the "not configured" case must offer NEITHER —
    // a Connect button on a deployment with no Google client would open a flow that 409s.
    [Fact]
    public async Task Status_DrivesBothButtons_AndOffersNeitherWhenUnconfigured()
    {
        var calendar = await _client.GetStringAsync("/js/calendar.js");

        Assert.Contains("/api/google/status", calendar, StringComparison.Ordinal);
        Assert.Contains("googleConnectBtn.hidden = !status.configured || status.connected", calendar, StringComparison.Ordinal);
        Assert.Contains("googleDisconnectBtn.hidden = !status.connected", calendar, StringComparison.Ordinal);
    }

    // The disconnect removes OUR tokens and nothing else. Saying so is the whole point of the line —
    // a user who believes disconnecting revoked the grant at Google would be wrong about what access
    // still exists, which is exactly the kind of thing this app should not be vague about.
    [Fact]
    public async Task Disconnect_SaysItDoesNotRevokeTheGrantAtGoogle()
    {
        var calendar = await _client.GetStringAsync("/js/calendar.js");
        var index = await _client.GetStringAsync("/");

        Assert.Contains("/api/google/connection", calendar, StringComparison.Ordinal);
        Assert.Contains("myaccount.google.com/permissions", calendar, StringComparison.Ordinal);
        Assert.Contains("myaccount.google.com/permissions", index, StringComparison.Ordinal);
    }

    // The callback's reason codes are mapped to sentences here, and Google's own error text is never
    // shown: the server sends a CATEGORY precisely because an error_description carries the client id
    // and the redirect URI. A user-facing string built from params.get("reason") raw would undo that.
    [Fact]
    public async Task OAuthReturn_MapsReasonCodes_RatherThanEchoingThem()
    {
        var calendar = await _client.GetStringAsync("/js/calendar.js");

        Assert.Contains("google_oauth", calendar, StringComparison.Ordinal);
        Assert.Contains("CONNECT_ERRORS[params.get(\"reason\")]", calendar, StringComparison.Ordinal);
        // Every reason the callback can send has a sentence, or the user gets the generic fallback for
        // the one case that actually needed explaining.
        foreach (var reason in new[]
                 {
                     "denied", "not_configured", "missing_code_or_state", "unknown_state",
                     "no_refresh_token", "exchange_failed",
                 })
        {
            Assert.Contains($"{reason}:", calendar, StringComparison.Ordinal);
        }
    }

    // The panel tells the user what connecting actually permits — full read/write on events, with no
    // per-action confirmation — and what it does not (creating/deleting calendars, changing sharing).
    // That is the consent surface for ADR 0053's accepted residual risk; it is not decoration.
    [Fact]
    public async Task CalendarPanel_StatesWhatTheConnectionPermits()
    {
        var index = await _client.GetStringAsync("/");
        var start = index.IndexOf("id=\"panelCalendar\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "panelCalendar missing");
        // To the dialog's action row, not to the first "</div>" — the panel contains nested divs, so the
        // first close tag lands inside .providers__head and would slice the prose away.
        var end = index.IndexOf("agent-form__actions", start, StringComparison.Ordinal);
        Assert.True(end > start, "panelCalendar must sit before the dialog's action row");
        var panel = index[start..end];

        Assert.Contains("delete events", panel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("without asking you to confirm", panel, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cannot create or delete a calendar", panel, StringComparison.OrdinalIgnoreCase);
    }
}
