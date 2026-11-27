using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The web tool shim (ADR 0011): <c>web_search</c> over the SearXNG sidecar, plus the three feeders of the
/// fetchable-URL allowlist. Its return value is persisted into the 2 MB session (ADR 0004), so it is budget-capped.
/// The allowlist (<see cref="FetchableUrlList"/>) guards <c>read_web_document</c> only, and it is an exfiltration
/// control, not an SSRF one.
///
/// <para>Built per grain activation with <c>new</c> (ADR 0147, amended by plan cc-12); public only because this repo
/// has no <c>InternalsVisibleTo</c>. It holds the search service and the <see cref="FetchableUrlList"/> port, no grain
/// state. <b>Call sites are the control</b>: <see cref="RememberFromUserText"/> may be called only from
/// <c>AgentGrain.BuildUserTurnAsync</c>, and <see cref="RememberSeededUrlsFromToolResult"/> only through the
/// <c>UrlHarvestingMcpTool</c> delegate. <c>ProjectLayoutTests</c> pins both.</para>
/// </summary>
public sealed partial class WebTools(IWebSearch webSearch, ILogger logger, string grainKey, FetchableUrlList fetchable)
{
    /// <summary>
    /// Agent tool: searches the web through the self-hosted SearXNG sidecar (ADR 0011). Exposed to the
    /// model as <c>web_search</c>; see <c>Skills/websearch/SKILL.md</c> for guidance.
    ///
    /// Every result's URL is recorded as fetchable for this conversation: a search engine returning a URL is one of
    /// the two ways a URL earns the right to be read into <c>FetchableUrls</c>; a URL-seeding MCP server's tool result
    /// is a third source, kept in its own list with weaker provenance (ADR 0067).
    /// </summary>
    public async Task<string> WebSearchAsync(
        [Description("What to search the web for, phrased as a search query rather than a question, e.g. 'Orleans grain persistence Cosmos'.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Give me something to search for.";
        }

        try
        {
            var results = await webSearch.SearchAsync(query, WebToolLimits.MaxSearchResults);

            if (results.Count == 0)
            {
                return $"The web search for '{query}' returned no results. Try different search terms.";
            }

            foreach (var result in results)
            {
                RememberFetchableUrl(result.Url);
            }

            logger.LogInformation(
                "Agent grain {GrainKey} searched the web for '{Query}' ({Count} result(s))",
                grainKey, query, results.Count);

            // The results are already budgeted by the IWebSearch implementation (WebToolLimits): this
            // value becomes a FunctionResultContent, which is persisted into the session every turn.
            return JsonSerializer.Serialize(results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool must never brick the turn: the model is told the search failed and can answer
            // from what it knows, rather than the whole reply throwing after the user has waited.
            logger.LogError(ex, "Agent grain {GrainKey} failed to search the web for '{Query}'", grainKey, query);

            return $"The web search failed: {ex.Message}";
        }
    }

    private void RememberFetchableUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            fetchable.Remember(uri);
        }
    }

