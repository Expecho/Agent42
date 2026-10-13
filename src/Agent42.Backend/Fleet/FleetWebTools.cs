using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// <c>web_search</c> and <c>fetch_web_page</c> for one fleet task (ADR 0097), over the same <see cref="IWebSearch"/>
/// (SearXNG) and <see cref="IWebPageFetcher"/> (context.dev) as a chat. The results are capped like the grain's.
///
/// <para><b>The grain's exfiltration control is kept:</b> a page can only be fetched when its exact URL came back from
/// a search in this task, or is written in the user's request. Without it, an instruction on a page or in an MCP
/// result could make an agent fetch an attacker's URL with the task's data in the query string.</para>
/// </summary>
public sealed partial class FleetWebTools
{
    private readonly IWebSearch _webSearch;
    private readonly IWebPageFetcher _fetcher;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<string, bool> _fetchable = new(StringComparer.Ordinal);

    public FleetWebTools(IWebSearch webSearch, IWebPageFetcher fetcher, string request, ILogger logger)
    {
        _webSearch = webSearch;
        _fetcher = fetcher;
        _logger = logger;
        foreach (Match match in AbsoluteUrlRegex().Matches(request ?? ""))
        {
            Remember(match.Value);
        }

        Tools =
        [
            AIFunctionFactory.Create(SearchAsync, "web_search", "Search the web, returns the best results with their URL and a snippet."),
            AIFunctionFactory.Create(FetchAsync, "fetch_web_page", "Read a web page as text. Only a URL exactly as web_search returned it, or as the user wrote it in the task, can be read."),
        ];
    }

    public IReadOnlyList<AIFunction> Tools { get; }

    private async Task<string> SearchAsync(
        [Description("What to search the web for, phrased as a search query rather than a question.")] string query,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Give me something to search for.";
        }

        try
        {
            var results = await _webSearch.SearchAsync(query, WebToolLimits.MaxSearchResults, cancellationToken);
            foreach (var result in results)
            {
                Remember(result.Url);
            }

            return results.Count == 0
                ? $"The web search for '{query}' returned no results. Try different search terms."
                : FleetTeamRunner.Cut(JsonSerializer.Serialize(results.Take(WebToolLimits.MaxSearchResults)), WebToolLimits.MaxTotalPageTextLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet web search failed");
            return $"The web search failed: {ex.Message}";
        }
    }

    private async Task<string> FetchAsync(
        [Description("The absolute http(s) URL to read, exactly as web_search returned it or as the user wrote it.")] string url,
        CancellationToken cancellationToken)
    {
        if (!TryNormalize(url, out var normalized))
        {
            return $"'{url}' is not an absolute http(s) URL, so there is no page to read.";
        }

        if (!_fetchable.ContainsKey(normalized))
        {
            _logger.LogWarning("Fleet fetch of '{Url}' refused: not a URL from a search result or the request", url);
            return $"I won't fetch '{url}': only a URL exactly as a web_search in this task returned it, or as the user wrote it in "
                + "the task, can be read. You may not build a URL yourself or take one out of the text of a page. Search for the topic instead.";
        }

        try
        {
            var page = await _fetcher.FetchAsync(url, cancellationToken: cancellationToken);
            return FleetTeamRunner.Cut(JsonSerializer.Serialize(new { page.Url, page.Title, page.Text, page.Truncated }), WebToolLimits.MaxTotalPageTextLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet fetch of '{Url}' failed", url);
            return $"I couldn't read '{url}': {ex.Message}";
        }
    }

    private void Remember(string url)
    {
        if (TryNormalize(url, out var normalized))
        {
            _fetchable[normalized] = true;
        }
    }

    // The grain's normalization: case-insensitive scheme and host, the fragment dropped, path and query exact.
    private static bool TryNormalize(string url, out string normalized)
    {
        normalized = "";
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }

        normalized = $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.PathAndQuery}";
        return true;
    }

    [GeneratedRegex("https?://[^\\s\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlRegex();
}

/// <summary>
/// Turns an MCP elicitation (ADR 0095) during a fleet task into a fleet question. A URL elicitation asks the user to
/// open the link; a form asks for the fields as JSON. The server's message is untrusted text, and the page shows it as
/// text.
/// </summary>
public sealed class FleetElicitationSink(IFleetInteraction interaction) : IMcpElicitationSink
{
    public async Task<McpElicitationAnswer> AskAsync(
        string serverName, string toolCallId, McpElicitationRequest request, CancellationToken cancellationToken)
    {
        if (request.Message.Length + (request.SchemaJson?.Length ?? 0) > McpElicitationLimits.MaxRequestChars)
        {
            return McpElicitationAnswer.Cancel;
        }

        if (request.Mode == McpElicitationLimits.UrlMode)
        {
            if (!McpElicitationLimits.TryValidateUrl(request.Url, out var uri))
            {
                return McpElicitationAnswer.Cancel;
            }

            var opened = await interaction.AskAsync(
                $"The MCP server '{serverName}' asks you to open {uri} : {request.Message}",
                [new("done", "I opened it and I'm done"), new("n", "Decline")],
                "",
                cancellationToken);
            return opened.Trim().Equals("done", StringComparison.OrdinalIgnoreCase)
                ? new McpElicitationAnswer(McpElicitationLimits.Accept)
                : new McpElicitationAnswer(McpElicitationLimits.Decline);
        }

        var answer = await interaction.AskAsync(
            $"The MCP server '{serverName}' asks: {request.Message}\nFields: {request.SchemaJson}",
            [new("n", "Decline")],
            "Or answer with the fields as a JSON object",
            cancellationToken);
        return IsJsonObject(answer)
            ? new McpElicitationAnswer(McpElicitationLimits.Accept, answer.Trim())
            : new McpElicitationAnswer(McpElicitationLimits.Decline);
    }

    private static bool IsJsonObject(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
