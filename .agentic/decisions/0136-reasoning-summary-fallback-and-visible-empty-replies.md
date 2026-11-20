---
id: 0136
date: 2026-11-20
status: accepted
tags: [models, reasoning, streaming, errors, foundry, no-migration]
supersedes: []
extends: [0014, 0071]
amends: [0071]
---

# A model that refuses the reasoning summary is retried without it, and a failed turn is never an empty bubble

## Context
`gpt-6.2-sol` and `gpt-6.2-astra` answered with an empty bubble while `gpt-6.1-terra` worked (2026-11-20). The span showed the
assistant message with `parts: []`, and a later span gave the cause: HTTP 400 `unsupported_parameter` on `reasoning.summary`.
Ticking "reasoning" for a model makes `ChatClientFactory.Build` send `reasoning: {summary}` (ADR 0071), and these models refuse it.

Two defects, measured on the pinned stack (`ReasoningSummaryFallbackTests`):
1. Nothing retried or remembered the refusal, so a model that refuses the summary could never be used with "reasoning" ticked.
2. A failure that rides INSIDE a 200 stream (a Responses `error` event) surfaces as an `ErrorContent` update, and
   `ProjectStreamedContent` dropped it in `default`. The framework then stored an assistant message with zero parts, which the
   browser's reply recovery adopted as an empty bubble and the next turn replayed to the provider.

## Decision
- **`ReasoningSummaryFallbackChatClient`** (added in `Build` only for a declared reasoning model, inside OTel): on a refusal of
  `reasoning.summary` it retries ONCE with the summary removed (the caller's effort kept) and remembers the model. Both shapes
  are recognised: `ClientResultException` before any update, and an `ErrorContent` update before any real content. A reply that
  already started is never replayed. Other failures propagate and remember nothing.
- **Remembered per process, not per turn:** a static set keyed by provider kind + endpoint + model, never a user, because the
  per-user factories are rebuilt. A model costs one extra call per process lifetime. A restart forgets it. A model switch has no effect.
- **A new live-only `Error` stream event** (last enum member). `ErrorContent` projects to it (capped at 500 chars), and the grain
  emits one itself, "The model returned an empty reply.", when a turn ends with no text, tool call or image, no error and no stop.
  `Done` still follows. The browser paints it as an error bubble under whatever the reply said, never into the copied text and never spoken.
- **`StripReasoningFromStorage` also owns `ErrorContent` and drops an assistant message with no parts.** This reverses ADR 0071's
  "an already-empty message passes through": an empty message means nothing to a model, and storing one is how defect 2 persisted.

## Alternatives considered
- **Always omit the summary on `gpt-6.2-*`.** Rejected: a model-name list rots, and it hides the summary from models that accept it.
- **Retry on every turn without remembering.** Rejected: one failed request per turn on a model that will never accept it.
- **Persist the remembered set.** Rejected: a restart costing one retry per model is cheaper than a store to keep correct.
- **Throw on `ErrorContent`.** Rejected: the turn may already have streamed text and tool calls, and `Done` must still settle the bubble.

## Consequences
- Existing sessions keep the empty assistant messages already written. The Responses adapter tolerated them (Terra answered
  after two), so they are named here, not migrated; they stop appearing as soon as the next turn re-serializes.
- `AgentMcpRunner` and the chat gateway read only `TextDelta`, so an `Error` event is ignored there and an agent-run or chat-app
  reply to an empty turn is still empty. Not in scope.
- New enum member and trailing `Error` event: no migration, no Bicep, no new secret. `sw.js` `CACHE` bumped v82 to v83.
- Unverified: a live `gpt-6.2-*` deployment. CI proves both failure shapes against a stub transport, not the real service.
