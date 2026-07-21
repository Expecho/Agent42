# Plan — Configuration health tab in Preferences

planned-against: main @ 0d26ae4

## Overview

Add a **Configuration** tab to the Preferences modal that tells a signed-in user which
required/optional configuration values are missing, malformed, or (opt-in) unreachable —
so a silently-degraded feature (geocoding off, push off, context.dev fetch failing) is
visible instead of mysterious. Backed by a new read-only `GET /api/config-health`
endpoint. The load-bearing property: **the report never contains a secret value** — only
set/missing/ok status, exactly like the existing `Mask`/`hasKey`/`PushConfig.Enabled`
discipline.

Why now: the app depends on ~10 config inputs sourced from GitHub secrets → Container App
secrets → env vars, and several degrade *silently* when absent. There is no way today to
see which are healthy without reading logs/Bicep.

## Key framing (drives the whole design)

The **fatal** config values are eager-validated at startup (`Program.cs:44/58/189/227`),
so **if the app booted, they are already present** — the user can never reach this tab
with a fatal value blank. Therefore the useful signal is:
1. **Optional/degrading** features silently off — geocoding, context.dev, Web Push.
2. **Prod-infra** secrets that don't block boot in the shape the health check sees them.
3. **Present-but-wrong** values (right shape, wrong key/endpoint) — detectable **only by a
   live probe**, not a presence check.

Per the interview, checks are **static by default + opt-in live probes** (a "Run live
checks" action), and the tab is scoped to **secrets + degrading features** (fatal-but-
present values shown as a compact "OK" summary, not itemised noise).

## Affected areas (verified against the tree @ 0d26ae4)

- `src/Agent42.Backend/ConfigHealth.cs` — **new.** A **pure static builder**
  `ConfigHealth.BuildStatic(IConfiguration, IHostEnvironment, PushConfig)` + a
  `ProbeAsync(...)` live-probe helper. **DTO placement:** the wire records
  (`ConfigHealthReport`, `ConfigCheck`) go at the **bottom of `AgentApi.cs`** with every
  other endpoint response DTO (`PushConfigResponse:974`, `ProvidersResponse:949`) —
  convention wins over co-location; only config value-objects (`PushConfig`) get their own
  file.
- `src/Agent42.Backend/AgentApi.cs` — add `GET /api/config-health` inside `MapAgentApi`
  (the preferences GET block, ~line 403–511), following the `GET /api/push/config`
  (line 511) read-only shape; add the two response records at the bottom (~line 974).
- `src/Agent42.Backend/wwwroot/index.html` — new tab button + panel in the preferences
  tablist (region 254–352).
- `src/Agent42.Backend/wwwroot/app.js` — **APPEND** the `[tab,panel]` pair to `profileTabs`
  as the **last** entry (line 57–62). Critical: `profileTabs.select(3)` is hardcoded in the
  MCP-OAuth-return deep-link (`app.js:2466`) and `select(0)` opens General (`:1642`);
  inserting the new tab **before** MCP would shift MCP to index 4 and break the OAuth return.
  Append last → new tab is index 4, MCP stays 3. Add `loadConfigHealth()` fetch+render
  (mirror `loadProviders`/`renderProviders`, line 1777–1865), a "Run live checks" handler
  (re-fetch with `?probe=true`), and a call in `openProfileDialog` (line 1635+).
- `src/Agent42.Backend/wwwroot/styles.css` — status pill styling (reuse existing
  preference-list styles where possible, region ~1242–1281).
- `tests/Agent42.Tests/ConfigHealthTests.cs` — **new.** Builder + security-invariant tests.
- `tests/Agent42.Tests/ConfigHealthAssetsTests.cs` — **new** (source-text heuristic, same
  pattern as `LocationAssetsTests`, since there is no JS runner in the repo).
- `.agentic/decisions/0029-config-health-tab.md` — **new** ADR via `/decide`.

## Context & constraints (what the executor can't cheaply re-derive)

