---
id: 0137
date: 2026-11-21
status: accepted
tags: [models, routing, foundry, egress, per-user, no-migration]
supersedes: []
extends: [0013, 0018, 0019, 0103, 0108]
---

# Title: Route "Auto" turns with a per-user Microsoft Decision-1 provider

## Context
A user with several providers (Foundry, Anthropic, GitHub Models, ...) must pick the model by hand; nothing picks per
turn (only a server-side default). Microsoft-Decision-1 is a small Foundry classification model: it returns a probability
per option and no generated text (spike 2026-11-21: p50 0.24 s, p95 0.28 s, 4 options). There is no operator model
(ADR 0019/0030), so the router must be per-user too.

## Decision
- A new **`DecisionModel` `ProviderKind`** (appended last, ADR 0103) holds a Foundry resource endpoint, an API key and ONE
  declared model whose id is the Decision-1 deployment. It is never a chat model: invisible in `/api/models`, the picker,
  the default chain, `FallbackProviderKey` and the embedding/speech/transcription selectors, and its client refuses.
- An **Auto** picker entry exists only when the user has an enabled `DecisionModel` provider. The reserved value `auto`
  lives only on the wire; it is never persisted as `DeploymentName`. The chosen real model is persisted, plus a trailing
  `AgentGrainState.AutoModel` bool so the picker restores to Auto (`ModelRouting` is taken by Fleet).
- Each Auto turn calls `POST <resource>/providers/microsoft/v1/systemone` with the last user message and the user's
  candidate models as options (positional keys, never model ids) plus a "cannot tell" option. **Sticky unless clear win**:
  switch only when the best option clears a probability threshold AND a margin over the current model; "cannot tell" keeps it.
  Thresholds are constants, not a per-user setting.
- Candidates are hard-filtered first (the router is advice, not a capability check): no copilot-only, non-chat, no-tools
  when tools are on, no-vision when the turn has images. 0 candidates: no call; 1: no call.
- **Failure never fails the turn** (timeout, non-2xx, bad JSON, unknown key): keep the current model, log a warning.
- Option text is layered: user `Hint` per model, then a best-effort public model card (OpenRouter listing, exact normalised
  name match, cache-only on the hot path), then a description derived from capability flags. Layer 3 alone works.
- Scope: browser chats and chat-app plain chats. Agent chats, fleet, stored agents, email-in, reminders, incognito
  (ephemeral) turns, and a turn while another run is in flight do not route. `auto` is refused at every other
  server entry that takes a model.
- Routing is per-user only: an operator-baseline `DecisionModel` provider is ignored. A conversation that ever held an image routes to
  vision models only (history images are re-sent every turn). An explicit model pick clears Auto even on an incognito turn. If another
  run starts while the router call is awaited, the decision is dropped and the turn keeps its model.
- A new `Routed` stream event (appended last) shows the chosen model as a non-persisted label.

## Egress (ADR 0013 extension)
The last user message (trimmed) and the option texts (each candidate's display name, the user's hints, and public model-card text, which is untrusted content in the classifier prompt but cannot widen capabilities because the capability filter runs first) go to the user's own Foundry resource, opt-in: no `DecisionModel` provider means
nothing is sent. Auto also changes which of the user's providers receives the whole conversation, including third-party
endpoints they did not pick for that chat. The model-card layer makes one unparameterised GET of openrouter.ai's public models listing (cached 24 h, in the background);
that request carries nothing about the user, their models or their conversation. Per-model opt-out from Auto is rejected for v1: the user picks by hand, or does not connect a provider
they don't want used. The endpoint comes from the stored provider, never a request (ADR 0018: not SSRF-guarded).

## Alternatives considered
- **A separate router setting** — would duplicate the provider editor, key encryption and per-user storage.
- **Reuse an existing provider kind** — a router model is not a chat model and would leak into every picker and default.
- **Route every turn without stickiness** — every switch changes the prompt prefix (ADR 0104) and flip-flops on close calls.
- **A streamed `TextDelta` notice** — lands in the bubble text, copy, export and regenerate; hence the `Routed` event.
- **Azure OpenAI "Decisions API" (`gpt-6-luna`)** — a different shape, out of scope.

## Consequences
- Router tokens are not in the usage dashboard or the cost estimate (the call is not an `IChatClient`, ADR 0110 counts chat
  spans); token usage is logged on an `Activity`. List price seen: $0.042 per 1M input tokens, informational.
- A model switch breaks prompt-prefix caching for that turn; stickiness is the mitigation, no hit rate is claimed.
- The Android NDJSON golden file gains the `Routed` event (ADR 0114); its parser already maps unknown types to `UNKNOWN`.
- Deploying `Microsoft-Decision-1.1` is a manual prerequisite handed to the user. Spec: `.agentic/specs/plan-decision-model-router.md`.
