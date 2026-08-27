using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.TestingHost;

namespace Agent42.Tests;

/// <summary>
/// The three <c>GET /api/diagnostics/*</c> reads behind the live console at <c>/logs</c> (ADR 0054),
/// hosted in-memory (TestServer).
///
/// <para>Two behaviours are worth more than the wire shape. First, the buffer is resolved
/// <b>optionally</b> from <see cref="IServiceProvider"/> rather than taken as a handler parameter, so a
/// host with no capture registered must return an <i>empty payload</i> — not a 500, and above all not a
/// broken route table: <c>MapAgentApi</c> infers every endpoint's parameters up front, so an
/// unresolvable parameter breaks routing for the whole host, which is why
/// <see cref="Diagnostics_WithoutCapture_ReturnsEmptyNot500"/> also checks an <i>unrelated</i> endpoint
/// still answers. Second, <c>limit</c> is clamped server-side — a caller may narrow a page, never widen
/// it past the configured budget.</para>
/// </summary>
public sealed class DiagnosticsApiTests : IAsyncLifetime
{
    private const string IdHeader = "X-MS-CLIENT-PRINCIPAL-ID";

    private TestCluster _cluster = null!;
    private WebApplication _withCapture = null!;
    private WebApplication _withoutCapture = null!;
    private HttpClient _client = null!;
    private HttpClient _bareClient = null!;
    private LiveTelemetryBuffer _buffer = null!;

    public async Task InitializeAsync()
    {
        var builder = new TestClusterBuilder();
        builder.AddSiloBuilderConfigurator<TestSiloConfigurator>();
        _cluster = builder.Build();
        await _cluster.DeployAsync();

        _buffer = new LiveTelemetryBuffer(new DiagnosticsConfig(
            MaxSpans: 100, MaxLogs: 100, MaxTagValueLength: 1024, MaxBufferBytes: 1024 * 1024,
            MaxItemsPerPoll: 2, MetricExportIntervalMs: DiagnosticsConfig.Default.MetricExportIntervalMs));

        (_withCapture, _client) = await BuildHostAsync(_buffer);
        (_withoutCapture, _bareClient) = await BuildHostAsync(buffer: null);
    }

    private async Task<(WebApplication App, HttpClient Client)> BuildHostAsync(
        LiveTelemetryBuffer? buffer, Dictionary<string, string?>? configOverrides = null)
    {
        var appBuilder = WebApplication.CreateSlimBuilder();
        appBuilder.WebHost.UseTestServer();
        if (configOverrides is not null)
        {
            appBuilder.Configuration.AddInMemoryCollection(configOverrides);
        }

        appBuilder.Services.AddSingleton<IGrainFactory>(_cluster.GrainFactory);

        // MapAgentApi infers every endpoint's params up front, so the services the OTHER endpoints
        // resolve must be present even though these tests only hit /api/diagnostics/*. Mirrors
        // ConfigHealthApiTests' host wiring.
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

        // The whole point of the "without capture" host: the buffer singleton is simply absent.
        if (buffer is not null)
        {
            appBuilder.Services.AddSingleton(buffer);
        }

        var app = appBuilder.Build();
        app.MapAgentApi();
        await app.StartAsync();
        return (app, app.GetTestClient());
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        _bareClient.Dispose();
        await _withCapture.DisposeAsync();
        await _withoutCapture.DisposeAsync();
        await _cluster.DisposeAsync();
    }

