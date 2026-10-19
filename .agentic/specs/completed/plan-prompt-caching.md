planned-against: main @ 3406469

# Plan: prompt-prefix caching (stable prefix, OpenAI cache key, cached tokens in usage)

## Overview

Both providers cache the longest identical request **prefix**. OpenAI and Azure do this automatically, and
Anthropic does it through the `cache_control` that only `AnthropicCompatibilityChatClient` adds (ADR 0103). Agent42
currently breaks that prefix every turn in three places:

- memory recall and shared state ride the **instructions**, ahead of the tools and history;
- every image in history gets a **new SAS URL** each turn;
- MCP tool order follows whatever each server returns.

This plan reorders the request to **persona → tools → history → new user turn → per-turn context**. It sends a
per-conversation `prompt_cache_key` on Azure OpenAI and shows cached tokens on the `/usage` dashboard, in the fleet
budget, and as Anthropic cache writes in the per-turn usage line. It pairs with `plan-deferred-tool-loading.md`,
which shrinks the tools segment. That plan changes the tool array only when it loads a tool, so the two compose.

## Affected areas

- `src/Agent42.Agents/PromptCaching/TrailingContextProvider.cs` (**new**, feature folder, flat namespace
  `Agent42.Agents`). An `AIContextProvider` wrapper that turns the inner provider's `Instructions` into a trailing
  user-role message.
- `src/Agent42.Agents/PromptCaching/PromptCacheKey.cs` (**new**). The opaque per-conversation key and the
  `ChatOptions.AdditionalProperties` name it travels under.
- `src/Agent42.Agents/Memory/AgentGrain.Memory.cs`: `BuildMemoryProvider` returns the Hindsight provider wrapped.
  Its return type changes from `HindsightMemoryProvider?` to `AIContextProvider?`; the only caller is
  `BuildAgent.cs:109`.
- `src/Agent42.Agents/SharedState/AgentGrain.SharedState.cs`: `SharedStateContextProvider` emits a message, not
  instructions.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`:
  - each MCP server's tools are sorted by name (ordinal) in `SelectableTools`;
  - `ChatOptions.AdditionalProperties` carries the cache key.
- `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`: reuse a still-fresh read SAS per blob, with an
  injected `TimeProvider`. Check `src/Agent42.Backend/Program.cs:148` and the other silo hosts that register it.
- `src/Agent42.Backend/Models/PromptCacheKeyChatClient.cs` (**new**). A `DelegatingChatClient` that strips the
  property and, on `ProviderKind.AzureOpenAI`, maps it to the SDK's `PromptCacheKey`.
- `src/Agent42.Backend/Models/ChatClientFactory.cs` (`Build`, around lines 197-221): one `.Use(...)` below
  `UseOpenTelemetry`.
- Usage reporting:
  - `src/Agent42.Agents/Conversations/AgentGrain.cs` (`TurnTokenTotals`, `ProjectStreamedContent`);
  - `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` (trailing `[Id(19)] long? CacheWriteTokens`);
  - `src/Agent42.Backend/wwwroot/js/thread.js` (`showUsage`) and `src/Agent42.Backend/wwwroot/js/send.js` (its
    caller, around line 321);
  - `src/Agent42.Backend/wwwroot/sw.js` (`CACHE` bump, currently `agent42-shell-v64`).
- Dashboard text:
  - `src/Agent42.Backend/wwwroot/index.html` (around lines 1120-1122: the "read 0" note and the comment above it);
  - `src/Agent42.Agents/Telemetry/UsageContracts.cs:11`;
  - `src/Agent42.Agents/Telemetry/UsageEvent.cs:25-27`.
- Fleet:
  - `src/Agent42.Backend/Fleet/FleetBudget.cs` (`AddTokens`, a new `CachedInputTokens`);
  - `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs` (budget event, around lines 272-285);
  - `src/Agent42.Backend/wwwroot/fleet/fleet.js` (around lines 398-399) and the fleet page's metric markup.
  `/fleet` is outside `SHELL` (`sw.js` around line 101), so it needs no extra cache bump. The `wwwroot/js` change
  above already forces one.
- Tests:
  - new `tests/Agent42.Tests/TrailingContextProviderTests.cs`;
  - new `tests/Agent42.Tests/PromptCacheKeyChatClientTests.cs`;
  - extended `AgentGrainTests.cs` (memory tests around lines 2570-2671, and shared state), `SharedStateTests.cs`,
    `AttachmentTests.cs` / `AttachmentRehydrateTests.cs`, `UsageProcessorTests.cs`,
    `GrainStorageSerializerTests` (the new event field), the fleet budget tests (find them with
    `grep FleetBudget tests`), `UsageAssetsTests.cs`, `PwaAssetsTests.cs`, and the fleet asset test if one exists.
- Docs:
  - new ADR **0104** (next free number: run `ls .agentic/decisions` and `AdrNumberingTests` decides);
  - amendment banners on ADRs 0056, 0089, 0031 and 0103;
  - `.agentic/gotchas.md`;
  - the `CLAUDE.md` feature-map rows.
- **Not touched:** Bicep, secrets, packages (OpenAI 2.14.0 already exposes `PromptCacheKey`), `excludedPaths`,
  Cosmos containers, the Hindsight package.

## Context & constraints

### What the request looks like today (decompiled AF 1.23.0 + M.E.AI.OpenAI 10.10.1)

The agent's prepare step is `ChatClientAgent.PrepareSessionAndMessagesAsync`. It loads history first, then runs
`AIContextProviders` in list order. `AIContextProvider.InvokingCoreAsync` builds:

- `Instructions`: `input.Instructions + "\n" + provider.Instructions`;
- `Messages`: `input.Messages.Concat(provider.Messages)`, so provider messages land **after** the new user message.

On the Foundry Responses path, `ChatOptions.Instructions` becomes the top-level `instructions` field.

Current wire order, with whether each segment is stable across turns:

| Segment | Stable across turns? |
|---------|----------------------|
| `instructions`: persona, rendering hint, document names, skills list | Yes |
| `instructions` (cont.): `## Memories` recall (Hindsight) | **No, changes every turn** |
| `instructions` (cont.): research todo instructions | Yes |
| `instructions` (cont.): `## Shared state` JSON | **No, changes whenever the state does** |
| `tools` | Yes |
| `input`: history | Yes |
| `input`: user turn, then RAG excerpts and the todo message | No, but these sit after history |

