---
id: 0031
date: 2026-07-22
status: accepted
tags: [telemetry, opentelemetry, usage, tokens, dashboard, grains, cosmos, cross-user, easy-auth, egress]
---

# Token-usage dashboard fed from OpenTelemetry, stored in a global grain

> **Amended by [ADR 0104](0104-prompt-prefix-caching.md) (2026-10-19): cached and reasoning tokens are NOT always 0.** The
> M.E.AI OTel emitter tags `gen_ai.usage.cache_read.input_tokens` and the reasoning count whenever the provider's
> `UsageDetails` carry them (`ChatSpan_CarriesCachedAndReasoningTokens`); this ADR's spike provider simply reported
> none. The dashboard stays OTel-only; only its note and doc comments changed.

## Context
There was no in-app view of how many tokens the app consumes, by whom, on which model. A sibling repo
(`AI.Telemetry.Dashboard`) already solves this shape for many apps: it ingests OpenTelemetry GenAI traces
into Postgres and renders headline tiles + tokens-by-model + tool-calls-by-name + usage-by-user tables with
range filters. We want the **same data and visual style** for Agent42 alone, reachable by any signed-in
user, without a new datastore.

Agent42 already emits the raw material: the chat `IChatClient` is `UseOpenTelemetry`-wrapped
(`ChatClientFactory.cs`, source `Agent42.Backend`), and `UserTelemetrySpanProcessor` (ADR 0010) stamps
`user.*` onto in-grain spans. A **spike (2026-07-22)** confirmed against Microsoft.Agents.AI 1.13.0 /
Microsoft.Extensions.AI 10.6.0 that: `chat`, `execute_tool`, and `orchestrate_tools` spans all emit on the
already-registered `Agent42.Backend` source; `chat` spans carry `gen_ai.usage.input_tokens` /
`output_tokens`; `execute_tool` spans carry `gen_ai.tool.name`; and `orchestrate_tools` re-reports the
**sum** of its child chat spans (counting it would double-count). Cache-read and reasoning token attributes
are **not** emitted, and there is **no** `gen_ai.agent.name` on the `RunAsync` path.

## Decision
A dedicated full-page usage view, fed **solely from the OTel spans already emitted** — no manual token
counter in `AgentGrain` — and stored in Cosmos **without a new container**.

- **Ingestion is an in-process OTel processor + background flush, not an OTLP receiver.** A
  `UsageSpanProcessor : BaseProcessor<Activity>` overrides `OnEnd` (usage is set at span completion, unlike
  the `OnStart` identity processor), projects the span via the pure `UsageSpanProjector`, and enqueues to a
  **bounded, drop-on-full** `Channel<UsageEvent>`. `UsageIngestionService` (a `BackgroundService`) drains it
  on a **≥30 s** cadence into the rollup grain. `OnEnd` never blocks or does I/O — usage telemetry must never
  back-pressure a chat turn. Backend-only, so the dashboard is empty offline.
  (Corrected 2026-08-27, ADR 0054 — this decision stands; only the parenthetical reason below was
  overtaken by later work: DevHost now *has* an OTel pipeline — instrumentation and live capture,
  no exporter. The dashboard is still empty offline, but because `UsageSpanProcessor` and
  `UsageIngestionService` are registered by the Backend alone, not because there is nothing to observe.)
- **The projection rule is load-bearing:** count tokens **only** from `gen_ai.operation.name == "chat"` and
  tool events **only** from `execute_tool`; **ignore `orchestrate_tools`** and everything else, or the
  spike-confirmed child-sum double-counts.
- **Storage is a single GLOBAL grain riding the existing `agent-state` container.** `UsageRollupGrain`
  (`IGrainWithStringKey`, well-known key `"global"`) uses `[PersistentState("usage-rollups",
  AgentGrain.StorageProviderName)]` — the `UserPushGrain` overlay pattern, so **no new Cosmos container, no
  Bicep**. It holds hourly + daily token and tool buckets, and **prunes on every write** (hourly ~48 h,
  daily ~92 d) to hold the 2 MB Cosmos item cap (ADR 0004). Consequence: `range=all` means "the retained
  daily window", not literally all-time.
