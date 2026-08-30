using System.Net.Http.Headers;

namespace Agent42.Backend;

/// <summary>
/// Translation rules for the SearXNG web-UI reverse proxy (ADR 0059), which serves the sidecar's own
/// HTML at <c>/searxng/*</c> on the app's single Easy-Auth-gated ingress. ADR 0011 kept the sidecar
/// <c>localhost</c>-only and therefore "unreachable from outside by construction"; this route retires
/// that half of the property, so everything the sidecar must NOT receive — and everything its
/// responses must NOT be able to do on our origin — is enforced here.
///
/// <para>Deliberately free of DI and of <c>HttpContext</c>: the rules are the security surface, so
/// they are unit-testable directly rather than only through a host fixture (<c>SearxngProxyTests</c>).
/// Split by direction because the outbound and inbound rule sets are independent — and because a
/// single method carrying both loops and all their conditionals is exactly the shape that trips
/// S3776 (ADR 0050).</para>
///
/// <para><b>Two allowlists, no denylists.</b> Requests forward only the headers and cookies named
/// here, and responses copy only the headers named here. A denylist of known-dangerous names (the
/// Easy Auth session cookie, <c>X-MS-CLIENT-PRINCIPAL-*</c>) was rejected as unauditable: it fails
/// open when the app later gains a cookie or header nobody thought to add to it.</para>
/// </summary>
public static class SearxngProxy
{
    /// <summary>Name of the DI-registered <see cref="HttpClient"/> the proxy dials the sidecar with.</summary>
    /// <remarks>
    /// Deliberately NOT <see cref="SearxngWebSearch.HttpClientName"/>. That client is shared with
    /// <c>web_search</c> and the config-health probe, and three of its primary-handler defaults break a
    /// proxy: it follows redirects itself (so the <c>Location</c> rewrite below would never run), and it
    /// pools a <c>CookieContainer</c> across consumers (one user's SearXNG preference cookie would
    /// replay onto another user's request and onto the tool's own queries). The third,
    /// <c>AutomaticDecompression</c>, is defence in depth rather than a live bug: the browser's
    /// <c>Accept-Encoding</c> is never forwarded (it is not in the request allowlist), so the sidecar has
    /// no reason to compress — but since <c>Content-Encoding</c> is deliberately not copied back, a
    /// compressed upstream response would be forwarded as undeclared encoded bytes.
    /// </remarks>
    public const string HttpClientName = "searxng-ui";

    /// <summary>The path the UI is served under. Also the value baked into <c>SEARXNG_BASE_URL</c>.</summary>
    public const string RoutePrefix = "/searxng";

    /// <summary>
    /// Namespace applied to every cookie crossing the proxy in either direction. SearXNG's own cookie
    /// names are generic — one preferences save emits <c>method</c>, <c>tokens</c>, <c>theme</c>,
    /// <c>language</c>, <c>categories</c> and 17 more, all at <c>Path=/</c> — so on the app's own
    /// origin they are live collision candidates rather than a theoretical one.
    /// </summary>
    public const string CookiePrefix = "sxng_";

    /// <summary>
    /// The sandbox half of the policy. Omitting <c>allow-same-origin</c> puts the document in an opaque
    /// origin, which stops every <b>read</b>: the sidecar's HTML and JS execute on our origin, where they
    /// would otherwise read the ADR 0032 IndexedDB conversation mirror and <c>fetch</c> any <c>/api/*</c>
    /// with the ambient Easy Auth cookie. Measured under this exact value: IndexedDB,
    /// <c>document.cookie</c> and <c>localStorage</c> all throw <c>SecurityError</c> and a same-origin
    /// <c>fetch</c> is blocked, while the page still renders styled, the search form still submits and
    /// preferences still persist (SearXNG reads those from HTTP cookies server-side, and HTTP cookies
    /// still round-trip).
    /// </summary>
    public const string SandboxCsp = "sandbox allow-scripts allow-forms";

