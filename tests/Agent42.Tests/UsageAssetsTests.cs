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
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public UsageAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

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

    // AC10 (ADR 0104): the dashboard no longer claims cached and reasoning always read 0 — the emitter does tag them
    // (UsageProcessorTests.ChatSpan_CarriesCachedAndReasoningTokens).
    [Fact]
    public async Task UsageNote_DoesNotClaimCachedAlwaysZero()
    {
        var index = await _client.GetStringAsync("/");

        Assert.DoesNotContain("emits only input/output", index, StringComparison.Ordinal);
        Assert.Contains("Cached input and reasoning are what each provider reports", index, StringComparison.Ordinal);
    }

    // AC11 (ADR 0104): the per-turn usage line reads the Done event's cacheWriteTokens, and send.js hands it the chunk.
    [Fact]
    public async Task ShowUsage_RendersCacheWrites()
    {
        var thread = await _client.GetStringAsync("/js/thread.js");
        var send = await _client.GetStringAsync("/js/send.js");

        Assert.Contains(
            "export function showUsage(msg, { inputTokens, outputTokens, cachedTokens, cacheWriteTokens })",
            thread, StringComparison.Ordinal);
        Assert.Contains(
            "if (cacheWriteTokens != null) parts.push(`${cacheWriteTokens.toLocaleString()} cache write`);",
            thread, StringComparison.Ordinal);
        Assert.Contains("showUsage(agentMsg, chunk)", send, StringComparison.Ordinal);
    }

    // AC15: the view fetches the reference contract endpoint.
    [Fact]
    public async Task App_FetchesUsageEndpoint()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("fetch(`/api/usage?", app);
    }

    // AC15: auto-refresh is wired — the interval select drives a setInterval, and BOTH closeUsage()
    // AND a re-selection clear it first. A leaked timer would keep polling /api/usage from the
    // console view (the M5 critic finding this test exists to pin).
    [Fact]
    public async Task App_WiresAutoRefresh_AndClearsIntervalOnClose()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("usageRefresh", app);
        Assert.Contains("setInterval(loadUsage", app);

        var closeUsage = StaticAssetsServerFixture.Body(app, "function closeUsage()");
        Assert.Contains("clearInterval", closeUsage + StaticAssetsServerFixture.Body(app, "function stopUsageAutoRefresh()"));
        Assert.Contains("stopUsageAutoRefresh()", closeUsage);
    }

    // AC15: rows render a share bar — the only "chart" this view uses, a percentage-width div.
    [Fact]
    public async Task App_RendersShareBar()
    {
        var app = await _fixture.GetAppScriptAsync();

        Assert.Contains("bar__fill", app);
        var barCell = StaticAssetsServerFixture.Body(app, "function usageBarCell(value, max)");
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
}