So with memory on, **nothing after the persona can be cached**. RAG (`TextSearchProvider`) already uses the
correct trailing-message placement. That is the shape this plan copies.

Evidence:

- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:94-150`;
- `src/Agent42.Agents/SharedState/AgentGrain.SharedState.cs:157-175`;
- `.agentic/decisions/0056-hindsight-long-term-memory.md:129` ("injected into the system instructions").

Provider-sourced messages are **excluded from storage** by the history provider's
`StorageInputRequestMessageFilter` (`BuildAgent.cs` around lines 143-155; `AgentGrain.Documents.cs:31-40`). A
trailing context message therefore never reaches `SerializedSession` and never accumulates. That is the same
property ADR 0056 and the shared-state comment relied on instructions for. Prove it per provider (AC2).

### The wrapper, not a package change (user's decision)

The user chose an **Agent42-side wrapper** over a placement option in `Hindsight.AgentFramework.Net`, so no
package release is needed. Copy the shape of `src/Agent42.Agents/UserSkills/TruncatingSkillsProvider.cs`:

- call the inner provider with an **isolated `InvokingContext`**. Isolation is load-bearing, because
  `InvokingAsync` accumulates onto the input context; see that file's comment. **But unlike skills, the context must
  not be empty.** Hindsight builds its recall query from the run's messages, which arrive on `AIContext.Messages`
  (ADR 0056, divergence 1). The isolated context therefore carries `Messages` = a copy of the caller's
  `context.AIContext.Messages`, with `Instructions` and `Tools` null. The wrapper then keeps only what the inner
  provider **added**: its `Instructions`, and the returned messages minus the input ones (by reference, or
  `Skip(inputCount)`; Step 1 pins which one AF 1.23 honours). An empty context would make the recall query empty,
  and recall would silently return nothing (critic finding 1);
- forward `StateKeys`, `GetService` and `StoreAIContextAsync` → `inner.InvokedAsync`, so Hindsight's **retain**
  hook still runs;
- return a **new** `AIContext`. The inner provider's `Instructions` become one
  `new ChatMessage(ChatRole.User, text)` appended after its `Messages`. Tools pass through.

`SharedStateContextProvider` is Agent42's own code. Change it to return `Messages` directly, keep its "It is data,
not instructions" wording, and rewrite its doc comment, which today argues *for* instructions.

**Trust is a side benefit, and it must be recorded:** untrusted recalled memories (ADR 0056's durable-injection
risk) move from system authority down to user-role context.

### The feedback-loop risk this creates, which must be measured

ADR 0056 ported `exclude_sources` "as nothing", because injected instructions never appear as messages. Now they
will. If Hindsight's **retain** (`InvokedAsync`) or **recall query** reads `AIContextProvider`-sourced messages,
then:

- recalled memories would be re-retained, a feedback loop that grows the bank;
- RAG excerpts may already be retained.

Step 0 measures this. The ADR 0056 report says the recall query uses *external* user messages; retain is
unverified. If retain does read them, the wrapper's `StoreAIContextAsync` must pass `inner.InvokedAsync` a context
with `AIContextProvider`-sourced request messages filtered out. That is a fact to test (AC3), not to assume.

### Anthropic placement

Consecutive user-role messages are already sent today: the user turn followed by the RAG excerpts. So an extra
trailing user message is not a new shape for the shim. Its trailing-assistant rule
(`AnthropicCompatibilityChatClient.RewriteMessages`) is unaffected, because the request still ends on a user
message. Keep `cache_control` **only** in the shim (the user's constraint). Top-level automatic caching then
places the breakpoint on the last cacheable block, and moving memory out of `system` is exactly what lets it hit.

### SAS URLs re-signed every turn break the history prefix

`RehydrateAttachmentUrlsAsync` runs every turn by design (`AgentGrain.cs:734-740`): a warm grain outlives a SAS.
It calls `GetReadUrlAsync`, which mints a fresh one-hour SAS each time (`BlobAttachmentStore.cs:28, 135-136`).
The first image in a conversation therefore ends every cache hit.

Fix this **in the store**: memoize the URL per blob and reuse it while at least half of `ReadUrlLifetime`
remains. Every handed-out URL then has at least 30 minutes left, which covers a turn. The per-turn rehydrate stays,
because its expiry protection is still needed. The key-freshness rule (`IsDelegationKeyFresh`) is unchanged: a
reused URL was valid when minted and is not yet expired. Bound the memo with expiry, using `MemoryCache` or an
equivalent; there is a single replica (CLAUDE.md rule 12).

The memo must sit **above both** minting branches, the user-delegation key and the Azurite `CanGenerateSasUri` path
(`BlobAttachmentStore.cs:135-165`). Its only construction site is `Program.cs:148`, and a `TimeProvider? = null`
parameter resolves under DI. Residual for the ADR: an image-bearing conversation still takes one cache miss about
every 30 minutes, when the URL renews.

### `prompt_cache_key` (user's decision: yes, per conversation)

- **Key:** `"a42-" + hex(SHA-256(grain key))[..32]`. The grain key contains the user id, so it must **never be
  sent raw**. ADR 0013 egress applies: an opaque, stable per-conversation identifier now reaches the provider. No
  content is sent, but the key does make requests linkable. Record this in the ADR.
- **How it travels:**
  1. `BuildAgent` puts the key in `ChatOptions.AdditionalProperties[PromptCacheKey.PropertyName]`. BuildAgent runs
     per conversation, so no run-options plumbing is needed.
  2. `PromptCacheKeyChatClient` in `ChatClientFactory.Build` removes the property from a **clone**; the caller's
     options are never mutated, the same rule as the shim.
  3. On `ProviderKind.AzureOpenAI` only, it composes `RawRepresentationFactory`. The caller wins: a caller-supplied
     object keeps every field it set and gains `PromptCacheKey` only when it is null.
- **Why not set it in the grain:** a grain-side `RawRepresentationFactory` would return an OpenAI options object to
  the Anthropic shim, which passes unknown objects through. That breaks Anthropic caching.
- **The client is shared** across conversations (`ChatClientFactory` caches per model; deferred-tool spec point 3),
  so the key must come from the per-call options, never a field.
- **Unverified until Step 0:**
  - which options type each Azure path's factory must return: `CreateResponseOptions` / `ResponseCreationOptions`
    on Responses, `ChatCompletionOptions` on Chat Completions;
  - whether `AsIChatClientWithStoredOutputDisabled` preserves a caller's factory result;
  - whether M.E.AI or the Anthropic adapter would otherwise serialize an unknown `AdditionalProperties` key onto
    the wire.
- **Scope:** `OpenAICompatible` (GitHub Models, Ollama, vLLM) may reject unknown body fields, so it gets **no key**,
  only the strip. The same applies to Anthropic.
- **The strip runs on every call** through `ChatClientFactory.Build`, including the fleet, research, follow-up and
  memory-free paths. Those set no property, so for them the wrapper must be a pure pass-through (tested).
- **OTel:** the property is removed **below** `UseOpenTelemetry`, so with sensitive-data capture the span can still
  record it in `AdditionalProperties`. That is harmless, because the value is hashed, but the ADR egress paragraph
  should say so.

### Cached tokens in usage: mostly wired already, and the docs are stale

A scout decompiled `OpenTelemetryChatClient.TraceResponse` in M.E.AI 10.10.0 (and 10.6.0). It **does** set
`gen_ai.usage.cache_read.input_tokens` and `gen_ai.usage.reasoning.output_tokens` when the `UsageDetails` values are
present. `UsageSpanProjector` already reads both (`UsageSpanProjector.cs:24-25, 78-79`), and the dashboard already
renders the tile and the per-model and per-user columns (`usage.js:74, 112, 127`).

The "always 0" claim in ADR 0031, gotchas (around lines 718-756), `UsageContracts.cs:11`, `UsageEvent.cs:25-27`
and the `index.html` note was therefore an artefact of the 2026-07-22 spike's provider (GitHub Models returned no
cached count), not of the emitter. Core M.E.AI 10.10.1 is not in the NuGet cache, and the build resolves 10.10.0.
AC9 pins the behaviour with a test, so this is settled by measurement.

Both adapters fill `CachedInputTokenCount`:

- OpenAI maps `cached_tokens` on both paths;
- Anthropic 12.58.0 `ToUsageDetails` sets `InputTokenCount = input + cache_read + cache_creation` (so cached stays
  a subset of input, as `UsageContracts.cs:10` assumes), `CachedInputTokenCount = cache_read`, and
  `AdditionalCounts["CacheCreationInputTokens"]` when the value is above 0.

The per-turn line already shows cached reads (`thread.js:398-403`). **Cache writes** (user's decision: show them)
come from that `AdditionalCounts` key. Read the key through a named constant, because it is the SDK's string.

### Ordering inside `tools`

There are three sources, each verified by a scout:

- MCP server order is the stored config order (`UserMcpToolProvider.cs:91`). It is stable, and the user changes it
  deliberately.
- Each server's tool order is whatever `ListToolsAsync` returns (`McpConnectionFactory.cs:109`). The order is not
  guaranteed, so **sort it ordinally by name**.
- `MergedSkills` returns the shipped catalog unsorted when nothing is uploaded (`BuildAgent.cs:525-535`), but
  `SkillCatalog.Discover` already sorts it (`SkillCatalog.cs:102`). That is fine, so leave it.

A server that fails to connect drops its tools for that activation. That is out of scope, because it is an
availability property.

### Governing ADRs and gotchas

ADRs:

- 0103 (shim; "prompt-prefix stability for more cache hits" listed as out of scope there);
- 0056/0057 (memory placement and trust);
- 0089 (shared state);
- 0031 (usage; OTel-only, so keep it OTel-only and do **not** add a grain counter for the dashboard);
- 0004 (attachments, SAS);
- 0013 (egress);
- 0097 (fleet);
- 0041 (ES modules, `sw.js` CACHE).

CLAUDE.md cross-cutting rules that apply:

- 1 (flat namespace);
- 2 (trailing `[Id]` with a default; prove it on the Cosmos JSON path);
- 3 (`sw.js` CACHE bump);
- 9 (S3776; `BuildAgent`/`SelectableTools` are near the limit, so extract helpers);
- 13 (egress).

## Steps

0. **Spike** (`/spike-dotnet`, a file-based app pinned to repo versions, outside the tree). Measure:
   - **(a)** On a stub transport, for each Azure path (Responses through `AsIChatClientWithStoredOutputDisabled`,
     and Chat Completions): which options type `RawRepresentationFactory` must return so that `prompt_cache_key`
     appears in the request body, and that the helper keeps it.
   - **(b)** Whether an unknown `ChatOptions.AdditionalProperties` key reaches the wire on the OpenAI paths and on
     the Anthropic adapter.
   - **(c)** With `Hindsight.AgentFramework.Net` 0.1.0 and `RecordingHindsightClient`-style capture: whether
     `InvokedAsync` retains, or the recall query includes, a request message stamped
     `AgentRequestMessageSourceType.AIContextProvider`.

   Record the outputs in the spec's Review section and in the ADR. The outcome of (c) decides whether the wrapper
   filters what it forwards to `StoreAIContextAsync`.

1. **`TrailingContextProvider`**: write `PromptCaching/TrailingContextProvider.cs` in the `TruncatingSkillsProvider`
   shape. Tests in `TrailingContextProviderTests.cs`, with a stub inner provider and a real `ChatClientAgent` over a
   recording `IChatClient`:
   - the inner provider **sees the run's messages**: a `RecordingHindsightClient`-backed Hindsight provider's recall
     query equals the user's text when called through the wrapper;
   - the output never echoes the input messages back (the reason for the `TruncatingSkillsProvider` isolation);
   - the instructions are byte-identical across two runs whose inner output differs;
   - the inner text arrives as the **last** message, after the user turn;
   - tools pass through;
   - `InvokedAsync` reaches the inner provider, filtered according to the Step 0 outcome.

   Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~TrailingContextProviderTests`.

