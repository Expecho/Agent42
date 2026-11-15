# Plan — Export traces and metrics to Application Insights, chat content stripped

planned-against: main @ 2d03758

## Overview

**Goal:** send Agent42's OpenTelemetry **traces and metrics** (not logs, see D5) to the **existing**
Application Insights resource `agent-42-appinsights` (RG `rg-agent42`), **in addition to** the
current OTLP export. App Insights must receive no chat content. The home collector and the `/logs`
console keep receiving exactly what they receive today.

**Why now:** the resource already exists (created 2026-07-11), and the app has never written to it:
the Azure Monitor block in `src/Agent42.ServiceDefaults/Extensions.cs` is the commented-out Aspire
template.

## Decisions taken in the planning interview

- **D1 — Use the existing resource and provision nothing.** The resource is workspace-based:
  Log Analytics `agent-42-logs` in `rg-agent42`, 90-day retention. Local auth is not disabled, so
  connection-string ingestion works **with no RBAC change**. Verified 2026-09-19 with
  `az resource show`. Neither `Microsoft.Insights/components` nor any workspace enters Bicep.
- **D2 — Connection string flows from a GitHub secret to a Bicep param to a container
  `secretRef`**, following the `HINDSIGHT_API_KEY` pattern. The user rejected a Bicep `existing`
  reference. A manual env var is not viable: the Bicep container template declares the whole env
  array, so the next deploy wipes it.
- **D3 — Strip chat content for App Insights only.** App Insights keeps the gen_ai spans, with token
  counts, durations, model, tool names and `user.*`, but with no content. The home OTLP collector
  and `/logs` still get content.
  - Rejected: sending everything (full prompts in Log Analytics for 90 days).
  - Rejected: also stripping `user.*`.
  - **Rejected after critic review:** the non-mutating alternative, where trace OTLP stays unchanged
    and App Insights gets its own batch processor whose filtering exporter drops gen_ai spans whole.
    That design needs no OTLP move, has no ordering constraint and couples no failure domains. The
    user chose to keep **per-span token and user attribution** in App Insights and accepted the
    trade-offs in D6. The ADR must record this alternative and why it lost.
- **D4 — Keep the home collector.** App Insights is added alongside the env-var OTLP endpoint
  (`deploy.yml`: `otlpEndpoint="http://otlp.example.com:4317"`) and the optional
  `Otel:LocalCollector:*`.
- **D5 — No logs to App Insights.** The app's own Warning+ log templates carry model-chosen
  conversation-derived text, rendered because `IncludeFormattedMessage = true` and kept because prod
  runs at `Default: Warning` (`src/Agent42.Backend/appsettings.json`):
  - search queries: `src/Agent42.Agents/Web/AgentGrain.WebTools.cs` (`LogError … '{Query}'`) and
    `src/Agent42.Backend/Web/SearxngWebSearch.cs`
  - model-chosen URLs: `src/Agent42.Backend/Web/ContextWebPageFetcher.cs`

  So **no** `AddAzureMonitorLogExporter`. Logs keep going to OTLP and `/logs`.
- **D6 — Accepted trade-off: shared failure domain for traces.** One batch worker exports
  sequentially: OTLP (env), then OTLP (local, when configured), then strip, then App Insights. A
  stalled `otlp.example.com:4317` (OTLP timeout defaults to 10 s) delays App Insights batches, and a
  slow App Insights delays the next OTLP batch. Both also share the provider's shutdown budget.
  Metrics are unaffected because they keep separate readers. Record this in the ADR.
- **D7 — Statsbeat off.** The Azure Monitor exporter's internal Statsbeat defaults to on. It sends
  SDK telemetry to **Microsoft-owned** ingestion keys (`westeurope-5` /
  `westus-0.in.applicationinsights.azure.com`) and fetches config from `data.stats.monitor.azure.com`.
  That egress leaves the operator's boundary, which rule 13 cares about. Turn it off with
  `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true` in the container env, and in every test host that
  builds an Azure Monitor exporter, so `dotnet test` makes no calls to Microsoft endpoints.
  (Planner-decided; conservative per rule 13. Flip only by recording the egress in the ADR.)

## Context & constraints (evidence the executor cannot cheaply re-derive)

### Spike results (2026-09-19, file-based app outside the repo)

The spike ran OpenTelemetry 1.18.0 (the repo pin) with Azure.Monitor.OpenTelemetry.Exporter 1.9.0
(latest stable; its nuspec pins OpenTelemetry.Extensions.Hosting **1.18.0**, verified by the critic).

