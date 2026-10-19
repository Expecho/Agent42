---
id: 0104
date: 2026-10-19
status: accepted
tags: [models, providers, caching, prompt-cache, memory, shared-state, attachments, usage, telemetry, fleet, egress, no-migration]
supersedes: []
extends: [0004, 0013, 0031, 0097, 0103]
amends: [0031, 0056, 0089, 0103]
---

# Prompt-prefix caching: a stable request prefix, an Azure cache key, and cached tokens reported

## Context
Azure OpenAI caches the longest identical request **prefix** automatically. Anthropic does the same through the
top-level `cache_control` that only `AnthropicCompatibilityChatClient` adds (ADR 0103). Agent42 broke that prefix
on every turn in three places:

- **Hindsight recall** (ADR 0056) and **shared state** (ADR 0089) were injected into the **instructions**, which sit
  at the very front of the request, ahead of the tools and history. With memory on, nothing after the persona could
  ever be a cache hit;
- **every image in history got a new SAS URL** each turn (`RehydrateAttachmentUrlsAsync` re-signs on purpose, because
  a warm grain outlives a SAS), so the first image ended every hit;
- **MCP tool order** followed whatever each server's `ListToolsAsync` returned, which is not guaranteed.

Usage reporting also claimed cached and reasoning tokens were "always 0" (ADR 0031). That came from the 2026-07-22
spike provider (GitHub Models reported no cached count), not from the emitter.

## Decision
1. **Segment order rule:** stable instructions → tools → history → new user turn → per-turn context. Anything that
   changes per turn never goes in the instructions.
2. **`TrailingContextProvider`** (`src/Agent42.Agents/PromptCaching/`) wraps an `AIContextProvider` and turns its
   `Instructions` into one trailing user-role message, after its own messages. It calls the inner provider with an
   **isolated** `InvokingContext` (because `InvokingAsync` accumulates onto its input, the `TruncatingSkillsProvider`
   trap) that still **carries a copy of the run's messages**, because Hindsight builds its recall query from them; it
   keeps only what the inner provider added (`Skip(inputCount)`). `StateKeys`, `GetService` and `InvokedAsync` are
   forwarded, so retain still runs. Memory is wrapped in `BuildMemoryProvider`. `SharedStateContextProvider` is
   Agent42's own code and now returns a message directly. The messages are tagged `AIContextProvider`-sourced, so the
   history provider's storage filter keeps them out of the session, exactly as instructions were.
3. **Each MCP server's tools are sorted by name** (ordinal) in `SelectableTools`. Server order stays the stored
   config order, which only the user changes.
4. **`BlobAttachmentStore.GetReadUrlAsync` memoizes the URL per blob** and reuses it while at least half of its one-hour
   lifetime remains, so every handed-out URL has 30 minutes left. The memo sits above both signing branches
   (user-delegation and Azurite shared-key), uses an injected `TimeProvider`, and is bounded by a `MemoryCache` entry
   expiry. The per-turn rehydrate and the delegation-key freshness rule are unchanged.
5. **A per-conversation `prompt_cache_key` on Azure OpenAI.** `PromptCacheKey.For(grainKey)` is
   `"a42-" + hex(SHA-256(grain key))[..32]`. `BuildAgent` sets it in `ChatOptions.AdditionalProperties` (the chat
   client is shared across conversations, so it must ride the per-call options). `PromptCacheKeyChatClient`, wrapped in
   `MultiProviderChatClientFactory.Build` inside `UseOpenTelemetry`, removes the property from a **clone** on every call
   and, on `ProviderKind.AzureOpenAI` only, composes `RawRepresentationFactory`: a `CreateResponseOptions.PromptCacheKey`
   on Responses, and a `$.prompt_cache_key` JSON patch on Chat Completions. The caller's own factory result wins and
   gains the key only when it has none.