2. **Wire memory and shared state**:
   - `BuildMemoryProvider` returns `new TrailingContextProvider(hindsight)`. Keep the `IsEnabled` closure, and do not
     capture `_ephemeral` (see that file's ordering-trap comment).
   - `SharedStateContextProvider` returns `Messages`.
   - Update the memory and shared-state grain tests: their `## Memories` / `## Shared state` assertions must now be
     seen through `FakeChatClient`'s message echo, not `[sys:…]`. Check how `[ctx:…]` is built (`FakeChatClient.cs`
     around line 504) and extend it if it only echoes the RAG message.
   - Add the AC2 storage assertion (no `## Memories` / `## Shared state` in `SerializedSession`) and the AC1
     instructions-stable assertion.

   Verify: `--filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~SharedStateTests"`.

3. **MCP tool order** *(parallelizable)*: sort each server's tools by `Name` (ordinal) where `SelectableTools` appends
   them. Extract a helper if S3776 complains. Test: a server returning `[b, a]` yields `[a, b]` in the agent's tools.
   Verify: `--filter FullyQualifiedName~AgentGrainTests` and `dotnet build Agent42.slnx`.

4. **SAS memo** *(parallelizable)*: `BlobAttachmentStore` takes a `TimeProvider`, defaulting to
   `TimeProvider.System`, and keep a ctor that DI can resolve. It reuses a URL per (container, blob) while
   `expiresOn - now ≥ ReadUrlLifetime / 2`. If the store cannot be built without Azure in tests, extract the memo
   into a small pure class (`ReadUrlMemo`) and test that with a hand-rolled `TimeProvider` subclass. The repo has
   no FakeTimeProvider package, and none should be added for this. Tests:
   - same URL within the window;
   - a new URL after it;
   - a different blob gets its own URL.

   Verify: `--filter "FullyQualifiedName~AttachmentTests|FullyQualifiedName~AttachmentRehydrateTests"`.

