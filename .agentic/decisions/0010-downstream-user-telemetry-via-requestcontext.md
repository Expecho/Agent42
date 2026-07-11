---
id: 0010
date: 2026-07-11
status: accepted
tags: [telemetry, observability, privacy, orleans, auth]
---

# Propagate user identity to downstream spans via Orleans RequestContext (not Activity.Baggage)

## Context
ADR 0009 stamps the `user.*` identity attributes onto the ASP.NET Core **server span** only.
OpenTelemetry attributes are per-span and are **not** inherited by child or subsequent spans, so
every downstream span carried no user attribution — most importantly the **Azure OpenAI
chat-completion span**, which is created *inside* the Orleans grain (`AgentGrain.ChatAsync`'s
streaming iterator). The ask was to carry the identity to those downstream spans, grain spans
included, without leaking PII out of the process.

Backend and the Orleans silo are **co-hosted** on one `WebApplication`, so a single processor on the
Backend's `TracerProvider` observes both in-request and in-grain spans. The open question was the
propagation channel from the request to the grain-side span.

## Decision
Publish the request's identity — extracted once from the Easy Auth headers into a serializable
`UserTelemetryContext` carrier (`[GenerateSerializer]`, in `Agent42.Agents`) — into **Orleans
`RequestContext`** from a request-scoped middleware registered first in the pipeline. A
`BaseProcessor<Activity>` (`UserTelemetrySpanProcessor`) reads that carrier in `OnStart` and stamps the
`user.*` tags (roles as `string[]`) onto every started span, no-op when no identity is present.

`RequestContext` propagates across the grain call by default (proven, load-bearing — see below), so the
same processor attributes both in-request child spans and the in-grain chat-completion span. The server
span keeps its ADR 0009 `EnrichWithHttpRequest` enrichment: it starts *before* the middleware runs, so
the processor's `OnStart` can't reach it — no double-stamping (different spans).

## Alternatives considered
- **`Activity.Baggage`** — rejected, and this is the core reason for the whole design. Baggage
  auto-injects a W3C `baggage` request header on outbound HttpClient calls (the default
  `DistributedContextPropagator`), which would ship user PII (name/email/roles) to **Azure OpenAI** in
  request headers. `RequestContext` is an in-cluster channel (Orleans' own protocol, never an external
  HTTP header), so identity reaches the grain without leaking outward. **This no-external-leak
  invariant is non-negotiable — do not "simplify" to Baggage.**
- **Grain-call filter only** (`IOutgoingGrainCallFilter`/`IIncomingGrainCallFilter` copying identity) —
  the reserve fallback had `RequestContext` propagation been off by default; unnecessary once the gate
  test proved default propagation works, and it wouldn't cover in-request (non-grain) spans.
- **Grain-key-derived id only** (`user.id` from the `{userId}/{conversationId}` grain key, no carrier) —
  recovers only the id, dropping name/email/roles/idp; loses parity with the server span (ADR 0009's
  "store as much as possible" choice).

## Consequences
- Downstream spans — the in-grain chat-completion span primarily, plus in-request HttpClient spans —
  now carry the full `user.*` set, matching the server span. In-request coverage comes for free from the
  same processor + ambient `RequestContext` (an AsyncLocal in the Backend flow too).
- **Load-bearing fact, now guarded by a test:** Orleans 10.2 propagates `RequestContext` into the
  *streaming* `ChatAsync` grain path by default (`RequestContextPropagationTests`, via a `FakeChatClient`
  `[uctx:…]` echo read at the in-grain chat-client call site). If a future Orleans upgrade changes this
  default, that test fails first — restore propagation (silo config or a grain-call-filter pair) before
  shipping.
- **In-cluster PII propagation is accepted** (name/email/roles now travel to the silo via
  `RequestContext`) — consistent with ADR 0009's accepted-PII trade-off for this personal, two-user app;
  the invariant is only that PII never leaves the process as an outbound `baggage` header.
- The actual stamp on a real in-grain span is **prod-smoke** (a real OTel host + Easy Auth are prod-only,
  as in ADR 0009): offline, the chain is proven by composition — processor stamps from `RequestContext`,
  middleware writes it from headers, Orleans propagates it into the grain.
- **Still uncovered** (out of scope, called out not silently dropped): reminders/background grain
  activity (no request identity); Orleans grain-call orchestration spans (`Microsoft.Orleans.*` source
  not sampled today); named embedding GenAI spans (the embedding generator isn't OTel-wrapped).

## Supersession / relationships
Extends ADR 0009 (server-span `user.*` enrichment); reuses its `CurrentUser` extractor, now refactored
to a single header→carrier→tags path. ADR 0009's out-of-scope "outbound/grain-span enrichment" bullet is
amended to point here.
