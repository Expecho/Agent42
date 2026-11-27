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
        { "method_lines_max", "Directory.Packages.props" },
        { "method_params_max", "Directory.Packages.props" },
        { "e2e_failing_tests_max", "tests/e2e/playwright.config.mjs" },
    };

    /// <summary>Each analyzer-enforced gate: its yaml key, its Sonar rule, the rule's parameter name and built-in default.</summary>
    public static TheoryData<string, string, string, int> AnalyzerGates() => new()
    {
        { "cognitive_complexity_max", "S3776", "threshold", 15 },
        { "method_lines_max", "S138", "max", 80 },
        { "method_params_max", "S107", "max", 7 },
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

    // Hard rule 3: a threshold lives in ONE place. Each rule's threshold is settable only via a SonarLint.xml
    // AdditionalFile (`.editorconfig` is silently ignored for it — measured), and the analyzer's built-in default
    // is the number in the table above. So either the gate equals that default and no SonarLint.xml overrides the
    // rule, or a SonarLint.xml carries the SAME number for THAT rule. Change the gate without the file and the build
    // would keep enforcing the default while the yaml claimed otherwise — silently, since nothing else compares them.
    // A SonarLint.xml for one rule must not excuse another, so the file is parsed per rule, not merely detected.
    [Theory]
    [MemberData(nameof(AnalyzerGates))]
    public void AnalyzerGates_MatchWhatTheAnalyzerActuallyEnforces(string gateKey, string rule, string parameter, int builtInDefault)
    {
        var declared = int.Parse(
            Regex.Match(Gates(), $@"^\s*{gateKey}:\s*(\d+)", RegexOptions.Multiline).Groups[1].Value);
        var configured = Directory
            .EnumerateFiles(RepoRoot(), "SonarLint.xml", SearchOption.AllDirectories)
            .Where(p => !p.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !p.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(p => Regex.Match(
                File.ReadAllText(p),
                $@"<Key>{rule}</Key>\s*<Parameters>\s*<Parameter>\s*<Key>{parameter}</Key>\s*<Value>(\d+)</Value>",
                RegexOptions.Singleline))
            .Where(m => m.Success)
            .Select(m => int.Parse(m.Groups[1].Value))
            .ToList();

        var enforced = configured.Count > 0 ? configured[0] : builtInDefault;
        Assert.True(
            declared == enforced && configured.Count <= 1,
            $"{gateKey} is {declared}, but the build enforces {enforced} for {rule} "
            + $"(built-in default {builtInDefault}, {configured.Count} SonarLint.xml override(s)). "
            + "Add or fix the SonarLint.xml AdditionalFile for this rule, or restore the gate.");
    }

    // The rule is adopted, and adopted as BLOCKING. Two ways this silently stops gating anything:
    // without an explicit severity S3776 ships as `info`, which no build surfaces (the first ADR 0050
    // baseline counted zero violations for exactly that reason, and there were eleven); and at
    // `warning` it is visible but nothing fails, since the build is not -warnaserror. ADR 0051 cleared
    // the backlog, so `error` is what makes the threshold in quality-gates.yaml actually bind.
    [Fact]
    public void AdoptedRules_AreEnabledAsErrors_AndTheAnalyzerIsReferenced()
    {
        foreach (var rule in new[] { "S3776", "S138", "S107" })
        {
            Assert.Contains($"dotnet_diagnostic.{rule}.severity = error", Read(".editorconfig"), StringComparison.Ordinal);
        }

        Assert.Contains("SonarAnalyzer.CSharp", Read("Directory.Packages.props"), StringComparison.Ordinal);
    }

    // Only THREE Sonar rules are adopted (S3776 ADR 0050, S138 and S107 plan cc-11; see .editorconfig for the
    // measurement behind that). A path-scoped exemption may LOWER one to `suggestion`, never turn another rule on. The blanket
    // default-off is what keeps it that way; delete it and ~300 warnings return, most of them firing on
    // this repo's deliberate idioms rather than on defects.
    [Fact]
    public void OnlyTheAdoptedSonarRules_AreEnabled()
    {
        var editorconfig = Read(".editorconfig");

        Assert.Contains("dotnet_analyzer_diagnostic.severity = none", editorconfig, StringComparison.Ordinal);

        var enabled = Regex.Matches(editorconfig, @"^dotnet_diagnostic\.(S\d+)\.severity\s*=\s*(\w+)", RegexOptions.Multiline)
            .Where(m => m.Groups[2].Value != "none")
            .Select(m => m.Groups[1].Value)
            .Distinct()
            .Order()
            .ToList();

        Assert.Equal(["S107", "S138", "S3776"], enabled);
    }

    // `dotnet format` runs an `analyzers` pass and exits non-zero on any analyzer diagnostic, so while
    // ADR 0050 carried a backlog of eleven S3776 violations the format job had to exclude the rule —
    // a gate failing on findings it has nothing to say about. ADR 0051 cleared the backlog, so the
    // carve-out is gone and must stay gone: re-adding it would let a complexity regression through the
    // one CI job that would otherwise catch it before the build does.
    [Fact]
    public void FormatGate_CarriesNoComplexityCarveOut()
    {
        Assert.DoesNotContain("--exclude-diagnostics", Read(".github/workflows/pr-checks.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("--exclude-diagnostics", Read("CLAUDE.md"), StringComparison.Ordinal);
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

    /// <summary>
    /// AC 1 (ADR 0062). The deprecated community <c>OrleansDashboard</c> package must never be
    /// referenced. This is an availability gate, not a taste one: version 8.2.0 — its latest, built
    /// 2024-07-13 — throws <c>MissingMethodException</c> out of its pre-baked codegen on Orleans 10.x,
    /// and it does so from <c>Silo.StartAsync</c>, so the silo NEVER STARTS. In Container Apps that is a
    /// crash-looping container, and revision health is gated on every container in the replica, so it
    /// takes the whole app down — the <c>SEARXNG_SECRET</c> shape. It also drags in
    /// <c>OpenTelemetry.Api</c> 1.9.0, which carries a known advisory and would fail
    /// <c>vulnerable_packages: 0</c> on its own. It was deprecated in Nov 2025 and merged into
    /// <c>dotnet/orleans</c>; the supported package is <c>Microsoft.Orleans.Dashboard</c>.
    ///
    /// <para>Matched on the exact <c>Include</c> attribute, deliberately: a bare
    /// <c>Contains("OrleansDashboard")</c> is fine, but <c>Contains("Dashboard")</c> would
    /// false-positive on the Microsoft package this repo DOES reference.</para>
    /// </summary>
    [Fact]
    public void Packages_DoNotReferenceTheDeprecatedCommunityDashboard()
    {
        var packages = Read("Directory.Packages.props");

        Assert.DoesNotContain("Include=\"OrleansDashboard\"", packages, StringComparison.Ordinal);
        // The supported one is present, so this test cannot pass by the section simply being absent.
        Assert.Contains("Include=\"Microsoft.Orleans.Dashboard\"", packages, StringComparison.Ordinal);
    }
}
