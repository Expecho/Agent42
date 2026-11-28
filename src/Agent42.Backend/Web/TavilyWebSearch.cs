using System.Text.Json;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebSearchProvider"/> over Tavily <c>POST /search</c> (ADR 0155). Cheapest mode: <c>basic</c> depth, no raw
/// content, no generated answer, which costs 1 credit per call. The key travels as a bearer header. The vendor's
/// out-of-credits signals are 432 (key or plan limit) and 433 (pay-as-you-go limit).
/// </summary>
public sealed class TavilyWebSearch(HttpClient httpClient, string apiKey) : IWebSearchProvider
{
    public const string HttpClientName = "websearch-tavily";
    public const string Provider = "tavily";

    public string Name => Provider;

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var take = Math.Clamp(maxResults, 1, WebToolLimits.MaxSearchResults);
        var body = JsonSerializer.Serialize(new
        {
            query,
            search_depth = "basic",
            include_raw_content = false,
            include_answer = false,
            max_results = take,
        });

        using var request = WebSearchProviderSupport.Post("search", apiKey, body);
        using var response = await httpClient.SendAsync(request, cancellationToken);
        WebSearchProviderSupport.ThrowIfFailed(response, 432, 433);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            // A 2xx without the results container is schema drift, not an empty answer: fail so the chain moves on.
            throw new WebSearchProviderException(WebSearchFailureKind.Transient);
        }

        var mapped = new List<WebSearchResult>(take);
        foreach (var result in results.EnumerateArray())
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

            // Tavily calls the snippet "content" (a short description, a few chunks joined).
            mapped.Add(new WebSearchResult(Str(result, "title") ?? url, url, WebSearchProviderSupport.Snippet(Str(result, "content"))));
        }

        return mapped;
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
