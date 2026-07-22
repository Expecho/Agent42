# Plan — Token-consumption / LLM telemetry dashboard

planned-against: main @ e920707

## Overview

Add an in-app **usage dashboard** to Agent42 that reproduces the **data and visual style** of
the sibling reference repo `Z:\Sources\AI.Telemetry.Dashboard`: headline token/call tiles, a
tokens-by-model table, a tool-calls-by-name table, and a cross-user usage-by-user table, with
range filters (24h / 7d / 30d / all) and auto-refresh. Any signed-in user sees **global,
cross-user** aggregates.

The data is fed **solely from the OpenTelemetry GenAI spans Agent42 already emits** (the
`UseOpenTelemetry()` chat-client wrapper, `ChatClientFactory.cs:164`), consumed by an
**in-process span processor + background flush** that projects spans into rollups — **no manual
token counter in `AgentGrain`, no OTLP-over-the-wire receiver**. Rollups persist to **Cosmos with
no new container**: a single **global** Orleans grain riding the existing `agent-state` storage
provider (the `UserPushGrain` pattern), with bounded retention + prune.

Why now: the user wants visibility into token spend across the app, styled like the reference
dashboard, reachable by any logged-in user.

## Confirmed decisions (from the planning interview)

1. **Global cross-user** — any signed-in user sees aggregates across all users, including the
   per-user table. (Reference parity.)
2. **Time-bucketed ranges** — 24h / 7d / 30d / all + auto-refresh → needs hourly + daily rollups.
3. **All four regions**, fed **solely from OTEL** (chat spans → tokens; `execute_tool` spans →
   tool-call counts). No cost, no total-tokens field anywhere (reference parity).
4. **In-process OTEL exporter/processor** ingestion — a `BaseProcessor<Activity>` (the
   `UserTelemetrySpanProcessor` shape) reads finished spans; **no** loopback OTLP receiver, port,
   or shared secret.
5. **Cosmos, no new container** — a single **global** rollup grain on the `agent-state` provider.
6. **Dedicated full-page in-app view** (not a Preferences tab) — roomy enough for the four regions
   + filter header + auto-refresh, faithful to the reference's full-width layout.

## The load-bearing UNKNOWN — resolve in Step 0 before building anything

The scout could **not** confirm, from Microsoft.Extensions.AI 10.6.0 / Microsoft.Agents.AI 1.13.0,
the exact facts the entire projection depends on. **These are assumptions until the spike proves
them** (repo rule: validate library behaviour, never recall it — use the `spike-dotnet` skill):

- **A. Exact `gen_ai.*` attribute spellings** actually emitted (dots vs underscores; e.g.
  `gen_ai.usage.input_tokens`, `…output_tokens`, cache-read spelling, reasoning spelling,
  `gen_ai.request.model`, `gen_ai.response.model`, `gen_ai.provider.name`/`gen_ai.system`,
  `gen_ai.operation.name`, `gen_ai.tool.name`, `error.type`). The reference's
  `Projection/GenAiAttributes.cs` (verified against these libs, possibly a different version) is
  the **starting hypothesis**, not gospel.
- **B. The ActivitySource NAME(s)** the chat span and the tool span are emitted on.
  `Program.cs:22` registers only `AddSource("Agent42.Backend")` and `Extensions.cs:68`
  `AddSource(ApplicationName)`. If the chat span or (especially) the `execute_tool` span is
  emitted on a **different** source (e.g. `Experimental.Microsoft.Extensions.AI` /
  `Microsoft.Agents.AI`), our processor **never sees it** until that source is added to the
  tracer. **This is the single most likely reason the tool-calls region silently comes up empty.**
- **C. Whether `execute_tool` spans are emitted at all** on Agent42's path. Agent42 runs
  `ChatClientAgent.RunStreamingAsync` (Agent Framework), **not** `FunctionInvokingChatClient`;
  the scout found no `UseFunctionInvocation`. If tool invocation is not span-instrumented on this
  path, the tool-calls region **cannot** be OTEL-fed without additional wiring — surface that to
  the user rather than shipping an always-empty table.
- **D. Whether cache-read and reasoning token counts are emitted as span attributes.** M.E.AI's
  `UsageContent` exposes `CachedInputTokenCount`/reasoning, but the **OTel emitter may only emit
  input/output**. If cache/reasoning are absent from the span, the OTEL-only path **loses the
  cached-input tile and reasoning column** — an accepted degradation of decision 3 that must be
  called out (the manual grain loop at `AgentGrain.cs:824` *does* have cached, but decision 4
  forbids that path).
