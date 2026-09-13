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

    // The owner-side dialog and its create / share-or-copy / revoke controls are present, with names distinct from the
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

        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
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
        var row = StaticAssetsServerFixture.Body(app, "export function buildConvRow(conv, hoisted, grouped)");
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

    // ADR 0047 — the OS share sheet. Both buttons ship in the markup and Share starts hidden, so a browser
    // without navigator.share (and the pre-JS paint) shows Copy. Bounded at BOTH ends twice over: the button
    // has to sit inside #shareLinkDialog rather than merely somewhere in the document (it would otherwise
    // pass from the topbar), and the hidden attribute has to be on its OWN tag rather than on the
    // #shareLinkResult wrapper two lines up.
    [Fact]
    public async Task NativeShare_ButtonIsInTheDialog_AndHiddenByDefault()
    {
        var index = await _client.GetStringAsync("/");

        var dialogAt = index.IndexOf("id=\"shareLinkDialog\"", StringComparison.Ordinal);
        Assert.True(dialogAt >= 0, "the share dialog is missing");
        var dialogEnd = index.IndexOf("</dialog>", dialogAt, StringComparison.Ordinal);
        Assert.True(dialogEnd > dialogAt, "the share dialog is not closed");
        var dialog = index[dialogAt..dialogEnd];

        var at = dialog.IndexOf("id=\"shareLinkShareBtn\"", StringComparison.Ordinal);
        Assert.True(at >= 0, "the native-share button is missing from the share dialog");
        var end = dialog.IndexOf("</button>", at, StringComparison.Ordinal);
        Assert.True(end > at, "the native-share button is not closed");
        Assert.Contains("hidden", dialog[at..end]);
    }

    // The swap is a PAIR, not an add: where the sheet exists Share appears and Copy goes away. Asserting
    // both halves is what makes it a swap — the Share half alone would pass with the two buttons side by
    // side, which is the alternative ADR 0047 rejected.
    [Fact]
    public async Task NativeShare_ReplacesCopyWhenTheApiExists()
    {
        var app = await _fixture.GetAppScriptAsync();

        var wire = StaticAssetsServerFixture.Body(app, "export function wireShareLinks()");
        Assert.Contains("shareLinkShareBtn.hidden = !canNativeShare", wire);
        Assert.Contains("shareLinkCopyBtn.hidden = canNativeShare", wire);
        Assert.Contains("shareLinkShareBtn.addEventListener(\"click\", nativeShareLink)", wire);
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

    // The payload is title + url and nothing else (a text field repeating the URL renders twice in some
    // targets), and every rejection is swallowed — a dismissed sheet (AbortError) and a blocked one
    // (NotAllowedError) are both non-events. The negative is scoped to the call's own argument rather than
    // the whole function, so prose in a comment cannot trip it.
    [Fact]
    public async Task NativeShare_SendsTitleAndUrl_AndSwallowsAFailedSheet()
    {
        var app = await _fixture.GetAppScriptAsync();

        var body = StaticAssetsServerFixture.Body(app, "async function nativeShareLink()");
        Assert.Contains("navigator.share", body);
        Assert.Contains("title:", body);
        Assert.Contains("catch", body);

        var at = body.IndexOf("navigator.share(", StringComparison.Ordinal);
        var end = body.IndexOf(')', at);
        Assert.True(end > at, "the navigator.share call is not closed");
        var payload = body[at..end];
        Assert.Contains("title:", payload);
        Assert.Contains("url", payload); // on the ARGUMENT, not the body — `const url = …` makes that vacuous
        Assert.DoesNotContain("text:", payload); // anchored by the positives above
    }
}
