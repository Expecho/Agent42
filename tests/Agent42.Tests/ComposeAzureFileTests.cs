using YamlDotNet.RepresentationModel;

namespace Agent42.Tests;

/// <summary>
/// ADR 0145: <c>compose.azure.yaml</c> (plain Docker, Azure storage, native Entra sign-in) keeps what makes it safe to run:
/// real sign-in is on, the app is loopback-bound, the Copilot shell is off, no Postgres, no <c>local-dev</c> owner, and every
/// secret is required from the environment rather than written into the file.
/// </summary>
public sealed class ComposeAzureFileTests
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

    private static string Text() => File.ReadAllText(Path.Combine(Root, "compose.azure.yaml"));

    private static YamlMappingNode Load()
    {
        var yaml = new YamlStream();
        using var reader = new StringReader(Text());
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static YamlMappingNode Service(string name) => (YamlMappingNode)((YamlMappingNode)Load()["services"])[name];

    private static string Env(string key) => ((YamlScalarNode)((YamlMappingNode)Service("app")["environment"])[key]).Value!;

    [Fact]
    public void ComposeAzure_TurnsOnEntraSignIn_AndAzureStorage()
    {
        Assert.Equal("entra", Env("Auth__Mode"));
        foreach (var key in new[] { "Auth__Entra__TenantId", "Auth__Entra__ClientId", "Auth__Entra__ClientSecret", "ConnectionStrings__cosmos", "Storage__BlobEndpoint", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET" })
        {
            Assert.Contains(":?", Env(key), StringComparison.Ordinal); // required from .env, never blank
        }

        // Azure is the default provider: setting `local` would point the data at a Postgres this file does not have.
        Assert.DoesNotContain("Storage__Provider", Text(), StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeAzure_IsLoopbackBound_ShellOff_AndHasNoPostgres()
    {
        var ports = ((YamlSequenceNode)Service("app")["ports"]).Children.Select(c => ((YamlScalarNode)c).Value!).ToList();
        Assert.NotEmpty(ports);
        Assert.All(ports, p => Assert.StartsWith("127.0.0.1:", p, StringComparison.Ordinal));

        Assert.Equal("false", Env("Copilot__BuiltInToolsEnabled"));

        var services = ((YamlMappingNode)Load()["services"]).Children.Keys.Select(k => ((YamlScalarNode)k).Value!).ToList();
        Assert.DoesNotContain("postgres", services);
        Assert.DoesNotContain("ConnectionStrings__postgres", Text(), StringComparison.Ordinal);

        var others = ((YamlMappingNode)Load()["services"]).Children.Where(c => ((YamlScalarNode)c.Key).Value != "app");
        Assert.All(others, c => Assert.False(((YamlMappingNode)c.Value).Children.ContainsKey("ports")));
    }

    [Fact]
    public void ComposeAzure_NeverListsLocalDevAsOwner_AndWritesNoSecretsIntoTheFile()
    {
        Assert.DoesNotContain("local-dev", Env("Access__Owners__0"), StringComparison.Ordinal);
        Assert.Contains(":?", Env("Access__Owners__0"), StringComparison.Ordinal);

        foreach (var key in new[] { "Auth__Entra__ClientSecret", "AZURE_CLIENT_SECRET" })
        {
            Assert.StartsWith("${", Env(key), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ComposeAzure_EnvExample_NamesEveryRequiredVariable()
    {
        var example = File.ReadAllText(Path.Combine(Root, ".env.example"));
        foreach (var variable in new[]
                 {
                     "AZURE_TENANT_ID", "AZURE_CLIENT_ID", "AZURE_CLIENT_SECRET", "AUTH_CLIENT_ID", "AUTH_CLIENT_SECRET",
                     "COSMOS_ACCOUNT_ENDPOINT", "BLOB_ENDPOINT", "OWNER_OBJECT_ID",
                 })
        {
            Assert.Contains(variable, example, StringComparison.Ordinal);
            Assert.Contains("${" + variable, Text(), StringComparison.Ordinal);
        }
    }
}
