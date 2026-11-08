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

    // AC13 (ADR 0104): the budget metrics show the cached input the budget event now carries.
    [Fact]
    public async Task FleetPage_ShowsCachedInputTokens()
    {
        var page = await _client.GetStringAsync("/fleet/");
        var script = await _client.GetStringAsync("/fleet/fleet.js");

        Assert.Contains("<b id=\"mCached\">0</b> cached", page, StringComparison.Ordinal);
        Assert.Contains("$(\"#mCached\").textContent = nf.format(b?.cachedInputTokens || 0);", script, StringComparison.Ordinal);
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

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b")]
    private static partial Regex HexColourRegex();

    [GeneratedRegex(@"\.innerHTML\s*=[^;]*;")]
    private static partial Regex InnerHtmlRegex();

    [GeneratedRegex("Publish\\(\"([a-z.]+)\"")]
    private static partial Regex PublishRegex();
}
