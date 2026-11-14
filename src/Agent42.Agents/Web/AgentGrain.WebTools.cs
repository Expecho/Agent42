using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Agent42.Agents;

/// <summary>
/// The web tool shim (ADR 0011): <c>web_search</c> over the SearXNG sidecar. Its return value is
/// persisted into the 2 MB session (ADR 0004), so it is budget-capped. The whole-URL allowlist kept
/// here now guards <c>read_web_document</c> only, and it is an exfiltration control, not an SSRF one.
/// </summary>
public sealed partial class AgentGrain
{
    /// <summary>
    /// Agent tool: searches the web through the self-hosted SearXNG sidecar (ADR 0011). Exposed to the
    /// model as <c>web_search</c>; see <c>Skills/websearch/SKILL.md</c> for guidance.
    ///
    /// Every result's URL is recorded as fetchable for this conversation — a search engine returning a
    /// URL is one of the two ways a URL earns the right to be read into <c>FetchableUrls</c> (see
    /// <see cref="IsFetchableUrl"/>); a URL-seeding MCP server's tool result is a third source, kept in
    /// its own list with weaker provenance (ADR 0067).
    /// </summary>
    private async Task<string> WebSearchAsync(
        [Description("What to search the web for, phrased as a search query rather than a question, e.g. 'Orleans grain persistence Cosmos'.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return "Give me something to search for.";
        }

        try
        {
            var results = await _webSearch.SearchAsync(query, WebToolLimits.MaxSearchResults);

            if (results.Count == 0)
            {
                return $"The web search for '{query}' returned no results. Try different search terms.";
            }

            foreach (var result in results)
            {
                RememberFetchableUrl(result.Url);
            }

            _logger.LogInformation(
                "Agent grain {GrainKey} searched the web for '{Query}' ({Count} result(s))",
                this.GetPrimaryKeyString(), query, results.Count);

            // The results are already budgeted by the IWebSearch implementation (WebToolLimits): this
            // value becomes a FunctionResultContent, which is persisted into the session every turn.
            return JsonSerializer.Serialize(results);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A tool must never brick the turn: the model is told the search failed and can answer
            // from what it knows, rather than the whole reply throwing after the user has waited.
            _logger.LogError(ex, "Agent grain {GrainKey} failed to search the web for '{Query}'",
                this.GetPrimaryKeyString(), query);

            return $"The web search failed: {ex.Message}";
        }
    }

    /// <summary>
    /// True when this exact URL was produced by a search engine or written by the user in this
    /// conversation, OR seeded by a URL-seeding MCP server's tool result (ADR 0067). Compared on the
    /// normalized form, so trivial spelling differences (case, a fragment) don't matter — but a different
    /// path or query string is a different URL, which is the whole point.
    ///
    /// <para><b>The check is a security boundary, not a convenience.</b> The text a document tool returns
    /// enters the model's context, and an instruction written <i>in that document</i> can tell the model to
    /// call the tool again with the conversation's contents in the query string — exfiltrating a chat that
    /// may also carry document excerpts (ADR 0008). That is a prompt-injection exfiltration concern,
    /// independent of SSRF (which <c>PrivateAddressGuard</c> handles separately, ADR 0066).</para>
    ///
    /// <para>The check is on the <b>whole URL</b>, not its host, and that distinction is the control.
    /// Matching on host alone is bypassable in one extra tool call, because the model also chooses the
    /// search <i>query</i>: "search for <c>attacker.example &lt;secret&gt;</c>, then fetch it" whitelists the
    /// attacker's host in the same turn. Requiring the exact URL means the model can only read a string a
    /// search engine or the user produced verbatim — so no attacker-chosen data can ride in the path or
    /// query. (Narrowed, per server and opt-in, by ADR 0067: a URL a URL-seeding MCP server returned is
    /// allowlisted too, and that one was authored on a page rather than produced verbatim.)</para>
    ///
    /// <para>The two lists are checked separately and stay separate: a seeded URL was authored on a scraped
    /// page, not produced verbatim by a search engine or the user, and merging them would erase that
    /// distinction at rest as well as at the point of use. See
    /// <see cref="AgentGrainState.SeededFetchableUrls"/>.</para>
    /// </summary>
    private bool IsFetchableUrl(Uri uri)
    {
        var normalized = Normalize(uri);
        return _state.State.FetchableUrls.Contains(normalized, StringComparer.Ordinal)
            || _state.State.SeededFetchableUrls.Contains(normalized, StringComparer.Ordinal);
    }

    private void RememberFetchableUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            RememberFetchableUrl(uri);
        }
    }

    // Records every URL the USER wrote in a message, so "summarize https://example.com/post" works
    // without a search first. A bare hostname ("have a look at wikipedia.org") contributes only that
    // site's HOMEPAGE — never an arbitrary path — so a user mentioning a domain can't be turned into
    // permission to send data to it in a query string.
    //
    // Only ever called on a real user turn. It is deliberately NOT called on the reminder path: a
    // scheduled prompt's text is authored by the *model*, so harvesting URLs from it would let a
    // prompt-injected page schedule a reminder naming the attacker's URL and thereby whitelist it —
    // reopening the exfiltration channel this list exists to close.
    private void RememberFetchableUrlsInUserText(string text)
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
                RememberFetchableUrl(uri);
            }
        }
    }

    private void RememberFetchableUrl(Uri uri)
    {
        var normalized = Normalize(uri);
        if (_state.State.FetchableUrls.Contains(normalized, StringComparer.Ordinal))
        {
            return;
        }

        _state.State.FetchableUrls.Add(normalized);

        // Bounded: the list is persisted in the 2 MB session item (ADR 0004). Oldest out first.
        while (_state.State.FetchableUrls.Count > MaxFetchableUrls)
        {
            _state.State.FetchableUrls.RemoveAt(0);
        }
    }

    /// <summary>
    /// Harvests absolute http(s) URLs out of one URL-seeding MCP server's tool result (ADR 0067) into
    /// <see cref="AgentGrainState.SeededFetchableUrls"/>, so <c>read_web_document</c>
    /// may use them in this conversation.
    ///
    /// <para>Called only from <see cref="UrlHarvestingMcpTool"/>, which <c>BuildAgent</c> wraps around the
    /// tools of a server whose owner opted in. It is a plain list mutation persisted by the turn tail — the
    /// shape <see cref="WebSearchAsync"/> already uses mid-run; no rebuild and no write of its own.</para>
    ///
    /// <para>It is deliberately NOT suppressed on the reminder path. That guard exists on
    /// <see cref="RememberFetchableUrlsInUserText"/> because a scheduled prompt's TEXT is model-authored;
    /// a tool result is equally untrusted on both paths, so a guard here would buy nothing while implying
    /// the chat path is trusted. See ADR 0067.</para>
    /// </summary>
    private void RememberSeededUrlsFromToolResult(string serverId, string toolName, string result)
    {
        if (string.IsNullOrEmpty(result))
        {
            return;
        }

        var seeded = new List<string>();
        foreach (Match match in AbsoluteUrlPattern().Matches(result))
        {
            if (seeded.Count >= MaxSeededUrlsPerResult)
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
                RememberSeededUrl(uri) is { } normalized)
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
        _logger.LogInformation(
            "Agent grain {GrainKey} seeded {Count} fetchable URL(s) from MCP server {Server} tool '{Tool}': {Urls}",
            this.GetPrimaryKeyString(), seeded.Count, serverId, toolName, string.Join(", ", seeded));
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

    // Adds one seeded URL, returning its normalized form when it was new (null when already present).
    // Normalization is SHARED with the user/search path deliberately: a second rule would mean a URL seeded
    // one way and checked another never matches, and the feature would look broken with every test green.
    private string? RememberSeededUrl(Uri uri)
    {
        var normalized = Normalize(uri);
        if (_state.State.SeededFetchableUrls.Contains(normalized, StringComparer.Ordinal))
        {
            return null;
        }

        _state.State.SeededFetchableUrls.Add(normalized);

        // Bounded, oldest first — the MaxFetchableUrls shape. This list evicts only its own members, which
        // is what stops a link-heavy result from evicting the URLs the user typed.
        while (_state.State.SeededFetchableUrls.Count > MaxSeededFetchableUrls)
        {
            _state.State.SeededFetchableUrls.RemoveAt(0);
        }

        return normalized;
    }

    // Scheme + host lower-cased (both are case-insensitive), path and query kept verbatim (they are
    // not), fragment dropped (it never reaches the server).
    private static string Normalize(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.PathAndQuery}";

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