- **E. Agent-name availability on a span** (for the "application" dimension — see below).

**Known blind spot — embedding tokens are NOT captured (critic M2).** Only the **chat** client is
`UseOpenTelemetry`-wrapped (`ChatClientFactory.cs:164`); the embedding generator
(`ChatClientFactory.cs:214/281`, `.AsIEmbeddingGenerator()`) is **not**, and ADR 0010
(`0010-…md:65`) confirms embedding GenAI spans are uncovered. So document-RAG ingestion + per-query
retrieval token spend (ADR 0007/0008/0030) will **never** appear here. The dashboard measures
**chat/completion** token spend, not total app token spend. Wrapping the embedding generator to
close this gap is a **possible Step-0 add** if trivial, else **out of scope** and stated plainly in
the ADR + UI — do not let the dashboard imply it covers embeddings when it does not.

**Spike shape:** a file-based `dotnet run app.cs` (pinned to repo versions) that builds a
`ChatClientAgent` over a **fake `IChatClient`** returning a `UsageContent` **and** a tool call,
wraps it with `.UseOpenTelemetry(sourceName:"Agent42.Backend", cfg.EnableSensitiveData=true)`,
attaches an `ActivityListener` that samples **all** sources, runs one turn, and dumps every
Activity's `Source.Name`, `DisplayName`, `OperationName`, and **all tags**. That answers A–E in one
run. Record findings in the spec's Implementation-notes before writing the projector.

## Step-0 spike findings (RESOLVED — build against these, verified live 2026-07-22)

Ran a throwaway `dotnet run` with a fake `IChatClient` (tool call + usage) behind
`ChatClientAgent` + `.UseOpenTelemetry(sourceName:"Agent42.Backend", EnableSensitiveData=true)`,
listening on all sources. Confirmed:

- **A. Chat-span token tags:** `gen_ai.usage.input_tokens` + `gen_ai.usage.output_tokens` (both
  `Int32`). Model = `gen_ai.response.model` (falls back to `gen_ai.request.model`);
  `gen_ai.provider.name`; `gen_ai.response.finish_reasons`. Key on the **`gen_ai.operation.name`
  tag** (value `"chat"`), not DisplayName.
- **B. Source:** **all** spans (`chat`, `execute_tool`, `orchestrate_tools`) emit on
  **`Agent42.Backend`** — already `AddSource`'d (`Program.cs:22`) and already OTLP-exported. So
  **no new `AddSource`** and **no new export/PII surface** → **critic M1 is fully moot** (those
  spans already ship today).
- **C. `execute_tool` span IS emitted:** `gen_ai.operation.name=execute_tool`,
  `gen_ai.tool.name`, `gen_ai.tool.type`, `gen_ai.tool.call.id`. Tool-calls region is viable.
- **D. NO cache/reasoning tokens** even with `AdditionalCounts` populated — only input/output.
  **User decision: keep the reference layout, render cached-input + reasoning as 0 with an honest
  note; stay OTEL-only (no grain tap).** Contract keeps the fields (parity) at 0.
- **E. operation.name values:** `chat`, `execute_tool`, `orchestrate_tools`. **No** `invoke_agent`
  / `gen_ai.agent.name` on the `RunAsync` path → application dimension uses the service constant.
- **Double-count hazard confirmed CONCRETELY:** the `orchestrate_tools` span carried
  `input_tokens=140` = exact sum of the two chat spans (100+40). **The projector MUST whitelist
  `chat` for tokens and `execute_tool` for tool events, and IGNORE `orchestrate_tools`** (and any
  other op). This is the load-bearing projection rule.
- **Errors:** not exercised in the spike; treat a span as errored if it has an `error.type` tag
  **or** `Activity.Status == ActivityStatusCode.Error` (defensive — the OTel error case is
  prod-only).

## The "application" dimension (single-app adaptation)

The reference groups by `application` because it ingests many services. Agent42 is one service, and
**agent name is NOT on spans today** (scout: only `user.*` is stamped; `AgentName` lives in grain
state). Decision, in priority order, pinned by the spike:
- If the spike shows a span attribute carrying the agent/agent-run name (e.g. `gen_ai.agent.name`
  on the chat or a parent span that the processor can read), use it as `application`.
- **Else** set `application` to the OTel **service name** constant (`Agent42.Backend` /
  `"Agent42"`). The column stays present (reference parity) but is effectively constant — honest,
  and **no** separate tracker is added. Per-agent attribution is explicitly a **future
  enhancement** (would mean stamping an agent tag on the span — an OTEL-native addition, out of
  scope here).