    /// <summary>
    /// Builds the full policy for one response: the sandbox, plus a <c>form-action</c> pinned to this
    /// route. Both halves are required, and the second is not belt-and-braces.
    /// </summary>
    /// <remarks>
    /// <para><b>The sandbox stops reads, not cookie-bearing navigations.</b> Measured: a sandboxed
    /// opaque-origin page can still submit a form to a same-origin URL, and the browser <b>sends the
    /// session cookie with it</b>. That is not theoretical here —
    /// <c>POST /api/conversations/{id}/share</c> takes no body, so a form submission runs it verbatim and
    /// mints a public, no-login link to a conversation (ADR 0035). <c>form-action</c> is what actually
    /// closes that, and it is why <c>allow-forms</c> can be kept at all (SearXNG's search form is a POST).</para>
    /// <para><b><c>form-action 'self'</c> does NOT work here — measured.</b> <c>'self'</c> is matched
    /// against the origin of the URL, not the document's opaque origin, so it still permits every
    /// same-origin endpoint and blocks nothing. The source expression must carry the <b>path</b>, which
    /// means it must name the scheme and host, which means it is per-request. <c>Request.Scheme</c> is
    /// trustworthy behind the ACA ingress only because ADR 0042 put
    /// <c>UseForwardedHeaders(XForwardedProto)</c> first in the pipeline — without it this would emit
    /// <c>http://</c> and the policy would fail closed, blocking SearXNG's own form.</para>
    /// </remarks>
    public static string BuildCsp(string scheme, string? host)
        => string.IsNullOrEmpty(host)
            ? SandboxCsp
            : $"{SandboxCsp}; form-action {scheme}://{host}{RoutePrefix}/";

    /// <summary>
    /// Request headers forwarded upstream. Everything absent here is dropped, which is what keeps
    /// <c>X-MS-CLIENT-PRINCIPAL-*</c> (Easy Auth injects the user's id, email and name into every
    /// request — precisely the per-user attribution ADR 0011 says self-hosting buys the absence of),
    /// <c>Authorization</c>, <c>Cookie</c>, <c>Host</c>, <c>Referer</c> and <c>X-Forwarded-For</c> away
    /// from the sidecar without enumerating any of them.
    /// </summary>
    public static readonly IReadOnlyList<string> ForwardedRequestHeaders =
        ["Accept", "Accept-Language", "User-Agent"];

    /// <summary>
    /// Response headers copied back. <c>Content-Type</c> only: <c>Content-Encoding</c> in particular
    /// must not survive, because the handler decompresses and the bytes we emit are no longer encoded.
    /// <c>Location</c> and <c>Set-Cookie</c> are not copied — they are rewritten, below.
    /// </summary>
    public static readonly IReadOnlyList<string> ForwardedResponseHeaders = ["Content-Type"];

    /// <summary>
    /// Composes the upstream URI from the route's catch-all remainder by <b>explicit string
    /// concatenation onto the fixed base</b>, then re-checks the authority. Returns <c>null</c> when
    /// the result would leave the sidecar, which the caller turns into a 502.
    /// </summary>
    /// <remarks>
    /// The catch-all value arrives percent-DECODED, so <c>/searxng/%2f%2fevil.example/x</c> yields the
    /// remainder <c>//evil.example/x</c> — which <c>new Uri(base, remainder)</c> resolves as
    /// protocol-relative to <c>http://evil.example/x</c> and would dial from inside the container,
    /// reaching the backend's own port or (under AppHost) the Cosmos emulator. Collapsing the leading
    /// separators is what prevents that; the authority comparison is the belt to that braces. ADR
    /// 0011's SSRF section is this repo's scar tissue on exactly this shape.
    /// </remarks>
    public static Uri? BuildUpstreamUri(Uri baseAddress, string? remainder, string? queryString)
    {
        // Backslashes as well as slashes: several URL parsers treat the two interchangeably in the
        // authority position, so a leading "\\host" must collapse exactly like "//host".
        var path = "/" + (remainder ?? string.Empty).TrimStart('/', '\\');
        var composed = baseAddress.GetLeftPart(UriPartial.Authority) + path + queryString;

        if (!Uri.TryCreate(composed, UriKind.Absolute, out var uri))
        {
            return null;
        }

        return string.Equals(uri.Authority, baseAddress.Authority, StringComparison.OrdinalIgnoreCase)
            ? uri
            : null;
    }

