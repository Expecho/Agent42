using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>ADR 0145: the new session cookie never reaches the SearXNG sidecar, and the new docs page is reachable.</summary>
public sealed class EntraDocsAndProxyTests
{
    [Fact]
    public void SessionCookie_IsNotForwardedToTheSearxngSidecar()
    {
        var upstream = SearxngProxy.BuildUpstreamCookieHeader(
        [
            new(EntraAuthRegistration.SessionCookieName, "secret-session"),
            new(EntraAuthRegistration.SessionCookieName + "C1", "chunk"),
            new(".AspNetCore.OpenIdConnect.Nonce.abc", "nonce"),
            new(".AspNetCore.Correlation.xyz", "corr"),
            new("sxng_theme", "dark"),
        ]);

        Assert.Equal("theme=dark", upstream);
    }

    [Fact]
    public void TheEntraDocsPage_ExistsAndIsInTheMkdocsNav()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        Assert.True(File.Exists(Path.Combine(root, "docs", "setup", "docker-entra.md")));
        Assert.Contains("setup/docker-entra.md", File.ReadAllText(Path.Combine(root, "mkdocs.yml")), StringComparison.Ordinal);
    }
}