5. **Prompt cache key** (needs Step 0a/b):
   - `PromptCacheKey.For(grainKey)` and `PropertyName`;
   - `BuildAgent` sets the property;
   - `PromptCacheKeyChatClient` in Backend `Models/`, added in `ChatClientFactory.Build` below
     `UseOpenTelemetry` for every provider: strip always, key only for `AzureOpenAI`.

   Tests in `PromptCacheKeyChatClientTests.cs`, on a stub transport like `AnthropicStub` / the provider tests:
   - the Azure body carries `prompt_cache_key` equal to the hashed key, never the raw grain key;
   - an `OpenAICompatible` body and an Anthropic body carry neither the key nor the property;
   - a call with no property set (the fleet, follow-up and research shape) passes through unchanged;
   - a caller's own factory result keeps its fields;
   - the caller's `ChatOptions` are not mutated.

   Verify: `--filter "FullyQualifiedName~PromptCacheKeyChatClientTests|FullyQualifiedName~ModelProviderTests|FullyQualifiedName~AnthropicCompatibilityChatClientTests"`.

6. **Dashboard cached tokens** *(parallelizable)*:
   - Add a test in `UsageProcessorTests.cs`: a fake `IChatClient` returning
     `UsageDetails { CachedInputTokenCount = 40, ReasoningTokenCount = 7 }`, wrapped with `UseOpenTelemetry` on the
     `Agent42.Backend` source; the projected `UsageEvent` has `CachedInputTokens == 40` and reasoning 7.
   - If the test shows the tag is **not** emitted, stop and report. Do not add a grain counter (ADR 0031).
   - Otherwise remove the stale "read 0" note and comment in `index.html`, and fix the doc comments in
     `UsageContracts.cs` and `UsageEvent.cs`.
   - Adjust `UsageAssetsTests` if it pins the note.

   Verify: `--filter "FullyQualifiedName~UsageProcessorTests|FullyQualifiedName~UsageAssetsTests"`.

