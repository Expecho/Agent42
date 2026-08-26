using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the Easy Auth carve-out list in <c>infra/modules/container-app.bicep</c> (ADR 0002/0022/0035,
/// corrected by ADR 0042). Nothing in this suite can exercise Easy Auth itself — TestServer has no ingress —
/// so this asserts the <b>Bicep content</b> against the matching semantics that were <i>measured</i> against
/// the live app on 2026-08-20, in the source-text style of the <c>*AssetsTests</c>.
///
/// <para>The bug it exists to stop from coming back: <c>excludedPaths</c> matches a path <b>exactly</b>
/// unless the entry ends in <c>/*</c>. Shipping the bare prefixes <c>/api/share</c> + <c>/shared</c> gated
/// every URL a recipient actually loads behind the Entra login page, making ADR 0035 non-functional in
/// production while every test, the Bicep and the ADR all looked right.</para>
/// </summary>
public sealed class EasyAuthExclusionTests
{
    /// <summary>Shape of a real share token (ADR 0035): the <c>shr_</c> prefix plus a base64url body.</summary>
    private const string SampleToken = "shr_LMh3xg8k1QzR0pV7bYtN2sWfA6eJcD9uKmT4iXoZbQ1";

    /// <summary>The list the app shipped with, kept so the matcher can be proven to reject it.</summary>
    private static readonly string[] OldBrokenList = ["/api/agent-run", "/api/share", "/shared"];

    /// <summary>
    /// The measured matcher. An entry ending in <c>/*</c> is a prefix match that <b>keeps the trailing
    /// slash</b> (<c>entry[..^1]</c>, not <c>entry[..^2]</c>) and spans any number of further segments,
    /// including an empty remainder; every other entry matches exactly. Stripping the slash would be wrong
    /// and would still pass every positive assertion below — which is what
    /// <see cref="WildcardEntry_DoesNotMatchANameStem"/> is for.
    /// </summary>
    private static bool IsExcluded(IReadOnlyList<string> entries, string path) =>
        entries.Any(entry => entry.EndsWith("/*", StringComparison.Ordinal)
            ? path.StartsWith(entry[..^1], StringComparison.OrdinalIgnoreCase)
            : path.Equals(entry, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Exclusions_CoverTheViewerPage()
    {
        var entries = ExcludedPaths();

        // The trailing-slash URL is the one a recipient's browser actually lands on; the bare form only
        // needs to reach the app far enough to be 301'd to it.
        Assert.True(IsExcluded(entries, "/shared/"), Because("/shared/", entries));
        Assert.True(IsExcluded(entries, "/shared"), Because("/shared", entries));
    }

    [Fact]
    public void Exclusions_CoverEverySameOriginUrlTheViewerRequests()
    {
        var entries = ExcludedPaths();
        var urls = ViewerRequestedUrls();

        // A tautology guard: if the extraction silently found nothing, the loop below would pass vacuously.
        Assert.True(urls.Count >= 5, $"Parsed only {urls.Count} viewer URLs — the extraction broke.");

        foreach (var url in urls)
        {
            Assert.True(IsExcluded(entries, url), Because(url, entries));
        }
    }

    [Fact]
    public void Exclusions_CoverBothPublicShareRoutes()
    {
        var entries = ExcludedPaths();

        Assert.True(IsExcluded(entries, $"/api/share/{SampleToken}"), Because("the token route", entries));
        Assert.True(
            IsExcluded(entries, $"/api/share/{SampleToken}/attachments/att-1"),
            Because("the multi-segment attachment route", entries));
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/index.html")]
    [InlineData("/api/models")]
    [InlineData("/api/conversations")]
    [InlineData("/api/conversations/x/share")]
    [InlineData("/api/conversations/x/pin")]
    [InlineData("/api/providers")]
    [InlineData("/api/usage")]
    [InlineData("/api/profile")]
    [InlineData("/api/mcp/oauth/start")]
    // AC12 (ADR 0053). A Google OAuth route reachable outside Easy Auth would make GetUserId() return
    // the local-dev principal and bind every user's calendar tokens to one phantom identity — so all
    // three are named here one at a time, because that is how this theory proves gating at all.
    [InlineData("/api/google/status")]
    [InlineData("/api/google/oauth/start")]
    [InlineData("/api/google/oauth/callback")]
    public void Exclusions_DoNotCoverAnyAuthenticatedPath(string path)
    {
        var entries = ExcludedPaths();

        // The assertion that stops a future widening (e.g. '/api/*') from silently un-gating the whole app.
        Assert.False(IsExcluded(entries, path), $"'{path}' is excluded from Easy Auth: [{string.Join(", ", entries)}]");
    }

    [Fact]
    public void Exclusions_StillCoverAgentRun()
    {
        var entries = ExcludedPaths();

        // ADR 0022's carve-out must survive every edit to this list.
        Assert.True(IsExcluded(entries, "/api/agent-run"), Because("/api/agent-run", entries));
    }

    [Fact]
    public void BarePrefixEntry_DoesNotCoverSubPaths()
    {
        // Pins the bug itself, not just the fix: feed the matcher the list that shipped and watch every URL
        // a recipient actually loads fall through to the login page.
        Assert.False(IsExcluded(OldBrokenList, "/shared/"));
        Assert.False(IsExcluded(OldBrokenList, "/shared/viewer.js"));
        Assert.False(IsExcluded(OldBrokenList, $"/api/share/{SampleToken}"));

        // ...while the bare entries themselves still matched, which is why it looked correct.
        Assert.True(IsExcluded(OldBrokenList, "/shared"));
        Assert.True(IsExcluded(OldBrokenList, "/api/agent-run"));
    }

    [Theory]
    [InlineData("/api/sharezzz")]
    [InlineData("/sharedzzz")]
    [InlineData("/shared-secrets")]
    public void WildcardEntry_DoesNotMatchANameStem(string path)
    {
        // Measured: the wildcard prefix includes the trailing slash, so it cannot bleed onto a sibling route
        // that merely shares a name stem.
        Assert.False(IsExcluded(ExcludedPaths(), path), $"'{path}' leaked through the wildcard.");
    }

    private static string Because(string what, IReadOnlyList<string> entries) =>
        $"{what} is NOT carved out of Easy Auth by [{string.Join(", ", entries)}] — recipients get the login page.";

    /// <summary>Reads the literal <c>excludedPaths</c> array out of the Bicep module.</summary>
    private static IReadOnlyList<string> ExcludedPaths()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", "container-app.bicep"));
        var start = bicep.IndexOf("excludedPaths: [", StringComparison.Ordinal);
        Assert.True(start >= 0, "No excludedPaths array found in container-app.bicep");

        var end = bicep.IndexOf(']', start);
        Assert.True(end > start, "Unterminated excludedPaths array in container-app.bicep");

        var entries = Regex
            .Matches(bicep[start..end], "'([^']+)'")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(entries);
        return entries;
    }

    /// <summary>
    /// Every same-origin URL the public viewer actually requests, resolved against <c>/shared/</c>: the
    /// subresources in its markup plus the API templates in <c>viewer.js</c>. Deliberately NOT "every file
    /// under wwwroot/shared/" — <c>/shared/*</c> covers that tautologically. The failure this catches is the
    /// one the markup comment warns about: someone points the viewer at <c>/lib/marked.min.js</c> or
    /// <c>/styles.css</c> on the authenticated surface and recipients get a 401 again.
    /// </summary>
    private static IReadOnlyList<string> ViewerRequestedUrls()
    {
        var dir = Path.Combine(RepoRoot(), "src", "Agent42.Backend", "wwwroot", "shared");

        var markup = Regex
            .Matches(File.ReadAllText(Path.Combine(dir, "index.html")), "(?:src|href)\\s*=\\s*\"([^\"]+)\"")
            .Select(match => match.Groups[1].Value)
            .Where(IsSameOrigin);

        var scripted = Regex
            .Matches(File.ReadAllText(Path.Combine(dir, "viewer.js")), "[\"'`](/api/[^\"'`]*)[\"'`]")
            .Select(match => Regex.Replace(match.Groups[1].Value, @"\$\{[^{}]*\}", SampleToken))
            .Where(IsSameOrigin);

        return markup.Concat(scripted).Select(ResolveAgainstViewer).Distinct().ToList();
    }

    private static bool IsSameOrigin(string url) =>
        !url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("http", StringComparison.OrdinalIgnoreCase)
        && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith('#');

    /// <summary>
    /// The viewer is served from <c>/shared/</c>, so a relative subresource resolves under it. Resolved
    /// through <see cref="Uri"/> rather than by concatenation so <b>dot-segments collapse</b>: a
    /// <c>../lib/marked.min.js</c> would otherwise stay the literal <c>/shared/../lib/marked.min.js</c>,
    /// which starts with <c>/shared/</c> and would be reported covered while the browser actually requests
    /// the gated <c>/lib/…</c> and gets a 401.
    /// </summary>
    private static string ResolveAgainstViewer(string url) =>
        new Uri(new Uri("https://placeholder.invalid/shared/"), url).AbsolutePath;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        return dir!.FullName;
    }
}
