using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the feature-folder layout of <c>src/Agent42.Agents</c> (ADR 0039) by walking the source
/// tree, following the precedent in <see cref="UserTelemetryProcessorTests"/>.
///
/// <para>This is deliberately a SECOND guard. The primary one is the scoped IDE0130 rule in the root
/// <c>.editorconfig</c>, which <c>dotnet format</c> enforces in CI — but that rule is one
/// <c>.editorconfig</c> edit away from being switched off, and it cannot express the assertions below
/// about which file holds which member. A tool-independent check is what keeps the structure from
/// eroding quietly.</para>
/// </summary>
public sealed class ProjectLayoutTests
{
    private const string AgentsProject = "Agent42.Agents";

    /// <summary>Files under the project, excluding build output (obj/bin hold generated sources).</summary>
    private static IEnumerable<string> AgentsSourceFiles() =>
        Directory
            .EnumerateFiles(AgentsProjectDir(), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static string AgentsProjectDir() => Path.Combine(RepoRoot(), "src", AgentsProject);

    // AC5(a): the flat layout is gone and cannot creep back one convenient file at a time.
    [Fact]
    public void NoSourceFileSitsInTheProjectRoot()
    {
        var strays = Directory.EnumerateFiles(AgentsProjectDir(), "*.cs", SearchOption.TopDirectoryOnly).ToList();

        Assert.True(
            strays.Count == 0,
            "Every .cs file in Agent42.Agents belongs to a feature folder (ADR 0039); found at the root: "
                + string.Join(", ", strays.Select(Path.GetFileName)));
    }

    /// <summary>
    /// The feature folders of ADR 0039, plus <c>Skills/</c>, which is content rather than a feature
    /// namespace and must not move (its csproj <c>Content</c> glob resolves next to the assembly).
    /// </summary>
    private static readonly string[] ExpectedFolders =
    [
        "Attachments", "Conversations", "Documents", "ImageGeneration", "Location", "Mcp", "Models",
        "Push", "Scheduling", "Sharing", "Skills", "Speech", "StoredAgents", "Telemetry", "Tools",
        "Users", "Web",
    ];

    // AC5: the folder set is exactly what ADR 0039 and CLAUDE.md describe. Asserted by name rather than
    // by count because prose that says "sixteen folders" next to a list of seventeen is how the docs
    // drifted in the first place — a reader who adds a folder has to update this list, and therefore
    // the docs it mirrors.
    [Fact]
    public void FeatureFolderSet_IsExactlyWhatTheAdrDescribes()
    {
        var actual = Directory
            .EnumerateDirectories(AgentsProjectDir())
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(ExpectedFolders, actual);
    }

    // AC5(b): folder and namespace agree. AgentGrain.*.cs is the one documented exception — a partial
    // class cannot span namespaces, so every part declares Agent42.Agents.Conversations wherever it sits.
    // That exception is asserted positively below rather than merely skipped here.
    [Fact]
    public void EveryFileDeclaresTheNamespaceOfItsFolder()
    {
        var root = AgentsProjectDir();
        var mismatches = new List<string>();

        foreach (var file in AgentsSourceFiles())
        {
            var name = Path.GetFileName(file);
            if (name.StartsWith("AgentGrain.", StringComparison.Ordinal) || name == "AgentGrain.cs")
            {
                continue;
            }

            var folders = Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[..^1];
            var expected = string.Join('.', new[] { "Agent42", "Agents" }.Concat(folders));
            var declared = DeclaredNamespace(file);

            if (declared != expected)
            {
                mismatches.Add($"{name}: declares '{declared}', folder implies '{expected}'");
            }
        }

        Assert.True(
            mismatches.Count == 0,
            "Namespace must match folder (ADR 0039): " + string.Join("; ", mismatches));
    }

    /// <summary>The ten parts of <c>AgentGrain</c> and the folder each belongs to (ADR 0039).</summary>
    public static TheoryData<string> AgentGrainParts() =>
    [
        "Conversations/AgentGrain.cs",
        "Conversations/AgentGrain.FollowUps.cs",
        "Tools/AgentGrain.BuildAgent.cs",
        "Documents/AgentGrain.Documents.cs",
        "Web/AgentGrain.WebTools.cs",
        "Location/AgentGrain.LocationTool.cs",
        "Scheduling/AgentGrain.Scheduling.cs",
        "ImageGeneration/AgentGrain.ImageTurn.cs",
        "Attachments/AgentGrain.Attachments.cs",
        "Push/AgentGrain.Push.cs",
    ];

    // AC6(c): each part exists where planned, and all of them declare the ONE namespace of the class
    // they complete — the deliberate IDE0130 exception, checked here so it stays deliberate.
    [Theory]
    [MemberData(nameof(AgentGrainParts))]
    public void AgentGrainPart_ExistsAndDeclaresTheConversationsNamespace(string relativePath)
    {
        var file = Path.Combine(AgentsProjectDir(), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(file), $"Missing AgentGrain part (ADR 0039): {relativePath}");
        Assert.Equal("Agent42.Agents.Conversations", DeclaredNamespace(file));
        Assert.Contains("partial class AgentGrain", File.ReadAllText(file), StringComparison.Ordinal);
    }

    // AC6(d): the turn-scoped fields stay in the CORE part. Each is safe only because its declaration
    // and its clear are read together (see the _turnLocation and _ephemeral entries in gotchas.md);
    // splitting a declaration out to a feature part is the specific regression this catches.
    // Scoped to these three names on purpose — three static/const members legitimately move with their
    // feature, so a general "only the core declares members" assertion would be false.
    [Theory]
    [InlineData("_turnLocation")]
    [InlineData("_ephemeral")]
    [InlineData("_interactiveTurn")]
    public void TurnScopedField_IsDeclaredInTheCorePart(string field)
    {
        var core = Path.Combine(AgentsProjectDir(), "Conversations", "AgentGrain.cs");

        Assert.Matches($@"private\s+[^;\r\n]*\s{Regex.Escape(field)}\s*;", File.ReadAllText(core));
    }

    private static string DeclaredNamespace(string file)
    {
        var match = Regex.Match(File.ReadAllText(file), @"^namespace\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline);

        Assert.True(match.Success, $"No file-scoped namespace declaration found in {file}");
        return match.Groups[1].Value;
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
