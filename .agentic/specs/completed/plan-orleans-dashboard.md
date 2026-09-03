# Plan — Orleans dashboard at `/dashboard`, owner-restricted in prod

planned-against: main @ bc170fa

## Overview

The app runs an Orleans silo and has no way to see it. Grain activation counts, silo health,
per-grain-method latency and reminder state are all invisible; the closest thing today is the
`/logs` metrics table (ADR 0054), which shows instruments, not cluster shape.

This plan mounts the **official** `Microsoft.Orleans.Dashboard` into the existing ASP.NET
pipeline at `/dashboard`, in DevHost and in production. Easy Auth gates it for free (it is not
in `excludedPaths`), and on top of that a single-owner check restricts it to one Entra principal
so it does not become a third cross-user read surface.

It also bumps the Orleans stack 10.2.2 → 10.3.1, which the user chose in the planning interview
so the dashboard can be taken at its latest stable rather than pinned back.

Why now: identifying the *right* package is the expensive half of this work, and it has been done —
see the measured findings below, including the wrong turn that would have taken production down.

## Affected areas

Verified against the tree at `bc170fa`.

| Area | Files |
|---|---|
| Package pins | `Directory.Packages.props` (7 Orleans pins → 10.3.1, + 1 new dashboard pin) |
| Backend project ref | `src/Agent42.Backend/Agent42.Backend.csproj` |
| **New Backend feature folder** | `src/Agent42.Backend/Orleans/` — `DashboardOwnerGate.cs`, `DashboardRegistration.cs` |
| Backend host | `src/Agent42.Backend/Program.cs` (silo `AddDashboard()`, gate install, map call) |
| Backend config | `src/Agent42.Backend/appsettings.json` (`Dashboard:OwnerOid`) |
| DevHost | `tools/Agent42.DevHost/Program.cs` (silo `AddDashboard()`, gate + map, owner = `local-dev`) |
| AppHost | `src/Agent42.AppHost/AppHost.cs` (`Dashboard__OwnerOid=local-dev` env on the backend resource) |
| Service worker | `src/Agent42.Backend/wwwroot/sw.js` (`/dashboard` guard + `CACHE` bump) |
| **Layout guard (WILL FAIL until updated)** | `tests/Agent42.Tests/ProjectLayoutTests.cs` — Backend `ExpectedFolders()` asserted **by name**, 23 → 24 |
| **Orleans-bump fallout (WILL FAIL until updated)** | `tests/Agent42.Tests/GrainStorageSerializerTests.cs` (asserted exception substring) |
| **Latent test gap the bump exposes** | `tests/Agent42.Tests/LiveMetricExporterTests.cs`, `LiveSpanProcessorTests.cs`, `LiveTelemetryBufferTests.cs`, `LiveTelemetryCaptureWiringTests.cs` (join `[Collection("LiveTelemetry")]`) |
| **CACHE version pinned in TWO places** | `tests/Agent42.Tests/PwaAssetsTests.cs:234` **and** `tests/Agent42.Tests/SearchAssetsTests.cs:118-119` (the latter also asserts `DoesNotContain` the previous version) |
| Easy Auth guard | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` (three new gated-path rows) |
| Package guard | `tests/Agent42.Tests/QualityGateTests.cs` (banned community package) |
| New tests | `tests/Agent42.Tests/DashboardOwnerGateTests.cs` |
| Docs | `.agentic/decisions/0062-*.md` (new), `.agentic/gotchas.md`, `CLAUDE.md` |

**Infra is untouched.** No `excludedPaths` entry, no Bicep change, no new secret, no new Cosmos
container, no deploy-workflow change. The owner id is non-secret config committed to
`appsettings.json` (the user's interview call — see below).

## Context & constraints

Judgment and findings the executor cannot cheaply re-derive. Code facts are pointers, not copies.

### Measured this session, 2026-09-03 — do not re-litigate, re-check only if a step fails

All of the following were run against real silos and the real repo, not inferred.

1. **The community `OrleansDashboard` package MUST NOT be used.** Version 8.2.0 (the latest, built
   2024-07-13) fails on Orleans 10.2.2 with
   `System.MissingMethodException: Method not found: '!!0 Orleans.Serialization.Invocation.ITargetHolder.GetTarget()'`
   thrown from its pre-baked `OrleansCodeGen.OrleansDashboard.Invokable_*` types. **The silo never
   starts** — an unhandled exception out of `Silo.StartAsync`, not a broken page. In ACA that is a
   crash-looping container, and revision health is gated on every container in the replica, so it
   takes the whole app down (the `SEARXNG_SECRET` shape). Upstream issue #450, closed without a fix;
   the project was deprecated in Nov 2025 and merged into `dotnet/orleans` (PR #9751). Its dependency
   tree also pulls `OpenTelemetry.Api` 1.9.0, which carries a known moderate advisory — a
   `vulnerable_packages: 0` hard-gate failure on its own.
2. **The official `Microsoft.Orleans.Dashboard` works.** Verified end-to-end on a real silo at both
   10.2.2 and 10.3.1: silo starts; `GET /dashboard` → **301** to `/dashboard/`; `GET /dashboard/` →
   **200 text/html**; `/dashboard/index.min.js` → 200 (3.5 MB), `/dashboard/index.css` → 200 (481 KB),
   `/dashboard/favicon.ico` → 200; and `/index.min.js` → **404**, i.e. nothing leaks to the root. Its
   markup references assets **relatively** (`./index.min.js`), which is why the 301 matters — but the
   package emits it, so no redirect handling is ours to write.
3. **`AddDashboard()` imposes no silo-side storage requirement.** Verified on a silo configured the
   way this repo configures its own — `AddActivityPropagation()`, a **single named** grain storage
   provider (`agent-state`), an in-memory reminder service, **no `PubSubStore`, no default provider**:
   the silo starts and both `/dashboard/` and its assets return 200. No new storage provider and no
   change to `AgentGrain.StorageProviderName`.
4. **`AddDashboard()` opens NO listener of its own.** `DashboardOptions` exposes exactly three
   properties — `HideTrace`, `CounterUpdateIntervalMs`, `HistoryLength` — and **no `Port` / `HostSelf`**
   (the community package had both). Measured listening ports after silo + dashboard start were only
   the app port, `11111` (silo) and `30000` (gateway); nothing else. **This closes a real question:**
   containers in one Container App share a network namespace (it is why `web_search` reaches
   `localhost:8888`), so a self-hosted dashboard port would have been reachable from the SearXNG
   sidecar — third-party code ADR 0059 treats as untrusted. It is not. The only path in is our own
   gated pipeline.
5. **`AddDashboard()` DOES install an incoming grain-call filter.** Measured: it registers
   `GrainProfilerFilter` as an `IIncomingGrainCallFilter`, so **every grain call is profiled**,
   including `AgentGrain`'s non-reentrant turn path. This is a real per-call cost on the user's
   latency path and must be stated in the ADR as a consequence, not discovered later. In-memory stats
   are bounded by `HistoryLength`. The package DLL adds ~16 MB to the image.
6. **API shape** (from the package's own XML docs): `siloBuilder.AddDashboard()` on the silo, and
   `app.MapOrleansDashboard(routePrefix: "/dashboard")` on the endpoint builder, returning an
   `IEndpointConventionBuilder`. It maps into the **existing** pipeline.
7. **Orleans 10.3.1 is clean to move to.** All 8 packages exist at 10.3.1; restore is clean with no
   `NU1605` downgrades and **no vulnerable packages**. `Aspire.Hosting.Orleans` 13.5.2 has **no
   `Microsoft.Orleans.*` dependency at all**, so the AppHost is unaffected by the bump.
8. **The bump's exact test cost — measured, full suite, both versions.** On 10.2.2 the suite is
   **1124/1124 green**. On 10.3.1 exactly two tests fail, both understood:
   - `GrainStorageSerializerTests.StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack`
     — **behaviour unchanged; only the exception text moved.** 10.2.2 said
     `"Unable to find a constructor to use"`; 10.3.1 says `"Unable to resolve type "<>z__ReadOnlyArra…"`.
     The state is still unreadable, so the `gotchas.md` hazard entry still holds and **must not be
     softened** — only the quoted message updates.
   - `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` — **not a new
     regression and not the random flake it resembles.** It is the pre-existing process-global
     contamination the `LiveTelemetry` collection exists to prevent, and that collection is
     **incomplete**: only `LiveLogProcessorTests`, `MeterNamingTests` and `OtelConfigurationTests`
     joined, while `LiveMetricExporterTests`, `LiveSpanProcessorTests`, `LiveTelemetryBufferTests` and
     `LiveTelemetryCaptureWiringTests` also build live-capture hosts and did not. 10.3.1 emits enough
     extra activity to make the latent race **deterministic** instead of ~2-in-6.
     **Verified fix:** adding `[Collection("LiveTelemetry")]` to those four takes 10.3.1 to
     **1123/1124**. No assertion is weakened.
