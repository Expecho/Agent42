using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// The signal-cli the backend image bundles (ADR 0111, phase 3; AC32), as <c>src/Agent42.Backend/Dockerfile</c> declares
/// it. Nothing in the suite builds the image, so this asserts the text: the release is pinned by version AND digest and
/// the digest is checked, the JRE is pinned by digest, and the version and release date reach the runtime as the
/// environment variables Config health reads to warn before Signal's three-month expiry.
/// </summary>
public sealed partial class SignalImageTests
{
    [Fact]
    public void SignalCli_IsPinnedByVersionAndDigest_AndTheDigestIsChecked()
    {
        var dockerfile = Dockerfile();

        Assert.Matches(VersionArg(), dockerfile);
        Assert.Matches(DigestArg(), dockerfile);
        Assert.Matches(ReleasedArg(), dockerfile);
        // Global ARGs: only one declared before the first FROM reaches the later stages (an empty value 404s the download).
        Assert.True(dockerfile.IndexOf("ARG SIGNAL_CLI_VERSION=", StringComparison.Ordinal)
            < Regex.Match(dockerfile, "(?m)^FROM ").Index);
        Assert.Contains("sha256sum -c", dockerfile);
        Assert.Matches(@"(?m)^FROM eclipse-temurin:25-jre\S*@sha256:[0-9a-f]{64} AS jre$", dockerfile);
    }

    [Fact]
    public void SignalCli_VersionAndReleaseDate_ReachTheRuntime()
    {
        var runtime = Dockerfile()[Dockerfile().IndexOf("AS runtime", StringComparison.Ordinal)..];

        Assert.Contains("COPY --from=signal-cli /opt/signal-cli /opt/signal-cli", runtime);
        Assert.Contains("COPY --from=jre /opt/java/openjdk /opt/java/openjdk", runtime);
        Assert.Contains("SIGNAL_CLI_VERSION=${SIGNAL_CLI_VERSION}", runtime);
        Assert.Contains("SIGNAL_CLI_RELEASED=${SIGNAL_CLI_RELEASED}", runtime);
        // The process runs the launcher script from here (SignalOptions.ExecutablePath's default).
        Assert.Contains("\"/opt/signal-cli/bin/signal-cli\"", File.ReadAllText(Path.Combine(
            RepoRoot(), "src", "Agent42.Backend", "ChatGateway", "Signal", "SignalOptions.cs")));
    }

    [GeneratedRegex(@"(?m)^ARG SIGNAL_CLI_VERSION=\d+\.\d+\.\d+$")]
    private static partial Regex VersionArg();

    [GeneratedRegex(@"(?m)^ARG SIGNAL_CLI_SHA256=[0-9a-f]{64}$")]
    private static partial Regex DigestArg();

    [GeneratedRegex(@"(?m)^ARG SIGNAL_CLI_RELEASED=\d{4}-\d{2}-\d{2}$")]
    private static partial Regex ReleasedArg();

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
