using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the SearXNG engine set in <c>infra/searxng/settings.yml</c> and the base tag in
/// <c>infra/searxng/Dockerfile</c> (ADR 0060). Nothing in this suite can run the sidecar, so this asserts
/// the <b>config content</b> against behaviour that was <i>measured</i> by running the image on
/// 2026-08-30, in the source-text style of <see cref="EasyAuthExclusionTests"/>.
///
/// <para>The bug it exists to stop from coming back: <c>keep_only</c> <b>filters</b> upstream's engine
/// list and enables nothing. It named Google, DuckDuckGo and Bing from ADR 0011 onward while upstream
/// shipped Google <c>inactive: true</c> and Bing <c>disabled: true</c> — so DuckDuckGo was the only engine
/// that ever answered, and <c>web_search</c> silently returned empty lists once it began serving CAPTCHAs
/// to the datacenter IP. Every doc, the yaml and the ADR all looked right throughout.</para>
/// </summary>
public sealed class SearxngEngineConfigTests
{
    /// <summary>
    /// The engines that must both survive <c>keep_only</c> and be switched on by the override. Google and
    /// Bing are the two upstream ships off; DuckDuckGo is on by default and needs no override entry.
    /// </summary>
    private static readonly string[] MustBeEnabled = ["google", "bing"];

    /// <summary>
    /// The first tag on which Google is reachable from <c>settings.yml</c> at all. Before it Google shipped
    /// <c>inactive: true</c>, which <c>load_engines</c> skips outright — measured on
    /// <c>2026.7.12-c19d86faa</c>: <c>/config</c> reported it enabled and every query still returned zero
    /// results, with no error. Any older tag would look like a working config and silently drop Google, so
    /// this compares ordering rather than inequality against one known-bad tag.
    /// </summary>
    private static readonly Version FirstTagGoogleIsReachableOn = new(2026, 8, 29);

    [Fact]
    public void Override_EnablesTheEnginesUpstreamShipsOff()
    {
        var engines = OverrideBlock();

        foreach (var engine in MustBeEnabled)
        {
            Assert.True(
                Regex.IsMatch(engines, $@"-\s*name:\s*{engine}\s*\n\s*disabled:\s*false"),
                $"'{engine}' is not switched on by the `engines:` override in settings.yml. `keep_only` "
                + "alone does NOT enable an engine — see ADR 0060; web_search falls back to DuckDuckGo only.");
        }
    }

    /// <summary>
    /// The two lists must agree. <c>keep_only</c> runs <b>first</b> and removes every engine it does not
    /// name, so an override entry for an engine missing from <c>keep_only</c> is dead config — the engine
    /// never reaches the merge.
    /// </summary>
    [Fact]
    public void EveryOverriddenEngine_SurvivesKeepOnly()
    {
        var kept = KeepOnly();

        foreach (var engine in MustBeEnabled)
        {
            Assert.Contains(engine, kept);
        }
    }

    /// <summary>
    /// Gotcha "SearXNG: JSON API 403 unless enabled": the JSON API is off by default, so <c>json</c> must stay under
    /// <c>search.formats</c> or every <c>web_search</c> call 403s. And the client sends no <c>safesearch</c>: a per-request
    /// value overrides <c>search.safe_search</c>, which would make that setting dead config.
    /// </summary>
    [Fact]
    public void JsonFormatIsOn_AndTheClientSendsNoSafeSearch()
    {
        var client = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Web", "SearxngWebSearch.cs"));
        var code = string.Join('\n', client.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        Assert.Matches(new Regex(@"\n\s*formats:\s*\n(\s*-\s*\w+\s*\n)*?\s*-\s*json\s*\n"), Settings());
        Assert.Matches(new Regex(@"\n\s*safe_search:\s*\d"), Settings());
        Assert.DoesNotContain("safesearch", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BaseTag_IsPinnedAndImmutable()
    {
        var tag = BaseTag();

        Assert.DoesNotContain("latest", tag, StringComparison.OrdinalIgnoreCase);
        Assert.True(
            Regex.IsMatch(tag, @"^\d{4}\.\d{1,2}\.\d{1,2}-[0-9a-f]{9,}$"),
            $"'{tag}' is not an immutable SearXNG date tag (yyyy.m.d-<sha>); rolling tags are never deployed.");
    }

    /// <summary>
    /// Pins the half of ADR 0060 that the override cannot express: an <c>inactive</c> engine is unreachable
    /// from <c>settings.yml</c> at any setting, so only the image bump made Google enableable at all.
    /// Ordering, not inequality — every tag before this one has the same defect, not just the one we came
    /// from.
    /// </summary>
    [Fact]
    public void BaseTag_IsAtLeastTheOneGoogleBecameReachableOn()
    {
        var date = BaseTagDate();

        Assert.True(
            date >= FirstTagGoogleIsReachableOn,
            $"Base image {date} predates {FirstTagGoogleIsReachableOn}, where upstream downgraded Google from "
            + "`inactive` to `disabled`. The `engines:` override cannot reach an `inactive` engine, so Google "
            + "would be silently absent while settings.yml still claimed to enable it (ADR 0060).");
    }

    /// <summary>The <c>yyyy.m.d</c> half of the tag, as a comparable value.</summary>
    private static Version BaseTagDate()
    {
        var match = Regex.Match(BaseTag(), @"^(\d{4})\.(\d{1,2})\.(\d{1,2})-");
        Assert.True(match.Success, $"Cannot read a date out of base tag '{BaseTag()}'.");
        return new Version(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value),
            int.Parse(match.Groups[3].Value));
    }

    /// <summary>
    /// Reads the top-level <c>engines:</c> override block — everything from the <c>engines:</c> key to the
    /// next top-level key. Deliberately not a YAML parse: this asserts the file a human edits.
    /// </summary>
    private static string OverrideBlock()
    {
        var yaml = Settings();
        var start = yaml.IndexOf("\nengines:", StringComparison.Ordinal);
        Assert.True(start >= 0, "No top-level `engines:` override block in infra/searxng/settings.yml (ADR 0060).");

        var rest = yaml[(start + 1)..];
        var end = Regex.Match(rest[1..], @"\n(?=\S)");
        return end.Success ? rest[..(end.Index + 1)] : rest;
    }

    /// <summary>Reads the engine names listed under <c>use_default_settings.engines.keep_only</c>.</summary>
    private static IReadOnlyList<string> KeepOnly()
    {
        var yaml = Settings();
        var start = yaml.IndexOf("keep_only:", StringComparison.Ordinal);
        Assert.True(start >= 0, "No keep_only list in infra/searxng/settings.yml");

        var rest = yaml[(start + "keep_only:".Length)..];
        var names = Regex
            .Matches(rest, @"\G\s*\n\s+-\s*(\w+)")
            .Select(match => match.Groups[1].Value)
            .ToList();

        Assert.NotEmpty(names);
        return names;
    }

    /// <summary>Reads the tag off the <c>FROM searxng/searxng:...</c> line.</summary>
    private static string BaseTag()
    {
        var dockerfile = File.ReadAllText(Path.Combine(SearxngDir(), "Dockerfile"));
        var match = Regex.Match(dockerfile, @"^FROM\s+searxng/searxng:(\S+)", RegexOptions.Multiline);
        Assert.True(match.Success, "No `FROM searxng/searxng:<tag>` line in infra/searxng/Dockerfile");
        return match.Groups[1].Value;
    }

    private static string Settings() => File.ReadAllText(Path.Combine(SearxngDir(), "settings.yml"));

    private static string SearxngDir() => Path.Combine(RepoRoot(), "infra", "searxng");

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
