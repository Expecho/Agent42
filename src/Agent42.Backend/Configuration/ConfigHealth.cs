using Microsoft.Extensions.AI;

namespace Agent42.Backend;

/// <summary>
/// Builds the <c>GET /api/config-health</c> report (the Preferences → Configuration tab): which
/// required/optional configuration inputs are present, malformed, or (opt-in) unreachable, so a silently
/// degraded feature (geocoding off, Web Push off, context.dev fetch failing) is visible instead of
/// mysterious. Backs an operator diagnostic, not per-user state.
///
/// <para><b>The load-bearing invariant: the report never contains a secret value.</b> Every check exposes
/// a status enum + a <b>curated</b> message only — the same discipline as <c>AgentApi.Mask</c> (keys → a
/// <c>hasKey</c> flag) and <see cref="PushConfig"/> (<c>Enabled</c>, never the key). This matters most on
/// the <b>probe</b> path: the geocoding key rides in the request query string (<c>GoogleGeocoder</c>), so a
/// probe that catches an exception must <b>never</b> put <c>ex.Message</c>/<c>ex.ToString()</c> or the
/// target URL into a message — <see cref="ProbeAsync"/> wraps every probe in a catch that discards the
/// exception and emits a fixed curated string.</para>
///
/// <para><b>The framing that shapes every check:</b> the fatal config values are eager-validated at startup
/// (<c>Program.cs</c>), so <b>if the app booted, they are already present</b> — a user can never reach this
/// tab with a fatal value blank. The useful signal is therefore (1) optional/degrading features silently
/// off, (2) prod-infra values, and (3) present-but-wrong values, detectable only by a live probe.</para>
///
/// <para><see cref="BuildStatic"/> is a <b>pure function</b> of <see cref="IConfiguration"/> +
/// <see cref="IHostEnvironment"/> + <see cref="PushConfig"/> — no HTTP, no DI graph — so it is unit-testable
/// without a host and cannot drift into a slow call. Live probes are a separate <see cref="ProbeAsync"/>
/// path, opt-in because a geocoding/context.dev probe costs a billable call.</para>
/// </summary>
public static class ConfigHealth
{
    // Status values a check can carry (serialized as-is into ConfigCheck.Status). Not an enum so the wire
    // contract is an explicit lowercase string the frontend switches on for pill colour.
    public const string Ok = "ok";
    public const string Warning = "warning";
    public const string Error = "error";
    public const string Skipped = "skipped";
    public const string NotApplicable = "na";

    // Group labels — the tab renders one section per group, in this order.
    private const string GroupCore = "Core (required at startup)";
    private const string GroupWebSearch = "Web search sidecar";
    private const string GroupInfra = "Production infrastructure";
    private const string GroupOptional = "Optional features";

    // Keys of the checks a live probe overrides. Stable so ProbeAsync can find them by key.
    private const string ProbeSearxng = "probe:searxng";
    private const string ProbeGeocoding = "probe:geocoding";
    private const string ProbeContext = "probe:context.dev";

