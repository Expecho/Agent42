namespace Agent42.Tests;

/// <summary>
/// Guards the token-usage dashboard wiring (ADR 0031) — a dedicated full-page overlay, not a
/// Preferences tab. Serves the real Backend <c>wwwroot</c> through the shared
/// <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text-heuristic shape as <see cref="LocationAssetsTests"/>/<see cref="ConfigHealthAssetsTests"/>
/// (the repo has no JS runner by design, so these prove the code <i>says</i> the right thing, not that a
/// browser <i>does</i> it).
/// </summary>
[Collection("StaticAssets")]
public sealed class UsageAssetsTests
{
    private readonly HttpClient _client;

    public UsageAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

    // AC15: the dedicated view and its nav entry are present in the markup.
    [Fact]
    public async Task UsageView_IsPresentInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"usageView\"", index);
        Assert.Contains("id=\"openUsageBtn\"", index);
        Assert.Contains("id=\"usageCloseBtn\"", index);
        Assert.Contains("id=\"usageApplication\"", index);
        Assert.Contains("id=\"usageUser\"", index);
        Assert.Contains("id=\"usageRange\"", index);
        Assert.Contains("id=\"usageRefresh\"", index);
    }

    // AC15: the view fetches the reference contract endpoint.
    [Fact]
    public async Task App_FetchesUsageEndpoint()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("fetch(`/api/usage?", app);
    }

    // AC15: auto-refresh is wired — the interval select drives a setInterval, and BOTH closeUsage()
    // AND a re-selection clear it first. A leaked timer would keep polling /api/usage from the
    // console view (the M5 critic finding this test exists to pin).
    [Fact]
    public async Task App_WiresAutoRefresh_AndClearsIntervalOnClose()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("usageRefresh", app);
        Assert.Contains("setInterval(loadUsage", app);

        var closeUsage = Body(app, "function closeUsage()");
        Assert.Contains("clearInterval", closeUsage + Body(app, "function stopUsageAutoRefresh()"));
        Assert.Contains("stopUsageAutoRefresh()", closeUsage);
    }

    // AC15: rows render a share bar — the only "chart" this view uses, a percentage-width div.
    [Fact]
    public async Task App_RendersShareBar()
    {
        var app = await _client.GetStringAsync("/app.js");

        Assert.Contains("bar__fill", app);
        var barCell = Body(app, "function usageBarCell(value, max)");
        Assert.Contains("* 100", barCell);
        Assert.Contains("fill.style.width", barCell);
    }

    // AC15: the overlay has its own CSS with a leak-proof z-index above the rest of the app.
    [Fact]
    public async Task Styles_DefineUsageOverlay()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        Assert.Contains(".usage {", styles);
        Assert.Contains(".bar__fill", styles);
    }

    // The source of one function, from its declaration to the brace that closes its body — copied
    // from LocationAssetsTests.Body so an assertion can scope to one function rather than the whole file.
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
