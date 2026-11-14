using YamlDotNet.RepresentationModel;

namespace Agent42.Tests;

/// <summary>
/// AC13: <c>compose.yaml</c> keeps the properties that make self-hosting safe and bootable: the app is bound to loopback
/// (there is no authentication in front of it), Postgres is health-checked and gates the app, the volumes are made writable
/// for the non-root app user, and the keys the app throws without are present. It does not try to prove every key is read
/// by code; the real boot proof is the scripted smoke recorded in the MR.
/// </summary>
public sealed class ComposeFileTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    private static YamlMappingNode Load()
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Root, "compose.yaml"));
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static YamlMappingNode Service(string name)
        => (YamlMappingNode)((YamlMappingNode)Load()["services"])[name];

    private static IEnumerable<string> Sequence(YamlNode node) => ((YamlSequenceNode)node).Children.Select(c => ((YamlScalarNode)c).Value!);

    [Fact]
    public void TheAppPort_IsBoundToLoopbackOnly()
    {
        var ports = Sequence(Service("app")["ports"]).ToList();

        Assert.NotEmpty(ports);
        Assert.All(ports, p => Assert.StartsWith("127.0.0.1:", p, StringComparison.Ordinal));
    }

    [Fact]
    public void NoOtherServicePublishesAPort()
    {
        var services = (YamlMappingNode)Load()["services"];

        foreach (var (name, node) in services.Children)
        {
            if (((YamlScalarNode)name).Value != "app")
            {
                Assert.False(((YamlMappingNode)node).Children.ContainsKey("ports"), $"{((YamlScalarNode)name).Value} publishes a port");
            }
        }
    }

    [Fact]
    public void Postgres_HasAHealthcheck_AndTheAppWaitsForItToBeHealthy()
    {
        Assert.True(Service("postgres").Children.ContainsKey("healthcheck"));

        var dependsOn = (YamlMappingNode)Service("app")["depends_on"];
        var postgres = (YamlMappingNode)dependsOn["postgres"];
        Assert.Equal("service_healthy", ((YamlScalarNode)postgres["condition"]).Value);
    }

    [Fact]
    public void TheAppEnvironment_HasTheKeysItThrowsWithout()
    {
        var environment = (YamlMappingNode)Service("app")["environment"];
        var keys = environment.Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToHashSet();

        foreach (var required in new[]
        {
            "Storage__Provider", "ConnectionStrings__postgres", "Storage__Local__Root", "WebSearch__Endpoint", "Access__Owners__0",
        })
        {
            Assert.Contains(required, keys);
        }

        Assert.Equal("local", ((YamlScalarNode)environment["Storage__Provider"]).Value);
        Assert.Equal("local-dev", ((YamlScalarNode)environment["Access__Owners__0"]).Value);

        // No authentication, so no auto-approved shell inside the container (it could read the DB password and key ring).
        Assert.Equal("false", ((YamlScalarNode)environment["Copilot__BuiltInToolsEnabled"]).Value);
    }

    [Fact]
    public void TheSearxngSidecar_GetsItsPort_Secret_AndSubpathBaseUrl()
    {
        var environment = (YamlMappingNode)Service("searxng")["environment"];

        Assert.Equal("8888", ((YamlScalarNode)environment["SEARXNG_PORT"]).Value);
        Assert.Contains("SEARXNG_SECRET", ((YamlScalarNode)environment["SEARXNG_SECRET"]).Value);
        Assert.EndsWith("/searxng/", ((YamlScalarNode)environment["SEARXNG_BASE_URL"]).Value);

        // The app reaches it at the same authority the base URL names.
        var endpoint = ((YamlScalarNode)((YamlMappingNode)Service("app")["environment"])["WebSearch__Endpoint"]).Value!;
        Assert.StartsWith(endpoint, ((YamlScalarNode)environment["SEARXNG_BASE_URL"]).Value);
    }

    [Fact]
    public void TheVolumes_AreMadeWritableForTheNonRootAppUser_BeforeTheAppStarts()
    {
        var init = Service("init-volumes");
        Assert.Contains("chown", ((YamlScalarNode)init["command"]).Value);

        var dependsOn = (YamlMappingNode)Service("app")["depends_on"];
        Assert.Equal("service_completed_successfully", ((YamlScalarNode)((YamlMappingNode)dependsOn["init-volumes"])["condition"]).Value);

        // The root the app writes under and the workspace root are each a mounted volume the init step chowns.
        var environment = (YamlMappingNode)Service("app")["environment"];
        var mounts = Sequence(Service("app")["volumes"]).Select(v => v.Split(':')[1]).ToHashSet();
        Assert.Contains(((YamlScalarNode)environment["Storage__Local__Root"]).Value!, mounts);
        Assert.Contains(((YamlScalarNode)environment["Copilot__WorkspaceRoot"]).Value!, mounts);
    }

    [Fact]
    public void SecretsAreRequiredFromTheEnvironment_NotWrittenIntoTheFile()
    {
        var text = File.ReadAllText(Path.Combine(Root, "compose.yaml"));

        Assert.Contains("${POSTGRES_PASSWORD:?", text, StringComparison.Ordinal);
        Assert.Contains("${SEARXNG_SECRET:?", text, StringComparison.Ordinal);

        var example = File.ReadAllText(Path.Combine(Root, ".env.example"));
        Assert.Contains("POSTGRES_PASSWORD=", example, StringComparison.Ordinal);
        Assert.Contains("SEARXNG_SECRET=", example, StringComparison.Ordinal);
        Assert.Contains(".env", File.ReadAllLines(Path.Combine(Root, ".gitignore")).Select(l => l.Trim()));
    }

    [Fact]
    public void TheBuildContexts_Exist()
    {
        Assert.True(File.Exists(Path.Combine(Root, "src", "Agent42.Backend", "Dockerfile")));
        Assert.True(File.Exists(Path.Combine(Root, "infra", "searxng", "Dockerfile")));
    }

    [Fact]
    public void TheDocs_NameTheNoAuthenticationLimit()
    {
        var doc = File.ReadAllText(Path.Combine(Root, "docs", "features", "self-hosting.md"));
        Assert.Contains("No authentication", doc, StringComparison.Ordinal);
        Assert.Contains("SearXNG forwards", doc, StringComparison.Ordinal);
    }
}
