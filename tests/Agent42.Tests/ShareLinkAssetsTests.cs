namespace Agent42.Tests;

/// <summary>
/// Guards the read-only share-link wiring (ADR 0035). Serves the real Backend <c>wwwroot</c> through the
/// shared <see cref="StaticAssetsServerFixture"/> and asserts on the key source strings — the same
/// source-text heuristic as <see cref="ConfigHealthAssetsTests"/> (the repo has no JS runner by design, so
/// these prove the code <i>says</i> the right thing). The public /shared/ viewer is included, so a break in
/// its API wiring or self-containment shows up here.
/// </summary>
[Collection("StaticAssets")]
public sealed class ShareLinkAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public ShareLinkAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    // The public viewer is served under /shared/, advertises itself read-only, and reads its content from the
    // token-scoped /api/share endpoint (never the user-scoped /api/conversations proxy).
    [Fact]
    public async Task Viewer_Served_AndWiredToShareApi()
    {
        var page = await _client.GetStringAsync("/shared/");
        Assert.Contains("read-only", page);
        Assert.Contains("viewer.js", page);

        var viewer = await _client.GetStringAsync("/shared/viewer.js");
        Assert.Contains("/api/share/", viewer);
        Assert.Contains("location.hash", viewer);
    }

    // Self-containment (ADR 0035): the viewer loads its libs from under /shared/, not the authenticated
    // /lib — so the Easy Auth hole stays confined to the /shared and /api/share route families. The exact
    // entries, and that they actually cover what this page requests, are asserted in EasyAuthExclusionTests
    // (ADR 0042); this test is the complementary half — that the page does not request anything else.
    [Fact]
    public async Task Viewer_IsSelfContained_NoAuthenticatedAssets()
    {
        var page = await _client.GetStringAsync("/shared/");

        Assert.Contains("lib/marked.min.js", page);
        Assert.DoesNotContain("/lib/", page);       // never the authenticated app's lib folder
        Assert.DoesNotContain("../", page);         // no traversal up out of /shared/
        Assert.DoesNotContain("styles.css", page);  // its own viewer.css, not the app stylesheet

        // The copied libs actually resolve under /shared/lib.
        (await _client.GetAsync("/shared/lib/marked.min.js")).EnsureSuccessStatusCode();
        (await _client.GetAsync("/shared/lib/purify.min.js")).EnsureSuccessStatusCode();
    }

    // The rich-rendering libs (mermaid, KaTeX, Chart.js, highlight.js) are vendored ONCE, under the public
    // /shared/lib/, and loaded from there by both the chat app and the viewer. A missing file here is a
    // diagram/formula/chart that silently stays a code block, so each one is fetched for real. The KaTeX
    // font is the one katex.min.css asks for by relative url(fonts/…).
    [Theory]
    [InlineData("/shared/lib/richtext.js")]
    [InlineData("/shared/lib/highlight/highlight.min.js")]
    [InlineData("/shared/lib/highlight/github.min.css")]
    [InlineData("/shared/lib/highlight/github-dark.min.css")]
    [InlineData("/shared/lib/mermaid/mermaid.min.js")]
    [InlineData("/shared/lib/katex/katex.min.js")]
    [InlineData("/shared/lib/katex/katex.min.css")]
    [InlineData("/shared/lib/katex/fonts/KaTeX_Main-Regular.woff2")]
    [InlineData("/shared/lib/chart/chart.umd.min.js")]
    public async Task SharedLibs_AreServed(string path)
    {
        var res = await _client.GetAsync(path);

        res.EnsureSuccessStatusCode();
        Assert.True((await res.Content.ReadAsByteArrayAsync()).Length > 0, $"{path} is empty");
    }

    // The viewer renders rich content with the SAME renderer as the app (ADR 0083), loaded relative to
    // /shared/ (an absolute /shared/lib/ path would contain the authenticated "/lib/" marker), and enhances
    // each agent turn after rendering its markdown.
    [Fact]
    public async Task Viewer_UsesSharedRenderer()
    {
        var page = await _client.GetStringAsync("/shared/");
        Assert.Contains("<script src=\"lib/richtext.js\"></script>", page);
        Assert.Contains("<script src=\"lib/highlight/highlight.min.js\"></script>", page);

        var viewer = await _client.GetStringAsync("/shared/viewer.js");
        var turns = StaticAssetsServerFixture.Body(viewer, "function renderTurns(turns)");
        var render = turns.IndexOf("prose.innerHTML = renderMarkdown(", StringComparison.Ordinal);
        var enhance = turns.IndexOf("rich.enhance(prose)", StringComparison.Ordinal);
        Assert.True(render >= 0, "renderTurns no longer renders agent markdown");
        Assert.True(enhance > render, "renderTurns must enhance AFTER rendering the markdown");
        Assert.Contains("marked.use({ extensions: rich.mathExtensions })", viewer);
    }

    // The gate is a PURE capability check (ADR 0047) — not a touch test and not a UA sniff. Without this the
    // other four guards all pass against a hardcoded true or a userAgent match, so the decision would be
    // unfalsifiable. The negatives are paired with the positive anchor from the same document, per the
    // discipline in TopbarActionsAssetsTests.
    [Fact]
    public async Task NativeShare_IsGatedOnAPureFeatureDetect()
    {
        var module = await _client.GetStringAsync("/js/sharelinks.js");

        Assert.Contains("typeof navigator.share === \"function\"", module);
        Assert.DoesNotContain("userAgent", module);
        Assert.DoesNotContain("hover: none", module);
    }

    // Create renders the minted link BEFORE it opens the sheet. Ordering, not presence: transient user
    // activation is a Window-scoped window that can expire across the POST await (and WebKit wants the call
    // in the gesture's own task), so a share that never opens must still leave the user holding the link.
    [Fact]
    public async Task NativeShare_RendersTheLinkBeforeOpeningTheSheet()
    {
        var app = await _fixture.GetAppScriptAsync();

        var create = StaticAssetsServerFixture.Body(app, "async function createShareLink()");
        var render = create.IndexOf("renderShareLinkState", StringComparison.Ordinal);
        var share = create.IndexOf("nativeShareLink", StringComparison.Ordinal);
        Assert.True(render >= 0, "createShareLink no longer renders the link");
        Assert.True(share >= 0, "createShareLink no longer opens the share sheet");
        Assert.True(render < share, "createShareLink must render the link BEFORE handing it to the sheet");
    }

}
