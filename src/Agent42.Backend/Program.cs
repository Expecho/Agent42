using System.Threading.Channels;
using Agent42.Agents;
using Agent42.Backend;
using Azure;
using Azure.AI.OpenAI;
using Azure.Identity;
using Azure.ResourceManager;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.AI;
using OpenTelemetry.Instrumentation.AspNetCore;
using Orleans.Dashboard;

var builder = WebApplication.CreateBuilder(args);

// Who authenticates the caller (ADR 0145): Easy Auth in front of the app (the default; absent changes nothing and prod's
// Bicep needs no change), or this process via native Entra OIDC. `entra` with a missing setting throws here, at startup:
// failing open to an anonymous `local-dev` owner is worse than not booting.
var authMode = AuthModeSetting.Read(builder.Configuration);
if (authMode == AuthMode.Entra)
{
    builder.Services.AddEntraAuth(EntraAuthSettings.Read(builder.Configuration));
}

// Usage-telemetry ingestion channel (ADR 0031): the span processor enqueues projected usage events
// here (OnEnd, best-effort) and UsageIngestionService drains them into the global rollup grain.
// Bounded + DropWrite so a slow grain can never back-pressure the OTel export path.
var usageChannel = Channel.CreateBounded<UsageEvent>(
    new BoundedChannelOptions(10_000) { FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true });
builder.Services.AddSingleton(usageChannel.Reader);
builder.Services.AddHostedService<UsageIngestionService>();

// OTel logging, metrics and tracing via Aspire service defaults: OTLP, plus Application Insights metrics
// when APPLICATIONINSIGHTS_CONNECTION_STRING is set. NonOwnerContentRedactor.Register is passed as the
// hook that runs BEFORE any trace exporter is added (ADR 0121): it removes non-owners' prompt text from
// gen_ai spans ahead of the OTLP exporters and the /logs ring. Registering it any later is too late.
builder.AddServiceDefaults(NonOwnerContentRedactor.Register);

// The Backend's span processors (user.* identity, usage rollups), the live telemetry console at /logs
// (ADR 0054) and, LAST, the Application Insights trace fan-out that strips chat content. One method,
// because the order is load-bearing and nothing in the suite runs this file. Right after
// AddServiceDefaults(), before anything else registers a trace processor.
builder.AddBackendTelemetry(usageChannel.Writer);

// Identity aliases (ADR 0074): an additional Entra object id resolving to the object id that owns the
// data, so one person's two accounts reach one dataset with no data movement. Built ONCE here, because
// the enrichment callback below receives an HttpRequest — not an HttpContext — and runs at
// HttpRequestIn.Start, BEFORE the middleware pipeline, where request-scoped resolution is not safe to
// assume; it captures this local instead of resolving DI. The same instance is registered for
// CurrentUser.GetUserId and UserTelemetryMiddleware, which do have request services.
// An invalid entry is dropped and reported, never fatal: identity aliasing is not on the availability
// path, so an app that boots with the alias inert is recoverable where one that refuses to boot is not.
var userIdAliases = UserIdAliases.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(userIdAliases);

// Who is an owner (ADR 0121): the committed Access:Owners list. Fails closed when empty. Advisory, not a
// security boundary: see IOwnerPolicy.
builder.Services.AddSingleton<IOwnerPolicy>(OwnerPolicy.FromConfiguration(builder.Configuration));

// Stamp OTel semantic-convention user.* attributes onto the ASP.NET Core server span of every
// authenticated request (ADR 0009). The Easy-Auth-specific extraction lives in CurrentUser here in
// the Backend, keeping the shared ServiceDefaults generic (ADR 0002). This Configure delegate
// composes with the AddAspNetCoreInstrumentation Filter set in ServiceDefaults — both run, they set
// different options properties, so neither overwrites the other.
//
// Skipped in `entra` mode (ADR 0145): this runs at HttpRequestIn.Start, BEFORE any middleware, so it would tag the span from
// a FORGED X-MS-CLIENT-* header. PrincipalHeaderMiddleware re-applies the tags after it has stamped the real principal.
if (authMode != AuthMode.Entra)
{
    builder.Services.Configure<AspNetCoreTraceInstrumentationOptions>(options =>
        options.EnrichWithHttpRequest = (activity, request) =>
            activity.ApplyUserTelemetryTags(request.Headers, userIdAliases));
}

