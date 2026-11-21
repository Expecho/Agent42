---
id: 0110
date: 2026-10-21
status: accepted
tags: [telemetry, usage, cost, pricing, dashboard, tokens, providers, no-migration]
supersedes: []
extends: [0031, 0104]
amends: [0031]
---

# Estimated cost per turn and on /usage, derived from a checked-in, effective-dated price table

## Context
`/usage` (ADR 0031) and the per-turn token line (ADR 0104) count tokens but say nothing about money, and ADR 0031's
contract said "no cost field anywhere". The sibling `Expecho/GenAI.Telemetry.Dashboard` (its ADR 0012) already settled
how to price GenAI telemetry: cost is **derived** from tokens at read time and never stored, against an effective-dated
price table, and "unpriced" is a fact, not $0. Agent42 differs in one way that matters: models come from four
per-user provider kinds (ADR 0013/0018), and `gen_ai.provider.name` reads `"openai"` for Azure OpenAI **and** every
OpenAI-compatible endpoint, so the span alone cannot say which price applies.

A spike (2026-10-21, Anthropic 12.58.0, stubbed transport) settled the formula's one open input: the Anthropic
`IChatClient` reports `InputTokenCount` as plain input **plus** cache reads **plus** cache writes (wire
`10 / 100 / 50` → `160`), with the writes in `AdditionalCounts["CacheCreationInputTokens"]`.

## Decision
- **`ModelPricing`** (`Agents/Telemetry`) holds a checked-in table keyed on `(provider kind, model, validFrom)` in
  USD per 1M tokens (input, cached input, cache write, output), and the formula
  `max(0, in − cached − write)·in + cached·(cachedRate ?? in) + write·(writeRate ?? in) + out·outRate`.
  No reasoning term (reasoning is part of output). Matching is exact after lower-casing and stripping a release-date
  suffix — never a prefix match. Three answers: `priced`, `included` (GitHub Copilot, a subscription), `unpriced`
  (no row; rendered "—", never "$0"). On `/usage`, a row spanning an effective date is `partial` ("≥").
- 2026-11-21: added `gpt-6-sol`, `gpt-6-astra`, `gpt-6.1-sol` and `gpt-6.1-terra` (Standard Global, short-context tier). Azure lists no 6.2 meter, so `gpt-6.2-*` stays unpriced.
- Seeded from the **Azure Retail Prices API** (Global meters, normalized to per-1M) and **Anthropic list prices**
  (cache write 5-minute = 1.25× input). OpenAI-compatible endpoints have **no rows**: GitHub Models, Ollama and vLLM
  can be free, local, or anything.
- **The provider kind rides its own span tag**, `agent42.provider.kind`, set by `ProviderKindTagChatClient` directly
  inside `UseOpenTelemetry` (where `Activity.Current` is the `chat` span, and only when it is). `TokenBucket` gains a
  trailing `ProviderKind` (and the bucket key includes it); cost is computed in `UsageRollupState.Query`, never stored.
- **Per turn**, `IChatClientFactory.GetPricingIdentity(modelId)` (default `("", "")` = unpriced) is resolved once at
  turn start, and the `Done` event carries `CostUsd` + `CostStatus`, priced with the cache-write term.

## Alternatives considered
- **A live refresh from the Azure Retail Prices API** (the sibling's design). Deferred, not rejected: it needs a
  curated model→meter map anyway (meter names follow no scheme; a near-miss prices silently), a background service,
  and an egress to `prices.azure.com`. A ~40-row table edited by hand is honest at two users. The table's shape is
  the sibling's, so the refresh can be added later without changing callers.
- **Price by `gen_ai.provider.name` + model.** Rejected: it cannot tell Azure from OpenAI-compatible, so a GitHub
  Models `gpt-4o` would bill at Azure rates.
- **Publish the kind through Orleans `RequestContext`** like `agent42.application`. Rejected: the factory, not the
  grain, knows which provider served a call.
- **Store a cost on each bucket.** Rejected (sibling ADR 0006/0012): a stored amount can never be corrected; a
  derived one is fixed by editing the table.
- **Prefix matching** so new dated versions price automatically. Rejected: `gpt-5.4-pro` would price as `gpt-5.4`
  at a twelfth of its real rate, silently.

## Consequences
- ADR 0031's "no cost field anywhere" no longer holds: `UsageTotals`, `ModelUsage`, `UserUsage` and the `Done` event
  carry trailing cost fields. Cost is still never persisted.
- **Every bucket written before this change is unpriced** (no provider kind). Correct and honest; no migration.
- **/usage and the turn line disagree for Anthropic prompt caching:** no OTel tag carries cache writes (ADR 0104), so
  /usage bills them at the input rate, the turn line at 1.25×. Stated in the dashboard note and pinned by
  `Query_AnthropicHasNoCacheWriteTerm`.
- The estimate is **Global-tier list price**: Data Zone or regional Azure deployments, negotiated discounts, batch,
  Haiku 5.5's >100K-prompt tier and the Anthropic 1-hour cache are not modelled. The UI says "Est.".
- **The table is a maintenance obligation**: a new model is unpriced ("—") until someone adds a row. A reprice adds a
  row with a later `validFrom`; it never edits one, so history keeps its price.
- Cost on `/usage` is cross-user, like the token counts (ADR 0031); with per-user providers that is each user's own
  key spend, visible to the ~2 allowlisted users.
- Embedding, image, speech, fleet and Copilot-SDK agent spend stay out (none reaches the chat span or the Done event).
- No new package, no secret, no infra, and no egress.
