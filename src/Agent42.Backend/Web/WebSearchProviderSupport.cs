using System.Net;
using System.Net.Http.Headers;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// What every keyed web-search provider shares (ADR 0155): status classification, the snippet cap, and a request that
/// carries the key in a header (never in the URL). Failures become a <see cref="WebSearchProviderException"/> whose
/// message is fixed text: a response body, URL or key must never reach the model through it.
/// </summary>
internal static class WebSearchProviderSupport
{
    /// <summary>Throws the classified failure for a non-success response; returns normally for a 2xx.</summary>
    /// <param name="exhaustedStatuses">The vendor's own "out of credits" statuses (e.g. Tavily 432, Firecrawl 402).</param>
    public static void ThrowIfFailed(HttpResponseMessage response, params int[] exhaustedStatuses)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var status = (int)response.StatusCode;
        if (Array.IndexOf(exhaustedStatuses, status) >= 0)
        {
            throw new WebSearchProviderException(WebSearchFailureKind.Exhausted);
        }

        throw status switch
        {
            401 or 403 => new WebSearchProviderException(WebSearchFailureKind.Unauthorized),
            429 => new WebSearchProviderException(WebSearchFailureKind.RateLimited, RetryAfter(response)),
            _ => new WebSearchProviderException(WebSearchFailureKind.Transient),
        };
    }

    /// <summary>A <c>Retry-After</c> in seconds, or null. A date form or garbage is ignored: the default cooldown applies.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var delta = response.Headers.RetryAfter?.Delta;
        return delta is { } wait && wait > TimeSpan.Zero ? wait : null;
    }

    public static HttpRequestMessage Post(string path, string apiKey, string json)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        return request;
    }

    // Snippets are persisted into the session and re-sent every turn (WebToolLimits), so they are capped here.
    public static string Snippet(string? content)
    {
        var text = (content ?? string.Empty).Trim();
        return text.Length <= WebToolLimits.MaxSnippetLength ? text : text[..WebToolLimits.MaxSnippetLength];
    }
}