// No operator embedding floor any more: embeddings are a per-user model chosen in Preferences (the
// document-RAG generator resolves from the user's overlay, ADR 0007/0018), so AzureOpenAI:Endpoint/ApiKey
// are no longer eager-validated at startup. They remain readable ONLY by the dormant Foundry-provider
// inheritance (ProviderSettings.ResolveFoundry) — baseline Models:Providers is empty (ADR 0019), so nothing
// triggers it; a user's Foundry provider declares its own endpoint + key. The app now boots with no Azure
// OpenAI config at all, like it already did with no chat provider.

// The managed identity drives the services that are NOT Azure OpenAI: blob storage (user-delegation SAS)
// and — kept for a future discovering provider — the ARM management plane. TenantId still binds it.
var credential = new DefaultAzureCredential(AzureCredentialSettings.Options(builder.Configuration));

// Model providers, the per-user resolver/factory/catalogs and the Auto router (ADR 0013, 0018, 0019, 0137); see
// ModelProvidersRegistration. The returned setup is kept for the startup log of skipped providers below.
var models = builder.Services.AddModelProviders(builder.Configuration, credential);

// Durable storage (Storage:Provider, default azure): Cosmos + Blob today, or Postgres + a filesystem volume for
// self-hosting. Registers the attachment, skill, document-index and chat-search ports, the Data Protection key
// ring (ADR 0018) and, for Azure, the Cosmos/Blob clients. See StorageRegistration; the Orleans half is below.
builder.AddStorageProvider(credential);

// Per-user MCP tools (config-driven remote MCP servers): the provider connects to the signed-in user's
// enabled MCP servers and hands the agent grain their tools (resolved on activation, version-cached,
// best-effort, bounded connection lifetime). The agent grain merges them into BuildAgent's tool list,
// gated by the ToolCatalog.Mcp key (ADR 0021).
builder.Services.AddSingleton<IMcpConnectionFactory, McpConnectionFactory>();
builder.Services.AddSingleton<IUserMcpToolProvider, UserMcpToolProvider>();
// MCP Apps (ADR 0133): the registry reads a view and runs a view's tool call through the SAME provider instance (and so
// the same cached sockets); it is Backend-only, and the endpoints resolve it with GetService so another host may omit it.
builder.Services.AddSingleton<IMcpAppHandleSource>(sp => sp.GetRequiredService<IUserMcpToolProvider>() as UserMcpToolProvider
    ?? throw new InvalidOperationException("MCP Apps need the Backend UserMcpToolProvider."));
builder.Services.AddSingleton<IMcpAppRegistry, McpAppRegistry>();
// The InteractiveOAuth access-token resolution shared by the activation path above and the user-pressed
// connection test below (ADR 0064). Extracted so the test cannot false-negative on a server the chat path
// connects to fine: there is exactly one implementation of the refresh-at-connect rule.
builder.Services.AddSingleton<McpOAuthTokenResolver>();
// The one-shot connection probe behind POST /api/mcp-servers/{id}/test. Resolved OPTIONALLY out of
// IServiceProvider inside the endpoint handler (the /api/speech pattern), so a host without an
// IMcpConnectionFactory — DevHost's real shape before its offline fake — degrades to a curated 503 instead
// of breaking route matching for the whole host.
builder.Services.AddSingleton<McpConnectionTester>();
// The local bridge (ADR 0076): a LocalBridge MCP server is reached over a socket its helper opened to
// /api/local-bridge. AddLocalBridge DECORATES the IMcpConnectionFactory registered above, so it must stay
// below it — ProjectLayoutTests.Hosts_RegisterLocalBridge pins the order.
builder.Services.AddLocalBridge();

