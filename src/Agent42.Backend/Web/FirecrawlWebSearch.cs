using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebSearchProvider"/> over Firecrawl <c>POST /v2/search</c> (ADR 0155). Cheapest mode: no
/// <c>scrapeOptions</c>, so only url, title and description come back (no page is scraped), which costs 2 credits per
/// 10 results. The key travels as a bearer header. Out of credits is 402. Page reading stays removed (ADR 0125).
/// </summary>
public sealed class FirecrawlWebSearch(HttpClient httpClient, string apiKey) : IWebSearchProvider
{
    public const string HttpClientName = "websearch-firecrawl";
    public const string Provider = "firecrawl";

    public string Name => Provider;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var take = Math.Clamp(maxResults, 1, WebToolLimits.MaxSearchResults);
        var body = JsonSerializer.Serialize(new { query, limit = take });

        using var request = WebSearchProviderSupport.Post("v2/search", apiKey, body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        WebSearchProviderSupport.ThrowIfFailed(response, 402);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            // A 2xx without the data object (e.g. success:false) is not an empty answer: fail so the chain moves on.
            throw new WebSearchProviderException(WebSearchFailureKind.Transient);
        }

        if (!data.TryGetProperty("web", out var web) || web.ValueKind != JsonValueKind.Array)
        {
            return []; // data present, no web results
        }

        var mapped = new List<WebSearchResult>(take);
        foreach (var result in web.EnumerateArray())
        {
            if (mapped.Count == take)
            {
                break;
            }

            var url = Str(result, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            // Firecrawl calls the snippet "description".
            mapped.Add(new WebSearchResult(Str(result, "title") ?? url, url, WebSearchProviderSupport.Snippet(Str(result, "description"))));
        }

        return mapped;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