## Affected areas (verified against the tree @ e920707)

- `src/Agent42.Agents/UsageEvent.cs` — **new.** Pure projected record(s): one normalized event per
  contributing span (`chat` → token event; `execute_tool` → tool event), plus the pure
  `UsageSpanProjector` that maps a tag-bag (`IReadOnlyDictionary<string,object?>` / an `Activity`)
  to zero-or-one `UsageEvent`. **Placed in `Agent42.Agents`** so it is referenceable by the grain
  and unit-testable without OTel/DI. No Orleans, no I/O.
- `src/Agent42.Agents/UsageRollupState.cs` — **new.** `[GenerateSerializer]`/`[Alias]` state:
  hourly + daily bucket dictionaries keyed by `(bucketStartUtc, application, provider, model,
  userKey)` for token rollups and `(bucketStartUtc, application, tool, userKey)` for tool rollups;
  each value sums input/output/cachedInput/reasoning/calls/errors (tokens) or count/errors (tools).
  Pure `Apply(delta)` upsert, `Prune(nowUtc)` (drop hourly > retention, daily > retention), and
  `Query(range, applicationFilter, userFilter, nowUtc)` → the wire contract. All pure → unit-test
  without a cluster.
- `src/Agent42.Agents/IUsageRollupGrain.cs` + `src/Agent42.Agents/UsageRollupGrain.cs` — **new.**
  `IGrainWithStringKey`, obtained at a **well-known constant key** (e.g. `"global"`). Rides
  `[PersistentState("usage-rollups", AgentGrain.StorageProviderName)]` (the `UserPushGrain`
  pattern — **no new container**). Methods: `ApplyAsync(IReadOnlyList<UsageDelta>)` (batch upsert +
  prune + `WriteStateAsync`), `QueryAsync(range, app, user)`, `ListApplicationsAsync()`,
  `ListUsersAsync()`.
- `src/Agent42.Backend/UsageSpanProcessor.cs` — **new.** `BaseProcessor<Activity>` (mirror
  `UserTelemetrySpanProcessor`), but overriding **`OnEnd`**: run `UsageSpanProjector`, and on a hit
  enqueue to a bounded `Channel<UsageEvent>`. **Must be cheap and non-blocking** (no async, no grain
  call in `OnEnd`).
- `src/Agent42.Backend/UsageIngestionService.cs` — **new.** `BackgroundService`/`IHostedService`
  that drains the channel, folds events into `UsageDelta`s, and calls
  `IUsageRollupGrain.ApplyAsync` in batches (time- or size-triggered). Resolves the grain via
  `IClusterClient`/`IGrainFactory`. **Backend-only registration.**
- `src/Agent42.Backend/Program.cs` — register the processor (`.AddProcessor(new
  UsageSpanProcessor(channel))`), **`AddSource(...)` for any additional source the spike finds**
  (esp. the tool-span source), the `Channel`, and the hosted service. (Lines ~20–27 tracer block;
  hosted service near other `AddHostedService`/registrations.)
- `src/Agent42.Backend/AgentApi.cs` — add `GET /api/usage?application=&user=&range=`,
  `GET /api/usage/applications`, `GET /api/usage/users` inside `MapAgentApi`, behind Easy Auth,
  `Results.Json(...)`, `http.GetUserId()` shape (mirror `GET /api/config-health`). Wire DTOs
  (`UsageResponse`, `UsageTotals`, `ModelUsage`, `ToolUsage`, `UserUsage`) at the **bottom** of
  `AgentApi.cs` with the other response records (repo convention, per the config-health plan).
- `src/Agent42.Backend/wwwroot/index.html` — **new dedicated view** container (`<section
  id="usageView" hidden>`), a **nav entry** to toggle it (app header/menu), and the four-region
  markup (filter header, tiles grid, three `.grid` tables) — adapted from the reference
  `Ui/index.html`.
- `src/Agent42.Backend/wwwroot/app.js` — view toggle (show usage view / back to chat),
  `loadUsage()`/`renderUsage()` (tiles + three tables + client-side share bars), filter-change +
  auto-refresh (`setInterval`, Off/5s/15s/30s/60s/5m), populate app/user dropdowns from the two
  list endpoints. Mirror the `loadConfigHealth`/`renderConfigHealth` fetch/render idiom and the
  silent-fail `fetch` pattern (`app.js:1946`, `:283`).
