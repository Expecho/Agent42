---
id: 0077
date: 2026-09-19
status: accepted
tags: [observability, opentelemetry, privacy, egress, infra]
amended_by: [0125]
---

# Export traces and metrics to Application Insights, with chat content stripped from the traces

> **Amended by [ADR 0125](0125-remove-fetch-web-page-and-context-dev.md) (2026-11-14).** The context.dev `?url=` half of the residual below is moot because context.dev is removed. A model-chosen URL's host and path can still reach App Insights on a `GuardedDocumentFetcher` span.

## Context

The Application Insights resource `agent-42-appinsights` (RG `rg-agent42`) was created on 2026-07-11
and had never received anything. The Azure Monitor block in `src/Agent42.ServiceDefaults/Extensions.cs`
was still the commented-out Aspire template. It is workspace-based (Log Analytics `agent-42-logs`,
90-day retention) and allows local auth, so connection-string ingestion needs no RBAC change.

The app's gen_ai spans carry full chat content, because `EnableSensitiveData` is on for the home OTLP
collector and the `/logs` console (ADR 0054). Application Insights has no hook for filtering attributes:
the exporter reads the `Activity` as it is. Keeping content out of it therefore means mutating spans
that the other sinks share.

## Decision

Export **traces and metrics, never logs**, to the existing resource, **alongside** the OTLP export.

- **Wiring.** The connection string flows from the `APPLICATIONINSIGHTS_CONNECTION_STRING` GitHub
  secret, through the `@secure()` Bicep param `appInsightsConnectionString`, to a container `secretRef`.
  This is the optional-secret shape `HINDSIGHT_API_KEY` uses. Nothing is provisioned.
- **One trace fan-out.** One `BatchActivityExportProcessor` feeds a fan-out exporter
  (`src/Agent42.ServiceDefaults/AzureMonitorTraceExport.cs`). Each batch goes through three stages in
  order:
  1. the OTLP trace exporter(s), which get the whole span
  2. an in-place strip of every gen_ai span
  3. the `AzureMonitorTraceExporter`

  The fan-out is registered **last** in the Backend's trace pipeline, by
  `BackendTelemetryExtensions.AddBackendTelemetry`.
- **The strip is an exact-key allowlist on spans that carry `gen_ai.operation.name`.** Everything else
  is dropped, whatever its prefix. The allowlist covers gen_ai metadata (model, token counts, tool names,
  ids, finish reasons), `user.*` and `agent42.application`. It drops:
  - the message, instruction, tool-definition and tool-I/O content keys
  - `gen_ai.request.stop_sequences`
  - `gen_ai.tool.description` and `gen_ai.agent.description`, which users write through uploaded skills
    and stored agents
  - any un-prefixed `AdditionalProperties` key

  Other spans (HTTP, Orleans) are untouched.
- **Metrics** go through `AddAzureMonitorMetricExporter`, the gen_ai histograms included.
- **No logs.** Warning+ log templates carry model-chosen search queries and URLs (`WebTools`,
  `SearxngWebSearch`, `ContextWebPageFetcher`), rendered because `IncludeFormattedMessage` is on.
- **Statsbeat off.** The container env sets `APPLICATIONINSIGHTS_STATSBEAT_DISABLED=true`, and so does
  every test that builds an exporter. The exporter option for it is internal in 1.9.0. **Live Metrics is
  off** and no sampler is installed.

## Alternatives considered

- **Send everything, content included.** Rejected: full prompts and replies would sit in Log Analytics
  for 90 days, in a store with a wider read surface than the home collector.
- **Also strip `user.*`.** Rejected: per-user attribution is the reason to have gen_ai spans there at
  all. ADR 0009 already accepts `user.*` in every sink.
- **Leave the spans unmutated, and give App Insights its own batch processor whose filtering exporter
  drops gen_ai spans whole.** This was the critic's alternative. It is simpler: no hand-built OTLP
  exporters, no ordering constraint, and no shared failure domain. It lost because it also removes
  per-span token and user attribution from App Insights, which is the point of the export. The user
  chose attribution and accepted the costs below.
- **A Bicep `existing` reference to read the connection string.** Rejected in favour of the secret
  pattern. A manually set env var is not viable: the Bicep container template declares the whole env
  array, so the next deploy would wipe it.
- **The Azure Monitor distro (`UseAzureMonitor`).** Rejected: it is a one-shot registration that cannot
  host the fan-out. It would also add Live Metrics, auto-sampling and log export.

## Consequences

- **Egress (ADR 0013, CLAUDE.md rule 13).** App Insights sits in the operator's own subscription and
  tenant, inside the Foundry trust boundary. No gen_ai span delivers prompt, completion,
  system-instruction, tool-definition or tool-I/O text there, and no log does either. It does receive
  `user.*` PII, making it a **third sink** for what ADR 0009 accepted for the two OTLP collectors.
  Statsbeat's Microsoft-owned egress is off. Turning Statsbeat back on is an egress change that must be
  recorded here.
- **Accepted residual: one model-chosen URL still reaches it on an HTTP span.** Non-gen_ai spans are
  not stripped, and OTel redacts only the query string. So the HTTP client span of
  `GuardedDocumentFetcher`, which fetches a model-chosen URL from inside the container
  (`read_web_document`), carries that URL's host and path to App Insights. The
  `http.client.request.duration` metric carries its `server.address`. The same URL is dropped from the
  `execute_tool` span as a tool argument. The other model-driven clients keep their model input in the
  query string: context.dev's `?url=` and SearXNG's `?q=`, both redacted. Closing the gap means
  suppressing that client's span, as the "never redacts the PATH" gotcha prescribes. That would also
  change what the home collector receives, which this ADR's scope excluded.
- **ADR 0054 is amended.** "In production the only sink is an OTLP export" is no longer true.
- **Shared failure domain for traces.** One batch worker exports sequentially. A stalled
  `otlp.example.com:4317` (OTLP timeout 10 s) delays App Insights batches, a slow App Insights delays
  the next OTLP batch, and both share the provider's shutdown budget. Metrics keep separate readers and
  are unaffected.
- **With a connection string set, the fan-out owns the trace OTLP exporters.** ServiceDefaults then
  registers none, so if `AddBackendTelemetry` were ever dropped, **every** trace would be lost, the home
  collector's included. Those exporters are built by hand from `IConfiguration` (endpoint, protocol,
  headers). `OTEL_BSP_*`, timeout and retry variables would need hand-wiring; nothing sets them today.
  Over `http/protobuf` (local dev only) the exporter also uses a plain `HttpClient`, so it skips the
  `IHttpClientFactory` client and its ServiceDefaults resilience handler. Production's gRPC path never
  used that client.
- **Ordering is load-bearing.** The fan-out must stay the last `OnEnd` reader, and `UseOtlpExporter` must
  never be introduced because it sorts itself to the end. `.agentic/gotchas.md` records the mechanics.
- **A library upgrade that adds a gen_ai attribute drops it from App Insights by default.** That is the
  intended fail-closed direction; add a wanted key by exact name.
- **Manual prerequisite.** Set the `APPLICATIONINSIGHTS_CONNECTION_STRING` repo secret. Without it the
  deploy still succeeds and the app stays OTLP-only.
