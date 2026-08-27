using System.Net;

namespace Agent42.Tests;

/// <summary>
/// Guards the live telemetry console page (ADR 0054), served through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserted on its source text — the same heuristic as
/// <see cref="ShareLinkAssetsTests"/> and <see cref="ConfigHealthAssetsTests"/> (the repo has no JS runner
/// by design, so these prove the page <i>says</i> the right thing).
///
/// <para><b>This class must never use <c>GetAppScriptAsync()</c>.</b> That helper walks the ES-module
/// import graph from <c>/js/main.js</c>, and <c>wwwroot/logs/</c> is deliberately outside it — a single
/// classic script reached only by this page. Nothing under <c>wwwroot/logs/</c> is reachable from
/// <c>main.js</c>, so every existing source-text guard in the suite is blind to it and this file is the
/// only thing standing between the page and a silent break.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class LogsConsoleAssetsTests
{
    private readonly HttpClient _client;

    public LogsConsoleAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    private Task<string> PageAsync() => _client.GetStringAsync("/logs/");

    private Task<string> ScriptAsync() => _client.GetStringAsync("/logs/logs.js");

    [Fact]
    public async Task LogsPage_IsServedAtBothSlashForms()
    {
        // Measured against a real Kestrel server and reproduced here: UseDefaultFiles'
        // RedirectToAppendTrailingSlash (on by default) 301s the bare form. Two separate requests,
        // because the TestServer client does not follow redirects — and the redirect is exactly why the
        // page's relative asset hrefs resolve: the browser lands on /logs/, not /logs.
        var bare = await _client.GetAsync("/logs");
        Assert.Equal(HttpStatusCode.MovedPermanently, bare.StatusCode);

        var location = bare.Headers.Location;
        Assert.NotNull(location);
        Assert.Equal("/logs/", location.IsAbsoluteUri ? location.AbsolutePath : location.ToString());

        var page = await _client.GetAsync("/logs/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Equal("text/html", page.Content.Headers.ContentType?.MediaType);

        var css = await _client.GetAsync("/logs/logs.css");
        Assert.Equal(HttpStatusCode.OK, css.StatusCode);
        Assert.Equal("text/css", css.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task LogsPage_HasTracesLogsAndMetricsTabs()
    {
        var page = await PageAsync();

        foreach (var signal in new[] { "Traces", "Logs", "Metrics" })
        {
            Assert.Contains($"data-tab=\"{signal.ToLowerInvariant()}\"", page);
            Assert.Contains($">{signal}<", page);
        }

        // Each tab needs somewhere to render, or the page is three buttons and nothing else.
        Assert.Contains("id=\"panelTraces\"", page);
        Assert.Contains("id=\"panelLogs\"", page);
        Assert.Contains("id=\"panelMetrics\"", page);
    }

    [Fact]
    public async Task LogsPage_FetchesAllThreeDiagnosticsEndpoints()
    {
        var script = await ScriptAsync();

        Assert.Contains("/api/diagnostics/traces", script);
        Assert.Contains("/api/diagnostics/logs", script);
        Assert.Contains("/api/diagnostics/metrics", script);

        // Polling is INCREMENTAL: without the cursor every tick re-downloads the whole ring, which on a
        // buffer holding whole conversations as span tags is megabytes every couple of seconds.
        Assert.Contains("?since=", script);
    }

    [Fact]
    public async Task LogsScript_IsASingleClassicScript()
    {
        var page = await PageAsync();

        Assert.Contains("<script src=\"logs.js\"></script>", page);

        // NOT type="module": the service worker stale-while-revalidates same-origin static assets, so a
        // module graph here carries ADR 0041's version-skew hazard (renamed export + stale cached module
        // = SyntaxError, blank page) with no CACHE bump to protect it. This MR does not touch
        // wwwroot/js/, so no bump is due — which is only safe while this stays one classic script.
        Assert.DoesNotContain("type=\"module\"", page);

        // Matched at the START of a line, so the assertion is about module SYNTAX rather than about the
        // words appearing in a comment — the looser check fails on this file's own prose.
        var script = await ScriptAsync();
        Assert.DoesNotContain("\nimport ", script);
        Assert.DoesNotContain("\nexport ", script);
    }

    [Fact]
    public async Task LogsPage_ShowsTheEffectiveMinimumLevel()
    {
        var page = await PageAsync();
        var script = await ScriptAsync();

        // The Logs tab is legitimately EMPTY in production: Logging:LogLevel:Default is Warning and
        // ILogger filtering runs BEFORE the OpenTelemetry processor, so Information records never reach
        // the buffer and nothing on this page can recover them. Surfacing the level is what makes an
        // empty tab read as correct rather than broken.
        Assert.Contains("id=\"statusLevel\"", page);
        Assert.Contains("effectiveMinimumLevel", script);
        Assert.Contains("Logging__LogLevel__Default", script);
    }

    [Fact]
    public async Task LogsPage_RendersTelemetryAsTextNeverAsMarkup()
    {
        var script = await ScriptAsync();

        // This page paints raw prompt text, tool arguments and fetched web pages — attacker-influenced
        // content by construction (ADR 0011/0027/0049). It is the one page in the app that must never
        // interpret what it shows, and it ships without marked/purify to make that unambiguous.
        // ".innerHTML" rather than "innerHTML": the member access is the actual risk, and the bare word
        // appears in this page's own comment explaining why it is absent.
        Assert.DoesNotContain(".innerHTML", script);
        Assert.Contains("textContent", script);

        var page = await PageAsync();
        Assert.DoesNotContain("marked.min.js", page);
        Assert.DoesNotContain("purify.min.js", page);
    }

    [Fact]
    public async Task LogsPage_IsSelfContainedAgainstTheAppShell()
    {
        var page = await PageAsync();

        // Like /shared, this page carries its own stylesheet rather than importing the app's. Unlike
        // /shared the reason is not Easy Auth (this page is gated, and stays gated) but the same
        // standalone-page shape: it must render with no part of the SPA loaded.
        Assert.Contains("href=\"logs.css\"", page);
        Assert.DoesNotContain("/styles.css", page);
        Assert.DoesNotContain("/js/main.js", page);
    }
}
