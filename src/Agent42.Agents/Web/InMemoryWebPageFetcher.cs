namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IWebPageFetcher"/> for local dev (DevHost) and tests: returns canned markdown
/// for any URL without touching the network, and honours opt-in link following (ADR 0027) with canned
/// followed pages so the grain's follow path is exercisable offline.
///
/// It deliberately does <b>not</b> reimplement any address guard, because there is nothing to guard:
/// as of ADR 0027 the real fetcher (<c>ContextWebPageFetcher</c>) makes no outbound socket to the
/// model's URL — the hosted scrape API does — so the old SSRF sink is gone from this path entirely.
/// </summary>
public sealed class InMemoryWebPageFetcher : IWebPageFetcher
{
    /// <summary>
    /// Offline failure-injection hook (AC12): a fetch of any URL containing this marker throws, so a
    /// test can prove the grain turns a fetch failure into a message to the model rather than a thrown
    /// turn. Keyed on the URL so it needs no cross-silo instance wiring — a test drives it by getting
    /// the fake to fetch a URL containing this marker.
    /// </summary>
    public const string FailureTriggerUrlMarker = "fetch-injected-failure";

    public Task<WebPageContent> FetchAsync(
        string url,
        string? followLinkFilter = null,
        int maxFollowedLinks = 1,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{url}' is not an absolute http(s) URL.", nameof(url));
        }

        // The root fetch is what throws on failure (mirrors the real fetcher, which throws when the
        // scrape API is unreachable) — a followed link failing must never fail the root.
        if (url.Contains(FailureTriggerUrlMarker, StringComparison.Ordinal))
        {
            throw new HttpRequestException($"Injected offline failure fetching '{url}'.");
        }

        return Task.FromResult(new WebPageContent(
            Url: url,
            Title: $"Canned page at {uri.Host}",
            Text: $"Canned offline page content for {url}.",
            Truncated: false,
            Followed: BuildFollowed(uri, followLinkFilter, maxFollowedLinks)));
    }

    // Following is opt-in: no filter ⇒ no following (ADR 0027). With a filter, return canned pages whose
    // link text contains it, up to the clamped ceiling — enough to exercise the grain's follow path
    // offline without pretending to be the real markdown link extractor (that is unit-tested against
    // ContextWebPageFetcher).
    private static IReadOnlyList<FollowedPage> BuildFollowed(Uri root, string? filter, int maxFollowedLinks)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return WebPageContent.NoFollowed;
        }

        var count = Math.Clamp(maxFollowedLinks, 0, WebToolLimits.MaxFollowedLinks);
        if (count == 0)
        {
            return WebPageContent.NoFollowed;
        }

        var followed = new List<FollowedPage>(count);
        for (var i = 0; i < count; i++)
        {
            var linkText = $"{filter} ({i + 1})";
            var followedUrl = new Uri(root, $"/followed-{i + 1}").ToString();
            followed.Add(new FollowedPage(
                LinkText: linkText,
                Url: followedUrl,
                Title: $"Canned followed page at {root.Host}",
                Text: $"Canned offline followed content for '{linkText}' at {followedUrl}.",
                Truncated: false,
                Error: null));
        }

        return followed;
    }
}
