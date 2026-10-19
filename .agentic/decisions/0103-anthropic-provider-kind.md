---
id: 0103
date: 2026-10-18
status: accepted
tags: [models, providers, anthropic, claude, reasoning, fleet, copilot, egress, no-migration]
supersedes: []
extends: [0013, 0018]
---

# Anthropic (Claude) is a native provider kind, behind one compatibility shim

## Context
On 2026-10-18 the user wanted Claude as a first-class model everywhere a chat model works: chats, stored agents and
`/api/agent-run`, the research agent, and the agent fleet (planner, judge, moderator, Magentic manager, team agents,
ADR 0097). Providers are per-user (ADR 0018) with no operator model (ADR 0019). Claude was reachable already through
an `OpenAICompatible` provider pointed at Anthropic's OpenAI-compatibility endpoint, but that endpoint drops prompt
caching and real thinking. Current Claude models (Opus 5.5, Sonnet 5.5, Fable 5.1) also return a 400 on request shapes
the app, or Agent Framework internals we cannot edit, send: forced tool choice, non-default sampling params,
`thinking: disabled`, and an assistant-prefill final message.

## Decision
1. **`ProviderKind.Anthropic`** (added at the end of the enum; config and storage bind by name), built by
   `Provider.Anthropic` over the **official `Anthropic` SDK** (12.58.0) `AsIChatClient`. `ModelProviderBuilder` gives it
   an explicit branch, because before this every non-Foundry kind fell through to the OpenAI client. It shares the
   OpenAI-compatible rules: blank key → Skipped, blank endpoint → Invalid, models declared. Capabilities are
   declared-only (`SupportsHostedTools` false). There are no image, embedding, speech or Copilot funcs.
2. **SDK settings are explicit.** `ApiKey`, `AuthToken = null` and `BaseUrl` all come from the stored provider, because
   the SDK otherwise falls back to `ANTHROPIC_API_KEY` / `ANTHROPIC_AUTH_TOKEN` / `ANTHROPIC_BASE_URL`, an ambient
   operator credential (ADR 0019/0030). `defaultMaxOutputTokens` is **32 000** (`AnthropicLimits`), because the SDK's own
   fallback is a hard-coded 1024 that would truncate replies and long tool arguments. A non-streaming 32 000 request is
   sent by the SDK (measured on the stub, `NonStreaming_MaxTokens32k_IsSent`), so no non-streaming cap is needed.
   `MaxRetries = 3` is set explicitly: that is the OpenAI SDK's default `ClientRetryPolicy` count (read by reflection
   from the pinned System.ClientModel 1.15.0; the Anthropic default is 2), so Claude retries like every other provider.
   The repo's retry concern (gotcha "A named HttpClient silently inherits AddServiceDefaults' retry pipeline") is about
   a layer stacked on top of the SDK's own, and `0` would discard the SDK's `retry-after` handling of 429s. The
   transport is the SDK's own, never an `IHttpClientFactory` client.
3. **One compatibility shim at the provider boundary**, `AnthropicCompatibilityChatClient : DelegatingChatClient`,
   applied only inside `Provider.Anthropic`. Per request, on a **clone** of the options and a new message list (the
   messages are the grain's live session history, ADR 0004), it:
   - maps `RequireAny` / `RequireSpecific` to `Auto`;
   - nulls `Temperature`, `TopP` and `TopK`;
   - maps `Reasoning.Effort = None` to `Low` (thinking cannot be disabled; low effort is the substitute) and never
     touches `Reasoning.Output`;
   - drops the messages the SDK would send with zero blocks, mirroring the SDK's own mapping (measured: it skips
     whitespace-only text, an empty unsigned thinking block, audio, JSON and a `text/*` URL; it sends images, PDFs,
     `text/*` data and hosted files), and never drops a message with tool content;
   - appends a `"Continue."` user turn when the request would end on an assistant message without a pending tool call;
   - turns on top-level automatic prompt caching through `RawRepresentationFactory`, returning `MessageCreateParams
     { Model, MaxTokens, Messages = [], CacheControl = ephemeral }`. Returning params makes the SDK take `Model` and
     `MaxTokens` from them, so both are copied in. A caller's own `MessageCreateParams` keeps its fields and gains only
     a missing `cache_control`. Any other caller result passes through.

   `ChatClientFactory.Build` wraps the shim, so ADR 0071's reasoning flag and OTel run outside it. It applies to every
   Claude model on purpose: older ones accept the downgraded shapes too, and a per-model matrix is drift.
