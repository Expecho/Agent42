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
    private readonly HttpClient _client;

    public ShareLinkAssetsTests(StaticAssetsServerFixture fixture) => _client = fixture.Client;

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
        var app = await _client.GetStringAsync("/app.js");

        var row = Body(app, "function buildConvRow(conv)");
        Assert.Contains("conv__share", row);
        Assert.Contains("openShareLink(conv.id", row);

        var open = Body(app, "async function openShareLink(convId, title)");
        Assert.Contains("/share", open);
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
    // /lib — so the Easy Auth hole stays exactly /shared + /api/share.
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

    // The source of one function, from its declaration to the brace that closes its body (copied from the
    // sibling asset tests) so an assertion can scope to one function rather than the whole file.
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
