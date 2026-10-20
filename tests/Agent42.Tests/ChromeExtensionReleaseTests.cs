namespace Agent42.Tests;

/// <summary>
/// The Chrome extension ships as a zip on a GitHub Release (the Chrome-extension ADR), the same shape as the
/// helper's release (<see cref="LocalBridgeReleaseTests"/>). Nothing in the suite can run a release, so these pin
/// the source text the promise rests on.
/// </summary>
public sealed class ChromeExtensionReleaseTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Workflow() => Read(".github", "workflows", "release-chrome-extension.yml");

    [Fact]
    public void Workflow_ReleasesFromADedicatedTag_AndAutomaticallyOnMain()
    {
        var workflow = Workflow();

        Assert.Contains("tags: ['chrome-extension-v*']", workflow, StringComparison.Ordinal);
        Assert.Contains("- tools/Agent42.ChromeExtension/**", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
    }

    // Immutable releases: a published release rejects uploads, so the draft gets everything and is published LAST.
    [Fact]
    public void Workflow_PublishesTheReleaseOnlyAfterTheZipIsUploaded()
    {
        var workflow = Workflow();

        var draft = workflow.IndexOf("gh release create \"$TAG\" --draft", StringComparison.Ordinal);
        var upload = workflow.IndexOf("gh release upload \"$TAG\" \"$NAME\"", StringComparison.Ordinal);
        var sums = workflow.IndexOf("gh release upload \"$TAG\" SHA256SUMS.txt", StringComparison.Ordinal);
        var publish = workflow.IndexOf("gh release edit \"$TAG\" --draft=false", StringComparison.Ordinal);
        Assert.True(draft > 0 && upload > draft && sums > upload && publish > sums, "draft → upload → sums → publish");
    }

    // Chrome accepts only 1–4 dot-separated integers as "version"; the full SemVer goes in "version_name". The
    // tests run before anything is released, and the zip never carries them.
    [Fact]
    public void Workflow_StampsAChromeValidVersion_AndShipsNoTests()
    {
        var workflow = Workflow();

        Assert.Contains("manifest.version = version.split(\"-\")[0];", workflow, StringComparison.Ordinal);
        Assert.Contains("manifest.version_name = version;", workflow, StringComparison.Ordinal);
        Assert.Contains("--exclude=./test", workflow, StringComparison.Ordinal);
        Assert.True(
            workflow.IndexOf("node --test", StringComparison.Ordinal) < workflow.IndexOf("gh release create", StringComparison.Ordinal),
            "the extension's tests must run before a draft release is created");
    }

    // The extension is not part of the app: the deploy never ships it and is not triggered by its path.
    [Fact]
    public void Deploy_DoesNotShipTheExtension()
    {
        Assert.DoesNotContain("Agent42.ChromeExtension", Read(".github", "workflows", "deploy.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("ChromeExtension", Read("src", "Agent42.Backend", "Dockerfile"), StringComparison.Ordinal);
    }

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
