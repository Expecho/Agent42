# Plan — a live OpenTelemetry console at `/logs`

planned-against: main @ a74a34c

## Overview

Agent42 emits traces, logs and metrics through OpenTelemetry but there is **no in-app way to look at
them**. In production the only sink is an OTLP export to `http://home.peterbons.nl:4317` (set in
`.github/workflows/deploy.yml`); offline there is no pipeline at all. The token-usage dashboard
(ADR 0031) reads *derived aggregates*, never the raw signal.

Add a **standalone diagnostics console at `/logs`** — three tabs (Traces / Logs / Metrics) polling a
bounded in-memory ring buffer that is filled by in-process OTel capture components. No new datastore,
no new container, no new secret, no OTLP receiver, no Bicep change.

## Affected areas

Verified against the tree at `a74a34c`.

**New — `src/Agent42.Backend/Telemetry/`** (existing feature folder, ADR 0040; no new folder):
- `LiveTelemetryBuffer.cs` — the three ring buffers + sequence cursor
- `LiveTelemetryContracts.cs` — `CapturedSpan` / `CapturedLog` / `CapturedMetric` + response records
- `LiveSpanProcessor.cs` — `BaseProcessor<Activity>`
- `LiveLogProcessor.cs` — `BaseProcessor<LogRecord>`
- `LiveMetricExporter.cs` — `BaseExporter<Metric>`
- `LiveTelemetryCaptureExtensions.cs` — the one registration entry point both hosts call
- `AgentApi.Diagnostics.cs` — a **new** `AgentApi` partial (`MapDiagnosticsEndpoints`)

**New — `src/Agent42.Backend/wwwroot/logs/`** (mirrors `wwwroot/shared/`, ADR 0035):
- `index.html`, `logs.css`, `logs.js`

**Modified:**
- `src/Agent42.Backend/Program.cs` — one `AddLiveTelemetryCapture` call
- `tools/Agent42.DevHost/Program.cs` — minimal OTel providers + the same call
- `src/Agent42.Backend/Api/AgentApi.cs` — register `MapDiagnosticsEndpoints`, **and fix its
  `<summary>`'s "all 44 templates are distinct"** (three new templates)
- `src/Agent42.Backend/appsettings.json` — a `Diagnostics` section with buffer + byte budgets,
  carrying a `"//"` documentation block like `Push` / `ContextScrape` / `GoogleCalendar` do
- `tools/Agent42.DevHost/Agent42.DevHost.csproj` — direct `PackageReference`s for the
  instrumentation it calls. Transitive flow would compile, but the repo's own precedent is explicit:
  `Agent42.Backend.csproj` already carries `OpenTelemetry.Instrumentation.AspNetCore` with the
  comment *"Direct reference (transitive via ServiceDefaults) so … is unambiguously in scope"*.
  Central Package Management means no version is written either way.
- **`tests/Agent42.Tests/ProjectLayoutTests.cs`** — a **15th row** in `AgentApiParts()` (it is a
  hardcoded `TheoryData`, **not** a directory scan) plus its `<summary>` "fourteen" → "fifteen"
- **`tests/Agent42.Tests/EasyAuthExclusionTests.cs`** — new `[InlineData]` rows on the existing
  `Exclusions_DoNotCoverAnyAuthenticatedPath` theory (the ADR 0053 shape)
- `tests/Agent42.Tests/OtelConfigurationTests.cs` — a case covering capture + the dual exporters
- `.agentic/decisions/0054-*.md` (new ADR), `.agentic/gotchas.md`
- **`CLAUDE.md` — two edits, not one:** the new repo-note bullet, **and** the ADR 0040 bullet's
  "`MapAgentApi` is split into **14 per-feature partials**" → 15.
- **`.agentic/decisions/0031-token-usage-dashboard.md` + the `CLAUDE.md` ADR 0031 bullet** — both say
  the usage dashboard is *"empty offline (DevHost has no OTel pipeline)"*. After step 3 DevHost
  **has** one; the dashboard stays empty (the usage processor is Backend-only) but the stated
  *reason* becomes false. Hard rule 2: fix the claim in this MR, do not leave it to drift.

All new `src/Agent42.Backend/**` files declare the flat `namespace Agent42.Backend` — folders and
namespaces disagree on purpose (ADR 0039/0040), and `ProjectLayoutTests.EveryFileStaysInTheFlatNamespace`
fails otherwise.

**Deliberately NOT modified:** `infra/modules/container-app.bicep` (`excludedPaths` stays the four
ADR 0042 entries), `src/Agent42.Backend/wwwroot/sw.js`, `index.html`, `styles.css`,
`src/Agent42.Backend/wwwroot/js/**`, `Directory.Packages.props`, `src/Agent42.ServiceDefaults/Extensions.cs`.

## Context & constraints

### User decisions from the interview (do not relitigate)

1. **Standalone page at `/logs`**, not an SPA overlay. Rejected: a `#logsView` overlay like `/usage`
   (not URL-reachable), and "both".
2. **Show telemetry raw; document the exposure.** Rejected: redact-by-default-with-reveal, and
   redact-always. This is the single widest read surface in the app — see the PII note below.
3. **In-memory ring buffer only.** Rejected: persisting to a grain, and an OTLP receiver (which would
   have needed an ADR 0031 supersession).
4. **Wire capture into DevHost too**, so the feature is exercisable offline rather than prod-only.

### Facts measured during planning — trust these, do not re-derive

Spikes were run against the repo's pinned `OpenTelemetry 1.18.0`. Two of them **change the
implementation**; a naive version is wrong in a way no compiler catches.

