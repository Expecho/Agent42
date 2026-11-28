using Agent42.Agents;

namespace Agent42.Backend;

/// <summary>
/// Registers the web tools (ADR 0011, 0059, 0066, 0155): <c>web_search</c> over the SearXNG sidecar (behind an optional
/// keyed-provider fallback chain), the SearXNG web UI proxy, and the guarded <c>read_web_document</c> fetcher.
///
/// <para>Lifted out of <c>Program.cs</c> (cc-06), the <see cref="HindsightRegistration"/> shape. The wiring below is
/// pinned by <c>WebRegistrationTests</c>, which resolves the real handler chain rather than reading this file.</para>
/// </summary>
public static class WebRegistration
{
    /// <summary>
    /// Search runs against the SearXNG sidecar in this Container App, reached over localhost. Eager-validated so a
    /// missing <c>WebSearch:Endpoint</c> fails at startup rather than in the middle of a chat.
    /// </summary>
    public static void AddWebTools(this IServiceCollection services, IConfiguration configuration)
    {
        var webSearchEndpoint = configuration["WebSearch:Endpoint"];
        if (string.IsNullOrWhiteSpace(webSearchEndpoint))
        {
            throw new InvalidOperationException(
                "WebSearch:Endpoint is not configured. Set it in appsettings.json, user secrets, or the " +
                "WebSearch__Endpoint environment variable (the SearXNG sidecar, e.g. http://localhost:8888).");
        }

        AddHttpClients(services, new Uri(webSearchEndpoint));

        // web_search (ADR 0011, 0155): the bare SearXNG sidecar, or a fallback chain in front of it when an operator
        // key for Tavily or Firecrawl is set.
        services.AddWebSearchChain(configuration);

        // read_web_document (ADR 0066): the guarded fetcher over the client registered above. A mandatory AgentGrain
        // dependency (AddAgent42Silo checks it); its ingestion port is host-invariant and comes from AddAgent42Silo.
        services.AddSingleton<IWebDocumentFetcher>(sp => new GuardedDocumentFetcher(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(GuardedDocumentFetcher.HttpClientName)));
    }

    // Explicit timeouts on the web clients, and NO retry pipeline.
    //
    // AddServiceDefaults() applies AddStandardResilienceHandler() to EVERY HttpClient via
    // ConfigureHttpClientDefaults, so these named clients would silently inherit 3 retries plus the
    // handler's own generous per-attempt/total timeouts. Those are wrong here, and RemoveAllResilienceHandlers
    // is what actually opts out (setting HttpClient.Timeout alone does not — the pipeline still wraps it):
    //   * AgentGrain is NON-REENTRANT, so a slow search→read chain serializes the conversation's turn and
    //     delays the user's first token — and a retry multiplies that stall by four.
    // A web lookup that hasn't answered within these timeouts is not worth the stall; the tools degrade by
    // telling the model the lookup failed.
    // EXTEXP0001: RemoveAllResilienceHandlers is still marked experimental, but it is the only supported way
    // to opt a named client out of the pipeline ConfigureHttpClientDefaults applied to it. Suppressed
    // deliberately and narrowly: the alternative — inheriting retries that stall a non-reentrant grain — is a
    // latency regression, not a style preference. If this API is ever removed, the fix is to stop applying
    // the standard handler by default in ServiceDefaults, not to accept the retries.
    private static void AddHttpClients(IServiceCollection services, Uri searxng)
    {
#pragma warning disable EXTEXP0001
        services.AddHttpClient(SearxngWebSearch.HttpClientName, http =>
        {
            http.BaseAddress = searxng;
            http.Timeout = TimeSpan.FromSeconds(10);
        })
            .RemoveAllResilienceHandlers();

        // read_web_document over an in-container fetch (ADR 0066). THIS ONE IS AN SSRF SINK, and the
        // ConfigurePrimaryHttpMessageHandler below is the entire control: our container — which holds the managed
        // identity Azure IMDS mints Cosmos/Storage/OpenAI tokens for — dials a URL the MODEL chose, and (because
        // /api/agent-run is on excludedPaths, ADR 0022) a URL an API key holder can put in front of it with no
        // browser session. PrivateAddressGuard runs at CONNECT time inside GuardedDocumentFetcher.CreateHandler, so
        // it sees the address actually dialled and covers every redirect hop; a guard applied to the URL string
        // instead would be TOCTOU-broken and blind to redirects. A guard that is built, tested and never wired here
        // is the highest-consequence way this feature can ship green, which is why WebRegistrationTests resolves
        // the handler chain and asserts the guard is on the primary handler.
        //
        // Explicit timeout + RemoveAllResilienceHandlers for the same reason as the clients above; note the
        // timeout is NOT the whole story here, because the fetcher reads with ResponseHeadersRead — see
        // WebDocumentLimits.DownloadDeadline, which bounds the body read the timeout stops covering.
        services.AddHttpClient(GuardedDocumentFetcher.HttpClientName, http =>
        {
            http.Timeout = TimeSpan.FromSeconds(30);
        })
            .ConfigurePrimaryHttpMessageHandler(
                () => GuardedDocumentFetcher.CreateHandler(PrivateAddressGuard.IsPubliclyRoutable))
            .RemoveAllResilienceHandlers();

        // The SearXNG web UI proxy (ADR 0059). A SECOND client against the same sidecar, deliberately: three
        // primary-handler defaults that suit web_search break a proxy, and all three are shared with
        // SearxngWebSearch and ConfigHealth's probe, so they cannot simply be changed on the existing client.
        //   * AllowAutoRedirect — the client would follow the 3xx itself, so the endpoint would never see a
        //     Location to rewrite; the redirect rules would be dead in production while green against a stub.
        //   * UseCookies — the pooled CookieContainer is shared across every consumer of the named client, so
        //     one user's SearXNG preference cookie would replay onto another user's request AND onto
        //     web_search's own queries, silently changing search behaviour.
        //   * AutomaticDecompression — without it the proxy forwards the browser's Accept-Encoding and emits
        //     gzip bytes labelled text/html.
        // RemoveAllResilienceHandlers for the same reason as the two clients above: a retried proxy request is
        // a stalled page, and the grain-latency argument applies to a user watching a browser tab too.
        services.AddHttpClient(SearxngProxy.HttpClientName, http =>
        {
            http.BaseAddress = searxng;
            http.Timeout = TimeSpan.FromSeconds(10);
        })
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                AutomaticDecompression = System.Net.DecompressionMethods.All,
            })
            .RemoveAllResilienceHandlers();
#pragma warning restore EXTEXP0001
    }
}