    /// <summary>
    /// The static report — a pure function of config + environment + push state. No network. Fatal values
    /// (which a booted app already has) are itemised so a malformed value still surfaces as an error, but
    /// the tab renders the Core group compactly; the high-value signal is the Optional group.
    /// </summary>
    public static ConfigHealthReport BuildStatic(IConfiguration config, IHostEnvironment env, PushConfig push)
    {
        var isDevelopment = env.IsDevelopment();
        var checks = new List<ConfigCheck>();

        // ————— Core (fatal — eager-validated at startup, so present on any booted app) —————
        // Embeddings are no longer operator config (the AzureOpenAI:ApiKey/EmbeddingDeploymentName floor was
        // removed — embeddings are now a per-user model, ADR 0007/0018). There is nothing operator-level to
        // health-check for RAG, and BuildStatic is pure (no grain I/O, ADR 0029) so it cannot see a user's
        // per-user embedding pick anyway — that visibility lives in the Model providers tab's embedding
        // selector, not here.
        checks.Add(UriCheck(GroupCore, "WebSearch:Endpoint", config["WebSearch:Endpoint"], requireHttps: false));

        // ————— Web search sidecar (things the backend genuinely cannot presence-check) —————
        checks.Add(new ConfigCheck(GroupWebSearch, "SEARXNG_SECRET", Skipped,
            "The backend never reads the sidecar's secret, so it cannot be presence-checked from app "
            + "config. A live probe of the SearXNG endpoint is the only signal.", Billable: false));
        checks.Add(NotProbed(GroupWebSearch, ProbeSearxng, "SearXNG reachability", billable: false));

        // ————— Production infrastructure (informational / n-a in Development) —————
        if (isDevelopment)
        {
            checks.Add(new ConfigCheck(GroupInfra, "Storage:BlobEndpoint", NotApplicable,
                "Not required offline — the AppHost/DevHost uses an in-memory attachment store.", Billable: false));
            checks.Add(new ConfigCheck(GroupInfra, "AzureOpenAI:TenantId", NotApplicable,
                "Not required offline.", Billable: false));
        }
        else
        {
            checks.Add(UriCheck(GroupInfra, "Storage:BlobEndpoint", config["Storage:BlobEndpoint"],
                requireHttps: true, missingStatus: Warning,
                missingMessage: "not set — blob attachments and the Data Protection key ring degrade"));
            checks.Add(PresenceCheck(GroupInfra, "AzureOpenAI:TenantId", config["AzureOpenAI:TenantId"],
                present: "configured", missing: "not set — the managed identity uses the ambient tenant",
                missingStatus: Warning));
        }

        checks.Add(new ConfigCheck(GroupInfra, "AAD_CLIENT_SECRET", Skipped,
            "Injected by Bicep into the Container App's auth sidecar; it is not visible to app config, "
            + "so it cannot be presence-checked here.", Billable: false));

        // ————— Optional / degrading features (the high-value items) —————
        checks.Add(PresenceCheck(GroupOptional, "Geocoding:GoogleApiKey", config["Geocoding:GoogleApiKey"],
            present: "reverse geocoding enabled",
            missing: "not set — get_current_location reports coordinates without a street address",
            missingStatus: Warning));
        checks.Add(NotProbed(GroupOptional, ProbeGeocoding, "Geocoding reachability", billable: true));

        checks.Add(PresenceCheck(GroupOptional, "ContextScrape:ApiKey", config["ContextScrape:ApiKey"],
            present: "page scraping enabled",
            missing: "not set — fetch_web_page will fail per call with 401",
            missingStatus: Warning));
        checks.Add(NotProbed(GroupOptional, ProbeContext, "context.dev reachability", billable: true));

        // Google Calendar (ADR 0053). Presence only, and deliberately NO live probe: every other probe
        // here is an operator-keyed call, but a calendar probe would need a USER's OAuth token and would
        // be meaningless with none connected. Two checks rather than one derived flag, because the two
        // halves fail separately and an operator who set only one needs to see which.
        checks.Add(PresenceCheck(GroupOptional, "GoogleCalendar:ClientId", config["GoogleCalendar:ClientId"],
            present: "configured",
            missing: "not set — the calendar tools report 'not connected' and Preferences offers no Connect",
            missingStatus: Warning));
        checks.Add(PresenceCheck(GroupOptional, "GoogleCalendar:ClientSecret", config["GoogleCalendar:ClientSecret"],
            present: "configured",
            missing: "not set — a Google Web application client is confidential, so the token exchange needs this too",
            missingStatus: Warning));

        // Web Push: derive from PushConfig.Enabled — the single source of the "push is on" fact (both VAPID
        // keys required). Never recompute from the raw keys.
        checks.Add(new ConfigCheck(GroupOptional, "Push:Vapid", push.Enabled ? Ok : Warning,
            push.Enabled
                ? "Web Push enabled."
                : "off — Web Push needs both a VAPID private key (secret) and public key.",
            Billable: false));

        return new ConfigHealthReport(env.EnvironmentName, checks);
    }

