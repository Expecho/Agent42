using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;

namespace Agent42.Tests;

/// <summary>Tavily and Firecrawl providers (ADR 0155), against payload shapes taken from the vendors' docs (Step 0).</summary>
public sealed class HostedWebSearchProviderTests
{
    private const string Key = "tvly-SECRET";

    private sealed class Stub(Func<HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Seen;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return respond();
        }
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    private const string TavilyOk = """
        { "query": "orleans", "results": [
          { "title": "Orleans overview", "url": "https://learn.microsoft.com/orleans", "content": "A framework.", "score": 0.9 },
          { "title": "dotnet/orleans", "url": "https://github.com/dotnet/orleans", "content": "Cloud native.", "score": 0.8 } ],
          "usage": { "credits": 1 } }
        """;

    private const string FirecrawlOk = """
        { "success": true, "creditsUsed": 2, "data": { "web": [
          { "url": "https://learn.microsoft.com/orleans", "title": "Orleans overview", "description": "A framework.", "position": 1 },
          { "url": "https://github.com/dotnet/orleans", "title": "dotnet/orleans", "description": "Cloud native.", "position": 2 } ] } }
        """;

    private static (TavilyWebSearch Search, Stub Stub) Tavily(Func<HttpResponseMessage> respond)
    {
        var stub = new Stub(respond);
        return (new TavilyWebSearch(new HttpClient(stub) { BaseAddress = new Uri("https://api.tavily.com/") }, Key), stub);
    }

    private static (FirecrawlWebSearch Search, Stub Stub) Firecrawl(Func<HttpResponseMessage> respond)
    {
        var stub = new Stub(respond);
        return (new FirecrawlWebSearch(new HttpClient(stub) { BaseAddress = new Uri("https://api.firecrawl.dev/") }, "fc-SECRET"), stub);
    }

    [Fact]
    public async Task Tavily_SendsCheapestRequest_KeyInHeaderOnly()
    {
        var (search, stub) = Tavily(() => Json(TavilyOk));

        await search.SearchAsync("orleans", 5);

        Assert.Equal("https://api.tavily.com/search", stub.Seen!.RequestUri!.ToString());
        Assert.Equal($"Bearer {Key}", stub.Seen.Headers.Authorization!.ToString());
        Assert.DoesNotContain(Key, stub.Seen.RequestUri.ToString());
        using var body = JsonDocument.Parse(stub.Body!);
        Assert.Equal("basic", body.RootElement.GetProperty("search_depth").GetString());
        Assert.False(body.RootElement.GetProperty("include_raw_content").GetBoolean());
        Assert.False(body.RootElement.GetProperty("include_answer").GetBoolean());
        Assert.Equal(5, body.RootElement.GetProperty("max_results").GetInt32());
    }

    [Fact]
    public async Task Tavily_MapsResults()
    {
        var (search, _) = Tavily(() => Json(TavilyOk));

        var results = await search.SearchAsync("orleans", 5);

        Assert.Equal(2, results.Count);
        Assert.Equal(new WebSearchResult("Orleans overview", "https://learn.microsoft.com/orleans", "A framework."), results[0]);
    }

    [Fact]
    public async Task Firecrawl_SendsCheapestRequest_NoScrapeOptions_KeyInHeaderOnly()
    {
        var (search, stub) = Firecrawl(() => Json(FirecrawlOk));

        await search.SearchAsync("orleans", 5);

        Assert.Equal("https://api.firecrawl.dev/v2/search", stub.Seen!.RequestUri!.ToString());
        Assert.Equal("Bearer fc-SECRET", stub.Seen.Headers.Authorization!.ToString());
        Assert.DoesNotContain("fc-SECRET", stub.Seen.RequestUri.ToString());
        using var body = JsonDocument.Parse(stub.Body!);
        Assert.False(body.RootElement.TryGetProperty("scrapeOptions", out _));
        Assert.Equal(5, body.RootElement.GetProperty("limit").GetInt32());
    }

    [Fact]
    public async Task Firecrawl_MapsDescriptionToSnippet()
    {
        var (search, _) = Firecrawl(() => Json(FirecrawlOk));

        var results = await search.SearchAsync("orleans", 5);

        Assert.Equal(new WebSearchResult("Orleans overview", "https://learn.microsoft.com/orleans", "A framework."), results[0]);
    }