1. **`LogRecord` IS POOLED AND REUSED. Retaining one silently corrupts the buffer.** Measured: two
   log calls captured by a `BaseProcessor<LogRecord>` that stored the `LogRecord` references yielded
   `count=2 distinctRefs=1`, and *both* entries read back as the **second** message. Every field —
   `CategoryName`, `LogLevel`, `FormattedMessage`, `Attributes`, `Exception`, timestamp, trace ids —
   must be **copied into a POCO inside `OnEnd`**, and `Attributes` must be materialised into a new
   list (it is the record's own buffer). Values copied *inside* `OnEnd` were correct.
2. **`MetricType` has EIGHT members, not five:** `LongSum, DoubleSum, LongGauge, DoubleGauge,
   Histogram, ExponentialHistogram, LongSumNonMonotonic, DoubleSumNonMonotonic`. A switch covering
   only the obvious five reads `NaN` for `dotnet.process.memory.working_set`,
   `dotnet.gc.last_collection.heap.size` and `dotnet.gc.last_collection.memory.committed_size` —
   measured, all three are `LongSumNonMonotonic`. Non-monotonic sums are read with the same
   `GetSumLong()` / `GetSumDouble()` accessors; the bug is a missing switch arm, not a missing API.
3. **A second `PeriodicExportingMetricReader` alongside the existing OTLP one works.** Measured: a
   custom `BaseExporter<Metric>` on a second reader captured 19 metric streams (including the
   `AddRuntimeInstrumentation` set) while a first reader was also registered. `MetricPoint` is
   enumerated `foreach (ref readonly var p in metric.GetMetricPoints())`.
4. **`Activity` is NOT pooled** (`captured=6 distinctRefs=6`) and is safe to read after `OnEnd` —
   but project it anyway, to bound buffer memory. Two further observations from the same run:
   **capture works with NO exporter registered at all** (which is what makes the DevHost leg
   possible), and **spans arrive child-first** — a child completes before its parent, so the viewer
   must build the trace tree from the buffer and must not assume arrival order.
5. **`/logs` 301-redirects to `/logs/`** under the repo's `UseDefaultFiles()` + `UseStaticFiles()`
   pair (measured against a real Kestrel server: `/logs` → `301`, `Location: /logs/`; `/logs/` →
   `200 text/html`; `/logs/logs.css` → `200 text/css`). It comes from
   `DefaultFilesOptions.RedirectToAppendTrailingSlash`, which is on by default and is the same
   middleware `StaticAssetsServerFixture` mounts — so the TestServer reproduces it, and its client
   does **not** auto-follow redirects. AC1 therefore asserts the `301` + `Location` on `/logs` and
   the `200` on `/logs/` as two separate requests. Because the browser lands on `/logs/`, relative
   asset paths inside the page resolve correctly; `ShareUrl` already mints `/shared/#{token}`
   **with** the trailing slash for the same reason.
6. **Calling `builder.Logging.AddOpenTelemetry` a SECOND time is additive, not duplicating.**
   Measured: with two calls each adding one processor, a single log line reached each processor
   **exactly once** (`A saw 1, B saw 1`) — one `OpenTelemetryLoggerProvider`, two processors. This is
   what makes step 3 safe in the Backend, where `AddServiceDefaults()` has already made the first
   call. The same additive property is **not** assumed for *instrumentation* registrations — see the
   `addInstrumentation` flag in step 3.

### The exposure this creates (the headline consequence for the ADR)

`src/Agent42.Backend/Models/ChatClientFactory.cs:186` sets **`EnableSensitiveData = true`** on the
chat client's OTel wrapper, so **gen_ai spans carry full prompt and completion text**. Every span
also carries `user.email` / `user.full_name` / `user.roles` (ADR 0009/0010). `/logs` is behind Easy
Auth but, like `/api/usage` (ADR 0031) and `/api/config-health` (ADR 0029), there is **no role
system** — so **any** signed-in user sees **every** user's complete conversations, plus document
excerpts, street addresses (ADR 0012), calendar data (ADR 0053) and web-fetch content. This is
materially wider than ADR 0031's aggregates and wider than ADR 0029's curated statuses. The user
chose this deliberately for a two-user personal app; the ADR must say so plainly rather than bury it.

**It also exposes incognito chats.** ADR 0034's guarantee is explicitly *"not persisted, not listed"*
and explicitly **not** "not in memory". An ephemeral conversation runs through the same
`ChatCoreAsync` with the same `EnableSensitiveData`, so `/logs` turns that in-memory residue into a
**URL-addressable, cross-user-readable** view of its full prompt and completion text. That is a new
interaction between two shipped features and must be named in ADR 0054's consequences, not left for
someone to discover.

Two existing protections must keep holding — but **do not overstate them**:
`OpenTelemetry.Instrumentation.Http` redacts query strings by default (so the Google API key and the
user's coordinates stay out of `url.full`), and `CalendarSpanSuppressingHandler` suppresses the
calendar **HTTP client** span. That handler does **not** protect calendar *content*: the
`execute_tool` span's `gen_ai.tool.call.arguments` / `gen_ai.tool.call.result` carry event titles,
locations and shared-calendar ids (other people's email addresses) straight into the buffer. The ADR
must say that plainly rather than imply calendar data is covered. `/logs` must not weaken either
protection — in particular it must not set
`OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` to "see more".

### Governing ADRs and gotchas to read before starting

- `.agentic/decisions/0031-token-usage-dashboard.md` — **the shape to copy**: an in-process
  `BaseProcessor` + bounded drop-on-full transport, never an OTLP receiver (explicitly rejected
  there). This plan is *consistent* with 0031, not a supersession — 0031 rejected receiving OTLP
  over the wire, and this plan does not add one.
- `.agentic/decisions/0009-*.md` / `0010-*.md` — what is on a span and why (`user.*`,
  `RequestContext` not `Baggage`). `/logs` must not add any new tag or propagation channel.
- `.agentic/decisions/0035-read-only-share-links.md` +
  `.agentic/decisions/0042-wildcard-easy-auth-exclusions-and-forwarded-proto.md` — `wwwroot/shared/`
  is the standalone-static-page exemplar, and 0042 is why **nothing** gets added to `excludedPaths`:
  `/logs` is authenticated-only, so leaving the list untouched is the fix, not an omission.
- `.agentic/decisions/0040-feature-folders-in-backend-project.md` — a new `AgentApi` partial must be
  called from the composition root or every endpoint in it silently vanishes.
- `.agentic/decisions/0050-measurable-quality-gates.md` — `Map*Endpoints` partials are the methods
  that historically blew the S3776 cognitive-complexity gate, because the metric sums independent
  registration lambdas. **This is why the diagnostics endpoints get their OWN partial** rather than
  being appended to `MapTelemetryEndpoints`.
- Gotchas by heading: *"The usage dashboard counts tokens from `chat` spans ONLY"*; *"OTel redacts an
  HttpClient span's query string by default"*; *"A frontend module unreachable from `main.js` is
  invisible to every source-text guard"* (ADR 0041 — `wwwroot/logs/` is exactly such an orphan and
  needs its own asset test); *"Service worker must stay network-first for navigations and bypass
  /api"*; *"Minimal-API handlers returning a bare string serve text/plain, not JSON"*.

### Design constraints that follow

- **`OnEnd` / `Export` must never block, allocate unboundedly, or throw.** Same rule as
  `UsageSpanProcessor` — telemetry capture must never back-pressure a chat turn. Ring append under a
  short lock; drop oldest on overflow.
- **Self-observation must be filtered — and the obvious log filter does NOT work.** Polling
  `/api/diagnostics/*` creates a server span *and* log lines on every tick, which would fill the
  buffer with the console watching itself. The **span** side is easy: drop spans whose `http.route` /
  `url.path` starts with `/api/diagnostics`, mirroring the `HealthEndpointPath` filter already in
  `src/Agent42.ServiceDefaults/Extensions.cs`.
  The **log** side cannot be filtered by category: the noise is
  `Microsoft.AspNetCore.Hosting.Diagnostics` "Request starting/finished", a **shared** category every
  other request also uses — filtering it would blind the console to all request logging. Key the log
  filter off the record's **`TraceId`/`SpanId`** instead (matching the span the span-processor just
  dropped), or off the ambient `Activity`'s route. This matters most **offline**: `tools/Agent42.DevHost/`
  has **no `appsettings.json` at all**, so its default minimum is `Information` with no
  `Microsoft.AspNetCore` override — a 2-second poll would flood the Logs tab, which is exactly the
  failure this constraint exists to prevent, in the one environment where the feature gets
  demonstrated (step 6).
- **`wwwroot/logs/logs.js` must be ONE classic script, not an ES-module graph.** The service worker
  stale-while-revalidates static assets (`sw.js`, the branch after the navigate branch), so a
  multi-module page would carry the ADR 0041 version-skew hazard (`SyntaxError`, blank page) with no
  `CACHE` bump to protect it — and this MR deliberately does not touch `wwwroot/js/`, so no bump is
  due. `wwwroot/shared/viewer.js` is the precedent: a single classic script.
- **Endpoints resolve the buffer via `IServiceProvider`, not a constructor param.** The
  `/api/speech` + `/api/config-health` pattern: `MapAgentApi` infers every endpoint's parameters up
  front, so an unresolvable param breaks routing for the *whole host* (the gotcha in ADR 0029's
  entry). Return an empty payload rather than 500 when capture is absent.
- **Single replica is load-bearing and already true:** `infra/main.bicep:136,141` pins
  `minReplicas = maxReplicas = 1` (Orleans localhost clustering). An in-memory buffer would only show
  one replica's view otherwise. Say this in the ADR so a future scale-out knows what it breaks.
- **The rings must be bounded by BYTES, not just by item count — this is a real OOM risk, not a
  tidiness point.** With `EnableSensitiveData = true`, the sensitive content rides as **span tags**
  (not events): `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`,
  `gen_ai.tool.definitions`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`.
  `gen_ai.input.messages` is **the entire conversation history serialised to JSON on every single
  turn**, and `gen_ai.tool.definitions` carries every tool schema (MCP servers + uploaded skills).
  A count-bounded 200-span ring on a long RAG conversation is tens to hundreds of MB — inside a
  backend container capped at `containerMemory = '1Gi'` (`infra/main.bicep`) that also shares its
  replica budget with the SearXNG sidecar (the *"Container Apps bills the per-replica total across
  ALL containers in the replica"* gotcha).
  So `Diagnostics:*` carries **both** a max item count **and** a per-tag-value cap plus a
  whole-buffer byte budget, with truncation marked in the payload — the shape this repo already uses
  everywhere an unbounded value is retained (`WebToolLimits.MaxTotalPageTextLength`,
  `SkillToolLimits`, the truncation wrappers in the *"An agent tool's RETURN value is persisted"*
  gotcha). The same cap bounds the `/api/diagnostics/traces` response, which is otherwise unbounded
  per poll. Config values are never hardcoded at the call site.
- **The Logs tab will look EMPTY in production unless the reader knows why.**
  `src/Agent42.Backend/appsettings.json` sets `Logging:LogLevel:Default: "Warning"` (and
  `Microsoft.AspNetCore: "Warning"`). `ILogger` category filtering runs **before** the OTel logging
  processor, so `Information`/`Debug` records never reach `LiveLogProcessor` at all — no buffer
  setting can recover them. Do **not** "fix" this by lowering the level in `appsettings.json`: that
  changes what the app ships to the OTLP collector too. Instead the Logs tab must show the currently
  effective minimum level and say that raising verbosity is a configuration change
  (`Logging__LogLevel__Default`), so an empty tab reads as *correct* rather than *broken*. Record it
  in the ADR's consequences.

## Steps

**All steps are sequential.** Steps 4 and 5 look independent but are not: step 5's asset test asserts
against step 4's endpoint paths, and both edit files in the same checkout. Running them as two
concurrent write-capable subagents would need `isolation: "worktree"` (CLAUDE.md's working
agreement), which is not worth it for two coupled steps — so no step here is marked parallelizable.

### Step 1 — Contracts + the ring buffer

Files: `src/Agent42.Backend/Telemetry/LiveTelemetryContracts.cs`,
`src/Agent42.Backend/Telemetry/LiveTelemetryBuffer.cs`.

`CapturedSpan` (seq, timestamp, traceId, spanId, parentSpanId, name, kind, durationMs, status,
statusDescription, source, tags as an ordered name/value list), `CapturedLog` (seq, timestamp,
category, level, formatted message, exception text, traceId, spanId, attributes, scopes),
`CapturedMetric` (name, unit, description, type, temporality, points as tag-set/value pairs).
Response records wrapping each list plus the next cursor. `CapturedSpan` also projects
`Activity.Events`, or an exception recorded as an event is silently dropped (status and description
alone are not enough to debug one).

`LiveTelemetryBuffer`: a singleton with three independently-bounded rings, a **monotonic** `long`
sequence per signal, `Append`, and `Since(seq, limit)`. Metrics are a **snapshot replaced wholesale**
each export interval, not a ring — the viewer wants current values.

Verify: `dotnet test Agent42.slnx --filter LiveTelemetryBufferTests`.

### Step 2 — The three capture components

Files: `src/Agent42.Backend/Telemetry/LiveSpanProcessor.cs`, `LiveLogProcessor.cs`,
`LiveMetricExporter.cs`.

- `LiveSpanProcessor : BaseProcessor<Activity>` — `OnEnd` only (like `UsageSpanProcessor`, and
  unlike `UserTelemetrySpanProcessor`'s `OnStart`); projects and appends; drops `/api/diagnostics*`.
- `LiveLogProcessor : BaseProcessor<LogRecord>` — **copies every field inside `OnEnd`** (measured
  fact 1). Do not store the `LogRecord`. Materialise `Attributes` and the scope output into new
  lists before returning.
- `LiveMetricExporter : BaseExporter<Metric>` — projects the whole `Batch<Metric>` into a fresh
  snapshot inside `Export` and swaps it into the buffer. The `MetricType` switch must cover **all
  eight** members (measured fact 2); an unknown member is recorded with a null value, never silently
  `NaN`.

Verify: `dotnet test Agent42.slnx --filter "LiveLogProcessorTests|LiveSpanProcessorTests|LiveMetricExporterTests"`.

### Step 3 — Registration extension + both hosts

Files: `src/Agent42.Backend/Telemetry/LiveTelemetryCaptureExtensions.cs`,
`src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs`.

`AddLiveTelemetryCapture(this IHostApplicationBuilder builder, bool addInstrumentation = false)`:
registers the buffer singleton, adds the log processor via `builder.Logging.AddOpenTelemetry`, and
adds the span processor + the metric reader via `builder.Services.AddOpenTelemetry()`.

- **Backend** calls it with `addInstrumentation: false` — `AddServiceDefaults()` already registered
  AspNetCore/Http/Runtime instrumentation, and registering them twice risks duplicate spans.
- **DevHost** calls it with `addInstrumentation: true`, which adds `AddSource("Agent42.Backend")`,
  `AddAspNetCoreInstrumentation`, `AddHttpClientInstrumentation`, `AddRuntimeInstrumentation` and
  **no OTLP exporter**. Do **not** make DevHost call `AddServiceDefaults()`, which would also pull in
  resilience handlers, health checks and service discovery it deliberately lacks.
- **`AddLiveTelemetryCapture` must set `IncludeFormattedMessage = true` and `IncludeScopes = true`
  itself.** Today those live inside `ConfigureOpenTelemetry`
  (`src/Agent42.ServiceDefaults/Extensions.cs`), which DevHost never calls — so without this, offline
  `LogRecord.FormattedMessage` is **`null`** and scopes yield nothing, and step 6 would be signing
  off on a Logs tab full of blank rows. They are idempotent options, so setting them in both places
  is harmless in the Backend.
- The two calls differ, so this is *shared wiring*, not identical wiring — see AC17's wording.

Verify: `dotnet build Agent42.slnx`, then `dotnet run --project tools/Agent42.DevHost` starts and
serves `/` (the offline UI must be unchanged).

### Step 4 — API endpoints

Files: `src/Agent42.Backend/Telemetry/AgentApi.Diagnostics.cs` (new partial),
`src/Agent42.Backend/Api/AgentApi.cs` (register `MapDiagnosticsEndpoints`),
`tests/Agent42.Tests/ProjectLayoutTests.cs` (15th `AgentApiParts()` row + summary text),
`tests/Agent42.Tests/EasyAuthExclusionTests.cs` (new `[InlineData]` rows).

- `GET /api/diagnostics/traces?since=&limit=`
- `GET /api/diagnostics/logs?since=&limit=&minLevel=`
- `GET /api/diagnostics/metrics`

Behind Easy Auth, **never** `excludedPaths`. Global/cross-user by design (no `GetUserId` scoping) —
same posture as `/api/usage`, and the reason the ADR's consequence section exists. Buffer resolved
optionally from `IServiceProvider`; absent → empty payload, not 500. `limit` is clamped server-side.
`Results.Json`, never a bare string (that gotcha).

Verify: `dotnet test Agent42.slnx --filter DiagnosticsApiTests`.

### Step 5 — The `/logs` page

Files: `src/Agent42.Backend/wwwroot/logs/index.html`, `logs.css`, `logs.js`.

Three tabs. **Traces**: group spans by `traceId`, build a parent/child tree from `parentSpanId`
(spans arrive child-first — measured fact 4), render duration bars, expand a span to see all tags.
**Logs**: level-filtered stream, newest first, expandable attributes/scopes, click a `traceId` to
jump to that trace. **Metrics**: instrument table grouped by meter with current value(s) per tag set.
Shared controls: live/pause toggle, poll interval, clear, and a text filter. Poll incrementally with
the `since` cursor so the page is cheap.

Style: reuse the existing amber/phosphor tokens by **copying** the ones needed into `logs.css`
(`wwwroot/shared/viewer.css` is the precedent — a standalone page does not import `styles.css`).
Asset hrefs may stay relative: `/logs` 301s to `/logs/` (measured fact 5).

`LogsConsoleAssetsTests` joins `[Collection("StaticAssets")]` and uses `StaticAssetsServerFixture.Client`
directly (`GET /logs/`, `GET /logs/logs.js`) — it must **not** use `GetAppScriptAsync()`, which walks
the `wwwroot/js` import graph from `main.js` and will never see this page. Adding files under
`wwwroot/logs/` does not affect `ServiceWorkerShell_ListsExactlyTheModuleDirectory` (it compares only
the `"/js/…"` entries against the `wwwroot/js` directory) or `EveryFrontEndModule_IsReachableFromTheEntryPoint`
(scoped to `wwwroot/js`) — verified, which is why no `sw.js` change is due.

**Two service-worker residuals to record in the ADR rather than fix here.** `sw.js`'s
stale-while-revalidate branch caches **every** same-origin non-GET-navigation request, so `/logs/logs.js`
*is* runtime-cached, and `activate` only purges when `CACHE` changes — a single classic script removes
the cross-module skew hazard but **not** the stale-asset one, so a future `/logs` edit is served stale
to the installed PWA until some other MR bumps `CACHE`. And an offline navigation to `/logs` falls back
to `caches.match("/index.html")`, painting the chat shell at that URL. Both are acceptable for a
diagnostics page that is useless offline anyway; neither is a reason to touch `sw.js` in this MR.

Verify: `dotnet test Agent42.slnx --filter LogsConsoleAssetsTests`.

### Step 6 — Manual browser leg (headless Chrome over CDP against DevHost)

The repo norm since ADR 0044 — ADRs 0044/0048/0049/0052 all ran this leg and recorded measured
values; ADR 0043 did not and had to record two unverified residuals. Run DevHost, drive a headless
Chrome to `http://localhost:61326/logs`, send a chat turn, and record: the observed `/logs` →
`/logs/` redirect, span/log/metric counts appearing in each tab, and one screenshot per tab. Note
explicitly that DevHost's canned chat client emits no real gen_ai spans, so **prompt-content capture
is verifiable only in production** — record it as an honest residual rather than implying coverage.

### Step 7 — ADR + gotchas + CLAUDE.md

Run `/decide` for **ADR 0054**. It must record: the four user decisions and what was rejected; that
it is *consistent with* ADR 0031 rather than superseding it (no OTLP receiver); the single-replica
dependency; and the PII consequence in the blunt form above. Run `/gotcha` for the two traps that
cost real time — `LogRecord` pooling and the eight-member `MetricType` — plus the `wwwroot/logs/`
source-text-guard blind spot. Add a `CLAUDE.md` repo-note bullet in the house style.

### Step 8 — Gates

`dotnet build Agent42.slnx` (S3776 is an `error` since ADR 0051 — a method over 15 fails the build),
`dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, and
`node .agentic/scripts/check-new-code-coverage.mjs`. Fix failures in-branch; **never** edit
`.agentic/quality-gates.yaml` to make one pass.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | `/logs` is reachable and serves the console (`/logs` → 301 `/logs/`, `/logs/` → 200 html) | `LogsPage_IsServedAtBothSlashForms` | `tests/Agent42.Tests/LogsConsoleAssetsTests.cs` |
| AC2 | The page has all three tabs and their containers | `LogsPage_HasTracesLogsAndMetricsTabs` | same |
| AC3 | The page polls all three endpoints | `LogsPage_FetchesAllThreeDiagnosticsEndpoints` | same |
| AC4 | The page is a single classic script (no ES-module graph → no SW skew hazard) | `LogsScript_IsASingleClassicScript` | same |
| AC5 | Ring buffers bound size and drop oldest; sequence stays monotonic across wrap | `Buffer_DropsOldestAndKeepsSequenceMonotonic` | `tests/Agent42.Tests/LiveTelemetryBufferTests.cs` |
| AC6 | `since` returns only newer items and the next cursor | `Since_ReturnsOnlyNewerItems` | same |
| AC7 | **Log capture survives `LogRecord` pooling** — two logs yield two *distinct* messages | `LogProcessor_DoesNotRetainThePooledRecord` | `tests/Agent42.Tests/LiveLogProcessorTests.cs` |
| AC8 | Log attributes/scopes/exception are copied, not aliased | `LogProcessor_CopiesAttributesAndException` | same |
| AC9 | Spans project with trace/parent ids so a tree is reconstructable | `SpanProcessor_CapturesParentChildLinkage` | `tests/Agent42.Tests/LiveSpanProcessorTests.cs` |
| AC10 | The console's own `/api/diagnostics` traffic is not captured | `SpanProcessor_DropsItsOwnDiagnosticsSpans` | same |
| AC11 | **All eight `MetricType` members project a real value** — no `NaN` for non-monotonic sums | `MetricExporter_HandlesEveryMetricType` | `tests/Agent42.Tests/LiveMetricExporterTests.cs` |
| AC12 | A second metric reader coexists with an existing one (drive it with `ForceFlush()`, never a timing wait) | `MetricExporter_CoexistsWithAnotherReader` | same |
| AC12b | A span tag over the per-value cap is truncated and marked, and the buffer honours its byte budget | `Buffer_TruncatesOversizeTagsAndHonoursTheByteBudget` | `tests/Agent42.Tests/LiveTelemetryBufferTests.cs` |
| AC12c | The console's own polling **log** records are dropped (trace/span-id keyed, not category-keyed) | `LogProcessor_DropsItsOwnDiagnosticsRecords` | `tests/Agent42.Tests/LiveLogProcessorTests.cs` |
| AC13 | The three endpoints return the wire shape and honour `since` / `limit` / `minLevel` | `Diagnostics_ReturnsCapturedSignals` | `tests/Agent42.Tests/DiagnosticsApiTests.cs` |
| AC14 | Absent capture degrades to an empty payload, never 500, and routing still works | `Diagnostics_WithoutCapture_ReturnsEmptyNot500` | same |
| AC15 | The three endpoints **and** `/logs`, `/logs/`, `/logs/logs.js` stay behind Easy Auth — `excludedPaths` unchanged | new `[InlineData]` rows on `Exclusions_DoNotCoverAnyAuthenticatedPath` | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| AC16 | The new `AgentApi` partial is registered from the composition root | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` **after adding a 15th `AgentApiParts()` row** | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| AC17 | Both hosts call `AddLiveTelemetryCapture` (Backend without instrumentation, DevHost with) | `BothHosts_RegisterLiveTelemetryCapture` — a **source-text** guard in the `*AssetsTests` style, since nothing in the suite runs either `Program.cs` | `tests/Agent42.Tests/LiveTelemetryCaptureWiringTests.cs` |
| AC18 | Capture composes with the existing dual-exporter pipeline — the test must **call `AddLiveTelemetryCapture`**, not merely resolve providers, or `LiveTelemetryCaptureExtensions.cs` is an uncovered `src/**/*.cs` file against the 80% new-code gate | extend `OtelConfigurationTests` | `tests/Agent42.Tests/OtelConfigurationTests.cs` |
| AC19 | The Logs tab states the effective minimum level, so an empty tab is legible | `LogsPage_ShowsTheEffectiveMinimumLevel` | `tests/Agent42.Tests/LogsConsoleAssetsTests.cs` |

**AC15 and AC16 are the two rows most likely to be got wrong.** Neither guard is a directory scan:
`AgentApiParts()` is a hardcoded 14-row `TheoryData` (verified at `ProjectLayoutTests.cs:158`) whose
`<summary>` says "fourteen", and `Exclusions_DoNotCoverAnyAuthenticatedPath` is a hardcoded
`[InlineData]` theory. A new partial or a new route is **invisible to both** until a row is added by
hand — so "the existing test covers it" is false, and adding the rows is part of the work.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (`a74a34c`):

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff -U0` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — an **`error`** since ADR 0051, so `dotnet build` fails |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `outdated_packages` | report (soft) | CI |
| Lint | no diff | `dotnet format Agent42.slnx --verify-no-changes` |

Enforcement: `local: soft_warning`, `ci: hard_block`. No new package is added, so
`add_policy: latest-stable` does not apply — but if one becomes necessary, resolve the version from
the feed (`dotnet add package`) and pin it in `Directory.Packages.props`; never write a version from
memory.

## Out of scope

- **No OTLP receiver.** Nothing external can push telemetry in. ADR 0031 rejected that shape and this
  plan does not revisit it.
- **No persistence.** The buffer is lost on restart, redeploy and scale event. `/logs` is a *live*
  view; `/api/usage` remains the durable one.
- **No redaction, masking or reveal-toggle** — decision 2. Also no per-user filtering and no role
  system.
- **No `excludedPaths`, Bicep, secret, Cosmos container, grain or DI-mandatory change.**
- **No change to what the Backend emits** — no new span, tag, meter or propagation channel, and
  nothing new reaches the OTLP collector. `EnableSensitiveData` is left as-is and
  `CalendarSpanSuppressingHandler` keeps suppressing. (DevHost *does* gain instrumentation in step 3 —
  that is the point of user decision 4, and it exports nowhere.)
- **No log-level change.** `Logging:LogLevel:Default` stays `Warning` in production; the Logs tab
  reports the effective level instead of widening it.
- **No SPA integration** — no sidebar entry point, no `wwwroot/js/` change, no `sw.js` `CACHE` bump,
  no `index.html` / `styles.css` edit.
- **No trace export / download, no search across restarts, no alerting.**
- **Multi-replica correctness.** Single replica is assumed and pinned in Bicep today.

## Review

A `critic` pass ran against this spec before approval. Verdict: **not ready** — three blocking
findings plus doc/registration debt. It confirmed no ADR supersession is owed (ADR 0031 rejected an
OTLP *receiver*; this plan adds none), that `excludedPaths` untouched is correct, that the gate table
is copied accurately, and that the five measured framework facts and their evidence paths hold.

**Findings accepted and fixed in this spec:**

| Finding | Fix |
|---|---|
| **S1** — `AgentApiParts()` is a hand-written 14-row table, so the "existing guard" gives the new partial *zero* coverage; and the count appears in three more places | Added `ProjectLayoutTests.cs` to Affected areas with the 15th row; scheduled the `CLAUDE.md` "14 per-feature partials" and `AgentApi.cs` "44 templates" edits; rewrote AC16 |
| **S1** — the ring was bounded by item **count** only, while `gen_ai.input.messages` is the whole conversation JSON *per turn* inside a 1 GiB container shared with the SearXNG sidecar | Added an explicit per-tag cap + whole-buffer **byte** budget following the `WebToolLimits` / `SkillToolLimits` exemplar, bounding the poll response too, with AC12b |
| **S1** — the log self-filter as written cannot work: the noise is the shared `Microsoft.AspNetCore.Hosting.Diagnostics` category, and DevHost has **no `appsettings.json`**, so it defaults to `Information` and would flood | Filter is now keyed off `TraceId`/`SpanId`, not category; the DevHost flood risk is stated; added AC12c |
| **S2** — DevHost would capture logs with `FormattedMessage == null` and no scopes (those options live in `ConfigureOpenTelemetry`, which DevHost never calls) | `AddLiveTelemetryCapture` now sets `IncludeFormattedMessage` / `IncludeScopes` itself |
| **S2** — `appsettings.json` missing from Affected areas despite a config-driven constraint | Added, with the house `"//"` documentation-block shape |
| **S2** — step 3 falsifies "DevHost has no OTel pipeline" in ADR 0031 *and* `CLAUDE.md`, and contradicts this plan's own Out of scope | Both doc fixes scheduled (hard rule 2); Out of scope re-scoped to "what the **Backend** emits" |
| **S2** — incognito chats (ADR 0034) become cross-user readable, unnamed in the exposure section | Added to the exposure paragraph and to ADR 0054's required consequences |
| **S2** — steps 4/5 marked parallelizable without `isolation: "worktree"` | All steps are now sequential; they were coupled anyway |
| **S3** — AC17 said "identically" while step 3 passes different flags, and no test runs `Program.cs` | Reworded, and pinned as a source-text guard |
| **S3** — the SW *does* runtime-cache `/logs/*`, and offline `/logs` paints the chat shell | Both recorded as ADR residuals; still no `sw.js` change |
| **S3** — AC15 under-covered the static paths | Added `/logs`, `/logs/`, `/logs/logs.js` rows |
| **S3** — the "calendar is already defended" note overstated `CalendarSpanSuppressingHandler` | Corrected: it suppresses the HTTP span only; `execute_tool` tool arguments/results still carry calendar content |
| **S3** — DevHost relying on transitive packages diverges from the repo's own precedent | Direct `PackageReference`s added to the DevHost csproj |
| **S3** — no `ActivityEvent` projection; AC12 timing-dependent; AC18 wouldn't cover the new file | `CapturedSpan` projects events; AC12 uses `ForceFlush()`; AC18 must call the extension |

**Rejected: none.** Every finding was either a genuine gap or a genuine imprecision.

One residual the critic did not raise and that no fix removes: `LogRecord` pooling and the
eight-member `MetricType` are both **silent** failures — the naive implementation compiles, runs and
looks right. AC7 and AC11 exist specifically to fail on it, so they must be written to fail against a
naive implementation, not merely to pass against the correct one.

## Divergences from the plan (recorded at execution)

Executed on `feature/live-otel-console` from `main @ a74a34c` — the exact `planned-against` ref, so
there was no drift to reconcile. Every step was implemented as written except the following.

### 1. A fourth Telemetry file: `LiveTelemetryFilter.cs`

The plan listed six files for `src/Agent42.Backend/Telemetry/`. The self-observation filter is shared by
**both** the span processor and the log processor, and its log half is the subtle one (path-keyed, two
independent sources), so it got its own file rather than being duplicated or parked in an unrelated one.
`LiveTelemetryContracts.cs` also carries `DiagnosticsConfig` — the plan named the file "contracts + response
records" and this is the small config record the buffer is built from, following `PushConfig`'s shape.

### 2. The `AgentApi.cs` template count was already stale — corrected to the measured value, not to 44 + 3

The plan said to change *"all 44 templates are distinct"* to 47. **44 was already wrong before this MR.**
Measured on the branch: 56 `app.MapGet/Post/Put/Delete` registrations, all distinct in method + path (53
before the three added here). The claim now reads *"all 56 route registrations are distinct in method +
path"* — the phrasing made explicit so the next reader knows what is being counted. Hard rule 2: the code
is the source of truth. No guard was added to stop it drifting again; that would be a separate change.

### 3. Direct instrumentation `PackageReference`s went on the Backend, not on DevHost

The plan put them on `tools/Agent42.DevHost/Agent42.DevHost.csproj`. But `AddHttpClientInstrumentation` /
`AddRuntimeInstrumentation` are called from `LiveTelemetryCaptureExtensions.cs`, which lives in
**`Agent42.Backend`** — DevHost only passes a flag. The references therefore follow the code, matching the
precedent the plan itself cited (`Agent42.Backend.csproj` already carries
`OpenTelemetry.Instrumentation.AspNetCore` for exactly this reason). No version was written by hand.

### 4. A configurable metric export interval was added (`Diagnostics:MetricExportIntervalMs`)

Not in the plan. Measured during step 6: the SDK's default export interval is **60 s**, so a *live*
console's Metrics tab was blank for a full minute after start. The capture reader now takes its own
interval, defaulted to 10 s and read from config like every other budget — measured to produce the first
export at ~9 s. It exports in-process to a ring, so a shorter interval costs a projection and no I/O, and
the OTLP reader's own interval is untouched.

### 5. AC19's assertion is stronger than planned

`DiagnosticsApiTests` was expected to see the framework default. It reads the **real Backend
`appsettings.json`** (copied into the test output by the project reference), so it asserts the production
value `Warning` — the value that actually makes the Logs tab empty in production. The framework-default
fallback (`Information`, the DevHost case) is covered by a second test with the key blanked.

### 6. `LiveMetricExporterTests` covers all eight `MetricType` members with real SDK data

The first draft included a helper listing the handled members, which would only have tested its own list.
It was replaced: eight real instruments are driven through a real `MeterProvider` — including
`ExponentialHistogram`, which needs a `Base2ExponentialBucketHistogramConfiguration` **view** to exist at
all — and the test asserts the number of distinct observed types equals `Enum.GetValues<MetricType>().Length`.

### 7. `LiveMetricExporter` takes no `TimeProvider`

Drafted with one, then dropped: `TimeProvider` appears nowhere else in the repo, and introducing a DI
dependency for a snapshot timestamp is a shape this codebase does not use.

## Verification performed

- Steps 1–5 each ran their narrowest filter immediately, per the skill's per-step gating.
- **AC7 was falsified against a naive implementation before being accepted.** A throwaway processor that
  retained the `LogRecord` reference was run in this repo's exact pipeline and produced
  `count=2 distinctRefs=1`, both entries reading back as `"second message"` — reproducing the plan's
  measurement. The probe was deleted; the real test is written to fail the same way.
- **Step 6 (browser leg) was run**: headless Chrome over CDP against DevHost. Observed values are recorded
  in ADR 0054's Verification table, including the two residuals the leg cannot close (DevHost's canned chat
  client emits no gen_ai spans, so prompt-content capture and parent/child tree rendering are unverified in
  a browser).
- The plan's warning about the DevHost log flood was checked directly and held: after ~15 diagnostics
  polls the buffer contained **0 diagnostics spans and 0 diagnostics log records**, while **12
  `Hosting.Diagnostics` request records for genuine requests survived** — both halves of the property in
  one measurement.

## Critic pass on the diff (execution step 5)

A `critic` subagent reviewed the full diff before commit. It confirmed the AC→test table is fully
honoured (all 19 rows map to a real test in the named file) and that the ADR-contradiction checks come
back clean — ADR 0031 (no receiver added; the stale "DevHost has no OTel pipeline" reason corrected in
both places), 0034 (incognito named), 0035/0042 (`excludedPaths` untouched, six new rows), 0039/0040
(flat namespace, existing folder, 15th part registered from the composition root), 0041 (`wwwroot/js/`
untouched so no `CACHE` bump due), 0050 (own partial). It independently re-counted the 56 route
registrations and confirmed the corrected claim.

**Seven findings, all accepted and fixed. None rejected.**

| # | Finding | Fix |
|---|---|---|
| S2-1 | `SpanProcessor_KeepsSpansThatMerelyMentionTheDiagnosticsRoute` was **vacuous** — it set `gen_ai.tool.call.result`, a tag the filter never consults, so it passed against a match-anywhere implementation too. And `IsDiagnosticsPath` really was a substring check (`Contains(" " + prefix)`), contradicting the gotcha written beside it | Split the two rules: allowlisted path tags now match by **prefix only**, and the looser `"{method} {route}"` form is confined to an activity's **display name** (framework-generated, never model content). Test became a `[Theory]` with a case at position 0 that a naive prefix-on-every-tag check would drop, plus a case whose allowlisted value merely *contains* the route. `SpanProcessor_DropsItsOwnDiagnosticsSpans` was also renamed off `"GET /api/diagnostics"` — its display name already matched, so the tag assertion proved nothing |
| S2-2 | `RequestPath` in `PathAttributes` is **unreachable**, so "two independent ways" was really one and a half | **Measured the real shape** against DevHost rather than reasoning about it: `Path` **is** a state attribute on both request messages (key list recorded in the code, ADR and gotcha), while `RequestPath` exists only inside a formatted *scope* string the filter never reads. `RequestPath` deleted as dead code; ADR and gotcha corrected |
| S2-3 | The `addInstrumentation: true` branch — the whole of user decision 4 — was executed by **no test** | Added `AddLiveTelemetryCapture_WithInstrumentation_BuildsTheDevHostShapedPipeline`, which builds the providers on that path |
| S3-4 | **A BOM was introduced** into `src/Agent42.Backend/Program.cs` and `tools/Agent42.DevHost/Program.cs` (a scripted edit used `utf-8-sig`), making them the only two BOM-carrying `.cs` files in the repo — and unlike CRLF a BOM survives `git add` | Both stripped and verified against `a74a34c`. Two new files also lost a stray leading blank line that does not match the processor exemplar (it *is* the idiom for `AgentApi.*` partials, so `AgentApi.Diagnostics.cs` keeps it) |
| S3-5 | ADR 0054 claimed one classic script "keeps the *skew* hazard away" — it removes **cross-module** skew only; fresh network-first HTML can still meet a stale SWR script | Sentence corrected to say exactly that |
| S3-6 | The ADR 0031 annotation used the word "Superseded" in an accepted ADR's Decision section, which `README.md` reserves for real supersession | Reworded to "Corrected … this decision stands; only the parenthetical reason was overtaken" |
| S3-7 | `LogsResponse.Dropped` was returned and never rendered — and the log ring is the one likelier to evict first, since the byte budget is split evenly and a chatty log stream is what the split defends against. Separately, `EffectiveMinimumLogLevel` reads only `Logging:LogLevel:Default` while the label read as a uniform guarantee | Span and log drops are now tracked and reported **separately**. The effective-level caveat (per-category and per-provider overrides are not resolved) is documented on the endpoint, and the status line reads "default log level: … (Logging__LogLevel__Default)" rather than "log capture floor" |

After the fixes the browser leg was **re-run** (headless Chrome over CDP) to confirm the page still
renders all three tabs and the revised status line: `default log level: Information
(Logging__LogLevel__Default)`, 6 traces / 63 logs / 51 metric rows, span detail expanding to 14 rows.