7. **Per-turn cache writes**:
   - `TurnTokenTotals.CacheWrite` accumulates `usage.Details.AdditionalCounts?["CacheCreationInputTokens"]`, through
     a named constant.
   - `AgentStreamEvent` gains trailing `[property: Id(19)] long? CacheWriteTokens = null`, with a comment in the
     file's style; it is null when 0, like `CachedTokens`.
   - `send.js` passes it, and `showUsage` renders `N cache write`.
   - Bump `sw.js` `CACHE`.

   Tests:
   - a grain test where `FakeChatClient` emits a `UsageContent` with that count (add a marker if needed) and the
     `Done` event carries it;
   - `GrainStorageSerializerTests` / serialization compatibility for the new field;
   - an asset test that `showUsage` reads `cacheWriteTokens`;
   - `PwaAssetsTests` stays green.

   Verify: `--filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~GrainStorageSerializerTests|FullyQualifiedName~PwaAssetsTests"`.

8. **Fleet cached tokens** *(parallelizable)*: `FleetBudget` gains `CachedInputTokens`, counted in `AddTokens` but
   **not** added to `Tokens`, because cached reads are a subset of input and the budget limit must not
   double-count them. The budget event carries `cachedInputTokens`, and `fleet.js` renders a "cached" metric next
   to `#mIn`/`#mOut`. Tests:
   - a `FleetBudget` unit test (cached counted, `Tokens` unchanged);
   - a fleet event/asset test if one pins the budget payload.

   Verify: `--filter FullyQualifiedName~Fleet`.

