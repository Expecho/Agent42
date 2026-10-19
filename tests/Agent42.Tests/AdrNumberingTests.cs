using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the ADR numbering in <c>.agentic/decisions/</c>. Two branches that each take "the next free number"
/// both pass review on their own, and the collision only exists once both have merged: 0100 (#195 vs #196,
/// renumbered in #197) and 0102 (#198 vs #199, the Anthropic ADR renumbered to 0103). Every "ADR NNNN" in
/// code and docs is then ambiguous. This fails the PR that would land the second copy, as long as its CI
/// runs against a base that already holds the first.
/// </summary>
public sealed partial class AdrNumberingTests
{
    [GeneratedRegex(@"^(\d{4})-")]
    private static partial Regex NumberedFile();

    [GeneratedRegex(@"\A---\r?\n(?:.*\r?\n)*?id:\s*(\d{4})\s*$", RegexOptions.Multiline)]
    private static partial Regex FrontMatterId();

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

    private static IEnumerable<(string Name, string Number)> NumberedAdrs()
        => Directory.EnumerateFiles(DecisionsDir(), "*.md")
            .Select(Path.GetFileName)
            .Select(name => (Name: name!, Match: NumberedFile().Match(name!)))
            .Where(f => f.Match.Success)
            .Select(f => (f.Name, f.Match.Groups[1].Value));

    [Fact]
    public void Every_adr_number_is_used_once()
    {
        var duplicates = NumberedAdrs()
            .GroupBy(f => f.Number)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key}: {string.Join(", ", g.Select(f => f.Name))}")
            .ToList();

        Assert.True(
            duplicates.Count == 0,
            "ADR numbers collide; renumber the later one and every reference to it:\n" + string.Join("\n", duplicates));
    }

    [Fact]
    public void Every_adr_front_matter_id_matches_its_file_number()
    {
        var mismatches = NumberedAdrs()
            .Select(f => (f.Name, f.Number, Id: FrontMatterId().Match(File.ReadAllText(Path.Combine(DecisionsDir(), f.Name)))))
            .Where(f => !f.Id.Success || f.Id.Groups[1].Value != f.Number)
            .Select(f => $"{f.Name}: id {(f.Id.Success ? f.Id.Groups[1].Value : "missing")}")
            .ToList();

        Assert.True(mismatches.Count == 0, "ADR front-matter id differs from the file number:\n" + string.Join("\n", mismatches));
    }
}