9. **What actually protects prod data across the bump** (cite this in the ADR — it is the only thing
   standing between an Orleans upgrade and every conversation at rest):
   `GrainStorageSerializerTests.DocumentWrittenByAnEarlierDeployment_StillDeserializes`,
   `ConversationIndexDocument_WrittenBeforePinning_StillDeserializes_AsUnpinned` and
   `UserMcpConfigDocument_WrittenBeforeKnownToolNames_StillDeserializes` read committed **golden Cosmos
   documents** and are green on 10.3.1.
10. **`/dashboard` is gated with no infra change.** `excludedPaths` holds exactly four entries and
    matches a path exactly unless the entry ends `/*`; anything absent is gated.

### Decisions taken by the user in the planning interview

- **Both environments** — reachable in DevHost and prod.
- **Prod additionally restricted to a single owner principal**; other Easy-Auth-allowlisted users get
  **404** (not 403 — do not confirm the page exists).
- **The owner id is committed to `appsettings.json`**, not an env var / Bicep / GitHub secret. An
  Entra object id is an identifier, not a credential, and the repo is private. This is what keeps the
  MR free of infra changes. Rejected: env var via Bicep (drags in `container-app.bicep`,
  `main.bicep`, `deploy.yml`, a new secret); matching on **email** (the `gotchas.md` entry "Easy Auth
  claim `typ` spellings … are a prod-only fact" makes the email claim measurably riskier than the
  principal id, which arrives in one well-known header).
