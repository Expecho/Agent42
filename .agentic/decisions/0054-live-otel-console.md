---
id: 0054
date: 2026-08-27
status: accepted
tags: [observability, opentelemetry, diagnostics, frontend, privacy]
---

# Title: A live OpenTelemetry console at `/logs`

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** `/logs` and `/api/diagnostics/*` are now **owner-only** (404 otherwise), and prompt and
> completion text from non-owners' spans is redacted before it reaches the OTLP collector or the live buffer. The
> "any signed-in user can read every user's conversations" exposure below is narrowed to owners, but the tier is
> **advisory**: any signed-in user can become an owner through the Copilot shell. Log records and URLs can still carry
> search queries.

> **Amended by [ADR 0077](0077-app-insights-export-content-stripped.md) (2026-09-19).** The Context
> below says "in production the only sink is an OTLP export". That is no longer true: traces and metrics
> also go to Application Insights when a connection string is configured. Chat content is stripped from
> the traces there, and logs never go there. The console itself is unchanged: it still sees full
> content, because its span processor runs before the stripping fan-out, which is registered last.

## Context

Agent42 emits traces, logs and metrics through OpenTelemetry, but there was **no in-app way to look at
any of it**. In production the only sink is an OTLP export to a collector outside the app
(`.github/workflows/deploy.yml`); offline there was no pipeline at all. The token-usage dashboard
(ADR 0031) reads *derived aggregates* — token counts rolled up per model, per tool, per user — and never
the raw signal, so "why did that turn take nine seconds" or "what did the model actually receive" had no
answer short of the external collector.

Four decisions were taken in the planning interview and are **not** revisited here:

1. **A standalone page at `/logs`**, not an SPA overlay. Rejected: a `#logsView` overlay like `/usage`
   (not URL-reachable), and shipping both.
2. **Show telemetry raw, and document the exposure.** Rejected: redact-by-default-with-reveal, and
   redact-always. See Consequences — this is the widest read surface in the app.
3. **An in-memory ring buffer only.** Rejected: persisting to a grain, and accepting OTLP over the wire.
4. **Wire capture into DevHost too**, so the feature is exercisable offline rather than prod-only.

## Decision

Add a standalone diagnostics console at `/logs` — three tabs (Traces / Logs / Metrics) polling a bounded
in-memory ring buffer filled by in-process OpenTelemetry capture components.

**No new datastore, container, secret, Cosmos container, grain, OTLP receiver, `excludedPaths` entry or
Bicep change.** The only infrastructure-shaped edits are two direct `PackageReference`s already flowing
transitively.

- `LiveTelemetryBuffer` — two independently bounded rings (spans, logs) plus a wholesale-replaced metric
  snapshot, read through a monotonic `long` cursor.
- `LiveSpanProcessor` / `LiveLogProcessor` (`BaseProcessor`, `OnEnd`-only) and `LiveMetricExporter`
  (`BaseExporter<Metric>` on its own reader).
- `AddLiveTelemetryCapture(builder, addInstrumentation)` — one entry point, called by both hosts.
- `AgentApi.Diagnostics.cs` — a new (15th) `AgentApi` partial exposing three reads.
- `wwwroot/logs/` — `index.html`, `logs.css`, one classic `logs.js`.

### This is consistent with ADR 0031, not a supersession

ADR 0031 rejected **receiving OTLP over the wire**. This plan adds no receiver: nothing external can push
telemetry in, and capture is entirely in-process. It reuses 0031's shape — an in-process `BaseProcessor`
with a bounded, drop-on-full, never-blocking transport — for a different question (raw signal, live)
than 0031's (derived aggregates, durable).

### Bounded by BYTES, not only by item count

This is the load-bearing implementation constraint and it is an out-of-memory bound, not tidiness.
`ChatClientFactory` sets **`EnableSensitiveData = true`**, so a gen_ai span carries
`gen_ai.input.messages` — **the entire conversation history serialised to JSON, on every single turn** —
plus `gen_ai.tool.definitions` (every MCP server and skill schema). A count-bounded 500-span ring on a
long RAG conversation is tens to hundreds of megabytes, inside a backend container capped at `1Gi` that
also shares its replica budget with the SearXNG sidecar.

