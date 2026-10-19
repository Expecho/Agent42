using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// The shell of a Copilot fleet agent (ADR 0097) can run only what the backend image bakes in: the app runs as
/// <c>$APP_UID</c>, so nothing can be installed at runtime. Python 3 and Node.js are installed in the runtime stage
/// on purpose, and a Dockerfile refactor must not drop them silently.
/// </summary>
public sealed partial class BackendImageRuntimeTests
{
    [Fact]
    public void TheRuntimeStage_InstallsPython3()
    {
        var runtime = RuntimeStage();

        Assert.Matches(@"apt-get install [^\n]*\bpython3\b", runtime);
    }

    [Fact]
    public void TheRuntimeStage_CopiesNodeAndNpmFromTheNodeStage()
    {
        var dockerfile = Dockerfile();
        var runtime = RuntimeStage();

        // Ubuntu 24.04's apt nodejs is the end-of-life 18.x, so Node comes from the official image's LTS line.
        Assert.Matches(@"(?m)^FROM node:\S+ AS node$", dockerfile);
        Assert.Contains("COPY --from=node /usr/local/bin/node /usr/local/bin/node", runtime);
        Assert.Contains("COPY --from=node /usr/local/lib/node_modules /usr/local/lib/node_modules", runtime);
        Assert.Contains("/usr/local/bin/npm", runtime);
    }

    [Fact]
    public void TheRuntimeStage_StillEndsNonRoot()
    {
        // The non-root user is why the runtimes must be baked in (and what UserSkillProvider relies on).
        Assert.EndsWith("ENTRYPOINT [\"dotnet\", \"Agent42.Backend.dll\"]", RuntimeStage().TrimEnd());
        Assert.Contains("USER $APP_UID", RuntimeStage());
    }

    /// <summary>Everything from the <c>AS runtime</c> stage to the end of the Dockerfile.</summary>
    private static string RuntimeStage()
    {
        var match = RuntimeStageStart().Match(Dockerfile());
        Assert.True(match.Success, "The backend Dockerfile has no `AS runtime` stage");
        return Dockerfile()[match.Index..];
    }

    [GeneratedRegex(@"(?m)^FROM \S+ AS runtime$")]
    private static partial Regex RuntimeStageStart();

    private static string Dockerfile() =>
        File.ReadAllText(Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Dockerfile")).ReplaceLineEndings("\n");

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