    /// <summary>
    /// Runs the opt-in live probes and merges their results onto <paramref name="staticReport"/>, replacing
    /// the matching <c>probe:*</c> checks. Each probe is independently try/caught and <b>never throws</b>; a
    /// probe whose client is unresolved stays <c>skipped</c> (its static value). The catch discards the
    /// exception — a probe message is always a fixed curated string, never <c>ex</c> or a target URL (the
    /// geocoding key rides in the query string), which is the security invariant on this path.
    ///
    /// <para>Probe delegates come from a <see cref="ConfigHealthProbeSet"/> resolved <b>optionally</b> from
    /// <paramref name="services"/> — tests inject a counting/fake set (also proving the billing-safe "static
    /// runs zero probes" invariant); production falls back to <see cref="ConfigHealthProbeSet.FromServices"/>,
    /// which builds the real probes from the already-registered clients (never a fabricated one).</para>
    /// </summary>
    public static async Task<ConfigHealthReport> ProbeAsync(
        ConfigHealthReport staticReport, IServiceProvider services, IConfiguration config,
        CancellationToken cancellationToken = default)
    {
        var probes = services.GetService<ConfigHealthProbeSet>()
            ?? ConfigHealthProbeSet.FromServices(services, config);

        var overrides = new Dictionary<string, ConfigCheck>();
        async Task RunAsync(string key, Func<CancellationToken, Task<ProbeResult>>? probe, ConfigCheck original)
        {
            if (probe is null)
            {
                return; // unresolved client / not configured → keep the static skipped value
            }

            ProbeResult result;
            try
            {
                result = await probe(cancellationToken);
            }
            catch
            {
                // NEVER surface the exception: on the geocoding path it can carry the key-bearing URL.
                result = new ProbeResult(Error, "unreachable");
            }

            overrides[key] = original with { Status = result.Status, Message = result.Message };
        }

        var byKey = staticReport.Checks.ToDictionary(c => c.Key);
        if (byKey.TryGetValue(ProbeSearxng, out var searxng))
        {
            await RunAsync(ProbeSearxng, probes.SearXng, searxng);
        }

        if (byKey.TryGetValue(ProbeGeocoding, out var geocoding))
        {
            await RunAsync(ProbeGeocoding, probes.Geocoding, geocoding);
        }

        if (byKey.TryGetValue(ProbeContext, out var context))
        {
            await RunAsync(ProbeContext, probes.ContextScrape, context);
        }

        if (overrides.Count == 0)
        {
            return staticReport;
        }

        var merged = staticReport.Checks
            .Select(c => overrides.GetValueOrDefault(c.Key, c))
            .ToList();
        return staticReport with { Checks = merged };
    }

    // A check for a value that parses (or should) as an absolute URI. Missing → missingStatus (Error for a
    // fatal Core value, Warning for optional infra); present-but-not-absolute → Error (malformed).
    private static ConfigCheck UriCheck(
        string group, string key, string? value, bool requireHttps,
        string missingStatus = Error, string? missingMessage = null)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return new ConfigCheck(group, key, missingStatus, missingMessage ?? "missing", Billable: false);
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || (requireHttps && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new ConfigCheck(group, key, Error,
                requireHttps ? "malformed — not an absolute https URL" : "malformed — not an absolute URL",
                Billable: false);
        }

        return new ConfigCheck(group, key, Ok, "configured", Billable: false);
    }

    private static ConfigCheck PresenceCheck(
        string group, string key, string? value, string present, string missing, string missingStatus = Error) =>
        string.IsNullOrWhiteSpace(value)
            ? new ConfigCheck(group, key, missingStatus, missing, Billable: false)
            : new ConfigCheck(group, key, Ok, present, Billable: false);

    // A probe check's static (unprobed) shape: skipped, with a hint to run live checks.
    private static ConfigCheck NotProbed(string group, string key, string label, bool billable) =>
        new(group, key, Skipped, $"{label}: not probed (run live checks).", Billable: billable);
}

/// <summary>
/// The outcome of a single live probe: a status (<c>ok</c>/<c>error</c>/<c>skipped</c>) and a <b>curated</b>
/// message. A probe delegate returns this; it must never build the message from an exception or a target URL.
/// </summary>
public sealed record ProbeResult(string Status, string Message);

