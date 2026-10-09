using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the artifact pane script, <c>/shared/lib/artifactpane.js</c>, shared by the chat app and the public viewer.
/// It is a classic script, not part of the <c>js/</c> module graph, so it is fetched from the fixture client directly
/// and never through <see cref="StaticAssetsServerFixture.GetAppScriptAsync"/>. Source-text heuristics like every
/// other asset guard (the repo has no JS runner); the sandbox itself is checked by hand in a browser.
/// </summary>
[Collection("StaticAssets")]
public sealed class ArtifactAssetsTests
{
    private readonly StaticAssetsServerFixture _fixture;
    private readonly HttpClient _client;

    public ArtifactAssetsTests(StaticAssetsServerFixture fixture)
    {
        _fixture = fixture;
        _client = fixture.Client;
    }

    private Task<string> GetPaneAsync() => _client.GetStringAsync("/shared/lib/artifactpane.js");

    // AC16: model-written HTML runs only in a frame sandboxed to exactly allow-scripts. allow-same-origin would hand it
    // the app origin (cookies, storage, /api), so it must appear nowhere in the file, not even in a comment-free
    // corner. The frame also sends no referrer.
    [Fact]
    public async Task ArtifactPane_IsSandboxed()
    {
        var pane = await GetPaneAsync();
        var body = StaticAssetsServerFixture.Body(pane, "async function renderBody(");

        Assert.Contains("frame.setAttribute(\"sandbox\", \"allow-scripts\");", body);
        Assert.Contains("frame.setAttribute(\"referrerpolicy\", \"no-referrer\");", body);
        Assert.Single(Regex.Matches(pane, @"setAttribute\(""sandbox"""));
        Assert.DoesNotContain("allow-same-origin", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("srcdoc", pane, StringComparison.Ordinal);
    }

    // AC16: fetched source reaches innerHTML ONLY through the page's injected sanitizing renderMarkdown, and code goes
    // in as textContent (never a markdown fence, which a source containing ``` breaks out of).
    [Fact]
    public async Task ArtifactPane_RendersSourceSafely()
    {
        var pane = await GetPaneAsync();

        var assignments = Regex.Matches(pane, @"\w+\.(?:innerHTML|outerHTML)\s*=[^;]*;").Select(m => m.Value);
        Assert.Equal(["prose.innerHTML = renderMarkdown(source);"], assignments);
        Assert.DoesNotContain("insertAdjacentHTML", pane, StringComparison.Ordinal);
        Assert.DoesNotContain("document.write", pane, StringComparison.Ordinal);

        var body = StaticAssetsServerFixture.Body(pane, "async function renderBody(");
        Assert.Contains("code.textContent = source;", body);
        Assert.DoesNotContain("```", body, StringComparison.Ordinal);
    }

    // The chat app loads the pane as a classic script before its module graph, and the module that adapts it is reached
    // from main.js and guarded, so a 404 on the shared script degrades to no cards rather than a blank app.
    [Fact]
    public async Task App_LoadsThePane_AndGuardsIt()
    {
        var index = await _client.GetStringAsync("/");
        var pane = index.IndexOf("<script src=\"/shared/lib/artifactpane.js\"></script>", StringComparison.Ordinal);
        var main = index.IndexOf("<script type=\"module\" src=\"js/main.js\"></script>", StringComparison.Ordinal);
        Assert.True(pane >= 0 && pane < main, "index.html must load artifactpane.js before js/main.js");

        var adapter = await _client.GetStringAsync("/js/artifacts.js");
        Assert.Contains("const Pane = globalThis.Agent42ArtifactPane;", adapter);
        Assert.Contains("if (!Pane) return;", adapter);
        Assert.Contains("if (toolName !== \"create_artifact\" && toolName !== \"update_artifact\") return;", adapter);
    }

    // A reopened chat whose cards did not survive in history (ADR 0084: they vanish with their tool messages) must
    // still reach its artifacts: the topbar button opens the pane on its state-backed list, and opening a chat syncs
    // the button from the /artifacts list rather than from history.
    [Fact]
    public async Task ReopenedChat_ReachesThePane_WithoutACard()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Matches(@"<button[^>]*id=""artifactsBtn""[^>]*\shidden", index);

        var pane = await GetPaneAsync();
        Assert.Contains("return { renderCard, open, openList, close, setFull, isOpen: () => !pane.hidden };", pane);

        var adapter = await _client.GetStringAsync("/js/artifacts.js");
        var wire = StaticAssetsServerFixture.Body(adapter, "export function wireArtifacts(");
        Assert.Contains("artifactsBtn.addEventListener(\"click\"", wire);
        Assert.Contains("pane.openList();", wire);
        Assert.Contains("artifactsBtn.hidden = true;", StaticAssetsServerFixture.Body(adapter, "export function closeArtifactPane("));

        var conversations = await _client.GetStringAsync("/js/conversations.js");
        var select = StaticAssetsServerFixture.Body(conversations, "export async function selectConversation(");
        Assert.Contains("syncArtifactsButton(id);", select);
    }

    // The pane can go full screen and back (Escape steps out of it before closing), and a framed artifact is asked for
    // in the page's theme, so /frame can inject the app's look (ArtifactTheme). Both pages share the pane, so both
    // stylesheets carry the full-screen rule.
    [Fact]
    public async Task ArtifactPane_GoesFullScreen_AndFramesInTheTheme()
    {
        var pane = await GetPaneAsync();
        Assert.Contains("pane.classList.toggle(\"artifact-pane--full\", on);", StaticAssetsServerFixture.Body(pane, "function setFull("));
        Assert.Contains("setFull(false);", StaticAssetsServerFixture.Body(pane, "function close("));
        Assert.Contains("frame.src = `${urls.frame(record.id, version)}?theme=${frameTheme}`;",
            StaticAssetsServerFixture.Body(pane, "async function renderBody("));
        Assert.Contains("attributeFilter: [\"data-theme\"]", pane);

        foreach (var css in new[] { "/styles.css", "/shared/viewer.css" })
        {
            var sheet = await _client.GetStringAsync(css);
            Assert.Contains(".artifact-pane--full {", sheet);
            Assert.DoesNotContain("background: #fff;", StaticAssetsServerFixture.Body(sheet, ".artifact-pane__frame {"));
        }
    }
}
