namespace Agent42.Agents;

/// <summary>
/// Runs a web search and returns ranked results for the model to read (ADR 0011). By default backed by a
/// self-hosted SearXNG instance, so a query needs no API key and carries no per-user attribution. It is
/// <b>not</b> private: SearXNG is a <i>meta</i>search engine — it forwards the query to upstream engines (Google,
/// DuckDuckGo, Bing — ADR 0060) from the container's IP. When the operator sets a Tavily or Firecrawl key, a fallback
/// chain tries those first (ADR 0155) and the query goes to that vendor, which can attribute it to the operator's
/// account. Treat a search query as leaving our infrastructure, because it does.
///
/// Production talks to the SearXNG sidecar over localhost (<c>SearxngWebSearch</c>), alone or as the last member of
/// that chain (<c>FallbackWebSearch</c>); local dev and
/// tests use an in-memory implementation (<see cref="InMemoryWebSearch"/>) — the same real/in-memory
/// split as <see cref="IAttachmentStore"/> and <see cref="IDocumentIndex"/>.
/// </summary>
public interface IWebSearch
{
    /// <summary>
    /// Returns up to <paramref name="maxResults"/> results for <paramref name="query"/>, best first.
    /// Implementations clamp to <see cref="WebToolLimits.MaxSearchResults"/> and truncate snippets to
    /// <see cref="WebToolLimits.MaxSnippetLength"/> — the results are persisted into the session, so
    /// the caller does not get to ask for an unbounded payload.
    /// </summary>
    Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default);
}
