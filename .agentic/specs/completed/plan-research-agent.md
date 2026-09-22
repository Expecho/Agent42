planned-against: main @ b78e1ce

# Plan: a seeded, built-in Research agent

## Overview

Add a **Research** agent that every user finds in their agent list. It is a normal stored agent (ADR 0003): editable, with an
ADR 0022 key and ADR 0070 exposure. A chat bound to it runs a research loop: plan → search/read → reflect → answer. The loop is
composed from Agent Framework 1.19 building blocks already in `Microsoft.Agents.AI`: `TodoProvider` (plan state),
`CompactionProvider` (bounded prompt) and `LoopAgent` (re-invoke until done), wrapped around the existing `ChatClientAgent`.
While it works, the user sees a **live todo checklist**. Why now: the web tools (ADR 0011/0027) and RAG already exist. What is
missing is the plan/reflect/stop structure. A spike (2026-09-22, see Context) showed that structure composes on our stack
without the `Microsoft.Agents.AI.Harness` package.

## Affected areas (verified against the tree @ b78e1ce)

- `src/Agent42.Agents/StoredAgents/AgentDefinition.cs`: new trailing `[Id(11)] string Kind = ""`
- `src/Agent42.Agents/StoredAgents/AgentBinding.cs`: new trailing `[Id(5)] string Kind = ""`
- `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` (+ `IAgentRegistryGrain.cs`): `AgentRegistryState [Id(1)] bool
  ResearchAgentSeeded`, `EnsureResearchAgentAsync`, and the Kind/Continuous invariants in `SaveAsync`
- `src/Agent42.Agents/Conversations/AgentGrainState.cs`: new trailing `[Id(14)] string? AgentKind` (13 is the current max)
- `src/Agent42.Agents/Conversations/AgentGrain.cs`:
  - `BindAgentAsync` (:419) and `GetAgentBindingAsync` (:465)
  - `StreamModelReplyAsync` (:843–886): todo emit and iteration boundary
  - the stop path (`AppendStoppedTurnToSession`/`TryPrepareStoppedTurn`, :907–928)
  - `ReceiveReminder` (:1444–1494): research reset site
  - `GetHistoryAsync` projection (:1235+): the one-assistant-turn invariant
- `src/Agent42.Agents/Conversations/AgentStreamEvent.cs`: `AgentStreamEventType.Todos` (appended LAST), a trailing
  `[Id(14)] IReadOnlyList<ResearchTodo>? Todos = null` on `AgentStreamEvent` (13 = `McpServerId` is the current max), and a
  new `[GenerateSerializer]`/`[Alias("Agent42.Agents.ResearchTodo")]` record in the flat namespace
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`: a single plain call into the research composition (S3776)
- NEW folder `src/Agent42.Agents/Research/` (flat namespace `Agent42.Agents`, rule 1): `AgentGrain.Research.cs`,
  `ResearchLimits.cs`, `ResearchAgentDefaults.cs` (seed name, description, prompt, tools, and the `AgentKinds.Research`
  constant), `ResearchTodo.cs`. **A new folder requires updating** `tests/Agent42.Tests/ProjectLayoutTests.cs`
  (`FeatureFolderSet_IsExactlyWhatTheAdrDescribes` :92–128 and `AgentGrainParts` :130–148) **and**
  `.agentic/decisions/0039-feature-folders-in-agents-project.md` (folder list :34/:39) in the same MR
- Binding call sites (all four pass Kind): `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:147`,
  `src/Agent42.Backend/Mcp/AgentMcpRunner.cs:64`, `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs:114`,
  `AgentGrain.cs:467`
- `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` (the GET handler seeds) and `AgentContracts.cs` (Kind is
  **response-only**; `AgentUpsertRequest` does not gain it)
- `src/Agent42.Backend/Mcp/AgentMcpRunner.cs` (`DrainWithinBudgetAsync` :131–137): final-iteration text only
- NDJSON streaming is already generic (`AgentApi.Conversations.cs:14–18,163`): **no backend mapping change**
- Frontend:
  - `wwwroot/js/agents.js`: Research badge (:61 list row); continuous checkbox locked for research
  - `wwwroot/js/send.js`: dispatch a `todos` chunk to `research.js`
  - NEW `wwwroot/js/research.js`: checklist render, imported into the `main.js` graph
  - `wwwroot/sw.js`: `CACHE` bump + `SHELL` entry
  - checklist CSS
- `tools/Agent42.DevHost/CannedChatClient.cs`: a scripted research path (`todos_add` → `web_search` → `todos_complete`, plus
  a judge verdict) so DevHost can demonstrate the loop instead of running to the cap
- Tests (`tests/Agent42.Tests/`):
  - new `ResearchAgentTests.cs`
  - extend `GrainStorageSerializerTests.cs`, `AgentRegistryGrainTests.cs`, `AgentApiTests.cs`, `AgentMcpServerTests.cs`,
    `StopGenerationTests.cs`, `ProjectLayoutTests.cs` and `AgentEditorAssetsTests.cs`
  - add `FakeChatClient` scripting as needed
- Docs:
  - the new ADR, numbered by `/decide` at execution time (0082 is free today, but don't pin it)
  - `.agentic/gotchas.md` entries plus a banner on "A cancelled Agent Framework run persists NOTHING"
  - ADR 0039 folder list
  - CLAUDE.md feature-map row

## Context & constraints

### Interview decisions (user, 2026-09-22)
- **Shape: a seeded built-in agent**, not a generic "research mode" flag on every agent. The code still needs a discriminator
  (`Kind`), but it is **set only by seeding** and never accepted from the upsert request. The user can edit everything else.
- **Stop rule: todos done + an AI judge**, under a hard iteration cap.
- **Progress UI: a live todo checklist** (a new stream event plus a frontend module), not just tool-call folding.
- **Per-chat only:** a research agent may never be `Continuous`. Enforce it server-side in the registry and lock it in the
  editor. Reason: ADR 0016 — a continuous chat is one 2 MB grain, and research turns are the largest turns we produce.

### Measured facts (spike 2026-09-22, AF 1.19.0, net10.0, offline fake client, streaming path)
Spike files were ephemeral. This list is the record; Step 1 re-measures anything that turns load-bearing.
- **`[Experimental("MAAI001")]`**: `CompactionProvider`, every `*CompactionStrategy`, `LoopAgent`, `LoopAgentOptions` and every
  `*LoopEvaluator`. **Stable:** `TodoProvider`, `AgentModeProvider`. The repo suppresses MAAI001 with **scoped
  `#pragma warning disable/restore`**, never `NoWarn` (`src/Agent42.Backend/Models/ChatClientFactory.cs:304`,
  `src/Agent42.Agents/UserSkills/TruncatingSkillsProvider.cs:76`). Copy that.
