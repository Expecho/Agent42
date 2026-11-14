planned-against: main @ 82eda28

# Plan: deferred tool loading (`search_tools`)

> **Note (2026-10-09):** `plan-location-as-client-tool` landed first (ADR 0090). `get_current_location` is now a
> client tool (in `ClientToolLimits.ToolNames`, still under the `location` key), and the turn-scoped field family is
> three (`_ephemeral`, `_interactiveTurn`, `_webDocumentIngested`); re-check the citations below before executing.

## Overview

Every selected tool's full JSON schema is sent on every model call (`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`,
the MCP loop and `Tools = capabilities.SupportsTools ? tools : []`). With a few MCP servers that is dozens of
schemas per call: input tokens, cost, and on small-cap providers (ADR 0013's GitHub 16k/8k input caps, HTTP 413) a
real availability limit. This plan adopts the Claude Code ToolSearch pattern **client-side**, so it works on every
provider: when a conversation has many deferrable tools, the model is sent only the core tools plus a new
`search_tools` function and a compact **name index**; a deferred tool's schema is sent only after `search_tools`
loads it, and loaded tools stay loaded for the conversation.

## Affected areas

- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` — one call into the new helper; the client passed to
  `ChatClientAgent` becomes the wrapped one; instructions gain the index.
- `src/Agent42.Agents/Tools/AgentGrain.DeferredTools.cs` — **new partial**: the `search_tools` function, the
  deferral decision, the index text, loaded-set bookkeeping.
- `src/Agent42.Agents/Tools/DeferredToolsChatClient.cs` — **new**, `DelegatingChatClient` that hides unloaded
  deferred tools per inner call.
- `src/Agent42.Agents/Tools/DeferredToolGate.cs` — **new**, `DelegatingAIFunction` refusing a call to a deferred
  tool whose schema the model was not sent.
- `src/Agent42.Agents/Tools/ToolSearch.cs` — **new**, pure static matching/ranking (unit-testable, no grain).
- `src/Agent42.Agents/Tools/DeferredToolLimits.cs` — **new**, the constants (`WebToolLimits` shape).
- `src/Agent42.Agents/Conversations/AgentGrainState.cs` — trailing `[Id(14)] List<string> LoadedToolNames`.
- `src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs` — `McpServerTools` gains trailing `string? ServerName = null`.
- `src/Agent42.Backend/Mcp/UserMcpToolProvider.cs` — pass `server.Name` at its three `new McpServerTools(` sites.
- Tests: `tests/Agent42.Tests/AgentGrainTests.cs`, `tests/Agent42.Tests/FakeChatClient.cs`,
  `tests/Agent42.Tests/ProjectLayoutTests.cs`, `tests/Agent42.Tests/UserMcpToolProviderTests.cs`, new
  `tests/Agent42.Tests/ToolSearchTests.cs`, new `tests/Agent42.Tests/DeferredToolsChatClientTests.cs`.
- Docs: new ADR `0077`; amendment banners on ADR `0067` and `0075`; `.agentic/gotchas.md` (new entries + the ADR 0067
  and turn-scoped-field entries corrected); `CLAUDE.md` (new bullet + the 0021/0067/0075 bullets corrected); the
  `BuildAgent.cs` comment above `sendPushNotification`. Step 7 lists every site.
- **Not touched:** anything under `wwwroot/` (a `search_tools` call renders as an ordinary tool chip — `thread.js`
  uses `toolName` verbatim, no mapping), so **no `sw.js` `CACHE` bump**; no Bicep, secret, container, package,
  `excludedPaths` or endpoint.

## Context & constraints

### The mechanism was measured, not assumed (spike, 2026-09-18, Microsoft.Agents.AI 1.19.0 + M.E.AI 10.9.0)

A throwaway file-based app with a scripted fake `IChatClient` (script: call `search_tools` → call the deferred
tool → text), both `RunAsync` and `RunStreamingAsync`, default `ChatClientAgentOptions` (what `BuildAgent` uses):

- **Works — the design to build.** Register ALL selected tools normally in `ChatOptions.Tools`; wrap the model
  client in a `DelegatingChatClient` that removes not-yet-loaded deferrable tools from `options.Tools` on each
  inner call. Agent Framework inserts its own `FunctionInvokingChatClient` ABOVE that wrapper, so every selected
  tool stays invocable while the model sees only what is loaded; a tool loaded mid-run is sent on the very next
  inner call of the same run:
  ```
  model call 0: tools=[search_tools]
  model call 1: tools=[search_tools,get_weather]
  model call 2: tools=[search_tools,get_weather]
  AdditionalTools after run: [search_tools,get_weather]  get_weather invoked: 1  final: It is sunny in Utrecht -> PASS
  ```
- **Fails — rejected.** Our own `FunctionInvokingChatClient` with `AdditionalTools` + a layer that ADDS tools: in
  default mode Agent Framework reuses our FICC but **overwrites `AdditionalTools` with the agent's tools**
  (`AdditionalTools after ctor: [search_tools]`) → `Error: Requested function "get_weather" not found.` It only
  works with `UseProvidedChatClientAsIs = true`, which also drops Agent Framework's default decorators
  (`ApprovalResponseBindingChatClient`, `ApprovalNotRequiredFunctionBypassingChatClient`, …); `run_skill_script`
  is approval-wrapped (ADR 0049), so that is not an option.

Record both in the ADR as the evidence for the losing alternative.

A second spike (2026-09-19, same versions, default agent mode, both run paths) proved the gate's per-call
mechanism (design point 1): the wrapper stamps each returned `FunctionCallContent` in `AdditionalProperties`, and a
`DelegatingAIFunction` reads it back via `FunctionInvokingChatClient.CurrentContext?.CallContent`: the stamp
arrives intact. That holds both for a sequential load-then-call and when `search_tools` + the deferred tool arrive in one
parallel batch (refused, because the schema was not in that request):
```
seq   streaming=True: gate saw [sent=[search_tools,get_weather] allowed=True]  get_weather invoked=1 -> PASS
batch streaming=True: gate saw [sent=[search_tools] allowed=False]  get_weather invoked=0 -> PASS
```

### Decisions the user made (2026-09-17/19)

- **Client-side (option B2)** over OpenAI's hosted tool search (`HostedToolSearchTool` in M.E.AI 10.9 — Responses
  path only, so it would never reach Chat Completions providers, the ones with the tightest caps) and over a
  generic `invoke_tool(name, args)` meta-tool (the model never sees the real schema → worse argument adherence).
- **Deferrable = MCP tools + the non-core built-ins**: `schedule_prompt`, `cancel_scheduled_prompt`,
  `cancel_all_scheduled_prompts`, `get_current_location`, `read_web_document`, `send_push_notification`.
  **Always loaded (core):** `get_date`, `web_search`, the ADR 0015 hosted tools
  (`HostedWebSearchTool`/`HostedCodeInterpreterTool` — not `AIFunction`s), `search_tools` itself, and every tool an
  `AIContextProvider` contributes (skills' `load_skill`/`read_skill_resource` — already progressive-disclosure).
  Deferrability is decided by **name** against an explicit list of the built-ins + membership of `_mcpServers`,
  never by `is AIFunction`.
- **Threshold, not always-on:** deferral activates only when the conversation's selected deferrable tools number
  **more than `DeferredToolLimits.ActivationThreshold` (15)**. At or below it `BuildAgent` behaves exactly as
  today — no `search_tools`, no index, no wrapper, no gate. (So ~10 MCP tools plus the 6 built-ins trips it.) A
  constant, not config — tune it by editing the constant.
- **Up front the model sees tool NAMES grouped per server** (the Claude Code shape), appended to the
  instructions, never schemas: one line for the built-ins, one line per MCP server labelled with the server's
  user-given name.
- **Loaded tools stay loaded for the conversation:** persisted in grain state, bounded, least-recently-loaded
  evicted first. An incognito (ADR 0034) turn does not write state, so no special case is needed (design point 8).

### Design points the executor must keep (each is a trap if dropped)

1. **Hidden ≠ uncallable — hence `DeferredToolGate`.** Because the agent's FICC holds every selected tool, a model
   that reads a name off the index (or out of history) can call a deferred tool WITHOUT ever seeing its schema.
   For `read_web_document` and `send_push_notification` that is not cosmetic: ADR 0067's lesson is that **the rule
   the model obeys lives in the TOOL DESCRIPTION** (URL provenance rules; ADR 0075's push policy). So every
   deferrable tool is wrapped (outermost, after `WithUrlSeeding`) in a gate that **allows the call only if that
   tool's schema was in the request that produced the call**. Mechanism (spike-proven, see above): for every
   returned `FunctionCallContent` whose name is deferrable, the wrapper sets ONE bool,
   `AdditionalProperties["agent42.schemaSent"]`, meaning "this call's own tool was in the request". The key is
   defined once, as a constant in `DeferredToolLimits`. Stamp on BOTH the `GetResponseAsync` response messages and
   each streamed update. The gate reads it from `FunctionInvokingChatClient.CurrentContext?.CallContent` and
   **removes the key** before returning: the content object is the one persisted into the session (ADR 0004), so
   leave nothing behind. A missing stamp counts as "not sent", which fails closed. Never a list of names: it would
   be persisted per call. Never a field on the wrapper: see point 9. Otherwise: do NOT invoke, add the tool to the loaded set, and return a short fixed result
   ("`x` was not loaded, so you could not see its parameters or rules. It is loaded now — read its description and
   call it again."). "Schema was sent" is the right test, not "is in the loaded set": `search_tools` and the tool
   can arrive in one parallel batch, and FICC invokes sequentially, so the set can already contain it.
   The server-side controls (fetchable-URL allowlist, push cap, incognito refusal) are unchanged and remain the
   actual enforcement; the gate protects compliance, not security.
2. **The wrapper must sit BELOW the agent's FICC** — i.e. wrap `_chatClientFactory.GetClient(modelId)` and pass
   the wrapper to `new ChatClientAgent(...)`; never build our own FICC (spike, above).
3. **The factory's client is SHARED** across every conversation on that model (`ChatClientFactory` caches it in a
   `ConcurrentDictionary` by qualified id, `src/Agent42.Backend/Models/ChatClientFactory.cs`). The wrapper is per
   `BuildAgent` call, must **`Clone()` the options, never mutate the ones it was given**, and must **not dispose the
   inner client** (`DelegatingChatClient.Dispose` disposes `InnerClient` by default — override it).
4. **Selection still decides reachability (ADR 0021/0058).** The deferrable set is built from `tools` AFTER the
   selection filter and the MCP per-tool filter, so `search_tools` can only ever find and load selected tools; a
   `select:` of an unselected or unknown name loads nothing and says so.
5. **Tool and server names become PROMPT text** once they are in the instructions index. A deferrable tool whose
   name does not match `^[A-Za-z0-9_-]{1,64}$` is simply **not deferred** (stays always-loaded, today's behaviour —
   such a name would not survive the provider's function-name rules anyway). The server name is user-supplied and
   its sink is the system prompt, so it takes an **ALLOWLIST**, per the repo's choose-by-sink rule
   (`src/Agent42.Agents/Push/PushToolLimits.cs` and `src/Agent42.Agents/Documents/WebDocumentLimits.cs` document
   it; ADR 0075 explains why push is the denylist exception). Keep only `[A-Za-z0-9 _.-]`, collapse whitespace,
   cap at `DeferredToolLimits.MaxServerNameLength` (40). An empty result falls back to a neutral `MCP server {n}`
   label, never the opaque server id. The helper is a small public static on `DeferredToolLimits`: do NOT reuse
   `WebDocumentLimits.Sanitize`, which is private and URL-shaped.
5a. **Name collisions — nothing dedupes tool names today** (MCP tools are appended after the built-ins). Rules:
   (i) an MCP tool that shares a name with any **core** tool (`get_date`, `web_search`,
   `code_interpreter`) is **never deferred**, so both are sent exactly as today and the core tool is never hidden;
   (ii) `search_tools` is **reserved**: if any selected tool is named `search_tools`, deferral does not activate for
   that build (log a warning) and the conversation behaves exactly as today; (iii) an MCP tool that shares a name
   with a deferrable built-in is hidden/shown together with it (name-keyed), matching today's duplicate behaviour.
6. **`search_tools` returns bounded text** into the 2 MB session (ADR 0004): at most
   `DeferredToolLimits.MaxSearchResults` (5) tools, the result capped at `MaxSearchResultLength` (2000 chars), each
   entry `name — description` with the description truncated. It returns names/descriptions only; the **schema**
   reaches the model through the wrapper on the next inner call. MCP descriptions are server-authored text that
   was already in every request's schema, so this adds no new inbound channel (ADR 0024), but keep it bounded.
7. **`search_tools` query forms:** `select:a,b` (exact, case-sensitive names — loads every selected deferrable one
   named) or free keywords (case-insensitive token match over tool name, description and server name; name hits
   outrank description hits; ties keep catalog order; loads the top N). Deterministic, no embeddings, no egress.
8. **Loaded-set lifetime.** `LoadedToolNames` is state, mutated by `search_tools` and the gate during the turn and
   persisted by the turn's own existing write (`PersistTurnUnlessEphemeralAsync` on the chat/agent-run paths,
   `ReceiveReminder`'s `WriteStateAsync`) — **no new write sites**. Loading an already-loaded name moves it to the
   end; beyond `MaxLoadedTools` the front is evicted. `MaxLoadedTools` **equals `ActivationThreshold` (15)**, so a
   deferred conversation never sends more deferrable schemas than an undeferred one at the threshold would.
   Incognito: the turn does not write it. It sits in memory and a later write on the same activation would carry
   it, which is exactly how `FetchableUrls` already behaves; an incognito conversation is incognito throughout. It is NOT pruned when the selection changes — the
   wrapper only ever un-hides names that are in the current deferrable set, so stale names are inert and the cap
   bounds them. It is NOT a turn-scoped field: do **not** add it to the `_turnLocation`/`_ephemeral` clear-it-in-
   three-places family.
9. **Other model runs CAN interleave with a streaming run, so the wrapper holds no per-run state.** Orleans'
   `IAsyncEnumerableGrainExtension.StartEnumeration`/`MoveNext`/`DisposeAsync` are `[AlwaysInterleave]`
   (measured by reflection on 10.3.1). Between `MoveNext` batches, `ReceiveReminder`, `EnqueuePromptAsync` or a
   second `ChatAsync` stream can run a whole model run on the same `_agent`, and so on the same wrapper. That
   happens with two tabs on one continuous `agent-` chat, or with an ADR 0070 caller reusing an `mcp-` id. The
   streaming FICC yields a `FunctionCallContent` (→ `ToolCallStart`) BEFORE it invokes the function, so that gap
   falls exactly between "schema sent" and "gate checks". This is why the gate's evidence rides the call itself
   (point 1). The wrapper's only shared inputs are the deferrable-name set (immutable per build) and a read of
   `LoadedToolNames`; a concurrent run loading a tool merely un-hides it early, which is harmless. Also correct
   the turn-scoped-field gotcha: it says `ChatAsync` "suspends" but does not say that another grain call runs in
   the suspension.
9a. **Replaying hidden tools from history is an ACCEPTED residual.** History keeps `FunctionCallContent`/
   `FunctionResultContent` for tools that are now hidden. That happens after the conversation crossed the
   threshold, after eviction, or for `search_tools` calls once it drops back below. The repo guards replay
   WITHOUT a schema only where the request declares no tools at all (the downgrade strip in `SwitchModelAsync`,
   `src/Agent42.Agents/Conversations/AgentGrain.cs`). A deferred request always declares tools (core +
   `search_tools`). Auto-loading every name found in history was rejected: it gradually re-sends every schema a long
   conversation ever touched and undoes the feature. Record it in the ADR as unmeasured on each provider, with a
   post-deploy check (a conversation that crossed the threshold must still reopen on a Chat Completions model and on
   the Responses model).
10. **S3776 (threshold 15, `error`).** `BuildAgent` has little headroom (see its own `WithUrlSeeding` comment). The
    deferral decision and wrapping live in ONE helper call in the new partial returning a small record
    (tools, instruction suffix, client); `BuildAgent` gains no branch.
11. **No `ToolCatalog` entry for `search_tools`** — it follows `get_date`'s always-on, unmapped-name pattern
    (`ToolCatalog.IsToolEnabled` keeps unmapped names). The test
    `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` must learn the new exception.
11a. **Visibility:** `DeferredToolsChatClient`, `DeferredToolGate`, `ToolSearch` and `DeferredToolLimits` must be
    **`public`**. The repo has no `InternalsVisibleTo` and makes types public for tests (`SkillCatalog`,
    `TruncatingSkillsProvider`). Do not copy `UrlHarvestingMcpTool`'s `internal`.
11b. **FakeChatClient facts** (`tests/Agent42.Tests/FakeChatClient.cs`):
    - It echoes `[tools:…]` only on the FINAL, text-producing inner call. Inner calls that return a tool call are
      not echoed.
    - It echoes instructions as `[sys:…]`. Because the index puts every deferred name into `[sys:…]`, tool-list
      assertions MUST go through `ToolNames(...)` (`AgentGrainTests.cs`); a bare `Assert.Contains(name, answer)`
      passes vacuously.
    - Its existing triggers ("remind", "where am i", …) call tools without checking that the tool was offered, so
      over the threshold they land in the gate. The single-call guard allows no retry in the same run, so a
      "refused then succeeds" test spans TWO turns.
    - `TryChainedToolCall` is at its S3776 limit, so its own comment says to extract the new chains.
12. **Instructions are not persisted** (they ride `ChatOptions.Instructions`), and the index lists all deferred
    tools regardless of what is loaded, so it is stable across turns — do not rebuild the agent when the loaded set
    changes.

### Governing ADRs / gotchas

- ADR 0021, 0026, 0048, 0058 (selection; `null == all`; per-tool keys) — deferral only hides, never grants.
- ADR 0015 (hosted-tool swap), ADR 0013 (capability: a no-tools model gets no `search_tools` and no index).
- ADR 0024 (MCP tools, inbound injection), ADR 0067 (the rule is in the description — the gate exists for it),
  ADR 0075 (push policy in the description), ADR 0066 (read_web_document; its name sanitizer).
- ADR 0004 (2 MB session — bound the result), ADR 0049 (approval decorators — why `UseProvidedChatClientAsIs` lost).
- `.agentic/gotchas.md`: "Selectable tools" / null==all, "`AgentSkillsProvider` is a TOOL", the ADR 0067 tool-
  description entry, the turn-scoped-field (`_turnLocation`) family, the MCP tool-provider entries, tool-result
  truncation.
- Exemplars: grain partial `src/Agent42.Agents/Web/AgentGrain.WebTools.cs`; bounded state list
  `SeededFetchableUrls` in `src/Agent42.Agents/Conversations/AgentGrainState.cs`; delegating function
  `src/Agent42.Agents/Mcp/UrlHarvestingMcpTool.cs`; limits class `src/Agent42.Agents/Web/WebToolLimits.cs`; tool-
  gating test `ToolModel_StillRegistersAllTools` and `NewClusterWithMcpToolsAsync` in
  `tests/Agent42.Tests/AgentGrainTests.cs`; scripted tool calls `ToolCallTriggers`/`TryChainedToolCall` in
  `tests/Agent42.Tests/FakeChatClient.cs` (whose `[tools:…]` echo reports what the INNER client received — i.e.
  after the wrapper — which is exactly what these tests need).

## Steps

1. **Limits + pure search** — `DeferredToolLimits.cs`, `ToolSearch.cs` (input: the deferrable entries
   `(name, description, serverName)` + query; output: matched names in rank order; `select:` parsing). Verify:
   `dotnet test Agent42.slnx --filter FullyQualifiedName~ToolSearchTests` (new).
2. **Wrapper + gate** — `DeferredToolsChatClient.cs` (hides unloaded deferrable names, stamps `agent42.schemaSent` on returned calls,
   clones options, does not dispose inner) and `DeferredToolGate.cs`. Verify:
   `--filter FullyQualifiedName~DeferredToolsChatClientTests` (new; a fake inner client, no cluster).
   *Parallelizable with step 1.*
3. **Server name plumbing** — trailing `ServerName` on `McpServerTools`; `UserMcpToolProvider` passes `server.Name`
   at all three sites; `SingleUserMcpToolProvider.Tools` convenience may leave it null (the index then falls back to
   a neutral label, never the opaque id). Verify: `--filter FullyQualifiedName~UserMcpToolProviderTests`.
   *Parallelizable with 1–2.*
4. **State** — `[Id(14)] LoadedToolNames` on `AgentGrainState`, zero-migration comment in the `SeededFetchableUrls`
   shape. Verify: `--filter "FullyQualifiedName~GrainStorageSerializerTests|FullyQualifiedName~SerializationCompatibilityTests"`.
5. **Grain wiring** — new partial `Tools/AgentGrain.DeferredTools.cs` (the `search_tools` `AIFunction`, the helper
   that decides deferral and returns tools/instruction suffix/client, the index text, loaded-set bookkeeping);
   `BuildAgent` calls it once; add the partial to `ProjectLayoutTests.AgentGrainParts()`. Verify:
   `dotnet build Agent42.slnx` (S3776 is a build error) then `--filter FullyQualifiedName~ProjectLayoutTests`.
6. **Grain tests** — extend `FakeChatClient` with triggers that call `search_tools` (select and keyword forms),
   then the deferred tool, and one that calls a deferred tool directly (gate path); write the AC tests in
   `AgentGrainTests`; update `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool`. Verify:
   `--filter FullyQualifiedName~AgentGrainTests`, then the full suite (existing tool-list tests must stay green —
   they run below the threshold).
7. **Docs** — `/decide` → ADR 0077, which **amends ADR 0067 and ADR 0075** (and extends 0021/0024).
   - **ADR content:**
     - context and decision;
     - the three rejected alternatives, with both spikes' output;
     - the history-replay residual (point 9a) and its post-deploy check.
   - **Consequences the ADR must record:**
     - the extra round-trip on a tool's first use;
     - the threshold, and `MaxLoadedTools` equal to it;
     - user-given MCP **server names** now reach the model (ADR 0013 egress; before this only tool names and
       schemas did);
     - changing the tool array mid-conversation invalidates provider prompt caching.
   - **Inline amendment banners (the 0028←0075 precedent)** go on
     `.agentic/decisions/0067-mcp-results-seed-the-fetch-allowlist.md` and
     `.agentic/decisions/0075-model-invoked-push-notification.md`. Both
     state that the rule-bearing description "ships in the JSON schema every turn". Above the threshold it no longer
     does; the gate upholds the property.
   - **Premise sites to correct** (same claim, fix each):
     - `.agentic/gotchas.md`: the ADR 0067 entry ("in the JSON schema on **every** turn"), plus the turn-scoped-field
       entry's missing interleave sentence (point 9);
     - the `BuildAgent.cs` comment above `sendPushNotification` ("the description ships in the JSON schema every
       turn");
     - `CLAUDE.md`'s ADR 0067 bullet ("in the JSON schema every turn") and ADR 0075 bullet;
     - `CLAUDE.md`'s ADR 0021 bullet ("`get_date` and document RAG stay always-on"), which gains `search_tools`.
   - **`/gotcha` entries:**
     - wrapper below FICC, because `AdditionalTools` is overwritten (spike output);
     - the shared factory client: clone, never dispose;
     - hidden ≠ uncallable, the per-call stamp and why it is not a wrapper field;
     - `LoadedToolNames` is state, not a turn-scoped field;
     - the name-collision rules.
   - **Finish with:** one new `CLAUDE.md` bullet for ADR 0077, then `/sync-docs` as a final sweep, not as the list.
8. **Gates** — `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`,
   `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"`, `node .agentic/scripts/check-new-code-coverage.mjs`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | At or below the threshold, the tool list the model receives is unchanged and has no `search_tools` | `FewDeferrableTools_SendsEveryToolAndNoSearchTools` | AgentGrainTests.cs |
| 2 | Above the threshold, the first inner call carries the core tools + `search_tools` and no deferrable tool | `ManyDeferrableTools_HidesThemBehindSearchTools` | AgentGrainTests.cs |
| 3 | Above the threshold, the instructions list deferred names grouped per server under the (allowlist-sanitized) server's name, never the opaque server id; a name that sanitizes to empty gets the neutral label | `DeferredIndex_GroupsNamesPerServerName` (via the `[sys:…]` echo), `ServerName_IsAllowlisted_WithNeutralFallback` | AgentGrainTests.cs / ToolSearchTests.cs |
| 4 | `search_tools("select:x")` makes `x`'s schema appear on the next inner call of the same run and `x` is invoked | `SelectLoadsTool_AndItIsCallableInTheSameRun` | AgentGrainTests.cs |
| 5 | Keyword search ranks name hits above description hits, is case-insensitive, returns at most 5, deterministic order | `Keywords_RankNameAboveDescription`, `Keywords_CapResults`, `Select_ParsesExactNames` | ToolSearchTests.cs |
| 6 | A loaded tool is still sent on the next turn and after reactivation | `LoadedTool_SurvivesNextTurnAndReactivation` | AgentGrainTests.cs |
| 7 | The loaded set is bounded at `MaxLoadedTools` (= threshold), re-loading moves a name to the end, the front is evicted | `LoadedSet_IsBoundedLeastRecentlyLoadedFirst` | AgentGrainTests.cs (or a pure helper test) |
| 8 | A call to a deferred tool whose schema was not sent is not invoked, returns the fixed refusal, and the tool is loaded so the call succeeds on the NEXT turn | `UnloadedDeferredToolCall_IsRefusedThenLoaded` (two turns) | AgentGrainTests.cs |
| 8a | `search_tools` + a deferred tool in ONE parallel batch: the deferred call is refused (its schema was not in that request), on both run paths | `ParallelBatch_DeferredCallIsRefused` (agent over a scripted fake, no cluster — the spike's shape) | DeferredToolsChatClientTests.cs |
| 8b | The gate removes the stamp, so no `agent42.schemaSent` key reaches the persisted session; a missing stamp is refused | `Gate_RemovesStamp`, `Gate_MissingStamp_FailsClosed` | DeferredToolsChatClientTests.cs |
| 8c | Name collisions: an MCP tool named like a core tool is never deferred and the core tool is never hidden; a selected tool named `search_tools` disables deferral for that build | `McpToolNamedLikeACoreTool_IsNeverDeferred`, `ToolNamedSearchTools_DisablesDeferral` | AgentGrainTests.cs |
| 9 | `search_tools` never finds or loads a deselected tool (ADR 0021/0058) | `SearchTools_CannotLoadADeselectedTool` | AgentGrainTests.cs |
| 10 | A no-tools model gets no `search_tools`, no index | `NoToolModel_GetsNoSearchToolsOrIndex` | AgentGrainTests.cs |
| 11 | A deferrable tool with a non-conforming name is never deferred | `NonConformingName_IsNeverDeferred` | ToolSearchTests.cs / DeferredToolsChatClientTests.cs |
| 12 | The wrapper never mutates the caller's options and never disposes the shared inner client | `Wrapper_ClonesOptions`, `Wrapper_DoesNotDisposeInner` | DeferredToolsChatClientTests.cs |
| 13 | The search result stays within `MaxSearchResultLength` | `SearchResult_IsBounded` | ToolSearchTests.cs |
| 14 | A tool loaded on the reminder path is persisted (the reminder's prompt drives a `search_tools` load of a deferrable tool — `schedule_prompt` itself is deferrable, so schedule the reminder while below the threshold or load it first) | `ReminderTurn_PersistsLoadedTools` | AgentGrainTests.cs |
| 15 | MCP groups carry the server's name | extend an existing connect test | UserMcpToolProviderTests.cs |
| 16 | The new partial is pinned; `search_tools` is an allowed unmapped tool — asserted in a cluster OVER the threshold (the existing test injects one MCP tool, so it never registers `search_tools` and would pass vacuously) | `AgentGrainParts` list; new `EveryRegisteredTool_…_OverThreshold` variant | ProjectLayoutTests.cs / AgentGrainTests.cs |
| 17 | Persisted state without `LoadedToolNames` deserializes to an empty (non-null) list, and the field round-trips — the ADR 0067 precedent, not the golden document alone | `MissingLoadedToolNames_DeserializesEmpty`, `LoadedToolNames_RoundTrips` + existing golden test green | GrainStorageSerializerTests.cs |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` over coverlet cobertura |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `error` — `dotnet build Agent42.slnx` |
| vulnerable packages | 0 | CI package-freshness (no package is added) |
| lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |

## Out of scope

- OpenAI's hosted tool search (`HostedToolSearchTool`) — a possible later swap on the Responses path only.
- Embedding/semantic ranking in `search_tools` (would re-import ADR 0030's per-call egress).
- A user preference or per-agent toggle; a config-driven threshold.
- Deferring `get_date`, the web tools, the hosted tools or skills' tools.
- Any UI change (the `search_tools` chip renders through the existing path).
- DevHost exercisability: DevHost's tool count stays below the threshold, so it behaves as today; the feature is
  verified by the grain tests. Pushing DevHost over the threshold would break its canned `demo_find_links` chain
  (`CannedChatClient` checks `options.Tools` for that name) — a recorded residual, not a step.
- Measuring the real token saving in production — a post-deploy check via `/logs` (ADR 0054) gen_ai spans, not
  a gate.

## Review

`critic` pass (2026-09-19) against the first draft. It found no blocking ADR contradiction and confirmed that every
named anchor exists and the gate thresholds match `quality-gates.yaml`. Findings and handling:

| # | Severity | Finding | Handling |
|---|---|---|---|
| 1 | HIGH | Design point 9 was conditional. Orleans' `IAsyncEnumerable` grain extension methods are `[AlwaysInterleave]`, so another run can share the wrapper mid-run and a "last sent" field is unreliable. | **Fixed.** The gate's evidence is now a per-call bool stamp on the `FunctionCallContent`, spike-proven on both run paths including the parallel batch (Context §2nd spike, design points 1 and 9). The gotcha correction is added to step 7. |
| 2 | MEDIUM | No rule for tool-name collisions (an MCP `web_search`, or `search_tools`). | **Fixed.** Design point 5a and AC 8c. |
| 3 | MEDIUM | Deferral breaks the "description ships every turn" premise in ADR 0067/0075, gotchas, the `BuildAgent` comment and `CLAUDE.md`; step 7 named none of them. | **Fixed.** ADR 0077 amends 0067 and 0075 with banners; step 7 lists every site. |
| 4 | MEDIUM | The sanitizer instruction was self-contradictory: it described a denylist but pointed at 0066's private, URL-shaped allowlist. | **Fixed.** A system-prompt sink takes an allowlist; a local public helper; a neutral fallback (point 5, AC 3). |
| 5 | MEDIUM-LOW | History replays calls to now-hidden tools. | **Accepted as a residual, not designed around** (point 9a). A deferred request still declares tools, unlike the downgrade case the repo guards. Auto-loading every tool name found in history was rejected because it gradually undoes the feature. The residual is recorded in the ADR with a post-deploy check. |
| 6 | MEDIUM-LOW | There is no `InternalsVisibleTo`, and the exemplar is `internal`. | **Fixed.** Point 11a makes the new types `public`. |
| 7 | LOW | AC17 was only a golden-document check; AC16 was vacuous below the threshold; AC3's hedge was unneeded (the fake echoes `[sys:…]`). | **Fixed** in the AC table. |
| 8 | LOW | FakeChatClient specifics: only the final call is echoed; the index pollutes `[sys:…]`; existing triggers land in the gate; there is no retry in the same run; `TryChainedToolCall` is at its S3776 limit. | **Fixed.** Point 11b; AC8 is now two turns and AC14 loads first. |
| 9 | LOW | The incognito wording ("never persists") was imprecise. | **Fixed.** Point 8 now says "the turn does not write it", the `FetchableUrls` behaviour. |
| 10 | LOW | Consequences to record: `MaxLoadedTools` 30 > threshold 15; server names now reach the model; prompt caching. | **Fixed.** `MaxLoadedTools` = threshold (15); the other two are in step 7's consequence list. |

No finding was rejected outright. Finding 5 was accepted as a residual rather than fixed, for the reason given.
