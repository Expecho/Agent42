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
    private static ServiceProvider Build(string? endpoint = "http://localhost:8888", params (string Key, string? Value)[] extra)
    {
        var settings = new Dictionary<string, string?> { ["WebSearch:Endpoint"] = endpoint };
        foreach (var (key, value) in extra)
        {
            settings[key] = value;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
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

    // ADR 0155: the chain. No key means no client and the bare SearXNG search (today's behaviour); a key means a
    // key-gated named client without the inherited resilience pipeline, and the fallback chain.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ABlankKey_CountsAsNoKey(string? key)
    {
        using var sp = Build("http://localhost:8888", ("WebSearch:Tavily:ApiKey", key));

        Assert.IsType<SearxngWebSearch>(sp.GetRequiredService<IWebSearch>());
    }

    [Fact]
    public void AKey_RegistersTheChain_AndTheFleetResolvesTheSameOne()
    {
        using var sp = Build("http://localhost:8888", ("WebSearch:Firecrawl:ApiKey", "fc-key"));

        Assert.IsType<FallbackWebSearch>(sp.GetRequiredService<IWebSearch>());
        Assert.Same(sp.GetRequiredService<IWebSearch>(), sp.GetRequiredService<IWebSearch>());
    }

    [Theory]
    [InlineData(TavilyWebSearch.HttpClientName, "WebSearch:Tavily:ApiKey")]
    [InlineData(FirecrawlWebSearch.HttpClientName, "WebSearch:Firecrawl:ApiKey")]
    public void AKeyedClient_ExistsOnlyWithItsKey_AndHasNoResiliencePipeline(string client, string keySetting)
    {
        using var without = Build();
        using var with = Build("http://localhost:8888", (keySetting, "k"));

        // No key: the name is unregistered, so the factory hands back a plain client that DID inherit the pipeline
        // from ConfigureHttpClientDefaults. The registration never asks for it (see the chain test above).
        Assert.True(HasResilience(without, client));
        Assert.False(HasResilience(with, client));
        using var http = with.GetRequiredService<IHttpClientFactory>().CreateClient(client);
        Assert.Equal(TimeSpan.FromSeconds(6), http.Timeout);
    }

    [Fact]
    public void ProvidersSetting_DecidesTheOrder_AndSearxngIsAlwaysLast()
    {
        var order = WebSearchChainRegistration.ParseOrder("firecrawl, tavily", out var unknown);

        Assert.Equal(["firecrawl", "tavily", "searxng"], order);
        Assert.Empty(unknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankProvidersSetting_MeansTheDefaultOrder(string? raw) =>
        Assert.Equal(["tavily", "firecrawl", "searxng"], WebSearchChainRegistration.ParseOrder(raw, out _));

    [Fact]
    public void AnUnknownProvider_IsIgnored_AndAllUnknownLeavesSearxngOnly()
    {
        var mixed = WebSearchChainRegistration.ParseOrder("bing,tavily", out var unknownMixed);
        var allBad = WebSearchChainRegistration.ParseOrder("bing,brave", out var unknownAll);
        var mid = WebSearchChainRegistration.ParseOrder("searxng,tavily", out _);

        Assert.Equal(["tavily", "searxng"], mixed);
        Assert.Equal(["bing"], unknownMixed);
        Assert.Equal(["searxng"], allBad);
        Assert.Equal(2, unknownAll.Count);
        Assert.Equal(["tavily", "searxng"], mid);
    }

    [Fact]
    public void AKeyForAProviderOutsideTheList_DoesNotBuildTheChain()
    {
        using var sp = Build("http://localhost:8888",
            ("WebSearch:Providers", "firecrawl"), ("WebSearch:Tavily:ApiKey", "k"));

        Assert.IsType<SearxngWebSearch>(sp.GetRequiredService<IWebSearch>());
    }
}
