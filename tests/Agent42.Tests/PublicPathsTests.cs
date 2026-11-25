using Agent42.Backend;
using System.Text.RegularExpressions;

namespace Agent42.Tests;

public sealed class PublicPathsTests
{
    [Theory]
    [InlineData("/api/agent-run")]
    [InlineData("/api/share/shr_abc")]
    [InlineData("/api/share/shr_abc/attachments/a1")]
    [InlineData("/api/share/")]
    [InlineData("/shared")]
    [InlineData("/shared/")]
    [InlineData("/shared/app.js")]
    [InlineData("/api/agent-mcp")]
    [InlineData("/api/agent-mcp/")]
    [InlineData("/api/local-bridge")]
    [InlineData("/api/email-in")]
    [InlineData("/legal/privacy.html")]
    [InlineData("/API/Agent-Run")]
    public void PublicPaths_Matcher_Public(string path) => Assert.True(PublicPaths.IsPublic(path), path);

    [Theory]
    [InlineData("/api/sharezzz")]
    [InlineData("/api/share")]
    [InlineData("/api/agent-run/x")]
    [InlineData("/api/email-in/x")]
    [InlineData("/sharedx")]
    [InlineData("/legal")]
    [InlineData("/api/share/../models")]
    [InlineData("/api/share/%2e%2e/models")]
    [InlineData("/api/share/%2E%2E%2fmodels")]
    [InlineData("/shared/..\\logs")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("api/share/x")]
    public void PublicPaths_Matcher_Gated(string path) => Assert.False(PublicPaths.IsPublic(path), path);

    [Theory]
    [InlineData("/api/mcp/oauth/callback")]
    [InlineData("/api/mcp/oauth/start")]
    [InlineData("/api/providers/github/callback")]
    [InlineData("/api/providers/github/start")]
    [InlineData("/dashboard")]
    [InlineData("/logs")]
    [InlineData("/index.html")]
    [InlineData("/sw.js")]
    [InlineData("/manifest.webmanifest")]
    public void ProtectedPaths_AreNotPublic(string path) => Assert.False(PublicPaths.IsPublic(path), path);

    [Fact]
    public void PublicPaths_EqualBicepExcludedPaths()
    {
        var bicep = File.ReadAllText(Path.Combine(RepoRoot(), "infra", "modules", "container-app.bicep"));
        var block = Regex.Match(bicep, @"excludedPaths:\s*\[(?<body>[^\]]*)\]");
        Assert.True(block.Success, "excludedPaths block not found in container-app.bicep");

        var fromBicep = Regex.Matches(block.Groups["body"].Value, @"'([^']+)'").Select(m => m.Groups[1].Value).ToArray();
        Assert.True(fromBicep.Length >= 9, $"Parsed only {fromBicep.Length} entries - the extraction broke.");
        Assert.Equal(fromBicep.OrderBy(s => s, StringComparer.Ordinal), PublicPaths.Entries.OrderBy(s => s, StringComparer.Ordinal));
    }

    [Fact]
    public void AuthSurface_IsRecognised()
    {
        Assert.True(PublicPaths.IsAuthSurface("/.auth/login/aad"));
        Assert.True(PublicPaths.IsAuthSurface("/.auth/me"));
        Assert.False(PublicPaths.IsAuthSurface("/.authx"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }
}
