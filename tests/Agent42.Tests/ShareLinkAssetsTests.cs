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

    // The owner-side dialog and its create/copy/revoke controls are present, with names distinct from the
    // ADR 0023 Web Share Target (#shareDialog) so the two don't collide.
    [Fact]
    public async Task ShareDialog_PresentInMarkup()
    {
        var index = await _client.GetStringAsync("/");

        Assert.Contains("id=\"shareLinkDialog\"", index);
        Assert.Contains("id=\"shareLinkCreateBtn\"", index);
        Assert.Contains("id=\"shareLinkUrl\"", index);
        Assert.Contains("id=\"shareLinkRevokeBtn\"", index);
    }

    // Every sidebar row (regular + agent chats share buildConvRow) gets a share action that opens the dialog,
    // and the dialog's create/revoke calls hit the owner share endpoint.
    [Fact]
    public async Task ShareAction_WiredIntoConvRow_AndOwnerEndpoint()
    {
        var app = await _fixture.GetAppScriptAsync();

        var row = StaticAssetsServerFixture.Body(app, "function buildConvRow(conv, hoisted)");
        Assert.Contains("conv__share", row);
        Assert.Contains("openShareLink(conv.id", row);

        var open = StaticAssetsServerFixture.Body(app, "async function openShareLink(convId, title)");
        Assert.Contains("/share", open);
    }

    // The row action and the topbar action are two entry points into ONE feature, so they draw one mark:
    // the row's old "↗" text glyph was replaced by the same three-node share SVG the topbar button
    // carries. Pinned on the distinctive connector path, which no other icon in the app contains.
    [Fact]
    public async Task ShareIcon_IsTheSameMarkInBothPlaces()
    {
        const string connector = "M5.7 7.1l4.6-2.6M5.7 8.9l4.6 2.6";

        var index = await _client.GetStringAsync("/");
        var app = await _fixture.GetAppScriptAsync();

        // The topbar button draws it in the markup — bounded at BOTH ends, so the mark has to be inside
        // this button rather than merely somewhere further down the document.
        var shareAt = index.IndexOf("id=\"topbarShareBtn\"", StringComparison.Ordinal);
        Assert.True(shareAt >= 0, "the topbar share button is missing");
        var shareEnd = index.IndexOf("</button>", shareAt, StringComparison.Ordinal);
        Assert.True(shareEnd > shareAt, "the topbar share button is not closed");
        Assert.Contains(connector, index[shareAt..shareEnd]);

        // ...and buildConvRow draws the same one, in place of the text glyph it used to set.
        var row = StaticAssetsServerFixture.Body(app, "function buildConvRow(conv, hoisted)");
        Assert.Contains(connector, row);
        Assert.DoesNotContain("share.textContent", row); // anchored by the two positives above
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
}