- **K1: no attribute-filter hook.** The public surface is:
  - `AzureMonitor{Trace,Log,Metric}Exporter`, which are sealed and have a public
    `ctor(AzureMonitorExporterOptions)`
  - the per-signal `AddAzureMonitor{Trace,Metric,Log}Exporter` extensions
  - `UseAzureMonitorExporter`

  The options only control sampling, storage, live metrics, standard metrics and perf counters. So
  stripping must happen on the `Activity`, before the exporter reads it.
- **K2: in-place stripping is race-free only if every reader runs before it, on the same thread.**
  Each batch processor exports the **same `Activity` instances** on its own worker thread. The shape
  that was measured to work is **one** `BatchActivityExportProcessor` over a fan-out exporter: inner
  OTLP trace exporter(s) first, **then** strip, **then** `AzureMonitorTraceExporter`. Spike output:
  `OTLP: content=True service.name=True` / `AI: span=True content=False tokens=True role=True`.
- **K3: `Batch<Activity>` is single-pass.** A batch drains a circular buffer, so a second
  enumeration yields **0 items** even though `Count` still reads 1. The second exporter then posted
  only `_OTELRESOURCE_` and returned `Success`, with no error. **Materialise the batch once, then
  hand each inner exporter a fresh `new Batch<Activity>(array, array.Length)`.**
- **K4: inner exporters need `ParentProvider`, and its setter is internal.** Without it there is no
  resource: no `ai.cloud.role`, and no `service.name` on OTLP.
  - **Reflection-free fix (measured):** a no-op anchor that subclasses `CompositeProcessor<Activity>`
    and does nothing in `OnStart`/`OnEnd`. It holds `SimpleActivityExportProcessor` children that
    wrap the inner exporters, and the provider propagates `ParentProvider` through it.
  - The anchor **owns** the inner exporters' shutdown and dispose. The fan-out never shuts them down
    or disposes them.
  - The anchor is registered **immediately after** the fan-out's processor. That gives the ordering
    rule: *"the fan-out is the last processor that reads spans; only the no-op anchor follows it."*
    `CompositeProcessor.OnShutdown` shuts children down sequentially, in order, so the final batch
    drains before the inner exporters shut down (critic-verified).
- **K5: no sampler.** The stock `AddAzureMonitorTraceExporter` installs a rate-limited sampler
  (5 traces/s by default), and it dropped even the **first** span in the baseline spike. Building
  the exporter directly (K2) installs none. Keep it that way, with `SamplingRatio` left at 1.0.
- **K6: no content in span events.** M.E.AI 10.9.0 contains no `AddEvent`, `AddException` or
  `new ActivityEvent` (critic, decompiled). Errors reach only `SetStatus(Error, ex.Message)`, and the
  Azure Monitor exporter 1.9.0 never reads `StatusDescription`. AC2 stays as the regression guard.

### What "content" means: exact-key allowlist (critic M1)

With `EnableSensitiveData`, M.E.AI 10.9.0 copies every `ChatOptions.AdditionalProperties` and
`ChatResponse.AdditionalProperties` entry onto the span **under its raw key**. M.E.AI.OpenAI already
writes `EndUserId` and `Error` there. So a `gen_ai.`-prefix denylist, or `gen_ai.*` wildcards, would
leak. The rule is:

- On spans that carry `gen_ai.operation.name` (`chat`, `execute_tool`, and any other operation),
  keep **only exact keys** from the allowlist below. Drop every other tag, whatever its prefix.
- Spans without `gen_ai.operation.name` (HTTP server and client, Orleans) are untouched. `url.full`
  query redaction stays governed by the existing gotcha.
