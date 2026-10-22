namespace Agent42.Tests;

/// <summary>
/// The Android companion app ships as a signed APK on a GitHub Release (the Android-companion ADR), the same shape as
/// the Chrome extension's release (<see cref="ChromeExtensionReleaseTests"/>). Nothing in the suite can run a release,
/// so these pin the source text the promise rests on.
/// </summary>
public sealed class AndroidReleaseTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Workflow() => Read(".github", "workflows", "android.yml");

    [Fact]
    public void Workflow_ChecksPullRequests_AndReleasesFromATagAndAutomaticallyOnMain()
    {
        var workflow = Workflow();

        Assert.Contains("pull_request:", workflow, StringComparison.Ordinal);
        Assert.Contains("tags: ['android-v*']", workflow, StringComparison.Ordinal);
        Assert.Contains("- tools/Agent42.Android/**", workflow, StringComparison.Ordinal);
        Assert.Contains("branches: [main]", workflow, StringComparison.Ordinal);
        Assert.Contains("cancel-in-progress: false", workflow, StringComparison.Ordinal);
        Assert.Contains("./gradlew --no-daemon test lint assembleDebug", workflow, StringComparison.Ordinal);
        // A PR never reaches the release job, and the release never runs without the checks passing.
        Assert.Contains("needs: check", workflow, StringComparison.Ordinal);
        Assert.Contains("if: github.event_name != 'pull_request'", workflow, StringComparison.Ordinal);
    }

    // Android refuses an update whose versionCode doesn't increase: the code is derived from the SemVer core and both
    // values reach Gradle, which reads exactly these property names.
    [Fact]
    public void Workflow_StampsAnIncreasingVersionCode_ThatTheBuildReads()
    {
        var workflow = Workflow();
        var build = Read("tools", "Agent42.Android", "app", "build.gradle.kts");

        Assert.Contains("code=$((major * 10000 + minor * 100 + patch))", workflow, StringComparison.Ordinal);
        Assert.Contains("-Pagent42.versionName=\"$VERSION\" -Pagent42.versionCode=\"$CODE\"", workflow, StringComparison.Ordinal);
        Assert.Contains("gradleProperty(\"agent42.versionName\")", build, StringComparison.Ordinal);
        Assert.Contains("gradleProperty(\"agent42.versionCode\")", build, StringComparison.Ordinal);
    }

    // A sideloaded app updates only when every release is signed with the same key, so an unsigned release must fail.
    [Fact]
    public void Workflow_FailsWithoutTheSigningKey_AndTheBuildSignsWithIt()
    {
        var workflow = Workflow();
        var build = Read("tools", "Agent42.Android", "app", "build.gradle.kts");

        Assert.Contains("ANDROID_KEYSTORE_BASE64 is not set", workflow, StringComparison.Ordinal);
        Assert.Contains("ANDROID_KEYSTORE_PATH=$RUNNER_TEMP/release.jks", workflow, StringComparison.Ordinal);
        Assert.Contains("environmentVariable(\"ANDROID_KEYSTORE_PATH\")", build, StringComparison.Ordinal);
        Assert.Contains("signingConfig = signingConfigs.findByName(\"release\")", build, StringComparison.Ordinal);
    }

    // Only the release job may write, and it alone sees the signing secrets: the check job also runs on pull requests.
    // A workflow edit alone releases nothing, and a version whose code would collide (minor or patch >= 100) is refused.
    [Fact]
    public void Workflow_ScopesWritesAndSecretsToTheReleaseJob_AndReleasesOnlyOnAppChanges()
    {
        var workflow = Workflow();
        var check = workflow[workflow.IndexOf("  check:", StringComparison.Ordinal)..workflow.IndexOf("  release:", StringComparison.Ordinal)];
        var release = workflow[workflow.IndexOf("  release:", StringComparison.Ordinal)..];
        var push = workflow[workflow.IndexOf("  push:", StringComparison.Ordinal)..workflow.IndexOf("  workflow_dispatch:", StringComparison.Ordinal)];

        Assert.Contains("permissions:\n  contents: read", workflow.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("contents: write", check, StringComparison.Ordinal);
        Assert.DoesNotContain("secrets.", check, StringComparison.Ordinal);
        Assert.Contains("contents: write", release, StringComparison.Ordinal);
        Assert.DoesNotContain(".github/workflows/android.yml", push, StringComparison.Ordinal);
        Assert.Contains("if [ \"$minor\" -ge 100 ] || [ \"$patch\" -ge 100 ]; then", workflow, StringComparison.Ordinal);
    }

    // Immutable releases: a published release rejects uploads, so the draft gets everything and is published LAST.
    [Fact]
    public void Workflow_PublishesTheReleaseOnlyAfterTheApkIsUploaded()
    {
        var workflow = Workflow();

        var draft = workflow.IndexOf("gh release create \"$TAG\" --draft", StringComparison.Ordinal);
        var build = workflow.IndexOf("assembleRelease", StringComparison.Ordinal);
        var upload = workflow.IndexOf("gh release upload \"$TAG\" \"$NAME\"", StringComparison.Ordinal);
        var sums = workflow.IndexOf("gh release upload \"$TAG\" SHA256SUMS.txt", StringComparison.Ordinal);
        var publish = workflow.IndexOf("gh release edit \"$TAG\" --draft=false", StringComparison.Ordinal);
        Assert.True(
            draft > 0 && build > draft && upload > build && sums > upload && publish > sums,
            "draft → build → upload → sums → publish");
    }

    // The app is not part of the server: the deploy never ships it and is not triggered by its path.
    [Fact]
    public void Deploy_DoesNotShipTheApp()
    {
        Assert.DoesNotContain("Agent42.Android", Read(".github", "workflows", "deploy.yml"), StringComparison.Ordinal);
        Assert.DoesNotContain("Agent42.Android", Read("src", "Agent42.Backend", "Dockerfile"), StringComparison.Ordinal);
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
