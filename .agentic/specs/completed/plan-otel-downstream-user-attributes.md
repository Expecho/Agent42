# Plan — Carry `user.*` telemetry attributes to downstream (grain + in-request) spans

planned-against: feature/otel-user-attributes @ 436dd2d

> **Stacked on PR #24.** This plan builds directly on the `user.*` server-span enrichment from
> PR #24 (branch `feature/otel-user-attributes`, not yet merged to `main`). It reuses
> `CurrentUser.GetUserTelemetryTags`/`ApplyUserTelemetryTags` and ADR 0009. **Base the execution
> branch on PR #24's code, not bare `main`.** If PR #24 has merged to `main` by execution time,
> branch from `main` instead (the code is equivalent) and update this header.

## Overview

PR #24 stamps the OpenTelemetry `user.*` attributes only on the **ASP.NET Core server span**.
OTel attributes are per-span and are **not** inherited by child/subsequent spans, so every
downstream span — most importantly the **Azure OpenAI chat completion span**, which executes
*inside the Orleans grain* — currently carries no user attribution. Goal: propagate the identity
so downstream spans (grain spans **and** in-request child spans) carry the full `user.*` set,
using **Orleans `RequestContext`** as the propagation channel and a **span processor** that stamps
each started span. Reminders remain out of scope (no request, no identity).

## Affected areas

- **New** `src/Agent42.Agents/UserTelemetryContext.cs` — a small serializable carrier record
  (`[GenerateSerializer]`) holding the extracted identity (id/name/email/full_name/roles/idp) with a
  `ToTags()` method yielding the `KeyValuePair<string, object>` tag shape (roles as `string[]`). Lives
  in `Agent42.Agents` (alongside the other Orleans-serializable message types) because it crosses the
  grain-call boundary; it owns the `user.*` attribute-key constants.
- `src/Agent42.Backend/CurrentUser.cs` — refactor the header parsing so header→`UserTelemetryContext`
  is the **single** extraction path; `GetUserTelemetryTags` becomes
  `headers.GetUserTelemetryContext().ToTags()` (no second parse — keeps `duplication_pct_max: 3`).
  `ApplyUserTelemetryTags` unchanged. Add `GetUserTelemetryContext(this IHeaderDictionary)`.
- **New** `src/Agent42.Backend/UserTelemetrySpanProcessor.cs` — `BaseProcessor<Activity>`; `OnStart`
  reads `RequestContext.Get(Key)`, and when an identity is present stamps the activity via the
  carrier's tags. No-op when absent.
- `src/Agent42.Backend/Program.cs` — (a) register a middleware, first in the pipeline, that sets
  `RequestContext.Set(Key, headers.GetUserTelemetryContext())`; (b) add `.AddProcessor(new
  UserTelemetrySpanProcessor())` to the existing `WithTracing(...)`. Keep the PR #24
  `EnrichWithHttpRequest` block (it still owns the server span — see Context).
- `tests/Agent42.Tests/` — extend `UserTelemetryTests.cs`; new processor + middleware tests; new
  `RequestContextPropagationTests.cs` proving propagation through the **real streaming `ChatAsync`
  path** (TestCluster), plus a small `RequestContext` echo added to the existing
  `tests/Agent42.Tests/FakeChatClient.cs` (a `[uctx:…]` marker, mirroring its `[sys:…]`/`[ctx:…]`
  echoes) so the grain's streamed reply reveals what `RequestContext` the chat-client call site saw.
  **No new grain** — reusing `AgentGrain.ChatAsync` exercises the exact `IAsyncEnumerable` streaming
  context where the production chat-completion span is created, and avoids defining a grain in the test
  assembly (which lacks Orleans SDK codegen — see Review R2).
- `.agentic/decisions/` — new ADR 0010 (downstream propagation via RequestContext, not Baggage);
  amend ADR 0009's out-of-scope note to point at it.
