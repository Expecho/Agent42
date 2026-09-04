using System.Net;
using System.Net.Sockets;
using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Thrown when the fetcher refused to connect to an address (SSRF guard). Distinct from a transport
/// failure: the model is told the URL is not fetchable, not that the site was down.
/// </summary>
public sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>
/// <see cref="IWebDocumentFetcher"/> over a guarded <see cref="HttpClient"/> (ADR 0066): fetches one
/// model-chosen document URL as bytes, from <b>our own container</b>, for ingestion into conversation
/// RAG.
///
/// <para><b>Read the SSRF note on <see cref="IWebDocumentFetcher"/> before touching this class.</b> This
/// is the one web path where our container dials a model-chosen address, so
/// <see cref="PrivateAddressGuard"/> is installed at <b>connect</b> time — see
/// <see cref="CreateHandler"/> — and the client must be built with that handler or the whole control is
/// absent while every class-level test still passes.</para>
///
/// <para>Three independent bounds apply and none substitutes for another: the size cap is enforced
/// <i>while streaming</i> (a <c>Content-Length</c> pre-check <b>and</b> a bounded read loop, so a 500 MB
/// body is never buffered), the whole download runs under its own deadline (see
/// <see cref="WebDocumentLimits.DownloadDeadline"/> for why <c>HttpClient.Timeout</c> is not enough),
/// and the content type must be one the extractor supports.</para>
/// </summary>
/// <param name="httpClient">
/// Must be built over <see cref="CreateHandler"/>, or the SSRF guard is simply absent.
/// </param>
/// <param name="downloadDeadline">
/// Overrides <see cref="WebDocumentLimits.DownloadDeadline"/>. A parameter solely so a test can prove
/// the slow-body abandonment in milliseconds rather than in the production 30 seconds; production
/// passes nothing.
/// </param>
public sealed class GuardedDocumentFetcher(HttpClient httpClient, TimeSpan? downloadDeadline = null)
    : IWebDocumentFetcher
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "webdocumentfetch";

    private readonly TimeSpan _downloadDeadline = downloadDeadline ?? WebDocumentLimits.DownloadDeadline;

    /// <summary>
    /// Builds the primary handler for the document client, with the SSRF guard installed at
    /// <b>connect</b> time.
    ///
    /// The callback resolves the hostname itself, keeps only the addresses
    /// <paramref name="isAllowed"/> accepts, and then dials <b>those addresses</b> — never the
    /// hostname. That is what makes the check TOCTOU-free: the socket cannot re-resolve and land
    /// somewhere else. Because every redirect hop opens its own connection through this same callback,
    /// redirects are covered without extra code.
    ///
    /// <paramref name="isAllowed"/> is the policy and is <see cref="PrivateAddressGuard.IsPubliclyRoutable"/>
    /// in production; it is a parameter so a test can host a server on loopback and still prove that
    /// the guard fires at connect time, on the address actually dialled.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(Func<IPAddress, bool> isAllowed) => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectCallback = async (context, cancellationToken) =>
        {
            var host = context.DnsEndPoint.Host;

            var addresses = IPAddress.TryParse(host, out var literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);

            var permitted = Array.FindAll(addresses, a => isAllowed(a));
            if (permitted.Length == 0)
            {
                throw new BlockedAddressException(
                    $"Refusing to connect to '{host}': it resolves only to non-public addresses " +
                    "(loopback, link-local, private, or CGNAT). Internal hosts are not fetchable.");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                // Connect to the vetted IPs, not to the hostname — a hostname here would resolve again.
                await socket.ConnectAsync(permitted, context.DnsEndPoint.Port, cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    public async Task<WebDocument> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BlockedAddressException(
                $"'{url}' is not an absolute http(s) URL. Only http and https documents can be read.");
        }

        // The deadline covers the request AND the body read. HttpClient.Timeout stops applying once the
        // headers are in under ResponseHeadersRead, which is exactly the window a slow-drip body would
        // stall a non-reentrant grain in.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_downloadDeadline);

        using var response = await SendAsync(uri, deadline.Token);
        response.EnsureSuccessStatusCode();

        // Cheap pre-check. Not a substitute for the bounded read below: Content-Length is optional, and
        // a chunked or lying response has none.
        if (response.Content.Headers.ContentLength is long declared && declared > WebDocumentLimits.MaxDownloadBytes)
        {
            throw new InvalidOperationException(
                $"'{url}' is {declared} bytes, which is over the {WebDocumentLimits.MaxDownloadBytes}-byte " +
                "limit for a document. Try a smaller document.");
        }

        // Redirects ARE followed (up to 5 hops, each one address-guarded by the same ConnectCallback), so
        // the bytes may well come from a URL that was never on the allowlist. The type and the name are
        // therefore derived from the FINAL URI, not the one the model named: attributing hop 5's content
        // to the allowlisted URL's name would be the one genuinely misleading thing this could do. See
        // the redirect residual in ADR 0066.
        var landed = response.RequestMessage?.RequestUri ?? uri;

        var mediaType = ResolveMediaType(landed, response);
        var bytes = await ReadCappedAsync(response, url, deadline.Token);

        return new WebDocument(
            Url: url,
            FileName: WebDocumentLimits.SanitizeFileName(landed, mediaType),
            MediaType: mediaType,
            Content: bytes);
    }

    // The exact set this path will ingest. Deliberately an ALLOWLIST rather than DocumentMediaTypes.IsSupported,
    // which admits ANY text/* — a rule written for browser uploads, where a human picked the file. Here the
    // URL is chosen by the MODEL, so text/css, text/xml and text/javascript would all "ingest" as documents.
    // The rest of this feature is careful to allowlist (see WebDocumentLimits.IsAllowedFileNameChar); this is
    // the same discipline applied to content types.
    private static readonly string[] AllowedMediaTypes =
    [
        DocumentMediaTypes.Pdf, DocumentMediaTypes.Docx, DocumentMediaTypes.Pptx, DocumentMediaTypes.Xlsx,
        DocumentMediaTypes.PlainText, DocumentMediaTypes.Markdown,
    ];

    // The media type comes from the RESPONSE, with the URL's extension used only to rescue a generic
    // type (application/octet-stream, application/zip, none at all) — the same rule the browser-upload
    // path uses, and the same reason: servers routinely send a generic type for .docx/.pptx/.xlsx/.md.
    //
    // text/html gets its OWN message rather than falling into the generic rejection below, because it is
    // the common failure and not an edge case: a sign-in wall served for a .pdf URL. Telling the model to
    // use fetch_web_page instead is the actionable answer.
    private static string ResolveMediaType(Uri uri, HttpResponseMessage response)
    {
        var declared = response.Content.Headers.ContentType?.MediaType;
        var fileName = uri.Segments.Length > 0 ? uri.Segments[^1] : "";
        var resolved = DocumentMediaTypes.ResolveMediaType(fileName, declared);

        if (resolved.Equals("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{uri}' returned an HTML page, not a document. This tool reads PDF, Office and text " +
                "documents; use fetch_web_page for a web page.");
        }

        if (!AllowedMediaTypes.Contains(resolved, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{uri}' returned content type '{(resolved.Length == 0 ? "(none)" : resolved)}', which is " +
                "not a document this tool can read. Supported: PDF, Word, PowerPoint, Excel and text.");
        }

        return resolved;
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (HttpRequestException ex) when (Unwrap(ex) is { } blocked)
        {
            // SocketsHttpHandler wraps whatever ConnectCallback threw; surface the real reason so the
            // model is told "not fetchable", not "the site is down".
            throw blocked;
        }
    }

    private static BlockedAddressException? Unwrap(Exception exception)
    {
        for (var e = exception.InnerException; e is not null; e = e.InnerException)
        {
            if (e is BlockedAddressException blocked)
            {
                return blocked;
            }
        }

        return null;
    }

    // Transport guard: a hostile or merely huge document must never be pulled into memory in full. The
    // read STOPS at the cap and the fetch fails — deliberately not a silent truncation, because half a
    // PDF is not a shorter PDF, it is a corrupt one the extractor would either reject or mis-read.
    private static async Task<byte[]> ReadCappedAsync(
        HttpResponseMessage response, string url, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[81920];
        using var captured = new MemoryStream();
        int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (captured.Length + read > WebDocumentLimits.MaxDownloadBytes)
            {
                throw new InvalidOperationException(
                    $"'{url}' is larger than the {WebDocumentLimits.MaxDownloadBytes}-byte limit for a " +
                    "document, so it was not read. Try a smaller document.");
            }

            captured.Write(buffer, 0, read);
        }

        return captured.ToArray();
    }
}
