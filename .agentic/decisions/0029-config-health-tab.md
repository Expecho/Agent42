---
id: 0029
date: 2026-07-21
status: accepted
tags: [diagnostics, configuration, preferences, security, secrets, egress, probes, easy-auth]
amended_by: [0121, 0125]
---

# Configuration health tab in Preferences

> **Amended by [ADR 0125](0125-remove-fetch-web-page-and-context-dev.md) (2026-11-14).** The context.dev presence check and its `probe:context.dev` are removed, and `ConfigHealthProbeSet` has three members.

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** `/api/config-health` is now **owner-only** (404 for everyone else, including
> `?probe=true`): its payload is operator information and its probes spend the operator's billable keys. The
> "all allowlisted users may view it" line below no longer holds.

## Context
The app depends on ~10 configuration inputs that flow GitHub secret → Container App secret → env var, and
several of them **degrade silently** when absent: geocoding falls back to coordinates-only (ADR 0012),
`fetch_web_page` 401s per call (ADR 0027), Web Push simply stays off (ADR 0028). Today there is no way to
see which of these are healthy without reading container logs or Bicep. The **fatal** values are
eager-validated at startup (`Program.cs`), so a running app already has them — the person who most needs
this signal (an operator wondering why a feature is quiet) has nowhere to look.

## Decision
Add a read-only **Configuration** tab to the Preferences modal, backed by a new `GET /api/config-health`
endpoint, that reports each config input's status (`ok`/`warning`/`error`/`skipped`/`na`) plus a curated
message — **never a secret value**.

- **The report can only speak about a booted app, so it is framed accordingly.** Fatal values are present
  by construction (the app validated them at startup), so they render as a compact Core summary, not
  itemised noise; the high-value signal is the **Optional/degrading** group (geocoding, context.dev, Web
  Push). Format checks (absolute-https URI) still run on the fatal values as cheap defence-in-depth.
- **Static by default, opt-in live probes.** `GET /api/config-health` returns the pure static report;
  `?probe=true` also runs live reachability/auth probes. Probing is a button (`Run live checks
  (billable)`), never on tab-open, because a geocoding or context.dev probe **costs a metered call/credit**.
- **`ConfigHealth.BuildStatic` is a pure function** of `IConfiguration` + `IHostEnvironment` + `PushConfig`
  — no HTTP, no DI graph — so it is unit-testable without a host and cannot drift into a slow call. Live
  probes are a separate `ProbeAsync` path.
- **The endpoint stays behind Easy Auth (never `excludedPaths`), and all allowlisted users may view it.**
  There is no role system beyond the Easy-Auth object-id allowlist (2 trusted users, `main.bicep`; superseded by ADR 0121: owner-only now); presence
  booleans + curated messages are low-sensitivity, so no admin gate is added (none exists to reuse).
- **Things the app genuinely cannot see are reported `skipped` WITH a reason, not faked.** `SEARXNG_SECRET`
  is a sidecar secret the backend never reads; `AAD_CLIENT_SECRET` is injected into the Container App's auth
  sidecar and is not visible to app config. Both are `skipped` with an explanatory message rather than a
  fabricated check.

### The never-leak invariant — and the PROBE path is the real leak channel
The report exposes a status enum + a **curated** message only — the same discipline as `AgentApi.Mask`
(keys → a `hasKey` flag) and `PushConfig` (`Enabled`, never the key). The subtle hole is the **probe** path,
not the static one: the Google geocoding key **rides in the request query string** (`GoogleGeocoder`), so a
probe that caught an exception and put `ex.Message`/`ex.ToString()` or the target URL into a message would
leak the key. Therefore `ProbeAsync` wraps every probe in a `catch` that **discards the exception** and
emits a fixed curated string (`"unreachable"`), and each probe classifies a non-2xx by status/marker
without ever echoing the response body. Tested at the serialized-response level on **both** paths: a static
report built from sentinel secrets, and a **failing secret-bearing probe** whose exception text contains the
fake key (asserted absent from the serialized report).

