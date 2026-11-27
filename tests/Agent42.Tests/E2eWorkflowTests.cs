using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Source-text guards for the three e2e smoke layers (ADR 0151). Nothing here runs a browser; it pins the wiring so a
/// layer cannot be quietly dropped from CI, and keeps the post-deploy script's public paths a subset of the Bicep
/// Easy Auth <c>excludedPaths</c> so the two cannot drift.
/// </summary>
public sealed partial class E2eWorkflowTests
{
    [Fact]
    public void PrChecks_RunsTheDevHostBrowserSmoke()
    {
        var workflow = Read(".github", "workflows", "pr-checks.yml");
        Assert.Matches(@"(?m)^  e2e-devhost:", workflow);
        Assert.Contains("npx playwright test devhost", workflow);
    }

    [Fact]
    public void Deploy_SmokeJob_RunsAfterDeploy_AndProbesTheBicepBackendUrl()
    {
        var workflow = Read(".github", "workflows", "deploy.yml");
        Assert.Matches(@"(?ms)^  smoke:\r?\n(?:(?!^  \S).)*?needs:\s*deploy\s*$", workflow);
        Assert.Contains("infra/smoke-test.sh", workflow);
        Assert.Contains("properties.outputs.backendUrl.value", workflow);
        Assert.Contains("backendUrl", Read("infra", "main.bicep"));
    }

    [Fact]
    public void SelfHostWorkflow_BuildsFromSource_AndRunsTheSelfHostSpec()
    {
        var workflow = Read(".github", "workflows", "e2e-selfhost.yml");
        Assert.Contains("compose.build.yaml", workflow);
        Assert.Contains("tests/e2e/selfhost/compose.e2e.yaml", workflow);
        Assert.Contains("npx playwright test selfhost", workflow);
        Assert.Contains("schedule:", workflow);
    }

    [Fact]
    public void SmokePublicPaths_AreExcludedFromEasyAuth()
    {
        var paths = SmokePublicPaths();
        Assert.NotEmpty(paths);

        var excluded = EasyAuthExclusionTests.ExcludedPaths();
        var exposed = paths.Where(p => !EasyAuthExclusionTests.IsExcluded(excluded, p)).ToArray();
        Assert.True(
            exposed.Length == 0,
            $"infra/smoke-test.sh expects {string.Join(", ", exposed)} to be public, but the Bicep excludedPaths "
                + "(container-app.bicep) would put it behind the Entra login.");

        // /api/email-in is probed unauthenticated too, and only the exclusion makes that meaningful.
        Assert.True(EasyAuthExclusionTests.IsExcluded(excluded, "/api/email-in"));
        Assert.Contains("/api/email-in", Read("infra", "smoke-test.sh"));
    }

    [Fact]
    public void E2eSpecs_AndTheirStack_Exist()
    {
        foreach (var file in new[]
        {
            "tests/e2e/playwright.config.mjs", "tests/e2e/devhost.spec.mjs", "tests/e2e/selfhost.spec.mjs",
            "tests/e2e/selfhost/compose.e2e.yaml", "tests/e2e/selfhost/llm-stub.mjs", "tests/e2e/package-lock.json",
        })
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot(), file.Replace('/', Path.DirectorySeparatorChar))), file);
        }
    }

    private static List<string> SmokePublicPaths()
    {
        var block = PublicPathsBlock().Match(Read("infra", "smoke-test.sh"));
        Assert.True(block.Success, "smoke-test.sh has no PUBLIC_PATHS=( ... ) array");
        return [.. QuotedPath().Matches(block.Groups[1].Value).Select(m => m.Groups[1].Value)];
    }

    [GeneratedRegex(@"PUBLIC_PATHS=\(\r?\n(.*?)\r?\n\)", RegexOptions.Singleline)]
    private static partial Regex PublicPathsBlock();

    [GeneratedRegex("\"(/[^\"]*)\"")]
    private static partial Regex QuotedPath();

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