9. **Docs** (`/decide`, then `/sync-docs`):
   - **ADR 0104 "Prompt-prefix caching"** covers:
     - the segment order rule (stable instructions → tools → history → user turn → per-turn context);
     - the wrapper;
     - the SAS memo;
     - the cache key and its egress note;
     - the usage findings;
     - the Step 0 measurements;
     - the rejected alternatives: the package option (the user chose the wrapper), a grain-side
       `RawRepresentationFactory` (breaks the shim), `cache_control` outside the shim (the user's constraint), and
       a grain token counter for the dashboard (ADR 0031).
   - **Amendment banners:**
     - 0056: memory now arrives as a trailing user-role message; the trust line changes;
     - 0089: shared-state placement;
     - 0031: cached and reasoning are emitted when the provider reports them;
     - 0103: the out-of-scope line about cache reporting and prefix stability.
   - **`gotchas.md`:**
     - correct the usage entry's "no cache-read, no reasoning" (1);
     - new entry: per-turn-varying context must never go in instructions; wrap it with `TrailingContextProvider`
       (evidence: AC1 test);
     - new entry: `GetReadUrlAsync` is memoized, and a fresh SAS per call breaks the history prefix.
   - **`CLAUDE.md`:** add 0104 to the Model providers row and the Telemetry row.

   Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~AdrNumberingTests|FullyQualifiedName~DocPath"`.

10. **Full gate**: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
    `dotnet format Agent42.slnx --verify-no-changes`, and
    `node .agentic/scripts/check-new-code-coverage.mjs`.

Order: 0 → 1 → 2; 0 → 5. Steps 3, 4, 6 and 8 are independent of everything else. Step 7 is independent too, but it
shares `AgentGrain.cs` with 2, so run it sequentially after 2 or in a separate worktree. Step 9 is last.

## Acceptance criteria → tests

| # | AC | Test | File |
|---|----|------|------|
| 1 | With memory and shared state on, the instructions the model receives are byte-identical on two consecutive turns with different recall and state | `MemoryAndSharedState_DoNotChangeInstructions` | AgentGrainTests.cs |
| 2 | Recalled memories and shared state arrive as messages after the user turn, and neither is stored in `SerializedSession` | `TrailingContext_FollowsUserTurn`, `TrailingContext_IsNotPersisted` | TrailingContextProviderTests.cs / AgentGrainTests.cs |
| 3 | Hindsight neither retains nor queries with the injected memory message (per the Step 0c outcome) | `TrailingMemory_IsNotRetained` | TrailingContextProviderTests.cs |
| 3a | Through the wrapper, Hindsight's recall query is the user's text (not empty), and the wrapper's output never echoes the input messages | `Wrapper_PassesRunMessagesToInner`, `Wrapper_DoesNotEchoInput` | TrailingContextProviderTests.cs |
| 4 | Incognito still neither recalls nor retains; memory resumes on the next normal turn | existing `EphemeralTurn_NeitherRecallsNorRetains`, `MemoryResumes_OnTheNormalTurnAfterAnEphemeralOne` stay green | AgentGrainTests.cs |
| 5 | One MCP server's tools reach the model sorted by name regardless of server order | `McpTools_AreSortedPerServer` | AgentGrainTests.cs |
| 6 | Two `GetReadUrlAsync` calls for one blob within the window return the same URL; after it, a new one with a full lifetime | `ReadUrl_IsReusedWhileFresh`, `ReadUrl_IsRenewedPastHalfLife` | AttachmentTests.cs (or ReadUrlMemoTests.cs) |
| 7 | An Azure OpenAI request carries `prompt_cache_key` = the hashed conversation key, never the raw key; OpenAI-compatible and Anthropic requests carry neither; a call without the property passes through | `Azure_SendsHashedPromptCacheKey`, `OtherKinds_SendNoKeyOrProperty`, `NoProperty_IsPassThrough` | PromptCacheKeyChatClientTests.cs |
| 8 | The wrapper keeps a caller's `RawRepresentationFactory` fields and never mutates the caller's options | `CallerFactory_Wins`, `Options_AreCloned` | PromptCacheKeyChatClientTests.cs |
| 9 | A chat span with cached and reasoning counts projects them into `UsageEvent` | `ChatSpan_CarriesCachedAndReasoningTokens` | UsageProcessorTests.cs |
| 10 | The dashboard no longer claims cached and reasoning are always 0 | updated note assertion | UsageAssetsTests.cs |
| 11 | A turn whose usage reports cache creation sends `CacheWriteTokens` on `Done`, and the browser renders it | `Done_CarriesCacheWriteTokens`, `ShowUsage_RendersCacheWrites` | AgentGrainTests.cs / PwaAssetsTests.cs (or the thread asset test) |
| 12 | `AgentStreamEvent.CacheWriteTokens` round-trips the Orleans and Cosmos JSON paths | serializer case | GrainStorageSerializerTests.cs |
| 13 | The fleet budget counts cached input without adding it to `Tokens`, and the budget event carries it | `AddTokens_CountsCachedSeparately` | Fleet budget test file |
| 14 | Anthropic requests still carry top-level `cache_control`, and nothing else adds one | existing `AnthropicCompatibilityChatClientTests` stay green, plus `OtherKinds_SendNoKeyOrProperty` asserts no `cache_control` | AnthropicCompatibilityChatClientTests.cs / PromptCacheKeyChatClientTests.cs |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ 3406469)