- **Allowlist.** Metadata keys extracted from the 10.9.0 / Agents.AI 1.19.0 DLLs, plus the app's
  own tags:
  - `gen_ai.operation.name`, `gen_ai.provider.name`, `gen_ai.conversation.id`,
    `gen_ai.output.type`, `gen_ai.token.type`, `gen_ai.embeddings.dimension.count`
  - `gen_ai.request.model`, `gen_ai.request.choice.count`, `gen_ai.request.frequency_penalty`,
    `gen_ai.request.max_tokens`, `gen_ai.request.presence_penalty`, `gen_ai.request.seed`,
    `gen_ai.request.stream`, `gen_ai.request.temperature`, `gen_ai.request.top_k`,
    `gen_ai.request.top_p`, `gen_ai.request.image.height`, `gen_ai.request.image.width`
  - `gen_ai.response.model`, `gen_ai.response.id`, `gen_ai.response.finish_reasons`,
    `gen_ai.response.time_to_first_chunk`
  - `gen_ai.usage.input_tokens`, `gen_ai.usage.output_tokens`,
    `gen_ai.usage.cache_read.input_tokens`, `gen_ai.usage.reasoning.output_tokens`,
    `gen_ai.usage.input_audio_tokens`, `gen_ai.usage.input_text_tokens`,
    `gen_ai.usage.output_audio_tokens`, `gen_ai.usage.output_text_tokens`
  - `gen_ai.tool.name`, `gen_ai.tool.type`, `gen_ai.tool.call.id`
  - `gen_ai.agent.id`, `gen_ai.agent.name`
  - `gen_ai.realtime.output_modalities`, `gen_ai.realtime.received_modalities`,
    `gen_ai.realtime.session_kind`, `gen_ai.realtime.voice`
  - `server.address`, `server.port`, `error.type`
  - `user.id`, `user.name`, `user.email`, `user.full_name`, `user.roles`, `user.idp` (constants in
    `src/Agent42.Agents/Telemetry/UserTelemetryContext.cs`)
  - `agent42.application` (`UsageSpanProjector.ApplicationTag`)

  Reference the existing constants for the app's own tags rather than re-typing them.
- **Deliberately dropped:**
  - content: `gen_ai.input.messages`, `gen_ai.output.messages`, `gen_ai.system_instructions`,
    `gen_ai.tool.definitions`, `gen_ai.tool.call.arguments`, `gen_ai.tool.call.result`,
    `gen_ai.tool.message`
  - free text: `gen_ai.request.stop_sequences`
  - user-written: `gen_ai.tool.description` and `gen_ai.agent.description` (uploaded skills and
    agents, ADR 0049/0070)
- A library upgrade that adds a new attribute is dropped by default. That is the intended
  fail-closed direction. If a dropped key turns out to be wanted, add it by exact name.

### Registration order and the API split (critic M4, M7)

- **K7: call order is pipeline order in 1.18.0.** `TracerProviderSdk` stable-sorts processors by
  `PipelineWeight`. Only `UseOtlpExporter` sets a non-zero weight (10000). `AddOtlpExporter`,
  instance `AddProcessor` and factory `AddProcessor` all keep weight 0 (critic, decompiled). **Never
  introduce `UseOtlpExporter`**: it would silently reorder the pipeline. Put this in the gotcha.
- **K8: the fan-out must run after every `OnEnd` reader.** A batch worker can export an activity as
  soon as it is enqueued, on the timer tick or when a full batch is reached. That can happen while the
  ending thread is still running `OnEnd` of processors registered after the batch processor.
  `UsageSpanProcessor` (Program.cs) and `LiveSpanProcessor` (`AddLiveTelemetryCapture`) register
  **after** `AddServiceDefaults()`, so the fan-out must register after both. Verified: no reader
  touches an `Activity` after `OnEnd` on another thread. `UsageSpanProcessor` and `LiveSpanProcessor`
  both project synchronously.
- **Explicit API split:**
  - **ServiceDefaults `ConfigureOpenTelemetry`** keeps today's behaviour exactly when
    `APPLICATIONINSIGHTS_CONNECTION_STRING` is empty. When it is set, it registers **no trace
    exporter** (OTLP logs and metrics stay as today) and adds `AddAzureMonitorMetricExporter` to
    metrics.
    - That registration must **not** sit behind the early `return` in `AddOpenTelemetryExporters`
      that fires when neither OTLP endpoint is set. An App-Insights-only setup must still export.
  - **ServiceDefaults also exposes a public trace fan-out registration**, e.g.
    `AddTraceExportFanOut(...)`, taking the `ExportProcessorType`: Batch in prod, Simple in the
    ordering test.
    - When the connection string is empty, it does nothing.
    - When it is set, it registers the fan-out processor over the configured OTLP endpoints plus
      Azure Monitor, then the anchor.
  - **Backend: one composition method** (new, in `src/Agent42.Backend/Telemetry/`) holds today's
    `Program.cs` `WithTracing` block (the `OtelSourceName` source, `UserTelemetrySpanProcessor`,
    `UsageSpanProcessor`), then `AddLiveTelemetryCapture()`, then the fan-out registration, in that
    order. `Program.cs` calls it right after `AddServiceDefaults()`. Tests call the same method.
  - If the late call is ever missing while a connection string is set, **all** traces are lost,
    home collector included. AC5 pins this.