- **Bump the whole Orleans stack to 10.3.1** rather than pinning the dashboard back, honouring
  `add_policy: latest-stable`. The cost is item 8 and is bounded and measured.

### Resolved design decisions (these were open questions; do not re-open)

- **One authorization rule, no environment branching.** The rule: *the request's user id must equal
  the configured `Dashboard:OwnerOid`; if no owner is configured, deny.* DevHost and AppHost set
  `Dashboard:OwnerOid` to **`local-dev`**, exactly what `GetUserId()` falls back to with no Easy Auth
  principal header — so offline is open through the *same* rule that locks prod down. **No
  `IsDevelopment()` branch anywhere**: an environment check is a second code path that only ever
  executes where you cannot test it.
- **Fail closed.** Blank/absent `Dashboard:OwnerOid` denies everyone. A missing config value must
  never widen access.
- **`local-dev` is the one catastrophic value for that key**, because it is the *unauthenticated*
  fallback identity (`CurrentUser.LocalDevelopmentUserId`). Setting it in `src/Agent42.Backend/appsettings.json`
  would hand the dashboard to any request with no principal header. Requires: a `//` warning comment
  at the key in `appsettings.json`, and an explicit negative test `IsOwner("<guid>", "local-dev") == false`.
- **Compare with `OrdinalIgnoreCase`, not `Ordinal`.** A GUID pasted in different casing would
  otherwise fail closed and 404 the dashboard for everyone — the exact "looks broken" outcome the
  prerequisite note below warns about.
- **The gate is MIDDLEWARE over the path prefix, not an endpoint filter** — `UseWhen` on
  `Path.StartsWithSegments("/dashboard")` (segment-safe, and `OrdinalIgnoreCase` by default, so
  `/Dashboard` is covered). The dashboard's routes are mapped by the package, so a convention applied
  to the returned `IEndpointConventionBuilder` would depend on the package propagating it to every
  endpoint including its 3.5 MB asset route. A security control must not rest on that.
  **Honest limit:** this is *reasoning*, not a tested property — see the AC3 note below.
- **The gate must be installed BEFORE `app.UseDefaultFiles()` / `app.UseStaticFiles()`**, immediately
  after `app.Use(UserTelemetryMiddleware.InvokeAsync)`. Installed after them it still works today only
  because no `wwwroot/dashboard/` exists; anyone later dropping a file there would serve it **ungated**
  while the gate still looked installed. Ordering is asserted (AC12).
  Position relative to `UseForwardedHeaders`/`UserTelemetryMiddleware` is otherwise immaterial: the
  gate reads `X-MS-CLIENT-PRINCIPAL-ID` and uses neither scheme nor host.
- **New folder is `src/Agent42.Backend/Orleans/`, not `Dashboard/`.** ADR 0031's `/usage` view is
  already called "the token-usage dashboard" and lives in `Telemetry/`; a folder called `Dashboard/`
  is ambiguous under ADR 0040's mirror-the-vocabulary rule. Note `ExpectedFolders` is Ordinal-compared,
  so `"Orleans"` inserts between `"Models"` and `"Properties"`.
- **Deliberately NOT a new `AgentApi.*` endpoint group.** The routes come from the package; forcing
  one would mean a fake map method existing only to satisfy a test. **Endpoint-group count stays 17;
  Backend folder count goes 23 → 24.** The folder is justified by ADR 0040 (feature vocabulary) —
  **do not claim an S3776 complexity reason in the ADR**: `Program.cs` gains straight-line calls and
  no new branch, so the complexity delta is zero and the claim would be decorative.
- **`CACHE` bumps `agent42-shell-v26` → `-v27`.** Strictly the standing rule names `wwwroot/js/` and
  `/dashboard` adds no module, but ADR 0059 bumped for exactly this change (a new `sw.js` guard for a
  new server-rendered path). **Two tests pin the literal version** — see the affected-areas table.

### The same-origin question — an accepted residual, NOT "it's our own data"

An earlier draft argued no CSP was needed because the dashboard renders "our own cluster's data".
**That premise is false and must not appear in the ADR.** Two attacker-influenceable strings flow
into exactly what an Orleans dashboard renders:

- **Reminder names are model-supplied free text.** `src/Agent42.Agents/Scheduling/AgentGrain.Scheduling.cs`
  takes a `name` parameter checked only for null/whitespace before `RegisterOrUpdateReminder`. A prompt
  injection (ADR 0011 / 0027 / 0049 — all live inbound channels) chooses that string.
- **Grain primary keys are client-supplied.** `AgentKey(http.GetUserId(), conversationId)` where
  `conversationId` is a route value the browser picks.

So the honest framing is: **content an injection can influence, rendered on our origin, in the one
browser that can reach it** — with the ADR 0032 `agent42-cache` IndexedDB mirror and the ambient Easy
Auth cookie (ADR 0035 share minting, ADR 0054 trace dump) behind the same origin. This is structurally
the threat the `gotchas.md` entry "Proxying a third-party app onto our own origin" was written for,
and the owner gate makes the *administrator* the target rather than mitigating it.

