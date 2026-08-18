namespace Agent42.Agents.Web;

/// <summary>
/// Runs a web search and returns ranked results for the model to read (ADR 0011). Backed by a
/// self-hosted SearXNG instance, so a query needs no API key, carries no per-user attribution and
/// costs nothing. It is <b>not</b> private: SearXNG is a <i>meta</i>search engine — it forwards the
/// query to upstream engines (DuckDuckGo, Bing) from the container's IP. Treat a search query as
/// leaving our infrastructure, because it does.
///
/// Production talks to the SearXNG sidecar over localhost (<c>SearxngWebSearch</c>); local dev and
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
