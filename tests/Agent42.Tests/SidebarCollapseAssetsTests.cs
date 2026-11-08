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

}
