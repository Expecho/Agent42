namespace Agent42.Agents;

/// <summary>
/// Fetches one web page and reduces it to readable markdown for the model (ADR 0011, mechanism replaced
/// by ADR 0027), optionally following depth-1 links whose text matches a filter.
///
/// <b>This is no longer an SSRF sink.</b> Under ADR 0011 the container dialled the model-chosen URL
/// itself, so it had to guard the address at connect time. As of ADR 0027 the request is made by a
/// <b>third party</b> (the hosted context.dev scrape API) — our container never opens a socket to the
/// model's URL — so the SSRF guard (<c>PrivateAddressGuard</c>) is gone. What did <b>not</b> go away is
/// the caller's other responsibility: deciding <i>which</i> URLs the model may name at all. The
/// whole-URL allowlist (<c>AgentGrainState.FetchableUrls</c>) is a prompt-injection <i>exfiltration</i>
/// control, independent of SSRF, and remains the caller's job — <c>AgentGrain</c> checks the <b>root</b>
/// URL against it before calling here. Followed links are deliberately exempt from that check (they come
/// from the page and would never be in the list), which is an accepted residual risk recorded in ADR 0027.
///
/// Production is <c>ContextWebPageFetcher</c>; local dev and tests use <see cref="InMemoryWebPageFetcher"/>.
/// </summary>
public interface IWebPageFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/> and returns its readable text. Each page's text is capped at
    /// <see cref="WebToolLimits.MaxPageTextLength"/>, and the root plus every followed page together at
    /// <see cref="WebToolLimits.MaxTotalPageTextLength"/> (the whole result is persisted into the session).
    ///
    /// Following is <b>opt-in</b>: with a null or empty <paramref name="followLinkFilter"/> nothing is
    /// followed and <see cref="WebPageContent.Followed"/> is empty. When the filter is non-empty, links
    /// whose text contains it (case-insensitive) are followed, up to <paramref name="maxFollowedLinks"/>
    /// clamped to <see cref="WebToolLimits.MaxFollowedLinks"/>. Following is depth-1 only — a followed
    /// page's own links are never expanded — and a single followed link that fails to scrape is captured
    /// on its <see cref="FollowedPage.Error"/> rather than failing the whole fetch.
    ///
    /// Throws when the <b>root</b> fetch fails — the scrape API is unreachable or errors, or the URL is
    /// not an absolute http(s) URL — so the caller can tell the model the page could not be read.
    /// </summary>
    Task<WebPageContent> FetchAsync(
        string url,
        string? followLinkFilter = null,
        int maxFollowedLinks = 1,
        CancellationToken cancellationToken = default);
}
