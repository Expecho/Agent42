using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Agent42.Agents;
using AngleSharp;
using AngleSharp.Dom;

namespace Agent42.Backend;

/// <summary>
/// Thrown when the fetcher refused to connect to an address (SSRF guard). Distinct from a transport
/// failure: the model is told the URL is not fetchable, not that the site was down.
/// </summary>
public sealed class BlockedAddressException(string message) : Exception(message);

/// <summary>
/// <see cref="IWebPageFetcher"/> over a guarded <see cref="HttpClient"/> (ADR 0011): fetches one
/// model-chosen URL and reduces it to readable text.
///
/// Two independent budgets, deliberately different numbers — do not conflate them:
/// the <b>transport</b> cap (<see cref="MaxDownloadBytes"/>) bounds what we pull off the wire, and
/// the <b>return</b> cap (<see cref="WebToolLimits.MaxPageTextLength"/>) bounds what we hand back to
/// the model, which is the value that gets persisted into the 2 MB session every turn (ADR 0004).
/// </summary>
public sealed partial class WebPageFetcher(HttpClient httpClient) : IWebPageFetcher
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> this service resolves.</summary>
    public const string HttpClientName = "webpagefetch";

    /// <summary>Transport guard: stop reading a response body past this. Not the return budget.</summary>
    public const int MaxDownloadBytes = 1024 * 1024;

    private static readonly string[] AllowedMediaTypes = ["text/html", "text/plain"];

    /// <summary>
    /// Builds the primary handler for the fetch client, with the SSRF guard installed at
    /// <b>connect</b> time.
    ///
    /// The callback resolves the hostname itself, keeps only the addresses
    /// <paramref name="isAllowed"/> accepts, and then dials <b>those addresses</b> — never the
    /// hostname. That is what makes the check TOCTOU-free: the socket cannot re-resolve and land
    /// somewhere else (Context 3 of the plan / ADR 0011). Because every redirect hop opens its own
    /// connection through this same callback, redirects are covered without extra code.
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

    public async Task<WebPageContent> FetchAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new BlockedAddressException(
                $"'{url}' is not an absolute http(s) URL. Only http and https pages can be fetched.");
        }

        using var response = await Send(uri, cancellationToken);
        response.EnsureSuccessStatusCode();

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null || !AllowedMediaTypes.Contains(mediaType, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"'{url}' returned content type '{mediaType ?? "(none)"}', which is not a readable web page. " +
                "Only text/html and text/plain can be fetched.");
        }

        var body = await ReadCappedAsync(response, cancellationToken);

        var (title, text) = mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)
            ? await ExtractFromHtmlAsync(body, cancellationToken)
            : (uri.Host, Normalize(body));

        var truncated = text.Length > WebToolLimits.MaxPageTextLength;
        if (truncated)
        {
            text = text[..WebToolLimits.MaxPageTextLength] + WebToolLimits.TruncationMarker;
        }

        return new WebPageContent(url, title, text, truncated);
    }

    private async Task<HttpResponseMessage> Send(Uri uri, CancellationToken cancellationToken)
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

    // Transport guard: a hostile or merely huge page must not be pulled into memory in full. This is
    // independent of the return cap — see the class remarks.
    private static async Task<string> ReadCappedAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        var buffer = new byte[8192];
        using var captured = new MemoryStream();
        int read;
        while (captured.Length < MaxDownloadBytes &&
               (read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            var take = (int)Math.Min(read, MaxDownloadBytes - captured.Length);
            captured.Write(buffer, 0, take);
        }

        return Encoding.UTF8.GetString(captured.ToArray());
    }

    private static async Task<(string Title, string Text)> ExtractFromHtmlAsync(
        string html, CancellationToken cancellationToken)
    {
        var browsing = BrowsingContext.New(Configuration.Default);
        using var document = await browsing.OpenAsync(req => req.Content(html), cancellationToken);

        // Chrome, navigation and boilerplate are noise the model would otherwise pay context for.
        foreach (var node in document.QuerySelectorAll("script, style, noscript, template, svg, nav, header, footer, aside"))
        {
            node.Remove();
        }

        var title = string.IsNullOrWhiteSpace(document.Title) ? string.Empty : document.Title.Trim();
        var text = Normalize(document.Body?.TextContent ?? string.Empty);

        return (title, text);
    }

    // Collapse the whitespace an HTML-to-text pass leaves behind: it is pure token cost.
    private static string Normalize(string text) =>
        BlankLines().Replace(HorizontalWhitespace().Replace(text, " "), "\n\n").Trim();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex HorizontalWhitespace();

    [GeneratedRegex(@"(\s*\n\s*){2,}")]
    private static partial Regex BlankLines();
}