    /// <summary>
    /// Builds the <c>Cookie</c> header sent upstream: only <see cref="CookiePrefix"/>-named cookies,
    /// with the prefix stripped back off so SearXNG sees the names it set. Returns <c>null</c> when
    /// there is nothing to send.
    /// </summary>
    /// <remarks>
    /// An allowlist rather than a denylist of auth cookie names. The browser attaches the Easy Auth
    /// session cookie (<c>AppServiceAuthSession</c>, path <c>/</c>) to every <c>/searxng/*</c> request;
    /// under an allowlist a cookie the app gains later cannot leak upstream by omission.
    /// </remarks>
    public static string? BuildUpstreamCookieHeader(IEnumerable<KeyValuePair<string, string>> cookies)
    {
        var forwarded = cookies
            .Where(c => c.Key.StartsWith(CookiePrefix, StringComparison.Ordinal))
            .Select(c => $"{c.Key[CookiePrefix.Length..]}={c.Value}")
            .ToArray();

        return forwarded.Length == 0 ? null : string.Join("; ", forwarded);
    }

    /// <summary>
    /// Namespaces one upstream <c>Set-Cookie</c>: prefixes the name and re-scopes it to
    /// <see cref="RoutePrefix"/>, dropping any upstream <c>Path</c> or <c>Domain</c> while keeping the
    /// attributes preference persistence depends on (<c>Expires</c>, <c>Max-Age</c>).
    /// </summary>
    public static string NamespaceSetCookie(string setCookie)
    {
        var parts = setCookie.Split(';');
        var attributes = parts.Skip(1)
            .Where(a => !IsAttribute(a, "Path") && !IsAttribute(a, "Domain"))
            .Select(a => a.Trim())
            .Where(a => a.Length > 0);

        var scoped = string.Join("; ", attributes.Prepend(CookiePrefix + parts[0].Trim()));
        return scoped + $"; Path={RoutePrefix}/";
    }

