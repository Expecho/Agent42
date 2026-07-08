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