So `Diagnostics:*` carries a per-tag-value cap **and** a whole-buffer byte budget (split evenly between
the two rings so a chatty log stream cannot evict the spans the console exists to show), with truncation
**marked** in the payload — the `WebToolLimits` / `SkillToolLimits` discipline. The same
`MaxItemsPerPoll` bounds the response, which is otherwise unbounded per poll; a caller-supplied `limit`
may narrow it, never raise it.

### Self-observation is filtered — and the obvious log filter does not work

Polling `/api/diagnostics/*` produces a server span **and** request log lines every tick, so without a
filter the buffer fills with the console watching itself.

The span half is easy: drop spans whose `url.path` / `http.route` starts with `/api/diagnostics`,
mirroring the `HealthEndpointPath` filter in ServiceDefaults.

**The log half cannot be keyed off the category.** The noise is
`Microsoft.AspNetCore.Hosting.Diagnostics` "Request starting"/"Request finished" — a **shared** category
every other request also uses, so filtering it would blind the console to all request logging. The filter
is keyed off the **request path** instead, reached two independent ways because neither is reliable at
the moment a record is written: the ambient `Activity`'s `url.path`/`http.route` tag, and the record's own
**`Path`** state attribute — measured, not assumed. Both request messages carry it (`Request starting`:
`Protocol, Method, ContentType, ContentLength, Scheme, Host, PathBase, Path, QueryString,
{OriginalFormat}`; `Request finished`: the same plus `ElapsedMilliseconds, StatusCode`).
**`RequestPath` is deliberately not consulted**: it exists only inside a formatted *scope* string
(`"RequestPath:/ RequestId:…"`), which this filter does not read, so listing it would have been dead code
that made the filter look more thorough than it is.

Both matches are **prefix** matches over an **allowlist of path-bearing keys**, and the allowlist is the
load-bearing half: it is why a `gen_ai.tool.call.result` quoting the diagnostics route — the content a
reader opened the console to see — is never examined at all. The looser `"{method} {route}"` form is
confined to an activity's **display name**, which is framework-generated and never carries model or web
content.

Measured offline (DevHost, the worst case — no `appsettings.json`, so the floor is `Information`): after
~15 diagnostics requests the console held **6 spans and 0 diagnostics spans**, and **63 logs of which 12
were `Hosting.Diagnostics` request records for genuine requests and 0 were diagnostics polls**. Both
halves hold: the console's own traffic is gone, and request logging generally survives.

**Metrics are deliberately not filtered this way.** The metric snapshot is fixed-size and replaced
wholesale, so there is no ring to flood — `http.server.request.duration` legitimately shows a
`/api/diagnostics/*` route dimension, and hiding it would be lying about the app's actual request mix.

### Single replica is a dependency, and it is already true

`infra/main.bicep` pins `minReplicas = maxReplicas = 1` (Orleans localhost clustering), so one in-memory
buffer is the whole cluster's view. **A future scale-out breaks this**: `/logs` would show whichever
replica happened to answer the poll, with a cursor that means nothing across replicas.

## Framework facts measured during this work

1. **`LogRecord` is POOLED and REUSED. Retaining one silently corrupts the buffer.** Re-measured in this
   MR's exact pipeline shape: two log calls captured by a `BaseProcessor<LogRecord>` that stored the
   references yielded `count=2 distinctRefs=1`, and *both* entries read back as the **second** message.
   Every field must be copied **inside `OnEnd`**, and `Attributes` materialised into a new list.
   `LogProcessor_DoesNotRetainThePooledRecord` fails against the naive version.
