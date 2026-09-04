namespace Agent42.Tests;

/// <summary>
/// AC12 and AC16 (ADR 0061): the chat-search frontend is wired, reachable, and — the one that
/// matters — its render branch lives INSIDE <c>renderSidebar</c>.
///
/// <para><b>Source-text heuristics.</b> The repo has no JS test runner by design, so these prove the
/// code <i>says</i> the right thing, not that a browser <i>does</i> it. AC16 is the ADR 0058
/// precedent: a client rule with no runtime test, guarded by asserting on the source and verified
/// once by hand in a browser. Every negative is paired with a positive anchor from the same
/// document, because <see cref="StaticAssetsServerFixture.GetAppScriptAsync"/> builds its corpus by
/// walking import specifiers over HTTP — a degraded walk would drop a module and turn a whole-file
/// <c>DoesNotContain</c> green for the wrong reason.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class SearchAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public SearchAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // AC12: the box exists in the conversations nav, above the list it filters.
    [Fact]
    public async Task SearchInput_IsInTheConversationsNav()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"searchInput\"", index);
        Assert.Contains("id=\"conversationList\"", index);

        // Above the list: the box filters what follows it, and a box rendered after the list would
        // read as belonging to whatever comes next.
        Assert.True(
            index.IndexOf("id=\"searchInput\"", StringComparison.Ordinal)
            < index.IndexOf("id=\"conversationList\"", StringComparison.Ordinal),
            "the search box must be rendered above the conversation list it filters.");
    }

    // AC12: the module is reachable from the composition root — a module unreachable from main.js is
    // invisible to every source-text guard in this suite, including the ones below (ADR 0041).
    [Fact]
    public async Task SearchModule_IsWiredFromTheCompositionRoot()
    {
        var main = await _client.GetStringAsync("/js/main.js");

        Assert.Contains("wireSearch", main);
        Assert.Contains("./search.js", main);

        var app = await _fixture.GetAppScriptAsync();
        Assert.Contains("export function wireSearch", app);
    }

    // AC16 — the load-bearing client rule of this feature. renderSidebar is called from nine sites
    // that fire independently of the search box (a background refresh, ADR 0032 cache hydration, an
    // agent save, send()'s success path). If the hits were painted by a render path BESIDE
    // renderSidebar rather than inside it, any of those callers would silently wipe an active result
    // list while the user was reading it.
    //
    // Asserted structurally: the search branch must appear after renderSidebar's opening line and
    // before the pinned-block pass that follows it, which is what "inside" means here.
    [Fact]
    public async Task SearchBranch_LivesInsideRenderSidebar()
    {
        var sidebar = await _client.GetStringAsync("/js/sidebar.js");

        var renderStart = sidebar.IndexOf("export function renderSidebar()", StringComparison.Ordinal);
        Assert.True(renderStart >= 0, "renderSidebar must still exist in sidebar.js.");

        var branch = sidebar.IndexOf("renderSearchHits(conversationList)", StringComparison.Ordinal);
        Assert.True(branch > renderStart, "the search render must be called from INSIDE renderSidebar.");

        // And it must be an EARLY RETURN, before the pinned/unpinned walk — otherwise the hit list
        // would be appended after the ordinary rows rather than replacing them.
        var pinnedPass = sidebar.IndexOf("Pass 1 — the pinned block", StringComparison.Ordinal);
        Assert.True(pinnedPass > branch, "the search branch must precede the pinned/unpinned passes.");
    }

    // The ADR 0045 two-pass walk survives on the no-query path: the search branch must not have been
    // implemented by replacing the grouping walk.
    [Fact]
    public async Task PinnedAndGroupedPasses_SurviveOnTheNoQueryPath()
    {
        var sidebar = await _client.GetStringAsync("/js/sidebar.js");

        Assert.Contains("Pass 1 — the pinned block", sidebar);
        Assert.Contains("Pass 2 — the unchanged grouping walk", sidebar);
        Assert.Contains("buildConvRow(conv, true)", sidebar);
    }

    // The hit list is built with textContent, never innerHTML: it renders message text, which
    // includes whatever a model or a web page put into the conversation.
    [Fact]
    public async Task SearchHits_AreRenderedAsTextNotMarkup()
    {
        var search = await _client.GetStringAsync("/js/search.js");

        Assert.Contains("textContent", search);
        // The ASSIGNMENT, not the word: search.js's own comment explains why it uses textContent, and
        // a bare "innerHTML" negative would trip over that comment rather than over real markup
        // injection.
        Assert.DoesNotContain(".innerHTML", search);
    }

    // AC12: the service worker's SHELL lists the new module and CACHE was bumped. SHELL is
    // all-or-nothing — a missing entry plus a renamed export is a SyntaxError and a blank app — and a
    // stale CACHE serves the old modules against the new markup (ADR 0041).
    [Fact]
    public async Task ServiceWorker_ListsTheSearchModule_AndBumpsTheCache()
    {
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/search.js\"", sw);
        // The version moves on with every MR that touches wwwroot/js/ (ADR 0064 took it to v28); what this
        // test owns is that search.js is in SHELL and the cache is not left on a stale version.
        Assert.Contains("agent42-shell-v31", sw);
        Assert.DoesNotContain("agent42-shell-v30", sw);
    }
}