- **The config surface** (evidence from `Program.cs` + `appsettings.json` + `deploy.yml` +
  `infra/main.bicep`), grouped as the tab should present it:
  - *Core (fatal — show as compact OK summary; blank is impossible on a booted app):*
    `AzureOpenAI:Endpoint` (`Program.cs:44`), `AzureOpenAI:ApiKey` (`:58`),
    `AzureOpenAI:EmbeddingDeploymentName` (`:189`), `WebSearch:Endpoint` (`:227`).
    `SEARXNG_SECRET` is a **sidecar** secret the backend never reads — the backend
    **cannot presence-check it**; only a live probe of SearXNG on `WebSearch:Endpoint`
    tells you anything. Say so honestly in the report rather than faking a check.
  - *Prod infra:* `Storage:BlobEndpoint` (`Program.cs:141/159`, required in prod, also
    gates DP-ring persistence), `AzureOpenAI:TenantId` (`:72`). `AAD_CLIENT_SECRET` is a
    Container-App auth secret injected by Bicep and **not visible to app config** — same
    honesty caveat as SEARXNG_SECRET.
  - *Optional/degrading (the high-value items):* `Geocoding:GoogleApiKey` (`:288`, else
    `CoordinatesOnlyGeocoder`), `ContextScrape:ApiKey` (`:266`, else per-call 401),
    `Push:VapidPrivateKey`+`Push:VapidPublicKey` (`:327–334`, **both** required — reuse
    `PushConfig.Enabled`, don't recompute).
- **Never leak secrets (THE security invariant), and the PROBE path is the real leak
  channel — not the static path.** The report exposes booleans/enums + curated messages
  only. Mirror `AgentApi.Mask` (line ~429) and `PushConfig` (`Enabled`, never the key).
  The subtle hole (critic HIGH): `ConfigCheck.Message` is free text, and a live probe that
  catches an exception must **never** put `ex.Message`/`ex.ToString()` or the **target URL**
  into it — the geocoding key **rides in the request query string**
  (`GoogleGeocoder.cs:63`, `&key={apiKey}`; its own comment at `:23–24` flags this, same
  hazard as the "OTel redacts an HttpClient span's query string" gotcha). A probe therefore
  reports only a **fixed, curated** message per outcome (e.g. "reachable", "auth failed",
  "unreachable") — never the raw exception or URL. Tests must cover **both** paths at the
  **serialized-response** level: static (fake secrets in config) AND a **failing
  secret-bearing probe** (a probe delegate that throws an exception whose text contains the
  fake key → assert the key is absent from the serialized report). Read the `/api/providers`
  masking (`AgentApi.cs:431–436`) and `PushConfig.cs` before writing the DTO.
- **Static core must be a pure function** of `IConfiguration` + `IHostEnvironment` +
  `PushConfig` — no HTTP, no DI graph — so it is unit-testable without a host and cannot
  drift into a slow/costly call. Live probes are a **separate** `ProbeAsync` path.
- **Avoid a new mandatory both-hosts DI registration, and DO NOT inject `IHttpClientFactory`
  directly.** `PushConfig` had to be registered in **both** `Backend/Program.cs` **and**
  `tools/Agent42.DevHost/Program.cs` (DevHost `Program.cs:111`, comment warns `/api/push/config`
  param inference breaks routing for the whole host if absent). Prefer: the endpoint injects
  **`IServiceProvider`** plus the already-both-registered `IConfiguration`, `IHostEnvironment`,
  `PushConfig` (Backend `Program.cs:337`, DevHost `:111`), and resolves probe deps **optionally**
  via `sp.GetService<T>()` (null → `skipped`). **Do not take `IHttpClientFactory` as an endpoint
  parameter** — it is NOT registered in DevHost (no `AddServiceDefaults`/`AddHttpClient`), so
  minimal-API param inference would re-trip the DevHost routing trap the `Program.cs:108` comment
  warns about. Any new mandatory singleton MUST be registered in both hosts (and documented).
- **Live probes are opt-in and safe by construction.** Targets are **config-fixed hosts**
  (embedding endpoint, `WebSearch:Endpoint`, `maps.googleapis.com`, context.dev) with
  **zero model/user input** — the same reasoning that keeps `get_current_location` off the
  SSRF path (gotcha "`get_current_location` is NOT an SSRF sink"). Do **not** add an
  address guard, and do **not** let any probe target come from the request body/query.
- **Probe-client wiring (critic corrected two factual errors — verify before coding):**
  - **Embeddings has NO named HttpClient** — it runs through the Azure SDK
    `AzureOpenAIClient(...).GetEmbeddingClient(...)` (`Program.cs:200–202`). To probe it,
    resolve the registered `IEmbeddingGenerator<string, Embedding<float>>` singleton
    (both hosts) and do a tiny embed, **not** a named client.
  - **The geocoding named client is registered CONDITIONALLY** inside
    `if (!IsNullOrWhiteSpace(googleApiKey))` (`Program.cs:289–311`), so its
    `RemoveAllResilienceHandlers()` opt-out lives in a branch that may not run — calling
    `CreateClient("google-geocoding")` when the key is absent returns a client that
    **inherits** the retry pipeline (gotcha "A named HttpClient silently inherits
    AddServiceDefaults' retry pipeline"). So: only probe geocoding when the key is present
    (else `skipped`), and never fabricate a client. Only the **SearXNG** and **context.dev**
    named clients are unconditional and already `RemoveAllResilienceHandlers()`.
  - **SearXNG probe accuracy (gotcha "the JSON API answers 403 unless enabled"):** a
    `?format=json` probe can return **403** on a *healthy* sidecar (JSON not in `formats`),
    reading as an auth failure, while a plain GET returns 200 HTML even when JSON is off.
    Probe with the same request shape the real client uses and treat 403 accordingly, or the
    probe mislabels a healthy sidecar.
- **Probe billing/latency:** geocoding and context.dev probes cost a billable call/credit;
  that is why they are opt-in (button), never on tab-open. Label them as billable in the UI.
- **Environment awareness:** prod-only checks (`Storage:BlobEndpoint`, tenant, the
  can't-see-them caveats) render as informational/"n/a" in Development. Read
  `IHostEnvironment.EnvironmentName`. DevHost runs offline (canned LLM, in-memory grains,
  no `AddServiceDefaults`) — the tab should render there without error, mostly "not
  configured (offline)".
- **Visibility is acceptable to all allowlisted users.** No role system exists beyond the
  Easy-Auth object-id allowlist (2 trusted users, `main.bicep:111–114`); presence booleans
  are low-sensitivity. Endpoint stays **behind Easy Auth** (never `excludedPaths`), like
  every other `/api/*` read. Record this as an explicit decision in the ADR.
- **Frontend is vanilla JS, no build** (`wwwroot/app.js` exemplar). Copy the `wireTabs`/
  tab-pair shape (`app.js:57–62`, `1607–1633`) and the `loadProviders`→`renderProviders`
  fetch/render shape (`1777–1865`). No JS test runner → asset tests are source-text
  heuristics (see `LocationAssetsTests`, gotcha notes on that limitation).

## Steps

1. **DTOs + static builder** — `ConfigHealth.cs`: `ConfigHealthReport(string Environment,
   IReadOnlyList<ConfigCheck> Checks)`, `ConfigCheck(string Group, string Key, string
   Status /* ok|warning|error|skipped|na */, string Message, bool Billable)`. Implement
   `BuildStatic(config, env, pushConfig)` covering the grouped surface above, including the
   honest "cannot check from app config" cases (SEARXNG_SECRET, AAD secret) as `skipped`
   with an explanatory message, and cheap format checks (endpoint parses as absolute https
   URI; VAPID public key non-empty when private key set). **Verify:** `ConfigHealthTests`
   — all-set→ok, missing-optional→warning, malformed-endpoint→error; **secret-leak test**
   (known fake secrets absent from serialized report). *(Parallelizable with step 4.)*
2. **Live probe helper** — `ProbeAsync` on `ConfigHealth`: reachability/auth probes for
   SearXNG (`WebSearch:Endpoint`), embeddings, geocoding, context.dev, using optionally-
   resolved existing clients; each probe independently try/caught → `ok`/`error`/`skipped`,
   never throws. Merge probe results onto the static report. **Verify:** `ConfigHealthTests`
   — a fake probe delegate returns error → check flips to error; unregistered client →
   skipped. (No live network in tests.)
3. **Endpoint** — `GET /api/config-health` in `MapAgentApi` (behind Easy Auth): `?probe=true`
   runs static+live, else static only. Returns `Results.Json(report)`. Inject `IServiceProvider`
   + `IConfiguration`/`IHostEnvironment`/`PushConfig` (NOT `IHttpClientFactory`). **Verify:**
   `ConfigHealthApiTests` over `Microsoft.AspNetCore.TestHost` (exemplar: `ProvidersApiTests.cs`)
   — (a) `GET /api/config-health` returns the expected JSON shape; (b) the static call triggers
   **zero** probe calls (billing-safety, via an injected fake probe counter); (c) `?probe=true`
   runs probes.
4. **Frontend tab** — index.html tab button + panel; `app.js` tab-pair, `loadConfigHealth()`
   (GET static on open), render grouped list with status pills, a "Run live checks
   (billable)" button that re-fetches with `?probe=true`; styles.css pills. **Verify:**
   `ConfigHealthAssetsTests` (source-heuristic: tab id present, fetch path present, wired
   into `profileTabs`); manual DevHost open. *(Parallelizable with step 1.)*
5. **ADR** — `/decide` → `0029-config-health-tab.md`: records the never-leak invariant, the
   "can only report on a booted app" framing, opt-in-live-probe decision, behind-Easy-Auth +
   all-allowlisted-users visibility, and the can't-see-sidecar/auth-secret honesty caveats.
6. **Doc sweep** — `/sync-docs`: add a CLAUDE.md bullet for the feature; add a gotcha only
   if a new both-hosts registration was introduced or to pin the never-leak invariant.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Static report flags a missing optional secret as `warning` | `MissingOptionalSecret_IsWarning` | `tests/Agent42.Tests/ConfigHealthTests.cs` |
| AC2 | All-configured → optional items `ok`, fatal summary `ok` | `AllConfigured_ReportsOk` | same |
| AC3 | Malformed endpoint → `error` | `MalformedEndpoint_IsError` | same |
| AC4 | Static report never contains a secret value (security invariant) | `Report_NeverContainsSecretValues` | same |
| AC4b | **A failing secret-bearing PROBE never leaks the secret** (probe messages are curated, never `ex`/URL) | `FailingProbe_NeverLeaksSecret` | same |
| AC5 | Push status derives from `PushConfig.Enabled` (both keys required) | `PushCheck_MirrorsPushConfigEnabled` | same |
| AC6 | A failing live probe flips its check to `error`; unresolved client → `skipped` | `LiveProbe_FlipsStatus`, `UnresolvedProbeClient_IsSkipped` | same |
| AC7 | Sidecar/auth secrets the app can't see are reported `skipped` with reason, not faked | `UnreadableSecret_IsSkippedWithReason` | same |
| AC8 | Frontend tab is present and wired, appended LAST (source heuristic) | `ConfigTab_IsWiredIntoPreferences` | `tests/Agent42.Tests/ConfigHealthAssetsTests.cs` |
| AC9 | Endpoint returns expected JSON; **static call runs zero probes** (billing-safety); `?probe=true` runs probes | `Static_RunsNoProbes`, `Probe_RunsProbes` | `tests/Agent42.Tests/ConfigHealthApiTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80
- cognitive_complexity_max: 15
- vulnerable_packages: 0 (no new packages expected)
- `dotnet format Agent42.slnx --verify-no-changes` clean · `dotnet test Agent42.slnx` green
- Branch: `feature/config-health-tab` (+ MR). Never commit to `main`.

## Out of scope

- Checking **GitHub Actions secrets** directly (the app only sees runtime config; a missing
  GitHub secret surfaces as a missing env var, which the runtime *can* see, or as a sidecar
  the app can't inspect — reported honestly as `skipped`).
- Editing/repairing config from the UI (read-only diagnostic only).
- A role-gated "admin" view (no role system; all allowlisted users may view).
- Historical/continuous health monitoring, alerting, or a Bicep/deploy-time check.
- Validating the Easy-Auth user allowlist against Entra enterprise-app assignments
  (out-of-process, needs Graph — not runtime config).

## Review

Critic pass (fresh-context, verified against tree @ 0d26ae4). Verdict: sound design that
navigates the trickiest traps well, but one HIGH hole in the security invariant + several
test/factual gaps. All findings **accepted and folded in**:

1. **HIGH — probe path is the real leak channel, untested.** Accepted. The geocoding key
   rides in the query string (`GoogleGeocoder.cs:63`), so an unsanitized probe exception
   message would leak it. Fixed: added the "PROBE path is the real leak channel" constraint
   (curated messages only, never `ex`/URL) + AC4b + `FailingProbe_NeverLeaksSecret`.
2. **MEDIUM — opt-in/billing invariant untested.** Accepted → AC9 `Static_RunsNoProbes`.
3. **MEDIUM — "reuse existing clients" factually wrong.** Accepted → corrected the
   probe-client wiring bullet: embeddings use `IEmbeddingGenerator` (no named client);
   the geocoding named client is conditionally registered (retry-pipeline trap when key
   absent); only SearXNG/context.dev are unconditional.
4. **MEDIUM — endpoint had no AC/test.** Accepted → step 3 now maps to `ConfigHealthApiTests`
   over `TestHost` (exemplar `ProvidersApiTests.cs`); AC9.
5. **LOW-MEDIUM — hardcoded tab indices.** Accepted → "APPEND last" pinned (MCP OAuth
   deep-link uses `select(3)` at `app.js:2466`); AC8 asserts it.
6. **LOW — SearXNG 403-on-json probe trap.** Accepted → noted in the probe-wiring bullet.
7. **LOW (shape) — DTO placement.** Accepted → wire records moved to `AgentApi.cs` bottom
   (convention), builder stays in `ConfigHealth.cs`.

Verified clean by the critic (no change needed): all line citations accurate; `planned-against`
== HEAD; ADR 0002 (Easy Auth) — no contradiction, endpoint stays behind auth; ADR 0029 slot
free; SSRF gotcha correctly applied; both-hosts registration trap correctly avoided (plus the
added `IServiceProvider`-not-`IHttpClientFactory` note).

## Implementation notes (divergence from plan)

Executed on branch `feature/config-health-tab` from `main @ 0d26ae4` (no drift). The design was
followed faithfully; the notable elaborations:

- **Probe seam.** `ProbeAsync` takes a swappable `ConfigHealthProbeSet` (nullable delegates, one per
  probeable check) that it resolves **optionally** from `IServiceProvider`, falling back to
  `ConfigHealthProbeSet.FromServices` (the real probes built from the registered clients). This is the
  seam the plan's AC9 "injected fake probe counter" needed — a test registers a counting set, so a
  static call is proven to run **zero** probes and `?probe=true` to run them. `ConfigHealthProbeSet` is
  registered **only in tests**, never in either prod host, so it introduces no both-hosts registration.
- **Check model.** Each probeable feature has its own `probe:*` check that is `skipped` statically and
  overridden by the merge (rather than mutating the presence check), so "unresolved client → skipped"
  and "static value stands" fall out for free.
- **Extra test file (not in the plan's file list): `ConfigHealthProbeTests.cs`.** Added to pin the real
  probe classification logic (the SearXNG-403-is-healthy rule, geocoding `REQUEST_DENIED` → auth-failed
  with the body/key absent from the message, context 401/403 → auth-failed, embeddings success → ok, and
  `FromServices` null-when-unregistered) over a canned `HttpMessageHandler` + fake `IEmbeddingGenerator`
  — **not** live network. This lifted `ConfigHealth.cs` coverage from ~70% (probe methods uncovered) to
  **99.2%** and is what carried the coverage gate.
- **Critic pass (fresh context, post-implementation): clean, 3 LOW nits, all fixed before commit** — two
  misleading code comments (embeddings "not billable"; context.dev "no scrape") corrected, and the
  CLAUDE.md feature bullet (plan step 6) added.

### Evidence (this session)
- `dotnet test Agent42.slnx -c Release`: **464 passed, 0 failed**.
- `dotnet format --verify-no-changes`: clean.
- Coverage (coverlet): `ConfigHealth.cs` **99.2%** (127/128); one unreachable base-address fallback line.
- Cyclomatic complexity (lizard): max **10** (`ProbeAsync`), under the 15 gate.
- No package changes (the `System.Security.Cryptography.Xml` `NU1903` warning is a pre-existing transitive
  test dependency, not introduced here).