The conclusion may still be "no sandbox" — a `sandbox` without `allow-same-origin` is what would close
it, and it would break the dashboard's own same-origin `fetch`. But it must be reached honestly:

- **Step 5a** measures whether the bundle escapes grain ids and reminder names (render a grain key and
  a reminder name containing `<img src=x onerror=…>` and observe whether it executes).
- If it escapes, record a **bounded residual** (trust in the escaping of a signed Microsoft package).
- If it does **not** escape, that is a stored-XSS on our origin reachable by prompt injection, and
  this plan stops — the feature does not ship on that finding without a new decision from the user.

### Prerequisite the executor must obtain from the user

**The owner's Entra object id (OID)** — a GUID — for `Dashboard:OwnerOid` in
`src/Agent42.Backend/appsettings.json`. Not derivable from the repo: Azure Portal → Entra ID → Users →
Object ID, or a `user.id` attribute on any span in the production `/logs` console. **Do not invent a
placeholder GUID.** The rule fails closed, so a wrong value 404s the dashboard for everyone and looks
broken. Ask; if unavailable, leave the key present and blank, and say so in the MR.

### Environments — "both" is really three, and one needs a deliberate line

`dotnet run --project src/Agent42.AppHost` runs the **Backend** project, which loads
`src/Agent42.Backend/appsettings.json` — so the Aspire full-stack path would pick up the prod owner
GUID and 404 for the `local-dev` identity. Hence the `AppHost.cs` env-var line in the affected-areas
table. DevHost has **no `appsettings.json` at all**, so it sets the value in `Program.cs`.

### Governing ADRs and gotchas — read before writing code

- `.agentic/decisions/0002-entra-easy-auth-gate.md` — the default-deny gate this relies on.
- `0042` (`excludedPaths` semantics, `UseForwardedHeaders`) — the list is unchanged; `EasyAuthExclusionTests` proves it.
- `0054` (`/logs`) and `0031` (`/usage`) — the two existing cross-user read surfaces; the ADR must say why this is not a third.
- `0059-searxng-ui-behind-easy-auth-proxy.md` — closest prior art, and the source of the same-origin argument above.
- `0040` — flat namespace + feature folders.
- `gotchas.md`: "Service worker must stay network-first…"; "The service worker's SHELL is
  all-or-nothing"; "A live-telemetry test host captures from a PROCESS-GLOBAL source"; "A collection
  expression into `IReadOnlyList<T>` … unreadable from Cosmos"; "Proxying a third-party app onto our
  own origin"; "A scripted whole-file rewrite silently converts the checkout from CRLF to LF".
- **The CRLF gotcha bit twice during planning.** CRLF, no `.gitattributes`. Any scripted edit rewrites
  the file LF and `dotnet format` then fails on every line. Prefer targeted edits; verify with `file`.
- **Flat namespace:** `ProjectLayoutTests.EveryFileStaysInTheFlatNamespace` requires
  `namespace Agent42.Backend;` in `Orleans/*.cs` — a folder named `Orleans/` is exactly where a fresh
  executor writes `Agent42.Backend.Orleans` (and worse, shadows the real `Orleans` namespace).

### Exemplars to copy (shape, not invention)

- `src/Agent42.Backend/Memory/HindsightRegistration.cs` — feature registration lifted into its own
  folder. Note its test (`HindsightRegistrationTests`) **executes** the registration against a plain
  `ServiceCollection`; copy that testability, which is what keeps the coverage gate satisfiable.
- `src/Agent42.Backend/Web/SearxngProxy.cs` — a DI-free class holding the rules, **tested as rules**.
  `DashboardOwnerGate` copies that: a pure static predicate plus a thin middleware over it.
- `tests/Agent42.Tests/ProjectLayoutTests.cs` `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware`
  — the comment-stripped source-text **ordering** assertion. AC12 copies it exactly.
- `src/Agent42.Backend/Api/CurrentUser.cs` — `GetUserId()` and the `local-dev` fallback.
- `tests/Agent42.Tests/LiveTelemetryCollection.cs` — the collection the four classes must join.

## Steps

**Step 4 depends on Step 1** (a 10.3.1 dashboard package against 10.2.2 pins is an `NU1605`
downgrade). Steps 2–3 are independent of 5–9. Step 10 is last.

### Step 1 — Orleans 10.3.1 bump

Edit the 7 `Microsoft.Orleans.*` pins in `Directory.Packages.props` to `10.3.1`. Verify CRLF survived.
**Verify:** `dotnet build Agent42.slnx` succeeds with no new warnings.

### Step 2 — Fix the two tests the bump exposes

- `GrainStorageSerializerTests.StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack`:
  update the asserted substring to 10.3.1's message (`"Unable to resolve type"`). **Keep the test
  asserting the failure** — the hazard is unchanged. Note in a comment that the message is
  version-sensitive.