// Document RAG (ADR 0007/0018). There is NO operator embedding generator: the embedding model is
// per-user (chosen in Preferences), so ingestion and retrieval resolve their generator from the user's
// chat-client factory (IUserChatClientFactoryProvider.GetEmbeddingGenerator) — no fixed deployment, no
// AzureOpenAI:EmbeddingDeploymentName prerequisite. A user with no embedding model configured cannot
// attach documents (the composer gates it) and RAG retrieval degrades to empty. The extractor, chunker and
// ingestion service are host-invariant and come from AddAgent42Silo below.

// Web tools (ADR 0011, 0059, 0066): web_search + the SearXNG proxy + the guarded read_web_document fetcher. A missing
// WebSearch:Endpoint fails here, at startup; see WebRegistration.
builder.Services.AddWebTools(builder.Configuration);

// User location (ADR 0012): reverse-geocoding for the get_current_location tool. A missing key degrades to
// coordinates only; see GeocodingRegistration.
builder.Services.AddGeocoding(builder.Configuration);

// Long-term memory across conversations (ADR 0056): a Hindsight memory bank per user, recalled into the
// agent's instructions before each model call and retained after it. Optional and off by default; see
// HindsightRegistration for what the two branches mean and why this is not inline here.
builder.Services.AddHindsightMemory(builder.Configuration);

// Outbound webhooks for stored agents (ADR 0094): a signed POST of every bound, persisted turn to the owner's URL,
// over a client guarded against private addresses at connect time. See WebhookRegistration.
builder.Services.AddAgentWebhooks();

// Inbound email (ADR 0096): Mailgun forwards mail sent to an agent's address to POST /api/email-in. The signing key
// is an optional secret, but without it the endpoint answers 503 rather than skipping verification. See
// EmailRegistration.
builder.Services.AddInboundEmail(builder.Configuration);

// The chat gateway (ADR 0111): each user's own bots on chat platforms, connected OUT from this one replica (no public
// endpoint), behind the Blob lease that keeps an overlapping revision from running them twice. See
// ChatGatewayRegistration.
builder.Services.AddChatGateway();
// Signal (ADR 0111 phase 3): dormant unless ChatGateway:Signal:Enabled; see ChatGatewayRegistration.AddSignalChat.
builder.Services.AddSignalChat(builder.Configuration);

// The copilot agent kind: the GitHub Copilot SDK's agent loop on the user's own provider, with its session files in
// Blob. See CopilotRegistration and CopilotAgentFactory for the runtime's trust boundary.
builder.Services.AddCopilotAgents(builder.Configuration);

// The agent fleet (ADR 0097): recursive agent teams on the user's own models, run in-process (not in a grain) and
// persisted in the agent-fleet Blob container. After AddCopilotAgents: its Copilot runtime host reuses those options.
// See FleetRegistration, and ADR 0097 for the Copilot fleet agents' trust boundary.
builder.Services.AddAgentFleet(builder.Configuration);

// The resumable chat turn (ADR 0153): a turn is pumped in the Backend and its replay log kept in the chat-turns Blob
// container (files when self-hosting), so a dead socket, an F5 or a second device can rejoin a turn in flight.
builder.Services.AddChatTurns(builder.Configuration);

// Web Push notifications (ADR 0028): a push when an agent replies on an async path (a fired reminder or an
// agent-run) while the user is away. Optional, off without the VAPID key; see WebPushRegistration.
builder.Services.AddWebPush(builder.Configuration);

// The server-side MCP surface at /api/agent-mcp (ADR 0070) — stored agents exposed as a tool to an
// external MCP client, the inverse direction of the client-side servers above. Deliberately not part of
// MapAgentApi: MapMcp throws without these services, and a dependency one host lacks breaks routing for
// every endpoint that host maps. See AgentMcpServerRegistration.
builder.Services.AddAgentMcpServer();

// The registrations every host shares (ADR 0105), AFTER every port above: the endpoint invariants (the secret
// protectors on the Data Protection ring set up above, the OAuth client), then the grain-side ones, which also
// fail startup naming any grain dependency this host did not register.
builder.Services.AddAgent42Api();
builder.Services.AddAgent42Silo();