- `src/Agent42.Backend/wwwroot/styles.css` — port `.tile`/`.panel`/`.grid`/`.bar`/`.bar__fill` and
  the filter-header styles from the reference, **remapped onto Agent42's existing `:root` tokens**
  (`--bg`, `--amber`, `--mono`, `--serif`, …) — Agent42 already shares the amber/phosphor palette
  **and** both fonts (IBM Plex Mono + Newsreader), so **do not import a second font set**. Must
  respect the existing light-theme override block (`:root[data-theme="light"]`).
- `tests/Agent42.Tests/UsageSpanProjectorTests.cs` — **new.** Projection rules.
- `tests/Agent42.Tests/UsageRollupTests.cs` — **new.** Rollup state (pure).
- `tests/Agent42.Tests/UsageRollupGrainTests.cs` — **new.** Grain via TestCluster (UserPushGrain
  test shape).
- `tests/Agent42.Tests/UsageApiTests.cs` — **new.** Endpoint shape / filter / range-400 /
  cross-user (TestHost, `ProvidersApiTests`/`ConfigHealthApiTests` shape).
- `tests/Agent42.Tests/UsageAssetsTests.cs` — **new.** Source-text heuristics (no JS runner —
  `LocationAssetsTests` pattern).
- `.agentic/decisions/0031-token-usage-dashboard.md` — **new** ADR via `/decide`.

## Context & constraints (what the executor can't cheaply re-derive)

- **The exact ingestion facts are the Step-0 spike output — do not code the projector from the
  attribute list in this spec.** The list mirrors the reference; the versions may differ. If the
  spike contradicts this spec, the spike wins and the divergence is recorded.
- **Chat-only token counting (reference `SpanProjector` rule).** Count tokens **only** from
  `gen_ai.operation.name == "chat"` spans. `invoke_agent`/`orchestrate_tools` spans also carry
  `gen_ai.usage.*` but are **sums over children** — counting them double/triple-counts. The spike
  will show which operation names appear on Agent42's path; the projector must whitelist `chat`
  for tokens and `execute_tool` for tool events, and **ignore** the rest.
- **Effective model / user / application coalesce (reference `RollupSql`):** effective model =
  `response_model` ?? `request_model` ?? "" (rendered "unreported"); userKey = `user.email` ??
  `user.name` ?? `user.id` ?? "" — but see the identity note below; application per the dimension
  section above. Cached input is **always a subset of input** — its own tile, **never** summed into
  a token total; there is **no** total-tokens and **no** cost, anywhere (UI or storage).
