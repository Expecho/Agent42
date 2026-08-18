using System.Net;
using System.Text.Json;
using Agent42.Agents;
using Agent42.Agents.Web;

namespace Agent42.Backend;

/// <summary>
/// <see cref="IWebSearch"/> over the self-hosted SearXNG sidecar (ADR 0011). The sidecar runs in the
/// same Container App and is reached over <c>localhost</c>, so a query costs nothing, needs no API key
/// and is not attributable to a user account. It is <b>not</b> private: SearXNG is a <i>meta</i>search
/// engine and forwards the query to upstream engines (DuckDuckGo, Bing).
///
/// The <see cref="HttpClient"/> is a named client with <b>explicit</b> timeouts (see Program.cs):
/// <c>AgentGrain</c> is non-reentrant, so a slow search serializes the conversation's turn and delays
/// the first token — the standard resilience handler's generous defaults are the wrong budget here.
/// </summary>
public sealed class SearxngWebSearch(HttpClient httpClient, ILogger<SearxngWebSearch> logger) : IWebSearch
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "searxng";

    public async Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var take = Math.Clamp(maxResults, 1, WebToolLimits.MaxSearchResults);

        // Deliberately NO &safesearch= parameter: a per-request value OVERRIDES the instance setting,
        // so hardcoding one here made `search.safe_search` in infra/searxng/settings.yml dead config —
        // the file could say "off" while every actual query asked for "on". The instance owns the
        // policy; change it there.
        var requestUri = $"search?q={Uri.EscapeDataString(query)}&format=json";

        using var response = await httpClient.GetAsync(requestUri, cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden)
        {
            // The single most likely way this integration breaks. SearXNG only serves the formats
            // listed under `search.formats` in settings.yml — `html` alone by default — and answers
            // a request for an unlisted format with 403, which reads like an auth failure and sends
            // people hunting for a missing API key that does not exist.
            throw new InvalidOperationException(
                "SearXNG returned 403 Forbidden for a format=json request. This almost always means the " +
                "JSON API is not enabled on the instance: `json` must be listed under `search.formats` " +
                "in infra/searxng/settings.yml. It is not an authentication failure — the sidecar has no auth.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"SearXNG search failed with HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("results", out var results) ||
            results.ValueKind != JsonValueKind.Array)
        {
            logger.LogWarning("SearXNG response had no results array for query {Query}.", query);
            return [];
        }

        var mapped = new List<WebSearchResult>(take);
        foreach (var result in results.EnumerateArray())
        {
            if (mapped.Count == take)
            {
                break;
            }

            var url = GetString(result, "url");
            if (string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            mapped.Add(new WebSearchResult(
                Title: GetString(result, "title") ?? url,
                Url: url,
                // SearXNG calls the snippet "content".
                Snippet: Snippet(GetString(result, "content"))));
        }

        return mapped;
    }

    private static string? GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // Snippets are persisted into the session and re-sent every turn (WebToolLimits), so they are
    // capped here rather than trusted to be short.
    private static string Snippet(string? content)
    {
        var text = (content ?? string.Empty).Trim();
        return text.Length <= WebToolLimits.MaxSnippetLength
            ? text
            : text[..WebToolLimits.MaxSnippetLength];
    }
}