| Gate | Threshold | Tool |
|------|-----------|------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | S3776 at `dotnet build` (error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| Format | clean | `dotnet format Agent42.slnx --verify-no-changes` |

## Out of scope

- Deferred tool loading itself (`plan-deferred-tool-loading.md`). When that plan runs, its loaded tools must be
  appended in a stable order (catalog order) so that each load changes the tool array exactly once. Note this in
  ADR 0104's consequences.
- Explicit `cache_control` breakpoints, or any caching on non-Anthropic kinds beyond the Azure key.
- `prompt_cache_key` for `OpenAICompatible` providers, the fleet, the research reducer path, and the follow-up
  suggestion side call.
- Cache-write tokens on the `/usage` dashboard (no OTel tag is known for them) and in the fleet UI.
- The research agent's `ContextWindowCompactionStrategy` trimming the history head.
- MCP servers that fail to connect dropping their tools for an activation.
- Changing `Hindsight.AgentFramework.Net`.
- **The Copilot agent kind** (ADR 0085/0097). It runs the Copilot SDK's own loop
  (`src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs:71`), which assembles its own prompt, and it never receives
  the memory or shared-state providers (they are added only in `BuildAgent`'s `ChatClientAgent` path, at
  `BuildAgent.cs:109, 120`). Its calls still pass through `PromptCacheKeyChatClient` as a no-op.

## Review (critic, 2026-10-19)

| # | Severity | Finding | Handling |
|---|----------|---------|----------|
| 1 | HIGH | Copying `TruncatingSkillsProvider`'s **empty** isolated context would starve Hindsight's recall query | **Fixed.** The isolated context carries a copy of the run's messages; the wrapper keeps only what the inner provider added. New AC 3a. |
| 2 | MEDIUM | Copilot kind is neither handled nor scoped out | **Fixed.** Scoped out with evidence (it never gets these providers). |
| 3 | MEDIUM | The strip runs on every entry point, but only the "no key" part is stated | **Fixed.** Stated in Context, plus a pass-through test (AC7). |
| 4 | MEDIUM | `BuildMemoryProvider`'s return-type change is not listed | **Fixed.** Listed under Affected areas. |
| 5 | MEDIUM | SAS memo: must sit above the Azurite branch too; a miss every ~30 min is undocumented | **Fixed.** Both added to Context, and the residual goes in the ADR. |
| 6 | LOW | OTel can record the cache-key property before the strip | **Fixed.** Noted for the ADR egress paragraph. |

## Step 0 spike results (2026-10-19, measured on the pinned stack: OpenAI 2.14.0, M.E.AI.OpenAI 10.10.1, Anthropic 12.58.0, AF 1.23.0, Hindsight.AgentFramework.Net 0.1.0; stub transports, throwaway test file deleted)

- **(a) Responses** (`GetResponsesClient().AsIChatClientWithStoredOutputDisabled`): a `RawRepresentationFactory`
  returning `OpenAI.Responses.CreateResponseOptions { PromptCacheKey = … }` puts `"prompt_cache_key"` on the wire,
  and `"store":false` survives, so the helper keeps the caller's factory result. Returning `ResponseResult` (the
  only other type with the property) is ignored.
