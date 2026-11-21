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

        // And it must be an EARLY RETURN, before the render passes — otherwise the hit list would be
        // appended after the ordinary rows rather than replacing them. Anchored on the first pass's
        // CALL SITE rather than on a comment: ADR 0073 extracted the passes into named functions, so the
        // "Pass 1" prose now sits above renderSidebar rather than inside it.
        // The CALL, not the declaration: the extracted passes are declared ABOVE renderSidebar, so a bare
        // name matches the declaration first and the ordering assertion would be inverted (and vacuous).
        var firstPass = sidebar.IndexOf("= renderPinnedBlock();", StringComparison.Ordinal);
        Assert.True(firstPass > branch, "the search branch must precede the render passes.");
    }

    // AC12: the service worker's SHELL lists the new module and CACHE was bumped. SHELL is
    // all-or-nothing — a missing entry plus a renamed export is a SyntaxError and a blank app — and a
    // stale CACHE serves the old modules against the new markup (ADR 0041).
    [Fact]
    public async Task ServiceWorker_ListsTheSearchModule_AndBumpsTheCache()
    {
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("\"/js/search.js\"", sw);
        // The version moves on with every MR that touches wwwroot/js/ (the tool-call fold took it to
        // v36); what this test owns is that search.js is in SHELL and the cache is not left on a stale
        // version.
        Assert.Contains("agent42-shell-v85", sw);
        Assert.DoesNotContain("agent42-shell-v32", sw);
    }
}
