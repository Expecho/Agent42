using System.Net;
using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the agent fleet page (ADR 0097), served through the shared <see cref="StaticAssetsServerFixture"/> and
/// asserted on its source text, like <see cref="LogsConsoleAssetsTests"/> (the repo has no JS runner by design).
///
/// <para><b>This class must never use <c>GetAppScriptAsync()</c>.</b> <c>wwwroot/fleet/</c> is a single classic
/// script outside the <c>main.js</c> module graph, so every other source-text guard is blind to it.</para>
/// </summary>
[Collection("StaticAssets")]
public sealed class FleetPageAssetsTests(StaticAssetsServerFixture fixture)
{
    private readonly HttpClient _client = fixture.Client;

    [Fact]
    public async Task FleetPage_AndAssets_AreServed()
    {
        var bare = await _client.GetAsync("/fleet");
        Assert.Equal(HttpStatusCode.MovedPermanently, bare.StatusCode);

        foreach (var (path, type) in new[] { ("/fleet/", "text/html"), ("/fleet/fleet.css", "text/css"), ("/fleet/fleet.js", "text/javascript") })
        {
            var response = await _client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(type, response.Content.Headers.ContentType?.MediaType);
        }
    }

    // The page writes (POST/PUT/DELETE). Under no-referrer the browser sends `Origin: null` on those, and Easy Auth
    // answers with a bare 403 that never reaches the app, so nothing is logged.
    [Fact]
    public async Task FleetPage_ReferrerPolicy_KeepsTheOriginOnWrites()
    {
        var page = await _client.GetStringAsync("/fleet/");

        var policy = Regex.Match(page, "<meta\\s+name=\"referrer\"\\s+content=\"(?<value>[^\"]+)\"");
        Assert.True(policy.Success, "The fleet page has no referrer meta");
        Assert.Equal("same-origin", policy.Groups["value"].Value);
    }

    // AC13 (ADR 0104): the budget metrics show the cached input the budget event now carries.
    [Fact]
    public async Task FleetPage_ShowsCachedInputTokens()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("<b id=\"mCached\">0</b> cached", page, StringComparison.Ordinal);
        Assert.Contains("$(\"#mCached\").textContent = nf.format(b?.cachedInputTokens || 0);", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0118: <c>renderPrompt</c> writes the banner label by id, so a page without it would throw on the first
    /// question; and the composer must send the flag and handle both events or the interview never shows.</summary>
    [Fact]
    public async Task FleetPage_HasClarifyCheckbox_AndHandlers()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("id=\"promptLabel\"", page, StringComparison.Ordinal);
        Assert.Contains("clarify: clarify !== undefined ? clarify : composer.clarify.checked,", script, StringComparison.Ordinal);
        Assert.Contains("\"clarify.started\"()", script, StringComparison.Ordinal);
        Assert.Contains("\"clarify.done\"(d)", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0119: the composer sends the self-review flag (off by default, remembered per viewer), and Run again
    /// repeats the task's own flag, or a rerun would silently drop it.</summary>
    [Fact]
    public async Task FleetPage_SendsSelfReview_AndRunAgainRepeatsIt()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("selfReview: selfReview !== undefined ? selfReview : composer.selfReview.checked,", script, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem(\"agent42.fleet.selfReview\") === \"on\"", script, StringComparison.Ordinal);
        Assert.Contains("!!task.clarify, !!task.selfReview)", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0120: the page has a Share button and dialog, whose warning names what a link exposes, and the script
    /// talks to the owner's share route (never a public one).</summary>
    [Fact]
    public async Task FleetPage_HasShareButtonAndDialog_WiredToTheOwnerRoute()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("id=\"shareBtn\"", page, StringComparison.Ordinal);
        Assert.Contains("<dialog id=\"shareDialog\"", page, StringComparison.Ordinal);
        Assert.Contains("every file in its workspace", page, StringComparison.Ordinal);
        Assert.Contains("without signing in", page, StringComparison.Ordinal);
        Assert.Contains("/api/fleet/tasks/${encodeURIComponent(id)}/share", script, StringComparison.Ordinal);
        Assert.Contains("/api/fleet/tasks/${encodeURIComponent(shareTaskId)}/share", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/share/fleet", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0120 (ADR 0047's rule): the link is rendered BEFORE the OS sheet opens, and the sheet is gated on a pure
    /// feature detect. Without the ordering a blocked sheet leaves the user without the link.</summary>
    [Fact]
    public async Task FleetShare_RendersTheLinkBeforeTheSheet_PureFeatureDetect()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        var start = script.IndexOf("async function createShare()", StringComparison.Ordinal);
        Assert.True(start > 0, "createShare is missing");
        var body = script[start..script.IndexOf("async function revokeShare()", start, StringComparison.Ordinal)];
        var render = body.IndexOf("renderShareState(", StringComparison.Ordinal);
        var share = body.IndexOf("nativeShare()", StringComparison.Ordinal);
        Assert.True(render >= 0 && share > render, "createShare must render the link BEFORE handing it to the sheet");

        Assert.Contains("typeof navigator.share === \"function\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("userAgent", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0116: a node a stop cut off takes the team's state, and a delegation from a non-node agent is
    /// adopted instead of dropped.</summary>
    [Fact]
    public async Task FleetJs_GraphKeepsCutOffNodesAndOrphanSubTeams()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("if (!t.done) t.cut = true; t.done = true;", script, StringComparison.Ordinal);
        Assert.Contains("if (last.cut && [\"stopped\", \"interrupted\", \"failed\"].includes(team.status)) return team.status;", script, StringComparison.Ordinal);
        Assert.Contains("adoptOrphans(m, team, a, subs);", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0116: /fleet is not in the service worker's SHELL, so a change to it needs no CACHE bump.</summary>
    [Fact]
    public async Task ServiceWorker_LeavesFleetOutOfTheShell()
    {
        var sw = await _client.GetStringAsync("/sw.js");

        Assert.Contains("url.pathname.startsWith(\"/fleet\")", sw, StringComparison.Ordinal);
        var shellStart = sw.IndexOf("const SHELL", StringComparison.Ordinal);
        var shell = sw[shellStart..sw.IndexOf("];", shellStart, StringComparison.Ordinal)];
        Assert.DoesNotContain("/fleet", shell, StringComparison.Ordinal);
    }

    /// <summary>ADR 0117: the page folds <c>todos.updated</c> (replacing the list) and renders agent-written titles as
    /// text, never markup.</summary>
    [Fact]
    public async Task TodosHandler_Exists_NoInnerHtml()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("\"todos.updated\"(d) { const a = attemptOf(d); if (a) a.todos = d.todos || []; },", script, StringComparison.Ordinal);
        var start = script.IndexOf("function renderTodos(", StringComparison.Ordinal);
        Assert.True(start > 0, "renderTodos is missing");
        var body = script[start..script.IndexOf("function notesList(", start, StringComparison.Ordinal)];
        Assert.DoesNotContain("innerHTML", body, StringComparison.Ordinal);
        Assert.Contains("h(\"span\", { text: t.title })", body, StringComparison.Ordinal);
    }
}