2. **`MetricType` has EIGHT members, not five** — the extra three being `ExponentialHistogram`,
   `LongSumNonMonotonic`, `DoubleSumNonMonotonic`. Confirmed live: of **28 instruments captured offline,
   11 were `LongSumNonMonotonic`**, including `dotnet.process.memory.working_set`,
   `dotnet.gc.last_collection.heap.size` and `dotnet.gc.last_collection.memory.committed_size`. A
   five-arm switch reads `NaN` for **39% of the Metrics tab**. A non-monotonic sum uses the *same*
   `GetSumLong()`/`GetSumDouble()` accessors — the bug is a missing arm, not a missing API. An
   unrecognised member is recorded as a **null value with a detail naming it**, never a silent `NaN`.
3. **A second `PeriodicExportingMetricReader` coexists with the OTLP one** and sees the full instrument
   set.
4. **`Activity` is NOT pooled** (`captured=6 distinctRefs=6`); it is projected anyway, to make the byte
   budget computable. **Spans arrive child-first**, so the viewer builds the tree from `parentSpanId` and
   never from arrival order.
5. **`/logs` 301-redirects to `/logs/`** under `UseDefaultFiles()`. Confirmed twice this MR — against
   real Kestrel (`301`, `Location: http://localhost:61326/logs/`) and in a real browser, which landed on
   `/logs/`. That is why the page's relative asset hrefs resolve.
6. **Calling `builder.Logging.AddOpenTelemetry` a second time is additive, not duplicating** — one
   provider, two processors, each record seen once. This is what makes capture safe in the Backend, where
   `AddServiceDefaults()` has already made the first call. Additivity is **not** assumed for
   *instrumentation*, which is why `addInstrumentation` is a flag rather than unconditional.
7. **The SDK's default metric export interval is 60s**, which leaves a *live* console's Metrics tab blank
   for a minute after start (measured: first export at ~60s). The capture reader therefore takes its own
   `Diagnostics:MetricExportIntervalMs`, defaulted to 10s — measured to produce the first export at ~9s.
   It exports in-process to a ring, so a shorter interval costs a projection and no I/O, and it does not
   touch the OTLP reader's interval.

## Consequences

### The exposure this creates (read this part)

**`/logs` is the widest read surface in the app.** `EnableSensitiveData = true` means gen_ai spans carry
**full prompt and completion text**; every span also carries `user.email` / `user.full_name` /
`user.roles` (ADR 0009/0010). The page is behind Easy Auth, but — like `/api/usage` (ADR 0031) and
`/api/config-health` (ADR 0029) — **there is no role system**, so **any** signed-in user can read **every**
user's complete conversations, plus document excerpts, street addresses (ADR 0012), calendar event titles
and shared-calendar ids (ADR 0053), and fetched web content. This is materially wider than 0031's
aggregates and than 0029's curated statuses. It was chosen deliberately for a two-user personal app.

**It also exposes incognito chats.** ADR 0034's guarantee is explicitly *"not persisted, not listed"* and
explicitly **not** "not in memory". An ephemeral conversation runs through the same `ChatCoreAsync` with
the same `EnableSensitiveData`, so `/logs` turns that in-memory residue into a **URL-addressable,
cross-user-readable** view of its full prompt and completion text. That is a new interaction between two
shipped features, and it is named here rather than left to be discovered.