- `TodoProvider` state (StateBag `"TodoProvider"`, about 140 chars) round-trips through `Serialize/DeserializeSessionAsync`.
  `TodoCompletionLoopEvaluator` resolves the provider through `GetService`, with no wiring. The todo list reaches the model as an
  injected **user** message tagged `AIContextProvider`, so `ExcludeInjectedContextMessages`
  (`src/Agent42.Agents/Documents/AgentGrain.Documents.cs:35`) keeps it out of stored history.
- **Compaction trims only the model's view. Stored history keeps every raw tool result**, so rule 5 truncation stays mandatory.
  Register `CompactionProvider` **LAST** in `AIContextProviders`. With RAG after it, RAG was unbudgeted (view 48k vs 28k chars).
  Compaction persists its own `messagegroups` state, which can **duplicate un-evicted raw tool results**: session 72k with
  compaction last, 153k with compaction first, 181k in the chat-client pipeline. Budget for it in the size test.
- Rejected alternatives (recorded so nobody retries them): compaction in the `ChatClientBuilder` pipeline (largest state), and
  `strategy.AsChatReducer()` on the history provider. The reducer shrinks stored history but does not bound the in-run view,
  and it deletes history that ADR 0080 replays.
- Cancelling the token stops `LoopAgent` mid-loop on the streaming path (`TaskCanceledException`, no further model calls), and
  the session still serializes afterwards. **The session then contains the iterations that had already finished** (spike 3
  observed stored messages after the cancel). Step 1 re-measures the exact shape.
- **Spike trap:** file-based apps disable reflection JSON. Any spike here needs `#:property PublishAot=false` and
  `#:property JsonSerializerIsReflectionEnabledByDefault=true`, or serialization falsely fails on compaction state.

### Load-bearing invariant: one user turn → ONE assistant turn, and none while running
Several consumers assume the history gains exactly one assistant turn per user turn, and **only when the run finishes**:
- `send.js:49–50` states it outright. ADR 0046 stream recovery then adopts the reply at
  `turns.length >= priorTurns + 2 && last.role !== "user"` (`send.js:73`), and the next turn's `priorTurns` yardstick
  (`send.js:142`) builds on that count.
- `GetHistoryAsync` reads the **live** `_session` (`AgentGrain.cs:1235–1237`), and calls interleave between `ChatAsync`
  yields (the `_turnCts` remarks, `AgentGrain.cs:244–246`).
- Under `LoopAgent`, each finished iteration is persisted into the session **mid-turn**. So a backgrounded research turn would
  be "recovered" with iteration 1's intermediate text, and a reload could show N assistant bubbles against one live bubble.

