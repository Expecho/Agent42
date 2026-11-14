namespace Agent42.Tests;

/// <summary>
/// ADR 0125 removed <c>fetch_web_page</c> and the context.dev dependency. This pins that no reference to the
/// service, its config section or its secret creeps back into the shipped surface (src, infra, CI, tools),
/// where a stray one would resurrect an egress third party or a dead secret in the deploy.
/// </summary>
public class ContextDevRemovalTests
{
    private static readonly string[] Needles = ["context.dev", "ContextScrape", "CONTEXT_API_KEY", "contextApiKey"];

    private static readonly string[] TextExtensions =
    [
        ".cs", ".csproj", ".props", ".json", ".yml", ".yaml", ".bicep", ".js", ".mjs", ".html", ".css", ".md",
        ".txt", ".xml", ".kt", ".kts", ".gradle", ".slnx",
    ];

    [Fact]
    public void NoContextDevReferencesRemain()
    {
        var root = RepoRoot();
        var hits = new List<string>();

        foreach (var folder in new[] { "src", "infra", ".github", "tools" })
        {
            var dir = Path.Combine(root, folder);
            if (Directory.Exists(dir))
            {
                hits.AddRange(Scan(root, dir));
            }
        }

        Assert.True(hits.Count == 0, "context.dev was removed in ADR 0125, but it is referenced again:" + Environment.NewLine + string.Join(Environment.NewLine, hits));
    }

    private static IEnumerable<string> Scan(string root, string dir)
    {
        foreach (var file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(IsTextSource))
        {
            var text = File.ReadAllText(file);
            foreach (var needle in Needles.Where(n => text.Contains(n, StringComparison.OrdinalIgnoreCase)))
            {
                yield return $"{Path.GetRelativePath(root, file)}: {needle}";
            }
        }
    }

    private static bool IsTextSource(string file) =>
        !IsBuildOutput(file) && TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

    // bin/ and obj/ hold stale appsettings copies and PDBs on a dirty local tree; node_modules/.gradle hold
    // third-party text that is not ours.
    private static bool IsBuildOutput(string path)
    {
        var parts = path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or "node_modules" or ".gradle" or "build");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