    private static HttpRequestMessage Request(string uri)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Add(IdHeader, "user-a");
        return request;
    }

    private async Task<JsonElement> GetAsync(HttpClient client, string uri)
    {
        var response = await client.SendAsync(Request(uri));
        response.EnsureSuccessStatusCode();

        // Results.Json, never a bare string: a minimal-API handler returning a bare string serves
        // text/plain, which would parse here but is not what the console fetches.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static CapturedSpan Span(string name) =>
        new(0, DateTimeOffset.UnixEpoch, "trace-1", "span-" + name, null, name, "Internal", 1.0,
            "Unset", null, "Agent42.Backend", [new CapturedTag("gen_ai.request.model", "gpt-5.4")], []);

    private static CapturedLog Log(string message, string level) =>
        new(0, DateTimeOffset.UnixEpoch, "Test.Category", level, message, null, null, null, [], []);

    [Fact]
    public async Task Diagnostics_ReturnsCapturedSignals()
    {
        _buffer.AppendSpan(Span("chat"));
        _buffer.AppendLog(Log("something happened", "Warning"));
        _buffer.ReplaceMetrics(
            [new CapturedMetric("requests", "Agent42", "{req}", "desc", "LongSum", "Cumulative",
                [new CapturedMetricPoint([], 7, null)])],
            DateTimeOffset.UnixEpoch);

        var traces = await GetAsync(_client, "/api/diagnostics/traces");
        var span = traces.GetProperty("spans").EnumerateArray().Single();
        Assert.Equal("chat", span.GetProperty("name").GetString());
        Assert.Equal("trace-1", span.GetProperty("traceId").GetString());
        Assert.Equal(1L, traces.GetProperty("cursor").GetInt64());

        var logs = await GetAsync(_client, "/api/diagnostics/logs");
        Assert.Equal(
            "something happened",
            logs.GetProperty("logs").EnumerateArray().Single().GetProperty("message").GetString());

        var metrics = await GetAsync(_client, "/api/diagnostics/metrics");
        var metric = metrics.GetProperty("metrics").EnumerateArray().Single();
        Assert.Equal("requests", metric.GetProperty("name").GetString());
        Assert.Equal(7, metric.GetProperty("points").EnumerateArray().Single().GetProperty("value").GetDouble());
    }

    [Fact]
    public async Task Diagnostics_HonoursSinceLimitAndMinLevel()
    {
        for (var i = 1; i <= 5; i++)
        {
            _buffer.AppendSpan(Span($"s{i}"));
        }

        _buffer.AppendLog(Log("chatty", "Debug"));
        _buffer.AppendLog(Log("boom", "Error"));

        // limit is CLAMPED to the configured MaxItemsPerPoll (2 here), so a caller asking for 500 gets 2.
        var page = await GetAsync(_client, "/api/diagnostics/traces?limit=500");
        Assert.Equal(2, page.GetProperty("spans").GetArrayLength());

        // ...and `since` advances through the ring rather than replaying it.
        var cursor = page.GetProperty("cursor").GetInt64();
        var next = await GetAsync(_client, $"/api/diagnostics/traces?since={cursor}");
        var names = next.GetProperty("spans").EnumerateArray().Select(s => s.GetProperty("name").GetString());
        Assert.DoesNotContain("s1", names);

        var errors = await GetAsync(_client, "/api/diagnostics/logs?minLevel=Warning");
        Assert.Equal(
            "boom",
            errors.GetProperty("logs").EnumerateArray().Single().GetProperty("message").GetString());
    }

    [Fact]
    public async Task Diagnostics_ReportsTheEffectiveMinimumLevel()
    {
        // This host reads the REAL Backend appsettings.json (it is copied into the test output by the
        // project reference), so the value asserted here is the one production actually runs with. That is
        // the whole point of AC19: ILogger filtering runs BEFORE the OpenTelemetry logging processor, so at
        // Warning no Information record ever reaches the buffer and the Logs tab is legitimately empty. The
        // tab shows this value so an empty list reads as correct rather than broken.
        var logs = await GetAsync(_client, "/api/diagnostics/logs");

        Assert.Equal("Warning", logs.GetProperty("effectiveMinimumLevel").GetString());
    }

    [Fact]
    public async Task Diagnostics_FallsBackToTheFrameworkDefaultWhenNothingConfiguresALevel()
    {
        // The offline case: tools/Agent42.DevHost has no appsettings.json at all, so nothing sets a
        // level and the framework's own default applies. Reporting "" there would make the Logs tab
        // claim the app is filtering at nothing.
        var (app, client) = await BuildHostAsync(
            _buffer, new Dictionary<string, string?> { ["Logging:LogLevel:Default"] = "" });

        try
        {
            var logs = await GetAsync(client, "/api/diagnostics/logs");
            Assert.Equal("Information", logs.GetProperty("effectiveMinimumLevel").GetString());
        }
        finally
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    [Fact]
    public async Task Diagnostics_WithoutCapture_ReturnsEmptyNot500()
    {
        var traces = await GetAsync(_bareClient, "/api/diagnostics/traces");
        Assert.Empty(traces.GetProperty("spans").EnumerateArray());

        var logs = await GetAsync(_bareClient, "/api/diagnostics/logs");
        Assert.Empty(logs.GetProperty("logs").EnumerateArray());

        var metrics = await GetAsync(_bareClient, "/api/diagnostics/metrics");
        Assert.Empty(metrics.GetProperty("metrics").EnumerateArray());

        // The failure that would matter far more than an empty payload: an unresolvable handler parameter
        // does not 500 its own endpoint, it breaks parameter inference for the WHOLE host. So check an
        // unrelated endpoint still answers.
        var response = await _bareClient.SendAsync(Request("/api/models"));
        response.EnsureSuccessStatusCode();
    }
}
