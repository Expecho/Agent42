using Hindsight.AgentFramework;
using Microsoft.Extensions.AI;

namespace Agent42.Backend;

public static partial class ConfigHealth
{
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

        if (byKey.TryGetValue(ProbeHindsight, out var hindsight))
        {
            await RunAsync(ProbeHindsight, probes.Hindsight, hindsight);
        }

        if (byKey.TryGetValue(ProbeWebSearchChain, out var chain))
        {
            await RunAsync(ProbeWebSearchChain, probes.WebSearchChain, chain);
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
    Func<CancellationToken, Task<ProbeResult>>? Hindsight,
    Func<CancellationToken, Task<ProbeResult>>? WebSearchChain = null)
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
        var hindsightKey = config["Hindsight:ApiKey"];

        return new ConfigHealthProbeSet(
            SearXng: httpFactory is null ? null : ct => ProbeSearxngAsync(httpFactory, ct),
            // Only when the key is present — otherwise CreateClient returns a pipeline-inheriting client.
            Geocoding: httpFactory is null || string.IsNullOrWhiteSpace(googleKey)
                ? null
                : ct => ProbeGeocodingAsync(httpFactory, googleKey, ct),
            // Key-gated for the same reason as geocoding, not because the probe needs the key (it does not
            // — /health is unauthenticated): the named client is registered only when the key is present, so
            // CreateClient would otherwise hand back a resilience-pipeline-inheriting client.
            Hindsight: httpFactory is null || string.IsNullOrWhiteSpace(hindsightKey)
                ? null
                : ct => ProbeHindsightAsync(httpFactory, config, ct),
            WebSearchChain: services.GetService<WebSearchCooldowns>() is { } cooldowns
                ? _ => Task.FromResult(WebSearchChainState(cooldowns, config))
                : null);
    }

    // The chain's state from the in-memory cooldowns (ADR 0155). Calls no vendor, so it is free. Kinds and times only:
    // no key, no URL, no query. Nothing is shown unless a keyed provider is in the effective chain.
    public static ProbeResult WebSearchChainState(WebSearchCooldowns cooldowns, IConfiguration config)
    {
        var order = WebSearchChainRegistration.ParseOrder(config["WebSearch:Providers"], out _)
            .Where(n => n != SearxngSearchProvider.Provider
                && !string.IsNullOrWhiteSpace(config[$"WebSearch:{char.ToUpperInvariant(n[0])}{n[1..]}:ApiKey"]))
            .ToList();
        if (order.Count == 0)
        {
            return new ProbeResult(ConfigHealth.Ok, "no hosted provider configured — the SearXNG sidecar answers");
        }

        var parts = order.Select(name => cooldowns.TryGetBenched(name, out var b)
            ? $"{name} benched: {FallbackWebSearch.Describe(b.Kind)} until {b.Until:HH:mm} UTC"
            : $"{name} ok");
        var anyBenched = order.Any(n => cooldowns.TryGetBenched(n, out _));
        return new ProbeResult(anyBenched ? ConfigHealth.Warning : ConfigHealth.Ok,
            string.Join(", ", parts) + ", searxng fallback");
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

    // Hindsight /health is UNAUTHENTICATED and free, which is why this probe is Billable: false while the
    // geocoding one is not. No key is sent and none is needed; the point is only whether
    // the configured base address answers. A curated constant either way, never the exception or the URL.
    private static async Task<ProbeResult> ProbeHindsightAsync(
        IHttpClientFactory factory, IConfiguration config, CancellationToken ct)
    {
        var client = factory.CreateClient(nameof(HindsightClient));
        // Blank, not just null — an unset optional value arrives as an empty string (see
        // HindsightRegistration), and "" would probe "/health" against no host at all.
        var configured = config["Hindsight:BaseAddress"];
        var root = (string.IsNullOrWhiteSpace(configured)
            ? HindsightOptions.DefaultBaseAddress.ToString()
            : configured).TrimEnd('/');
        using var response = await client.GetAsync($"{root}/health", ct);
        return response.IsSuccessStatusCode
            ? new ProbeResult(ConfigHealth.Ok, "reachable")
            : new ProbeResult(ConfigHealth.Error, "unreachable");
    }
}