    /// <summary>
    /// Records every URL the USER wrote in a message, so "summarize https://example.com/post" works without a search
    /// first. A bare hostname ("have a look at wikipedia.org") contributes only that site's HOMEPAGE, never an
    /// arbitrary path, so a user mentioning a domain can't be turned into permission to send data to it in a query
    /// string.
    ///
    /// <para>Only ever called on a real user turn (<c>BuildUserTurnAsync</c>, the single call site). It is
    /// deliberately NOT reached from the reminder path: a scheduled prompt's text is authored by the <i>model</i>, so
    /// harvesting URLs from it would let a prompt-injected page schedule a reminder naming the attacker's URL and
    /// thereby whitelist it, reopening the exfiltration channel this list exists to close. A reminder is excluded
    /// structurally (it calls the agent directly and never builds a user turn), not by a flag here: agent-run and
    /// fleet-result turns do reach it, as before.</para>
    /// </summary>
    public void RememberFromUserText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        foreach (Match match in UrlPattern().Matches(text))
        {
            var scheme = match.Groups["scheme"].Success ? "" : "https://";
            if (Uri.TryCreate(scheme + match.Value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            {
                fetchable.Remember(uri);
            }
        }
    }

    /// <summary>
    /// Harvests absolute http(s) URLs out of one URL-seeding MCP server's tool result (ADR 0067) into the seeded list,
    /// so <c>read_web_document</c> may use them in this conversation.
    ///
    /// <para>Called only through <c>UrlHarvestingMcpTool</c>, which <c>BuildAgent</c> wraps around the tools of a
    /// server whose owner opted in. It is a plain list mutation persisted by the turn tail: no rebuild and no write
    /// of its own.</para>
    ///
    /// <para>It is deliberately NOT suppressed on the reminder path. The guard on <see cref="RememberFromUserText"/>
    /// exists because a scheduled prompt's TEXT is model-authored; a tool result is equally untrusted on both paths,
    /// so a guard here would buy nothing while implying the chat path is trusted. See ADR 0067.</para>
    /// </summary>
    public void RememberSeededUrlsFromToolResult(string serverId, string toolName, string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return;
        }

        var seeded = new List<string>();
        foreach (Match match in AbsoluteUrlPattern().Matches(result))
        {
            if (seeded.Count >= AgentGrain.MaxSeededUrlsPerResult)
            {
                break;
            }

            // The harvest sees the ALREADY-TRUNCATED result: UrlHarvestingMcpTool wraps OUTSIDE
            // TruncatingMcpTool, so a URL straddling the 8 KB boundary arrives as a garbage prefix that
            // would consume a slot and never match anything. Drop any candidate that ends at the cut.
            if (IsAtTruncationBoundary(result, match))
            {
                continue;
            }

            if (Uri.TryCreate(match.Value, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                fetchable.RememberSeeded(uri) is { } normalized)
            {
                seeded.Add(normalized);
            }
        }

        if (seeded.Count == 0)
        {
            return;
        }

        // The audit line is part of the design, not decoration: the separate list is cited as a provenance
        // record, and provenance nothing reads is dead state. This is ADR 0027's rule that an
        // allowlist-exempt channel must be auditable after the fact, applied to the third allowlist feeder.
        logger.LogInformation(
            "Agent grain {GrainKey} seeded {Count} fetchable URL(s) from MCP server {Server} tool '{Tool}': {Urls}",
            grainKey, seeded.Count, serverId, toolName, string.Join(", ", seeded));
    }

    // A candidate running right up to where TruncatingMcpTool cut the result was chopped mid-URL, so what
    // survives is the prefix of a URL that never existed — it would consume a seed slot and never match.
    // Both shapes are covered: a result cut exactly at the budget, and one carrying the truncation marker
    // (where the match ends just before the marker).
    private static bool IsAtTruncationBoundary(string result, Match match)
    {
        if (result.Length < McpToolLimits.MaxToolResultLength)
        {
            return false;
        }

        var end = match.Index + match.Length;
        return end >= result.Length
            || (result.EndsWith(McpToolLimits.TruncationMarker, StringComparison.Ordinal)
                && end >= result.Length - McpToolLimits.TruncationMarker.Length);
    }

    // Matches a URL in user prose, with or without a scheme: "https://example.com/post?a=b" keeps its
    // path and query, and a bare "wikipedia.org" matches as a host (the caller turns that into the
    // homepage only). Over-matching (e.g. "notes.txt") is harmless — an unresolvable host simply fails
    // to fetch; under-matching would silently refuse a link the user actually pasted.
    [GeneratedRegex(
        @"\b(?<scheme>https?://)?(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}(?::\d+)?(?:/[^\s]*)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex UrlPattern();

    // STRICTER than UrlPattern(), and that is the point. UrlPattern matches a bare hostname because user
    // prose says "have a look at wikipedia.org" — right there, wrong here: an MCP result is JSON, where a
    // bare "example.com" turns up in prose, error strings and unrelated fields, and turning each of those
    // into a fetchable homepage would seed the model on its own noise. A URL earns a seed only by arriving
    // ABSOLUTE, with a scheme.
    //
    // **The character class is the whole trimming mechanism, and it is deliberately the only one.** It
    // stops at JSON structure (quote, apostrophe, comma, backslash, brace, bracket, angle) as well as
    // whitespace, so `"href":"https://x/y.pdf"` yields exactly the URL. A post-hoc TrimEnd of "trailing
    // punctuation" on top of it is not defence in depth — every character it could remove is already
    // excluded here, so its only reachable effect is to damage a URL that legitimately ends in one
    // (`…/wiki/Foo_(bar)`), producing a seeded string the model's own URL can never match. One was written,
    // measured to be dead, and removed; SeededUrl_KeepsATrailingParenthesisThatIsPartOfThePath pins it out.
    [GeneratedRegex(
        "https?://[^\\s\"',\\\\\\]}<>]+",
        RegexOptions.IgnoreCase)]
    private static partial Regex AbsoluteUrlPattern();
}
