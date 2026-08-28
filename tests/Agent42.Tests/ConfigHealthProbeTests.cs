using System.Net;
using Agent42.Backend;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Agent42.Tests;

/// <summary>
/// The real live-probe classification logic built by <see cref="ConfigHealthProbeSet.FromServices"/> —
/// exercised over a CANNED <see cref="HttpMessageHandler"/> and a fake embedder (not a live network, per
/// the plan). Pins the load-bearing rule that a SearXNG <b>403 is a healthy sidecar</b> (the JSON API is
/// off), and that auth-vs-reachability is curated correctly per endpoint. No probe message ever carries the
/// response body — the geocoding key rides in the request, so a leak here would be the real hole.
/// </summary>
public sealed class ConfigHealthProbeTests
{
    private static IConfiguration Config(string? googleKey = null, string? hindsightKey = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection([
                new KeyValuePair<string, string?>("Geocoding:GoogleApiKey", googleKey),
                new KeyValuePair<string, string?>("Hindsight:ApiKey", hindsightKey),
            ])
            .Build();

    private static IServiceProvider Services(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IHttpClientFactory>(new SingleClientFactory(handler));
        return services.BuildServiceProvider();
    }

    // The whole point of the SearXNG probe: a 403 is a REACHABLE sidecar (json not in `search.formats`),
    // not an auth failure — mislabelling it would send an operator hunting for a missing key that does not
    // exist (the gotcha this rule encodes).
    [Theory]
    [InlineData(HttpStatusCode.OK, ConfigHealth.Ok)]
    [InlineData(HttpStatusCode.Forbidden, ConfigHealth.Ok)]
    [InlineData(HttpStatusCode.InternalServerError, ConfigHealth.Error)]
    public async Task SearxngProbe_ClassifiesStatus(HttpStatusCode status, string expected)
    {
        var probes = ConfigHealthProbeSet.FromServices(Services(new CannedHandler(status)), Config());

        var result = await probes.SearXng!(default);

        Assert.Equal(expected, result.Status);
    }

    // Geocoding is built only when the key is present (else CreateClient would inherit the resilience
    // pipeline — the trap the plan calls out). REQUEST_DENIED is an auth failure; a clean 200 is reachable.
    [Fact]
    public void GeocodingProbe_NullWhenKeyAbsent()
    {
        var probes = ConfigHealthProbeSet.FromServices(Services(new CannedHandler(HttpStatusCode.OK)), Config(googleKey: null));

        Assert.Null(probes.Geocoding);
    }

    [Theory]
    [InlineData("{ \"status\": \"OK\", \"results\": [] }", ConfigHealth.Ok)]
    [InlineData("{ \"status\": \"REQUEST_DENIED\" }", ConfigHealth.Error)]
    public async Task GeocodingProbe_ClassifiesBody(string body, string expected)
    {
        var probes = ConfigHealthProbeSet.FromServices(
            Services(new CannedHandler(HttpStatusCode.OK, body)), Config(googleKey: "SECRET-google-key"));

        var result = await probes.Geocoding!(default);

        Assert.Equal(expected, result.Status);
        // Curated only — the body (which can echo the key-bearing request) never reaches the message.
        Assert.DoesNotContain("REQUEST_DENIED", result.Message);
        Assert.DoesNotContain("SECRET-google-key", result.Message);
    }

    [Fact]
    public async Task GeocodingProbe_NonSuccess_IsUnreachable()
    {
        var probes = ConfigHealthProbeSet.FromServices(
            Services(new CannedHandler(HttpStatusCode.BadGateway)), Config(googleKey: "k"));

        Assert.Equal(ConfigHealth.Error, (await probes.Geocoding!(default)).Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ConfigHealth.Ok)]
    [InlineData(HttpStatusCode.Unauthorized, ConfigHealth.Error)]
    [InlineData(HttpStatusCode.Forbidden, ConfigHealth.Error)]
    public async Task ContextProbe_ClassifiesStatus(HttpStatusCode status, string expected)
    {
        var probes = ConfigHealthProbeSet.FromServices(Services(new CannedHandler(status)), Config());

        Assert.Equal(expected, (await probes.ContextScrape!(default)).Status);
    }

    // With no IHttpClientFactory registered, every probe is null → the check stays skipped (the "unresolved
    // client" path). Embeddings are no longer probed here — RAG is a per-user model now (ADR 0007/0018).
    [Fact]
    public void FromServices_AllNull_WhenNothingRegistered()
    {
        var probes = ConfigHealthProbeSet.FromServices(new ServiceCollection().BuildServiceProvider(), Config());

        Assert.Null(probes.SearXng);
        Assert.Null(probes.Geocoding);
        Assert.Null(probes.Hindsight);
        Assert.Null(probes.ContextScrape);
    }

    private sealed class CannedHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }

    // Ignores the client name (the probe methods each ask for their own named client); every client routes
    // through the one canned handler with a base address so relative probe URIs resolve.
    private sealed class SingleClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri("http://localhost/") };
    }

    // The Hindsight probe (ADR 0056) is key-gated like geocoding, but for a different reason worth keeping
    // straight: /health is UNAUTHENTICATED, so the probe does not need the key at all. The gate exists
    // because the named client is only registered when a key is present, and CreateClient on an
    // unregistered name hands back a client carrying the resilience pipeline.
    [Fact]
    public void HindsightProbe_NullWhenKeyAbsent()
    {
        var probes = ConfigHealthProbeSet.FromServices(
            Services(new CannedHandler(HttpStatusCode.OK)), Config(hindsightKey: null));

        Assert.Null(probes.Hindsight);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, ConfigHealth.Ok)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ConfigHealth.Error)]
    [InlineData(HttpStatusCode.NotFound, ConfigHealth.Error)]
    public async Task HindsightProbe_ClassifiesStatus(HttpStatusCode status, string expected)
    {
        var probes = ConfigHealthProbeSet.FromServices(
            Services(new CannedHandler(status)), Config(hindsightKey: "SECRET-hindsight-key"));

        var result = await probes.Hindsight!(default);

        Assert.Equal(expected, result.Status);
        // Curated constants only — never a body, a URL or an exception (the invariant on this whole path).
        Assert.Contains(result.Message, new[] { "reachable", "unreachable" });
    }

}
