using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;

namespace Agent42.Tests;

/// <summary>
/// The shipped web-client pipeline (ADR 0011/0059/0066), asserted on the REAL resolved handler chain. Replaces the
/// source-text slices over <c>Program.cs</c> that used to pin it: those could be satisfied by a neighbouring client's
/// settings and broke silently whenever the block moved. Here nothing reads source.
/// </summary>
public sealed class WebRegistrationTests
{
    private const string ControlClient = "control";

    // `AddServiceDefaults` applies the standard resilience handler to EVERY named client; this reproduces that so
    // "removed" is observable, and registers a control client that keeps it so the check is not vacuous.
    private static ServiceProvider Build(string? endpoint = "http://localhost:8888")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WebSearch:Endpoint"] = endpoint })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.ConfigureHttpClientDefaults(b => b.AddStandardResilienceHandler());
        services.AddHttpClient(ControlClient);
        services.AddWebTools(config);
        return services.BuildServiceProvider();
    }

    private static List<HttpMessageHandler> Chain(IServiceProvider sp, string name)
    {
        var chain = new List<HttpMessageHandler>();
        for (var handler = sp.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
             handler is not null;
             handler = (handler as DelegatingHandler)?.InnerHandler)
        {
            chain.Add(handler);
        }

        return chain;
    }

    private static bool HasResilience(IServiceProvider sp, string name)
        => Chain(sp, name).Any(h => h.GetType().Name.Contains("Resilience", StringComparison.Ordinal));

    [Fact]
    public void TheControlClient_KeepsTheInheritedResiliencePipeline()
    {
        using var sp = Build();

        Assert.True(HasResilience(sp, ControlClient), "The probe cannot see the pipeline, so the tests below prove nothing.");
    }

    [Theory]
    [InlineData(SearxngWebSearch.HttpClientName)]
    [InlineData(GuardedDocumentFetcher.HttpClientName)]
    [InlineData(SearxngProxy.HttpClientName)]
    public void EveryWebClient_RemovesTheInheritedResiliencePipeline(string name)
    {
        using var sp = Build();

        Assert.False(HasResilience(sp, name));
    }

    [Theory]
    [InlineData(SearxngWebSearch.HttpClientName, 10)]
    [InlineData(GuardedDocumentFetcher.HttpClientName, 30)]
    [InlineData(SearxngProxy.HttpClientName, 10)]
    public void EveryWebClient_SetsItsOwnTimeout(string name, int seconds)
    {
        using var sp = Build();

        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(name);

        Assert.Equal(TimeSpan.FromSeconds(seconds), http.Timeout);
    }

    // AC19: the SSRF guard is WIRED on the document client's primary handler. A PrivateAddressGuard that is built,
    // tested and never installed is the highest-consequence way this feature can ship green. Loopback must be refused
    // at connect time; nothing listens on the port, so a client without the guard would fail differently.
    [Fact]
    public async Task TheDocumentClient_RefusesALoopbackAddress_AtConnectTime()
    {
        using var sp = Build();
        using var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(GuardedDocumentFetcher.HttpClientName);

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() => http.GetAsync("http://127.0.0.1:1/"));

        Assert.IsType<BlockedAddressException>(ex.InnerException);
    }

    // The proxy must see the 3xx itself, not follow it, and must not share a cookie jar (ADR 0059).
    [Fact]
    public void TheProxyClient_DoesNotFollowRedirectsOrKeepCookies()
    {
        using var sp = Build();

        var primary = Chain(sp, SearxngProxy.HttpClientName)[^1];

        var handler = Assert.IsType<HttpClientHandler>(primary);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public void TheClients_AreBasedOnTheSearxngEndpoint()
    {
        using var sp = Build("http://searx.test:8888");
        var factory = sp.GetRequiredService<IHttpClientFactory>();

        Assert.Equal(new Uri("http://searx.test:8888"), factory.CreateClient(SearxngWebSearch.HttpClientName).BaseAddress);
        Assert.Equal(new Uri("http://searx.test:8888"), factory.CreateClient(SearxngProxy.HttpClientName).BaseAddress);
    }

    [Fact]
    public void RegistersTheSearchAndTheGuardedFetcher()
    {
        using var sp = Build();

        Assert.IsType<SearxngWebSearch>(sp.GetRequiredService<IWebSearch>());
        Assert.IsType<GuardedDocumentFetcher>(sp.GetRequiredService<IWebDocumentFetcher>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AMissingEndpoint_FailsAtStartup(string? endpoint)
        => Assert.Throws<InvalidOperationException>(() => Build(endpoint));
}
