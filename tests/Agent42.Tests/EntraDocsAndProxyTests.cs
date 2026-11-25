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
    public void TheSetupScript_ExistsAndIsLinkedFromTheDocsAndTheCompose()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? throw new InvalidOperationException("repo root not found");
        var script = File.ReadAllText(Path.Combine(root, "infra", "docker-entra-setup.sh"));
        Assert.Contains("Cosmos DB Built-in Data Contributor", script, StringComparison.Ordinal);
        Assert.Contains("Storage Blob Data Contributor", script, StringComparison.Ordinal);
        Assert.Contains("appRoleAssignmentRequired=true", script, StringComparison.Ordinal);

        foreach (var file in new[] { "docs/setup/docker-entra.md", "README.md", "compose.azure.yaml", ".env.example" })
        {
            Assert.Contains("docker-entra-setup.sh", File.ReadAllText(Path.Combine(root, file)), StringComparison.Ordinal);
        }
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
