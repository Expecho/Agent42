using System.Net;
using System.Text;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;
using Agent42.Agents.Web;

namespace Agent42.Tests;

public sealed class SearxngWebSearchTests
{
    // A SearXNG JSON payload: results carry `title`, `url` and — the snippet — `content`.
    private const string CannedJson = """
        {
          "query": "orleans",
          "results": [
            { "title": "Orleans overview", "url": "https://learn.microsoft.com/orleans", "content": "A framework for distributed apps." },
            { "title": "dotnet/orleans",   "url": "https://github.com/dotnet/orleans",   "content": "Cloud native application framework." }
          ]
        }
        """;

    private static SearxngWebSearch Build(StubHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:8888/") },
            NullLogger<SearxngWebSearch>.Instance);

    // AC2 — a SearXNG payload maps onto WebSearchResults (SearXNG's `content` is our Snippet).
    [Fact]
    public async Task Search_MapsResults()
    {
        var search = Build(StubHandler.Json(CannedJson));

        var results = await search.SearchAsync("orleans", WebToolLimits.MaxSearchResults);

        Assert.Collection(results,
            first =>
            {
                Assert.Equal("Orleans overview", first.Title);
                Assert.Equal("https://learn.microsoft.com/orleans", first.Url);
                Assert.Equal("A framework for distributed apps.", first.Snippet);
            },
            second => Assert.Equal("https://github.com/dotnet/orleans", second.Url));
    }

    // AC3 — format=json must always be on the query string, or SearXNG serves HTML (and see AC5).
    [Fact]
    public async Task Search_RequestsJsonFormat()
    {
        var handler = StubHandler.Json(CannedJson);
        var search = Build(handler);

        await search.SearchAsync("orleans grains", WebToolLimits.MaxSearchResults);

        var query = handler.LastRequestUri!.Query;
        Assert.Contains("format=json", query);
        Assert.Contains("q=orleans%20grains", query);
    }

    // AC4 — results and snippets are capped: a tool result is persisted into the 2 MB session
    // (ADR 0004), so neither the model's `maxResults` nor the instance's verbosity may set the size.
    [Fact]
    public async Task Search_CapsResultsAndSnippets()
    {
        var longSnippet = new string('x', WebToolLimits.MaxSnippetLength * 3);
        var many = string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{ "title": "t{{i}}", "url": "https://example.com/{{i}}", "content": "{{longSnippet}}" }"""));
        var search = Build(StubHandler.Json($$"""{ "results": [{{many}}] }"""));

        // Ask for far more than the cap allows.
        var results = await search.SearchAsync("anything", maxResults: 50);

        Assert.Equal(WebToolLimits.MaxSearchResults, results.Count);
        Assert.All(results, r => Assert.Equal(WebToolLimits.MaxSnippetLength, r.Snippet.Length));
    }

    // AC5 — 403 is the signature failure of this integration: SearXNG serves only the formats listed
    // in `search.formats`, and answers a request for an unlisted one with 403. It reads like an auth
    // failure, so the message must name the real cause rather than let the next person rediscover it.
    [Fact]
    public async Task Search_On403_ExplainsJsonFormatNotEnabled()
    {
        var search = Build(StubHandler.Status(HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => search.SearchAsync("orleans", WebToolLimits.MaxSearchResults));

        Assert.Contains("search.formats", ex.Message);
        Assert.Contains("not an authentication failure", ex.Message);
    }

    // An instance that answers without a results array must not throw — an empty result set is a
    // legitimate answer the model can act on.
    [Fact]
    public async Task Search_WithoutResultsArray_ReturnsEmpty()
    {
        var search = Build(StubHandler.Json("""{ "query": "x" }"""));

        Assert.Empty(await search.SearchAsync("x", WebToolLimits.MaxSearchResults));
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        private StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public static StubHandler Json(string body) => new(HttpStatusCode.OK, body);

        public static StubHandler Status(HttpStatusCode status) => new(status, string.Empty);

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
