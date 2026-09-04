namespace Agent42.Agents;

/// <summary>
/// The bytes of one web-hosted document, ready for the ingestion pipeline (ADR 0066).
/// <paramref name="FileName"/> is derived from the URL and <b>hard-sanitized</b>
/// (<see cref="WebDocumentLimits.SanitizeFileName"/>) because it reaches the model — in the agent's
/// instructions and in every citation. <paramref name="MediaType"/> is resolved from the response, so
/// it is what the extractor will actually be handed.
/// </summary>
public sealed record WebDocument(string Url, string FileName, string MediaType, byte[] Content);

/// <summary>
/// Fetches one web-hosted DOCUMENT (PDF / Office / text) as raw bytes so it can be ingested into the
/// conversation's RAG index (ADR 0066). This is the second, document-only web path — deliberately
/// disjoint from <see cref="IWebPageFetcher"/>, which reads HTML pages through the hosted context.dev
/// scrape API and cannot deliver bytes at all.
///
/// <para><b>This one IS an SSRF sink, and that is the whole reason the guard came back.</b> Unlike
/// <c>fetch_web_page</c> (ADR 0027, where context.dev makes the request), the production implementation
/// dials the model-chosen URL <b>from our container</b> — which holds the managed identity that Azure
/// IMDS will mint Cosmos/Storage/OpenAI tokens for. <c>PrivateAddressGuard</c> is installed at
/// <b>connect</b> time so every redirect hop is checked and DNS rebinding cannot slip past. Do not
/// generalise from the sibling interface's "no longer an SSRF sink" note: it is true there and false
/// here.</para>
///
/// <para>The whole-URL allowlist (<c>AgentGrainState.FetchableUrls</c>) is a <i>separate</i>,
/// prompt-injection exfiltration control and stays the caller's job — <c>AgentGrain</c> checks it
/// before calling here. There is no link following on this path, so there is no allowlist-exempt
/// content and none of ADR 0027's residual risk.</para>
///
/// <para>Production is <c>GuardedDocumentFetcher</c>; local dev and tests use
/// <see cref="InMemoryWebDocumentFetcher"/>.</para>
/// </summary>
public interface IWebDocumentFetcher
{
    /// <summary>
    /// Fetches <paramref name="url"/> and returns its bytes, capped at
    /// <see cref="WebDocumentLimits.MaxDownloadBytes"/> and bounded by
    /// <see cref="WebDocumentLimits.DownloadDeadline"/>.
    ///
    /// <para>Throws when the URL is not an absolute http(s) URL, when the address is refused by the SSRF
    /// guard, when the response fails, when the body exceeds the cap, or when the content is not a
    /// supported document type — including <c>text/html</c>, which is refused explicitly because an HTML
    /// error page served for a <c>.pdf</c> URL is the common case. The caller turns every failure into a
    /// message for the model rather than letting it brick the turn.</para>
    /// </summary>
    Task<WebDocument> FetchAsync(string url, CancellationToken cancellationToken = default);
}
