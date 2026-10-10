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
                "Artifacts", "Attachments", "ClientTools", "Conversations", "Copilot", "Documents", "Email", "ImageGeneration",
                "Location", "Mcp", "Memory", "Models", "Push", "Research", "Scheduling", "Search", "SharedState",
                "Sharing", "Skills", "Speech", "StoredAgents", "Telemetry", "Tools", "UserSkills", "Users", "Web", "Webhooks",
            ]
        },
        {
            "Agent42.Backend",
            [
                "Api", "Artifacts", "Attachments", "ClientTools", "Configuration", "Conversations", "Copilot",
                "Documents", "Email", "Export", "ImageGeneration", "Location", "Mcp", "Memory", "Models", "Orleans", "Properties", "Push",
                "Search", "SharedState", "Sharing", "Speech", "StoredAgents", "Telemetry", "Tools", "UserSkills",
                "Users", "Web", "Webhooks", "wwwroot",
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

    /// <summary>The parts of <c>AgentGrain</c> (ADR 0039) and the folder each belongs to.</summary>
    public static TheoryData<string> AgentGrainParts() =>
    [
        "Conversations/AgentGrain.cs",
        "Conversations/AgentGrain.FollowUps.cs",
        "Conversations/AgentGrain.Retention.cs",
        "Tools/AgentGrain.BuildAgent.cs",
        "Documents/AgentGrain.Documents.cs",
        "Documents/AgentGrain.WebDocumentTool.cs",
        "Web/AgentGrain.WebTools.cs",
        "Location/AgentGrain.LocationTool.cs",
        "Scheduling/AgentGrain.Scheduling.cs",
        "ImageGeneration/AgentGrain.ImageTurn.cs",
        "Attachments/AgentGrain.Attachments.cs",
        "Push/AgentGrain.Push.cs",
        "Push/AgentGrain.PushTool.cs",
        "Memory/AgentGrain.Memory.cs",
        "Search/AgentGrain.Search.cs",
        "Research/AgentGrain.Research.cs",
        "Artifacts/AgentGrain.ArtifactTools.cs",
        "Copilot/AgentGrain.Copilot.cs",
        "ClientTools/AgentGrain.ClientTools.cs",
        "SharedState/AgentGrain.SharedState.cs",
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

    /// <summary>The endpoint groups <c>MapAgentApi</c> was split into, and the folder each sits in.</summary>
    public static TheoryData<string, string> AgentApiParts() => new()
    {
        { "Conversations/AgentApi.Conversations.cs", "MapConversationEndpoints" },
        { "Conversations/AgentApi.ChatGroups.cs", "MapChatGroupEndpoints" },
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
        { "Web/AgentApi.Web.cs", "MapWebEndpoints" },
        { "Search/AgentApi.Search.cs", "MapSearchEndpoints" },
        { "ClientTools/AgentApi.ClientTools.cs", "MapClientToolEndpoints" },
        { "SharedState/AgentApi.SharedState.cs", "MapSharedStateEndpoints" },
        { "Email/AgentApi.Email.cs", "MapEmailEndpoints" },
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
    // and its clear are read together (see the turn-scoped-field and _ephemeral entries in gotchas.md);
    // splitting a declaration out to a feature part is the specific regression this catches. Scoped to
    // these names on purpose — three static/const members legitimately move with their feature, so
    // a general "only the core declares members" assertion would be false.
    [Theory]
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
        { "clienttools.js", "wireClientTools" },
        { "composer.js", "wireComposer" },
        { "confighealth.js", "wireConfigHealth" },
        { "conversations.js", "wireConversations" },
        { "drawer.js", "wireDrawer" },
        { "editfork.js", "wireEditFork" },
        { "groups.js", "wireGroups" },
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
        { "sharedstate.js", "wireSharedState" },
        { "sharelinks.js", "wireShareLinks" },
        { "sharetarget.js", "initShare" },
        { "skills.js", "wireSkills" },
        { "speech.js", "wireSpeech" },
        { "theme.js", "initTheme" },
        { "tools.js", "wireTools" },
        { "usage.js", "wireUsage" },
        { "voice.js", "wireVoice" },
        { "webhooks.js", "wireWebhooks" },
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
        var program = StrippedSource(Path.Combine(ProjectDir("Agent42.Backend"), "Program.cs"));

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

    /// <summary>
    /// AC 12 + AC 13 (ADR 0062), and the load-bearing guard of the whole feature.
    ///
    /// <para>Nothing in the suite runs <c>Program.cs</c>, so an edit that maps the Orleans dashboard
    /// while dropping the owner gate would compile, pass every test, and ship a live cluster console —
    /// grain activations, silo health, reminder names, conversation ids — to every Easy-Auth-allowlisted
    /// user. That is the failure that actually matters here, and this source-text assertion is the only
    /// thing standing in front of it. Mutation check: delete the
    /// <c>app.UseOrleansDashboardOwnerGate()</c> line from <c>Program.cs</c> and this must go red.</para>
    ///
    /// <para>It also pins that the raw package call is made in exactly ONE place. Both hosts go through
    /// the bundled extensions, so the gate and the map cannot drift apart per host.</para>
    /// </summary>
    [Fact]
    public void Program_GatesTheOrleansDashboardBeforeServingIt()
    {
        var program = StrippedSource(Path.Combine(ProjectDir("Agent42.Backend"), "Program.cs"));

        var gate = program.IndexOf("app.UseOrleansDashboardOwnerGate()", StringComparison.Ordinal);
        Assert.True(gate >= 0, "Program.cs no longer installs the dashboard owner gate — /dashboard is open to every allowlisted user");

        var map = program.IndexOf("app.MapOrleansDashboardUi()", StringComparison.Ordinal);
        Assert.True(map >= 0, "Program.cs no longer maps the Orleans dashboard");

        // Before the static-file middleware. It works below them today only because no wwwroot/dashboard/
        // exists; anyone later adding one would have it served ungated while the gate still looked installed.
        foreach (var later in new[] { "app.UseDefaultFiles()", "app.UseStaticFiles()", "app.MapOrleansDashboardUi()" })
        {
            var index = program.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index > gate, $"{later} runs before the dashboard owner gate — /dashboard would be reachable ungated.");
        }

        // AC13: the package's own map call appears exactly once in the repo, inside DashboardRegistration.
        // A second call site is a second place to forget the gate.
        // Production code only (src/ + tools/). Scoping to the whole repo would match this very file,
        // which names the call in its own assertion.
        var callSites = new[] { "src", "tools" }
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), root), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StrippedSource(f).Contains("MapOrleansDashboard(", StringComparison.Ordinal))
            .Select(f => Path.GetFileName(f))
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal<string>(["DashboardRegistration.cs"], callSites);
    }

    /// <summary>
    /// AC16 (ADR 0070). The server-side MCP surface is mapped from each host's composition root and is
    /// deliberately <b>not</b> an <c>AgentApi.*</c> group.
    ///
    /// <para>Ten call sites invoke <c>MapAgentApi</c> — two hosts and eight test fixtures — and minimal API
    /// infers every mapped endpoint's parameters up front, so a dependency one host has not registered
    /// breaks routing for <i>every</i> endpoint in that host. <c>MapMcp</c> throws outright without
    /// <c>AddMcpServer().WithHttpTransport()</c>, so folding it into <c>MapAgentApi</c> would take down
    /// eight fixtures at once. Consequently
    /// <see cref="AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot"/> does not apply here and must
    /// not be extended to cover it — this test is what records that as a decision rather than an omission.
    /// </para>
    /// </summary>
    [Fact]
    public void AgentMcp_IsNotMappedFromTheApiCompositionRoot()
    {
        var backend = ProjectDir("Agent42.Backend");

        var compositionRoot = StrippedSource(Path.Combine(backend, "Api", "AgentApi.cs"));
        Assert.DoesNotContain("MapAgentMcpServer", compositionRoot, StringComparison.Ordinal);
        Assert.DoesNotContain("MapMcp(", compositionRoot, StringComparison.Ordinal);

        // And no AgentApi partial maps it either — the group could otherwise be reached indirectly.
        foreach (var part in Directory.EnumerateFiles(backend, "AgentApi.*.cs", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("MapMcp(", StrippedSource(part), StringComparison.Ordinal);
        }

        // The SDK's own map call appears exactly once in production code, inside the registration file —
        // a second call site is a second place to forget the gate. Scoped to src/ + tools/ because
        // scoping to the repo would match this very file, which names the call in its own assertion.
        var callSites = new[] { "src", "tools" }
            .SelectMany(root => Directory.EnumerateFiles(
                Path.Combine(RepoRoot(), root), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => StrippedSource(f).Contains("MapMcp(", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal<string>(["AgentMcpServerRegistration.cs"], callSites!);
    }

    /// <summary>
    /// AC16, second half (ADR 0070). Every host that MAPS the surface must also install the key gate, and
    /// install it above the static-file middleware.
    ///
    /// <para>Mapping without the gate compiles cleanly and serves every exposed agent to anyone who can
    /// reach the URL — and since both entries are on <c>excludedPaths</c>, that is the public internet. No
    /// test executes <c>Program.cs</c>, so this source-text guard is the only thing standing between that
    /// mistake and production, exactly as <see cref="Program_GatesTheOrleansDashboardBeforeServingIt"/> is
    /// for the dashboard.</para>
    /// </summary>
    [Theory]
    [InlineData("src/Agent42.Backend/Program.cs")]
    [InlineData("tools/Agent42.DevHost/Program.cs")]
    public void Hosts_GateTheAgentMcpSurfaceBeforeServingIt(string relativePath)
    {
        var program = StrippedSource(
            Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var gate = program.IndexOf("app.UseAgentMcpKeyGate()", StringComparison.Ordinal);
        Assert.True(gate >= 0,
            $"{relativePath} no longer installs the MCP key gate — /api/agent-mcp is on excludedPaths, so the surface would be anonymously reachable.");

        Assert.Contains("AddAgentMcpServer()", program, StringComparison.Ordinal);

        foreach (var later in new[] { "app.UseDefaultFiles()", "app.UseStaticFiles()", "app.MapAgentMcpServer()" })
        {
            var index = program.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index > gate, $"{later} runs before the MCP key gate in {relativePath}.");
        }
    }

    /// <summary>
    /// AC22 (ADR 0076). Both silo hosts must register the local bridge and map its endpoint, and
    /// <c>AddLocalBridge()</c> must come <b>after</b> the inner <c>IMcpConnectionFactory</c> it decorates —
    /// registered before it, the decorator would wrap nothing and throw at startup, or (were that throw ever
    /// softened) the later registration would silently win and every bridge server would read as "not
    /// connected" forever. The placement beside the other excluded-path wiring (above the static-file
    /// middleware) is a readability convention rather than a correctness rule — the route is an endpoint and
    /// only its <c>UseWebSockets</c> half is order-sensitive — but it keeps every public surface in one place.
    /// No test executes <c>Program.cs</c>, hence source text.
    /// </summary>
    [Theory]
    [InlineData("src/Agent42.Backend/Program.cs")]
    [InlineData("tools/Agent42.DevHost/Program.cs")]
    public void Hosts_RegisterLocalBridge(string relativePath)
    {
        var program = StrippedSource(
            Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        var inner = program.IndexOf("AddSingleton<IMcpConnectionFactory", StringComparison.Ordinal);
        var decorator = program.IndexOf("AddLocalBridge()", StringComparison.Ordinal);
        Assert.True(inner >= 0, $"{relativePath} registers no IMcpConnectionFactory.");
        Assert.True(decorator > inner, $"{relativePath} must call AddLocalBridge() after registering IMcpConnectionFactory.");
        Assert.Equal(decorator, program.LastIndexOf("AddLocalBridge()", StringComparison.Ordinal));
        Assert.Equal(inner, program.LastIndexOf("AddSingleton<IMcpConnectionFactory", StringComparison.Ordinal));

        var use = program.IndexOf("app.UseLocalBridge()", StringComparison.Ordinal);
        Assert.True(use >= 0, $"{relativePath} does not map the local-bridge endpoint.");
        foreach (var later in new[] { "app.UseDefaultFiles()", "app.UseStaticFiles()" })
        {
            Assert.True(program.IndexOf(later, StringComparison.Ordinal) > use, $"{later} runs before UseLocalBridge() in {relativePath}.");
        }
    }

    /// <summary>
    /// AC 10 (ADR 0062). Both silo hosts must register the dashboard — the endpoints exist either way,
    /// but with no <c>AddDashboard()</c> on the silo they have no cluster services to read, so the page
    /// fails at runtime rather than at build time.
    /// </summary>
    [Theory]
    // DevHost lives under tools/, not src/, so these are repo-relative rather than ProjectDir() calls.
    [InlineData("src/Agent42.Backend/Program.cs")]
    [InlineData("tools/Agent42.DevHost/Program.cs")]
    public void Hosts_RegisterTheOrleansDashboard(string relativePath)
    {
        var source = StrippedSource(
            Path.Combine(RepoRoot(), relativePath.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Contains("silo.AddDashboard()", source, StringComparison.Ordinal);

        var gate = source.IndexOf("app.UseOrleansDashboardOwnerGate()", StringComparison.Ordinal);
        Assert.True(gate >= 0, $"{relativePath} does not install the dashboard owner gate");
        Assert.Contains("app.MapOrleansDashboardUi()", source, StringComparison.Ordinal);

        // The gate-before-static-files ordering is a property of BOTH hosts, not just the Backend.
        // DevHost's owner is `local-dev` so it is open by design and the blast radius is small — but the
        // ADR claims the ordering is asserted rather than merely documented, and an unqualified claim
        // that holds for one host only is the kind that rots. Same reasoning as the Backend guard: below
        // the static-file middleware it works only while no wwwroot/dashboard/ exists.
        foreach (var later in new[] { "app.UseDefaultFiles()", "app.UseStaticFiles()", "app.MapOrleansDashboardUi()" })
        {
            var index = source.IndexOf(later, StringComparison.Ordinal);
            Assert.True(index > gate, $"{relativePath}: {later} runs before the dashboard owner gate.");
        }
    }

    // Comment lines stripped first: this repo explains its own traps in dense prose, and an assertion
    // that cannot tell code from the comment warning about it is worse than no assertion.
    private static string StrippedSource(string path) => string.Join(
        Environment.NewLine,
        File.ReadAllLines(path).Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string DeclaredNamespace(string file)
    {
        var match = Regex.Match(File.ReadAllText(file), @"^namespace\s+([A-Za-z0-9_.]+)\s*;", RegexOptions.Multiline);

        Assert.True(match.Success, $"No file-scoped namespace declaration found in {file}");
        return match.Groups[1].Value;
    }

    /// <summary>
    /// AC16 (ADR 0074). <c>Program.cs</c> must BUILD the identity alias map, REGISTER it, and have the
    /// telemetry enrichment callback CAPTURE that local.
    ///
    /// <para>This guard is required rather than decorative. <c>CurrentUser.GetUserId</c> resolves the map
    /// <em>optionally</em> (<c>GetService</c>), so forgetting the registration does not fail anything: the
    /// alias simply never applies, the second Entra account lands on an empty dataset, and every runtime
    /// test still passes — because nothing in this suite executes <c>Program.cs</c>. Source text is the
    /// only thing standing between that mistake and production, exactly as
    /// <see cref="Hosts_GateTheAgentMcpSurfaceBeforeServingIt"/> is for the MCP key gate.</para>
    ///
    /// <para>The capture is asserted separately from the registration because they fail differently: the
    /// enrichment callback receives an <c>HttpRequest</c> and runs at <c>HttpRequestIn.Start</c>, before
    /// the middleware pipeline, so resolving DI inside it would leave the server span carrying the raw
    /// principal id while every other surface carried the canonical one.</para>
    /// </summary>
    [Fact]
    public void Program_BuildsAndRegistersTheUserIdAliasMap()
    {
        var program = StrippedSource(
            Path.Combine(RepoRoot(), "src", "Agent42.Backend", "Program.cs"));

        Assert.Contains(
            "UserIdAliases.FromConfiguration(builder.Configuration)", program, StringComparison.Ordinal);

        Assert.Contains(
            "builder.Services.AddSingleton(userIdAliases)", program, StringComparison.Ordinal);

        Assert.Contains(
            "activity.ApplyUserTelemetryTags(request.Headers, userIdAliases)",
            program,
            StringComparison.Ordinal);
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