**Decision:** enforce the invariant **in the grain**, not in `send.js`:
- (a) While a research turn is in flight, `GetHistoryAsync` hides everything after that turn's user message. This needs a
  turn-scoped marker cleared in both reset sites (rule 6).
- (b) The projection folds all assistant output between two user messages into one assistant turn. Its text is the **final
  iteration's** text. Earlier iterations' tool calls still replay through ADR 0080 folding.
- Live streaming, `reply` (follow-ups, TTS, the ADR 0078 hand-append), and the ADR 0070 MCP reply all use the **final
  iteration's text only**, so live and reload match.

Both parts depend on an **iteration boundary** in the stream, which Step 1 measures. If `LoopAgent` exposes none, count
completed inner runs another way; don't parse text.

### `Model` of the seeded agent: concrete, never `""`
`""` is the ADR 0019 "no model" sentinel, and it breaks the editor:
- `agents.js:194` returns early on `!payload.model`, so a save does nothing.
- `agents.js:154` + `models.js:88`: `selectModel` returns early on a falsy id, so an edit silently rewrites the model.
- `modelLabel("")` is empty (`agents.js:61`, `:248`).
- In the grain it works only by accident, through the no-model rebind (`AgentGrain.cs:639–655`, `:1543–1547`).

**Decision:** seed with the user's **concrete** `IChatClientFactory.DefaultModelId`, resolved in the GET handler through the
per-user factory (ADR 0018). If that id is `""` (no provider yet), **don't seed**. The flag stays false, so the agent appears on
the first GET after the user adds a provider. No frontend model special-casing is needed.

### Open risks for Step 1 (spike before any production code)
1. **What `LoopAgent` persists per iteration.** Its "on-behalf-of" feedback is input to the inner agent, so it likely lands as
   `user` messages. That breaks the `TurnAttachments` 1:1 join (`AgentGrain.cs:1223`), chat-search ordinals and replay.
   Expected fix: extend the request storage filter to drop messages with **`Role == User` AND
   `AuthorName == ResearchLimits.LoopAuthorName`**. Match on both: assistant messages carry the agent's name as `AuthorName`
   (`BuildAgent.cs:276`, `AgentGrain.cs:927`), and the user can rename the agent. Use a fixed author name the UI can't produce.
2. **The iteration boundary** in the streamed updates (message ids, `AgentResponseUpdate` metadata, or a `LoopAgent` hook), for
   the history invariant and final-iteration text.
3. **The stop path under a loop.** `reply` accumulates text from **every** iteration (`AgentGrain.cs:731`, appended :921–928).
   The hand-append from gotcha "A cancelled Agent Framework run persists NOTHING" would duplicate the user message **and** the
   text of iterations already persisted. It also skips compaction's `messagegroups`. Measure what the session holds after a
   stop in iteration 2, then pick one: append nothing, or append only the unpersisted tail.
4. **Resetting todo state per turn.** Todos live in the session, so a second question inherits the old list. Find the
   supported reset (StateBag removal vs `todos_remove`).
5. **Reading todos mid-stream** (`TodoProvider.GetAllTodosAsync`) while the provider holds its per-session lock.
6. **Session portability across agent shapes.** Serialize from `LoopAgent`+research providers and deserialize into a plain
   `ChatClientAgent`, and the reverse. This happens on bind (`AgentGrain.cs:443–446`), on `SwitchModelAsync` (:1003–1008), and
   when a no-tools model downgrades a research chat to a plain agent. Orphan StateBag keys must not throw.
7. **The AI judge's call** goes through the conversation's own `IChatClient` (no new egress, rule 13) and appears as a `chat`
   span (usage dashboard gotcha). Also measure what verdict format the judge expects, for the fakes.

### Design decisions (planner; the ADR records them)
- **Kind is persona, not routing.** It changes model behaviour, so unlike `Continuous`/`McpExposed` it **is** snapshotted
  into `AgentBinding`. This amends ADR 0003's snapshot list. Editing the seeded agent never changes an existing chat.
- **Seeding** happens in the registry grain (`EnsureResearchAgentAsync(model)`), called by `GET /api/agents` before listing.
  It goes through the normal insert path, so the ADR 0022 key hash and directory registration stay consistent; the raw key is
  discarded, and the user regenerates to get one. **Set `ResearchAgentSeeded` in the SAME `WriteStateAsync` as the insert**
  (`AgentRegistryGrain.cs:57`): a second write leaves a crash window that seeds twice. Two concurrent GETs are safe, because
  the grain is non-reentrant and the flag makes the second a no-op. **A deleted research agent is not re-seeded.** Existing
  registry documents deserialize the flag as `false`, so existing users get the agent once. The side effect on a GET is
  accepted: it is idempotent and user-scoped, and the offline IndexedDB cache simply stores the seeded list.
