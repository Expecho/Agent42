using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// The SearXNG sidecar as the last, never-benched chain member (ADR 0155). A thin adapter: <see cref="SearxngWebSearch"/>
/// is untouched, because it throws plain exceptions (including its 403 "JSON API is not enabled" message, which is not a
/// key problem) and, being last and never benched, its failures need no classification.
/// </summary>
public sealed class SearxngSearchProvider(IWebSearch inner) : IWebSearchProvider
{
    public const string Provider = "searxng";

    public string Name => Provider;

    public bool CanBeBenched => false;

    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default) =>
        inner.SearchAsync(query, maxResults, cancellationToken);
}