Two existing protections keep holding, and neither is overstated:
`OpenTelemetry.Instrumentation.Http` redacts query strings by default (so the Google API key and the
user's coordinates stay out of `url.full`), and `CalendarSpanSuppressingHandler` suppresses the calendar
**HTTP client** span. **That handler does not protect calendar content**: the `execute_tool` span's
`gen_ai.tool.call.arguments` / `gen_ai.tool.call.result` carry event titles, locations and other people's
email addresses straight into the buffer. `/logs` must never weaken either protection — in particular it
must not set `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION` to "see more".

The page renders **everything through `textContent`/`createElement`, never `innerHTML`**, and ships no
markdown or sanitiser library. It paints attacker-influenceable content by construction (ADR
0011/0027/0049), so it is the one page in the app that must never interpret what it shows.

### The Logs tab is legitimately empty in production

`appsettings.json` sets `Logging:LogLevel:Default` to `Warning`, and `ILogger` category filtering runs
**before** the OpenTelemetry logging processor — so Information/Debug records never reach
`LiveLogProcessor` at all and **no buffer setting can recover them**. Do not "fix" this by lowering the
level: that changes what the app ships to the OTLP collector too. Instead both the API
(`LogsResponse.EffectiveMinimumLevel`) and the tab report the effective floor and name
`Logging__LogLevel__Default`, so an empty tab reads as *correct* rather than *broken*.

### Other consequences

- **Nothing is persisted.** The buffer is lost on restart, redeploy and scale event. `/api/usage` remains
  the durable view; `/logs` is the live one.
- **`excludedPaths` is unchanged** — all three API routes and the static page stay behind Easy Auth.
  `EasyAuthExclusionTests` gains six rows so that is a checked fact rather than an omission.
- **Two service-worker residuals, deliberately not fixed here.** `sw.js`'s stale-while-revalidate branch
  caches every same-origin non-navigation request, so the script served at /logs/logs.js *is*
  runtime-cached and `activate` only purges when `CACHE` changes — a future `/logs` edit is served
  stale to the installed PWA until some other MR bumps `CACHE`. And an offline navigation to `/logs`
  falls back to `caches.match("/index.html")`, painting the chat shell at that URL. Both are acceptable
  for a diagnostics page that is useless offline; neither justifies touching `sw.js`. One classic script
  removes the *cross-module* skew hazard, not skew in general: `index.html` is a navigation and so
  network-first, while `logs.js`/`logs.css` are stale-while-revalidate, so fresh HTML can still meet a
  stale script until some MR bumps `CACHE`.
- **DevHost gains an OpenTelemetry pipeline** (instrumentation, no exporter). This falsifies the
  parenthetical *"empty offline (DevHost has no OTel pipeline)"* in ADR 0031 and CLAUDE.md — both corrected
  in this MR. The usage dashboard is still empty offline, because `UsageSpanProcessor` is Backend-only.

## Verification

The manual browser leg **was** run — headless Chrome over CDP against DevHost — and these are the observed
values, not estimates:

| Observation | Measured |
|---|---|
| `/logs` in a real browser | requested `/logs`, landed on `/logs/` |
| Traces tab | 6 traces rendered, duration bars, span detail expands to 14 rows / 2 sections |
| Logs tab | 63 rows, newest first, `Hosting.Diagnostics` request logs present for genuine requests |
| Metrics tab | 49 rows across 28 instruments, grouped by meter, histograms showing `sum · count=N` |
| Self-observation | 0 diagnostics spans and 0 diagnostics log records after ~15 polls |
| Effective level | `log capture floor: Information (Logging__LogLevel__Default)` |

**Two honest residuals the leg cannot close.** DevHost's canned chat client emits **no gen_ai spans** —
the captured sources were `Microsoft.AspNetCore` only, with zero `gen_ai` tags — so **prompt-content
capture, which is the whole PII consequence above, is verifiable only in production**. For the same reason
every captured trace was single-span (`spans with a parent: 0`), so the parent/child **tree rendering** is
covered by unit test (`SpanProcessor_CapturesParentChildLinkage`) but was not exercised visually.

## Alternatives considered

- **An OTLP receiver in-app.** Rejected in ADR 0031 and not revisited: it turns a read-only diagnostic
  into an ingest surface.
- **Persisting to a grain.** Rejected: raw spans carrying whole conversations against the 2 MB Cosmos item
  cap (ADR 0004) is the failure mode ADR 0016 already documents, for data with a useful life of minutes.
- **Redact-by-default with a reveal toggle.** Rejected by the user (decision 2). A redacted diagnostics
  console cannot answer the question people open it for, and the reveal would be one click away regardless.
- **An SPA overlay like `/usage`.** Rejected (decision 1): not URL-reachable, and it would put a page that
  must never call `innerHTML` inside a bundle whose whole job is rendering markdown.
- **Appending the endpoints to `MapTelemetryEndpoints`.** Rejected: `Map*Endpoints` methods are exactly
  what historically blew the S3776 cognitive-complexity gate (ADR 0050), because the metric sums
  independent registration lambdas. A new endpoint group gets a new partial.