- Docs sweep (`sync-docs`): a gotcha for the RequestContext channel + the no-`baggage`-header
  invariant; CLAUDE.md as the diff dictates.

## Context & constraints

- **Why not `Activity.Baggage`.** Baggage auto-injects a W3C `baggage` request header on outbound
  HttpClient calls (the default `DistributedContextPropagator`), which would ship user PII
  (name/email/roles) to **Azure OpenAI** in headers. `RequestContext` is an in-cluster channel
  (Orleans' own protocol, never an external HTTP header), so it carries identity to the grain without
  leaking it outward. **This no-external-leak invariant is the core reason for the design** — do not
  "simplify" to Baggage. (Verified reasoning; the invariant is documented, not offline-testable.)

- **One process, one TracerProvider.** Backend and the Orleans silo are co-hosted
  (`Program.cs` `builder.UseOrleans(...)` on the same `WebApplication`). So a single processor
  registered on the Backend's TracerProvider observes **both** in-request spans and in-grain spans —
  no cross-process concern.

- **Which spans this covers.** The processor stamps every sampled Activity started while an identity
  is in `RequestContext`:
  - *In-grain* (via RequestContext propagated across the grain call): the **chat completion** span
    (source `Agent42.Backend`, from `_agent.RunStreamingAsync` — `AgentGrain.cs` `ChatAsync`), and the
    grain-side retrieval **embedding** HttpClient spans (`AgentGrain.cs` `SearchDocumentContextAsync`).
  - *In-request* (RequestContext is an ambient AsyncLocal in the Backend flow too): the document-upload
    **embedding** and **blob storage** client spans (`DocumentIngestionService.IngestAsync`, called
    in-request at the `AgentApi.cs` upload endpoint) and any direct in-request HttpClient spans. So the
    user's "grain spans too" ask is delivered, and in-request coverage comes **for free** from the same
    mechanism — the processor and ambient are identical, so AC1 (processor stamps from RequestContext)
    proves it by composition; the end-to-end is prod-smoke (AC8).
  - *Caveat (unverified — prod-smoke):* the embedding/blob calls go through Azure SDK clients, which may
    emit their own `Azure.*` `ActivitySource` spans and **suppress** the nested HttpClient span. That
    `Azure.*` source is **not** added to the pipeline (`ServiceDefaults/Extensions.cs` adds only the app
    source + AspNetCore + Http instrumentation), so if suppression happens those spans would be neither
    sampled nor stamped. The **chat completion** span (source `Agent42.Backend`) *is* sampled and is the
    primary target; whether the embedding/blob spans actually appear-and-stamp is a collector check
    after deploy, not an offline guarantee.

- **What it does NOT cover (call out; don't silently drop).**
  - **Reminders** — run on a grain timer with no request/identity; their spans stay unattributed
    (they have no `HttpContext` at all — consistent with ADR 0009 out-of-scope).
  - **Orleans grain-call orchestration spans** (`Microsoft.Orleans.*` source) — that source is **not**
    added to the pipeline (`ServiceDefaults/Extensions.cs` adds only the app source + AspNetCore + Http
    instrumentation), so those spans are not sampled today; not stamped. Adding that source is a
    separate enhancement.
  - **Named embedding (GenAI) spans** — the embedding generator is not OTel-wrapped
    (`Program.cs`), so embedding calls produce no GenAI span; they may surface as an `Azure.*` client
    span or a raw HttpClient span (unverified — see the caveat above). Wrapping embeddings with
    `UseOpenTelemetry` is out of scope.

- **The server span stays on `EnrichWithHttpRequest`.** The ASP.NET Core server Activity starts
  *before* user middleware runs, so its processor `OnStart` fires before the middleware sets
  `RequestContext` — the processor cannot stamp it. PR #24's `EnrichWithHttpRequest` still owns the
  server span; the processor owns everything downstream. No double-stamping (different spans; the
  server span's `OnStart` has already passed by the time RequestContext is set).

- **LOAD-BEARING ASSUMPTION — Orleans RequestContext caller→grain propagation.** Orleans has
  propagated `RequestContext` key/values across grain calls by default since early versions, but the
  scout could not confirm this for **Orleans 10.2** from the repo, and nothing in the repo uses
  `RequestContext` yet. **Step 1 gates the whole plan on a TestCluster test** proving a value set
  before a grain call is visible inside the grain. Fallback if it is off by default: enable it via silo
  config, or add an `IOutgoingGrainCallFilter`/`IIncomingGrainCallFilter` pair to copy the value across.
  RequestContext values crossing the boundary must be Orleans-serializable — hence the
  `[GenerateSerializer]` carrier (fields are `string`/`string[]`, all natively serializable).

- **Offline-testing philosophy (as in PR #24).** A real OTel host is prod-only and the TestCluster
  runs no OTel pipeline, so "OTel actually stamps the in-grain span" is a **prod-smoke** check (AC8).
  Offline we prove the chain by composition: (a) the processor stamps from `RequestContext`
  (processor test), (b) the middleware writes identity into `RequestContext` from headers (middleware
  test), (c) Orleans propagates `RequestContext` into the grain (propagation test). Together these
  prove every link without booting OTel.

- **Exemplars.** Orleans TestCluster test shape: `tests/Agent42.Tests/AgentGrainTests.cs` +
  `tests/Agent42.Tests/TestSiloConfigurator.cs`. OTel pipeline test: `OtelConfigurationTests.cs`.
  Header-extractor + Activity tests: `tests/Agent42.Tests/UserTelemetryTests.cs` (PR #24).

- **Governing docs.** ADR 0009 (PII-in-telemetry decision — this extends it), ADR 0002 (Easy Auth
  headers), gotcha "Easy Auth claim `typ` spellings … prod-only" (PR #24).

## Steps

1. **GATE — prove Orleans RequestContext propagation into the *streaming* grain path (TestCluster).**
   The production chat span is created inside `AgentGrain.ChatAsync`'s `IAsyncEnumerable` streaming
   iterator (`_agent.RunStreamingAsync`), which Orleans pumps via repeated MoveNext RPCs — so a scalar
   request/response probe grain would **not** prove propagation at the real span's context (Review R1).
   Instead, exercise the real path: add a `RequestContextEcho` to `FakeChatClient` (the terminal client
   invoked *inside* the grain's streaming iteration — `TestSiloConfigurator.cs:21`) that reads
   `RequestContext.Get(Key)` and prepends a `[uctx:<user.id>]` marker to the streamed reply, exactly
   like its existing `[sys:…]`/`[ctx:…]` echoes. In `RequestContextPropagationTests.cs`: `RequestContext.Set`
   a `UserTelemetryContext` before calling `grain.ChatAsync(...)`, drain the stream, and assert the
   reply carries `[uctx:<id>]` (propagation reached the streaming chat-client call site); a second case
   with nothing set → no `[uctx:…]` marker. If this fails, STOP and resolve propagation (silo config or
   `IOutgoingGrainCallFilter`/`IIncomingGrainCallFilter`) before proceeding — the whole plan depends on it.
   Reusing the real grain avoids defining a grain in the test assembly, which lacks
   `Microsoft.Orleans.Sdk` codegen (Review R2).
   *Verify:* `dotnet test --filter RequestContextPropagation`.
   *Files:* `tests/Agent42.Tests/RequestContextPropagationTests.cs`, `tests/Agent42.Tests/FakeChatClient.cs`.
   *No `src/` changes.* *Depends on Step 2 (the `UserTelemetryContext` carrier type).*

2. **Carrier record.** Add `UserTelemetryContext` (`[GenerateSerializer]`, `[Id(n)]` fields) in
   `Agent42.Agents` with the six identity fields and `IEnumerable<KeyValuePair<string, object>>
   ToTags()` (emit each field only when present; roles as `string[]`). Move the `user.*` key constants
   here. *Verify:* `dotnet build`; unit-tested in Step 5. *Files:* `src/Agent42.Agents/UserTelemetryContext.cs`.

3. **Refactor extraction to a single path.** In `CurrentUser.cs`, add
   `GetUserTelemetryContext(this IHeaderDictionary)` (the header + claims-blob parse now produces the
   carrier), and make `GetUserTelemetryTags` return `GetUserTelemetryContext(headers).ToTags()` so
   there is exactly one parse (no duplication). `GetUserId`/`ResolveUserId`/`ApplyUserTelemetryTags`
   behaviour unchanged. *Verify:* `dotnet test --filter UserTelemetry` (PR #24 tests must stay green).
   *Files:* `src/Agent42.Backend/CurrentUser.cs`. *Depends on Step 2.*

4. **Span processor.** Add `UserTelemetrySpanProcessor : BaseProcessor<Activity>`; `OnStart(activity)`
   reads `RequestContext.Get(Key)` as `UserTelemetryContext`, and if non-null stamps
   `foreach tag in ctx.ToTags(): activity.SetTag(...)`. Stateless; no-op when absent. *Verify:* Step 5
   unit test. *Files:* `src/Agent42.Backend/UserTelemetrySpanProcessor.cs`. *Depends on Step 2.*

5. **Wire middleware + processor; tests.** In `Program.cs`: register a first-in-pipeline
   `app.Use(...)` that calls `RequestContext.Set(Key, ctx.Request.Headers.GetUserTelemetryContext())`;
   add `.AddProcessor(new UserTelemetrySpanProcessor())` to the existing `WithTracing`. Keep the
   `EnrichWithHttpRequest` block. Define the shared `RequestContext` key constant in one place
   (the processor or carrier). Tests: **processor** (set RequestContext → start real `Activity` →
   `OnStart` → assert all tags incl. roles array; empty RequestContext → no tags), **middleware**
   (invoke the delegate with a `DefaultHttpContext` carrying the Easy Auth headers and a `next` that
   captures `RequestContext.Get(Key)` → assert the carrier is populated), and **carrier `ToTags`**
   (present/absent fields). *Verify:* `dotnet test Agent42.slnx`.
   *Files:* `src/Agent42.Backend/Program.cs`, `tests/Agent42.Tests/UserTelemetryTests.cs` (or a new
   `UserTelemetryProcessorTests.cs`). *Depends on Steps 2–4.*

6. **ADR 0010 + amend 0009.** New ADR: "Propagate user identity to downstream spans via Orleans
   RequestContext (not Activity.Baggage)" — context (grain-side chat span unattributed), decision
   (RequestContext + span processor), the **no-external-`baggage`-header** rationale, alternatives
   (Baggage / grain-call filter only / grain-key-derived id-only), consequences (in-cluster PII
   propagation accepted; reminders + Orleans orchestration spans still uncovered). Amend ADR 0009's
   out-of-scope bullet to reference 0010. *Independent of Steps 1–5 (parallelizable); same MR.*
   *Verify:* doc-path integrity / `/audit-docs` spot check.

7. **Docs sweep (`/sync-docs`).** Gotcha: user identity flows to downstream/grain spans via
   `RequestContext` (never `Activity.Baggage` — no PII in outbound headers); note it's the one
   `RequestContext` use in the repo and the propagation is a load-bearing behaviour guarded by the
   Step 1 test. CLAUDE.md telemetry note if warranted. *Depends on Steps 1–6.*
   *Verify:* `dotnet format Agent42.slnx --verify-no-changes`; `/audit-docs` if docs changed.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proving test | File |
|---|----------------------|--------------|------|
| AC1 | A span started while identity is in `RequestContext` gets the full `user.*` set stamped via `SetTag` | `Processor_WithIdentityInRequestContext_StampsAllTags` | `UserTelemetryProcessorTests.cs` |
| AC2 | `user.roles` is stamped as a `string[]` (multi-value) on downstream spans | `Processor_StampsRolesAsArray` | `UserTelemetryProcessorTests.cs` |
| AC3 | **Orleans propagates `RequestContext` into the streaming `ChatAsync` grain path** (the load-bearing fact, at the real chat-span context) — set before the call → visible at the in-grain chat-client site; unset → not visible | `RequestContext_SetBeforeStreamingChat_IsVisibleAtChatClientSite` (via `FakeChatClient` `[uctx:…]` echo) | `RequestContextPropagationTests.cs` |
| AC4 | Processor is a no-op when no identity is set (background/reminder/startup spans) | `Processor_NoRequestContext_StampsNothing` | `UserTelemetryProcessorTests.cs` |
| AC5 | Middleware writes the header-derived identity into `RequestContext` | `Middleware_WithEasyAuthHeaders_SetsRequestContext` | `UserTelemetryProcessorTests.cs` |
| AC6 | Single extraction path: `GetUserTelemetryTags` still emits all six tags after the carrier refactor (PR #24 behaviour preserved) | existing `GetUserTelemetryTags_*` (unchanged, must stay green) | `UserTelemetryTests.cs` |
| AC7 | No telemetry-pipeline regression (dual exporter builds; `/health`,`/alive` filtered); `GetUserId` grain-keying unchanged | existing `OtelConfigurationTests`, `AgentApiTests` (unchanged, green) | `OtelConfigurationTests.cs`, `AgentApiTests.cs` |
| AC8 | OTel actually stamps the in-grain chat-completion span with `user.*` in prod | **prod-smoke only** — inspect a chat trace at the collector after deploy; not offline-testable (real OTel + Easy Auth are prod-only) | — |
| AC9 | Identity is never emitted as an outbound `baggage` header (RequestContext, never `Activity.Baggage`) | code-review invariant + gotcha; grep-guard that no `Baggage`/`SetBaggage` API is used | (documented) |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3` (single extraction path via the carrier; no measuring tool in repo → report SKIPPED)
- `coverage_new_code_pct_min: 80` (processor/middleware/carrier are heavily unit-tested; the ~2 wiring
  lines in `Program.cs` are prod-smoke, AC8)
- `cognitive_complexity_max: 15` (no analyzer in repo → report SKIPPED)
- `dependencies.vulnerable_packages: 0` (no new package expected — `Orleans.Runtime`,
  `OpenTelemetry` `BaseProcessor` are already referenced; confirm)
- Branch: `feature/otel-downstream-user-attributes` (stacked on PR #24; never commit to `main`); MR required.

## Out of scope

- **Reminders / background grain activity** — no request identity to propagate.
- **Orleans grain-call orchestration spans** (`Microsoft.Orleans.*` source not sampled today) — adding
  that source is a separate enhancement.
- **Named embedding GenAI spans** — the embedding generator is not OTel-wrapped; covering it means
  wrapping it, a separate change (embeddings are still stamped as raw HttpClient spans).
- **Logs / metrics dimensions** — unchanged from ADR 0009 (traces only; user id not a metric tag).
- **`Activity.Baggage`-based propagation** — deliberately rejected (external PII leak).

## Review

Critic pass (fresh-context adversarial review). The core design was confirmed **sound**: the
co-hosted single-TracerProvider observing in-grain spans, the server-span ordering / no-double-stamp,
the `[GenerateSerializer]` carrier serialization + placement in `Agent42.Agents`, the new-ADR-0010
(vs amending 0009) call, and the load-bearing assumption itself being appropriately *gated* rather
than asserted. Two MAJOR and two MINOR findings were raised; all accepted and fixed:

- **R1 (major) — accepted & fixed.** The original gate proved propagation only for a *scalar*
  request/response probe grain, but the real chat span is created inside `ChatAsync`'s
  `IAsyncEnumerable` streaming iterator (Orleans pumps it via repeated MoveNext RPCs). Step 1 now
  exercises the **real streaming path** (`grain.ChatAsync` + a `FakeChatClient` `[uctx:…]` echo read
  from `RequestContext` at the in-grain chat-client call site), and AC3 asserts propagation there.
- **R2 (major) — accepted & fixed.** The original "test-only probe grain in the test project" was an
  execution trap: `tests/Agent42.Tests` references `Microsoft.Orleans.TestingHost` but **not**
  `Microsoft.Orleans.Sdk`, so a grain defined there would lack codegen/discovery, and the cited
  exemplar hosts grains from `Agent42.Agents` (an exemplar-shape mismatch). The rewrite defines **no
  new grain** — it reuses `AgentGrain` + the existing `FakeChatClient` (a plain injected service,
  needing no codegen), dissolving the trap and the false "No src changes"/file-list gap.
- **R3 (minor) — accepted & fixed.** "Embedding calls appear only as raw HttpClient spans (still
  stamped)" was asserted; Azure SDK clients may emit `Azure.*` spans and suppress the HttpClient span,
  and that source isn't sampled. Now marked **unverified / prod-smoke**, with the chat-completion span
  (source `Agent42.Backend`) called out as the *primary, sampled* target.
- **R4 (minor) — accepted & fixed.** "In-request coverage for free" had no AC. Now explicitly grounded
  on AC1 by composition (identical processor + ambient) with the end-to-end demoted to AC8 prod-smoke.

## Implementation notes (divergences from plan)

Executed on branch `feature/otel-downstream-user-attributes` (stacked on PR #24, from
`feature/otel-user-attributes @ 436dd2d`; no drift). All steps landed; final suite **119 passed / 0
failed**, lint clean, build 0 errors, new-code coverage ~100% (per-file cobertura).

- **GATE result (Step 1):** Orleans 10.2 **does** propagate `RequestContext` into the streaming
  `ChatAsync` grain path by default — both propagation cases passed on the first run, so the reserve
  fallback (silo config / grain-call-filter pair) was **not** needed.
- **Middleware is a named delegate, not an inline lambda.** The plan's `app.Use(async (ctx,next)=>…)`
  was extracted to `src/Agent42.Backend/UserTelemetryMiddleware.cs` (`UserTelemetryMiddleware.InvokeAsync`,
  wired as `app.Use(UserTelemetryMiddleware.InvokeAsync)`), so AC5 tests the **real** delegate with a
  `DefaultHttpContext` + capturing `next` rather than a copy. New file not in the plan's file list.
- **Tests split into `UserTelemetryProcessorTests.cs`** (the plan's offered alternative) — processor
  (AC1/AC2/AC4), middleware (AC5 + a no-headers→local-dev case), and carrier `ToTags` present/absent/
  empty-roles cases. PR #24's `UserTelemetryTests.cs` left unchanged (AC6) and stays green.
- **AC9 is a real test, not just documented.** `NoBaggageWriteApi_IsUsedInTelemetryPath` (a `[Theory]`
  over Backend + Agents) source-scans for `SetBaggage`/`AddBaggage` and fails if either appears. Skips
  `obj/`/`bin/` generated output (critic L1 hardening).
- **RequestContext key** constant lives on the carrier: `UserTelemetryContext.RequestContextKey =
  "agent42.user-telemetry"`; the `user.*` tag constants moved onto the carrier too, and `CurrentUser`
  no longer holds any tag constant (single header→carrier→tags path).
- **CLAUDE.md not modified** — it carries no telemetry/observability claim this diff invalidates; the
  knowledge lives in ADR 0010 + the new gotcha. Critic confirmed this is defensible under hard rule 2.
- **Critic:** clean PASS; L1 (obj/bin scan) fixed, L2 (guard scope excludes ServiceDefaults) left as a
  noted non-gap (ServiceDefaults is generic and never touches user identity).
