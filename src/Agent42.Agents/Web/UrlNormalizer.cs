namespace Agent42.Agents;

/// <summary>
/// The one normalization the fetchable-URL allowlist compares on (ADR 0011/0067), shared by
/// <see cref="FetchableUrlList"/> and the web-document ingest. A second rule would mean a URL recorded one way and
/// checked another never matches, and the feature would look broken with every test green.
/// </summary>
public static class UrlNormalizer
{
    // Scheme + host lower-cased (both are case-insensitive), path and query kept verbatim (they are
    // not), fragment dropped (it never reaches the server).
    public static string Normalize(Uri uri) =>
        $"{uri.Scheme.ToLowerInvariant()}://{uri.Authority.ToLowerInvariant()}{uri.PathAndQuery}";
}
