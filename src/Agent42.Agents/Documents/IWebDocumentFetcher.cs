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
/// conversation's RAG index (ADR 0066). This is the only model-invoked web fetch path (ADR 0125
/// removed <c>fetch_web_page</c>).
///
/// <para><b>This one IS an SSRF sink, and that is the whole reason the guard exists.</b> The
/// production implementation dials the model-chosen URL <b>from our container</b> — which holds the managed identity that Azure
/// IMDS will mint Cosmos/Storage/OpenAI tokens for. <c>PrivateAddressGuard</c> is installed at
/// <b>connect</b> time so every redirect hop is checked and DNS rebinding cannot slip past.</para>
///
/// <para>The whole-URL allowlist (<c>AgentGrainState.FetchableUrls</c> and, since ADR 0067,
/// <c>AgentGrainState.SeededFetchableUrls</c>) is a <i>separate</i>, prompt-injection exfiltration
/// control and stays the caller's job — <c>AgentGrain</c> checks it before calling here. There is no
/// link following on this path, so there is no allowlist-exempt content.</para>
///
/// <para><b>ADR 0067 widened what can reach this fetcher, and this is the path where that matters
/// most.</b> With a URL-seeding MCP server configured, the URL handed here may be one authored on a page
/// that server scraped, rather than one a search engine or the user produced verbatim — and it is THIS
/// fetcher, holding the managed identity, that dials it. The address guard is not a substitute: it
/// permits every public address by construction.</para>
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
