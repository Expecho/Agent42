namespace Agent42.Tests;

/// <summary>
/// The local bridge helper ships as a downloadable GitHub Release: one self-contained, single-file executable
/// per platform (ADR 0076). Nothing in the suite can run a release, so these pin the source text the
/// promise rests on — the source-text style of <see cref="EasyAuthExclusionTests"/>. The real proof is a
/// release run: its smoke step executes the published binary and compares <c>--version</c> to the tag.
/// </summary>
public sealed class LocalBridgeReleaseTests
{
    private static string Read(params string[] parts) => File.ReadAllText(Path.Combine([RepoRoot(), .. parts]));

    private static string Workflow() => Read(".github", "workflows", "release-local-bridge.yml");

    // Self-contained + single-file is what makes "download and run, no .NET installed" true. It lives in the
    // csproj, conditional on a runtime identifier — unconditional SelfContained would break the plain
    // `dotnet build`/`dotnet test` of the solution, which has no RID.
    [Fact]
    public void Project_PublishesSelfContainedSingleFile_OnlyForARuntimeIdentifier()
    {
        var csproj = Read("tools", "Agent42.LocalBridge", "Agent42.LocalBridge.csproj");

        var conditional = csproj.IndexOf("Condition=\"'$(RuntimeIdentifier)' != ''\"", StringComparison.Ordinal);
        Assert.True(conditional >= 0, "the publish settings must be conditional on a runtime identifier");
        var group = csproj[conditional..csproj.IndexOf("</PropertyGroup>", conditional, StringComparison.Ordinal)];
        Assert.Contains("<SelfContained>true</SelfContained>", group);
        Assert.Contains("<PublishSingleFile>true</PublishSingleFile>", group);
        Assert.Contains("<IncludeNativeLibrariesForSelfExtract>true</IncludeNativeLibrariesForSelfExtract>", group);

        Assert.Equal(1, Count(csproj, "<SelfContained>"));
    }

    // Released from a dedicated tag, publishing with a runtime identifier for every platform. macOS must be
    // built on a macOS runner: only there does the SDK ad-hoc sign the apphost, and Apple Silicon will not run
    // an unsigned binary.
    [Fact]
    public void Workflow_ReleasesEveryPlatformFromADedicatedTag()
    {
        var workflow = Workflow();

        Assert.Contains("tags: ['local-bridge-v*']", workflow);
        Assert.Contains("contents: write", workflow);
        Assert.Contains("dotnet publish \"$PROJECT\" -c Release -r \"$RID\" -p:Version=\"$VERSION\"", workflow);
        Assert.Contains("PROJECT: tools/Agent42.LocalBridge/Agent42.LocalBridge.csproj", workflow);

        foreach (var rid in new[] { "win-x64", "win-arm64", "linux-x64", "linux-arm64" })
        {
            Assert.Contains($"rid: {rid},", workflow);
        }

        Assert.Contains("{ rid: osx-x64,     os: macos-latest", workflow);
        Assert.Contains("{ rid: osx-arm64,   os: macos-latest", workflow);
        Assert.Contains("SHA256SUMS.txt", workflow);
        Assert.Contains("--version", workflow);
    }

    // A change to the helper on main releases itself: a path-filtered push trigger picks the next patch after
    // the highest STABLE tag. One un-cancellable concurrency group is what keeps two quick pushes from racing
    // for the same version.
    [Fact]
    public void Workflow_ReleasesAutomaticallyWhenTheHelperChangesOnMain()
    {
        var workflow = Workflow();
        var on = workflow[workflow.IndexOf("\non:", StringComparison.Ordinal)..workflow.IndexOf("\npermissions:", StringComparison.Ordinal)];

        Assert.Contains("branches: [main]", on);
        Assert.Contains("- tools/Agent42.LocalBridge/**", on);
        Assert.Contains("- Directory.Packages.props", on);
        Assert.DoesNotContain("- src/", on); // backend changes never cut a helper release

        Assert.Contains("group: release-local-bridge\n", workflow.ReplaceLineEndings("\n"));
        Assert.Contains("cancel-in-progress: false", workflow);
        Assert.Contains("matching-refs/tags/local-bridge-v", workflow);
        Assert.Contains("grep -E '^[0-9]+\\.[0-9]+\\.[0-9]+$' | sort -V | tail -n 1", workflow);
        Assert.Contains("next=\"$major.$minor.$((patch + 1))\"", workflow);
    }

