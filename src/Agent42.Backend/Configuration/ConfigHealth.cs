namespace Agent42.Backend;

/// <summary>
/// Builds the <c>GET /api/config-health</c> report (the Preferences → Configuration tab): which
/// required/optional configuration inputs are present, malformed, or (opt-in) unreachable, so a silently
/// degraded feature (geocoding off, Web Push off, long-term memory off) is visible instead of
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
/// path, opt-in because a geocoding probe costs a billable call.</para>
/// </summary>
public static partial class ConfigHealth
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
    private const string ProbeHindsight = "probe:hindsight";

    /// <summary>
    /// The static report — a pure function of config + environment + push state. No network. Fatal values
    /// (which a booted app already has) are itemised so a malformed value still surfaces as an error, but
    /// the tab renders the Core group compactly; the high-value signal is the Optional group.
    /// </summary>
    public static ConfigHealthReport BuildStatic(
        IConfiguration config, IHostEnvironment env, PushConfig push, TimeProvider? clock = null)
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

        // ————— Production infrastructure (informational / n-a in Development, and n-a for local storage) —————
        AddInfrastructureChecks(checks, config, isDevelopment);

        checks.Add(new ConfigCheck(GroupInfra, "AAD_CLIENT_SECRET", Skipped,
            "Injected by Bicep into the Container App's auth sidecar; it is not visible to app config, "
            + "so it cannot be presence-checked here.", Billable: false));

        AddAuthChecks(checks, config);

        // ————— Optional / degrading features (the high-value items) —————
        checks.Add(PresenceCheck(GroupOptional, "Geocoding:GoogleApiKey", config["Geocoding:GoogleApiKey"],
            present: "reverse geocoding enabled",
            missing: "not set — get_current_location reports coordinates without a street address",
            missingStatus: Warning));
        checks.Add(NotProbed(GroupOptional, ProbeGeocoding, "Geocoding reachability", billable: true));

        // Long-term memory (ADR 0056). Absent is the DEFAULT and it is the OPERATOR's off switch — since
        // ADR 0057 a user can additionally deselect memory in Preferences → Tools, so this key being present
        // no longer means every turn is remembered. The missing message says what is lost rather than how to
        // fix it, because absent is the intended state.
        checks.Add(PresenceCheck(GroupOptional, "Hindsight:ApiKey", config["Hindsight:ApiKey"],
            present: "configured — long-term memory is available; each user selects it per chat in "
                + "Settings → Tools (ADR 0057), so this key being set does not mean every turn is remembered",
            missing: "not set — long-term memory is off; the agent recalls and retains nothing",
            missingStatus: Warning));
        // Unlike the geocoding probe this one is FREE: /health is unauthenticated and
        // metered by nobody, so it costs the operator nothing to press the button.
        checks.Add(NotProbed(GroupOptional, ProbeHindsight, "Hindsight reachability", billable: false));

        // Application Insights: traces (chat content stripped) and metrics, on top of OTLP. Absent is a
        // supported state: the app then exports to OTLP only. The connection string carries the ingestion
        // key, so like every other value here it is never echoed.
        checks.Add(PresenceCheck(GroupOptional, AzureMonitorTraceExport.ConnectionStringKey,
            config[AzureMonitorTraceExport.ConnectionStringKey],
            present: "configured — traces (without chat content) and metrics also go to Application Insights",
            missing: "not set — telemetry goes to the OTLP collector only",
            missingStatus: Warning));

        // Inbound email (ADR 0096). Absent is supported for the app, but the endpoint then refuses (503), so the
        // missing message names the refusal rather than a degradation. Nothing to probe without a delivery.
        checks.Add(PresenceCheck(GroupOptional, "Email:MailgunSigningKey", config["Email:MailgunSigningKey"],
            present: "configured — mail to an agent's address runs that agent",
            missing: "not set — POST /api/email-in answers 503 and no inbound email is accepted",
            missingStatus: Warning));

        // Web Push: derive from PushConfig.Enabled — the single source of the "push is on" fact (both VAPID
        // keys required). Never recompute from the raw keys.
        checks.Add(new ConfigCheck(GroupOptional, "Push:Vapid", push.Enabled ? Ok : Warning,
            push.Enabled
                ? "Web Push enabled."
                : "off — Web Push needs both a VAPID private key (secret) and public key.",
            Billable: false));

        checks.Add(AliasCheck(config));
        checks.Add(SignalCliCheck(config, (clock ?? TimeProvider.System).GetUtcNow()));

        return new ConfigHealthReport(env.EnvironmentName, checks);
    }
}
