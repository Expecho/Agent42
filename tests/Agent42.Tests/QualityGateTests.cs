using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the quality gates themselves (ADR 0050). The rule this file enforces is the one that made
/// the previous gate file misleading: <b>every gate must have a measuring tool in this repo.</b> A
/// threshold nothing measures cannot fail, so it is not a gate — it is a wish that reads like one, and
/// three such entries (<c>new_issues</c>, <c>new_security_issues</c>, <c>duplication_pct_max</c>) were
/// removed rather than estimated against.
///
/// <para>These are deliberately assertions about <b>configuration</b>, not behaviour. Two numbers that
/// must agree live in two files that cannot see each other — <c>quality-gates.yaml</c> and the
/// SonarAnalyzer default — and hard rule 3 says a threshold lives in exactly one place. Since nothing
/// at runtime reads both, a test is the only thing that can notice them drifting apart.</para>
/// </summary>
public sealed class QualityGateTests
{
    private static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Agent42.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(dir);
        return dir!;
    }

    private static string Read(string relativePath)
        => File.ReadAllText(Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

    private static string Gates() => Read(".agentic/quality-gates.yaml");

    /// <summary>Every gate key, paired with the file that proves something measures it.</summary>
    public static TheoryData<string, string> GatesAndTheirTooling() => new()
    {
        { "coverage_new_code_pct_min", ".agentic/scripts/check-new-code-coverage.mjs" },
        { "cognitive_complexity_max", "Directory.Packages.props" },
    };

    // Each gate is declared AND something in the repo measures it.
    [Theory]
    [MemberData(nameof(GatesAndTheirTooling))]
    public void EveryGate_HasAMeasuringToolInThisRepo(string gateKey, string tool)
    {
        // (?m) inline, not RegexOptions.Multiline: Assert.Matches takes only a pattern, so without it
        // `^` anchors to the start of the whole file and every gate looks undeclared.
        Assert.Matches($@"(?m)^\s*{Regex.Escape(gateKey)}:\s*\d", Gates());
        Assert.True(
            File.Exists(Path.Combine(RepoRoot(), tool.Replace('/', Path.DirectorySeparatorChar))),
            $"Gate '{gateKey}' names {tool} as its measuring tool, but that file does not exist.");
    }

    // The other direction, and the one that actually bites: a gate added later with nothing to measure
    // it is exactly the failure this ADR exists to correct, and it would otherwise pass unnoticed
    // because an unmeasurable gate never fails.
    [Fact]
    public void NoGateExists_WithoutAnEntryInTheToolingMap()
    {
        var gatesBlock = Regex.Match(Gates(), @"^gates:\s*$(.*?)^\w", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(gatesBlock.Success, "the 'gates:' block was not found in quality-gates.yaml");

        var declared = Regex.Matches(gatesBlock.Groups[1].Value, @"^\s{2}(\w+):\s*\d", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);
        var mapped = GatesAndTheirTooling().Select(row => (string)row[0]!).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(mapped.Order(), declared.Order());
    }

    // Hard rule 3: a threshold lives in ONE place. S3776's threshold is settable only via a
    // SonarLint.xml AdditionalFile (`.editorconfig` is silently ignored for it — measured on
    // SonarAnalyzer.CSharp 10.33.0.1635), and the analyzer's built-in default is 15. So as long as the
    // gate says 15 and no SonarLint.xml exists, there is exactly one number and it is the enforced one.
    // Change the gate without adding that file and the build would keep enforcing 15 while the yaml
    // claimed otherwise — silently, since nothing else compares them.
    [Fact]
    public void CognitiveComplexityGate_MatchesWhatTheAnalyzerActuallyEnforces()
    {
        var declared = int.Parse(
            Regex.Match(Gates(), @"^\s*cognitive_complexity_max:\s*(\d+)", RegexOptions.Multiline).Groups[1].Value);
        var hasSonarLintXml = Directory
            .EnumerateFiles(RepoRoot(), "SonarLint.xml", SearchOption.AllDirectories)
            .Any(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

        Assert.True(
            declared == 15 || hasSonarLintXml,
            $"cognitive_complexity_max is {declared}, but S3776's default is 15 and no SonarLint.xml overrides it — "
            + "the build would still enforce 15. Add a SonarLint.xml AdditionalFile, or restore the gate to 15.");
    }

    // The rule is adopted at all. Without an explicit severity S3776 ships as `info`, which no build
    // surfaces and no CI reads — the gate would look wired up while measuring nothing. That is not a
    // hypothetical: the first baseline taken for ADR 0050 counted zero violations for exactly this
    // reason, and there were eleven.
    [Fact]
    public void S3776_IsExplicitlyEnabled_AndTheAnalyzerIsReferenced()
    {
        Assert.Contains("dotnet_diagnostic.S3776.severity = warning", Read(".editorconfig"), StringComparison.Ordinal);
        Assert.Contains("SonarAnalyzer.CSharp", Read("Directory.Packages.props"), StringComparison.Ordinal);
    }

    // Only ONE Sonar rule is adopted (see .editorconfig for the measurement behind that). The blanket
    // default-off is what keeps it that way; delete it and ~300 warnings return, most of them firing on
    // this repo's deliberate idioms rather than on defects.
    [Fact]
    public void OnlyTheAdoptedSonarRule_IsEnabled()
    {
        var editorconfig = Read(".editorconfig");

        Assert.Contains("dotnet_analyzer_diagnostic.severity = none", editorconfig, StringComparison.Ordinal);

        var enabled = Regex.Matches(editorconfig, @"^dotnet_diagnostic\.(S\d+)\.severity\s*=\s*(\w+)", RegexOptions.Multiline)
            .Where(m => m.Groups[2].Value != "none")
            .Select(m => m.Groups[1].Value)
            .ToList();

        Assert.Equal(["S3776"], enabled);
    }

    // The format gate and the complexity gate collide, and the collision is invisible until CI.
    // `dotnet format` runs an `analyzers` pass and exits 2 on ANY analyzer diagnostic at warning
    // severity — so keeping S3776 visible at `warning` makes the FORMAT job fail on the eleven
    // recorded violations, a gate that has nothing to say about them. CI therefore excludes S3776
    // there specifically, and CLAUDE.md's Commands table must teach the same command or every local
    // lint run disagrees with CI.
    //
    // This is pinned because it is a two-file agreement that nothing else checks, and because the
    // tempting "fix" is to drop S3776 back to `info` — which silences it everywhere and returns the
    // gate to measuring nothing.
    [Fact]
    public void FormatGate_ExcludesTheRecordedComplexityBacklog()
    {
        Assert.Contains("--exclude-diagnostics S3776", Read(".github/workflows/pr-checks.yml"), StringComparison.Ordinal);
        Assert.Contains("--exclude-diagnostics S3776", Read("CLAUDE.md"), StringComparison.Ordinal);
    }

    // CI must actually run the coverage gate. A script in .agentic/scripts/ that no workflow invokes is
    // the same failure in a new costume — it looks like enforcement and enforces nothing.
    [Fact]
    public void NewCodeCoverageGate_IsInvokedByCi()
    {
        var workflow = Read(".github/workflows/pr-checks.yml");

        Assert.Contains("check-new-code-coverage.mjs", workflow, StringComparison.Ordinal);
        // The gate diffs against the merge base, so a shallow checkout would silently gate nothing.
        Assert.Contains("fetch-depth: 0", workflow, StringComparison.Ordinal);
    }
}