- **The dashboard is GLOBAL / cross-user, behind Easy Auth.** `GET /api/usage` (+ `/usage/applications`,
  `/usage/users`) returns aggregates across **all** users — including other users' email/name and usage — to
  **any** signed-in caller, matching the reference. It stays behind Easy Auth (never `excludedPaths`) like
  every `/api/*` read; all allowlisted users may view (no role system, the ADR 0029 precedent). This is the
  one genuinely new exposure and is accepted for the ~2 trusted allowlisted users.
- **Cached-input and reasoning are wire-contract fields kept for reference parity but always 0** (the OTel
  pipeline emits neither). The UI shows the reference's tile/column layout with an honest note, rather than
  dropping them or tapping the grain's `UsageContent` (which would reverse the OTEL-only decision).
- **The "application" dimension is the agent name (per-agent), falling back to the constant `"Agent42"`.**
  No agent-name attribute rides the span natively, so the grain publishes the bound agent's name into
  Orleans `RequestContext` for the turn (`PublishUsageApplication`, in `ChatCoreAsync` + `ReceiveReminder`),
  `UserTelemetrySpanProcessor` stamps it onto the in-grain spans as `agent42.application` at `OnStart` (the
  same ADR 0010 identity mechanism — spike-confirmed that a value set inside the grain is visible at the
  span's `OnStart` in the same async flow), and the projector reads the tag. A regular (non-agent) chat
  leaves it unset and groups under `"Agent42"`. This is a span **tag**, not an override of
  `gen_ai.operation.name` (overriding the semconv would corrupt the exported trace) — the correct lever for
  a new dimension.

## Alternatives considered
- **A manual token counter in `AgentGrain`** (record `UsageContent` totals to a grain/Meter). Rejected by
  the user in favour of OTEL-only — the spans already carry everything, including tool calls, and the async
  reminder/agent-run paths would each need separate wiring. Its one advantage — it *does* expose cached
  tokens — was not enough to keep a second data path.
- **An OTLP-over-the-wire receiver** (the reference's topology, Agent42 exporting to itself over loopback).
  Rejected: pure overhead for a single co-hosted app ingesting only itself — a new port, protobuf handling,
  and a shared secret for no benefit.
- **A per-user rollup grain** (natural fit for Agent42's per-user grains). Rejected because the dashboard is
  cross-user by design; a global grain aggregates once and a single replica (localhost clustering) means no
  cross-replica contention.
- **A new Cosmos container / a Preferences tab.** Rejected: the `agent-state` overlay pattern needs no new
  container, and the four-region dashboard + filters is too large for the Preferences modal.

## Consequences
- The dashboard measures **chat/completion** token spend only. **Embedding tokens are invisible** — the
  embedding generator is not OTel-wrapped (ADR 0010) — so RAG ingestion/retrieval spend (ADR 0007/0008/0030)
  never appears. Stated in the UI/ADR so it doesn't imply total coverage.
- Async paths (fired reminders, agent-run — ADR 0022) have **no request identity** in `RequestContext`, so
  their chat spans carry no `user.*` and their tokens land in an **unreported** user bucket (correct total,
  no attribution) — matching the reference's unreported bucket.
- The `execute_tool`/`orchestrate_tools` spans were **already** on the exported `Agent42.Backend` source, so
  this adds **no** new export volume or PII surface — the processor filters in `OnEnd`, it does not widen the
  trace.
- If the global grain's state ever approaches 2 MB, the escape hatch is sharding by day into per-date grains
  (out of scope now); the retention constants live in one place (`UsageRetention`).
- No new secret, no new package (ingestion is in-process), no infra change.