// Orleans silo: clustering comes from the Aspire Orleans resource when running
// under Aspire orchestration; localhost clustering is the fallback for a single
// standalone instance (e.g. one Container App replica with no Aspire AppHost).
// the agent state and reminders are persisted by the storage provider (Cosmos, or Postgres when local).
builder.UseOrleans(silo =>
{
    // Flow the W3C trace context (Activity.Current) across grain-call boundaries. Orleans does NOT
    // propagate it by default — grain calls hop through the Orleans scheduler, which does not carry
    // Activity.Current across the boundary — so without this, any span started INSIDE a grain (the
    // HttpClient instrumentation span for the chat-completion / geocoding / web-fetch calls) begins as
    // a new ROOT span, detached from the ASP.NET server span that triggered it. That is the "many
    // traces have no parent" symptom. This is a DISTINCT mechanism from the user.* attribution channel
    // (ADR 0010): identity rides Orleans RequestContext, which IS auto-propagated — trace-parent linkage
    // is not. Registers both the incoming and outgoing grain-call filters (this process co-hosts the
    // silo + client, so one call covers both directions). Async paths (fired reminders, agent-run) still
    // begin fresh roots by design — they have no ambient inbound Activity. See gotchas.
    silo.AddActivityPropagation();
    silo.AddDashboard(); // ADR 0062. Consequences + why NOT the community package: Directory.Packages.props.

    if (string.IsNullOrEmpty(builder.Configuration["Orleans:Clustering:ProviderType"]))
    {
        silo.UseLocalhostClustering();
    }

    silo.UseStorageProvider(builder.Configuration);
});

var app = builder.Build();

// Local storage (Storage:Provider = local) runs with no authentication: say so once at startup. See LocalModeWarning.
LocalModeWarning.LogIfLocal(builder.Configuration, app.Logger);

// Native Entra sign-in (ADR 0145): an ephemeral Data Protection ring or a `local-dev` owner must not fail silently.
AuthStartupChecks.LogIfEntra(builder.Configuration, app.Logger);

// An alias entry is an authorization grant, so one refused for being malformed must be visible rather
// than silently absent. Warning, not Information: Logging:LogLevel:Default is Warning in production, so
// an Information record would never reach a log at all (ADR 0054). The Configuration health tab reports
// the same count for the operator who is not reading logs.
if (userIdAliases.DroppedEntries.Count > 0)
{
    app.Logger.LogWarning(
        "Ignored {Count} invalid identity alias entry/entries (ADR 0074): {Detail}",
        userIdAliases.DroppedEntries.Count,
        string.Join("; ", userIdAliases.DroppedEntries));
}

// Say so once, loudly enough to find, when a provider was dropped for want of a key — otherwise
// "the GitHub models aren't in the picker" is a silent mystery. Deliberately NOT a throw (ADR 0013):
// the app must keep serving on Foundry when a third-party token is missing or has expired.
foreach (var skipped in models.SkippedProviders)
{
    app.Logger.LogWarning(
        "Model provider '{Provider}' has no ApiKey and was skipped; its models will not appear in the "
        + "picker. Set Models__Providers__{Provider}__ApiKey to enable it.", skipped, skipped);
}

// Say so once, loudly enough to find, when the location tool is running without reverse geocoding —
// otherwise "it only ever gives me coordinates" is a silent mystery (ADR 0012). Not a throw: an absent
// key degrades the feature, it does not take the app down.
if (app.Services.GetRequiredService<IGeocoder>() is CoordinatesOnlyGeocoder)
{
    app.Logger.LogInformation(
        "Geocoding:GoogleApiKey is not configured — get_current_location will report coordinates " +
        "without a street address. Set Geocoding__GoogleApiKey to enable reverse geocoding.");
}

