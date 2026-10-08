namespace Agent42.Tests;

/// <summary>
/// The collapsible desktop sidebar (ADR 0086): a manual hide/reopen pair, plus an automatic collapse while the window
/// is narrow or the artifact pane is open. Guarded on the source text served from the real <c>wwwroot</c>, the
/// <see cref="TopbarActionsAssetsTests"/> shape. Which rule wins at a given width is a cascade claim no source-text test
/// reaches, so these pin that the declarations shipped; exercise the behaviour by hand in DevHost.
/// </summary>
[Collection("StaticAssets")]
public sealed class SidebarCollapseAssetsTests(StaticAssetsServerFixture fixture)
{
    private const string CollapsedKey = "agent42.sidebar.collapsed";
    private const string NarrowQuery = "(min-width: 761px) and (max-width: 1100px)";

    private readonly HttpClient _client = fixture.Client;

    // The hide button sits in the sidebar header, and the reopen path is the topbar hamburger (outside the sidebar,
    // so it is still on screen once the sidebar is gone). Index ordering, since Body() cannot slice HTML.
    [Fact]
    public async Task CollapseButton_LivesInTheSidebarHeader()
    {
        var index = await _client.GetStringAsync("/");

        var headerAt = index.IndexOf("<header class=\"sidebar__header\">", StringComparison.Ordinal);
        var buttonAt = index.IndexOf("id=\"collapseSidebarBtn\"", StringComparison.Ordinal);
        var headerEnd = index.IndexOf("</header>", headerAt, StringComparison.Ordinal);
        Assert.True(headerAt >= 0 && buttonAt > headerAt && buttonAt < headerEnd, "the collapse button is not in the sidebar header");

        var sidebarEnd = index.IndexOf("</aside>", StringComparison.Ordinal);
        var menuAt = index.IndexOf("id=\"menuToggle\"", StringComparison.Ordinal);
        Assert.True(sidebarEnd >= 0 && menuAt > sidebarEnd, "the reopen hamburger must live outside the sidebar");
    }

    // The pre-paint script and drawer.js compute the SAME initial state; if either drifts, a stored collapse or a
    // narrow window flashes the sidebar in (or out) once the module loads.
    [Fact]
    public async Task PrePaintRule_MatchesTheModule()
    {
        var index = await _client.GetStringAsync("/");
        var drawer = await _client.GetStringAsync("/js/drawer.js");

        foreach (var source in new[] { index, drawer })
        {
            Assert.Contains(CollapsedKey, source);
            Assert.Contains(NarrowQuery, source);
            Assert.Contains("is-sidebar-collapsed", source);
        }
    }

    // Auto collapse: both triggers are watched, and the artifact pane is observed through its class on <html> rather
    // than by importing the sidebar into shared/lib/artifactpane.js, which the public viewer also loads.
    [Fact]
    public async Task AutoCollapse_WatchesTheNarrowWindowAndTheArtifactPane()
    {
        var drawer = await _client.GetStringAsync("/js/drawer.js");
        var pane = await _client.GetStringAsync("/shared/lib/artifactpane.js");

        Assert.Contains("narrow.addEventListener(\"change\", onAutoTriggerChange)", drawer);
        Assert.Contains("new MutationObserver(onAutoTriggerChange)", drawer);
        Assert.Contains("root.classList.contains(\"has-artifact-pane\")", drawer);

        Assert.Contains("classList.add(\"has-artifact-pane\")", pane); // anchor: the signal still exists
        Assert.DoesNotContain("sidebar", pane);

        // Only an EDGE of the trigger collapses, so a manual reopen while it holds is not undone on the next event.
        var change = StaticAssetsServerFixture.Body(drawer, "function onAutoTriggerChange()");
        Assert.Contains("if (next === autoCollapsed) return;", change);
    }

    // The collapsed rules live only in the >760px block, so the class can never fight the mobile drawer.
    [Fact]
    public async Task CollapsedStyles_AreDesktopOnly()
    {
        var styles = await _client.GetStringAsync("/styles.css");

        // Line-ending agnostic: find the first collapsed rule, then the >760px block opening right before it.
        var firstRuleAt = styles.IndexOf("html.is-sidebar-collapsed .app", StringComparison.Ordinal);
        Assert.True(firstRuleAt >= 0, "the collapsed rules are missing");
        var desktopAt = styles.LastIndexOf("@media (min-width: 761px) {", firstRuleAt, StringComparison.Ordinal);
        Assert.True(desktopAt >= 0, "the >760px collapsed block is missing");
        var block = StaticAssetsServerFixture.Body(styles[desktopAt..], "@media (min-width: 761px) {");
        Assert.Contains("html.is-sidebar-collapsed .sidebar { display: none; }", block);
        Assert.Contains("html.is-sidebar-collapsed .topbar__menu { display: grid; }", block);

        // Every collapsed-state selector in the file is one of the four in that block.
        static int Count(string text) => text.Split("html.is-sidebar-collapsed .").Length - 1;
        Assert.Equal(4, Count(block));
        Assert.Equal(Count(block), Count(styles));
    }
}