- **(a) Chat Completions** (`GetChatClient().AsIChatClient()`): `ChatCompletionOptions` has **no** `PromptCacheKey`
  property in 2.14.0. `options.Patch.Set("$.prompt_cache_key"u8, key)` (experimental `SCME0001`) puts it on the
  wire.
- **(b)** An unknown `ChatOptions.AdditionalProperties` key reaches the wire on **none** of the three adapters
  (Responses, Chat Completions, Anthropic). The strip is therefore defence in depth, not a fix for a leak.
- **(c)** With another provider injecting a user-role message, `HindsightMemoryProvider`'s recall query was the user's
  text only, and retain was `[user] USER-TEXT / [assistant] REPLY`. The injected message was excluded from both, in
  either provider order. **Hindsight already ignores `AIContextProvider`-sourced messages**, so the wrapper forwards
  `InvokedAsync` unfiltered. AC3 pins it.

## Implementation divergences (2026-10-19)

- **Step 1:** the wrapper keeps the inner provider's additions with `Skip(input.Count)`; mutation-checked
  (`Skip(0)` turns `Wrapper_DoesNotEchoInput`, `NothingRecalled_AddsNoMessage` and `Tools_And_Messages_PassThrough`
  red). Per Step 0c, `StoreAIContextAsync` forwards unfiltered. Two extra tests: `NothingRecalled_AddsNoMessage` and
  `TrailingContext_IsNotStored_InTheSession` (agent-level twin of the grain AC2 test).
- **Step 2:** `FakeChatClient.ContextEcho` now emits one `[ctx:…]` per injected message (it echoed only the first, which
  would have hidden memory and state behind RAG). AC2's grain test asserts on `GetHistoryAsync` user turns, because the
  fake's echo puts the context text into the stored ASSISTANT reply by design.
- **Step 4:** no `ReadUrlMemo` extraction. A shared-key client (`UseDevelopmentStorage=true`) signs locally, so the
  real `BlobAttachmentStore` is tested directly (`BlobAttachmentStoreReadUrlTests`, reusing `TestTimeProvider`). The
  memo is a `MemoryCache` whose entry expiry (real clock) bounds it; freshness is judged with the injected
  `TimeProvider`. The delegation-key freshness check still uses the wall clock, as planned (unchanged).
- **Step 5:** Chat Completions has no `PromptCacheKey` property in OpenAI 2.14.0, so that path uses the experimental
  `JsonPatch` (`SCME0001`). An extra test, `Agent_ForwardsTheProperty`, proves `ChatClientAgent` hands agent-level
  `AdditionalProperties` to the chat client (the grain half of the wiring). The client lives in
  `MultiProviderChatClientFactory.Build` (the plan's "ChatClientFactory").
- **Step 7:** `send.js` needed no change: it already passes the whole Done chunk to `showUsage`. AC12 round-trips
  Orleans binary and the NDJSON (System.Text.Json) shape, not the Cosmos path, because `AgentStreamEvent` is never
  stored. The cache-write constant is `AgentGrain.CacheCreationInputTokensKey`. `sw.js` `CACHE` → `agent42-shell-v65`
  (four asset tests pin the name).
- **Step 8:** no budget-payload test: nothing pins that payload today. The page asset test pins the new metric.
- **Step 9:** ADR 0039's folder inventory gained `PromptCaching/` and the missing `Email/` (ADR 0096);
  `ProjectLayoutTests.ExpectedFolders` updated. The "InvokingContext has no RequestMessages" gotcha got a note.
- **Full suite:** `OtelConfigurationTests.WithAppInsights_GrpcEnvEndpoint_ReachesTheCollectorWithHeaders` failed once
  under full-suite load and passed alone and on the rerun; it is unrelated to this diff.
