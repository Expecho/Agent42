using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the links between ADRs in <c>.agentic/decisions/</c> (audit 2026-11-24, which found ~150 missing back-links).
/// An ADR that <c>amends</c> (or partially <c>supersedes</c>) an accepted one must be listed in that one's <c>amended_by</c>,
/// so reading the older decision tells you it has moved. Link keys are YAML lists; prose goes after a <c>#</c>. An accepted
/// ADR never carries <c>superseded_by</c> (README: the active scan is <c>grep -L "^superseded_by:"</c>).
/// </summary>
public sealed partial class AdrLinkTests
{
    [GeneratedRegex(@"^(\d{4})-")]
    private static partial Regex NumberedFile();

    [GeneratedRegex(@"^(supersedes|superseded_by|amends|amended_by|extends|extended_by):[ \t]*([^#\n]*)", RegexOptions.Multiline)]
    private static partial Regex LinkLine();

    [GeneratedRegex(@"\b\d{4}\b")]
    private static partial Regex AdrId();

    [GeneratedRegex(@"^\[[\d, ]*\]$")]
    private static partial Regex YamlIdList();

    private sealed record Adr(string File, string Status, IReadOnlyDictionary<string, (string Raw, IReadOnlyList<string> Ids)> Links);

    private static string DecisionsDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Agent42.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return Path.Combine(dir!, ".agentic", "decisions");
    }

    private static Dictionary<string, Adr> Adrs()
    {
        var adrs = new Dictionary<string, Adr>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(DecisionsDir(), "*.md"))
        {
            var name = Path.GetFileName(path);
            if (NumberedFile().Match(name) is not { Success: true } number || number.Groups[1].Value == "0000")
            {
                continue;
            }

            var lines = File.ReadAllLines(path);
            var end = Array.IndexOf(lines, "---", 1);
            var frontMatter = string.Join('\n', lines.Take(end).Skip(1));
            var status = lines.Take(end).FirstOrDefault(l => l.StartsWith("status:", StringComparison.Ordinal))?[7..].Trim() ?? "";
            var links = LinkLine().Matches(frontMatter).ToDictionary(
                m => m.Groups[1].Value,
                m => (Raw: m.Groups[2].Value.Trim(), Ids: (IReadOnlyList<string>)[.. AdrId().Matches(m.Groups[2].Value).Select(i => i.Value)]));
            adrs[number.Groups[1].Value] = new Adr(name, status, links);
        }

        return adrs;
    }

    [Fact]
    public void Amending_adrs_are_listed_in_the_amended_adrs_amended_by()
    {
        var adrs = Adrs();
        var missing = new List<string>();
        foreach (var (id, adr) in adrs)
        {
            var targets = new[] { "amends", "supersedes" }.SelectMany(k => adr.Links.TryGetValue(k, out var l) ? l.Ids : []);
            foreach (var target in targets)
            {
                if (adrs.TryGetValue(target, out var older) && older.Status == "accepted"
                    && !(older.Links.TryGetValue("amended_by", out var back) && back.Ids.Contains(id)))
                {
                    missing.Add($"{older.File}: add {id} to amended_by ({adr.File} amends it)");
                }
            }
        }

        Assert.True(missing.Count == 0, "Missing ADR back-links:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void Adr_link_keys_are_yaml_lists_and_accepted_adrs_are_not_superseded()
    {
        var problems = Adrs().Values
            .SelectMany(adr => adr.Links
                .Where(l => !(l.Key == "superseded_by" && adr.Status == "superseded"))
                .Where(l => !YamlIdList().IsMatch(l.Value.Raw))
                .Select(l => $"{adr.File}: '{l.Key}' must be a list like [0001, 0002], prose after '#'")
                .Concat(adr.Status == "accepted" && adr.Links.ContainsKey("superseded_by")
                    ? [$"{adr.File}: accepted but has superseded_by; a partial supersession is amended_by"]
                    : []))
            .ToList();

        Assert.True(problems.Count == 0, string.Join("\n", problems));
    }
}