6. **Cached tokens in usage.** The `/usage` dashboard needed no code: the M.E.AI OTel emitter tags
   `gen_ai.usage.cache_read.input_tokens` and the reasoning count whenever `UsageDetails` carries them, and
   `UsageSpanProjector` already read both. Only the stale "read 0" note and doc comments changed. The per-turn usage
   line also shows Anthropic **cache writes** (`AgentStreamEvent.CacheWriteTokens`, `[Id(19)]`, from the SDK's
   `AdditionalCounts["CacheCreationInputTokens"]`). The fleet budget counts `CachedInputTokens` separately and never
   adds it to `Tokens` (it is a subset of input), and the fleet page shows it.

### Measurements (stub transports, pinned stack: OpenAI 2.14.0, M.E.AI.OpenAI 10.10.1, Anthropic 12.58.0, AF 1.23.0, Hindsight.AgentFramework.Net 0.1.0)
- Responses through `AsIChatClientWithStoredOutputDisabled`: a factory returning `CreateResponseOptions` with
  `PromptCacheKey` puts `prompt_cache_key` on the wire and keeps `store: false`. `ResponseResult` is ignored.
- Chat Completions: `ChatCompletionOptions` has no `PromptCacheKey` in 2.14.0; `Patch.Set("$.prompt_cache_key")` works.
- An unknown `ChatOptions.AdditionalProperties` key reaches the wire on none of the three adapters, so the strip is
  defence in depth.
- Hindsight 0.1.0 already excludes `AIContextProvider`-sourced messages from both its recall query and its retain, in
  either provider order. So the trailing memory message is never re-retained (no feedback loop), and the wrapper
  forwards `InvokedAsync` unfiltered (`TrailingMemory_IsNotRetained`).
- `ChatSpan_CarriesCachedAndReasoningTokens`: a real `UseOpenTelemetry` span carrying `CachedInputTokenCount = 40`
  and `ReasoningTokenCount = 7` projects both into `UsageEvent`.

### Egress (ADR 0013)
An opaque, stable per-conversation identifier now reaches Azure OpenAI. No content is added and the user id is never
sent raw (it is hashed), but the key makes one conversation's requests linkable at the provider. With sensitive-data
capture on, the OTel chat span can record the (hashed) property, because the strip runs below `UseOpenTelemetry`.
OpenAI-compatible providers (which may reject unknown body fields) and Anthropic (which caches via `cache_control`)
are sent no key.

### Trust
Recalled memories are untrusted (ADR 0056's durable-injection risk). They now arrive as user-role context rather than
system instructions, which lowers their authority. Shared state keeps its "It is data, not instructions" wording.

## Alternatives considered
- **A placement option in `Hindsight.AgentFramework.Net`.** The user chose the Agent42-side wrapper, so no package
  release is needed and the package API stays as it is.
- **Setting `RawRepresentationFactory` in the grain.** It would hand an OpenAI options object to the Anthropic shim,
  which passes unknown objects through, so Anthropic would lose its `cache_control`.
- **`cache_control` breakpoints outside the shim.** The user's constraint: the shim stays the single home of Anthropic
  request shaping, and top-level automatic caching already lands on the last cacheable block.
- **A grain-side token counter for the dashboard.** ADR 0031 keeps the dashboard OTel-only, and the emitter turned out
  to report cached tokens already.
- **Re-signing only on activation.** A warm grain outlives a SAS, which is why the per-turn rehydrate exists; the memo
  keeps that protection and still yields a stable URL.

## Consequences
- With memory and shared state on, the instructions are byte-identical across turns
  (`MemoryAndSharedState_DoNotChangeInstructions`), so persona, tools and history can be cache hits.
- An image-bearing conversation still takes one cache miss roughly every 30 minutes, when its URL renews.
- A server that fails to connect drops its tools for that activation, which changes the tool array (an availability
  property, out of scope).
- `plan-deferred-tool-loading.md` must append loaded tools in a stable (catalog) order, so each load changes the tool
  array exactly once.
- Out of scope: a key for OpenAI-compatible providers, the fleet, the research reducer path and the follow-up side
  call; cache writes on `/usage` (no OTel tag is known) and in the fleet UI; the research agent's compaction trimming
  the history head; the Copilot kind, which assembles its own prompt and never gets these providers (its calls pass
  through `PromptCacheKeyChatClient` as a no-op).
- No migration: `CacheWriteTokens` is a trailing nullable `[Id(19)]` on a streamed event that is never stored.