- **Why AC6 uses Simple:** with a Batch fan-out, a misordered registration strips content on the
  worker thread and `/logs` still sees content almost every time, so the test would pass against the
  bug. With `ExportProcessorType.Simple`, a misordered fan-out strips **synchronously, before**
  `LiveSpanProcessor.OnEnd`, and the test fails deterministically. No reflection, no timing.

### Rebuilding the trace OTLP exporter by hand (critic M6 details)

- Build inner OTLP exporters from the **`IConfiguration`-bound** `OtlpExporterOptions`, meaning the
  same endpoint, protocol and headers keys ServiceDefaults reads today (`OTEL_EXPORTER_OTLP_*` via
  `IConfiguration`, and `Otel:LocalCollector:*`). The gRPC pin (`OTEL_EXPORTER_OTLP_PROTOCOL=grpc`,
  container-app.bicep) must still apply.
  - Don't rely on `IOptionsFactory<OtlpExporterOptions>` being config-bound: that only happens when
    another signal's `AddOtlpExporter` happened to register the factory.
  - Binding explicitly from `IConfiguration` is the safe path.
- The public `OtlpTraceExporter(OtlpExporterOptions)` ctor reads its internal `SdkLimitOptions` and
  `ExperimentalOptions` (retry, attribute limits) from **process env**, not `IConfiguration`. In prod
  these are the same source (container env vars). In tests they differ, so tests must not depend on
  them. Nothing in the repo sets `OTEL_BSP_*`, `OTEL_EXPORTER_OTLP_TIMEOUT` or retry vars today
  (grep, 2026-09-19). Use the `BatchExportProcessor` defaults and note that `OTEL_BSP_*` would now
  need hand-wiring.

### Governing ADRs, gotchas and rules

- **ADR 0009** accepts `user.*` PII going "to both OTLP collectors". App Insights becomes a third
  sink for `user.*`, and the ADR must say so.
- **ADR 0013 and rule 13:** App Insights is in the operator's own subscription and tenant (the
  Foundry trust boundary). Under D3, D5 and D7 it receives **no prompt, completion, system
  instruction, tool-definition or tool-I/O text and no logs**, and Statsbeat's Microsoft-owned egress
  is off. Record all of that explicitly.
- **ADR 0031** (usage dashboard) is unaffected as long as K8 holds.
- **ADR 0054** says "in production the only sink is an OTLP export". That becomes false: amend it
  and add an amended-by note.
- **ADR 0029** (config health): every optional manual-prerequisite secret has a `PresenceCheck` in
  `src/Agent42.Backend/Configuration/ConfigHealth.cs`. Add one for
  `APPLICATIONINSIGHTS_CONNECTION_STRING` in the optional group.
- **Gotchas:**
  - "OTel redacts an HttpClient span's query string by default": extend the wording, since App
    Insights is now another sink a future un-redaction would leak to.
  - "The gen_ai metrics ride a meter named after `sourceName`": the gen_ai histograms now also reach
    App Insights. That is intended; update the stale comments (see Affected areas).
  - "A live-telemetry test host captures from a PROCESS-GLOBAL source": every new test class that
    builds this pipeline or emits on `OtelSourceName` joins `[Collection("LiveTelemetry")]` and is
    added to the class list in `tests/Agent42.Tests/LiveTelemetryCollection.cs`.
  - "Nothing in the suite runs `Program.cs`": this is why the composition method exists.
  - "A scripted whole-file rewrite silently converts CRLF to LF": edit with Edit, not scripted
    rewrites.
- **Rule 9 (S3776 ≤ 15):** the fan-out, anchor, strip allowlist and registration go in a **new
  file** in `src/Agent42.ServiceDefaults/`, not in `Extensions.cs`.
- **Hard rule 4:** `dotnet add package Azure.Monitor.OpenTelemetry.Exporter`, pinned in
  `Directory.Packages.props`. If the resolved latest demands OpenTelemetry > 1.18.0, **stop and
  report**. Never bump OTel as a side effect.
- **ADR number:** `.agentic/specs/plan-deferred-tool-loading.md` also reserves 0077. Use the **next
  free number at `/decide` time**.

## Affected areas (verified against the tree @ 2d03758)