    private static bool IsAttribute(string attribute, string name)
        => attribute.TrimStart().StartsWith(name + "=", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rewrites a 3xx <c>Location</c> so it points back through the proxy. Returns <c>null</c> to
    /// reject — the caller turns that into a 502 rather than forwarding it.
    /// </summary>
    /// <remarks>
    /// <para>An absolute value is <b>expected</b> here, not exceptional: saving preferences answers 302
    /// with an absolute <c>Location</c> built from <c>SEARXNG_BASE_URL</c>. That is why this accepts an
    /// absolute URL whose authority is one we recognise — the sidecar we dialled, or the host the
    /// browser asked for — and reduces it to a root-relative path. Rejecting every absolute value (the
    /// simpler rule) would 502 the single most common redirect in the UI.</para>
    /// <para>Every other absolute value, and anything protocol-relative, is rejected: the app origin
    /// hosts two OAuth callbacks (<c>/api/mcp/oauth/callback</c>, <c>/api/google/oauth/callback</c>), so
    /// an open redirector here would not be benign. A genuinely relative value is passed through
    /// untouched: the browser resolves it against the current <c>/searxng/</c> URL, so it stays on our
    /// origin — though note it is <b>not</b> confined to the prefix, since <c>../../foo</c> resolves
    /// above it. SearXNG emits absolute values (measured), so this branch is a fallback, not a path in
    /// normal use.</para>
    /// </remarks>
    public static string? RewriteLocation(string? location, Uri upstreamBase, string? requestHost)
    {
        if (string.IsNullOrWhiteSpace(location) || location.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        if (location.StartsWith('/'))
        {
            return WithPrefix(location);
        }

        if (!Uri.TryCreate(location, UriKind.Absolute, out var absolute))
        {
            return location;
        }

        return IsTrusted(absolute, upstreamBase, requestHost) ? WithPrefix(absolute.PathAndQuery) : null;
    }

    // The authority is DISCARDED either way — what comes back is always a root-relative path under our
    // own prefix — so this decides whether the redirect is honoured or refused, not where it points.
    //
    // The third clause is not slack. SEARXNG_BASE_URL's authority is not always one the backend dials:
    // under AppHost the sidecar is its own container and Aspire resolves its endpoint to the
    // container-network name (`searxng.dev.internal:8888`), which the backend never sees, so the first
    // two clauses both miss and a preferences save would 502. A path already under OUR route prefix can
    // only have been generated by the sidecar from the base URL we gave it — and since the authority is
    // dropped, honouring it redirects to our own /searxng/… rather than anywhere else.
    private static bool IsTrusted(Uri location, Uri upstreamBase, string? requestHost)
        => string.Equals(location.Authority, upstreamBase.Authority, StringComparison.OrdinalIgnoreCase)
            || (!string.IsNullOrEmpty(requestHost)
                && string.Equals(location.Authority, requestHost, StringComparison.OrdinalIgnoreCase))
            || location.AbsolutePath.StartsWith(RoutePrefix + "/", StringComparison.Ordinal)
            || location.AbsolutePath == RoutePrefix;

    // SEARXNG_BASE_URL carries the subpath, so upstream-generated paths already start with it; prefixing
    // unconditionally would yield /searxng/searxng/. Matching on the trailing slash keeps a sibling path
    // like /searxngfoo from being mistaken for an already-prefixed one.
    private static string WithPrefix(string pathAndQuery)
        => pathAndQuery.StartsWith(RoutePrefix + "/", StringComparison.Ordinal) || pathAndQuery == RoutePrefix
            ? pathAndQuery
            : RoutePrefix + pathAndQuery;

    /// <summary>
    /// Copies the allowlisted request headers onto the upstream message. Content headers
    /// (<c>Content-Type</c>) belong to the body and are set by the caller on the content itself.
    /// </summary>
    public static void CopyRequestHeaders(IHeaderDictionary source, HttpRequestMessage destination)
    {
        foreach (var name in ForwardedRequestHeaders)
        {
            if (source.TryGetValue(name, out var value))
            {
                destination.Headers.TryAddWithoutValidation(name, (IEnumerable<string>)value!);
            }
        }
    }

    /// <summary>
    /// Applies the inbound rules to the outgoing response: the sandbox CSP on everything, the
    /// allowlisted headers, namespaced cookies, and the rewritten <c>Location</c>. Returns <c>false</c>
    /// when the redirect was rejected, which the caller answers with a 502.
    /// </summary>
    public static bool TryApplyResponseHeaders(
        HttpResponseMessage upstream,
        IHeaderDictionary destination,
        Uri upstreamBase,
        string? requestHost,
        string requestScheme = "https")
    {
        destination["Content-Security-Policy"] = BuildCsp(requestScheme, requestHost);

        foreach (var name in ForwardedResponseHeaders)
        {
            if (upstream.Content.Headers.TryGetValues(name, out var values))
            {
                destination[name] = values.ToArray();
            }
        }

        if (upstream.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            destination["Set-Cookie"] = cookies.Select(NamespaceSetCookie).ToArray();
        }

        return TryApplyLocation(upstream.Headers.Location, destination, upstreamBase, requestHost);
    }

    private static bool TryApplyLocation(
        Uri? location, IHeaderDictionary destination, Uri upstreamBase, string? requestHost)
    {
        if (location is null)
        {
            return true;
        }

        // OriginalString, not ToString(): the header may be relative, and ToString() on a relative Uri
        // would not tell us so.
        var rewritten = RewriteLocation(location.OriginalString, upstreamBase, requestHost);
        if (rewritten is null)
        {
            return false;
        }

        destination["Location"] = rewritten;
        return true;
    }

    /// <summary>
    /// Wraps the inbound body as upstream content, carrying only <c>Content-Type</c> across. The caller
    /// decides whether a body applies at all; this is not called for methods without one.
    /// </summary>
    public static HttpContent BuildUpstreamContent(Stream body, string? contentType)
    {
        var content = new StreamContent(body);
        if (!string.IsNullOrEmpty(contentType) && MediaTypeHeaderValue.TryParse(contentType, out var parsed))
        {
            content.Headers.ContentType = parsed;
        }

        return content;
    }
}
