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
        Assert.Contains("Fleet", link.Groups["body"].Value);
        Assert.Contains("title=\"Agent42 agent fleet\"", link.Value);
        var footer = index[index.IndexOf("sidebar__footer-actions", StringComparison.Ordinal)..];
        Assert.True(footer.IndexOf("href=\"/fleet/\"", StringComparison.Ordinal) < footer.IndexOf("</div>", StringComparison.Ordinal),
            "The fleet link belongs in .sidebar__footer-actions");
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

    [GeneratedRegex(@"\.innerHTML\s*=[^;]*;")]
    private static partial Regex InnerHtmlRegex();

    [GeneratedRegex("Publish\\(\"([a-z.]+)\"")]
    private static partial Regex PublishRegex();
}