| File | Change |
|------|--------|
| `Directory.Packages.props`, `src/Agent42.ServiceDefaults/Agent42.ServiceDefaults.csproj` | add and pin `Azure.Monitor.OpenTelemetry.Exporter` |
| `src/Agent42.ServiceDefaults/Extensions.cs` | remove the commented Aspire block. With a connection string: skip trace OTLP and add the Azure Monitor metric exporter, outside the early return. Fix the stale "reads … env vars" comment (it reads `IConfiguration`) |
| `src/Agent42.ServiceDefaults/<new>` (e.g. `AzureMonitorTraceExport.cs`) | fan-out exporter (K2, K3), exact-key strip, anchor (K4), public `AddTraceExportFanOut` |
| `src/Agent42.Backend/Telemetry/<new>` (e.g. `BackendTelemetryExtensions.cs`) | the composition method (K8) |
| `src/Agent42.Backend/Program.cs` | replace the `WithTracing` block and the `AddLiveTelemetryCapture()` call with the composition-method call; update comments |
| `src/Agent42.Backend/Telemetry/LiveTelemetryCaptureExtensions.cs` | stale comments: the gen_ai meter "reaches the OTLP collector", but now App Insights too |
| `src/Agent42.Backend/Configuration/ConfigHealth.cs` | optional-group `PresenceCheck` for `APPLICATIONINSIGHTS_CONNECTION_STRING` |
| `infra/main.bicep` | `@secure() param appInsightsConnectionString string = ''`, passed to the module |
| `infra/modules/container-app.bicep` | conditional secret `appinsights-connection-string` + env `APPLICATIONINSIGHTS_CONNECTION_STRING` via `secretRef` (hindsight pattern); unconditional `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true` |
| `.github/workflows/deploy.yml` | `appInsightsConnectionString="${{ secrets.APPLICATIONINSIGHTS_CONNECTION_STRING }}"` |
| `tests/Agent42.Tests/OtelConfigurationTests.cs` | AC1, AC5 |
| `tests/Agent42.Tests/AzureMonitorTraceExportTests.cs` (new, `[Collection("LiveTelemetry")]`) | AC2, AC3, AC4, AC6 |
| `tests/Agent42.Tests/LiveTelemetryCollection.cs` | add the new class to the documented list |
| `tests/Agent42.Tests/LiveTelemetryCaptureWiringTests.cs` | it asserts `AddLiveTelemetryCapture();` in Backend `Program.cs` and no `addInstrumentation: true`. Re-point it at the composition file, plus a check that `Program.cs` calls the method. The "Backend never asks capture for instrumentation" rule must survive the move without being weakened |
| `tests/Agent42.Tests/ConfigHealthTests.cs` (or wherever presence checks are tested) | the new presence check |
| bicep/workflow source-text test (AC7) | copy the `EasyAuthExclusionTests` `ReadAllText`/`RepoRoot` shape |
| `.agentic/decisions/<next>-*.md` via `/decide`; `0054-live-otel-console.md` | new ADR (amends 0054, extends 0009, records D3/D5/D6/D7 and the rejected alternative); amended-by note |
| `.agentic/gotchas.md` via `/gotcha` | one entry, "stripping for one sink": K2, K3, K4, K5, K7, K8; extend the query-redaction entry |
| `CLAUDE.md` | Manual-prerequisites row "`APPLICATIONINSIGHTS_CONNECTION_STRING`: missing means no App Insights export; the app boots"; the Telemetry feature-map row gets the new ADR |
| `README.md` | secrets list |

No `wwwroot/js/` change, so no `sw.js` bump. No grain-state change.

## Steps

1. **Branch** `feature/app-insights-telemetry` from `main`. If `main` has moved past `2d03758`,
   diff `Extensions.cs`, `Program.cs`, the `Telemetry/` folder, `container-app.bicep` and
   `deploy.yml` first.
2. **Add the package** (hard rule 4). Verify: `dotnet build Agent42.slnx` is clean, and
   `dotnet list src/Agent42.ServiceDefaults package --include-transitive` shows OpenTelemetry
   1.18.0.
3. **ServiceDefaults: fan-out, strip, anchor, metric exporter and `ConfigureOpenTelemetry` split.**
   Build the exporter with `EnableLiveMetrics = false` explicitly (Live Metrics streams documents
   outside this pipeline) and no sampler. Tests set `DisableOfflineStorage = true` and
   `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true` for the test process. Capture Azure Monitor output
   through a loopback `IngestionEndpoint`, as the spike did. Write AC1–AC5 alongside.
   Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~OtelConfigurationTests|FullyQualifiedName~AzureMonitorTraceExportTests"`.