    // The repository has immutable releases on: a published release rejects every asset upload (HTTP 422), which
    // is how 1.0.1 and 1.0.2 shipped with no binaries. So the release is a draft until the last step, the build
    // jobs check out the commit (a draft has no tag yet), and an already-published version fails instead of
    // pretending its assets can be replaced.
    [Fact]
    public void Workflow_PublishesTheReleaseOnlyAfterEveryAssetIsUploaded()
    {
        var workflow = Workflow().ReplaceLineEndings("\n");

        var create = workflow.IndexOf("gh release create \"$TAG\" --draft --target \"$GITHUB_SHA\"", StringComparison.Ordinal);
        var upload = workflow.IndexOf("gh release upload \"$TAG\" \"$NAME\"", StringComparison.Ordinal);
        var checksums = workflow.IndexOf("gh release upload \"$TAG\" SHA256SUMS.txt", StringComparison.Ordinal);
        var publish = workflow.IndexOf("gh release edit \"$TAG\" --repo \"$GITHUB_REPOSITORY\" --draft=false", StringComparison.Ordinal);

        Assert.True(create >= 0, "the release must be created as a draft");
        Assert.True(create < upload && upload < checksums && checksums < publish, "publish must be the last release write");
        Assert.Equal(1, Count(workflow, "--draft=false"));
        Assert.Contains("needs: [create-release, publish]", workflow); // a failed platform leaves the draft unpublished

        Assert.Contains("ref: ${{ needs.create-release.outputs.sha }}", workflow);
        Assert.DoesNotContain("ref: ${{ needs.create-release.outputs.tag }}", workflow);
        Assert.Contains("Published releases are immutable; release a new version.", workflow);
    }

    // The helper is never deployed: the deploy workflow must not start on a release tag, and the backend image
    // must not pick the helper up.
    [Fact]
    public void Deploy_IsNotTriggeredByARelease_AndDoesNotShipTheHelper()
    {
        var deploy = Read(".github", "workflows", "deploy.yml");
        var on = deploy[deploy.IndexOf("on:", StringComparison.Ordinal)..deploy.IndexOf("env:", StringComparison.Ordinal)];
        Assert.DoesNotContain("tags", on);
        Assert.Contains("branches: [main]", on);

        Assert.DoesNotContain("LocalBridge", Read("src", "Agent42.Backend", "Dockerfile"));
    }

    // The UI tells the user where to get it, and what to run.
    // The release notes are where a downloader learns the switches, so every switch the helper's usage text
    // lists must appear there too — a new flag that only --help mentions fails here.
    [Fact]
    public void ReleaseNotes_DocumentEverySwitch()
    {
        var workflow = Workflow();
        var start = workflow.IndexOf("cat > notes.md", StringComparison.Ordinal);
        var notes = workflow[start..workflow.IndexOf("gh release create", start, StringComparison.Ordinal)];
        var switches = System.Text.RegularExpressions.Regex.Matches(Agent42.LocalBridge.BridgeOptions.Usage, @"--[a-z][a-z-]+")
            .Select(m => m.Value).Distinct().ToList();

        Assert.Contains("--yolo", switches);
        Assert.All(switches, s => Assert.Contains($"`{s}", notes, StringComparison.Ordinal));
        Assert.Contains("AGENT42_BRIDGE_KEY", notes, StringComparison.Ordinal);
    }

    [Fact]
    public void Ui_PointsAtTheReleaseAndTheReleasedExecutable()
    {
        var html = Read("src", "Agent42.Backend", "wwwroot", "index.html");
        var js = Read("src", "Agent42.Backend", "wwwroot", "js", "mcp.js");

        Assert.Contains("https://github.com/Expecho/Agent42/releases?q=local-bridge", html);
        Assert.Contains("./Agent42.LocalBridge --server ${location.origin}", js);
        Assert.DoesNotContain("dotnet run --project tools/Agent42.LocalBridge", js);
    }

    private static int Count(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + 1, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
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
