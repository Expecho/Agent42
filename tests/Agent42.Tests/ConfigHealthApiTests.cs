using System.Net;
using System.Text.Json;
using System.Threading;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The <c>GET /api/config-health</c> contract (Preferences → Configuration), hosted in-memory (TestServer).
/// The load-bearing assertion is the billing-safety invariant: a STATIC call runs ZERO probes (a
/// geocoding/context.dev probe costs a metered call), while <c>?probe=true</c> runs them. A counting
/// <see cref="ConfigHealthProbeSet"/> is injected so the count is observable without a network — the same
/// seam ProbeAsync prefers over its production FromServices factory.
/// </summary>
public sealed class ConfigHealthApiTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private TestCluster _cluster = null!;
    private WebApplication _app = null!;
    private HttpClient _client = null!;
    private readonly ProbeCounter _counter = new();

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);

        // MapAgentApi infers every endpoint's params up front, so the services the OTHER endpoints resolve
        // must be present even though these tests only hit /api/config-health (otherwise minimal-API treats
        // an unregistered service param as a body). Mirrors ProvidersApiTests' host wiring.
        appBuilder.Services.AddSingleton<IProviderSecretProtector>(
            new ProviderSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<ProviderSecretProtector>.Instance));
        appBuilder.Services.AddSingleton<IMcpSecretProtector>(
            new McpSecretProtector(new EphemeralDataProtectionProvider(), NullLogger<McpSecretProtector>.Instance));
        appBuilder.Services.AddSingleton(new McpOAuthClient(new HttpClient()));
        appBuilder.Services.AddSingleton<IModelCatalog>(new StaticModelCatalog("fake-model", []));
        appBuilder.Services.AddSingleton<IUserModelCatalogProvider>(sp =>
            new SingleUserModelCatalogProvider(sp.GetRequiredService<IModelCatalog>()));
        appBuilder.Services.AddSingleton<IAttachmentStore, InMemoryAttachmentStore>();
        appBuilder.Services.AddDocumentRagFakes();
        appBuilder.Services.AddSingleton(new PushConfig(Enabled: false, PublicKey: "", Subject: ""));

        // The counting probe set: ProbeAsync prefers a registered ConfigHealthProbeSet over its production
        // FromServices factory, so a static call (which never calls ProbeAsync) leaves the counter at 0 and
        // a probe=true call increments it once per probeable check.
        appBuilder.Services.AddSingleton(new ConfigHealthProbeSet(
            SearXng: _ => _counter.Bump(),
            Geocoding: _ => _counter.Bump(),
            ContextScrape: _ => _counter.Bump()));

        _app = appBuilder.Build();
        _app.MapAgentApi();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private HttpRequestMessage Request(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, "user-a");
        return request;
    }

    // AC9 (shape + billing-safety): a static call returns the expected JSON and runs ZERO probes.
    [Fact]
    public async Task Static_RunsNoProbes()
    {
        var response = await _client.SendAsync(Request("/api/config-health"));
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("environment", out _));
        Assert.NotEmpty(doc.RootElement.GetProperty("checks").EnumerateArray());
        Assert.Equal(0, _counter.Count);
    }

    // AC9: ?probe=true runs the probes (one per probeable check).
    [Fact]
    public async Task Probe_RunsProbes()
    {
        var response = await _client.SendAsync(Request("/api/config-health?probe=true"));
        response.EnsureSuccessStatusCode();

        Assert.True(_counter.Count > 0);
    }

    private sealed class ProbeCounter
    {
        private int _count;
        public int Count => Volatile.Read(ref _count);
        public Task<ProbeResult> Bump()
        {
            Interlocked.Increment(ref _count);
            return Task.FromResult(new ProbeResult(ConfigHealth.Ok, "reachable"));
        }
    }
}
