namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IWebPageFetcher"/> for local dev (DevHost) and tests: returns canned text
/// for any URL without touching the network.
///
/// It deliberately does <b>not</b> reimplement the SSRF guard. The guard is a production concern of
/// <c>WebPageFetcher</c> (it validates the IP actually dialled, which only means something when a
/// real socket is opened), and faking it here would give a false sense that the offline path proves
/// anything about it. The guard is tested directly, against <c>PrivateAddressGuard</c> and a real
/// connect.
/// </summary>
public sealed class InMemoryWebPageFetcher : IWebPageFetcher
{
    public Task<WebPageContent> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{url}' is not an absolute http(s) URL.", nameof(url));
        }

        return Task.FromResult(new WebPageContent(
            Url: url,
            Title: $"Canned page at {uri.Host}",
            Text: $"Canned offline page content for {url}.",
            Truncated: false));
    }
}