- **Seeded tools:** `web_search` only (it covers search and page fetch). **Not `read_web_document`**, for two reasons:
  - Mid-run ingestion cannot rebuild retrieval (gotcha "A mid-run ingest cannot rebuild the agent"), and a whole loop is one
    run. A re-read in iteration 2 would tell the model excerpts "are added to your context automatically"
    (`Documents/AgentGrain.WebDocumentTool.cs:89–91`), which is false until the turn ends.
  - Seeding it would bypass that key's fail-safe consent stance (`ToolCatalog.cs:96–101`).

  The user can still add it; the ADR records the caveat.
- Todo tools are **not** a catalog key. They come with the research kind, like RAG (gotcha "`AgentSkillsProvider` is a
  TOOL"), so rule 11 doesn't apply. They are a tool, so a **no-tools model gets a plain agent**: no TodoProvider, no loop.
- **Composition** (`Research/AgentGrain.Research.cs`): append `TodoProvider`, then `CompactionProvider` **last**, to the existing
  provider list. Wrap the `ChatClientAgent` in `LoopAgent([TodoCompletionLoopEvaluator, AIJudgeLoopEvaluator(sameClient,
  criteria)], MaxIterations = ResearchLimits.MaxIterations, ExcludeOnBehalfOfMessages = true, OnBehalfOfAuthorName =
  ResearchLimits.LoopAuthorName)`. `_agent` is typed `AIAgent` (`AgentGrain.cs:131`).
- **Limits:** a conservative constant context window and output size (`ResearchLimits`). `ModelCapabilities` carries no
  window size, so small-cap providers are a known limitation (gotcha "GitHub Models: the context-limit error is a 413").
- **Reminder path:** `ReceiveReminder` calls `_agent.RunAsync` directly (`AgentGrain.cs:1482`), so a scheduled prompt in a
  research chat runs the full loop without streaming. That is accepted. It needs its **own** todo-reset and in-flight-marker
  site, whether or not those are fields.
- **ADR 0070/0022:** a research turn will often exceed the MCP `RunTimeout` of 120 s (`AgentMcpLimits.cs:54`). The existing
  "still running" contract applies, and the ADR states it. The MCP reply carries final-iteration text only.
- **Egress (rule 13):** the judge calls the same user provider as the conversation, so there is no new third party, but it
  costs one extra call per iteration. If the user adds `memory`, Hindsight recall and retain run **once per iteration**
  (`BuildAgent.cs:264`), not once per turn. The ADR records both.
- The checklist is **live-only**, like `Suggestions`/`Reasoning`. The share view (ADR 0035) and the local bridge need nothing.
  The emit lives in `StreamModelReplyAsync`, because the `ProjectStreamed*` projections are static and synchronous
  (`AgentGrain.cs:975–985,1027`). Extract a helper to stay under S3776.

### Governing ADRs and gotchas to read first
- **ADRs:** 0003, 0004, 0013, 0016, 0018, 0019, 0021, 0022, 0039, 0046, 0055, 0068, 0070, 0078, 0080.
- **The new ADR must declare** `amends` 0003 (Kind snapshotted), 0016 (research is per-chat only), 0046 (the in-flight/fold
  invariant) and 0078 (stop under a loop).
- **Gotchas:**
  - "An agent tool's RETURN value is persisted"
  - "A grain's turn-scoped field lives for the ACTIVATION"
  - "A cancelled Agent Framework run persists NOTHING"
  - "The stop handle is NOT a turn-scoped field"
  - "`AgentSkillsProvider` is a TOOL"
  - "A mid-run ingest cannot rebuild the agent"
  - "Cosmos grain storage is JSON with `$type`"
  - "A collection expression into `IReadOnlyList<T>`"
  - "Chat-search ordinals must come from ONE projection"
  - "Every MR that touches `wwwroot/js/` must bump `CACHE`"
  - "A frontend module unreachable from `main.js`"
  - "Testing that a bound persona reaches the model needs the fake to echo its instructions"
  - "xUnit v2 runs `IAsyncLifetime` per TEST"

**Concurrent plan:** `.agentic/specs/plan-deferred-tool-loading.md` (unexecuted) also edits `BuildAgent`. Whichever lands
second rebases, and its tool-hiding layer must sit **below** the `LoopAgent` wrap.

## Spike results (Step 1, 2026-09-22, AF 1.19.0, net10.0, offline fake client)

Spikes ran from the session scratchpad with `#:property PublishAot=false` +
`JsonSerializerIsReflectionEnabledByDefault=true`. Expected vs actual:

1. **Loop feedback storage.** Expected: stored as a `user` message. Actual: the todo evaluator's feedback was stored as
   `user author=<OnBehalfOfAuthorName> 'You still have incomplete todo items…'`. The judge's and a delegate's feedback were
   **not** stored when `CompactionProvider` was registered, but **were** stored with `TodoProvider` alone. So the behaviour
   is inconsistent, and the Role+AuthorName storage filter is mandatory, as planned.
   **Trap found on the way:** replacing `StorageInputRequestMessageFilter` with a filter that forgets the `ChatHistory`
   exclusion made every judge re-invocation store the original question and the prior answer a second time. The repo's
   `IsInjectedOrReplayed` covers this; the new filter must extend it, not replace it.
2. **Iteration boundary.** Expected: some marker in the updates. Actual: nothing. Every update carries `author=Research`,
   and message ids differ per LLM call, not per iteration. **Chosen instead:** an *observer* `DelegateLoopEvaluator`
   placed FIRST. It returns `Stop()`, which does **not** short-circuit (judge calls = 2 with the observer first). It
   increments a per-run counter carried in `AgentRunOptions.AdditionalProperties`, which reaches it as
   `LoopContext.RunOptions`. The counter changes between the last update of iteration k and the first update of k+1
   (`markerNow=0 … 1` observed). The counter is per run, so concurrent runs on one activation don't share it. At the
   iteration cap the evaluators are not run after the last iteration (4 agent calls, 3 judge calls at `MaxIterations=4`).
3. **Stop in iteration 2.** Actual: `OperationCanceledException`. The session holds the user message **plus all of
   iteration 1** (call, result, `'iter1 text'`), and nothing of iteration 2. `reply` had accumulated `'iter1 text'`.
   So the existing hand-append would duplicate both the user message and the iteration-1 text. **Chosen:** append the
   user message only when no iteration completed. Append the text tail only when it was streamed in the iteration that
   did not complete.
4. **Todo reset.** `TodoProvider.StateKeys` = `["TodoProvider"]`. `session.StateBag.TryRemoveValue("TodoProvider")` →
   `true`, and `GetAllTodosAsync` → 0 afterwards. This is the supported reset.
5. **Reading todos mid-stream.** `GetAllTodosAsync(session)` called between updates, after each `FunctionResultContent`,
   returned the live list (`1:find A:True; 2:find B:False`) within the 2 s guard, with no deadlock.
6. **Portability.** research→plain and plain→research deserialize and run (`plain ok`, `research ok`). Orphan StateBag
   keys don't throw.
7. **Judge.** It is called with no tools and `ResponseFormat = ChatResponseFormatJson`. The system prompt starts
   `"You are an evaluator."`, and the user message starts `"# Has the original request been fully addressed?"`. The verdict
   is JSON `{"answered":bool,"gapAnalysis":string}`, with the text fallback `VERDICT: DONE` / `VERDICT: MORE`. Evaluators run
   in order and the first that asks to continue wins, so the judge is only called once the todos are done.
   `NonStreamingReturnsLastResponseOnly = true` makes `RunAsync` (the reminder path) return only the final iteration
   (`text='answer4' msgs=1`).

**Contradicts the design: `CompactionProvider` is disqualified.** Across 4 turns with `ContextWindowCompactionStrategy`
it **froze the request**. From turn 2 on, the storage filter saw `question 1` re-emitted (tagged
`ChatHistory/…CompactionProvider`) and never `question 2`. The history ended with `users=[:question 1]` after four turns,
so the model was sent the stale question and the real one was lost. **Replacement, measured:**
`new ChatClientBuilder(client).UseChatReducer(strategy.AsChatReducer())` **under** the agent's
`FunctionInvokingChatClient`. It bounded the model's view at 25,921 chars while stored history grew to 210k. All four
user turns were stored, and **no extra session state** was added (`stateBag=InMemoryChatHistoryProvider,TodoProvider`).
This is not the rejected "reducer on the history provider", which deletes stored history. A chat-client reducer changes
only what one model call sees. It is also not the rejected "compaction in the pipeline" (the stateful provider in the
builder). Consequence: RAG, memory and skills context all sit inside the budget automatically, because the reducer sees
the final request.

**Contradicts the design: the history fold.** `GetHistoryAsync` already projects every text-bearing assistant message
as its own turn. Folding at projection time would therefore need to know iteration boundaries that storage no longer
holds, because the feedback message is filtered out. **Chosen:** fold at **persist time**. The observer records the history
count at each boundary. When the turn ends, text is stripped from assistant messages of every iteration except the
**last one that produced text**, and a message left empty is dropped. The stored session then already has one final text
plus every tool call, and ADR 0080 replay needs no change. Live mirrors this. `reply` restarts, and a new
`AgentStreamEventType.TextReset` event is emitted, when the first text of a later iteration arrives. The rule is
"the answer is the last iteration that spoke", on the live, reload, MCP and follow-up paths alike.

## Steps

1. **Spike the seven open risks** (`/spike-dotnet`, pinned `Microsoft.Agents.AI@1.19.0`, with the reflection-JSON properties).
   Mirror `BuildAgent`'s history provider and filters. Write each answer into this spec under a new **Spike results** heading.
   Adjust Steps 5–7 if an answer contradicts the design. Verify: the spike prints expected vs actual for all seven.
2. **Data model.** Add the four trailing fields listed in Affected areas, plus `ResearchTodo` and the `AgentStreamEvent`
   `[Id(14)]`. Rule 2: defaulted, no collection expressions into `IReadOnlyList<T>` state. Verify: `GrainStorageSerializerTests`
   round-trips every new field on the **Cosmos JSON path**, and documents without them deserialize with the defaults.
3. **Registry: seeding and invariants.**
   - `EnsureResearchAgentAsync(model)`: idempotent, seed-once, same-write flag, no-op on `""`.
   - `SaveAsync` update: **preserve `Kind`** from the stored agent, as `ApiKeyHash` is preserved.
   - `SaveAsync` insert and update: **force `Continuous = false` when Kind is research**.
   - The GET handler resolves the per-user `DefaultModelId` and calls Ensure before GetAll.
   - The response carries `Kind`.

   Verify: `AgentRegistryGrainTests` cover seed-once, no reseed after delete, no seed on `""`, Kind survives PUT, and
   continuous forced off. `AgentApiTests` checks that a **unique principal** (as `ToolSelectionApiTests.cs:21–26` does) sees
   exactly one Research agent with a concrete model on first GET, and still one on the second.
4. **Binding propagation.** Pass `Kind` at all four sites. `BindAgentAsync` stores `AgentKind`, and `GetAgentBindingAsync`
   returns it. Verify: an `AgentGrainTests` test that the kind survives reactivation. `AgentApiTests` (agent-run) and
   `AgentMcpServerTests` assert the kind reaches the grain. *(Parallelizable with Step 3 only in a worktree.)*
5. **Research composition.**
   - Put the `Research/` partial and `ResearchLimits` in place, with scoped MAAI001 pragmas, gated on
     `AgentKind == research && capabilities.SupportsTools`.
   - Add the loop-feedback storage filter (risk 1).
   - `BuildAgent` gains one plain call.
   - Update `ProjectLayoutTests` (folder set + `AgentGrainParts`) and ADR 0039's folder list.

   Verify: `ResearchAgentTests` with a scripted `FakeChatClient` (`todos_add` → search → `todos_complete` over two
   iterations, plus judge verdicts). Test these cases:
   - loops until todos are done and the judge passes
   - stops at `MaxIterations`
   - a non-research agent and a no-tools research model build a plain agent
   - with RAG present, the fake sees a bounded prompt
   - a session crosses between plain and research shapes without error (risk 6)
6. **Turn-path integration.**
   - An in-flight marker plus the history fold, so `GetHistoryAsync` honours the invariant.
   - Final-iteration text for the live stream, `reply`, and `AgentMcpRunner`.
   - A per-turn todo reset.
   - Both reset sites (`ChatCoreAsync` entry and `ReceiveReminder`).
   - The stop-path fix (risk 3), without breaking the 12 existing `StopGenerationTests`.

   Verify:
   - `ResearchAgentTests`: during a turn `GetHistoryAsync` shows no new assistant turn; after it, exactly one with the final
     text and all tool calls; a second question starts with fresh todos; a reminder-fired research turn resets the todos and
     the marker; a max-iteration turn with truncated tool results stays under the session budget
     (`ToolResults_DoNotBlowTheSessionSizeBudget` style).
   - `StopGenerationTests`: a stop in iteration 2 leaves no duplicate user or assistant text, keeps `TurnAttachments`
     aligned, and the chat reopens.
7. **Live checklist.** Emit `Todos` from `StreamModelReplyAsync` after a `ToolCallEnd` whose tool name starts with `todos_`
   (risk 5). Add the frontend pieces listed in Affected areas, and the DevHost `CannedChatClient` research script. Verify: a
   `ResearchAgentTests` case that `Todos` events stream in order; `ProjectLayoutTests` and the source guards pass; a DevHost
   manual check shows the checklist ticking to completion.
8. **Editor UI.** In `agents.js`: the Research badge, and the continuous checkbox disabled with an explanation for
   `kind === "research"`. Verify: an `AgentEditorAssetsTests` case, and a DevHost manual check.
9. **Docs.**
   - `/decide`: the new ADR, with alternatives (HarnessAgent, a generic flag, reducer/pipeline compaction, `Model ""`,
     continuous allowed, seeding `read_web_document`), MAAI001 exposure, the amends list, egress, and the MCP timeout.
   - `/gotcha`: the traps Step 1 confirms (loop feedback storage, the stop path under a loop, compaction state duplication, the
     history invariant, the spike JSON trap). Put a banner on "A cancelled Agent Framework run persists NOTHING".
   - The CLAUDE.md feature-map row.
   - `/sync-docs`.

   Verify: the doc-path integrity check passes.
10. **Gates.** Run `dotnet build Agent42.slnx` (S3776), `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"`,
    `node .agentic/scripts/check-new-code-coverage.mjs`, and `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A user with a chat model sees exactly one "Research" agent (Kind research, concrete model) on first GET; not duplicated on the second | `FirstList_SeedsOneResearchAgent` | `AgentApiTests.cs` |
| 2 | A user without a chat model is not seeded until one exists | `NoModel_DoesNotSeed_ThenSeedsLater` | `AgentRegistryGrainTests.cs` |
| 3 | A deleted research agent is not re-seeded | `DeletedResearchAgent_IsNotReseeded` | `AgentRegistryGrainTests.cs` |
| 4 | PUT keeps `Kind` and forces `Continuous=false` for research | `Update_PreservesKind_AndForcesPerChat` | `AgentRegistryGrainTests.cs` |
| 5 | New fields round-trip on the Cosmos JSON path; old documents deserialize with defaults | `ResearchFields_RoundTrip`, `DocumentsWithoutResearchFields_Deserialize` | `GrainStorageSerializerTests.cs` |
| 6 | Kind is snapshotted from all three bind paths and survives reactivation | `Bind_ResearchKind_Persists` + agent-run/MCP asserts | `AgentGrainTests.cs`, `AgentApiTests.cs`, `AgentMcpServerTests.cs` |
| 7 | A research chat loops until todos are done and the judge passes | `ResearchTurn_LoopsUntilTodosDone` | `ResearchAgentTests.cs` |
| 8 | The loop never exceeds `MaxIterations` | `ResearchTurn_StopsAtIterationCap` | `ResearchAgentTests.cs` |
| 9 | A non-research agent and a no-tools research model are plain agents | `NonResearch_IsPlainAgent`, `NoToolsModel_IsPlainAgent` | `ResearchAgentTests.cs` |
| 10 | A session crosses between the research and plain shapes without error | `Session_CrossesAgentShapes` | `ResearchAgentTests.cs` |
| 11 | During a research turn history gains no assistant turn; after it exactly ONE, with final-iteration text and all tool calls (ADR 0046 parity) | `ResearchTurn_HistoryHasOneAssistantTurn` | `ResearchAgentTests.cs` |
| 12 | Loop feedback never appears as a user turn; `TurnAttachments` stays 1:1 | `LoopFeedback_IsNotAUserTurn` | `ResearchAgentTests.cs` |
| 13 | The live stream, follow-up input and MCP reply carry final-iteration text only | `ResearchTurn_RepliesWithFinalIterationText`, MCP case | `ResearchAgentTests.cs`, `AgentMcpServerTests.cs` |
| 14 | With RAG present, the prompt the model sees stays within the research budget | `ResearchPrompt_IsBoundedWithRag` | `ResearchAgentTests.cs` |
| 15 | Stopping in iteration 2: no duplicated user or assistant text, attachments aligned, reopenable | `Stop_MidResearchLoop_KeepsHistoryConsistent` | `StopGenerationTests.cs` |
| 16 | A second research question (interactive or reminder-fired) starts with fresh todos | `SecondResearchTurn_StartsWithFreshTodos`, `ReminderResearchTurn_ResetsTodos` | `ResearchAgentTests.cs` |
| 17 | A max-iteration research turn stays under the session budget | `ResearchTurn_DoesNotBlowTheSessionSizeBudget` | `ResearchAgentTests.cs` |
| 18 | A research turn streams `Todos` events reflecting add/complete, in order | `ResearchTurn_StreamsTodoChecklist` | `ResearchAgentTests.cs` |
| 19 | The `Research/` folder and partial are pinned; `research.js` is reachable, in `SHELL`, and `CACHE` is bumped; the editor locks continuous for research | `ProjectLayoutTests` (updated) + `AgentEditorAssetsTests` case | `ProjectLayoutTests.cs`, `AgentEditorAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ b78e1ce)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build (error severity) |
| `vulnerable_packages` | 0 | CI package-freshness job (no new packages expected) |
| build / tests / `dotnet format` / doc-path integrity | must pass | CI `pr-checks.yml` |

