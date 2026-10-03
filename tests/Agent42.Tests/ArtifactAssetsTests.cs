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
}