- Add `[Collection("LiveTelemetry")]` to `LiveMetricExporterTests`, `LiveSpanProcessorTests`,
  `LiveTelemetryBufferTests`, `LiveTelemetryCaptureWiringTests`.
**Verify:** `dotnet test Agent42.slnx` → 1124/1124 green on 10.3.1.

### Step 3 — Update the gotcha the bump re-dates

`.agentic/gotchas.md`: the collection-expression entry quotes the old exception message — update it
and its `Verified:` date. **Bound the edit to that entry**; footers there are near-identical and an
unbounded replace silently forges `Verified:` dates on neighbours.
**Verify:** `git diff .agentic/gotchas.md` touches exactly one entry.

### Step 4 — Add the dashboard package (depends on Step 1)

Use `dotnet add src/Agent42.Backend package Microsoft.Orleans.Dashboard` and pin what the tooling
resolves into `Directory.Packages.props` (hard rule 4 — do not type `10.3.1` from this plan).
**Verify:** `dotnet restore Agent42.slnx`; `dotnet list package --vulnerable --include-transitive`
reports none.

### Step 5 — `DashboardOwnerGate` (the rule) + tests

New `src/Agent42.Backend/Orleans/DashboardOwnerGate.cs`: a DI-free static predicate
`IsOwner(string? configuredOwnerOid, string requestUserId)` — blank owner ⇒ deny; otherwise
`OrdinalIgnoreCase` match — plus the middleware that 404s when it returns false.
New `tests/Agent42.Tests/DashboardOwnerGateTests.cs` covering the rule directly, **including the
`local-dev` negative case**.
**Verify:** `dotnet test --filter "FullyQualifiedName~DashboardOwnerGateTests"`.

### Step 5a — Measure the same-origin escaping question (gates Step 10's ADR)

Against a local silo with the dashboard mounted, create a reminder whose **name** and a conversation
whose **id** contain an HTML/script payload, load `/dashboard/`, and observe whether it renders inert
or executes. Record the observed result verbatim.
**This is a go/no-go**: if the payload executes, stop and take the finding to the user — it is a
stored XSS on our origin reachable through a documented prompt-injection channel.

### Step 6 — `DashboardRegistration` + all three hosts

New `src/Agent42.Backend/Orleans/DashboardRegistration.cs`. **Split it into two members**, so the
gate half is executable by a test without a silo (this is what keeps the coverage gate satisfiable):
one that installs the gate middleware on an `IApplicationBuilder`, one that calls
`MapOrleansDashboard(routePrefix: "/dashboard")`.
- `src/Agent42.Backend/Program.cs`: `silo.AddDashboard();` inside the existing `UseOrleans` lambda;
  the gate install immediately after `app.Use(UserTelemetryMiddleware.InvokeAsync)` and **before**
  `app.UseDefaultFiles()`; the map call beside `app.MapAgentApi()`.
- `src/Agent42.Backend/appsettings.json`: `Dashboard:OwnerOid` + the `local-dev` warning comment.
- `tools/Agent42.DevHost/Program.cs`: the same calls; owner `local-dev`.
- `src/Agent42.AppHost/AppHost.cs`: `Dashboard__OwnerOid=local-dev` env on the backend resource.
- `tests/Agent42.Tests/ProjectLayoutTests.cs`: Backend `ExpectedFolders()` 23 → 24 (`Orleans`,
  between `Models` and `Properties`).
**Verify:** `dotnet build`; `dotnet test --filter "FullyQualifiedName~ProjectLayoutTests"`.

### Step 7 — Host-level gate tests

Run the gate middleware in a minimal TestServer host with stub endpoints mapped under `/dashboard`
— **no Orleans silo needed**. Assert: owner passes on both `/dashboard/` and a sub-path; non-owner
gets **404 on both**; blank config denies everyone.
**Verify:** `dotnet test --filter "FullyQualifiedName~DashboardOwnerGateTests"`.

### Step 8 — Service worker + Easy Auth + package guards

- `sw.js`: add `if (url.pathname.startsWith("/dashboard")) return;` immediately after the `/searxng`
  guard and **before** any `respondWith`; bump `CACHE` to `agent42-shell-v27`.
- `PwaAssetsTests.cs`: update the `CACHE` string; add a guard-position assertion mirroring
  `ServiceWorker_BypassesTheProxiedSearchUiBeforeCaching`.
- `SearchAssetsTests.cs:118-119`: update **both** the `Contains` and the `DoesNotContain` version.
- `EasyAuthExclusionTests.cs`: add **three** rows — `/dashboard`, `/dashboard/`,
  `/dashboard/index.min.js` — following the stated `/shared` precedent that those are three separate
  mistakes.
- `QualityGateTests.cs`: assert `Directory.Packages.props` contains no `Include="OrleansDashboard"`
  (the exact attribute string — a bare `Contains("Dashboard")` false-positives on the Microsoft package).
**Verify:** `dotnet test --filter "FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~SearchAssetsTests|FullyQualifiedName~EasyAuthExclusionTests|FullyQualifiedName~QualityGateTests"`.

### Step 9 — Wiring guard (AC12/AC13)

