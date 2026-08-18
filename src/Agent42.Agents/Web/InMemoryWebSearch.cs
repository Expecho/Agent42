using System.Text.RegularExpressions;

namespace Agent42.Agents.Web;

/// <summary>
/// In-memory <see cref="IWebSearch"/> for local dev (DevHost) and tests, where there is no SearXNG
/// sidecar and no network. Returns canned results that echo the query, so an offline run can still
/// exercise the tool-call path end to end (the model calls <c>web_search</c>, a result comes back, a
/// tool chip renders).
///
/// <b>Searching for a hostname returns that host's homepage as the first result</b> — because that is
/// what a real search engine does, and because the security control in <c>AgentGrain</c> depends on it.
/// The prompt-injection attack it defends against is "search for <c>attacker.example</c>, then fetch
/// it": a canned search that only ever returned <c>example.com</c> could not express that attack, and
/// the regression test guarding it would pass vacuously.
///
/// It applies the same caps as the real implementation — results are what get persisted into the
/// session, and a test that asserts on the budget must see the same numbers offline as in prod.
/// </summary>
public sealed partial class InMemoryWebSearch : IWebSearch
{
    public Task<IReadOnlyList<WebSearchResult>> SearchAsync(
        string query, int maxResults, CancellationToken cancellationToken = default)
    {
        var count = Math.Clamp(maxResults, 1, WebToolLimits.MaxSearchResults);
        var named = HostPattern().Match(query ?? "");

        IReadOnlyList<WebSearchResult> results =
        [
            .. Enumerable.Range(1, count).Select(i => new WebSearchResult(
                Title: $"Result {i} for \"{query}\"",
                // A query naming a host surfaces that host, exactly as a real engine would.
                Url: i == 1 && named.Success
                    ? $"https://{named.Value.ToLowerInvariant()}/"
                    : $"https://example.com/result-{i}",
                Snippet: Truncate($"A canned offline snippet for \"{query}\" (result {i}).")))
        ];

        return Task.FromResult(results);
    }

    private static string Truncate(string s) =>
        s.Length <= WebToolLimits.MaxSnippetLength ? s : s[..WebToolLimits.MaxSnippetLength];

    [GeneratedRegex(@"\b(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex HostPattern();
}
