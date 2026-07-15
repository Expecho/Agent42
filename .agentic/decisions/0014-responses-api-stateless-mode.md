---
id: 0014
date: 2026-07-15
status: accepted
tags: [models, providers, responses-api, chat-completions, openai, api-mode, config, data-egress, hosted-tools]
---

# Support the OpenAI Responses API as a per-model option, run statelessly

## Context
Every model in Agent42 is reached through `IChatClient`, and the only place the wire
protocol is chosen is `ChatClientFactory.cs` — two `GetChatClient(model).AsIChatClient()`
calls that pin the app to the **Chat Completions** API (ADR 0013). Chat Completions cannot
carry the OpenAI-family **hosted** tools (server-side web search, file search): our client
gets HTTP 400 on `web_search_options`, which is the whole reason ADR 0011 stood up a
self-hosted SearXNG sidecar (~2× the app's compute). The **Responses API** is OpenAI's
newer surface and the one those hosted tools live on.

Two facts, established by the 2026-07-15 spike research (Microsoft Learn API docs + the
NuGet version index; the executable session/streaming checks could not run in the agent
environment and are captured in `scratchpad/responses-spike.cs` for a runtime with the SDK):

- The Responses API is **stateful by default** — responses are stored server-side and later
  turns reference them by id (`previous_response_id` / `ChatOptions.ConversationId`). A
  **stateless** mode (`store=false`) is a first-class opt-out that re-sends full history each
  turn, exactly as Chat Completions does.
- The Responses client is **already reachable on the pinned `OpenAI 2.12.0`**
  (`GetOpenAIResponseClient()` → `OpenAI.Responses.ResponsesClient`), but **not** on the
  pinned `Azure.AI.OpenAI 2.1.0`. The Azure accessor first appears in the `2.2.0-beta` line
  (documented at `2.9.0-beta.1`), and **there is no stable Azure.AI.OpenAI above 2.1.0** —
  every later version is prerelease.

## Decision
Add a Responses API mode as a **per-model / per-provider option** (a `UseResponsesApi` flag),
branched inside the factory: a Responses-enabled model is built with
`GetOpenAIResponseClient(model).AsIChatClientWithStoredOutputDisabled(model)` instead of
`GetChatClient(model).AsIChatClient()`. Everything upstream (`AgentGrain`, `ChatClientAgent`,
tools, streaming, the OTel wrapper) is unchanged — it only ever sees `IChatClient`.

**Run statelessly (`store=false`).** Agent42 holds the whole conversation client-side and
persists it into the 2 MB Cosmos session item as the source of truth (ADR 0004); the grain
reads it back via `TryGetInMemoryChatHistory`. Server-stored responses would break that
invariant *and* place a second copy of conversation content — document excerpts (ADR 0008),
image SAS URLs (ADR 0004), the user's street address (ADR 0012) — in a store outside the
Entra tenant, the exact egress line ADR 0013 draws.

**Enable it on OpenAI-compatible (API-key) providers first.** That path needs **zero package
change**. Foundry Responses is **deferred** (see Alternatives) until a stable Azure SDK
exposes it.

## Alternatives considered
- **Enable Responses on Foundry now.** Rejected for this iteration: it forces a **prerelease**
  `Azure.AI.OpenAI` (`2.9.0-beta.1`) pin, which collides with hard-rule #4's "latest *stable*"
  default — there is no stable release that has the accessor. Revisit when one ships.
- **Stored mode (`store=true`, the Responses default).** Rejected: breaks the client-side-history
  invariant the whole grain is built on, and stores tenant-egressing content server-side.
- **A global swap of every provider to Responses.** Rejected: Responses is not universally
  supported by OpenAI-*compatible* endpoints (Ollama/vLLM), so this would regress ADR 0013's
  "any OpenAI-compatible endpoint" promise. A per-model flag keeps Chat Completions the default.
- **A new `ProviderKind` (`OpenAIResponses`).** Rejected: duplicates the credential/endpoint
  resolution in `ModelProviderBuilder`. A flag on the existing kinds reuses it and mirrors how
  `SupportsTools`/`SupportsVision` already ride per-model.

## Consequences
- **Unblocks hosted tools later.** With a Responses client in place, the OpenAI-family hosted
  `web_search`/file-search tools become reachable — a future path to retiring the SearXNG
  sidecar (ADR 0011) for those providers. That is follow-up, not part of this change.
- **The `AsIChatClient(ResponsesClient, …)` adapter is `[Experimental("OPENAI001")]`** — the
  call site needs a scoped `#pragma warning disable OPENAI001` (or `NoWarn`), like other
  experimental M.E.AI surfaces.
- **`includeReasoningEncryptedContent: true`** (the helper's default) is load-bearing if a
  Responses-enabled model is ever a reasoning model: it carries reasoning items across turns in
  the client-held history, which is what makes multi-turn work without server storage.
- **Foundry stays on Chat Completions** until the Azure SDK stabilizes; the default model stays
  a Foundry Chat Completions model, so this is opt-in per conversation on top of ADR 0013's
  opt-in.
- **Pending runtime confirmation** (`scratchpad/responses-spike.cs`): that the stateless adapter
  serializes full history into the session blob `SerializeSessionAsync` needs, that a Chat
  Completions session blob cross-deserializes onto a Responses agent (the model-switch rehost
  path in `ChatCoreAsync`), and that the streamed updates still carry
  `TextContent`/`FunctionCallContent`/`FunctionResultContent`/`UsageContent`. If cross-deserialize
  fails, mixing the two APIs within one conversation is constrained — the plan must gate on this.
