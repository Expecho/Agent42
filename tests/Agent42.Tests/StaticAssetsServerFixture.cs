using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Agent42.Tests;

/// <summary>
/// Serves the real Backend <c>wwwroot</c> through a TestServer using the same
/// <c>UseDefaultFiles()</c> / <c>UseStaticFiles()</c> pair as Program.cs, so the static-asset
/// contracts the browser depends on (PWA installability, the composer's speech-input wiring)
/// are exercised end to end against the physical files. Shared across the asset-guardrail test
/// classes via a collection fixture so the server is built once. Production/DevHost serve the
/// same files via the SDK static-web-assets provider; both resolve MIME through the default
/// FileExtensionContentTypeProvider, so the content-type contract is equivalent.
/// </summary>
public sealed class StaticAssetsServerFixture : IAsyncLifetime
{
    private WebApplication _app = null!;

    public HttpClient Client { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Content root = the Backend project dir, so the web root resolves to its wwwroot
        // (set at build time — WebApplicationBuilder forbids changing it afterwards).
        var appBuilder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
        {
            ContentRootPath = ResolveBackendDir(),
        });
        appBuilder.WebHost.UseTestServer();
        _app = appBuilder.Build();
        _app.UseDefaultFiles();
        _app.UseStaticFiles();
        await _app.StartAsync();
        Client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await _app.DisposeAsync();
    }

    /// <summary>
    /// The whole front-end script as one string: <c>/js/main.js</c> plus every module reachable from
    /// it, fetched over HTTP and concatenated. This is the seam the source-text asset guards read
    /// (ADR 0041) — before the ES-module split they each did <c>GET /app.js</c>.
    /// <para>
    /// Fetching over HTTP (rather than reading <c>wwwroot</c> off disk) keeps the original meaning:
    /// an assertion still fails if a module exists but is not actually SERVED. Walking the *import
    /// graph* (rather than listing the directory) means an orphaned file — one no module imports —
    /// is correctly excluded from the corpus rather than silently propping an assertion up; the
    /// orphan itself is caught separately by <c>ProjectLayoutTests</c>.
    /// </para>
    /// </summary>
    public async Task<string> GetAppScriptAsync()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var script = new StringBuilder();
        await AppendModuleAsync("main.js", visited, script);
        return script.ToString();
    }

    // All four specifier forms, or a module reachable only by a side-effect import ("./pwa.js")
    // would be dropped from the corpus AND flagged as an orphan by the layout guard:
    //   import … from "./x.js"    import "./x.js"    export … from "./x.js"    export * from "./x.js"
    private static readonly Regex Specifier =
        new("""(?:\bfrom\s*|\bimport\s*)["'](\./[^"']+)["']""", RegexOptions.Compiled);

    private async Task AppendModuleAsync(string file, HashSet<string> visited, StringBuilder script)
    {
        if (!visited.Add(file))
        {
            return;
        }

        var source = await Client.GetStringAsync("/js/" + file);

        // Depth-first, so a module's dependencies land in the corpus before it does.
        foreach (Match match in Specifier.Matches(source))
        {
            await AppendModuleAsync(match.Groups[1].Value[2..], visited, script);
        }

        script.Append(source).Append('\n');
    }

    /// <summary>
    /// Returns the source text of one function, from its declaration to its matching closing brace,
    /// by counting brace depth. Crude, but it is what lets an assertion say "<c>send()</c> does not
    /// call <c>getCurrentPosition</c>" rather than the far weaker "the file contains the string
    /// somewhere" — which would pass no matter where the call sat.
    /// <para>
    /// Depth-counting rather than scanning for a fixed-indent <c>"\n  }"</c>: module-scope bodies
    /// close at column 0, and an indentation-sensitive scan would run straight past the end.
    /// </para>
    /// </summary>
    public static string Body(string source, string declaration)
    {
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"'{declaration}' not found in the front-end script — did it get renamed?");

        var open = source.IndexOf('{', start);
        Assert.True(open >= 0, $"no body found for '{declaration}'");

        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[start..(i + 1)];
        }

        Assert.Fail($"unbalanced braces after '{declaration}'");
        return string.Empty;
    }

    // Walk up from the test assembly location until the repo root (the directory holding
    // Agent42.slnx) is found, then the Backend project dir — robust across Debug/Release/CI.
    // The web root defaults to <contentRoot>/wwwroot.
    private static string ResolveBackendDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Agent42.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.True(dir is not null, "Could not locate repo root (Agent42.slnx) from " + AppContext.BaseDirectory);
        var backend = Path.Combine(dir!.FullName, "src", "Agent42.Backend");
        Assert.True(Directory.Exists(Path.Combine(backend, "wwwroot")), "Backend wwwroot not found under " + backend);
        return backend;
    }
}

/// <summary>
/// Collection binding the shared <see cref="StaticAssetsServerFixture"/> to the asset-guardrail
/// test classes (<see cref="PwaAssetsTests"/>, <see cref="SpeechInputAssetsTests"/>).
/// </summary>
[CollectionDefinition("StaticAssets")]
public sealed class StaticAssetsCollection : ICollectionFixture<StaticAssetsServerFixture>;
