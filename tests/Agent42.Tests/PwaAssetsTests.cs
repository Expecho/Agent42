using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;

namespace Agent42.Tests;

/// <summary>
/// Serves the real Backend <c>wwwroot</c> through a TestServer using the same
/// <c>UseDefaultFiles()</c> / <c>UseStaticFiles()</c> pair as Program.cs, so the PWA
/// installability contract the browser depends on (manifest MIME + fields, service
/// worker, icons) is exercised end to end. Production/DevHost serve the same physical
/// files via the SDK static-web-assets provider; both resolve MIME through the default
/// FileExtensionContentTypeProvider, so the content-type contract is equivalent.
/// </summary>
public sealed class PwaAssetsTests : IAsyncLifetime
{
    private WebApplication _app = null!;
    private HttpClient _client = null!;

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
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
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

    [Fact]
    public async Task Manifest_IsServed_WithInstallableFields()
    {
        var response = await _client.GetAsync("/manifest.webmanifest");

        response.EnsureSuccessStatusCode();
        Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("name").GetString()));
        Assert.Equal("/", root.GetProperty("start_url").GetString());
        Assert.Equal("standalone", root.GetProperty("display").GetString());

        var sizes = root.GetProperty("icons").EnumerateArray()
            .Where(i => i.GetProperty("type").GetString() == "image/png")
            .Select(i => i.GetProperty("sizes").GetString())
            .ToList();
        Assert.Contains("192x192", sizes);
        Assert.Contains("512x512", sizes);
    }

    [Fact]
    public async Task ServiceWorker_IsServed_AsJavaScript()
    {
        var response = await _client.GetAsync("/sw.js");

        response.EnsureSuccessStatusCode();
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        Assert.Contains(mediaType, new[] { "text/javascript", "application/javascript" });
    }

    [Fact]
    public async Task ServiceWorker_BypassesApiBeforeCaching()
    {
        // Guards the ADR-0002 auth invariant: the /api bypass must sit before any caching
        // so auth-bearing, streaming API responses are never cached. Source-text heuristic
        // (the repo has no JS test runner by design) — proves position, not logic.
        var response = await _client.GetAsync("/sw.js");
        response.EnsureSuccessStatusCode();
        var source = await response.Content.ReadAsStringAsync();

        var apiGuard = source.IndexOf("startsWith(\"/api\")", StringComparison.Ordinal);
        Assert.True(apiGuard >= 0, "Service worker is missing the /api bypass guard");

        // respondWith is the interception mechanism and appears only in the fetch handler,
        // so the /api early-return preceding every respondWith is the invariant we want:
        // /api requests are never intercepted or cached.
        var firstRespond = source.IndexOf("respondWith", StringComparison.Ordinal);
        Assert.True(firstRespond >= 0, "Service worker has no fetch handler (respondWith)");
        Assert.True(apiGuard < firstRespond, "The /api bypass must precede any respondWith");
    }

    [Theory]
    [InlineData("/icons/icon-192.png")]
    [InlineData("/icons/icon-512.png")]
    [InlineData("/icons/icon-maskable-512.png")]
    public async Task Icons_AreServed_AsPng(string path)
    {
        var response = await _client.GetAsync(path);

        response.EnsureSuccessStatusCode();
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEmpty(await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task IndexHtml_LinksManifest_AndAppRegistersServiceWorker()
    {
        var index = await _client.GetStringAsync("/");
        Assert.Contains("rel=\"manifest\"", index);
        Assert.Contains("theme-color", index);

        var app = await _client.GetStringAsync("/app.js");
        Assert.Contains("serviceWorker.register", app);
    }
}
