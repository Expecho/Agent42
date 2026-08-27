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
    /// <c>Properties/</c> are likewise not code folders. Note <c>UserSkills/</c> beside it: uploaded skills
    /// (ADR 0049) deliberately do NOT live under <c>Skills/</c>, because a <c>.cs</c> file there would be
    /// copied into the deployed <c>Skills/</c> directory by that same glob and handed to
    /// <c>AgentFileSkillsSource</c>.
    /// </summary>
    public static TheoryData<string, string[]> ExpectedFolders() => new()
    {
        {
            "Agent42.Agents",
            [
                "Attachments", "Calendar", "Conversations", "Documents", "ImageGeneration", "Location",
                "Mcp", "Models", "Push", "Scheduling", "Sharing", "Skills", "Speech", "StoredAgents",
                "Telemetry", "Tools", "UserSkills", "Users", "Web",
            ]
        },
        {
            "Agent42.Backend",
            [
                "Api", "Attachments", "Calendar", "Configuration", "Conversations", "Documents",
                "ImageGeneration", "Location", "Mcp", "Models", "Properties", "Push", "Sharing",
                "Speech", "StoredAgents", "Telemetry", "Tools", "UserSkills", "Users", "Web", "wwwroot",
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

    /// <summary>The eleven parts of <c>AgentGrain</c> (ADR 0039) and the folder each belongs to.</summary>
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
        "Calendar/AgentGrain.CalendarTools.cs",
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

    /// <summary>The fifteen endpoint groups <c>MapAgentApi</c> was split into, and the folder each sits in.</summary>
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
        { "Telemetry/AgentApi.Diagnostics.cs", "MapDiagnosticsEndpoints" },
        { "Mcp/AgentApi.Mcp.cs", "MapMcpEndpoints" },
        { "UserSkills/AgentApi.UserSkills.cs", "MapUserSkillEndpoints" },
        { "Calendar/AgentApi.Calendar.cs", "MapCalendarEndpoints" },
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

    /* ————— frontend: the ES-module split of app.js (ADR 0041) ————— */

    private static string JsDir() => Path.Combine(ProjectDir("Agent42.Backend"), "wwwroot", "js");

    private static string[] JsModules() =>
        Directory.EnumerateFiles(JsDir(), "*.js").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray()!;

    // All four specifier forms — a module reachable only by a side-effect `import "./x.js"` must not read
    // as an orphan. Same expression as StaticAssetsServerFixture's walker, deliberately: the corpus the
    // asset guards read and the graph this test calls complete are the same graph.
    private static readonly Regex JsSpecifier =
        new("""(?:\bfrom\s*|\bimport\s*)["']\./([^"']+)["']""", RegexOptions.Compiled);

    /// <summary>
    /// Every feature module and the entry point it must expose to <c>main.js</c>. A NAME table, not a
    /// directory scan: a scan passes trivially if a module drops its <c>wire*()</c> and registers its
    /// listeners at top level again — the very thing the composition root exists to prevent. Same
    /// reasoning as <see cref="FeatureFolderSet_IsExactlyWhatTheAdrDescribes"/>.
    /// </summary>
    public static TheoryData<string, string> FrontEndWiredModules() => new()
    {
        { "agents.js", "wireAgents" },
        { "attachments.js", "wireAttachments" },
        { "calendar.js", "wireCalendar" },
        // calendar.js has TWO entry points, for the same reason mcp.js does: dropping the
        // initGoogleOAuthReturn() call silently kills the ADR 0053 connect return, so a user who has
        // just consented at Google lands on a blank app with no idea whether it worked.
        { "calendar.js", "initGoogleOAuthReturn" },
        { "composer.js", "wireComposer" },
        { "confighealth.js", "wireConfigHealth" },
        { "conversations.js", "wireConversations" },
        { "drawer.js", "wireDrawer" },
        { "location.js", "initLocation" },
        { "mcp.js", "wireMcp" },
        // mcp.js has TWO entry points, and both need a row. Dropping the initMcpOAuthReturn() call
        // silently kills the ADR 0025 interactive-OAuth return — and with it the profileTabs.select(3)
        // deep-link that ADR 0029's "Configuration tab is appended LAST" invariant exists to protect.
        { "mcp.js", "initMcpOAuthReturn" },
        { "profile.js", "wireProfile" },
        { "providers.js", "wireProviders" },
        { "push.js", "initPush" },
        { "pwa.js", "wirePwa" },
        { "send.js", "wireSend" },
        { "sharelinks.js", "wireShareLinks" },
        { "sharetarget.js", "initShare" },
        { "skills.js", "wireSkills" },
        { "speech.js", "wireSpeech" },
        { "theme.js", "initTheme" },
        { "tools.js", "wireTools" },
        { "usage.js", "wireUsage" },
        { "voice.js", "wireVoice" },
    };

    // The frontend analogue of AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot (ADR 0040): a
    // module whose wire*() is never called from main.js loads fine, throws nothing, and silently attaches
    // no listeners — that feature's buttons just do nothing, and no build, lint or type check sees it.
    [Theory]
    [MemberData(nameof(FrontEndWiredModules))]
    public void FrontEndModule_ExportsItsEntryPoint_AndIsCalledFromTheCompositionRoot(string file, string entryPoint)
    {
        var path = Path.Combine(JsDir(), file);
        Assert.True(File.Exists(path), $"Missing front-end module: wwwroot/js/{file}");

        var module = File.ReadAllText(path);
        Assert.Matches($@"export\s+(async\s+)?function\s+{Regex.Escape(entryPoint)}\s*\(", module);

        // The full statement, not just the name followed by "(" — matching the ADR 0040 exemplar this
        // mirrors (`Assert.Contains($"{mapMethod}(app);", …)`). A bare name+"(" is satisfied by a
        // commented-out call, and main.js's own header comment discusses wire*() in prose.
        var main = File.ReadAllText(Path.Combine(JsDir(), "main.js"));
        Assert.Contains($"{entryPoint}();", main, StringComparison.Ordinal);
    }

    // The service worker's SHELL must equal the module directory EXACTLY — both directions.
    //
    // sw.js installs with cache.addAll(SHELL), which is all-or-nothing, so each direction fails
    // differently and both are invisible online:
    //   missing entry → the module isn't cached → the installed PWA breaks OFFLINE only, and ADR 0032's
    //                   IndexedDB mirror happily paints cached conversations into a shell whose JS failed;
    //   stale entry   → addAll rejects → the SW never activates AT ALL, taking down the offline read
    //                   cache (ADR 0032), the Web Share Target (ADR 0023) and Web Push (ADR 0028).
    [Fact]
    public void ServiceWorkerShell_ListsExactlyTheModuleDirectory()
    {
        var sw = File.ReadAllText(Path.Combine(ProjectDir("Agent42.Backend"), "wwwroot", "sw.js"));
        var shell = Regex.Match(sw, @"const SHELL = \[(.*?)\];", RegexOptions.Singleline);
        Assert.True(shell.Success, "sw.js no longer declares a SHELL array");

        var listed = Regex.Matches(shell.Groups[1].Value, @"""/js/([^""]+)""")
            .Select(m => m.Groups[1].Value)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(JsModules(), listed);
    }

    // No orphans: every module under wwwroot/js is reachable from main.js. An unreferenced module is dead
    // code that is also invisible to the asset guards — StaticAssetsServerFixture.GetAppScriptAsync()
    // walks this same import graph, so a file outside it contributes nothing to their corpus.
    [Fact]
    public void EveryFrontEndModule_IsReachableFromTheEntryPoint()
    {
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(["main.js"]);
        while (pending.Count > 0)
        {
            var file = pending.Pop();
            if (!reachable.Add(file))
            {
                continue;
            }

            foreach (Match match in JsSpecifier.Matches(File.ReadAllText(Path.Combine(JsDir(), file))))
            {
                pending.Push(match.Groups[1].Value);
            }
        }

        Assert.Equal(JsModules(), reachable.Order(StringComparer.Ordinal).ToArray());
    }

    // Every named import must actually be exported by its target module.
    //
    // This is the one static error that produces a BLANK app rather than a degraded one: the browser
    // throws "SyntaxError: The requested module './x.js' does not provide an export named 'y'", module
    // evaluation aborts, and nothing wires at all. Nothing else catches it — there is no JS build step,
    // and a per-file linter validates each module in isolation, never the edges between them. It caught
    // two real cases (`renderAgents`, `loadTools`) during the ADR 0041 split itself.
    [Fact]
    public void EveryNamedImport_IsActuallyExportedByItsTargetModule()
    {
        var exported = JsModules().ToDictionary(
            file => file,
            file => ExportedNames(File.ReadAllText(Path.Combine(JsDir(), file))),
            StringComparer.Ordinal);

        var broken = new List<string>();
        foreach (var file in JsModules())
        {
            var source = File.ReadAllText(Path.Combine(JsDir(), file));
            foreach (Match import in Regex.Matches(source, @"import\s*\{([^}]*)\}\s*from\s*[""']\./([^""']+)[""']"))
            {
                var target = import.Groups[2].Value;
                if (!exported.TryGetValue(target, out var names))
                {
                    broken.Add($"{file} imports from ./{target}, which does not exist");
                    continue;
                }

                foreach (var name in import.Groups[1].Value
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(entry => entry.Split(" as ")[0].Trim())
                             .Where(name => name.Length > 0 && !names.Contains(name)))
                {
                    broken.Add($"{file} imports {{ {name} }} from ./{target}, which does not export it");
                }
            }
        }

        Assert.True(broken.Count == 0, string.Join("\n", broken));
    }

    private static HashSet<string> ExportedNames(string source)
    {
        var names = Regex
            .Matches(source, @"^export\s+(?:async\s+)?(?:function|const|let|class)\s+([A-Za-z_$][\w$]*)", RegexOptions.Multiline)
            .Select(match => match.Groups[1].Value)
            .ToHashSet(StringComparer.Ordinal);

        // `export { a, b as c }` — the exported name is what follows `as`, when there is one.
        foreach (Match list in Regex.Matches(source, @"^export\s*\{([^}]*)\}", RegexOptions.Multiline))
        {
            foreach (var entry in list.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                names.Add(entry.Split(" as ")[^1].Trim());
            }
        }

        return names;
    }

    // The monolith is gone and cannot creep back: app.js was deleted, and every doc reference repointed.
    [Fact]
    public void TheMonolithicAppScript_IsGone()
    {
        Assert.False(
            File.Exists(Path.Combine(ProjectDir("Agent42.Backend"), "wwwroot", "app.js")),
            "wwwroot/app.js is back — the frontend lives in wwwroot/js/*.js (ADR 0041)");
    }

    // Nothing in the suite runs Program.cs, so the ordering that makes Request.Scheme trustworthy behind the
    // ACA ingress (ADR 0042) can only be asserted on the source text — the *AssetsTests heuristic. It has to
    // come before UserTelemetryMiddleware and, crucially, before UseDefaultFiles: the framework's own 301
    // builds an absolute URL from the scheme, and that redirect is what exposed the bug.
    [Fact]
    public void Program_TrustsForwardedProtoBeforeAnyOtherMiddleware()
    {
        // Comment lines are stripped first: this file explains the `KnownNetworks = { }` trap in prose, and
        // an assertion that cannot tell code from the comment warning about it is worse than no assertion.
        var program = string.Join(
            Environment.NewLine,
            File.ReadAllLines(Path.Combine(ProjectDir("Agent42.Backend"), "Program.cs"))
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        var forwarded = program.IndexOf("app.UseForwardedHeaders(", StringComparison.Ordinal);
        Assert.True(forwarded >= 0, "Program.cs no longer calls UseForwardedHeaders — share URLs revert to http://");

        Assert.Contains("ForwardedHeaders = ForwardedHeaders.XForwardedProto", program, StringComparison.Ordinal);

        // The trust lists must be emptied by CALLING Clear(). `KnownNetworks = { }` compiles (they are
        // get-only, so that parses as a collection initializer adding nothing), reads as "cleared", and
        // leaves the 127.0.0.0/8 + ::1 defaults in place — which makes the middleware inert behind a
        // non-loopback ingress and silently reverts every minted URL to http://. Measured, not assumed.
        Assert.Contains("KnownIPNetworks.Clear()", program, StringComparison.Ordinal);
        Assert.Contains("KnownProxies.Clear()", program, StringComparison.Ordinal);
        Assert.DoesNotContain("KnownNetworks = {", program, StringComparison.Ordinal);
        Assert.DoesNotContain("KnownProxies = {", program, StringComparison.Ordinal);

        // XForwardedFor/XForwardedHost would trust a strictly larger surface from an unidentified proxy, and
        // no consumer in this app reads either.
        Assert.DoesNotContain("XForwardedFor", program, StringComparison.Ordinal);
        Assert.DoesNotContain("XForwardedHost", program, StringComparison.Ordinal);

        foreach (var later in new[] { "app.Use(UserTelemetryMiddleware.InvokeAsync)", "app.UseDefaultFiles()", "app.MapAgentApi()" })
        {
            var index = program.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index > forwarded, $"{later} runs before UseForwardedHeaders — the scheme is still http there.");
        }
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