4. **Backend composition method + `Program.cs` + wiring-test update + ConfigHealth check.** Write AC6
   and AC8, and update `LiveTelemetryCaptureWiringTests`.
   Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AzureMonitorTraceExportTests|FullyQualifiedName~LiveTelemetryCaptureWiringTests|FullyQualifiedName~ConfigHealth|FullyQualifiedName~OtelConfigurationTests"`.
5. **Bicep + workflow** (D2, D7). Write AC7. Verify: `az bicep build --file infra/main.bicep`, plus
   `dotnet test Agent42.slnx --filter "FullyQualifiedName~AppInsightsWiring"` (or whatever the AC7
   class is named).
6. **Docs:** `/decide`, `/gotcha`, the CLAUDE.md and README rows, the stale comments, then
   `/sync-docs`. Verify: `node .agentic/scripts/check-doc-paths.mjs`.
7. **Full gates:** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
   `dotnet format Agent42.slnx --verify-no-changes`,
   `node .agentic/scripts/check-new-code-coverage.mjs`. Then open the MR.
8. **Manual, handed to the user (the agent does not execute it).** This sets a repo secret; no RBAC
   is involved.
   ```
   gh secret set APPLICATIONINSIGHTS_CONNECTION_STRING --repo Expecho/Agent42 --body "$(az resource show -g rg-agent42 -n agent-42-appinsights --resource-type Microsoft.Insights/components --query properties.ConnectionString -o tsv)"
   ```
   Without it, the deploy still succeeds: the param defaults to empty and the app stays OTLP-only.
9. **Post-deploy smoke (prod only, read-only queries against `agent-42-logs`),** after one chat turn
   with a tool call:
   - rows appear in `AppDependencies`/`AppRequests` and `AppMetrics`, with `AppRoleName` equal to
     `OTEL_SERVICE_NAME`
   - `AppDependencies | where Properties has "gen_ai.input.messages" or Properties has "gen_ai.tool.call.result"`
     returns **0**
   - a chat dependency row carries `gen_ai.usage.input_tokens` and `user.id`
   - `AppTraces` has **no** rows from this app (D5)
   - the home collector still shows `gen_ai.input.messages`

   Record the results in the MR.

Order: 2 → 3 → 4. Step 5 can run in parallel with 3–4 (no C#). Step 6 comes after step 4.

## Acceptance criteria → tests

| # | Acceptance criterion | Test (proposed) | File |
|---|----------------------|-----------------|------|
| AC1 | With no connection string, the pipeline is unchanged: the existing `ConfigureOpenTelemetry_*` tests stay green, no Azure Monitor exporter is added, and `AddTraceExportFanOut` is a no-op | `NoConnectionString_AddsNoAzureMonitorExport` | `OtelConfigurationTests.cs` |
| AC2 | A **real** M.E.AI chat span (`UseOpenTelemetry(EnableSensitiveData = true)` over a fake inner client) carries sentinels in the prompt, reply, system instructions, tool args, tool result and an un-prefixed `AdditionalProperties` key. The OTLP-side inner exporter receives **every** sentinel. The Azure Monitor payload contains **none**, yet still carries `gen_ai.usage.input_tokens`, `gen_ai.request.model` and `user.id`. Assertions are scoped to the test's own trace id | `StripsChatContent_OtlpKeepsIt` | `AzureMonitorTraceExportTests.cs` |
| AC3 | The exact-key strip, as a pure unit test: an unknown `gen_ai.foo`, an un-prefixed key on a gen_ai span, `gen_ai.request.stop_sequences` and `gen_ai.tool.description` are dropped; every allowlisted key survives; a span **without** `gen_ai.operation.name` is untouched | `Strip_IsExactKeyAllowlist_OnGenAiSpansOnly` | same |
| AC4 | The fan-out, tested directly with a hand-built `Batch<Activity>` of N spans: **both** inner exporters receive N items (K3 regression), and the Azure Monitor payload's `ai.cloud.role` equals the service name (K4) | `FanOut_DeliversEverySpanToEveryExporter_WithResource` | same |
| AC5 | Connection string plus both OTLP endpoints: the trace and metric providers build, and a span emitted through `ConfigureOpenTelemetry` + composition method reaches OTLP **and** App Insights. App-Insights-only (no OTLP endpoints): spans and metrics still reach App Insights. OTLP-only: exactly today's behaviour | `WithAppInsightsAndOtlp_SpansReachBoth`, `AppInsightsOnly_StillExports` | `OtelConfigurationTests.cs` |
| AC6 | Composition method with the fan-out as `ExportProcessorType.Simple`: `LiveTelemetryBuffer` holds `gen_ai.input.messages` for a span whose Azure Monitor copy is stripped. This fails deterministically if the fan-out is registered before `LiveSpanProcessor`. Shutdown delivers a pending batch to both sinks (Batch mode, `ForceFlush`-free dispose) | `FanOutRegisteredLast_LiveConsoleKeepsContent`, `Shutdown_DrainsToBothSinks` | `AzureMonitorTraceExportTests.cs` |
| AC7 | Source text: `container-app.bicep` wires `APPLICATIONINSIGHTS_CONNECTION_STRING` **only** via `secretRef`, conditional on a non-empty param, and sets `APPLICATIONINSIGHTS_STATSBEAT_DISABLED` to `'true'`; `main.bicep` declares the param `@secure()` and passes it through; `deploy.yml` passes `secrets.APPLICATIONINSIGHTS_CONNECTION_STRING` | `AppInsightsWiring_*` | new small class (EasyAuthExclusionTests file-reading shape) |
| AC8 | Config health reports `APPLICATIONINSIGHTS_CONNECTION_STRING` as an optional presence check, never echoing its value | existing ConfigHealth test style | the ConfigHealth test file |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 2d03758)

| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| `dependencies.add_policy` | latest-stable | `dotnet add package` (step 2) |
| build / tests / `dotnet format` / doc-path integrity | must pass | `.github/workflows/pr-checks.yml` |

## Out of scope

- Provisioning or reconfiguring the App Insights resource or its workspace, retention, daily cap,
  alerts or workbooks.
- Logs to App Insights (D5). Scrubbing the query and URL log templates.
- Changing what the home collector or `/logs` receives. `EnableSensitiveData` stays as is.
- Dropping `user.*` from any sink.
- Entra ingestion auth / `DisableLocalAuth` (needs RBAC, hard rule 5).
- The Azure Monitor distro (`UseAzureMonitor`): standard metrics, Live Metrics, auto-sampling,
  extra instrumentation. Its one-shot registration can't host the fan-out.

## Review

Critic pass, 2026-09-19. Verdict: design sound, no blockers, 7 major and 7 minor findings. Handling:

- **Verified by the critic, no action:** K7 ordering semantics; no off-thread `Activity` readers;
  no content in span events (M.E.AI 10.9.0); exporter 1.9.0 pins OTel 1.18.0.
- **M1 (not fail-closed: un-prefixed `AdditionalProperties` keys, wildcards):** fixed. The strip is
  now an exact-key allowlist on gen_ai spans, `stop_sequences` and `tool.description` /
  `agent.description` are dropped, `response.time_to_first_chunk` is added, and AC2/AC3 are
  extended.
- **M2 (logs carry search queries and URLs):** the user chose **no logs to App Insights** (D5).
- **M3 (Statsbeat egress to Microsoft):** fixed. Disabled in the container env and in test hosts
  (D7), pinned in AC7.
- **M4 (registration ambiguity, untested configurations):** fixed. The API split is explicit, the
  early-return trap is named, and AC5 covers App-Insights+OTLP and App-Insights-only.
- **M5 (`LiveTelemetryCaptureWiringTests` would break):** fixed. Added to Affected areas, with the
  instruction to re-point it without weakening it.
- **M6 (shared failure domain; simpler non-mutating alternative):** put to the user, who **kept the
  strip design**. The trade-off is recorded as D6, the rejected alternative is recorded in D3 and
  goes into the ADR, and the hand-built OTLP option details are in their own section.
