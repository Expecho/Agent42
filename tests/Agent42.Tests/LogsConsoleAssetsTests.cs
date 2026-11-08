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
