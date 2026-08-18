using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Backend;
using Microsoft.Extensions.Logging.Abstractions;

namespace Agent42.Tests;

/// <summary>
/// Tests for the hosted-scrape fetcher (ADR 0027). They run against a real loopback HTTP stub
/// (<see cref="TestHttpServer"/>) standing in for context.dev, so the assertions that matter — that the
/// right query parameters go out (AC5) and that a followed link is actually dialled (AC6) — can inspect
/// the <b>outbound request line</b>. A stub fed canned markdown would pass regardless of what the real
/// request carried; the note in the plan calls that out, so those two ACs assert on the request, not the
/// result.
/// </summary>
public sealed class ContextWebPageFetcherTests
{
    private const string RootUrl = "https://site.example/listing";

    // Root markdown with: one link whose text matches the "komplett" filter, one that does not, and an
    // IMAGE whose alt text matches (must never be followed).
    private const string RootMarkdown = """
        # A listing

        Body text. [Se komplett salgsoppgave](https://target.example/full) is the real content.
        Also see [Other listing](https://other.example/x) and ![komplett photo](https://img.example/k.png).
        """;

    // AC1 — a root URL is scraped and its markdown comes back.
    [Fact]
    public async Task Fetch_AllowedUrl_ReturnsMarkdown()
    {
        await using var api = FakeScrapeApi.Start(map => map[RootUrl] = Ok(RootMarkdown, "The listing"));

        var page = await api.Fetcher().FetchAsync(RootUrl);

        Assert.Equal(RootUrl, page.Url);
        Assert.Equal("The listing", page.Title);
        Assert.Contains("Se komplett salgsoppgave", page.Text);
        Assert.Empty(page.Followed);
    }

    // AC3 — only links whose text matches the filter are followed; the non-matching one is not.
    [Fact]
    public async Task Fetch_FollowsOnlyLinksMatchingFilter()
    {
        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] = Ok("# Full salgsoppgave\n\nThe complete document.", "Full");
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett", maxFollowedLinks: 3);