// Ahead of everything else, because it rewrites the request itself rather than observing it: the ACA
// ingress terminates TLS and forwards over plain HTTP, so without this Request.Scheme is "http" for every
// request and two consumers mint http:// absolute URLs — the share link owners copy (ADR 0035/0042) and
// the MCP interactive-OAuth redirect_uri (ADR 0025) — as does the framework's own UseDefaultFiles 301,
// which is why this sits in front of the static-file middleware too.
//
// XForwardedProto ONLY: nothing here needs the client IP or host, and trusting those from an unidentified
// proxy is a strictly larger surface for no consumer. ForwardLimit stays at its default of 1 — a single
// ingress hop.
//
// The trust lists must be emptied with .Clear(), NOT with an `= { }` initializer: those properties are
// get-only, so `KnownNetworks = { }` parses as a nested collection initializer that adds nothing and
// leaves the framework defaults (127.0.0.0/8 and ::1) in place — it compiles, reads as "cleared", and
// silently makes this whole middleware inert for any ingress that isn't loopback. Measured, not assumed
// (ADR 0042). Emptying them is required because the ACA ingress's address is not knowable from inside the
// container, which does mean a client can spoof X-Forwarded-Proto. Accepted: the blast radius is the
// scheme of a minted URL, every external request arrives through the ingress that sets the header, and
// the share token never rides the path anyway (it is in the fragment).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
};
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeaders);

// Native Entra sign-in (ADR 0145), only in `entra` mode. This is the trust boundary that replaces the Easy Auth ingress:
// authenticate the cookie, strip every inbound X-MS-CLIENT-* header and stamp them from the principal (so it precedes
// UserTelemetryMiddleware, which reads them), then gate. The gate MUST stay ABOVE UseDefaultFiles/UseStaticFiles: those are
// terminal middleware that no authorization policy ever sees, and Easy Auth used to gate /index.html and /logs too. The
// stamper and gate also precede UseWebSockets (inside UseLocalBridge).
if (authMode == AuthMode.Entra)
{
    app.UseAuthentication();
    app.Use(PrincipalHeaderMiddleware.InvokeAsync);
    app.Use(AuthGateMiddleware.InvokeAsync);
}

// First of our own middleware (the proxy fix-up above only corrects what the ingress obscured): extract
// the request's identity once and publish it into Orleans
// RequestContext, so it propagates to every downstream span — in-request HttpClient spans and,
// across the grain call, the in-grain chat-completion span (ADR 0010). RequestContext is an
// in-cluster channel (never an outbound HTTP header), so no user PII leaks to Azure OpenAI —
// the reason this is RequestContext and deliberately not Activity.Baggage. Runs after the server
// span has already started, which is why that span stays enriched via EnrichWithHttpRequest above.
app.Use(UserTelemetryMiddleware.InvokeAsync);

// Restrict the Orleans dashboard, /logs, /api/diagnostics and /api/config-health to owners (ADR 0062,
// generalised by ADR 0121). Easy Auth already gates each path (none is in excludedPaths, which is
// default-deny), so this is the second, narrower rule. Advisory, not a boundary: see IOwnerPolicy.
// MUST stay ABOVE UseDefaultFiles/UseStaticFiles: /logs is a static page, and a gated path that is a
// file would be served ungated while the gate still looked installed. ProjectLayoutTests asserts this.
app.UseOwnerGate();

// Authenticate the server-side MCP surface (ADR 0070) BEFORE the protocol reaches it. /api/agent-mcp is
// on excludedPaths, so Easy Auth never sees it and MapMcp serves initialize/tools/list from the same
// endpoint as tools/call — a check living only inside the tool method would leave the first two
// anonymously reachable. Same ordering rule as the dashboard gate above: ABOVE UseDefaultFiles/
// UseStaticFiles, so it cannot be bypassed by a file later dropped under that path in wwwroot.
app.UseAgentMcpKeyGate();

// The local-bridge socket (ADR 0076). Also on excludedPaths, so its endpoint self-enforces the bridge key.
// Only its UseWebSockets half is order-sensitive (the route is an endpoint); it sits here so every public,
// self-enforcing surface is wired in one place.
app.UseLocalBridge();

app.MapDefaultEndpoints();

// /.auth/login/aad, /.auth/logout, /.auth/me: Easy Auth's URLs, served here only when this process does the sign-in.
if (authMode == AuthMode.Entra)
{
    app.MapAuthEndpoints();
}

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapAgentApi();
app.MapOrleansDashboardUi();
app.MapAgentMcpServer();

app.Run();