## Out of scope

- A generic "research mode" flag on user-created agents (the user chose seeded-only; `Kind` leaves that door open).
- The `Microsoft.Agents.AI.Harness` package / `HarnessAgent`, hosted web search, file memory, background (parallel) sub-agents.
- Per-agent loop settings in the editor (iteration cap, judge on/off).
- Replaying the checklist on reload (live-only; ADR 0080 folding covers reload).
- Restoring a deleted research agent; per-model context-window metadata; continuous research agents.
- Making `read_web_document` loop-aware (mid-run retrieval rebuild).

## Review

Critic pass (2026-09-22) returned "not ready" with 18 findings. The plan was revised in place.

**Accepted and folded in:**
- #1 ADR 0046 recovery / assistant-turn parity: the invariant section, risk 2, AC 11/13
- #2 `Model ""`: now a concrete seeded model, no seed without a provider, AC 1/2
- #3 stop-path text duplication + iteration boundary: risks 2–3, AC 15
- #4 `Research/` folder vs `ProjectLayoutTests`/ADR 0039: Affected areas + Step 5
- #5 reminder reset site: design decision + AC 16
- #6 `read_web_document` inside a loop: dropped from the seed
- #7 MCP concatenation/timeout: final-iteration text + ADR note, AC 13
- #8 session portability: risk 6, AC 10
- #9 DevHost canned client: Affected areas + Step 7
- #10 `Todos` wiring specifics: Affected areas + design decision; the "SSE mapping" item was removed
- #11 unique principal in the API test: Step 3
- #12 same-write seed flag: design decision
- #13 Role+AuthorName filter: risk 1
- #14 amends list + gotcha banner: Governing ADRs + Step 9
- #15 memory egress per iteration: design decision
- #16 consent for seeded tools: resolved by not seeding `read_web_document`
- #17 ADR number not pinned: Affected areas
- #18 evidence moved off the memory file; claims re-measured in Step 1