        var followed = Assert.Single(page.Followed);
        Assert.Equal("Se komplett salgsoppgave", followed.LinkText);
        Assert.Equal("https://target.example/full", followed.Url);
        Assert.Contains("complete document", followed.Text);
        Assert.Null(followed.Error);
        // The non-matching link was never dialled.
        Assert.DoesNotContain(api.RequestedUrls, u => u == "https://other.example/x");
    }

    // AC4 — no filter ⇒ nothing is followed (opt-in default), and no second request is made.
    [Fact]
    public async Task Fetch_WithoutFilter_FollowsNothing()
    {
        await using var api = FakeScrapeApi.Start(map => map[RootUrl] = Ok(RootMarkdown));

        var page = await api.Fetcher().FetchAsync(RootUrl);

        Assert.Empty(page.Followed);
        Assert.Equal([RootUrl], api.RequestedUrls);
    }

    // AC5 — when following is requested, the ROOT request carries includeLinks=true (or the links never
    // survive into the markdown and following silently never fires). Asserts on the request URI.
    [Fact]
    public async Task Fetch_WithFilter_RequestsIncludeLinks()
    {
        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] = Ok("done");
        });

        await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett");

        var rootRequest = Assert.Single(api.RequestLines, line => line.Contains(Uri.EscapeDataString(RootUrl)));
        Assert.Contains("includeLinks=true", rootRequest);
    }

    // AC6 — a followed link (never on any allowlist — the fetcher has none) is actually dialled. Asserts
    // at the HTTP layer that the URL went out, not merely that the result object mentions it.
    [Fact]
    public async Task Fetch_FollowedLink_IsRequestedThoughNotAllowlisted()
    {
        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] = Ok("done");
        });

        await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett");

        Assert.Contains("https://target.example/full", api.RequestedUrls);
    }

    // AC7 — a model-supplied maxFollowedLinks above the ceiling is clamped to it.
    [Fact]
    public async Task Fetch_ClampsMaxFollowedLinksToCeiling()
    {
        const string ManyLinks = """
            [komplett 1](https://t1.example/) [komplett 2](https://t2.example/)
            [komplett 3](https://t3.example/) [komplett 4](https://t4.example/)
            [komplett 5](https://t5.example/)
            """;

        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(ManyLinks);
            for (var i = 1; i <= 5; i++)
            {
                map[$"https://t{i}.example/"] = Ok($"page {i}");
            }
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett", maxFollowedLinks: 99);

        Assert.Equal(WebToolLimits.MaxFollowedLinks, page.Followed.Count);
        // Exactly the ceiling number of followed links were dialled — not all five.
        Assert.Equal(
            WebToolLimits.MaxFollowedLinks,
            api.RequestedUrls.Count(u => u.StartsWith("https://t", StringComparison.Ordinal)));
    }

    // AC8 — following is depth-1: a followed page's own matching links are never expanded, and the
    // followed request itself asks for includeLinks=false (there is nothing to extract from it).
    [Fact]
    public async Task Fetch_DoesNotExpandLinksOnFollowedPages()
    {
        const string FollowedMarkdown = "A deep [komplett deeper](https://deep.example/more) link.";

        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] = Ok(FollowedMarkdown);
            map["https://deep.example/more"] = Ok("should never be reached");
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett");

        Assert.Single(page.Followed);
        // The followed page's own matching link was NOT dialled.
        Assert.DoesNotContain("https://deep.example/more", api.RequestedUrls);
        // The followed request asked for includeLinks=false.
        var followedRequest = Assert.Single(
            api.RequestLines, line => line.Contains(Uri.EscapeDataString("https://target.example/full")));
        Assert.Contains("includeLinks=false", followedRequest);
    }

    // AC9 — an image link ![alt](url) is never followed, even when its alt text matches the filter.
    [Fact]
    public async Task Fetch_DoesNotFollowImageLinks()
    {
        // Only the image matches; the sole non-image "komplett" link is the real target.
        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] = Ok("real");
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett");

        Assert.DoesNotContain("https://img.example/k.png", api.RequestedUrls);
        Assert.All(page.Followed, f => Assert.DoesNotContain("img.example", f.Url));
    }

    // AC10 — one followed link failing to scrape does not fail the root; its error is captured.
    [Fact]
    public async Task Fetch_FollowedLinkFailure_DoesNotFailRoot()
    {
        const string TwoLinks = """
            [komplett good](https://good.example/) and [komplett bad](https://bad.example/).
            """;

        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(TwoLinks);
            map["https://good.example/"] = Ok("good page");
            map["https://bad.example/"] = new TestHttpServer.HttpReply(500, "text/plain", "upstream exploded");
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett", maxFollowedLinks: 3);

        // Root still came back.
        Assert.Contains("komplett good", page.Text);
        Assert.Equal(2, page.Followed.Count);
        var bad = Assert.Single(page.Followed, f => f.Url == "https://bad.example/");
        Assert.NotNull(bad.Error);
        Assert.Empty(bad.Text);
        var good = Assert.Single(page.Followed, f => f.Url == "https://good.example/");
        Assert.Null(good.Error);
        Assert.Contains("good page", good.Text);
    }

    // A 200 that carries success:false is a failure at context.dev's end, not an empty page — the root
    // fetch must throw so the grain surfaces "couldn't read", not hand the model a blank page (ADR 0027).
    [Fact]
    public async Task Fetch_RootSuccessFalse_Throws()
    {
        await using var api = FakeScrapeApi.Start(map => map[RootUrl] =
            new TestHttpServer.HttpReply(200, "application/json", """{ "success": false, "markdown": null, "contentLength": 0 }"""));

        await Assert.ThrowsAsync<HttpRequestException>(() => api.Fetcher().FetchAsync(RootUrl));
    }

    // A followed link that comes back success:false is captured as a per-link error, never failing the
    // root — same guarantee as a transport failure (AC10).
    [Fact]
    public async Task Fetch_FollowedSuccessFalse_IsCapturedAsError()
    {
        await using var api = FakeScrapeApi.Start(map =>
        {
            map[RootUrl] = Ok(RootMarkdown);
            map["https://target.example/full"] =
                new TestHttpServer.HttpReply(200, "application/json", """{ "success": false, "markdown": null }""");
        });

        var page = await api.Fetcher().FetchAsync(RootUrl, followLinkFilter: "komplett");

        var followed = Assert.Single(page.Followed);
        Assert.NotNull(followed.Error);
        Assert.Empty(followed.Text);
    }

    // AC14 — a blank API key must not throw at registration: new AuthenticationHeaderValue("Bearer", "")
    // throws FormatException, so the header setup has to branch on a blank key.
    [Fact]
    public void BlankApiKey_DoesNotThrow()
    {
        using var blank = new HttpClient();
        ContextWebPageFetcher.ConfigureClient(blank, "");
        Assert.Null(blank.DefaultRequestHeaders.Authorization);
        Assert.Equal(new Uri(ContextWebPageFetcher.DefaultBaseAddress), blank.BaseAddress);

        using var keyed = new HttpClient();
        ContextWebPageFetcher.ConfigureClient(keyed, "ctxt_secret_test");
        Assert.Equal(new AuthenticationHeaderValue("Bearer", "ctxt_secret_test"),
            keyed.DefaultRequestHeaders.Authorization);
    }

    private static TestHttpServer.HttpReply Ok(string markdown, string title = "T") =>
        new(200, "application/json", $$"""
            {
              "success": true,
              "markdown": {{JsonSerializer.Serialize(markdown)}},
              "contentLength": {{markdown.Length}},
              "metadata": { "title": {{JsonSerializer.Serialize(title)}}, "finalUrl": null }
            }
            """);

    /// <summary>
    /// A loopback HTTP stub for context.dev: dispatches on the <c>url</c> query parameter and records
    /// every outbound request line so a test can assert on what was truly sent.
    /// </summary>
    private sealed class FakeScrapeApi : IAsyncDisposable
    {
        private readonly TestHttpServer _server;
        private readonly ConcurrentQueue<string> _requests = new();

        private FakeScrapeApi(Dictionary<string, TestHttpServer.HttpReply> map)
        {
            _server = TestHttpServer.Start(startLine =>
            {
                _requests.Enqueue(startLine);
                var target = TargetUrlOf(startLine);
                return map.TryGetValue(target, out var reply)
                    ? reply
                    : new TestHttpServer.HttpReply(404, "text/plain", $"no stub for {target}");
            });
        }

        public static FakeScrapeApi Start(Action<Dictionary<string, TestHttpServer.HttpReply>> configure)
        {
            var map = new Dictionary<string, TestHttpServer.HttpReply>(StringComparer.Ordinal);
            configure(map);
            return new FakeScrapeApi(map);
        }

        // The fetcher's client base-addresses at context.dev; here it points at the loopback stub, with
        // auth applied through the very method Program.cs uses.
        public ContextWebPageFetcher Fetcher()
        {
            var http = new HttpClient { BaseAddress = new Uri($"{_server.BaseUrl}/") };
            return new ContextWebPageFetcher(http, NullLogger<ContextWebPageFetcher>.Instance);
        }

        public IReadOnlyList<string> RequestLines => _requests.ToList();

        public IReadOnlyList<string> RequestedUrls => _requests.Select(TargetUrlOf).ToList();

        private static string TargetUrlOf(string startLine)
        {
            var path = startLine.Split(' ')[1];
            var question = path.IndexOf('?');
            if (question < 0)
            {
                return string.Empty;
            }

            foreach (var pair in path[(question + 1)..].Split('&'))
            {
                var eq = pair.IndexOf('=');
                if (eq > 0 && pair[..eq] == "url")
                {
                    return Uri.UnescapeDataString(pair[(eq + 1)..]);
                }
            }

            return string.Empty;
        }

        public ValueTask DisposeAsync() => _server.DisposeAsync();
    }
}