/// <summary>
/// The swappable set of live-probe delegates, one per probeable check (null = its client is unresolved, so
/// the check stays <c>skipped</c>). Production builds these from the registered clients via
/// <see cref="FromServices"/>; tests inject fakes to drive <see cref="ConfigHealth.ProbeAsync"/> and to prove
/// the billing-safe "a static call runs zero probes" invariant with a counter.
/// </summary>
public sealed record ConfigHealthProbeSet(
    Func<CancellationToken, Task<ProbeResult>>? SearXng,
    Func<CancellationToken, Task<ProbeResult>>? Geocoding,
    Func<CancellationToken, Task<ProbeResult>>? ContextScrape)
{
    /// <summary>
    /// Builds the real probes from the already-registered clients — each resolved <b>optionally</b>
    /// (<c>GetService</c>, null → the probe is null → the check stays <c>skipped</c>). Deliberately does NOT
    /// take <c>IHttpClientFactory</c> as anything but an optional resolve, and never fabricates a named
    /// client: the geocoding client is registered only when its key is present (else it would inherit the
    /// resilience pipeline), so its probe is built only when the key is present.
    /// </summary>
    public static ConfigHealthProbeSet FromServices(IServiceProvider services, IConfiguration config)
    {
        var httpFactory = services.GetService<IHttpClientFactory>();
        var googleKey = config["Geocoding:GoogleApiKey"];

        return new ConfigHealthProbeSet(
            SearXng: httpFactory is null ? null : ct => ProbeSearxngAsync(httpFactory, ct),
            // Only when the key is present — otherwise CreateClient returns a pipeline-inheriting client.
            Geocoding: httpFactory is null || string.IsNullOrWhiteSpace(googleKey)
                ? null
                : ct => ProbeGeocodingAsync(httpFactory, googleKey, ct),
            ContextScrape: httpFactory is null ? null : ct => ProbeContextAsync(httpFactory, ct));
    }

    // SearXNG healthy sidecars answer ?format=json with 403 when json is not in `search.formats` (gotcha),
    // which reads like an auth failure — so a 403 here is a REACHABLE sidecar, not a failure.
    private static async Task<ProbeResult> ProbeSearxngAsync(IHttpClientFactory factory, CancellationToken ct)
    {
        var client = factory.CreateClient(SearxngWebSearch.HttpClientName);
        using var response = await client.GetAsync("search?q=ping&format=json", ct);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Forbidden)
        {
            return new ProbeResult(ConfigHealth.Ok, "reachable");
        }

        return new ProbeResult(ConfigHealth.Error, "unreachable");
    }

    // The key rides in the query string; on any failure we return a curated message only (ProbeAsync's catch
    // is the backstop, but a mapped non-200 must not echo the body either).
    private static async Task<ProbeResult> ProbeGeocodingAsync(
        IHttpClientFactory factory, string apiKey, CancellationToken ct)
    {
        var client = factory.CreateClient(GoogleGeocoder.HttpClientName);
        if (client.BaseAddress is null)
        {
            client.BaseAddress = new Uri(GoogleGeocoder.DefaultBaseAddress);
        }

        var uri = "maps/api/geocode/json?latlng=0,0&key=" + Uri.EscapeDataString(apiKey);
        using var response = await client.GetAsync(uri, ct);
        if (!response.IsSuccessStatusCode)
        {
            return new ProbeResult(ConfigHealth.Error, "unreachable");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        // A curated status classification — never surface the body (it can echo the request).
        if (body.Contains("REQUEST_DENIED", StringComparison.Ordinal)
            || body.Contains("INVALID_REQUEST", StringComparison.Ordinal))
        {
            return new ProbeResult(ConfigHealth.Error, "auth failed");
        }

        return new ProbeResult(ConfigHealth.Ok, "reachable");
    }

    private static async Task<ProbeResult> ProbeContextAsync(IHttpClientFactory factory, CancellationToken ct)
    {
        var client = factory.CreateClient(ContextWebPageFetcher.HttpClientName);
        // A minimal scrape of a throwaway URL — this DOES consume one context.dev credit (hence billable).
        // Any 2xx/4xx that is not 401/403 means the endpoint is reachable and the key is accepted; 401/403
        // means the key is missing or rejected.
        using var response = await client.GetAsync("v1/web/scrape/markdown?url=https://example.com", ct);
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
        {
            return new ProbeResult(ConfigHealth.Error, "auth failed");
        }

        return new ProbeResult(ConfigHealth.Ok, "reachable");
    }
}
