namespace Agent42.Tests;

/// <summary>
/// Guards the user-location wiring in the composer (ADR 0012). Serves the real Backend <c>wwwroot</c>
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
    private readonly HttpClient _client;

    public LocationAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    [Fact]
    public async Task ShareLocationButton_IsPresent()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"locationBtn\"", index);
        // Ships hidden: it is shown only when we could capture but are not yet authorized.
        Assert.Matches(@"id=""locationBtn""[^>]*hidden", index);
    }

    // AC11 — the send path must never call getCurrentPosition, and never await a fix. An awaited
    // acquisition would add seconds to EVERY message once the cached fix went stale (a cold GPS lock on
    // mobile takes seconds), so typing "hi" would pay for a GPS spin-up. The send path reads an
    // already-cached fix synchronously instead.
    [Fact]
    public async Task App_NeverBlocksSendOnGeolocation()
    {
        var app = await _client.GetStringAsync("/app.js");

        // The send body attaches the cached fix through the synchronous accessor…
        Assert.Contains("location: currentLocationForSend()", app);

        // …and that accessor is neither async nor awaiting anything.
        var accessor = Body(app, "function currentLocationForSend()");
        Assert.DoesNotContain("await", accessor);
        Assert.DoesNotContain("getCurrentPosition", accessor);
        Assert.DoesNotContain("async function currentLocationForSend", app);

        // The send function itself never asks the browser for a position.
        var send = Body(app, "async function send(prompt)");
        Assert.DoesNotContain("getCurrentPosition", send);
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
        var app = await _client.GetStringAsync("/app.js");

        var authorize = Body(app, "async function isCaptureAuthorized()");
        // Leg 1: the Permissions API, when it exists.
        Assert.Contains("\"granted\"", authorize);
        // Leg 2: the stored grant, when it does not.
        Assert.Contains("storedGrant()", authorize);

        // The stored grant is only ever set after a capture actually SUCCEEDED — never merely because
        // the button was tapped, which would authorize us off a prompt the user then dismissed.
        var grant = Body(app, "async function requestLocationGrant()");
        Assert.Contains("await readPosition()", grant);
        Assert.Contains("setStoredGrant(true)", grant);

        // …and a PERMISSION_DENIED (code 1) on any later capture clears it: the user revoked the
        // permission in site settings, so the flag is now a lie and the grant control comes back.
        var onError = Body(app, "function handlePositionError(error)");
        Assert.Contains("code === 1", onError);
        Assert.Contains("setStoredGrant(false)", onError);
    }

    // AC14 — a stale fix is dropped, never sent. The model must not be told the user is somewhere they
    // left half an hour ago.
    [Fact]
    public async Task App_DropsStaleFix()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("LOCATION_MAX_AGE_MS", app);

        var accessor = Body(app, "function currentLocationForSend()");
        Assert.Contains("LOCATION_MAX_AGE_MS", accessor);
        Assert.Contains("return null", accessor);
    }

    // Capture also runs out of band — on grant, on the tab becoming visible, and on an interval while
    // it stays visible — which is what keeps a fresh fix in the cache for the send path to find.
    [Fact]
    public async Task App_RefreshesTheFixInTheBackground()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("visibilitychange", app);
        Assert.Contains("startBackgroundLocationRefresh", app);
        Assert.Contains("LOCATION_REFRESH_MS", app);
    }

    /// <summary>
    /// The source text of one function, from its declaration to the closing brace at its own nesting
    /// depth. Crude, but it is what lets an assertion say "<c>send()</c> does not call
    /// <c>getCurrentPosition</c>" rather than the far weaker "the file contains the string somewhere" —
    /// which would pass no matter where the call sat.
    /// </summary>
    private static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' not found in app.js — did it get renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{declaration}'");
        return string.Empty;
    }
}