Add to `ProjectLayoutTests.cs`, copying `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware`'s
comment-stripped source-text technique: `MapOrleansDashboard` appears **exactly once in the repo**,
inside `DashboardRegistration.cs`; both `Program.cs` files call only the bundled extensions; and in
`Program.cs` the gate install precedes `UseDefaultFiles`.
**Verify:** `dotnet test --filter "FullyQualifiedName~ProjectLayoutTests"`.

### Step 10 — Docs (`/decide`, `/sync-docs`)

- New `.agentic/decisions/0062-orleans-dashboard-owner-restricted.md`: the community package's
  measured failure and why it is banned; the owner gate and its fail-closed property; the same-origin
  residual **as measured in Step 5a** (not the "our own data" argument); why this is not a third
  cross-user surface; the Orleans bump, its two measured test costs, and the golden-document tests
  that protect data at rest; and the measured consequences — `GrainProfilerFilter` on every grain
  call, ~16 MB image growth, no extra listener.
- `gotchas.md`: an entry for the banned community package; and the divergence from the "Proxying a
  third-party app onto our own origin" entry recorded **in that entry** (a reader adding the next such
  surface reads the gotcha, not ADR 0062).
- `CLAUDE.md`: a new bullet **and** fix the ADR 0040 bullet's "the **23** directories" count → 24.
**Verify:** full `dotnet test`; `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | The deprecated community package is not referenced | `Packages_DoNotReferenceTheDeprecatedCommunityDashboard` | `QualityGateTests.cs` |
| AC2 | The configured owner reaches the dashboard | `Gate_AllowsTheConfiguredOwner` | `DashboardOwnerGateTests.cs` |
| AC3 | A non-owner gets 404 on a sub-path as well as the page | `Gate_Blocks_A_NonOwner_OnSubPathsAsWellAsThePage` | `DashboardOwnerGateTests.cs` |
| AC4 | Blank/absent owner denies everyone (fail closed) | `Gate_WithNoOwnerConfigured_DeniesEveryone` | `DashboardOwnerGateTests.cs` |
| AC5 | The unauthenticated `local-dev` identity is not an owner of a real OID | `Gate_DoesNotTreatTheOfflineFallbackUserAsOwner` | `DashboardOwnerGateTests.cs` |
| AC6 | Owner match is case-insensitive | `Gate_MatchesTheOwnerIdCaseInsensitively` | `DashboardOwnerGateTests.cs` |
| AC7 | `/dashboard`, `/dashboard/` and a subresource all stay behind Easy Auth | `Exclusions_DoNotCoverAnyAuthenticatedPath` (3 rows) | `EasyAuthExclusionTests.cs` |
| AC8 | SW bypasses `/dashboard` before any `respondWith` | `ServiceWorker_BypassesTheDashboardBeforeCaching` | `PwaAssetsTests.cs` |
| AC9 | `CACHE` moved in both places that pin it | existing assertions | `PwaAssetsTests.cs`, `SearchAssetsTests.cs` |
| AC10 | Both silo hosts register the dashboard | `Hosts_RegisterTheOrleansDashboard` (source-text) | `ProjectLayoutTests.cs` |
| AC11 | The new Backend folder is pinned by name | `ExpectedFolders()` 23 → 24 | `ProjectLayoutTests.cs` |
| AC12 | **The dashboard is never mapped without the gate**, and the gate precedes static files | `Program_GatesTheOrleansDashboardBeforeServingIt` | `ProjectLayoutTests.cs` |
| AC13 | `MapOrleansDashboard` appears exactly once, in `DashboardRegistration.cs` | same as AC12 | `ProjectLayoutTests.cs` |
| AC14 | The collection-expression hazard still holds on 10.3.1 | `StateBuiltInsideAGrain_WithACollectionExpression_CannotBeReadBack` (updated message) | `GrainStorageSerializerTests.cs` |
| AC15 | Golden Cosmos documents still deserialize on 10.3.1 | the three existing golden-document tests | `GrainStorageSerializerTests.cs` |
| AC16 | Full suite green on 10.3.1 | whole suite, 1124+ | — |

**Note on AC3 — read before writing it.** AC3 proves the middleware covers a sub-path. It does **not**
prove the middleware-vs-endpoint-filter choice: the test maps its own stub endpoints, so a filter
applied to our own builder would propagate to them by construction and AC3 would stay green. That
choice is recorded as reasoning in "Resolved design decisions", not as a tested property, because
falsifying it needs the real package's endpoints and no test spins up a silo with it.
**AC12/AC13 are the load-bearing guards instead** — they are what stops a future edit mapping the
dashboard without the gate, which is the failure that actually matters. Mutation-check AC12 by
deleting the gate install and confirming it goes red.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time — do not write these from memory.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time (blocks) |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report | CI, soft |
| Branch policy | never commit to `main` | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

**Coverage is the gate at real risk here, and the plan owes it a straight answer.** Source-text
assertions (AC8–AC13) execute **no** production lines. `DashboardRegistration.cs` and the new
`Program.cs` lines are added `src/**/*.cs` lines that no test executes, so they land in the
denominator with zero hits against `DashboardOwnerGate.cs` alone in the numerator. That is why Step 6
splits the registration: the **gate-install half must be executed by the Step 7 host test**, as
`HindsightRegistrationTests` executes `AddHindsightMemory`. The `MapOrleansDashboard` call cannot be
executed without a silo and will remain uncovered — keep it to as few lines as possible. If the gate
still fails, the answer is a wiring test, never a threshold edit (`gate-failures-fix-dont-defer`).

## Out of scope

- **Any `excludedPaths` / Bicep / deploy-workflow / secret change.** If a step seems to need one, stop
  — the design is wrong.
- **A role system.** One principal compared against one config value; not authorization
  infrastructure, and it must not grow into one here.
- **Retro-fitting the owner gate to `/logs` or `/usage`.** Both stay cross-user; narrowing them is a
  separate ADR.
- **Generalising a "third-party UI on our origin" abstraction.** Two instances with different threat
  models is not a pattern yet.
- **Exercising the dashboard's own UI in an automated test.** Verified by spike this session and by
  Step 5a; no test spins up a real silo with it.
- **Bumping any other dependency family**, including the pinned SearXNG image.

## Review

Reviewed by the `critic` subagent before approval; 11 findings, all accepted and folded in above.
The four that changed the design rather than the wording:

1. **The "no CSP" argument rested on a false premise.** "Our own cluster's data" was wrong — reminder
   names are model-supplied and grain keys are client-supplied, so injection-influenced content does
   render on our origin. Replaced with an honest residual **and a go/no-go measurement (Step 5a)**.
2. **Nothing pinned that the dashboard is never mapped without the gate.** No test executes
   `Program.cs`, so a future edit dropping the `UseWhen` would compile and ship green. Added AC12/AC13
   on the `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware` exemplar.
3. **AC3's stated mutation check was unexecutable** — stub endpoints make the filter-vs-middleware
   distinction unobservable. Claim downgraded; AC12/AC13 carry the weight instead.
4. **The coverage gate claim was wrong** — source-text tests execute nothing, so
   `DashboardRegistration.cs` would be dead weight in the denominator. Step 6 now splits it so the
   gate half is executable.

Also folded in: the second `CACHE` assertion in `SearchAssetsTests` (Step 8 would have failed);
Step 4's dependency on Step 1; three Easy Auth rows instead of one; `OrdinalIgnoreCase` and the
`local-dev` negative test; gate ordering before `UseStaticFiles`; the AppHost third environment;
`dotnet add package` per hard rule 4; folder renamed `Dashboard/` → `Orleans/`; the decorative S3776
rationale removed; and the `CLAUDE.md` "23 directories" count added to the doc-debt step.

Two findings were resolved by measurement rather than by editing the plan: `AddDashboard()` opens
**no** listener (so the SearXNG sidecar cannot reach it on loopback), and it **does** install
`GrainProfilerFilter` on every grain call. Both are now recorded facts, items 4 and 5.

## Implementation divergences (2026-09-03)

Executed on `feature/orleans-dashboard` from `main @ bc170fa` — no drift; the plan's
`planned-against:` ref was HEAD exactly.

### 1. Step 5a ran AFTER Step 6, not before it
The plan orders the XSS go/no-go before the wiring step, but the measurement needs a running host
with the dashboard mounted, which Step 6 is what creates. Step 6 was done first, then 5a. No other
step was reordered, and 5a still gated Step 10's ADR as intended.

### 2. Step 5a's result: GO, and the plan's premise was half right
The plan's premise — that reminder names and grain keys are attacker-influenceable and do reach the
dashboard — is **confirmed** for reminder names and grain keys (both appear on `/dashboard/Reminders`).
It is **wrong** for the grains page: `DashboardCounters.simpleGrainStats` aggregates by grain **type**,
so primary keys never appear there.

Measured in headless Chrome over CDP: both payloads render inert (`&lt;img src=x …`), zero elements
created, zero inline handlers in the document, canary never fires, no console errors. The page is also
a pure client-side SPA — the server HTML carries no cluster data at all. **GO**, with the bounded
residual recorded in ADR 0062.

To plant the payload, `CannedChatClient.BuildReminderCall`'s hardcoded `"drink-water"` name was
temporarily replaced and **reverted afterwards** (`git checkout --`); it is not in the diff.

### 3. The `OtelConfigurationTests` diagnosis in the plan is WRONG, and the prescribed fix is incomplete
The plan (Context item 8) attributes
`AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline`'s failure to gen_ai spans from the four
live-telemetry classes that had not joined `[Collection("LiveTelemetry")]`, and states that adding them
fixes it.

**Adding them helped but did not fix it.** Measured: 5 of 6 plain runs green afterwards, and **0 of 2
green under `--collect:"XPlat Code Coverage"`** — which is the CI path and the condition the collection's
own doc names. Dumping the buffer on a failing run showed the contaminants were **not gen_ai spans**:

```
Source = Microsoft.AspNetCore, Name = PUT /api/conversations/{conversationId}/pin
Source = Microsoft.AspNetCore, Name = GET /api/conversations
```

The mechanism is one level up from `AddSource`: the test calls `ConfigureOpenTelemetry()`, which
subscribes to the **ASP.NET Core instrumentation**, and an `ActivityListener` is **process-global**. So
while that host is alive it captures every server span in the process, and the contaminators are **the
entire API test suite** — not three classes. No collection membership can ever fix it; adding more
classes there is the wrong lever.

**Fix applied:** the emptiness assertion is scoped to the source under test
(`s.Source == LiveTelemetryCaptureExtensions.OtelSourceName`). This is not a weakened assertion —
nothing in that test emits on `OtelSourceName`, and the one class that does (`MeterNamingTests`) is
serialised with it by the collection, so a span appearing there would still be a real defect. The four
`[Collection("LiveTelemetry")]` additions were kept: they are correct on their own terms and the
collection's doc already required them. That doc now carries the corrected diagnosis.

Result: **4/4 green under `--collect`**, where it was 0/2 before.

### 4. `Dashboard:OwnerOid` ships BLANK
The prerequisite Entra object id was not available; the user chose to leave it blank rather than have
a placeholder invented. Because the rule fails closed, the production dashboard **denies everyone**
until an operator fills it in — inert, not open. DevHost and the AppHost are unaffected (`local-dev`).

### 5. Small mechanical deviations
- `Program.cs` and DevHost's `Program.cs` needed `using Orleans.Dashboard;` for the silo-side
  `AddDashboard()`; the plan did not mention it.
- `dotnet add package` inserted the `PackageVersion` in the middle of the
  `System.Security.Cryptography.Xml` security-pin comment block, orphaning that comment. Moved into the
  Orleans block with a comment recording the community-package ban.
- `ProjectLayoutTests.ProjectDir()` hardcodes `src/`, so the AC10 theory takes repo-relative paths
  (DevHost lives under `tools/`).
- AC13's repo-wide sweep for `MapOrleansDashboard(` matched the assertion's own file; scoped to
  `src/` + `tools/`.
- The host test could not map `/dashboard` and `/dashboard/` as two literal routes
  (`AmbiguousMatchException` — ASP.NET treats them as one template); it maps a page route plus a
  catch-all, which is also the shape the real package uses.
- The CRLF gotcha bit once as predicted: a `sed -i` on `Directory.Packages.props` silently converted
  the file to LF. Reverted and redone with a line-ending-preserving edit; every new file was normalised
  to CRLF and checked with `file`.

### 6. Critic round (9 findings, all addressed)

One was **blocking** and would have failed CI: ADR 0062's own prose tripped
`check-doc-paths.mjs` (a `hard_block` job). Seven backticked tokens were read as repo paths —
`/dashboard/index.min.js`, `./index.min.js`, and three `querySelectorAll(...).length` table cells
(a table row is a "claim line", where a bare `name.ext` token is checked too). Rewritten so no token
looks like a path; gate green.

Two were **doc-integrity** findings, and both were the same real mistake: the corrected telemetry
diagnosis (divergence 3) had been written into the commit message, this spec and a C# XML doc, but
**not** into the two places a future reader actually greps. ADR 0062 still recorded the plan's
disproved theory, and `.agentic/gotchas.md`'s "live-telemetry test host captures from a
PROCESS-GLOBAL source" entry still asserted the collection fix works and was still dated 2026-08-28.
Both now carry the correction, and `CLAUDE.md`'s bullet was corrected too. This mattered more than
its severity suggests: the entry instructs the reader to add classes to the collection, which this
branch measured to be inert.

The rest: the coverage gate was measured and recorded in the ADR (83.3%, PASS — it had been omitted
entirely, which hard rule 3 forbids); the ADR now names the two ways to actually enable the feature
and admits prod pays the profiler cost while `OwnerOid` is blank; `Hosts_RegisterTheOrleansDashboard`
now asserts gate-before-static-files **ordering for DevHost too**, not just presence; AC14 regained
the specificity it lost by also asserting `<>z__`; the stale 1140/16 counts became 1141/17; and
`Program_TrustsForwardedProtoBeforeAnyOtherMiddleware` was refactored onto the new `StrippedSource`
helper instead of keeping a byte-identical copy.

### 7. The coverage gate needed two moves the plan did not anticipate
The plan predicted this gate was at risk and prescribed splitting `DashboardRegistration` so the gate
half is executable — necessary but **not sufficient**. It first measured **58.3%**. Two things closed it:

- **`MapOrleansDashboardUi` is coverable after all.** The plan assumed it needs a silo. It does not:
  the package throws a guard `InvalidOperationException` naming `AddDashboard` when its services are
  absent, so a test reaches the line without a silo — and pinning that guard is worth having, since
  mapping without `silo.AddDashboard()` is a startup failure rather than a compile error.
- **Coverlet counts comment AND blank lines inside a lambda as coverable.** A 6-line comment plus one
  blank line inside `builder.UseOrleans(silo => …)` added **6 uncovered lines** to the denominator on
  their own. Top-level comments are not counted. The comment moved to the package pin and the ADR,
  leaving a one-line trailing pointer. This is a general trap for any future feature registering
  inside that lambda, and it is now recorded in ADR 0062's Verification section.
