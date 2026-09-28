using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// The Deploy workflow's <c>paths</c> filter must cover every file the images are built from. It once listed
/// only <c>src/**</c> and <c>infra/**</c>, so #166 — a <c>Directory.Packages.props</c>-only fix for a prod
/// outage — merged without deploying: the backend Dockerfile COPYs that root-level file, the trigger did not
/// watch it, and nothing said so.
/// </summary>
public sealed partial class DeployTriggerTests
{
    [Fact]
    public void EveryFileTheBackendImageCopies_TriggersADeploy()
    {
        var paths = DeployPaths();
        var sources = CopySources(Read("src", "Agent42.Backend", "Dockerfile"));

        // Non-vacuity: the root-level pin is the case that bit, and the src/ projects are the common case.
        Assert.Contains("Directory.Packages.props", sources);
        Assert.Contains(sources, s => s.StartsWith("src/", StringComparison.Ordinal));

        var uncovered = sources.Where(s => !paths.Any(p => Covers(p, s))).ToArray();
        Assert.True(
            uncovered.Length == 0,
            $"The backend Dockerfile copies {string.Join(", ", uncovered)}, but .github/workflows/deploy.yml's "
                + "push paths do not include it — a change to only that file would merge without deploying.");
    }

    [Fact]
    public void TheBuildContextFilter_TriggersADeploy() =>
        Assert.Contains(".dockerignore", DeployPaths());

    /// <summary>The <c>on.push.paths</c> entries of deploy.yml.</summary>
    private static List<string> DeployPaths()
    {
        var workflow = Read(".github", "workflows", "deploy.yml");
        var block = PathsBlock().Match(workflow);
        Assert.True(block.Success, "deploy.yml has no push `paths:` list");
        return [.. PathEntry().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value.Trim())];
    }

    /// <summary>The source of every build-context COPY (not <c>COPY --from</c>, which copies between stages).</summary>
    private static List<string> CopySources(string dockerfile) =>
        [.. CopyLine().Matches(dockerfile).Select(m => m.Groups[1].Value)];

    private static bool Covers(string pattern, string path) =>
        pattern.EndsWith("/**", StringComparison.Ordinal)
            ? path.StartsWith(pattern[..^2], StringComparison.Ordinal)
            : string.Equals(pattern, path.TrimEnd('/'), StringComparison.Ordinal);

    [GeneratedRegex(@"^    paths:\r?\n((?:\s*(?:#.*|-\s*\S+)\r?\n)+)", RegexOptions.Multiline)]
    private static partial Regex PathsBlock();

    [GeneratedRegex(@"^\s*-\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex PathEntry();

    [GeneratedRegex(@"^COPY (?!--from)(\S+) ", RegexOptions.Multiline)]
    private static partial Regex CopyLine();

    private static string Read(params string[] relativePath) =>
        File.ReadAllText(Path.Combine([RepoRoot(), .. relativePath]));

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
