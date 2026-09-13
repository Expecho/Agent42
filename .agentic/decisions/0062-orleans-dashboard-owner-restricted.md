---
id: 0062
date: 2026-09-03
status: accepted
tags: [orleans, observability, easy-auth, authorization, dependency-bump, no-migration]
supersedes: []
extends: [0002, 0042]
amends: []
---

# Orleans dashboard at `/dashboard`, restricted to a single owner

> **Amended by [ADR 0074](0074-identity-alias-for-a-second-entra-account.md) (2026-09-13).**
> The gate compares `CurrentUser.GetUserId()`, which is where the identity alias is applied, so the
> rule below now admits one **person** rather than one **principal**: an account aliased onto the
> configured owner reaches the dashboard too. Everything else stands — it is still not a cross-user
> surface, and `Dashboard:OwnerOid` still fails closed and still ships blank.

## Context
The app runs an Orleans silo and has no way to see it. Grain activation counts, silo health,
per-grain-method latency and reminder state are all invisible. The closest thing today is the `/logs`
metrics table (ADR 0054), which shows *instruments* — it does not show cluster shape.

Identifying the right package was the expensive half of this work, and getting it wrong takes
production down rather than merely producing a broken page.

### The community `OrleansDashboard` package must never be referenced (measured)
Version 8.2.0 — its latest, built 2024-07-13 — fails on Orleans 10.2.2 with:

```
System.MissingMethodException: Method not found:
'!!0 Orleans.Serialization.Invocation.ITargetHolder.GetTarget()'
```

thrown from its pre-baked `OrleansCodeGen.OrleansDashboard.Invokable_*` types. **The silo never
starts** — this is an unhandled exception out of `Silo.StartAsync`, not a degraded page. In Container
Apps that is a crash-looping container, and revision health is gated on *every* container in the
replica, so it takes the whole app down: the `SEARXNG_SECRET` failure shape, not the
`GOOGLE_MAPS_API_KEY` one. Its dependency tree also pulls `OpenTelemetry.Api` 1.9.0, which carries a
known moderate advisory — a `vulnerable_packages: 0` failure on its own.