4. **Thinking mode is Adaptive** (the SDK default). ADR 0071's `SupportsReasoning` keeps its meaning: ticked, it sends
   `thinking.display: "summarized"` and the user sees the thinking. Unticked, Claude still thinks and nothing is shown.
   The provider dialog says so.
5. **Thinking signatures round-trip within a turn without help.** `FunctionInvokingChatClient` re-sends the assistant
   thinking block with its signature intact before the `tool_use`, streamed (where the signature arrives as a
   separate update) and non-streamed alike. The Step-1 gate was pinned on the stub transport
   (`StreamedToolLoop_ResendsThinkingSignature`, `NonStreamedToolLoop_ResendsThinkingSignature`), so no recovery
   step was needed. **Across turns, no thinking is ever replayed**: ADR 0071's `StripReasoningFromStorage` stays as it
   is. That strip is also what keeps Claude clear of its preserved-thinking edit-history 400 on edit/regenerate/fork
   (ADR 0092), compaction and retention.
6. **Egress (an ADR 0013 extension, rule 13).** Conversation content, tool results and, for a model declared
   `SupportsVision`, image SAS URLs go to Anthropic. It is per-user and opt-in (the user adds the provider with their
   own key), the same shape as ADR 0018. It is not always on.
7. **UI:** an "Anthropic (Claude)" kind option. The endpoint is pre-filled with `https://api.anthropic.com` only when
   empty, and stays required for every kind (one validation rule; the unknown-kind message is derived from the enum).

## Alternatives considered
- **The OpenAI-compatibility endpoint via an `OpenAICompatible` provider.** It works with no code, but it is
  evaluation-grade: no prompt caching and no proper thinking. It remains available as is.
- **Fixing each call site** (the fleet moderator's `RequireAny`, follow-ups, …). It lost because Agent Framework
  internals (Magentic manager, `AIJudgeLoopEvaluator`, declarative fleet agents whose YAML the planner writes) send the
  same shapes and cannot be patched. OpenAI providers keep `RequireAny`.
- **Storing thinking for Claude.** It lost because it would re-introduce the preserved-thinking edit-history 400s and
  2 MB session pressure (ADR 0004), for no user-visible gain.
- **An optional endpoint for this kind.** It lost because it would add a per-kind validation branch. A pre-filled
  default gives the same convenience.
- **Per-block `WithCacheControl` caching.** This was the fallback if the factory swallowed fields. It wasn't needed:
  `Wire_UnderFactory_KeepsEveryField` pins model, max_tokens, tools, system, tool_choice, sampling and
  `thinking.display` under the factory.
- **Claude on Microsoft Foundry** (`Anthropic.Foundry`, the Messages API at `/anthropic/v1`). This is a follow-up kind,
  out of scope here.

## Consequences
- Claude works in chats, stored agents, research and the fleet. A fleet **Copilot** agent or a chat Copilot-kind
  agent on a Claude model is refused cleanly (planner feedback / `ProviderUnsupported`), because there is no Copilot
  BYOK mapping for Anthropic (a follow-up that needs its own verification of the BYOK provider types).
- The fleet moderator over Claude runs under `Auto`, so the model may answer in prose and selection silently falls
  back to round-robin (`Moderator_ProseReply_FallsBackToRoundRobin`). The `moderator` prompt now says "Always answer by
  calling next_speaker or finish", which is harmless for OpenAI too.
- No embeddings: a Claude-only user has no document search or fleet document search until they also add an
  embedding-capable provider (the existing ADR 0030 rule).
- Claude's server tools (web search/fetch, code execution), the Files API, refusal fallbacks, cache-token reporting on
  the usage dashboard, and prompt-prefix stability for more cache hits are out of scope.
- **Unverified live** until someone runs `LiveAnthropicChatTests` with a key (`AGENT42_LIVE_ANTHROPIC_KEY` or the
  git-ignored `live-anthropic.local.json`). **Post-deploy prod check:** a vision turn whose image is a private-blob SAS
  URL, which no test can mint.
- No migration: a new enum member read by name, and no new persisted fields.
