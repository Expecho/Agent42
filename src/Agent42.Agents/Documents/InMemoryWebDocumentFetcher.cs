using System.Text;

namespace Agent42.Agents;

/// <summary>
/// In-memory <see cref="IWebDocumentFetcher"/> for local dev (DevHost) and tests: returns a canned
/// plain-text document for any URL without touching the network, so the whole
/// <c>read_web_document</c> path — allowlist check, ingest, in-process ranking, deferred rebuild —
/// runs offline (ADR 0066).
///
/// <para>It deliberately does <b>not</b> reimplement the SSRF guard. The guard lives in
/// <c>SocketsHttpHandler.ConnectCallback</c> on the production fetcher's handler, because it must run
/// against the address actually dialled; a fake that opens no socket has nothing to guard, and a
/// re-implementation here would be a second, untested copy of a security control. What this fake DOES
/// share is <see cref="WebDocumentLimits.SanitizeFileName"/> — the file name reaches the model on both
/// paths, so it must be produced the same way on both.</para>
/// </summary>
public sealed class InMemoryWebDocumentFetcher : IWebDocumentFetcher
{
    /// <summary>
    /// Offline failure-injection hook: fetching any URL containing this marker throws, so a test can
    /// prove the grain turns a fetch failure into a message to the model rather than a thrown turn
    /// (AC13). Keyed on the URL so it needs no cross-silo instance wiring.
    /// </summary>
    public const string FailureTriggerUrlMarker = "document-injected-failure";

    /// <summary>
    /// A distinctive phrase in the canned document body, so a test can assert the ingested text really
    /// came back as an excerpt rather than merely that the tool ran.
    /// </summary>
    public const string CannedMarker = "Magrathea";

    public Task<WebDocument> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException($"'{url}' is not an absolute http(s) URL.", nameof(url));
        }

        if (url.Contains(FailureTriggerUrlMarker, StringComparison.Ordinal))
        {
            throw new HttpRequestException($"Injected offline failure fetching '{url}'.");
        }

        var text = $"""
            {CannedMarker} is a legendary planet whose inhabitants built custom worlds to order.
            This is canned offline document content for {url}.
            The dolphins left a message before they departed, and it was mostly about the fish.
            """;

        return Task.FromResult(new WebDocument(
            Url: url,
            FileName: WebDocumentLimits.SanitizeFileName(uri, DocumentMediaTypes.PlainText),
            MediaType: DocumentMediaTypes.PlainText,
            Content: Encoding.UTF8.GetBytes(text)));
    }
}