- **Async paths land in the "unreported" user bucket, by design.** Fired reminders
  (`ReceiveReminder`) and agent-runs (`EnqueuePromptAsync`, `[OneWay]` background) begin fresh
  root spans with **no ambient request identity** — `RequestContext` has no `UserTelemetryContext`,
  so `UserTelemetrySpanProcessor` stamps **no `user.*`** on those chat spans (gotcha: "User identity
  reaches downstream/grain spans via Orleans RequestContext"). Their tokens still count (correct)
  but under an **unreported** user. This is acceptable (the reference has the same bucket) — note it
  in the ADR, don't try to "fix" it by mining the grain.
- **`OnEnd` must not block or call a grain.** OTel processor callbacks run inline on the exporting
  path. Project + enqueue only; all grain I/O happens in the background service. Use a **bounded**
  channel with drop-oldest/drop-newest on overflow so a slow grain can never back-pressure the
  telemetry pipeline (usage data is best-effort, never load-bearing on a chat turn).
- **`AddSource` is TracerProvider-global, not a private feed (critic M1).** Adding the spike-found
  tool/agent source to `WithTracing` also routes those spans to the **ADR 0009 OTLP exporters**
  (`Extensions.cs:85`) and gets them **stamped with `user.*`** by the existing
  `UserTelemetrySpanProcessor`. That is a real change to exported trace volume/PII surface — likely
  desirable (richer traces) but a **side effect to record in the ADR**, and if an `execute_tool`
  span carries tool arguments/results, it widens the ADR 0013/0009 egress. The usage processor
  cannot get a "private" source; it filters in `OnEnd` instead.
- **Batch flush has a floor, and reads may contend (critic L3/L4).** The background service flushes
  on a **≥30 s** timer (or a max-batch-size trigger), never per-event — each `ApplyAsync` does
  `Prune` + `WriteStateAsync`, re-serializing the whole (~≤1 MB) item, and the global grain is
  **non-reentrant**, so a fast flush or a 5 s auto-refresh `QueryAsync` would serialize against
  ingestion. Trivial at 2 users; pin the interval so it isn't left to the executor, and if it ever
  grows, mark `QueryAsync` reentrant (`[AlwaysInterleave]`) rather than shrinking the flush.
- **Cosmos 2 MB item cap is the real storage risk (ADR 0004).** One global grain serializes its
  whole rollup state into a single `agent-state` item every write. State size ≈ (hourly rows +
  daily rows) × ~100 B, where rows ≈ buckets × applications × providers × models × users. **Bound
  it:** retain hourly buckets ~**48 h** and daily buckets ~**90 d**, and **`Prune` on every
  `ApplyAsync`** before `WriteStateAsync`. Worst-case sanity: 90 daily × 10 users × 10 models ×
  1 app ≈ 9 k rows ≈ ~900 KB — under 2 MB for this personal app (2 allowlisted users), but the
  bound is load-bearing, so retention constants live in one place and a test asserts pruning drops
  aged buckets. **Escape hatch if it ever grows:** shard by day into per-date grains (out of scope
  now). Also: **single replica** (Orleans localhost clustering, replicas pinned at 1 — SearXNG
  gotcha), so the single global grain has no cross-replica contention.
  - **Redo the worst-case math over ALL FOUR dictionaries (critic M3).** The ~900 KB figure counted
    only daily token rows. The real state = hourly-token (~48 × users × models) + daily-token (~90 ×
    users × models) + hourly-tool (~48 × users × ~5 tools) + daily-tool (~90 × users × ~5 tools),
    each row carrying the reasoning/cached columns. Summed at the same worst case this approaches
    **~2 MB, not ~900 KB** — the stated headroom is illusory at the extreme, and a `Discover:true`
    provider (ADR 0018) can inflate the `models` dimension further. Fine at 2 allowlisted users, but
    since the bound is load-bearing, either tighten retention (e.g. hourly 24 h / daily 60 d) or
    justify the real sum. Pin the final constants in Step 2 and cover them with `Prune` tests.
- **`range=all` means "the retained window", not literally all-time (critic M4).** `all` reads the
  daily rollup, which is pruned at the daily retention (~60–90 d). Label it honestly in the UI/ADR
  ("all = retained window") so it isn't mistaken for unbounded history.
- **Grain access + well-known key.** No global-grain precedent exists (scout: all grains are
  per-user string keys). Use `IGrainWithStringKey` + a constant key (`"global"`). No explicit
  registration (Orleans auto-discovery). The **background service must be registered in Backend
  only** — DevHost has no OTel pipeline, so the processor/ingestion never runs offline (dashboard
  renders **empty** offline; that is expected and consistent with the "OTel is prod-only offline"
  reality). **Do not** add a mandatory both-hosts registration.
- **Cross-user visibility is a deliberate privacy decision — record it in the ADR.** `GET
  /api/usage` returns **other users' `user.email`/name and their token usage** to **any** signed-in
  allowlisted user. The reference is an internal engineering dashboard that does exactly this;
  Agent42 has ~2 trusted allowlisted users (`main.bicep`). Endpoint stays **behind Easy Auth**
  (never `excludedPaths`), like every `/api/*` read. No role system exists (config-health ADR 0029
  set this precedent). Call this out explicitly — it is the one genuinely new exposure.
- **Range semantics (reference `UsageQueries`):** `range ∈ {24h,7d,30d,all}`; unknown → **400**;
  omitted → default **30d**; `24h` reads the **hourly** rollup, the rest read **daily**;
  `application`/`user` accept `all` or omitted = no filter. Match these exactly.
- **Wire contract = camelCase, reference parity** (System.Text.Json default). Shapes:
  `totals{inputTokens,outputTokens,cachedInputTokens,callCount,errorCount,toolCallCount}`;
  `models[]{application,model,provider,inputTokens,outputTokens,cachedInputTokens,
  reasoningOutputTokens,callCount,errorCount}`; `tools[]{application,toolName,count,errorCount}`;
  `users[]{user,inputTokens,outputTokens,cachedInputTokens,reasoningOutputTokens,callCount,
  errorCount,toolCallCount}`. `model`/`provider`/`user` render "unreported" when empty. Sort:
  models & users by `input+output` desc, tools by `count` desc.
- **Share bars are client-side only** (reference ADR-0009): `(input+output)` relative to the row
  max for model/user tables, `count` for tools. The only "chart" is a `<div>` with a percentage
  width — **no chart library**.
- **Frontend is vanilla JS, no build.** Copy the fetch/render idiom (`loadConfigHealth`/
  `renderConfigHealth`, `app.js:1946`; silent-fail fetch, `:283`). No JS test runner → asset tests
  are source-text heuristics (`LocationAssetsTests`).
- **The full-page view has NO existing exemplar — pin the mechanics (critic M5).** The cited
  config-health exemplar is a **modal tab**, not a page. `index.html` has a `#topbar` (~`:84`) with
  `#menuToggle` and the chat main area, but no view-switch pattern. So specify concretely: add a
  **nav control in `#topbar`** (a "Usage" button next to the existing menu/preferences controls)
  that toggles a new top-level `<section id="usageView" hidden>` while hiding the chat/sidebar
  region, and a "back to chat" affordance. On **entering** the view call `loadUsage()` + start the
  auto-refresh `setInterval`; on **leaving** it (back, or opening a conversation) **clear the
  interval** (store the handle; a leaked timer keeps polling `/api/usage` from the chat view). The
  asset test asserts the view id, the nav control, the fetch path, the share-bar render, the
  interval-option set, **and** an interval-clear on exit.
- **A new named `HttpClient` is NOT introduced** — ingestion is in-process, no outbound HTTP; the
  retry-pipeline gotcha does not apply here.
- **New `/api/usage*` handlers must inject only both-hosts-resolvable deps (critic L1).** DevHost
  (`tools/Agent42.DevHost/Program.cs:117`) carries the param-inference warning: a minimal-API
  handler taking a dep unregistered in DevHost breaks routing for the **whole** host. Inject
  `IGrainFactory grains` (proven available offline — `AgentApi.cs:100/104`) + `HttpContext`; **never**
  take the ingestion `Channel` or the Backend-only hosted service as a route parameter.

## Steps

0. **Spike (mandatory, blocking)** — `spike-dotnet`: resolve unknowns A–E above; write the
   confirmed attribute names, source name(s), operation-name values, tool-span presence, and
   cache/reasoning availability into this spec's Implementation-notes. **If `execute_tool` spans are
   absent or cache/reasoning attributes are missing, STOP and report to the user** (it changes what
   decision 3 can deliver) before proceeding. *(No test; it is throwaway.)*
1. **Projection (pure)** — `UsageEvent.cs` + `UsageSpanProjector`. Whitelist `chat`→token event,
   `execute_tool`→tool event, coalesce effective model/user/application, read `error.type`.
   **Verify:** `UsageSpanProjectorTests` — chat span → token event with right fields; `invoke_agent`
   span → **ignored** (no double count); `execute_tool` → tool event, no tokens; missing model/user
   → empty ("unreported") keys; error.type present → error flagged. *(Parallelizable with 5.)*
2. **Rollup state (pure)** — `UsageRollupState.cs`: `Apply`, `Prune`, `Query`. **Verify:**
   `UsageRollupTests` — upsert accumulates; hourly vs daily bucketing; `Prune` drops aged buckets
   (retention bound); `Query(24h)` uses hourly, `Query(30d)` daily; application/user filters;
   **cross-user aggregation** (two users → correct `users[]` + summed totals); cached ≤ input
   invariant preserved; no total/cost field exists.
3. **Grain** — `IUsageRollupGrain`/`UsageRollupGrain` on `agent-state` state `"usage-rollups"`,
   well-known key. `ApplyAsync` (batch → Apply → Prune → Write), `QueryAsync`, `List*Async`.
   **Verify:** `UsageRollupGrainTests` (TestCluster, UserPushGrain shape) — apply then query
   returns rollup; survives `DeactivateOnIdle` reactivation (persistence); prune persists.
4. **OTel processor + background flush** — `UsageSpanProcessor` (`OnEnd`→bounded channel),
   `UsageIngestionService` (drain→`ApplyAsync` batches). Register in `Program.cs` (processor,
   channel, hosted service, **plus `AddSource` for the spike-found tool source**). **Verify:**
   `UsageSpanProjectorTests` already covers projection; add a processor test that feeds a synthetic
   `Activity` (tags set) through `OnEnd` and asserts the channel receives the expected `UsageEvent`;
   a light ingestion test that a batch of events folds into the expected `UsageDelta`s. (No live
   OTel host — same prod-only caveat as existing telemetry tests.)
5. **API endpoints** — `GET /api/usage` (+ `/applications`, `/users`) behind Easy Auth; range
   validation; `Results.Json`. DTOs at `AgentApi.cs` bottom. **Verify:** `UsageApiTests` (TestHost)
   — usage JSON shape matches the contract; unknown range → **400**; default 30d; app/user filter
   narrows; **cross-user rows visible to the caller**; empty store → zeroed totals + empty tables.
   *(Parallelizable with 1.)*
6. **Frontend dedicated view** — index.html view + nav entry + four-region markup; app.js view
   toggle, `loadUsage`/`renderUsage`, filters, auto-refresh, dropdown population; styles.css ported
   tiles/panel/grid/bar on existing tokens (both themes). **Verify:** `UsageAssetsTests`
   (source-heuristic: view id, nav entry, `/api/usage` fetch, share-bar render, auto-refresh
   interval options present); manual DevHost open (renders empty offline — expected).
7. **ADR** — `/decide` → `0031-token-usage-dashboard.md`: OTEL-only in-process ingestion (vs manual
   counter vs OTLP receiver), Cosmos global grain on `agent-state` (no new container) + 2 MB
   retention/prune bound, **cross-user visibility privacy decision**, application=agent-name-or-
   service, unreported-async-path bucket, cached-subset / no-cost / no-total invariants.
8. **Doc sweep** — `/sync-docs`: CLAUDE.md feature bullet; gotchas for (a) the **source-must-be-
   registered-or-the-span-is-invisible** trap (esp. the tool span source), (b) the **chat-only token
   counting** rule, (c) the **global-grain 2 MB retention/prune** bound, (d) **async paths →
   unreported user** by design.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A `chat` span projects to a token event with input/output/model/provider/user | `ChatSpan_ProjectsTokenEvent` | `UsageSpanProjectorTests.cs` |
| AC2 | An `invoke_agent`/`orchestrate_tools` span is ignored (no double counting) | `AggregateSpan_IsNotCounted` | same |
| AC3 | An `execute_tool` span projects to a tool event (count, no tokens) | `ToolSpan_ProjectsToolEvent` | same |
| AC4 | Missing model/user coalesce to empty ("unreported") keys | `MissingDimensions_CoalesceToUnreported` | same |
| AC5 | Rollup upsert accumulates; hourly vs daily bucketing correct | `Apply_Accumulates_IntoCorrectBuckets` | `UsageRollupTests.cs` |
| AC6 | `Prune` drops buckets older than retention (2 MB bound) | `Prune_DropsAgedBuckets` | same |
| AC7 | `Query(24h)` reads hourly; `Query(30d/all)` reads daily; app/user filters narrow | `Query_RangeAndFilters` | same |
| AC8 | Cross-user aggregation: two users → correct per-user rows + summed totals | `Query_AggregatesAcrossUsers` | same |
| AC9 | Cached input never exceeds input; no total/cost field emitted | `Contract_NoTotalNoCost_CachedIsSubset` | same |
| AC10 | Grain persists rollups across reactivation | `Rollup_SurvivesReactivation` | `UsageRollupGrainTests.cs` |
| AC11 | `OnEnd` on a synthetic gen_ai Activity enqueues the expected event | `Processor_EnqueuesProjectedEvent` | `UsageProcessorTests.cs` (Backend-side processor; separate from the pure-projector file) |
| AC12 | `GET /api/usage` returns the reference contract shape | `Usage_ReturnsContractShape` | `UsageApiTests.cs` |
| AC13 | Unknown `range` → 400; omitted → 30d default | `Usage_RangeValidation` | same |
| AC14 | Cross-user rows are visible to any signed-in caller | `Usage_IsCrossUserVisible` | same |
| AC15 | Dedicated view present + wired (`/api/usage` fetch, share bars, auto-refresh, **interval cleared on exit**) | `UsageView_IsWiredIntoUi` | `UsageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80
- cognitive_complexity_max: 15
- vulnerable_packages: 0 (no new runtime packages expected; ingestion is in-process)
- `dotnet format Agent42.slnx --verify-no-changes` clean · `dotnet test Agent42.slnx` green
- Branch: `feature/token-usage-dashboard` (+ MR). **Never commit to `main`.**

## Out of scope

- **Cost / pricing** — the reference has a `CostFormula` with no production caller and renders no
  cost; we match that (no cost, no total-tokens).
- **Per-agent "application" attribution** — application is agent-name-if-on-span, else the service
  constant; stamping a dedicated agent tag on the span is a future OTEL-native enhancement.
- **OTLP-over-the-wire receiver** — explicitly rejected (decision 4).
- **A manual token counter in `AgentGrain`** — explicitly rejected (decision 4); the existing
  `UsageContent` loop is left untouched.
- **Historical backfill** — rollups accrue from deploy forward; no reconstruction of past spans.
- **A new Cosmos container / Bicep change** — the grain rides the existing `agent-state` container.
- **Sharding rollups across per-date grains** — the single-grain + prune bound suffices at this
  app's scale; sharding is the documented escape hatch if it ever approaches 2 MB.
- **Alerting / budgets / quotas** — reporting only.

## Implementation notes (divergence from plan)

Executed on `feature/token-usage-dashboard` from `main @ e920707` (no drift).

- **Step 0 spike resolved every unknown (see "Step-0 spike findings" above).** The decisive outcomes:
  `execute_tool` spans exist (tool region viable); all spans on the already-registered `Agent42.Backend`
  source (**no new `AddSource`; critic M1 fully moot — no new export/PII surface**); `orchestrate_tools`
  carries the SUM of child chats (chat-only counting is load-bearing, now covered by
  `AggregateSpan_IsNotCounted`); cache/reasoning tokens absent → **user chose "keep layout, show 0 + honest
  note"**, so those fields stay on the wire/UI at 0.
- **Wire DTOs live in `Agent42.Agents` (`UsageContracts.cs`), NOT at the bottom of `AgentApi.cs`** as the
  config-health plan modelled. Reason: these records **cross the grain boundary** (returned by
  `IUsageRollupGrain.QueryAsync`), so they must be `[GenerateSerializer]` in Agents; config-health DTOs did
  not cross a grain. They still serialize camelCase to the browser via `Results.Json`.
- **`UsageEvent` is the grain-boundary payload** (no separate `UsageDelta` type) — the ingestion service
  batches `UsageEvent`s straight into `ApplyAsync`, which folds them into buckets. Simpler than the plan's
  event→delta→grain hop.
- **Rollup keys are composite strings** (`bucket|app|provider|model|user`), not record keys — avoids any
  Orleans record-key serialization risk; each bucket value carries its own dimensions so `Query` filters off
  values, never keys.
- **`UsageIngestionService` got a second (4-arg) public constructor taking the flush interval** purely as a
  test seam (DI still binds the 3-arg ctor since `TimeSpan` isn't registered), so
  `UsageIngestionServiceTests` drives the real drain loop with a 50 ms interval instead of the 30 s
  production cadence — lifting the file off 0% coverage.
- **Frontend is a dedicated fixed-overlay full-page view** (`#usageView`, opened from a sidebar-footer
  `#openUsageBtn`), not a Preferences tab. Auto-refresh timer is a module `let` cleared on close (leak
  guard); the new `<section>` sits before the existing end-of-body `<script>` tags so `app.js` lookups
  resolve.
