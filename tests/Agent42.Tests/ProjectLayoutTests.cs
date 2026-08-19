using System.Text.RegularExpressions;

namespace Agent42.Tests;

/// <summary>
/// Guards the feature-folder layout of <c>src/Agent42.Agents</c> and <c>src/Agent42.Backend</c>
/// (ADR 0039 and its Backend follow-up), by walking the source tree — the precedent set by
/// <see cref="UserTelemetryProcessorTests"/>.
///
/// <para><b>These are the primary guard, not a backstop.</b> The <c>.editorconfig</c> deliberately
/// turns IDE0130 OFF rather than on, because the layout it would enforce — namespaces matching
/// folders — is the one that breaks production (see <see cref="GrainStorageSerializerTests"/>). That
/// makes the tooling silent here by design, so these assertions are what actually holds the structure
/// together, and they express things no analyzer could: which file holds which member, and that the
/// namespace stays deliberately out of step with the folders.</para>
/// </summary>
public sealed class ProjectLayoutTests
{
    private static string ProjectDir(string project) => Path.Combine(RepoRoot(), "src", project);

    /// <summary>Hand-written sources of a project, excluding build output (obj/bin hold generated code).</summary>
    private static IEnumerable<string> SourceFiles(string project) =>
        Directory
            .EnumerateFiles(ProjectDir(project), "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    /// <summary>
    /// Each reorganised project, its required namespace, and the files allowed to remain at its root.
    /// Backend keeps <c>Program.cs</c> there because it is the composition root of the host.
    /// </summary>
    public static TheoryData<string, string, string[]> ReorganisedProjects() => new()
    {
        { "Agent42.Agents", "Agent42.Agents", [] },
        { "Agent42.Backend", "Agent42.Backend", ["Program.cs"] },
    };

    // The flat layout is gone and cannot creep back one convenient file at a time.
    [Theory]
    [MemberData(nameof(ReorganisedProjects))]
    public void OnlyTheAllowedFilesSitInTheProjectRoot(string project, string _, string[] allowedAtRoot)
    {
        var strays = Directory
            .EnumerateFiles(ProjectDir(project), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(Path.GetFileName)
            .Where(name => !allowedAtRoot.Contains(name))
            .ToList();

        Assert.True(
            strays.Count == 0,
            $"Every .cs file in {project} belongs to a feature folder (ADR 0039); found at the root: "
                + string.Join(", ", strays));
    }

    // Every file stays in the project's FLAT namespace, however deep its folder.
    //
    // This is the inverse of the usual convention and it is load-bearing, not stylistic. Orleans Cosmos
    // grain storage serializes through JsonGrainStorageSerializer, which writes the full CLR type name
    // into every document ("$type":"Agent42.Agents.AgentGrainState, Agent42.Agents"). Giving a persisted
    // type a folder-matching namespace changes that string and every document already in Cosmos fails to
    // load — it throws on the ROOT type, so nothing is recoverable. Backend holds no persisted type
    // today, but it is kept flat for the same reason one would not want to find out later: a type that
    // moves into a persisted position must not carry a folder-shaped namespace with it.
    [Theory]
    [MemberData(nameof(ReorganisedProjects))]
    public void EveryFileStaysInTheFlatNamespace(string project, string expectedNamespace, string[] allowedAtRoot)
    {
        var offenders = SourceFiles(project)
            // Backend's Program.cs is top-level statements, so it declares no namespace at all. It is
            // the one file allowed at the project root, and the same list covers both facts.
            .Where(file => !allowedAtRoot.Contains(Path.GetFileName(file)))
            .Select(file => (Name: Path.GetFileName(file), Declared: DeclaredNamespace(file)))
            .Where(x => x.Declared != expectedNamespace)
            .Select(x => $"{x.Name} declares '{x.Declared}'")
            .ToList();

        Assert.True(
            offenders.Count == 0,
            $"Every type in {project} must stay in the flat '{expectedNamespace}' namespace — a "
                + "folder-matching namespace changes the $type in every stored Cosmos document and "
                + "bricks existing data (ADR 0039). Offenders: " + string.Join("; ", offenders));
    }

    /// <summary>
    /// The feature folders of each project. <c>Skills/</c> is content rather than a feature namespace and
    /// must not move (its csproj <c>Content</c> glob resolves next to the assembly); <c>wwwroot/</c> and
    /// <c>Properties/</c> are likewise not code folders.
    /// </summary>
    public static TheoryData<string, string[]> ExpectedFolders() => new()
    {
        {
            "Agent42.Agents",
            [
                "Attachments", "Conversations", "Documents", "ImageGeneration", "Location", "Mcp",
                "Models", "Push", "Scheduling", "Sharing", "Skills", "Speech", "StoredAgents",
                "Telemetry", "Tools", "Users", "Web",
            ]
        },
        {
            "Agent42.Backend",
            [
                "Api", "Attachments", "Configuration", "Conversations", "Documents", "ImageGeneration",
                "Location", "Mcp", "Models", "Properties", "Push", "Sharing", "Speech", "StoredAgents",
                "Telemetry", "Tools", "Users", "Web", "wwwroot",
            ]
        },
    };

    // The folder set is exactly what the ADR and CLAUDE.md describe. Asserted by NAME rather than by
    // count, because prose saying "sixteen folders" beside a list of seventeen is how the docs drifted
    // the first time — a reader who adds a folder must update this list, and therefore the docs it mirrors.
    [Theory]
    [MemberData(nameof(ExpectedFolders))]
    public void FeatureFolderSet_IsExactlyWhatTheAdrDescribes(string project, string[] expected)
    {
        var actual = Directory
            .EnumerateDirectories(ProjectDir(project))
            .Select(Path.GetFileName)
            .Where(name => name is not ("bin" or "obj"))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>The ten parts of <c>AgentGrain</c> (ADR 0039) and the folder each belongs to.</summary>
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

    // Each part exists where planned and really is a part of the one AgentGrain class.
    [Theory]
    [MemberData(nameof(AgentGrainParts))]
    public void AgentGrainPart_ExistsAndIsAPartialOfTheSameClass(string relativePath)
    {
        var file = Path.Combine(ProjectDir("Agent42.Agents"), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(file), $"Missing AgentGrain part (ADR 0039): {relativePath}");
        Assert.Equal("Agent42.Agents", DeclaredNamespace(file));
        Assert.Contains("partial class AgentGrain", File.ReadAllText(file), StringComparison.Ordinal);
    }

    /// <summary>The twelve endpoint groups <c>MapAgentApi</c> was split into, and the folder each sits in.</summary>
    public static TheoryData<string, string> AgentApiParts() => new()
    {
        { "Conversations/AgentApi.Conversations.cs", "MapConversationEndpoints" },
        { "Attachments/AgentApi.Attachments.cs", "MapAttachmentEndpoints" },
        { "Sharing/AgentApi.Sharing.cs", "MapSharingEndpoints" },
        { "Models/AgentApi.Models.cs", "MapModelEndpoints" },
        { "Tools/AgentApi.Tools.cs", "MapToolEndpoints" },
        { "StoredAgents/AgentApi.StoredAgents.cs", "MapStoredAgentEndpoints" },
        { "Users/AgentApi.Users.cs", "MapUserEndpoints" },
        { "Speech/AgentApi.Speech.cs", "MapSpeechEndpoints" },
        { "Push/AgentApi.Push.cs", "MapPushEndpoints" },
        { "Configuration/AgentApi.Configuration.cs", "MapConfigurationEndpoints" },
        { "Telemetry/AgentApi.Telemetry.cs", "MapTelemetryEndpoints" },
        { "Mcp/AgentApi.Mcp.cs", "MapMcpEndpoints" },
    };

    // Each endpoint group exists, is a partial of the one AgentApi class, and — the part that matters —
    // is actually REGISTERED from the composition root. A group that exists but is never called compiles
    // cleanly and silently removes every endpoint in it, which no build or lint step would notice.
    [Theory]
    [MemberData(nameof(AgentApiParts))]
    public void AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot(string relativePath, string mapMethod)
    {
        var file = Path.Combine(ProjectDir("Agent42.Backend"), relativePath.Replace('/', Path.DirectorySeparatorChar));

        Assert.True(File.Exists(file), $"Missing AgentApi part: {relativePath}");
        Assert.Equal("Agent42.Backend", DeclaredNamespace(file));

        var part = File.ReadAllText(file);
        Assert.Contains("partial class AgentApi", part, StringComparison.Ordinal);
        Assert.Contains($"private static void {mapMethod}(IEndpointRouteBuilder app)", part, StringComparison.Ordinal);

        var root = File.ReadAllText(Path.Combine(ProjectDir("Agent42.Backend"), "Api", "AgentApi.cs"));
        Assert.Contains($"{mapMethod}(app);", root, StringComparison.Ordinal);
    }

    // The turn-scoped fields stay in the CORE AgentGrain part. Each is safe only because its declaration
    // and its clear are read together (see the _turnLocation and _ephemeral entries in gotchas.md);
    // splitting a declaration out to a feature part is the specific regression this catches. Scoped to
    // these three names on purpose — three static/const members legitimately move with their feature, so
    // a general "only the core declares members" assertion would be false.
    [Theory]
    [InlineData("_turnLocation")]
    [InlineData("_ephemeral")]
    [InlineData("_interactiveTurn")]
    public void TurnScopedField_IsDeclaredInTheCorePart(string field)
    {
        var core = Path.Combine(ProjectDir("Agent42.Agents"), "Conversations", "AgentGrain.cs");

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
