namespace Agent42.Tests;

/// <summary>
/// Guards the user-location wiring in the page (ADR 0012): since ADR 0090 the browser answers the
/// <c>get_current_location</c> client tool instead of sending a fix with every chat request. Serves the real Backend <c>wwwroot</c>
/// through the shared <see cref="StaticAssetsServerFixture"/>, then asserts on the key source strings —
/// the same shape as <see cref="SpeechInputAssetsTests"/>.
///
/// <para><b>These are source-text heuristics, and their limits matter here more than usual.</b> The repo
/// has no JS test runner by design, so they prove the code <i>says</i> the right thing, not that a
/// browser <i>does</i> it. The WebKit leg especially (AC12) is only real once it has run on a device:
/// <c>navigator.permissions.query({name:"geolocation"})</c> rejects on iOS, and no C# string assertion
/// can tell you that. The Chrome-on-Android speech gotcha is the precedent — desktop and mobile engines
/// diverge, and only the device tells you.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class LocationAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public LocationAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    [Fact]
    public async Task LocationControl_IsInPreferences_AndComposerButtonRemoved()
    {
        var index = await _client.GetStringAsync("/");

        // The grant now lives in Preferences → General as an Enable button + status line.
        Assert.Contains("id=\"locationEnableBtn\"", index);
        Assert.Contains("id=\"locationStatus\"", index);

        // The old composer button (and its CSS hook) is gone — location is granted only from Preferences.
        Assert.DoesNotContain("id=\"locationBtn\"", index);
        var styles = await _client.GetStringAsync("/styles.css");
        Assert.DoesNotContain("composer__location", styles);
    }

    // AC2 (ADR 0090) — no location is sent on a chat request, and the send path never touches geolocation:
    // the position leaves the browser only as the answer to the location client tool, when the model asks.
    [Fact]
    public async Task Send_NeverTouchesGeolocation()
    {
        var app = await _fixture.GetAppScriptAsync();

        var send = StaticAssetsServerFixture.Body(app, "async function send(prompt, replay = null)");
        Assert.DoesNotContain("getCurrentPosition", send);
        Assert.DoesNotContain("location:", send);
        Assert.DoesNotContain("locationForTool", send);
        Assert.DoesNotContain("currentLocationForSend", app);

        // The tool is answered through the client-tool handlers instead.
        Assert.Contains("get_current_location: () => locationForTool()", app);
    }

    // AC3 (ADR 0090) — the tool checks the grant BEFORE any capture, so a page that is not authorized answers
    // not_shared without calling getCurrentPosition (no prompt on the Permissions-API leg). A denial on the
    // capture answers not_shared as well, and clears the stored grant through handlePositionError.
    [Fact]
    public async Task LocationTool_ChecksTheGrantBeforeCapturing()
    {
        var app = await _fixture.GetAppScriptAsync();

        var tool = StaticAssetsServerFixture.Body(app, "async function locationForTool()");
        var grant = tool.IndexOf("isCaptureAuthorized()", StringComparison.Ordinal);
        var capture = tool.IndexOf("readPosition(", StringComparison.Ordinal);
        Assert.True(grant >= 0 && capture > grant, "the grant check must come before the capture");
        Assert.Contains("status: \"not_shared\"", tool[..capture]);
        Assert.Contains("handlePositionError(error)", tool);
        Assert.Contains("status: \"no_fix\"", tool);
    }

    // AC4 (ADR 0090) — a stale fix is never returned (the model must not be told the user is somewhere they
    // left half an hour ago), and the one fresh capture is bounded BELOW the server's wait for an automatic
    // client tool, so the page answers no_fix before the server gives up on it.
    [Fact]
    public async Task LocationTool_DropsStaleFix_AndBoundsTheCapture()
    {
        var app = await _fixture.GetAppScriptAsync();

        var fresh = StaticAssetsServerFixture.Body(app, "function isFresh(fix)");
        Assert.Contains("LOCATION_MAX_AGE_MS", fresh);

        var tool = StaticAssetsServerFixture.Body(app, "async function locationForTool()");
        Assert.Contains("isFresh(lastFix)", tool);
        Assert.Contains("readPosition(LOCATION_TOOL_TIMEOUT_MS)", tool);
        Assert.Contains("timeout: timeoutMs", StaticAssetsServerFixture.Body(app, "function readPosition("));

        var bound = System.Text.RegularExpressions.Regex.Match(app, @"const LOCATION_TOOL_TIMEOUT_MS = (\d+);");
        Assert.True(bound.Success, "LOCATION_TOOL_TIMEOUT_MS must be a plain number");
        Assert.True(
            int.Parse(bound.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                < Agent42.Agents.ClientToolLimits.AutomaticTimeout.TotalMilliseconds,
            "the tool's capture must end before the server's wait does");
    }

    // AC12 — capture is authorized by EITHER leg, and a denial clears the stored grant.
    //
    // The second leg is not belt-and-braces: navigator.permissions.query({name:"geolocation"}) is
    // unsupported in WebKit (every browser on iOS/iPadOS), where it rejects. Gating capture purely on
    // state === "granted" would make this feature dead on every iPhone — the user grants, no fix is ever
    // attached, and the grant button never hides.
    [Fact]
    public async Task App_AuthorizesCaptureOnBothLegs()
    {
        var app = await _fixture.GetAppScriptAsync();

        var authorize = StaticAssetsServerFixture.Body(app, "async function isCaptureAuthorized()");
        // Leg 1: the Permissions API, when it exists.
        Assert.Contains("\"granted\"", authorize);
        // Leg 2: the stored grant, when it does not.
        Assert.Contains("storedGrant()", authorize);

        // The stored grant is only ever set after a capture actually SUCCEEDED — never merely because
        // the button was tapped, which would authorize us off a prompt the user then dismissed.
        var grant = StaticAssetsServerFixture.Body(app, "async function requestLocationGrant()");
        Assert.Contains("await readPosition()", grant);
        Assert.Contains("setStoredGrant(true)", grant);

        // …and a PERMISSION_DENIED (code 1) on any later capture clears it: the user revoked the
        // permission in site settings, so the flag is now a lie and the grant control comes back.
        var onError = StaticAssetsServerFixture.Body(app, "function handlePositionError(error)");
        Assert.Contains("code === 1", onError);
        Assert.Contains("setStoredGrant(false)", onError);
    }

    // Capture also runs out of band — on grant, on the tab becoming visible, and on an interval while
    // it stays visible — which is what lets most location tool calls be answered from the cache at once.
    [Fact]
    public async Task App_RefreshesTheFixInTheBackground()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("visibilitychange", app);
        Assert.Contains("startBackgroundLocationRefresh", app);
        Assert.Contains("LOCATION_REFRESH_MS", app);
    }
}