Upstream issue #450 was closed without a fix; the project was deprecated in Nov 2025 and merged into
`dotnet/orleans` (PR #9751). `QualityGateTests.Packages_DoNotReferenceTheDeprecatedCommunityDashboard`
pins the ban, matching the exact `Include="OrleansDashboard"` attribute — a bare
`Contains("Dashboard")` would false-positive on the Microsoft package this repo *does* reference.

### What the official package actually does (measured, 10.3.1)
- `GET /dashboard` → **301** to `/dashboard/`; `/dashboard/` → **200 text/html**;
  /dashboard/index.min.js → 200 (3.5 MB); /index.min.js → **404**, so nothing leaks to the root.
  The markup references assets *relatively* (./index.min.js), which is why the 301 matters — but the
  package emits it, so no redirect handling is ours.
- **No silo-side storage requirement.** Verified on a silo configured the way this repo configures its
  own — `AddActivityPropagation()`, a single *named* grain storage provider, an in-memory reminder
  service, no `PubSubStore` and no default provider. No new storage provider, no change to
  `AgentGrain.StorageProviderName`.
- **It opens NO listener of its own.** `DashboardOptions` exposes exactly three properties —
  `HideTrace`, `CounterUpdateIntervalMs`, `HistoryLength` — and no `Port`/`HostSelf` (the community
  package had both). This closes a real question: containers in one Container App share a network
  namespace (it is why `web_search` reaches `localhost:8888`), so a self-hosted dashboard port would
  have been reachable from the SearXNG sidecar — third-party code ADR 0059 treats as untrusted. It is
  not. The only path in is our own gated pipeline.
- **It DOES install an incoming grain-call filter.** It registers `GrainProfilerFilter` as an
  `IIncomingGrainCallFilter`, so **every grain call is profiled**, including `AgentGrain`'s
  non-reentrant turn path. In-memory stats are bounded by `HistoryLength`. The DLL adds ~16 MB to the
  image.

## Decision
Mount the official `Microsoft.Orleans.Dashboard` at `/dashboard` in both silo hosts, behind Easy Auth
(which it inherits for free) **plus** a single-owner check, and bump the Orleans stack 10.2.2 → 10.3.1
so the dashboard is taken at its latest stable rather than pinned back.

### One authorization rule, no environment branching
The rule is: *the request's user id must equal the configured `Dashboard:OwnerOid`; if no owner is
configured, deny.*

DevHost and the AppHost configure that key as **`local-dev`** — exactly what `CurrentUser.GetUserId()`
falls back to when no Easy Auth principal header is present — so offline is open through the *same*
rule that locks production down. There is deliberately **no `IsDevelopment()` branch anywhere**: an
environment check is a second code path that only ever executes where you cannot test it.

Three properties of the rule, each with a test:

- **Fails closed.** Blank or absent `Dashboard:OwnerOid` denies *everyone*. A missing config value must
  never widen access. This is what makes it safe to ship the key present and empty.
- **`local-dev` is the one catastrophic value for that key** in
  `src/Agent42.Backend/appsettings.json`, because it is the *unauthenticated* fallback identity. A
  `//DANGER` comment sits at the key, and `Gate_DoesNotTreatTheOfflineFallbackUserAsOwner` pins the
  negative case.
- **Compared `OrdinalIgnoreCase`.** A GUID pasted in different casing is the same principal; an ordinal
  compare would fail closed on casing and 404 the dashboard for its own owner — indistinguishable from
  the feature being broken.

Non-owners get **404, not 403**: a 403 confirms the page exists.

### The gate is middleware over the path prefix, not an endpoint filter
`UseWhen` on `Path.StartsWithSegments("/dashboard")` — segment-safe, and `OrdinalIgnoreCase` by
default. The dashboard's routes are mapped by the package, so a convention applied to the
`IEndpointConventionBuilder` it returns would depend on the package propagating that convention to
every endpoint, including its 3.5 MB asset route. A security control must not rest on a third party's
route-building details.

**Honest limit:** this is *reasoning*, not a tested property. `Gate_Blocks_A_NonOwner_OnSubPathsAsWellAsThePage`
maps its own stub endpoints, so an endpoint filter would propagate to them by construction and the
test would stay green either way. Falsifying the choice needs the real package's endpoints, and no
test spins up a silo with it.

### The gate is installed before the static-file middleware
Immediately after `app.Use(UserTelemetryMiddleware.InvokeAsync)`, and **before**
`app.UseDefaultFiles()`/`app.UseStaticFiles()`. Installed after them it still works today only because
no `wwwroot/dashboard/` exists — anyone later dropping a file there would serve it **ungated** while
the gate still looked installed. `Program_GatesTheOrleansDashboardBeforeServingIt` asserts the
ordering. Position relative to `UseForwardedHeaders` is immaterial: the gate reads
`X-MS-CLIENT-PRINCIPAL-ID` and uses neither scheme nor host.

### No infra change, deliberately
No `excludedPaths` entry, no Bicep change, no new secret, no new Cosmos container, no deploy-workflow
change. `excludedPaths` held four entries when this shipped — ADR 0002's banner carries the current list, which
is the only place worth reading a count off — and matches a path exactly unless the entry ends
`/*` (ADR 0042), so `/dashboard` is gated by *absence*. `EasyAuthExclusionTests` gains three rows —
`/dashboard`, `/dashboard/`, /dashboard/index.min.js — because `/shared` taught us those are three
separate mistakes.

**The owner id is committed to `appsettings.json`**, not threaded through Bicep as a secret. An Entra
object id is an identifier, not a credential, and this repo is private. That single call is what keeps
this MR free of infra changes. Matching on **email** was rejected: the gotcha "Easy Auth claim `typ`
spellings … are a prod-only fact" makes the email claim measurably riskier than the principal id,
which arrives in one well-known header.

### Where the code lives
A new Backend feature folder `src/Agent42.Backend/Orleans/` (23 → 24 folders), **not** `Dashboard/`:
ADR 0031's `/usage` view is already called "the token-usage dashboard" and lives in `Telemetry/`, so a
folder called `Dashboard/` is ambiguous under ADR 0040's mirror-the-vocabulary rule. Flat
`namespace Agent42.Backend;` as always (ADR 0039) — a folder named `Orleans/` is exactly where a fresh
reader writes `Agent42.Backend.Orleans`, which would both break persisted `$type` values and shadow the
real `Orleans` namespace.

`DashboardRegistration` is **split into two members** so the gate half is executable by a test with no
silo, which is what keeps the coverage gate satisfiable — the `HindsightRegistrationTests` precedent.
The `MapOrleansDashboardUi` half cannot run without a silo and is kept to one line for that reason.

This is **not** a new `AgentApi.*` endpoint group: the routes come from the package, and forcing one
would mean a fake map method existing only to satisfy a test. **Endpoint-group count stays 17.** The
folder is justified by ADR 0040's feature vocabulary alone — *not* by S3776, since `Program.cs` gains
straight-line calls and no new branch, so the complexity delta is zero and that claim would be
decorative.

### Orleans 10.2.2 → 10.3.1
All 8 packages exist at 10.3.1; restore is clean with no `NU1605` downgrades and no vulnerable
packages. `Aspire.Hosting.Orleans` 13.5.2 has no `Microsoft.Orleans.*` dependency at all, so the
AppHost is unaffected.

The bump's exact test cost was measured on both versions. On 10.2.2 the suite is 1124/1124 green; on
10.3.1 exactly two tests fail, both understood and neither a behaviour regression:

1. `GrainStorageSerializerTests.StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack` —
   **behaviour unchanged, only the exception text moved.** 10.2.2 said `"Unable to find a constructor
   to use"`; 10.3.1 says `"Unable to resolve type "<>z__ReadOnlyArra…"`. The state is still unreadable,
   so the `gotchas.md` hazard entry stands and was **not** softened — only the quoted message updated,
   with a comment saying it is version-sensitive.
2. `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` — **not a new
   regression and not the random flake it resembles**, but the standing explanation for it was
   **wrong**, and this is the finding of the two worth reading.

   The plan (and `gotchas.md`, and `LiveTelemetryCollection`'s own doc) held that this is
   process-global contamination by **gen_ai spans on `OtelSourceName`** from classes that had not
   joined `[Collection("LiveTelemetry")]`, and that adding the four remaining ones
   (`LiveMetricExporterTests`, `LiveSpanProcessorTests`, `LiveTelemetryBufferTests`,
   `LiveTelemetryCaptureWiringTests`) fixes it. **Measured: it does not.** With all four added the test
   still went **0 of 2 runs green under `--collect:"XPlat Code Coverage"`** — the CI path. Dumping the
   buffer on a failing run showed the contaminants were not gen_ai spans at all:

   ```
   Source = Microsoft.AspNetCore, Name = PUT /api/conversations/{conversationId}/pin
   Source = Microsoft.AspNetCore, Name = GET /api/conversations
   ```

   **The mechanism is one level above `AddSource`.** That test calls `ConfigureOpenTelemetry()`, which
   subscribes to the **ASP.NET Core instrumentation**, and an `ActivityListener` is process-global — so
   while the host is alive it captures every server span in the process. The contaminators are
   therefore **the entire API test suite**, not three classes, and **no collection membership can ever
   fix it**. Coverage instrumentation makes it far likelier because it holds the listener open for
   seconds rather than milliseconds.

   **Fix applied:** the emptiness assertion is **scoped to the source under test**
   (`s.Source == LiveTelemetryCaptureExtensions.OtelSourceName`). That is not a weakened assertion —
   nothing in the test emits on that source, and the one class that does (`MeterNamingTests`) is
   serialised with it by the collection, so a span appearing there would still be a real defect.
   **4/4 green under `--collect`**, where it was 0/2. The four collection additions were kept: they are
   correct on their own terms and the collection's doc already required them, but they are not what
   fixed this. Both `gotchas.md` and `LiveTelemetryCollection`'s doc now carry the correction, because
   the failure mode this leaves behind is a future reader adding a fifth class to the collection and
   burning a session on a lever that is measurably inert.

**What actually protects production data across the bump** — the only thing standing between an Orleans
upgrade and every conversation at rest — is that
`GrainStorageSerializerTests.DocumentWrittenByAnEarlierDeployment_StillDeserializes`,
`ConversationIndexDocument_WrittenBeforePinning_StillDeserializes_AsUnpinned` and
`UserMcpConfigDocument_WrittenBeforeKnownToolNames_StillDeserializes` read committed **golden Cosmos
documents** and are green on 10.3.1.

## Consequences

### Every grain call is now profiled
`GrainProfilerFilter` sits on the incoming grain-call path, including `AgentGrain`'s non-reentrant
turn. This is a real per-call cost on the user's latency path, stated here as a consequence rather
than discovered later. The image also grows ~16 MB.

### The same-origin question — an accepted residual, measured
An earlier draft argued no CSP was needed because the dashboard renders "our own cluster's data".
**That premise is false and must not be repeated.** Two attacker-influenceable strings flow into
exactly what this dashboard renders:

- **Reminder names are model-supplied free text.** `AgentGrain.Scheduling.cs` checks `name` only for
  null/whitespace before `RegisterOrUpdateReminder`. A prompt injection (ADR 0011 / 0027 / 0049 — all
  live inbound channels) chooses that string.
- **Grain primary keys are client-supplied.** `AgentKey(http.GetUserId(), conversationId)`, where
  `conversationId` is a route value the browser picks.

So the honest framing is: content an injection can influence, rendered on our origin, in the one
browser that can reach it — with ADR 0032's unencrypted `agent42-cache` IndexedDB mirror and the
ambient Easy Auth cookie (ADR 0035 share minting, ADR 0054 trace dump) behind that same origin. This is
structurally the threat the gotcha "Proxying a third-party app onto our own origin" was written for,
and the owner gate makes the *administrator* the target rather than mitigating it.

**Measured 2026-09-03, headless Chrome over CDP against DevHost.** A reminder was registered whose
`name` was `<img src=x onerror="window.__xss=1">`, on a grain whose conversation id was
`<b onmouseover="window.__xss=2">k</b>`; `/dashboard/Reminders` confirmed both strings reached the wire
(the JSON `<` escaping there is a JSON escape, not an HTML one, and proves nothing on its own).
Loading `/dashboard/#/reminders` and inspecting the live DOM:

| Check | Observed |
|---|---|
| Canary `window.__xss` after load / after Reminders | `0` / `0` — never fired |
| elements matching `img[src="x"]` | `0` |
| elements matching `b[onmouseover]` | `0` |
| elements matching `[onmouseover],[onerror],[onload]` (any inline handler) | `0` |
| Payload present in `innerHTML` **escaped** (`&lt;img src=x`) | `true` |
| Payload present in `innerHTML` **raw** | `false` |
| Grain key escaped / raw | `true` / `false` |
| Console errors | none |

Rendered markup, verbatim:
`<td><span class="pull-right">&lt;img src=x onerror="window.__xss=1"&gt;</span></td>`

**The bundle escapes both.** The page is also a pure client-side SPA — the server HTML carries no
cluster data at all, only a loader — so there is no server-rendered injection point either.

The residual we accept is therefore **bounded and named**: we depend on the escaping of a signed
Microsoft package, re-verifiable by repeating the measurement above. No `sandbox` CSP is applied,
because a sandbox without `allow-same-origin` is what would close it and that would break the
dashboard's own same-origin `fetch`. Had the payload executed, this would have been a stored XSS on
our origin reachable through a documented prompt-injection channel, and the feature would not have
shipped without a new decision.

### This is not a third cross-user read surface
`/logs` (ADR 0054) and `/usage` (ADR 0031) are both global: any signed-in user reads every user's
data. `/dashboard` deliberately is not — one principal, compared against one config value. It shows
grain *types* and activation counts rather than conversation content, but reminder rows do carry grain
keys and reminder names, which is exactly why the owner gate exists rather than being left to Easy
Auth alone.

### The service worker must bypass it
`sw.js` gains `if (url.pathname.startsWith("/dashboard")) return;` beside the `/api` and `/searxng`
guards, before any `respondWith`. The half that matters most is the navigate branch: the gate answers a
non-owner with 404, and the offline fallback would paint `/index.html` — the chat app — in its place, a
cached shell quietly contradicting the gate. `CACHE` bumps `agent42-shell-v26` → `-v27`. Strictly the
standing rule names `wwwroot/js/`, but ADR 0059 bumped for exactly this change (a new `sw.js` guard for
a new server-rendered path).

### Three environments, not two
`dotnet run --project src/Agent42.AppHost` runs the **Backend** project, so it loads the real
`src/Agent42.Backend/appsettings.json` and would pick up the production owner id (or the blank one) and
404 for the `local-dev` identity. Hence an explicit `Dashboard__OwnerOid=local-dev` env on the backend
resource in `AppHost.cs`. DevHost has no `appsettings.json` at all, so it sets the value in
`Program.cs`.

## Alternatives considered
- **The community `OrleansDashboard` package.** Rejected on measurement: it does not start the silo on
  Orleans 10.x, and it would crash-loop production. See Context.
- **Pinning the dashboard back to match Orleans 10.2.2.** Rejected in the planning interview in favour
  of bumping the whole stack, honouring `add_policy: latest-stable`. The cost was measured and bounded
  to two tests.
- **An env var via Bicep for the owner id.** Rejected: it drags in `container-app.bicep`, `main.bicep`,
  `deploy.yml` and a new secret, to protect a value that is not a credential.
- **Matching on email rather than object id.** Rejected: Easy Auth claim `typ` spellings are a
  prod-only fact (see gotchas), which makes the email claim measurably riskier than the principal id.
- **An endpoint filter on the returned `IEndpointConventionBuilder`.** Rejected: it would make a
  security control depend on the package propagating a convention to every route it maps, including the
  3.5 MB asset route.
- **A role system.** Out of scope. One principal compared against one config value; it must not grow
  into authorization infrastructure here.
- **Retro-fitting the owner gate to `/logs` or `/usage`.** Out of scope — narrowing those is a separate
  decision.

## Verification
- Full suite **1141/1141 green** on Orleans 10.3.1 (1124 baseline + 17 new), including **4/4 consecutive
  runs under `--collect:"XPlat Code Coverage"`** — the condition that reproduced the telemetry race.
- **`coverage_new_code_pct_min`: 83.3% (15/18) — PASS** against a threshold of 80, measured with
  `node .agentic/scripts/check-new-code-coverage.mjs`. This was the gate the plan called out as at real
  risk, and it needed two deliberate moves to clear:
  `DashboardRegistration` is split so the gate half is **executed** by a host test (11/11 on
  `DashboardOwnerGate`, 4/4 on `DashboardRegistration`), and `MapOrleansDashboardUi` — which the plan
  expected to stay uncovered — is reached by `MappingTheDashboard_WithoutTheSiloRegistration_FailsFast`,
  which asserts the package's own guard rather than serving a page, so it needs no silo.
  The 3 remaining uncovered lines are the three `Program.cs` statements; nothing in the suite runs
  `Program.cs`, which is structural in this repo and is exactly why AC12/AC13 are source-text guards.
  **Worth knowing for the next feature:** coverlet counts comment *and blank* lines inside a lambda
  (e.g. the `builder.UseOrleans(silo => …)` body) as coverable, so a densely commented registration
  there is charged to this gate — a 6-line comment and one blank line took the branch from 83.3% to
  58.3%. Comments at top level are not counted.
- **`cognitive_complexity_max`: 0 S3776 violations — PASS** (the rule is an `error`, so `Build
  succeeded` is the measurement).
- `dotnet format Agent42.slnx --verify-no-changes` clean; `dotnet build` with no new warnings and no
  `NU1605`; `dotnet list package --vulnerable --include-transitive` reports none across all six
  projects; `node .agentic/scripts/check-doc-paths.mjs` clean.
- **DevHost, live:** owner gets 301 / 200 / 200 on `/dashboard`, `/dashboard/`,
  /dashboard/index.min.js; a request carrying a different `X-MS-CLIENT-PRINCIPAL-ID` gets **404 on all
  three**, including the 3.5 MB asset; /index.min.js is 404, so nothing leaks to the root.
- **AC12 was mutation-checked**: deleting `app.UseOrleansDashboardOwnerGate()` from `Program.cs` turns
  `Program_GatesTheOrleansDashboardBeforeServingIt` red with
  *"Program.cs no longer installs the dashboard owner gate — /dashboard is open to every allowlisted
  user"*, and `Hosts_RegisterTheOrleansDashboard` red alongside it.
- The XSS go/no-go of Step 5a was measured in a real browser; the table is above.

## Unverified residual
- **`Dashboard:OwnerOid` ships BLANK, so production currently pays the full cost of this feature for
  none of its benefit.** The owner's Entra object id was not available at implementation time, and the
  plan forbids inventing a placeholder. Because the rule fails closed, the production dashboard
  **denies everyone** — inert, not open, which is the safe direction. But `silo.AddDashboard()` is
  called **unconditionally**, so the `GrainProfilerFilter` on every grain call and the ~16 MB of image
  are already being paid. That is a deliberate trade (a conditional registration would be a second code
  path that only runs in one environment — the thing this design exists to avoid), but it should be a
  short-lived state. DevHost and the AppHost are unaffected (`local-dev`).

  **To enable it**, either of:
  1. Set `Dashboard:OwnerOid` in `src/Agent42.Backend/appsettings.json` and deploy. This is the
     intended route and the reason the value is committed config — but note it is a code commit plus a
     full deploy, not a portal toggle.
  2. Set a `Dashboard__OwnerOid` environment variable on the container app. This works (the AppHost
     already proves the binding) but requires a `container-app.bicep` edit — i.e. exactly the infra
     change this ADR avoided. Prefer route 1 unless the id must differ per environment.

  Find the id in Azure Portal → Entra ID → Users → Object ID, or as a `user.id` attribute on any span
  in the production `/logs` console. A wrong value 404s the dashboard for everyone and looks identical
  to the feature being broken, which is why the match is `OrdinalIgnoreCase`.
- **The production leg is unexercised.** Everything above was measured against DevHost's silo. The
  owner path in production has never run with a real Easy Auth principal header, because no owner id
  is configured yet.
- **The middleware-vs-endpoint-filter choice is reasoning, not a tested property** — see the Decision
  section. AC12/AC13 carry the weight instead.