    [Fact]
    public async Task BothProviders_CapResultsAndSnippets()
    {
        var long_ = new string('x', WebToolLimits.MaxSnippetLength * 3);
        var items = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{ "title": "t{{i}}", "url": "https://example.com/{{i}}", "content": "{{long_}}", "description": "{{long_}}" }"""));

        var (tavily, _) = Tavily(() => Json($$"""{ "results": [{{items}}] }"""));
        var (firecrawl, _) = Firecrawl(() => Json($$"""{ "data": { "web": [{{items}}] } }"""));

        foreach (var results in new[] { await tavily.SearchAsync("q", 50), await firecrawl.SearchAsync("q", 50) })
        {
            Assert.Equal(WebToolLimits.MaxSearchResults, results.Count);
            Assert.All(results, r => Assert.Equal(WebToolLimits.MaxSnippetLength, r.Snippet.Length));
        }
    }

    [Fact]
    public async Task EmptyAnswers_AreEmptyResultsNotFailures()
    {
        var (tavily, _) = Tavily(() => Json("""{ "results": [] }"""));
        var (firecrawl, _) = Firecrawl(() => Json("""{ "success": true, "data": { } }"""));

        Assert.Empty(await tavily.SearchAsync("q", 5));
        Assert.Empty(await firecrawl.SearchAsync("q", 5));
    }

    [Theory]
    [InlineData(401, WebSearchFailureKind.Unauthorized)]
    [InlineData(403, WebSearchFailureKind.Unauthorized)]
    [InlineData(429, WebSearchFailureKind.RateLimited)]
    [InlineData(432, WebSearchFailureKind.Exhausted)]
    [InlineData(433, WebSearchFailureKind.Exhausted)]
    [InlineData(500, WebSearchFailureKind.Transient)]
    [InlineData(503, WebSearchFailureKind.Transient)]
    public async Task Tavily_ClassifiesFailures(int status, WebSearchFailureKind kind)
    {
        var (search, _) = Tavily(() => Json("""{"detail":{"error":"SECRET-BODY"}}""", (HttpStatusCode)status));

        var ex = await Assert.ThrowsAsync<WebSearchProviderException>(() => search.SearchAsync("q", 5));

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Theory]
    [InlineData(401, WebSearchFailureKind.Unauthorized)]
    [InlineData(402, WebSearchFailureKind.Exhausted)]
    [InlineData(429, WebSearchFailureKind.RateLimited)]
    [InlineData(408, WebSearchFailureKind.Transient)]
    [InlineData(500, WebSearchFailureKind.Transient)]
    public async Task Firecrawl_ClassifiesFailures(int status, WebSearchFailureKind kind)
    {
        var (search, _) = Firecrawl(() => Json("""{"success":false,"error":"SECRET-BODY"}""", (HttpStatusCode)status));

        var ex = await Assert.ThrowsAsync<WebSearchProviderException>(() => search.SearchAsync("q", 5));

        Assert.Equal(kind, ex.Kind);
        Assert.DoesNotContain("SECRET", ex.Message);
    }

    [Fact]
    public async Task RateLimit_CarriesRetryAfterSeconds()
    {
        var (search, _) = Tavily(() =>
        {
            var response = Json("{}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        });

        var ex = await Assert.ThrowsAsync<WebSearchProviderException>(() => search.SearchAsync("q", 5));

        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfter);
    }

    [Fact]
    public async Task A2xxWithoutTheResultsContainer_IsAFailure_NotAnEmptyAnswer()
    {
        var (tavily, _) = Tavily(() => Json("""{ "unexpected": true }"""));
        var (firecrawl, _) = Firecrawl(() => Json("""{ "success": false, "error": "SECRET-BODY" }"""));

        var one = await Assert.ThrowsAsync<WebSearchProviderException>(() => tavily.SearchAsync("q", 5));
        var two = await Assert.ThrowsAsync<WebSearchProviderException>(() => firecrawl.SearchAsync("q", 5));

        Assert.Equal(WebSearchFailureKind.Transient, one.Kind);
        Assert.Equal(WebSearchFailureKind.Transient, two.Kind);
    }

    [Fact]
    public async Task SearxngAdapter_IsNamedAndNeverBenched()
    {
        var provider = new SearxngSearchProvider(new InMemoryWebSearch());

        Assert.Equal("searxng", provider.Name);
        Assert.False(provider.CanBeBenched);
        Assert.NotNull(await provider.SearchAsync("q", 3));
    }
}