- **Retention constants** shipped as hourly 48 h / daily 92 d (`UsageRetention`); `range=all` = the retained
  daily window (documented in ADR + UI note).

## Review

Critic pass (fresh-context, verified against tree @ e920707). **Verdict: sound plan, no HIGH
blockers** — the Step-0 spike correctly gates the two genuine unknowns; projection/rollup/grain/API
stay pure-testable; the `UserPushGrain` no-new-container pattern is copied correctly.

Affirmed correct by the critic (challenged, held up): async paths carry no `user.*` → unreported
bucket (ADR 0010:63-65); `OnEnd` is the right override; the **chat/token** span is already on the
registered `Agent42.Backend` source (only the **tool** source needs `AddSource`); DevHost registers
`AddMemoryGrainStorage(agent-state)` + maps `MapAgentApi`, so the new state + endpoint work offline
(empty); ADR 0031 is the free slot; quality gates copied verbatim; `planned-against` == HEAD; ADR
0002/0013 consistent; no-new-`HttpClient` correctly declared N/A.

Findings — **all accepted and folded in**:
- **M1 (AddSource is TracerProvider-global)** → added constraint: the tool/agent source also feeds
  the OTLP exporters and gets `user.*`-stamped; recorded as an ADR side effect.
- **M2 (embedding tokens entirely invisible)** → added the "Known blind spot" note in Step 0; the
  dashboard measures chat/completion spend, not total app spend; stated in ADR + UI (or close the
  gap by wrapping the embedding generator if trivial).
- **M3 (2 MB math understated)** → added the all-four-dictionaries worst case (~2 MB, not ~900 KB);
  retention constants to be pinned/tightened in Step 2 with `Prune` tests.
- **M4 (`range=all` is capped at the retained window)** → labelled honestly in UI/ADR.
- **M5 (full-page view has no exemplar)** → pinned the nav control in `#topbar`, the `#usageView`
  toggle, and the **interval-clear on exit**; AC15 updated.
- **L1 (both-hosts param-inference trap)** → constrained the new handlers to `IGrainFactory` +
  `HttpContext` only.
- **L2 (AC11 file placement)** → AC11 moved to a Backend-side `UsageProcessorTests.cs`.
- **L3/L4 (flush floor + non-reentrant reads)** → pinned a ≥30 s flush and noted the
  `[AlwaysInterleave]` read escape hatch.
