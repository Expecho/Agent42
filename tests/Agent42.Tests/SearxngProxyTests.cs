using System.Net;
using System.Net.Http.Headers;
using Agent42.Backend;
using Microsoft.AspNetCore.Http;

namespace Agent42.Tests;

/// <summary>
/// The SearXNG web-UI proxy (ADR 0059). Most of these exercise <see cref="SearxngProxy"/> <b>directly</b>
/// rather than through the host: the translation rules are the security surface — what must never reach
/// the sidecar, and what its responses must never be able to do on our origin — so they are asserted as
/// rules. The host-level cases are the two that only exist end to end: that a request actually reaches
/// the sidecar, and that the route degrades to a curated 503 where no <c>IHttpClientFactory</c> is
/// registered.
/// </summary>
public sealed class SearxngProxyTests(SearxngProxyFixture fixture)
    : SharedClusterTests<SearxngProxyFixture>(fixture)
{
    private static readonly Uri UpstreamBase = new(SearxngProxyFixture.UpstreamBase);

    // ————— Outbound: what must not reach the sidecar —————

    /// <summary>
    /// AC 2. The browser attaches the Easy Auth session cookie (path <c>/</c>) to every <c>/searxng/*</c>
    /// request. Forwarding it would hand the sidecar the app's own session credential.
    /// </summary>
    [Fact]
    public void Proxy_DoesNotForwardTheEasyAuthCookie()
    {
        var header = SearxngProxy.BuildUpstreamCookieHeader(
        [
            new("AppServiceAuthSession", "session-secret"),
            new("AppServiceAuthSessionSuffix", "more-secret"),
        ]);

        Assert.Null(header);
    }

    /// <summary>AC 3. Only the proxy's own namespaced cookies cross, and the prefix comes back off.</summary>
    [Fact]
    public void Proxy_ForwardsOnlyItsOwnPrefixedCookies()
    {
        var header = SearxngProxy.BuildUpstreamCookieHeader(
        [
            new("AppServiceAuthSession", "session-secret"),
            new("sxng_theme", "simple"),
            new("sxng_language", "en"),
        ]);

        Assert.Equal("theme=simple; language=en", header);
        Assert.DoesNotContain("session-secret", header);
    }

    /// <summary>
    /// AC 4. An allowlist, so the headers that must never cross are dropped without being enumerated.
    /// <c>X-MS-CLIENT-PRINCIPAL-*</c> is the one that matters most: Easy Auth injects the user's id, email
    /// and name into every request, and forwarding them gives the sidecar exactly the per-user attribution
    /// ADR 0011 says self-hosting buys the absence of.
    /// </summary>
    [Fact]
    public void Proxy_ForwardsOnlyAllowlistedRequestHeaders()
    {
        var source = new HeaderDictionary
        {
            ["Accept"] = "text/html",
            ["Accept-Language"] = "en-GB",
            ["User-Agent"] = "Mozilla/5.0",
            ["X-MS-CLIENT-PRINCIPAL-NAME"] = "someone@example.com",
            ["X-MS-CLIENT-PRINCIPAL-ID"] = "principal-id",
            ["Authorization"] = "Bearer token",
            ["Referer"] = "https://chat.agent42.nl/",
            ["X-Forwarded-For"] = "203.0.113.7",
            ["Cookie"] = "AppServiceAuthSession=session-secret",
        };
        using var request = new HttpRequestMessage(HttpMethod.Get, UpstreamBase);

        SearxngProxy.CopyRequestHeaders(source, request);

        Assert.Equal(["Accept", "Accept-Language", "User-Agent"], request.Headers.Select(h => h.Key).Order());
    }

    /// <summary>
    /// AC 10. The catch-all value arrives percent-DECODED, so <c>%2f%2fevil.example</c> becomes
    /// <c>//evil.example</c> — a protocol-relative reference that would otherwise resolve off the sidecar
    /// and be dialled from inside the container.
    /// </summary>
    [Theory]
    [InlineData("//evil.example/x")]
    [InlineData("///evil.example/x")]
    [InlineData("\\\\evil.example/x")]
    public void Proxy_CannotBeSteeredOffTheSidecarByAProtocolRelativePath(string remainder)
    {
        var uri = SearxngProxy.BuildUpstreamUri(UpstreamBase, remainder, queryString: null);

        Assert.NotNull(uri);
        Assert.Equal(UpstreamBase.Authority, uri.Authority);
        Assert.DoesNotContain("evil.example", uri.Authority);
    }

    /// <summary>The ordinary case: the remainder and query survive onto the sidecar's own authority.</summary>
    [Fact]
    public void Proxy_ComposesTheUpstreamUriFromTheRemainderAndQuery()
    {
        var uri = SearxngProxy.BuildUpstreamUri(UpstreamBase, "search", "?q=hello&format=json");

        Assert.NotNull(uri);
        Assert.Equal(SearxngProxyFixture.UpstreamBase + "search?q=hello&format=json", uri.AbsoluteUri);
    }

    // ————— Inbound: what the response must not be able to do —————

    /// <summary>
    /// AC 9. The sandbox is the whole same-origin defence, so it goes on <b>every</b> response, not just
    /// HTML — a redirect and a stylesheet carry it too.
    /// </summary>
    [Fact]
    public void Proxy_SandboxesEveryResponse()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        var headers = new HeaderDictionary();

        Assert.True(SearxngProxy.TryApplyResponseHeaders(upstream, headers, UpstreamBase, "chat.agent42.nl"));

        var csp = headers["Content-Security-Policy"].ToString();
        Assert.StartsWith("sandbox allow-scripts allow-forms", csp);
        // Omitting allow-same-origin is what makes the origin opaque; a value carrying it would silently
        // hand the sidecar's scripts the app's IndexedDB and its /api surface.
        Assert.DoesNotContain("allow-same-origin", csp);
    }

    /// <summary>
    /// The sandbox stops READS, not cookie-bearing navigations. Measured: a sandboxed opaque-origin page
    /// can still submit a form to a same-origin URL and the browser sends the session cookie with it —
    /// and `POST /api/conversations/{id}/share` takes no body, so a form submission mints a public
    /// no-login share link (ADR 0035). `form-action` is what actually closes that.
    ///
    /// It must carry the PATH, which is why it names scheme and host and is built per request:
    /// `form-action 'self'` was measured NOT to work, because 'self' matches the URL's origin rather than
    /// the document's opaque one, so it still permits every same-origin endpoint.
    /// </summary>
    [Fact]
    public void Proxy_ConfinesFormSubmissionsToItsOwnPrefix()
    {
        var csp = SearxngProxy.BuildCsp("https", "chat.agent42.nl");

        Assert.Contains("form-action https://chat.agent42.nl/searxng/", csp);
        Assert.DoesNotContain("'self'", csp);
    }

    /// <summary>
    /// The scheme comes from the request, so it is `https` in production only because ADR 0042 put
    /// `UseForwardedHeaders(XForwardedProto)` first — behind the ACA ingress `Request.Scheme` is
    /// otherwise `http`, and the policy would fail closed and block SearXNG's own search form.
    /// </summary>
    [Fact]
    public void Proxy_BuildsTheFormActionFromTheRequestScheme()
    {
        Assert.Contains("form-action http://localhost:5000/searxng/", SearxngProxy.BuildCsp("http", "localhost:5000"));
        // No host to pin means no source expression can be written; the sandbox still ships.
        Assert.Equal(SearxngProxy.SandboxCsp, SearxngProxy.BuildCsp("https", null));
    }

    /// <summary>
    /// Content-Encoding must not survive: the handler decompresses, so forwarding it would label plain
    /// bytes as gzip. Only the allowlisted response headers cross.
    /// </summary>
    [Fact]
    public void Proxy_DoesNotForwardContentEncoding()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("x") };
        upstream.Content.Headers.ContentEncoding.Add("gzip");
        var headers = new HeaderDictionary();

        SearxngProxy.TryApplyResponseHeaders(upstream, headers, UpstreamBase, "chat.agent42.nl");

        Assert.False(headers.ContainsKey("Content-Encoding"));
    }

    /// <summary>
    /// AC 5. SearXNG's cookie names are generic — one preferences save emits <c>theme</c>, <c>method</c>,
    /// <c>tokens</c>, <c>language</c> and 18 more, all at <c>Path=/</c>. On the app's own origin those are
    /// live collision candidates, so the name is namespaced and the scope narrowed to the prefix, while the
    /// attributes persistence depends on are kept.
    /// </summary>
    [Fact]
    public void Proxy_NamespacesAndScopesUpstreamCookies()
    {
        var namespaced = SearxngProxy.NamespaceSetCookie("theme=simple; Max-Age=157680000; Path=/");

        Assert.StartsWith("sxng_theme=simple;", namespaced);
        Assert.Contains("Max-Age=157680000", namespaced);
        Assert.EndsWith("Path=/searxng/", namespaced);
        Assert.DoesNotContain("Path=/;", namespaced);
    }

    /// <summary>A <c>Domain</c> the sidecar sets would widen the scope back out, so it is dropped too.</summary>
    [Fact]
    public void Proxy_DropsAnUpstreamCookieDomain()
    {
        var namespaced = SearxngProxy.NamespaceSetCookie("theme=simple; Domain=.agent42.nl; Path=/");

        Assert.DoesNotContain("Domain", namespaced, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// AC 6, the accept half. An absolute Location is the <b>expected</b> case, not an exotic one: saving
    /// preferences answers 302 with an absolute URL built from <c>SEARXNG_BASE_URL</c>. Rejecting every
    /// absolute value would 502 the most common redirect in the UI.
    /// </summary>
    [Theory]
    // On the authority the proxy dialled — production with the base URL left at localhost, and AppHost.
    [InlineData("http://searxng.stub:8888/searxng/", "/searxng/")]
    // On the host the browser asked for — production with the base URL naming the custom domain.
    [InlineData("https://chat.agent42.nl/searxng/preferences", "/searxng/preferences")]
    // Root-absolute, already prefixed: must not become /searxng/searxng/.
    [InlineData("/searxng/preferences", "/searxng/preferences")]
    // Root-absolute without the prefix picks it up.
    [InlineData("/preferences", "/searxng/preferences")]
    // A sibling path is not an already-prefixed one.
    [InlineData("/searxngfoo", "/searxng/searxngfoo")]
    // An authority the backend never dials: under AppHost, Aspire resolves the sidecar's own endpoint to
    // the container-network name, so SEARXNG_BASE_URL carries THAT and neither the dial authority nor the
    // request host matches. Honoured because the path is already under our prefix - and the authority is
    // discarded regardless, so the redirect still lands on our origin. Measured in AppHost, not guessed.
    [InlineData("http://searxng.dev.internal:8888/searxng/", "/searxng/")]
    public void Proxy_RewritesRedirectLocation(string location, string expected)
    {
        var rewritten = SearxngProxy.RewriteLocation(location, UpstreamBase, "chat.agent42.nl");

        Assert.Equal(expected, rewritten);
    }

    /// <summary>
    /// AC 6, the reject half. The app origin hosts two OAuth callbacks
    /// (<c>/api/mcp/oauth/callback</c>, <c>/api/google/oauth/callback</c>), so an open redirector here
    /// would not be benign.
    /// </summary>
    [Theory]
    [InlineData("https://evil.example/steal")]
    [InlineData("//evil.example/steal")]
    [InlineData("http://searxng.stub.evil.example/x")]
    // An unrecognised authority is refused when its path is NOT under our prefix - which is every value
    // an attacker would actually want, since the prefix is ours to hand out.
    [InlineData("https://evil.example/api/mcp/oauth/callback")]
    public void Proxy_RejectsOffSiteRedirects(string location)
    {
        Assert.Null(SearxngProxy.RewriteLocation(location, UpstreamBase, "chat.agent42.nl"));
    }

    /// <summary>
    /// A rejected redirect must not be forwarded even partially — the caller turns the <c>false</c> into a
    /// 502, and no <c>Location</c> is left behind.
    /// </summary>
    [Fact]
    public void Proxy_ReportsARejectedRedirectToTheCaller()
    {
        using var upstream = new HttpResponseMessage(HttpStatusCode.Found) { Content = new StringContent("") };
        upstream.Headers.Location = new Uri("https://evil.example/steal");
        var headers = new HeaderDictionary();

        Assert.False(SearxngProxy.TryApplyResponseHeaders(upstream, headers, UpstreamBase, "chat.agent42.nl"));
    }

    // ————— Host level —————

    /// <summary>AC 1. The request reaches the sidecar and the body, status and content type come back.</summary>
    [Fact]
    public async Task Proxy_ForwardsThePathAndReturnsTheUpstreamResponse()
    {
        using var response = await Fixture.Client.GetAsync("/searxng/search?q=hello");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("upstream saw /search?q=hello", await response.Content.ReadAsStringAsync());
        var csp = response.Headers.GetValues("Content-Security-Policy").Single();
        Assert.StartsWith("sandbox allow-scripts allow-forms", csp);
        Assert.Contains("form-action", csp);
    }

    /// <summary>
    /// AC 2 and AC 4, END TO END. The helper tests above prove the rules in isolation; this proves the
    /// COMPOSITION in `BuildUpstreamRequest`, which calls the header allowlist and then adds a `Cookie`
    /// header of its own. Without this, a future edit that also copied the raw `Cookie` through, or added
    /// `X-Forwarded-For` to the allowlist, would pass every other test in this file.
    /// </summary>
    [Fact]
    public async Task Proxy_SendsNeitherTheSessionCookieNorTheIdentityHeaders_EndToEnd()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/searxng/search?q=hello");
        request.Headers.TryAddWithoutValidation("Cookie", "AppServiceAuthSession=SECRET-SESSION; sxng_theme=simple");
        request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-NAME", "someone@example.com");
        request.Headers.TryAddWithoutValidation("X-MS-CLIENT-PRINCIPAL-ID", "principal-id");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer app-token");
        request.Headers.TryAddWithoutValidation("Referer", "https://chat.agent42.nl/");
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", "203.0.113.7");

        using var response = await Fixture.Client.SendAsync(request);
        response.EnsureSuccessStatusCode();

        var upstream = Fixture.Received[^1];
        var names = upstream.Headers.Select(h => h.Key).ToArray();

        Assert.DoesNotContain(names, n => n.StartsWith("X-MS-", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Authorization", names);
        Assert.DoesNotContain("Referer", names);
        Assert.DoesNotContain("X-Forwarded-For", names);

        // The one cookie that may cross is the namespaced one, with its prefix stripped.
        var cookie = upstream.Headers.TryGetValues("Cookie", out var values) ? string.Join("; ", values) : "";
        Assert.Equal("theme=simple", cookie);
        Assert.DoesNotContain("SECRET-SESSION", cookie);
    }

    /// <summary>
    /// AC 7. A proxy that dropped bodies would break the UI: saving preferences is a POST on every upstream
    /// version. (The <i>search</i> form was also a POST on the image ADR 0059 measured; it is a GET since
    /// ADR 0060's bump. That is why this asserts the body path, not a particular form's verb.)
    /// </summary>
    [Fact]
    public async Task Proxy_ForwardsPostBodies()
    {
        using var content = new StringContent("q=hello&language=en");
        content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var response = await Fixture.Client.PostAsync("/searxng/search", content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("q=hello&language=en", Fixture.ReceivedBodies);
    }

    /// <summary>AC 8. A catch-all does not match a missing segment, so the bare prefix needs its own route.</summary>
    [Fact]
    public async Task Proxy_RedirectsTheBarePrefix()
    {
        using var client = Fixture.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/searxng");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/searxng/", response.Headers.Location?.OriginalString);
    }

    /// <summary>
    /// The canonical URL — every link on the proxied page points at <c>/searxng/</c> — must be SERVED, not
    /// redirected. Endpoint routing matches a trailing slash against the literal <c>/searxng</c> template
    /// and the literal out-ranks the catch-all, so a bare redirect there is an infinite loop on the one
    /// URL that matters most. Found by this test, not by review.
    /// </summary>
    [Fact]
    public async Task Proxy_ServesTheCanonicalSlashUrlWithoutLooping()
    {
        using var client = Fixture.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/searxng/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("upstream saw /", await response.Content.ReadAsStringAsync());
    }

    /// <summary>End to end: an upstream redirect on a trusted authority comes back root-relative.</summary>
    [Fact]
    public async Task Proxy_RewritesAnUpstreamRedirectEndToEnd()
    {
        using var client = Fixture.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/searxng/redirect-home");

        Assert.Equal(HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/searxng/", response.Headers.Location?.OriginalString);
    }

    /// <summary>End to end: an off-site redirect becomes a 502 rather than being handed to the browser.</summary>
    [Fact]
    public async Task Proxy_AnswersOffSiteRedirectsWithBadGatewayEndToEnd()
    {
        using var client = Fixture.CreateNonRedirectingClient();

        using var response = await client.GetAsync("/searxng/redirect-offsite");

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.False(response.Headers.Contains("Location"));
    }

    /// <summary>End to end: the upstream cookie is namespaced and re-scoped on the way back.</summary>
    [Fact]
    public async Task Proxy_NamespacesUpstreamCookiesEndToEnd()
    {
        using var response = await Fixture.Client.GetAsync("/searxng/set-prefs");

        var setCookie = response.Headers.GetValues("Set-Cookie").Single();
        Assert.StartsWith("sxng_theme=simple;", setCookie);
        Assert.EndsWith("Path=/searxng/", setCookie);
    }
}

/// <summary>
/// AC 11. Its own fixture — the bare <see cref="SharedApiFixture"/>, which registers no
/// <c>IHttpClientFactory</c> — because installing the stub is what registers the factory, so this cannot
/// share the host with the tests above. That absence is DevHost's situation exactly.
/// </summary>
public sealed class SearxngProxyUnconfiguredTests(SharedApiFixture fixture)
    : SharedClusterTests<SharedApiFixture>(fixture)
{
    [Fact]
    public async Task Proxy_ReturnsServiceUnavailable_WhenNoHttpClientFactory()
    {
        using var response = await Fixture.Client.GetAsync("/searxng/");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("not available", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// The other half of the same trap: a minimal-API parameter the host cannot resolve is treated as a
    /// request body and breaks routing for the WHOLE host. Injecting <c>IServiceProvider</c> is what keeps
    /// the unrelated endpoints working here.
    /// </summary>
    [Fact]
    public async Task Proxy_DoesNotBreakRoutingForOtherEndpoints()
    {
        using var response = await Fixture.Client.GetAsync("/api/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}