- **M7 (AC6 couldn't catch misordering):** fixed with an `ExportProcessorType` parameter and a
  Simple-mode test.
- **m1 (K4 vs K8 wording):** fixed ("last reader; only the no-op anchor follows").
- **m2 (ADR 0077 collision):** fixed ("next free number at `/decide` time").
- **m3 (stale comments, collection list):** added to Affected areas.
- **m4 (ConfigHealth):** in scope as AC8.
- **m5 (test hygiene):** trace-id scoping, direct fan-out unit test, `DisableOfflineStorage` are in
  AC2, AC4 and step 3.
- **m6 (AC7 covers only one file):** extended to `main.bicep` and `deploy.yml`.
- **m7 (vague verify commands):** concrete `--filter`s and `check-doc-paths.mjs`.
- **Rejected findings:** none.

## Divergence from plan (implementation notes, 2026-09-19)

Implemented on `feature/app-insights-telemetry` off `main` @ `2d03758`, with no drift. Where the
shipped code differs from the plan text:

- **One composite processor instead of "fan-out processor + anchor registered right after it" (K4).**
  `TraceExportFanOutProcessor : CompositeProcessor<Activity>` has these children:
  - first, the Batch or Simple export processor over the fan-out exporter
  - then one `SimpleActivityExportProcessor` per inner exporter, which exists only to receive
    `ParentProvider` and to own shutdown and disposal

  `OnStart` is a no-op, and `OnEnd` forwards to the first child only. The processors are the same as in
  the spike, but there is one registration, so the "the anchor directly follows the fan-out" ordering
  rule and the shared per-provider state between two factories are gone. Shutdown runs the children in
  order, so the export processor drains first. `Shutdown_DrainsToBothSinks` and
  `FanOut_DeliversEverySpanToEveryExporter_WithResource` pin it.
- **Statsbeat: env var only.** `AzureMonitorExporterOptions.EnableStatsbeat` exists but is internal in
  1.9.0; the first build failed on it. D7's env var is the only switch. The `TelemetryLoopback` test
  helper sets it for the test process.
- **The allowlist is split across two projects.** ServiceDefaults holds the gen_ai metadata keys
  (`AzureMonitorTraceExport.GenAiMetadataKeys`). ServiceDefaults cannot reference `Agent42.Agents`, so
  the `user.*` and `agent42.application` keys come from the existing constants through
  `BackendTelemetryExtensions.AppInsightsAllowedTags` and are passed into `AddTraceExportFanOut`.
- **Azure Monitor options are default-named `IOptions`.** One `Configure<AzureMonitorExporterOptions>`
  (connection string, `EnableLiveMetrics = false`, `SamplingRatio = 1`) is shared by
  `AddAzureMonitorMetricExporter()` and the fan-out, which resolves it through `IOptionsMonitor`. Tests
  layer `DisableOfflineStorage = true` on top. AC1 asserts that no `IConfigureOptions` is registered.
- **Composition method:** `AddBackendTelemetry(usageWriter, fanOutProcessorType)` in
  `src/Agent42.Backend/Telemetry/BackendTelemetryExtensions.cs`. `UsageApplication` moved there from
  `Program.cs`.
- **Hand-built OTLP targets:**
  - endpoint, protocol and headers are bound from `IConfiguration`
  - for `http/protobuf`, `v1/traces` is appended, which `AddOtlpExporter` does only for env-sourced
    endpoints
  - the local collector reuses `Extensions.ConfigureLocalCollector`, now an internal static method
- **Tests:** AC5 uses a Kestrel loopback (`tests/Agent42.Tests/TelemetryLoopback.cs`), one
  instance over HTTP/1.1 for App Insights and OTLP/HTTP and one over h2c for OTLP/gRPC.
  - Exercised against a wire, not mocked: the env endpoint over HTTP and over gRPC with headers (the
    production shape, added after the critic's M1), and the local collector over gRPC.
  - The gRPC case asserts the exact method path, because a wrongly appended `v1/traces` still reaches a
    loopback that matches only a suffix. Each of these was confirmed to fail
  against a deliberate mutation:
  - re-enumerating the batch (AC4)
  - stripping before the OTLP export (AC2)
  - registering the fan-out before capture (AC6)
  - placing the App Insights metric registration behind the early return (AC5)
- **ADR number:** 0077. `plan-deferred-tool-loading.md` also names 0077 and must renumber when it runs.

### Critic pass on the diff (2026-09-19): no blockers

- **M1 (production gRPC env path untested):** fixed with
  `WithAppInsights_GrpcEnvEndpoint_ReachesTheCollectorWithHeaders`. It was confirmed to fail when
  `v1/traces` is appended over gRPC.
- **M2 (ADR's absolute "no tool-I/O text" claim):** `GuardedDocumentFetcher`'s HTTP span carries a
  model-chosen host and path. This is recorded as an accepted residual in ADR 0077 and in the
  query-redaction gotcha. Suppressing that span would change what the home collector receives, which
  is out of scope.
- **L1 (a composite as the first processor swallows later ones):** a sentence added to the fan-out
  gotcha.
- **L2 (stale comment):** fixed.
- **L3 (dead `IsConfigured`):** deleted.
- **L4 (plain `HttpClient` over http/protobuf):** noted in the ADR and in the code comment.
