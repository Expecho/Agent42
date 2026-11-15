using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace Agent42.Tests;

/// <summary>
/// ADR 0130: the published images are what <c>compose.yaml</c> pulls and what an Azure deployer imports into their registry.
/// Three things must keep agreeing, and nothing else would notice if they drifted: the image names the publish workflow
/// pushes, the ones compose pulls, and the ones <c>infra/main.bicep</c> deploys (<c>az acr import</c> keeps the name).
/// </summary>
public sealed partial class PublishImagesTests
{
    private static readonly string Root = FindRepoRoot();

    [Fact]
    public void ComposePulls_TheImagesThePublishWorkflowPushes()
    {
        var published = PublishedImageNames();
        Assert.Equal(["agent42-backend", "agent42-searxng"], published.OrderBy(n => n, StringComparer.Ordinal));

        var services = (YamlMappingNode)Load("compose.yaml")["services"];
        foreach (var (service, name) in new[] { ("app", "agent42-backend"), ("searxng", "agent42-searxng") })
        {
            var node = (YamlMappingNode)services[service];
            Assert.False(node.Children.ContainsKey("build"), $"{service} builds in compose.yaml: the default is to pull");
            var image = ((YamlScalarNode)node["image"]).Value!;
            Assert.Contains($"/{name}:", image, StringComparison.Ordinal);
            Assert.Contains("${AGENT42_VERSION:-latest}", image, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheBuildOverride_BuildsBothImagesFromTheCheckout()
    {
        var services = (YamlMappingNode)Load("compose.build.yaml")["services"];
        foreach (var service in new[] { "app", "searxng" })
        {
            Assert.True(((YamlMappingNode)services[service]).Children.ContainsKey("build"), $"{service} has no build");
        }

        Assert.True(File.Exists(Path.Combine(Root, "src", "Agent42.Backend", "Dockerfile")));
        Assert.True(File.Exists(Path.Combine(Root, "infra", "searxng", "Dockerfile")));
    }

    [Fact]
    public void TheBicepDeploysTheSameImageNames_SoAnImportedImageIsFound()
    {
        var bicep = File.ReadAllText(Path.Combine(Root, "infra", "main.bicep"));
        foreach (var name in PublishedImageNames())
        {
            Assert.Contains($"= '{name}'", bicep, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ThePublishWorkflow_WritesPackages_LowercasesTheOwner_AndHardcodesNoOwner()
    {
        var workflow = Read(".github", "workflows", "publish-images.yml");

        Assert.Matches(@"(?m)^permissions:\r?\n  contents: read\r?\n  packages: write", workflow);
        // GHCR names are lowercase; the owner is not (Expecho), so the name is derived, never typed.
        Assert.Contains("${GITHUB_REPOSITORY_OWNER,,}", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("ghcr.io/expecho", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secrets.GITHUB_TOKEN", workflow, StringComparison.Ordinal);
        // Both architectures, each built natively: an arm64 build under QEMU would take far too long.
        Assert.Contains("ubuntu-24.04-arm", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void ThePublishWorkflow_Triggers_OnEveryFileTheBackendImageCopies()
    {
        var workflow = Read(".github", "workflows", "publish-images.yml");
        var paths = PathEntry().Matches(PathsBlock().Match(workflow).Groups[1].Value).Select(m => m.Groups[1].Value).ToList();
        var dockerfile = Read("src", "Agent42.Backend", "Dockerfile");

        var uncovered = CopyLine().Matches(dockerfile).Select(m => m.Groups[1].Value)
            .Where(s => !paths.Any(p => p.EndsWith("/**", StringComparison.Ordinal)
                ? s.StartsWith(p[..^2], StringComparison.Ordinal)
                : p == s.TrimEnd('/')))
            .ToArray();

        Assert.Contains(".dockerignore", paths);
        Assert.True(uncovered.Length == 0, $"publish-images.yml's push paths miss {string.Join(", ", uncovered)}");
    }

    [Fact]
    public void TheDocs_ExplainPullingAndImportingTheImages()
    {
        var local = Read("docs", "setup", "local.md");
        Assert.Contains("compose.build.yaml", local, StringComparison.Ordinal);

        var azure = Read("docs", "setup", "azure.md");
        Assert.Contains("az acr import", azure, StringComparison.Ordinal);
        Assert.Contains("ghcr.io/expecho/agent42-backend", azure, StringComparison.Ordinal);
    }

    /// <summary>The image repository names in the workflow's build matrix.</summary>
    private static List<string> PublishedImageNames() =>
        [.. MatrixImage().Matches(Read(".github", "workflows", "publish-images.yml")).Select(m => m.Groups[1].Value).Distinct()];

    private static YamlMappingNode Load(string file)
    {
        var yaml = new YamlStream();
        using var reader = new StreamReader(Path.Combine(Root, file));
        yaml.Load(reader);
        return (YamlMappingNode)yaml.Documents[0].RootNode;
    }

    private static string Read(params string[] relativePath) => File.ReadAllText(Path.Combine([Root, .. relativePath]));

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not find the repository root.");
    }

    [GeneratedRegex(@"\{ image: (agent42-[a-z]+),")]
    private static partial Regex MatrixImage();

    [GeneratedRegex(@"^    paths:\r?\n((?:\s*(?:#.*|-\s*\S+)\r?\n)+)", RegexOptions.Multiline)]
    private static partial Regex PathsBlock();

    [GeneratedRegex(@"^\s*-\s*(\S+)", RegexOptions.Multiline)]
    private static partial Regex PathEntry();

    [GeneratedRegex(@"^COPY (?!--from)(\S+) ", RegexOptions.Multiline)]
    private static partial Regex CopyLine();
}