**Rejected:** none.

## Implementation divergence from the plan (executed 2026-09-22, ADR 0082)

- **No `CompactionProvider`.** The spike showed that it froze the request across turns. The prompt is bounded by
  `UseChatReducer(ContextWindowCompactionStrategy.AsChatReducer())` on the conversation's client instead
  (`WithResearchBudget`). Consequence: there is no `messagegroups` state to budget for, and AC14 is proven against a
  history larger than the budget, not a single cap-sized document.
- **The fold happens at persist time, not in the projection.** `FoldResearchTurn` rewrites the stored turn once,
  when the run ends. `GetHistoryAsync` only gained `VisibleHistory` (the in-flight hide). A new
  `AgentStreamEventType.TextReset` (appended after `Todos`) keeps live, MCP and `reply` in step with the fold.
  The plan had no such event. Its "final-iteration text" rule became "the last iteration that spoke".
- **The iteration boundary is an observer evaluator**, and the per-run tracker is a
  `ConditionalWeakTable<ChatMessage, ResearchRun>` keyed by the turn's user message. `AgentRunOptions` leaks into
  the provider request body.
- **`Kind` is `string?` (default `null`)**, not `string = ""`: the Cosmos JSON path binds a missing record
  parameter to `null`. `AgentKinds` got a file of its own rather than living in `ResearchAgentDefaults`.
