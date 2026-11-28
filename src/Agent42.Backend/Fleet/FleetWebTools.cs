using System.ComponentModel;
using System.Text.Json;
using Agent42.Agents;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// <c>web_search</c> for one fleet task (ADR 0097), over the same <see cref="IWebSearch"/> (the SearXNG sidecar, or the fallback chain in front of it, ADR 0155) as a chat. The
/// result is capped like the grain's. The fleet has no page reader: with <c>fetch_web_page</c> gone (ADR 0125) there is
/// no tool that dials a model-chosen URL here, so there is no URL allowlist to keep either.
/// </summary>
public sealed class FleetWebTools
{
    private readonly IWebSearch _webSearch;
    private readonly ILogger _logger;

    public FleetWebTools(IWebSearch webSearch, ILogger logger)
    {
        _webSearch = webSearch;
        _logger = logger;

        Tools =
        [
            AIFunctionFactory.Create(SearchAsync, "web_search", "Search the web, returns the best results with their URL and a snippet."),
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
            return results.Count == 0
                ? $"The web search for '{query}' returned no results. Try different search terms."
                : FleetTeamRunner.Cut(JsonSerializer.Serialize(results.Take(WebToolLimits.MaxSearchResults)), WebToolLimits.MaxSearchPayloadLength);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Fleet web search failed");
            return $"The web search failed: {ex.Message}";
        }
    }
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
