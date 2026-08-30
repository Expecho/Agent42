namespace Agent42.Backend;

/// <summary>
/// The SearXNG web-UI reverse proxy (ADR 0059): serves the sidecar's own HTML search UI at
/// <c>/searxng/*</c> so a signed-in user can see what the instance actually returns and which engines
/// answered, instead of the sidecar being a black box behind <c>web_search</c>.
///
/// <para>These are the <b>first non-<c>/api</c> routes</b> in <c>MapAgentApi</c> — <c>/logs</c> and
/// <c>/shared</c> are static directories, not routes — which is why the service worker's early-return
/// guard had to grow a second path prefix beside <c>/api</c>.</para>
///
/// <para>The route is Easy-Auth gated like every other browser surface: it is deliberately <b>not</b>
/// in <c>excludedPaths</c>, and <c>EasyAuthExclusionTests</c> asserts that. All of the translation
/// rules live in <see cref="SearxngProxy"/>, which has no DI and no <c>HttpContext</c> so they can be
/// tested as rules rather than only through a host.</para>
/// </summary>
public static partial class AgentApi
{
    private static void MapWebEndpoints(IEndpointRouteBuilder app)
    {
        // A catch-all does not match a missing segment, so the bare prefix needs its own route. Handled
        // here rather than upstream on purpose: SearXNG answers it with a 308 to an ABSOLUTE URL built
        // from SEARXNG_BASE_URL, and serving the redirect ourselves keeps that off the rewrite path.
        //
        // The trailing-slash test is load-bearing, not defensive. Endpoint routing matches a trailing
        // slash against a literal template, and the literal OUT-RANKS the catch-all — so `/searxng/`,
        // the canonical URL every link on the page points at, arrives HERE rather than at the proxy
        // route below. Redirecting it unconditionally is an infinite redirect loop on the one URL that
        // matters most, which is what Proxy_ServesTheCanonicalSlashUrlWithoutLooping pins.
        app.MapGet(SearxngProxy.RoutePrefix, (IServiceProvider services, HttpContext http, CancellationToken ct) =>
            http.Request.Path.Value!.EndsWith('/')
                ? ProxyToSearxngAsync(path: null, services, http, ct)
                : Task.FromResult(Results.Redirect(SearxngProxy.RoutePrefix + "/")));

        // GET and POST only: preferences saves are POSTs, and the search form has been both (it was a POST
        // on the image ADR 0059 measured, a GET since ADR 0060's bump — do not narrow this to one verb)
        // are saved with one, but nothing in the UI needs another verb, and a proxy should forward the
        // smallest set that works.
        // IServiceProvider (NOT IHttpClientFactory) is injected deliberately, mirroring /api/speech and
        // /api/config-health: MapAgentApi infers every endpoint's parameters up front, and a parameter a
        // host cannot resolve is treated as a request BODY, which breaks routing for the WHOLE host.
        // DevHost registers no IHttpClientFactory at all, so resolving it optionally is also what makes
        // the curated 503 fall out for free rather than needing a second code path.
        // DisableAntiforgery mirrors the upload and /api/speech endpoints. It is a no-op today — nothing
        // calls AddAntiforgery, and minimal-API validation only covers endpoints that BIND form data,
        // which this one does not — but it is kept so the POST stays exempt if antiforgery is ever added.
        app.MapMethods(SearxngProxy.RoutePrefix + "/{**path}", ["GET", "POST"], ProxyToSearxngAsync)
            .DisableAntiforgery();
    }

    private static async Task<IResult> ProxyToSearxngAsync(
        string? path, IServiceProvider services, HttpContext http, CancellationToken ct)
    {
        var client = services.GetService<IHttpClientFactory>()?.CreateClient(SearxngProxy.HttpClientName);
        if (client?.BaseAddress is null)
        {
            return Results.Text(
                "The search UI is not available in this environment.",
                "text/plain",
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }

        var upstreamUri = SearxngProxy.BuildUpstreamUri(client.BaseAddress, path, http.Request.QueryString.Value);
        if (upstreamUri is null)
        {
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        using var request = BuildUpstreamRequest(http, upstreamUri);

        // The whole point of this route is to diagnose a sidecar that is misbehaving, so the case where it
        // is unreachable or too slow is the one it must handle best. Neither host installs
        // UseExceptionHandler, so without this a dead sidecar is a bare unhandled 500 with no explanation.
        HttpResponseMessage upstream;
        try
        {
            upstream = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return Results.Text(
                "The search sidecar did not respond.",
                "text/plain",
                statusCode: StatusCodes.Status502BadGateway);
        }

        using var _ = upstream;

        // Nothing has been written yet, so a rejected redirect can still clear the headers already set
        // and answer 502 instead of forwarding an off-site Location.
        if (!SearxngProxy.TryApplyResponseHeaders(
                upstream, http.Response.Headers, client.BaseAddress, http.Request.Host.Value, http.Request.Scheme))
        {
            http.Response.Headers.Clear();
            return Results.StatusCode(StatusCodes.Status502BadGateway);
        }

        http.Response.StatusCode = (int)upstream.StatusCode;
        await upstream.Content.CopyToAsync(http.Response.Body, ct);
        return Results.Empty;
    }

    private static HttpRequestMessage BuildUpstreamRequest(HttpContext http, Uri upstreamUri)
    {
        var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), upstreamUri);
        SearxngProxy.CopyRequestHeaders(http.Request.Headers, request);

        var cookies = SearxngProxy.BuildUpstreamCookieHeader(http.Request.Cookies);
        if (cookies is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookies);
        }

        if (HttpMethods.IsPost(http.Request.Method))
        {
            request.Content = SearxngProxy.BuildUpstreamContent(http.Request.Body, http.Request.ContentType);
        }

        return request;
    }
}