### Probe wiring — optional resolution, never a fabricated client
The endpoint injects `IServiceProvider` plus the both-hosts-registered `IConfiguration` / `IHostEnvironment`
/ `PushConfig`, and `ProbeAsync` resolves probe dependencies **optionally** (`GetService<T>()`, null → the
probe stays `skipped`). Two traps drove this:
- The endpoint deliberately does **not** take `IHttpClientFactory` as a parameter — it is not registered in
  DevHost (no `AddServiceDefaults`), and a minimal-API param the host cannot resolve is treated as a body,
  breaking routing for the **whole** host. It is resolved optionally inside `ProbeAsync` instead.
- The geocoding named client is registered **conditionally** (only when the key is present); calling
  `CreateClient("google-geocoding")` with no key returns a client that **inherits** `AddServiceDefaults`'
  retry pipeline. So the geocoding probe is built **only when the key is present** (else `skipped`), never
  fabricated. Embeddings have **no** named client (they run through the Azure SDK `IEmbeddingGenerator`),
  so that probe resolves the registered generator and does a tiny embed.
- The SearXNG probe treats a **403 as a healthy sidecar** (the JSON API is off — `search.formats`), not an
  auth failure — the same gotcha the real `SearxngWebSearch` client encodes.
- **No SSRF guard is added.** Every probe target is a **config-fixed host** (embedding endpoint,
  `WebSearch:Endpoint`, `maps.googleapis.com`, context.dev) with zero model/user input — the same reasoning
  that keeps `get_current_location` off the SSRF path. No probe target comes from the request.

### No new mandatory both-hosts DI registration
The feature adds **no** service that every host must register. `BuildStatic` needs only already-registered
config/env/push; probes resolve their clients optionally. A test-only `ConfigHealthProbeSet` is the swappable
seam (`ProbeAsync` prefers a registered one over its `FromServices` factory) used to inject counting/fake
probes — proving both the classification logic and the billing-safe "a static call runs **zero** probes"
invariant, without a network.

## Alternatives considered
- **Always-run probes on tab-open** — rejected: a geocoding/context.dev probe is billed, so opening
  Preferences would silently cost money. Probes are an explicit button.
- **Check GitHub Actions secrets directly** — out of scope: the app sees only runtime config. A missing
  GitHub secret surfaces as a missing env var (which the runtime *can* see) or as a sidecar it cannot
  inspect (reported honestly as `skipped`).
- **A role-gated admin view** — rejected: no role system exists, and presence booleans are low-sensitivity.
- **Editing/repairing config from the UI** — out of scope: read-only diagnostic only.
- **A dedicated `IConfigHealthProber` service registered in both hosts** — rejected: it would re-introduce
  the both-hosts registration burden the design set out to avoid. Optional `sp.GetService<T>()` resolution
  keeps the endpoint's only hard dependencies to services every host already has.

## Consequences

### The probe path is the load-bearing security surface
The static path exposes only booleans/enums and is safe by construction; the **probe** path is where a
secret could leak (the geocoding key in the query string). The `ProbeAsync` catch that discards the
exception, and each probe's refusal to echo the response body, are the controls — pinned by
`FailingProbe_NeverLeaksSecret` and `GeocodingProbe_ClassifiesBody` (asserts the key/marker are absent from
the message). Anyone adding a probe must return a **curated** `ProbeResult`, never build a message from `ex`
or a URL.

### The tab is appended LAST in the Preferences tablist
`mcp.js`'s MCP-OAuth return deep-links to `profileTabs.select(3)` (the MCP tab), so the Configuration pair
must sit **after** MCP in the `wireTabs` array — Configuration is index 4, MCP stays 3. Inserting it earlier
would silently break the OAuth return. An asset test asserts the ordering and that `select(3)` is unchanged.

### Environment-aware rendering
Prod-only inputs (`Storage:BlobEndpoint`, `AzureOpenAI:TenantId`) render as `na` in Development, where the
offline DevHost uses in-memory stores and needs none of them — so the tab is usable offline without a wall
of false warnings.

### No new egress, no new secret
The endpoint reads existing config and (on demand) dials existing config-fixed hosts with existing clients.
It adds no secret and no new outbound destination beyond what the probed features already reach.
