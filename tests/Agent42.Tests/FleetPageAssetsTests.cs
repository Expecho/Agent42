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
public sealed partial class FleetPageAssetsTests(StaticAssetsServerFixture fixture)
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

    [Fact]
    public async Task IndexHtml_LinksToFleetPage()
    {
        var index = await _client.GetStringAsync("/index.html");

        var link = Regex.Match(index, "<a[^>]*href=\"/fleet/\"[^>]*>(?<body>.*?)</a>", RegexOptions.Singleline);
        Assert.True(link.Success, "index.html has no link to /fleet/");
        // Icon-only: the name is the tooltip and the accessible name.
        Assert.Contains("title=\"Agent42 agent fleet\"", link.Value);
        Assert.Contains("aria-label=\"Agent42 agent fleet\"", link.Value);
        var footer = index[index.IndexOf("sidebar__footer-actions", StringComparison.Ordinal)..];
        Assert.True(footer.IndexOf("href=\"/fleet/\"", StringComparison.Ordinal) < footer.IndexOf("</div>", StringComparison.Ordinal),
            "The fleet link belongs in .sidebar__footer-actions");
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

    [Fact]
    public async Task FleetPage_UsesThemeTokensAndSharedLibs()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var css = await _client.GetStringAsync("/fleet/fleet.css");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        // Agent42's tokens, copied as logs.css does, in both themes, and the no-FOUC resolve.
        Assert.Contains("--amber: #ffb13d;", css);
        Assert.Contains(":root[data-theme=\"light\"]", css);
        Assert.Contains("--serif: \"Newsreader\"", css);
        Assert.Contains("document.documentElement.dataset.theme", page);
        Assert.Contains("href=\"/\"", page);

        // The libraries come from /shared/lib, purify before marked and both before the page script.
        var purify = page.IndexOf("/shared/lib/purify.min.js", StringComparison.Ordinal);
        var marked = page.IndexOf("/shared/lib/marked.min.js", StringComparison.Ordinal);
        var own = page.IndexOf("src=\"fleet.js\"", StringComparison.Ordinal);
        Assert.True(purify >= 0 && marked >= 0 && purify < marked && marked < own, "purify, then marked, then fleet.js");
        Assert.DoesNotContain("cdn.jsdelivr", page);

        // Model text reaches innerHTML only through DOMPurify.
        foreach (Match assignment in InnerHtmlRegex().Matches(script))
        {
            Assert.Contains("DOMPurify.sanitize(", assignment.Value);
        }

        Assert.Single(InnerHtmlRegex().Matches(script));
    }

    [Fact]
    public async Task FleetPage_ShowsProviderGateAndSignInAgain()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("id=\"signInBanner\"", page);
        Assert.Contains("Sign in again", page);
        Assert.Contains("Add a model provider first", script);
        Assert.Contains("new EventSource(", script);
        Assert.Contains("/api/fleet/tasks/", script);
    }

    // A finished task can be deleted from the page. The button stays hidden while the task runs, since the server
    // answers 409 then, and the delete is confirmed first because it also removes the task's files.
    [Fact]
    public async Task FleetPage_DeletesAFinishedTask()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("id=\"deleteBtn\"", page);
        Assert.Contains("$(\"#deleteBtn\").hidden = !(t && t.status !== \"running\")", script);
        Assert.Contains("api(\"DELETE\", `/api/fleet/tasks/${encodeURIComponent(id)}`)", script);
        Assert.Contains("confirm(`Delete task #", script);
    }

    /// <summary>ADR 0099: a task follows the chat default unless the picker was changed, and a failed catalog never
    /// turns into "no tools". The picker offers only the fleet's keys, and MCP names are set as text.</summary>
    [Fact]
    public async Task FleetJs_ToolOverride_SentOnlyWhenChanged()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("const toolOverride = () => toolPicker.dirty && S.tools ? readToolPicker() : undefined;", script);
        Assert.Contains("enabledTools: enabledTools !== undefined ? enabledTools : toolOverride(),", script);
        Assert.Contains("const fleetToolKey = key => key === \"web_search\" || key.startsWith(\"mcp:\");", script);
        Assert.Contains("S.tools = tools.ok && profile.ok ? (tools.data || []).filter(t => fleetToolKey(t.key)) : null;", script);
        Assert.Contains("h(\"span\", { text: row.label })", script);
    }

    /// <summary>ADR 0099: Run again keeps the task's override (null included) instead of falling back to the picker.
    /// ADR 0101: it passes followUpOf when the task has one, else copyInputsOf, so a new task's files come along.</summary>
    [Fact]
    public async Task FleetJs_RunAgain_ResendsTaskOverride()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("submitTask(task.request, task.followUpOf || null, task.enabledTools ?? null, task.followUpOf ? null : task.id)", script);
        Assert.Contains("copyInputsOf: copyInputsOf || null", script);
    }

    /// <summary>ADR 0101: the page posts FormData to the files route only when files are attached, never forces a JSON
    /// content-type on it (the browser must set the multipart boundary), and sets file names as text.</summary>
    [Fact]
    public async Task FleetJs_Files_MultipartOnlyWithFiles()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("if (files.length > 0) {", script);
        Assert.Contains("r = await api(\"POST\", \"/api/fleet/tasks/with-files\", form);", script);
        Assert.Contains("r = await api(\"POST\", \"/api/fleet/tasks\", request);", script);
        Assert.Contains("headers: body === undefined || form ? {} : { \"content-type\": \"application/json\" }", script);
        Assert.Contains("h(\"span\", { class: \"name\", text: f.name })", script);
        Assert.Contains("const files = text === undefined ? S.attachments : [];", script);
    }

    /// <summary>Every event type the backend publishes has a handler on the page, so no part of a run goes unshown.</summary>
    [Fact]
    public async Task FleetJs_HandlesEveryPublishedEventType()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");
        var fleetDir = Path.Combine(FleetOptionsTests.RepoRoot(), "src", "Agent42.Backend", "Fleet");
        var published = Directory.EnumerateFiles(fleetDir, "*.cs")
            .SelectMany(file => PublishRegex().Matches(File.ReadAllText(file)).Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(published.Count >= 20, $"Found only {published.Count} published event types");
        foreach (var type in published)
        {
            Assert.True(script.Contains($"\"{type}\"(d", StringComparison.Ordinal) || script.Contains($"\"{type}\"()", StringComparison.Ordinal),
                $"fleet.js has no handler for the '{type}' event");
        }
    }

    /// <summary>ADR 0102: the canvas opens in the shared artifact pane, loaded before fleet.js and guarded so the page
    /// works without it; page names and titles are model-written, so they are set as text.</summary>
    [Fact]
    public async Task FleetPage_LoadsArtifactPane_Guarded()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        var pane = page.IndexOf("<script src=\"/shared/lib/artifactpane.js\"></script>", StringComparison.Ordinal);
        Assert.True(pane > 0, "The fleet page doesn't load the artifact pane");
        Assert.True(pane < page.IndexOf("<script src=\"fleet.js\"></script>", StringComparison.Ordinal), "artifactpane.js must load before fleet.js");
        Assert.Contains("if (!Pane) return null;", script);
        Assert.Contains("\"canvas.updated\"(d)", script);
        Assert.Contains("/canvas/${encodeURIComponent(name)}/versions/${version}/${route}", script);
        Assert.Contains("h(\"button\", { class: \"linkish\", type: \"button\", text: p.title", script);
    }

    [Fact]
    public async Task FleetCss_StylesTheArtifactPane()
    {
        var css = await _client.GetStringAsync("/fleet/fleet.css");

        Assert.Contains(".artifact-pane {", css);
        Assert.Contains("html.has-artifact-pane body", css);
        Assert.Contains(".artifact-pane--full", css);
        Assert.Contains(".artifact-pane__frame", css);
    }

    /// <summary>ADR 0115: a chat's deep link <c>/fleet/?task=&lt;id&gt;</c> opens that task, looked up among the user's
    /// own tasks, so an unknown id does nothing.</summary>
    [Fact]
    public async Task FleetJs_OpensTheTaskOfTheDeepLink()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("taskById(new URLSearchParams(location.search).get(\"task\"))", script);
        var linked = script.IndexOf("const linked = ", StringComparison.Ordinal);
        Assert.True(script.IndexOf("await loadTasks();", StringComparison.Ordinal) < linked, "the task list loads before the link is looked up");
        Assert.Contains("const live = linked || S.tasks.find(", script);
    }

    /// <summary>ADR 0115: an automatic answer is logged on the page, and the question (which quotes model-written
    /// commands and MCP server messages) is rendered as text, never as html.</summary>
    [Fact]
    public async Task FleetJs_RendersQuestionAutoAsText()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("\"question.auto\"(d) { S.autoAnswers.push(", script);
        var start = script.IndexOf("function autoAnswersCard()", StringComparison.Ordinal);
        var end = script.IndexOf("function taskFooter(", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "autoAnswersCard is defined before taskFooter");
        var card = script[start..end];
        Assert.Contains("text: a.question", card);
        Assert.DoesNotContain("innerHTML", card);
        Assert.DoesNotContain("setMarkdown", card);
        Assert.DoesNotContain("clampText", card);
        Assert.Contains("autoAnswersCard(),", script[end..]);
    }

    /// <summary>ADR 0116: the page offers a Details | Graph switch and starts on Details. The choice is a per-viewer
    /// preference, so storage is only touched inside try/catch.</summary>
    [Fact]
    public async Task FleetJs_OffersAGraphView_DefaultingToDetails()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("detailView: loadDetailView(),", script, StringComparison.Ordinal);
        Assert.Contains("=== \"graph\" ? \"graph\" : \"details\"; } catch { return \"details\"; }", script, StringComparison.Ordinal);
        Assert.Contains("try { localStorage.setItem(\"agent42.fleet.detailView\", view); } catch", script, StringComparison.Ordinal);
        Assert.Contains("[[\"details\", \"Details\"], [\"graph\", \"Graph\"]]", script, StringComparison.Ordinal);
        Assert.Contains("if (S.detailView === \"graph\" && S.rootTeamId) {", script, StringComparison.Ordinal);
    }

    /// <summary>ADR 0116: every state a node can be in has its own explicit rule, coloured by theme tokens only (no
    /// hex or rgb(); <c>color-mix</c> of tokens is fine).</summary>
    [Fact]
    public async Task FleetCss_StylesTheGraphNodesWithThemeTokens()
    {
        var css = await _client.GetStringAsync("/fleet/fleet.css");

        foreach (var state in new[] { "idle", "working", "done", "retry" })
        {
            Assert.Contains($".gnode.{state} {{", css, StringComparison.Ordinal);
        }

        Assert.Contains(".gnode.failed, .gnode.stopped, .gnode.interrupted {", css, StringComparison.Ordinal);
        var graphCss = css[css.IndexOf("graph view (ADR 0116)", StringComparison.Ordinal)..];
        Assert.DoesNotContain("rgb(", graphCss, StringComparison.Ordinal);
        Assert.False(HexColourRegex().IsMatch(graphCss), "The graph styles use a hard-coded hex colour instead of a theme token");
    }

    /// <summary>ADR 0116: node labels, team labels and snippets are model-written, so the graph builds its nodes as text.</summary>
    [Fact]
    public async Task FleetJs_GraphBuildsNodesAsText()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        var start = script.IndexOf("// ---------- graph (ADR 0116)", StringComparison.Ordinal);
        var end = script.IndexOf("// ---------- agent panel ----------", start, StringComparison.Ordinal);
        Assert.True(start >= 0 && end > start, "the graph section sits before the agent panel");
        var graph = script[start..end];
        Assert.DoesNotContain("innerHTML", graph);
        Assert.DoesNotContain("DOMPurify", graph);
        Assert.DoesNotContain("setMarkdown", graph);
        Assert.Contains("class: \"gnode-name\", text: label", graph);
        Assert.Contains("class: \"gcluster-head\", text: label", graph);
    }

    /// <summary>ADR 0116: a delegation remembers the attempt it was made in, so a retry's graph never shows an earlier
    /// attempt's sub-team under the same agent.</summary>
    [Fact]
    public async Task FleetJs_DelegationCallsRecordTheirAttempt()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("teamId: d.teamId, attempt: d.attempt, result: null", script, StringComparison.Ordinal);
        Assert.Contains("const key = `${c.teamId}|${c.attempt}|${(c.agent || \"\").toLowerCase()}`;", script, StringComparison.Ordinal);
        Assert.Contains("`${team.id}|${a.n}|${(name || \"\").toLowerCase()}`", script, StringComparison.Ordinal);
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

    /// <summary>ADR 0116: a node click opens the agent's output panel, a team click selects the team, like the tree does.</summary>
    [Fact]
    public async Task FleetJs_GraphNodesReuseThePanelAndSelect()
    {
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("onclick: clickable ? () => openPanel(m.id, m.attemptN, c.name) : null", script, StringComparison.Ordinal);
        Assert.Contains("else select(m.id); } });", script, StringComparison.Ordinal);
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

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b")]
    private static partial Regex HexColourRegex();

    [GeneratedRegex(@"\.innerHTML\s*=[^;]*;")]
    private static partial Regex InnerHtmlRegex();

    [GeneratedRegex("Publish\\(\"([a-z.]+)\"")]
    private static partial Regex PublishRegex();
}