- **`Todos` is emitted after any tool result that changed the list**, compared by a signature, rather than after a
  `todos_*`-named `ToolCallEnd`. `ToolCallEnd` does not carry the tool name.
- **The in-flight marker is published by identity** (`_researchInFlight ??=`, cleared only if still ours), in
  the `_turnCts` shape rather than as a blindly reset turn-scoped field, and it is cleared in a `finally` at both run
  sites.
- **Extra tests beyond the AC table:** `Insert_CannotClaimAKind`, `ResearchChat_SurvivesReactivation`,
  `Stop_MidResearchAnswer_KeepsTheLaterIterationsTail`, `ChatEndpoint_StreamsResearchEventsAsNdjson`, and
  `ResearchAssetsTests`. The DevHost check was done over HTTP (seed, checklist 0→3, one `textReset`, one assistant
  turn with 5 tool calls), because the browser extension was not connected. The visual checklist and editor lock
  were **not** eyeballed.
- **Critic pass on the diff (2026-09-22).**
  - **Fixed:**
    - A failed or abandoned research turn is now folded in a `finally` at both run sites (new test
      `ResearchTurn_FailingInALaterIteration_StillFolds`, mutation-checked).
    - The stop path decides what to append from the stored history itself, not only the observer's count, which
      closes the gap between an iteration being stored and the observer recording it.
    - Doc debt: banners on ADRs 0003/0016/0046/0078; ADR 0039's partial count and instance-field rule; the
      turn-scoped-field gotcha.
  - **Accepted in ADR 0082:** overlapping runs on one activation (the pre-existing shared-session hazard), and a
    backgrounded research turn outlasting the ~30 s ADR 0046 recovery poll.
