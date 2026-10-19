# Plan: extract services from the AgentGrain god object, with an explicit per-turn context

planned-against: main @ e5096c0

## Overview

`AgentGrain` is one class split across 24 partial files (~7,200 lines; `Conversations/AgentGrain.cs` alone is
1,842). The ADR 0039 split was deliberately the *non-behavioural* step. Every partial still reads and writes the
same instance fields, and the turn-scoped ones (`_ephemeral`, `_interactiveTurn`, `_webDocumentIngested`,
`_pushesThisTurn`, `_documentPartsRead`) are the root of CLAUDE.md cross-cutting rule 6 and four gotchas entries.
They live for the activation, they must be hand-cleared at two or three sites per field, and they are **wrong under
interleaving**: a reminder that fires in a `yield` gap of a streaming turn resets `_pushesThisTurn`,
`_documentPartsRead` and `_ephemeral` for that streaming turn too.

This plan does two things. First, it replaces the turn-scoped fields with an explicit per-run `TurnContext` that is
passed as a parameter on the grain side and read through one async-local on the tool side. Then it extracts four
services behind interfaces, each with an explicit context object: **search projection**, **retention**,
**edit/fork**, and **tool selection/agent composition**. The grain stays the orchestrator and the only owner of
`_state`, `_session` and `_agent`.

ADR 0039's "Alternatives considered" names this as "the real decoupling, still open — but it moves turn-scoped
state across object boundaries, which is a behavioural change". This plan is that step, so it needs a new ADR
(Step 1).

## Affected areas

Verified against the tree at `e5096c0`:

- `src/Agent42.Agents/Conversations/AgentGrain.cs`: the field block, `ChatAsync`, `EnqueuePromptAsync`,
  `ChatCoreAsync`/`ChatTurnAsync`, `StreamModelReplyAsync` (the `PullAsync` call), `PersistTurnUnlessEphemeralAsync`,
  `ReceiveReminder`/`RunReminderAsync`, `SuggestFollowUpsAsync`, `PurgeAsync`, and the persist sites that call
  retention or search.
- `src/Agent42.Agents/ClientTools/AgentGrain.ClientTools.cs`: `CurrentClientToolBroker` (AsyncLocal) and `PullAsync`.
  These become the general turn-context carrier.
- Turn-field readers: `Artifacts/AgentGrain.ArtifactTools.cs` (:83, :126), `Copilot/AgentGrain.Copilot.cs` (:139),
  `Documents/AgentGrain.WebDocumentTool.cs` (:75, :183, :276-281), `Documents/AgentGrain.ReadDocumentTool.cs`
  (:22, :49, :80), `ImageGeneration/AgentGrain.ImageTurn.cs` (:120 plus the persist block :120-150),
  `Memory/AgentGrain.Memory.cs` (:75, the `IsEnabled` lambda), `Push/AgentGrain.PushTool.cs` (:62, :71, :102, :115),
  `Scheduling/AgentGrain.Scheduling.cs` (:37), `SharedState/AgentGrain.SharedState.cs` (:58, :130).
- `ClientTools/AgentGrain.ClientTools.cs:247-248` (`CurrentElicitationSink`, which reads the broker async-local and
  feeds `ElicitingMcpTool` via `BuildAgent.cs:383-385`).
- The away-push gates that read the push count **after** the run: `AgentGrain.cs:583` (`EnqueuePromptAsync`) and
  `:1708` (reminder). They must read the run's own context object, which the tools mutated.
- `src/Agent42.Agents/Copilot/SchedulerBoundAIFunction.cs` and `Copilot/AgentGrain.Copilot.cs`: Copilot tools run
  on the SDK's RPC thread, so an async-local does not flow there (see Context §5).
- Source comments that name the deleted fields; hard rule 2 means they are fixed in the same MR:
  - `src/Agent42.Backend/Mcp/AgentMcpRunner.cs:202-205`
  - `Webhooks/AgentGrain.Webhooks.cs:12-13, 55`
  - `Conversations/AgentGrain.FollowUps.cs:11`
  - `ClientTools/ClientToolBroker.cs:6`
  - `ImageGeneration/AgentGrain.ImageTurn.cs:15`
  - `SharedState/AgentGrain.SharedState.cs:22`
  - `Scheduling/AgentGrain.Scheduling.cs:13`
  - `Memory/AgentGrain.Memory.cs:23-55`
  - the `<see cref="_ephemeral"/>` crefs at `AgentGrain.cs:242/269/288`
  - `tests/Agent42.Tests/AgentMcpServerTests.cs:344`
  - `tests/Agent42.Tests/TestTimeProvider.cs:17`
  - `tests/Agent42.Tests/AgentGrainTests.cs:3286` (it already names a field that no longer exists, `_mcpTools`)
- Search: `src/Agent42.Agents/Search/AgentGrain.Search.cs`. Its fields are also touched in
  `Conversations/AgentGrain.EditFork.cs` (:62, :71, :95) and `Conversations/AgentGrain.Retention.cs` (:133).
- Retention: `src/Agent42.Agents/Conversations/AgentGrain.Retention.cs`, `RetentionLimits.cs` (unchanged).
- Edit/fork: `src/Agent42.Agents/Conversations/AgentGrain.EditFork.cs`.
- Tool assembly: `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs`, plus `Copilot/AgentGrain.Copilot.cs` (:56,
  :107 reuse `SelectedSkills`/`SelectableTools`), `Research/AgentGrain.Research.cs` (:57-59, `_researchTodos` is
  written as a BuildAgent side effect), and `Conversations/AgentGrain.cs:1782-1798` (`McpServersNeedingReauth` reads
  `EffectiveEnabledTools`).
- New files, all `namespace Agent42.Agents;` and in feature folders (never the project root):
  `Conversations/TurnContext.cs`, `Search/IChatSearchProjection.cs` + `Search/ChatSearchProjection.cs`,
  `Conversations/IHistoryRetention.cs` + `Conversations/HistoryRetention.cs`,
  `Conversations/IConversationForking.cs` + `Conversations/ConversationForking.cs`,
  `Tools/IToolSelection.cs` + `Tools/ToolSelection.cs`.
- Tests: `tests/Agent42.Tests/ProjectLayoutTests.cs` (`TurnScopedField_IsDeclaredInTheCorePart` and
  `AgentGrainParts`), `AgentGrainTests.cs`, `ChatSearchIndexingTests.cs`, `EditRegenerateForkTests.cs`,
  `McpElicitationTests.cs:369-372` (source-text pin on the MCP wrap chain), `McpUrlSeedingTests.cs:428` (source-text
  count in `BuildAgent.cs`), and new unit-test files per service.
- Docs: a new ADR `0106`, amendment notes in `0039` and `0051`, `CLAUDE.md` (rule 6, plus the stale "Agent Framework
  1.19" in the Stack line: `Directory.Packages.props` pins 1.23.0), `.agentic/gotchas.md` (the turn-scoped entries
  at "A grain's turn-scoped field lives for the ACTIVATION" and "Ephemeral chat: `_ephemeral`…", the search entries
  that name `_ephemeralOrdinals`/`_indexedTurnCount`, and the exemplar table).

## Context & constraints

**Investigation findings (each with evidence):**

1. **A per-run async-local already exists and was measured.** `CurrentClientToolBroker`
   (`ClientTools/AgentGrain.ClientTools.cs:38`) is set in `PullAsync` (:153-158) around each `MoveNextAsync` of
   the streaming run. Only that run's tool invocations see it (spike 0a of
   `.agentic/specs/completed/plan-client-tools-and-shared-state.md`). The turn context generalises exactly this
   shape. It is **not** a new mechanism.
2. **Two runs can be in flight on one activation.** `ChatAsync` suspends between `yield return`s, and a reminder or
   an `EnqueuePromptAsync` run executes in the gap (`_turnCts` remarks, `AgentGrain.cs:232-256`). That is why the
   context must be per run and never a field. Today's fields are already wrong in that case (Overview). Fixing it is
   an intended behaviour change, and the ADR records it.
3. **The reminder path does not stream.** `RunReminderAsync` calls `_agent.RunAsync` directly
   (`AgentGrain.cs:1654`). Setting the async-local before that call flows into the tools through the normal
   `ExecutionContext`. A fresh grain call starts with no value, so a reminder never inherits a chat's context.
4. **Context providers read `_ephemeral` per model call**: the memory `IsEnabled` lambda (`Memory/AgentGrain.Memory.cs:75`)
   and the shared-state provider (`SharedState/AgentGrain.SharedState.cs:130`). They run inside the run, so they
   read the async-local too. **The spike must confirm this on AF 1.23** (they are invoked by the agent, not the FICC).
5. **Copilot tools do not run in the turn's ExecutionContext.** `SchedulerBoundAIFunction` exists because the
   Copilot SDK invokes tools from its RPC thread (measured, file header), and the functions are bound once per
   `BuildAgent` (`CopilotFunctions`, Copilot.cs:~107-129; `ICopilotAgentFactory.CreateAgent` takes the tool list at
   build time, so there is no per-run tool binding). Assume an async-local set by the grain is **null** there.
   Which tools are affected is narrow: Copilot never gets the client tools, `update_shared_state`, `read_document`
   or `read_web_document` (`IsExcludedFromCopilot` and the `CopilotFunctions` filter). So the Copilot readers are
   `schedule_prompt`, the artifact tools and `send_push_notification`. `_ephemeral` is structurally false in a
   Copilot run, because incognito Copilot turns are refused before they start (`CopilotTurnRefusal`,
   Copilot.cs:139). Only the push budget is a live per-run value there. **Design (critic H1):** the grain keeps a
   by-identity registry of in-flight Copilot runs (added and removed like `_clientToolBrokers`, never
   first-wins like `_turnCts ??=`). `SchedulerBoundAIFunction` resolves the context **only when exactly one Copilot
   run is in flight**. With zero or more than one it uses the closed context, and the tool refuses with a clear
   message. Two concurrent Copilot runs on one activation (a reminder firing in a gap of a streamed Copilot turn)
   is rare, and refusing a push or schedule there is strictly safer than today, where both runs share one counter.
   Record it as a residual in ADR 0106. If Step 0(e) shows the SDK *does* flow the ExecutionContext of the run, the
   registry is unnecessary and is dropped.
6. **BuildAgent is not side-effect free.** It reassigns `_researchTodos` (Research.cs:59), `_copilotChat` and
   `_copilotUnavailable` (Copilot.cs `IsCopilotComposition`), and 9 call sites reassign `_agent`/`_session` after it
   (scout table: OnActivate, BindAgent, SwitchModel, RebuildAgentPreservingSession, RebindModel, ReResolveMcp,
   RegisterDocument, SetEnabledTools, BindForkedSession).
7. **Search projection touches no `_state`/`_session`/`_ephemeral`.** It owns `_indexedTurnCount`,
   `_ephemeralOrdinals` and `_searchReconcilePending` (Search.cs:36, 54, 82; all activation-scoped by design,
   ADR 0061/0068), and reads `GetHistoryAsync()`, `_userId` and the conversation id. Outside Search.cs its fields
   are touched only by EditFork (:62, :71, :95) and Retention (:133). That makes it the cleanest seam.
8. **Retention never writes state or calls a grain.** Callers persist (AgentGrain.cs ~1256/~1667, ImageTurn.cs:125,
   EditFork.cs:280). `UserTurnIndices`, `ExpiredLeadingTurnCount` and `TurnIsExpired` are already pure.
   `RemoveLeadingTurns` mutates the session list, `TurnAttachments` and `GeneratedImages`.
9. **Grain-dependency validation (ADR 0105)** reflects only *direct* grain ctor parameters
   (`src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs:67-82`). Every leaf test silo calls `AddAgent42Silo()`.

**Design decisions taken at plan time (the user may redirect them at approval):**

- **The services are constructed by the grain per activation, not DI-registered.** Three of them hold
  activation-scoped state (search cursor and ordinals; tool selection over per-user MCP/skills resolved in
  `OnActivateAsync`), so a singleton is wrong, and a DI-registered factory adds a port to every host and test silo
  (ADR 0105) for no gain. The **interfaces** exist for unit-testing the service and for substituting a fake in a
  grain test. The grain ctor signature does **not** change, and no host or test silo changes. *Rejected:* injecting
  `IXxxFactory` ports (churn in 3 hosts and ~8 test silos, and nothing varies per host).
- **`TurnContext` is a sealed reference type**, so tool-side mutations (push count, part count, ingested flag) are
  visible to the run that owns it. It carries `Ephemeral`, `Interactive`, `Broker` (it absorbs
  `CurrentClientToolBroker`), `PushesSent`, `DocumentPartsRead` and `WebDocumentIngested`. One is created per run at
  each run entry: `ChatAsync` (interactive), `EnqueuePromptAsync` (background), `ReceiveReminder` (background, never
  ephemeral). It is passed **explicitly** as a parameter down the grain-side call chain (`ChatCoreAsync` →
  `ChatTurnAsync` → `StreamModelReplyAsync` / `PersistTurnUnlessEphemeralAsync` / `GenerateImageTurnAsync` /
  `RebuildForIngestedWebDocumentAsync` / `SuggestFollowUpsAsync`). Tools and context providers, which cannot take a
  parameter, read `TurnContext.CurrentOrClosed` (over one private `AsyncLocal`, set by `PullAsync`, `DisposeRunAsync` and around `RunAsync`).
- **A missing context fails closed, through ONE accessor.** Readers never touch the async-local directly. They call
  `TurnContext.CurrentOrClosed`, which returns the run's context, or a shared immutable `TurnContext.Closed`:
  `Ephemeral = true`, budgets exhausted, no broker, and mutations ignored. A tool called outside a run is a bug, and
  the safe direction is refusal, not a silent persist or push. A source-text guard (AC4b) pins that no file but
  `TurnContext.cs` reads the raw async-local, so the fail-closed default cannot be bypassed one reader at a time.
- **The context is set around EVERY enumerator interaction, not only `MoveNextAsync`** (critic H2). The run's
  `DisposeAsync` (`AgentGrain.cs:966-967`, `SettlePendingPullAsync`) also executes provider and framework code
  (end-of-run hooks; Hindsight retain is external code whose call pattern we do not control). So `PullAsync` gains a
  sibling, `DisposeRunAsync(updates, ctx)`, that sets `Current` for the dispose. Otherwise the memory `IsEnabled`
  lambda would read Closed (memory OFF) at retain time and silently drop retention. Spike item (e) measures where
  Hindsight's `IsEnabled` is actually called.
- **The clear ritual is deleted, not moved.** With no fields there is nothing to clear. `_turnCts`,
  `_researchInFlight`, `_clientToolBrokers` and `_mcpReauthNotified` are **not** turn-valued (they name running turns
  or are activation-scoped by design). They stay exactly as they are and are out of scope.
- **The grain keeps the orchestration and every persist site.** The services compute and mutate the in-memory
  collections they are handed. `WriteStateAsync`, `SerializeSessionAsync`, `GrainFactory` and the order of the
  persist pipeline (compact → retention → serialize → write → reconcile → index, gotchas "five post-turn persist
  sites") stay in the grain. That keeps the hot-path ordering readable in one place and keeps the services
  Orleans-free.
- **`ToolSelection` is stateless** (critic M2). It takes a `ToolSelectionContext` argument on every call and caches
  nothing, so it cannot go stale. `EnabledTools` is mutated at activation (`AgentGrain.cs:363/373/380`), bind
  (`:436`), MCP re-resolve (`:1770`), `SetEnabledToolsAsync` (`BuildAgent.cs:411`) **and fork**
  (`EditFork.cs:154`). A cached selection would need a rebuild at each of those sites, and a missed one leaves a tool
  or memory ON after the user turned it off (rule 11, ADR 0021, the egress reasoning at `Memory.cs:32-37`). The grain
  builds the context from its fields at each use; it is cheap.
- **Tool assembly is extracted as *selection*, not as tool binding.** The tool *functions* are grain methods that
  read `_state` (schedule_prompt registers reminders, web tools write `FetchableUrls`, …). Moving them would move
  `_state` ownership, so they stay. What moves is the pure resolution: effective enabled set (override ?? default ??
  all, ADR 0021), `mcp`/`skills` wildcard expansion, merged skill catalog, capability gate, and which keys and skills
  are selected. `BuildAgent` additionally returns an `AgentComposition` record (`Agent`, `ResearchTodos`,
  `CopilotChat`, `CopilotUnavailable`) that **one** helper assigns, instead of three fields written as side effects
  from inside the build.
- **One branch, one MR, one commit per step.** Every step leaves the build and the full suite green, so the executor
  may stop after any step and still ship a coherent subset.

**Governing ADRs and gotchas to read before each step:** ADR 0039 (§2 flat namespace, §3 partial split, the
Alternatives paragraph this plan picks up), 0051 (`yield` must stay in `ChatCoreAsync`; don't split it into a
non-iterator pipeline), 0050 (S3776 at 15), 0055 (one TestCluster per class), 0105 (direct-ctor validation), 0034
(ephemeral), 0061/0068 (search ordinals, reconcile), 0066 (web document rebuild consume sites), 0075 (push budget),
0089 (client-tool broker per run), 0092 (edit/fork), 0098 (read_document budget), 0021/0048/0049 (tool selection).
In gotchas: the turn-scoped family entries (~:532-623), the chat-search entries (~:3307-3560), "Truncating history
removes WHOLE session turns" (~:4252), and the five-persist-sites entry (~:4027).

**Overlap with another open spec:** `.agentic/specs/plan-deferred-tool-loading.md` (unexecuted) also edits
`BuildAgent.cs`. Whichever lands second rebases. Step 6 here leaves the client-wrapping call site in `BuildAgent`
untouched so the deferred-loading helper still has one call to slot into.

## Steps

### Step 0: Spike — the turn context reaches every reader on AF 1.23 (blocks Steps 2+)

Use `/spike-dotnet` (a file-based app outside the repo, pinned to `Directory.Packages.props` versions:
Microsoft.Agents.AI 1.23.0 and the matching M.E.AI). Measure, with a fake `IChatClient` that emits a tool call:

- (a) An `AsyncLocal<TurnContext>` set around each `MoveNextAsync` (the `PullAsync` shape) is visible inside an
  `AIFunction` invoked by AF's FICC on `RunStreamingAsync`, **and** inside an `AIContextProvider.InvokingAsync` and
  a `ChatHistoryProvider` filter. Mutations the tool makes are visible to the caller afterwards.
- (b) The same holds when it is set once before `RunAsync` (the reminder shape).
- (c) Two interleaved runs on one thread (pull run A, then run B fully, then continue A) each see only their own
  context.
- (d) Through a `LoopAgent` (the research composition): the context is visible in the inner agent's tool calls.

- (e) The context is still set when end-of-run code runs from the enumerator's `DisposeAsync` with the
  `DisposeRunAsync` wrapper, and it is null there without the wrapper. Note which `AIContextProvider` hooks fire on
  that path in 1.23. Read the Hindsight package's provider source (`Hindsight.AgentFramework.Net`, the version in
  `Directory.Packages.props`) for where it calls `IsEnabled`, because retain is the case that must not see Closed.
- (f) A foreign-thread invocation started under `ExecutionContext.SuppressFlow()` (the honest stand-in for the
  Copilot SDK's RPC thread, critic H1; `Task.Run` alone flows the context and would prove nothing) sees null. The
  Copilot run registry (Context §5) then resolves it when exactly one run is registered, and returns Closed for
  zero or two.
- (g) Optional, only if the Copilot CLI is available locally: whether the real SDK flows the run's ExecutionContext
  into a custom tool. If it does, drop the registry (Context §5).

**Verify:** the spike prints each of a–f (and g if run) as PASS/FAIL. Record the results in the spec's
Implementation notes and in ADR 0106. A FAIL in a–f stops the plan for re-design, because Steps 2a-2b rest on it.

### Step 1: Decision — ADR 0106 (after Step 0)

`/decide`: "AgentGrain delegates to per-activation services; per-run state is an explicit TurnContext, never a
field".
- Frontmatter: `extends: [0039]`, `amends: [0034, 0039, 0066, 0075, 0089, 0098]`. Those are the ADRs whose stated
  mechanism changes (critic M4): 0034's clear ritual, 0066's consume-site clears, 0075's two reset sites, 0089's
  broker async-local (now generalised), 0098's part-budget reset, and 0039 §3 ("all instance fields stay in the core
  part"). 0051 is not touched, because no `yield` placement changes.
- Body: record
  - the interleaving bug the fields had (as narrowed by AC2/AC3's outcome, see Step 2b);
  - fail-closed through one accessor;
  - the dispose-path wrapper;
  - why the services are not DI-registered;
  - why `ToolSelection` is stateless;
  - why tool binding stays in the grain;
  - the Copilot registry and its exactly-one residual, from Step 0(e-g).
- Add a one-line "Amended by 0106" note to each amended ADR. Add 0106 to the CLAUDE.md feature map ("Layout and
  quality" row).

**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~AdrNumberingTests|FullyQualifiedName~DocPathChecker"`.

The turn-context migration is split into two commits (critic M5). Each leaves the suite green. Both use this
widened filter:

```
dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~ClientTool|FullyQualifiedName~PushTool|FullyQualifiedName~WebDocumentTool|FullyQualifiedName~ReadDocument|FullyQualifiedName~SharedState|FullyQualifiedName~Copilot|FullyQualifiedName~ProjectLayout|FullyQualifiedName~Ephemeral|FullyQualifiedName~Memory|FullyQualifiedName~Artifact|FullyQualifiedName~Schedul|FullyQualifiedName~ImageGeneration|FullyQualifiedName~Webhook|FullyQualifiedName~FollowUp|FullyQualifiedName~Research|FullyQualifiedName~McpElicitation|FullyQualifiedName~AgentMcpServer|FullyQualifiedName~TurnContext"
```

Run `dotnet build Agent42.slnx` first (S3776), and the full suite after.

### Step 2a: `TurnContext` with `Ephemeral`, `Interactive` and `Broker` (after Step 1)

Files: new `Conversations/TurnContext.cs`. Edit `Conversations/AgentGrain.cs`, `ClientTools/AgentGrain.ClientTools.cs`
(including `CurrentElicitationSink` :247-248), every `_ephemeral`/`_interactiveTurn` reader in Affected areas,
`Copilot/SchedulerBoundAIFunction.cs` and `Copilot/AgentGrain.Copilot.cs` (the run registry, if Step 0(g) did not
remove the need).

- Define `TurnContext` (sealed class) with `Ephemeral`, `Interactive` and `Broker`, plus the `Closed` instance and the
  `CurrentOrClosed` accessor over one private `AsyncLocal`. `CurrentClientToolBroker` is deleted. `PullAsync` and the
  new `DisposeRunAsync` set the async-local. Set it inside an async method frame around the reminder's `RunAsync`, the
  same way `PullAsync` does, so the value does not leak to the caller.
- Create one per run at the three entries. Thread it explicitly down the grain-side chain.
- Delete `_ephemeral` and `_interactiveTurn` and their reset lines.
- Update every comment in Affected areas that names the deleted fields.
- `ProjectLayoutTests.TurnScopedField_IsDeclaredInTheCorePart` is replaced by `TurnScopedState_IsNeverAGrainField`
  (AC1), plus `TurnContext_IsReadOnlyThroughTheFailClosedAccessor` (AC4b).
- Tests: `TurnContextTests` (AC4a), and `CopilotTool_SeesItsRunsTurnContext` (AC5). AC5 invokes the bound tool
  under `ExecutionContext.SuppressFlow()`, the way spike (f) does, because a plain `Task.Run` would pass vacuously. Add
  a second case for two in-flight runs, which refuses.

### Step 2b: `PushesSent`, `DocumentPartsRead` and `WebDocumentIngested` move into `TurnContext` (after Step 2a)

- Delete `_pushesThisTurn`, `_documentPartsRead` and `_webDocumentIngested`, and the remaining reset blocks. That
  includes all of `ReceiveReminder`'s block. Keep `ReceiveReminder` in the core part, but remove the comment that says
  the clears are why.
- The away-push gates (`AgentGrain.cs:583`, `:1708`) and the two `RebuildForIngestedWebDocumentAsync` consume sites
  read the **run's own** context object after the run.
- Tests: AC2 and AC3, written as follows (critic M6).
  - Hold the streaming turn on a client-tool wait (`FakeChatClient.CallToolPrompt(ClientToolLimits.ConfirmAction)`),
    as `EditRegenerateForkTests.Truncate_DuringAChatRun_IsRefused` does.
  - Fire the reminder (`ReceiveReminder`) and **first assert that it completed** (`TimesFired` incremented) before
    submitting the confirm result.
  - Then finish the turn and assert the streaming turn's push budget, or its incognito no-persist.
  - If the reminder cannot complete while the turn is held, the "interleaving bug" does not exist in that shape. In
    that case:
    1. Try the same with `EnqueuePromptAsync`.
    2. If neither interleaves, rewrite AC2 and AC3 as plain regression tests (the budget and incognito behave as
       before).
    3. Narrow ADR 0106's motivation to "structure, not a live bug", and record that in Implementation notes.
- The existing behavioural contract (AC6) must pass unchanged.

### Step 3: Search projection service (after Step 2b)

Files: new `Search/IChatSearchProjection.cs` and `Search/ChatSearchProjection.cs`. Edit `Search/AgentGrain.Search.cs`
(it shrinks to a thin delegating partial, or is deleted with `ReindexAsync` and `DeleteSearchIndexForConversationAsync`
moving to the core part, at the executor's call; keep the `ProjectLayoutTests.AgentGrainParts` list in sync either
way), `Conversations/AgentGrain.EditFork.cs` (:62, :71, :95), `Conversations/AgentGrain.Retention.cs` (:133).

- `ChatSearchProjection(IChatSearchIndex index, ILogger logger, string userId, string conversationId)` owns the three
  fields. Operations take a `SearchProjectionContext` (the projected `IReadOnlyList<ChatTurn>` from **one**
  `GetHistoryAsync()` call, per the ordinals gotcha) and are `IndexNewTurnsAsync`, `SkipEphemeralTurnAsync`,
  `RemapAfterPrune(int droppedProjectedTurns)`, `ReconcileAfterPruneAsync(bool pruned)`, `ReindexAsync`,
  `DeleteConversationAsync`, `TruncateTo(int projectedCount, bool ephemeral)` (absorbs EditFork :62/:71), and
  `HasEphemeralTurns` (EditFork :95). `ProjectForSearch` moves as a static.
- The grain constructs it in `OnActivateAsync`, after `_userId` is known.
- Tests: new `ChatSearchProjectionTests` (plain xUnit over `TestChatSearchIndex`, no silo). It ports the logic cases
  that today only run through the grain: remap after prune, ephemeral skip then resume, a failed reconcile retried on
  the next turn, an ephemeral turn taken between a failed reconcile and its retry, and truncate. Every
  `ChatSearchIndexingTests`, `SearchEndpointTests` and `EditRegenerateForkTests` case passes unchanged.

**Verify:** `dotnet test Agent42.slnx --filter
"FullyQualifiedName~ChatSearch|FullyQualifiedName~SearchEndpoint|FullyQualifiedName~EditRegenerateFork"`.

### Step 4: Retention service (after Step 3, because it calls `RemapAfterPrune`, which Step 3 creates; not parallelizable)

Files: new `Conversations/IHistoryRetention.cs` and `Conversations/HistoryRetention.cs`. Edit
`Conversations/AgentGrain.Retention.cs` (shrinks to the gate `IsRetentionWindowed` and one delegating
`ApplyRetentionAsync`).

- `HistoryRetention(TimeProvider clock, ILogger logger)` takes a `RetentionContext`: the session `List<ChatMessage>`,
  `TurnAttachments`, `GeneratedImages`, `windowed`, and a `Func<int>` projected-turn counter, so that the "delta is a
  PROJECTED-turn count" gotcha stays true. It returns `RetentionResult(bool Pruned, int DroppedProjectedTurns)`.
  Stamping, the expiry scan, `RemoveLeadingTurns` and `UserTurnIndices` move. `UserTurnIndices` becomes
  `internal static` on the service because EditFork uses it.
- The grain calls `RemapAfterPrune(result.DroppedProjectedTurns)` on the search projection, which replaces the
  hidden cross-partial call at Retention.cs:133.
- Tests: new `HistoryRetentionTests` (no silo, controllable `TimeProvider`). It ports the prune rules: window,
  minimum-preserved turn, leading non-user prefix, never orphaning a tool result, attachment realignment, a short
  attachment list, generated-image drop, and implausible timestamps. All 15 grain-level retention tests in
  `AgentGrainTests` (:3912-4258) and the 5 `ChatSearchIndexingTests.Prune_*` pass unchanged.

**Verify:** `dotnet test Agent42.slnx --filter
"FullyQualifiedName~HistoryRetention|FullyQualifiedName~Prune|FullyQualifiedName~ContinuousChat|FullyQualifiedName~ChatSearchIndexing"`.

### Step 5: Edit/fork service (after Steps 3 and 4)

Files: new `Conversations/IConversationForking.cs` and `Conversations/ConversationForking.cs`. Edit
`Conversations/AgentGrain.EditFork.cs`.

- The moves are `RemoveLastTurn` (over an `EditContext`: messages, `TurnAttachments`, `GeneratedImages`, `Artifacts`),
  `TokenMatches`/`HistoryTextHash`, `ArtifactVersionsCreatedIn`, `RemoveArtifactVersion`, `ArtifactsWithout`,
  `KeptGeneratedImages`, and the blob/artifact copy helpers (`CopyForkBlobAsync`, `CopyForkArtifactsAsync`,
  `TryCopyArtifactVersionAsync`, over `IAttachmentStore`).
- These stay in the grain: `HistoryEditRefusal` (reads `_runsInFlight`), `HasModelAsync` (writes
  `_chatClientFactory`), `PersistTruncationAsync` (the persist pipeline), `BindForkedSessionAsync` (`_agent`,
  `_session`), and the three public `IAgentGrain` methods as orchestrators.
- Tests: new `ConversationForkingTests` (no silo) covers token staleness, removing only the removed turn's artifact
  versions and image, the creating turn's artifact removal, dangling artifact versions, and blob copy under the fork
  key over `InMemoryAttachmentStore`. All `EditRegenerateForkTests`, `ConversationEditForkApiTests` and
  `AgentWebhookTurnTests` edit/fork cases pass unchanged.

**Verify:** `dotnet test Agent42.slnx --filter
"FullyQualifiedName~Fork|FullyQualifiedName~EditRegenerate|FullyQualifiedName~Truncate"`.

### Step 6: Tool selection service and an explicit agent composition (after Step 2b; independent of Steps 3-5, so it can run in a worktree in parallel with them)

Files: new `Tools/IToolSelection.cs` and `Tools/ToolSelection.cs`. Edit `Tools/AgentGrain.BuildAgent.cs`,
`Copilot/AgentGrain.Copilot.cs`, `Research/AgentGrain.Research.cs`, `Conversations/AgentGrain.cs`
(`McpServersNeedingReauth`, and the 9 `_agent = BuildAgent(...)` sites).

- `ToolSelection` is **stateless** (Context, "ToolSelection is stateless"). Each call takes a `ToolSelectionContext`
  (`_state.State.EnabledTools`, `_userDefaultEnabledTools`, `_mcpServers`, `_userSkills`, capabilities) that the
  grain builds fresh from its fields at the call. Its operations are `EffectiveEnabledTools`, `ExpandedEnabledTools`
  (the `GetEnabledToolsAsync` answer), `MergedSkills`, `SelectedSkills`, `IsSelected(key)` and `SelectedMcpTools`.
  No caching, so no rebuild site can be missed. That includes fork (`EditFork.cs:154`).
- `SelectableTools` stays in the grain and asks the selection which keys are on. The tool `AIFunction`s still bind
  grain methods.
- **Keep the MCP wrap chain string verbatim** in `BuildAgent.cs`, so that `McpElicitationTests:369-372` and the
  `McpUrlSeedingTests:428` text count still hold. If that proves impossible, move the pins together with the code and
  say so in the MR.
- `BuildAgent` returns `AgentComposition(Agent, ResearchTodos, CopilotChat, CopilotUnavailable)`. A single
  `ApplyComposition(...)` assigns the four fields, so `AddResearchProviders` and `IsCopilotComposition` no longer write
  fields from inside the build.
- Tests: new `ToolSelectionTests` (no silo) covers null == all (ADR 0021), override beats default beats all, `mcp` and
  `skills` wildcard expansion, an unknown tool list that keeps the server key intact, a no-tools capability gate that
  selects nothing but memory, upload-wins-on-name-clash, and a newly added key landing OFF for a saved list (rule 11).
  All `AgentGrainTests` NoToolModel_*/McpTools_*/UserSkills_*/BindAgent_*/LegacyState_*/GetEnabledTools_* cases, plus
  `Copilot*`, `Research*` and `Mcp*`, pass unchanged.

**Verify:** `dotnet test Agent42.slnx --filter
"FullyQualifiedName~ToolSelection|FullyQualifiedName~AgentGrainTests|FullyQualifiedName~Mcp|FullyQualifiedName~Copilot|FullyQualifiedName~Research|FullyQualifiedName~Skill"`.

### Step 7: Docs and guards (after Steps 2a-6)

- CLAUDE.md rule 6 is rewritten: "Per-run state is a `TurnContext` (ADR 0106), never a grain field. Pass it on
  the grain side, and read `TurnContext.CurrentOrClosed` in tools; a missing context fails closed. `_turnCts`-style handles
  that name a running turn are published by identity." Fix the Stack line (Agent Framework 1.23).
- gotchas.md: rewrite the two turn-scoped entries into one short entry about the async-local's reach (pull-scoped;
  Copilot needs the re-establish; fail-closed) and re-verify the date. Update the search entries' evidence to
  `ChatSearchProjection.cs`. Add the exemplar row "Grain-delegated service (per-activation, explicit context,
  interface for tests)" → `Search/ChatSearchProjection.cs`. Re-date the AgentGrain exemplar rows.
- Re-read every ADR, gotcha **and source/test comment** that names a moved method or a deleted field, and fix the
  paths. Grep `src/` and `tests/` too, not only `.agentic/`, for each of: the five field names,
  `CurrentClientToolBroker`, every method moved in Steps 3-6, `_indexedTurnCount`, `_ephemeralOrdinals` and
  `_searchReconcilePending`.
- `/sync-docs` sweep. Move this spec to `specs/completed/`.

**Verify:** `dotnet test Agent42.slnx` (full, including doc-path integrity and the ADR guard), `dotnet format
Agent42.slnx --verify-no-changes`, and `node .agentic/scripts/check-new-code-coverage.mjs` after a coverage run.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | No turn-scoped value is a grain field | `TurnScopedState_IsNeverAGrainField` | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 2 | A reminder firing mid-stream leaves the streaming turn's push budget intact | `ReminderFiringMidStream_DoesNotResetTheStreamingTurnsPushBudget` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| 3 | A reminder firing mid-stream does not make an incognito turn persist | `ReminderFiringMidStream_DoesNotUnsetTheStreamingTurnsIncognito` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| 4a | With no run, the accessor yields the closed context (ephemeral, budgets exhausted, no broker, mutations ignored) | `CurrentOrClosed_OutsideARun_IsClosed`, `Closed_IgnoresMutations` | `tests/Agent42.Tests/TurnContextTests.cs` |
| 4b | No reader can bypass the fail-closed accessor | `TurnContext_IsReadOnlyThroughTheFailClosedAccessor` (source guard: the raw async-local is referenced only in `TurnContext.cs`) | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 4c | Memory retain still runs at end of run (the dispose path sees the run's context, not Closed) | existing Hindsight retain tests in `AgentGrainTests` (`*Memory*`/`*Retain*`), plus `Retain_RunsWithTheRunsContext_OnTheDisposePath` if none asserts retain after a streamed turn | `tests/Agent42.Tests/AgentGrainTests.cs` |
| 5 | A Copilot tool on a foreign thread (flow suppressed) sees its own run's context when exactly one run is in flight, and refuses when two are | `CopilotTool_SeesItsRunsTurnContext`, `CopilotTool_TwoRunsInFlight_Refuses` | `tests/Agent42.Tests/CopilotAgentTests.cs` |
| 6 | Existing incognito, push, read_document, web-document and client-tool behaviour is unchanged | existing `EphemeralChat_*`, `EphemeralFlag_IsClearedAfterTheTurn_SoTheNextNormalTurnPersists`, `EphemeralChat_SchedulingIsRefused_NoReminderRegistered`, `PushTool_*`, `ReadWebDocument_RefusedOnEphemeralTurn` and the rest of `WebDocumentToolTests`, the `ReadDocument*` budget tests, `ClientToolTests`, `McpElicitationTests`, `McpReauthTests.ABackgroundRun_DoesNotConsumeTheNotice`, `AgentMcpServerTests` (follow-ups off on the drained path) | existing files |
| 7 | Search projection logic is unit-testable without a silo, with identical results | `ChatSearchProjectionTests` (remap, skip/resume, reconcile retry, truncate) and the unchanged `ChatSearchIndexingTests` | `tests/Agent42.Tests/ChatSearchProjectionTests.cs` |
| 8 | Retention rules are unit-testable without a silo, with identical results | `HistoryRetentionTests` and the unchanged retention cases in `AgentGrainTests` | `tests/Agent42.Tests/HistoryRetentionTests.cs` |
| 9 | Edit/fork helpers are unit-testable without a silo, with identical results | `ConversationForkingTests` and the unchanged `EditRegenerateForkTests`/`ConversationEditForkApiTests` | `tests/Agent42.Tests/ConversationForkingTests.cs` |
| 10 | Tool selection resolves as before (null == all, wildcards, capability gate, rule 11) | `ToolSelectionTests` and the unchanged `AgentGrainTests` selection cases | `tests/Agent42.Tests/ToolSelectionTests.cs` |
| 11 | BuildAgent writes no fields as a side effect | `BuildAgent_ReturnsItsCompositionInsteadOfWritingFields` (source-text guard: no `_researchTodos =`/`_copilotChat =`/`_copilotUnavailable =` outside `ApplyComposition`) | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 12 | New files stay in the flat namespace and out of the project root | existing `EveryFileStaysInTheFlatNamespace`, `OnlyTheAllowedFilesSitInTheProjectRoot` | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 13 | The grain ctor and the host registrations are unchanged | existing `SiloRegistrationTests` and `Hosts_CallAddAgent42Silo` pass with no edit | existing files |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| Branch | never commit to `main` | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

Plus `dotnet format Agent42.slnx --verify-no-changes` and the full `dotnet test Agent42.slnx`. No new packages are
expected. If one is needed, resolve it with `dotnet add package` (hard rule 4).

## Out of scope

- Moving tool **functions** (schedule, web, push, artifacts, documents, …) out of the grain. They own `_state` writes;
  that would be a separate plan.
- Changing `_turnCts`, `_researchInFlight`, `_clientToolBrokers`, `_mcpReauthNotified`, `_runsInFlight` or
  `_sharedStateDirty`. They are identity-published or activation-scoped by design.
- Splitting `ChatCoreAsync`/`ChatTurnAsync` into a non-iterator pipeline (ADR 0051 rejected this).
- Any change to persisted `AgentGrainState`, grain interfaces (`IAgentGrain`), endpoints or the frontend. No
  `sw.js` bump is needed, because no `wwwroot/js` changes.
- Research, attachments, image-generation, shared-state, MCP-reauth and Copilot composition beyond the turn-context
  and `AgentComposition` edits above.
- Deferred tool loading (its own spec).

## Review

Critic pass on 2026-10-19 (`.claude/agents/critic.md`). Verdict on the first draft: do not approve as written.
Every finding was handled in this revision.

| Finding | Severity | Handling |
|---|---|---|
| H1: Copilot context unsound under interleaving; the first-wins slot leaks; the planned test passes vacuously | HIGH | **Fixed.** Context §5 now uses a by-identity registry that resolves only when exactly one run is in flight and is Closed otherwise. Spike (f)/(g) added. AC5 runs under `SuppressFlow` and has a two-runs case. |
| H2: fail-closed not verified per reader; memory silently off on the dispose path | HIGH | **Fixed.** There is one `CurrentOrClosed` accessor with a source guard (AC4b), a `DisposeRunAsync` wrapper, spike (e), and AC4c for retain. |
| M1: Step 3 and Step 4 are not parallel | MEDIUM | **Fixed.** Step 4 runs after Step 3. Step 6 is the one independent step. |
| M2: ToolSelection rebuild triggers under-listed (the fork site) | MEDIUM | **Fixed.** `ToolSelection` is stateless and takes its context per call, so there are no rebuild sites. |
| M3: Affected-areas gaps (`CurrentElicitationSink`, away-push gates, stale comments) | MEDIUM | **Fixed.** They are listed under Affected areas, and Step 7 greps `src/` and `tests/`. |
| M4: ADR set too narrow; `amends:` frontmatter | MEDIUM | **Fixed.** `amends: [0034, 0039, 0066, 0075, 0089, 0098]`, and 0051 is dropped. |
| M5: Step 2 too large; thin verify filter | MEDIUM | **Fixed.** Split into 2a and 2b, with a widened filter. |
| M6: AC2/AC3 test design under-specified; the interleave is unproven | MEDIUM | **Fixed.** The tests use a client-tool hold, assert the reminder completed first, and have an explicit fallback if no interleave exists. |
| L: name the AC6 tests | LOW | **Fixed.** They are enumerated. |
| L: `RunAsync` line cite (~1645 vs 1654) | LOW | Accepted as cosmetic; the executor re-reads the code. |
| L: "no ADR-numbering test exists" | LOW | **Rejected.** `tests/Agent42.Tests/AdrNumberingTests.cs` exists, and Step 1 now names it. |

## Implementation notes (executed 2026-10-19, branch `feature/agentgrain-service-extraction`)

**Step 0 spike (AF 1.23.0, fake `IChatClient`, file-based app):** a–f all PASS, g not run (it needs the real
Copilot CLI, so the registry stays).
- (a) An async-local set around each `MoveNextAsync` reaches the FICC tool, both `AIContextProvider` hooks and the
  history storage filter, and tool mutations are visible to the caller.
- (b) The reminder shape (set once in an async frame before `RunAsync`) works and does not leak.
- (c) Interleaved runs each see only their own context.
- (d) It reaches the inner tool of a `LoopAgent`.
- (f) Code on a thread started under `SuppressFlow` sees null.
- (e) **One unexpected finding:** `StoreAIContextAsync` (Hindsight retain) runs inside the **final `MoveNextAsync`**,
  not in `DisposeAsync`, and an early dispose runs no provider hook. `DisposeRunAsync` was kept anyway, as a
  defensive measure. Hindsight 0.1.0 calls `IsEnabled` in both hooks.

**AC2/AC3: the interleave is real.** The plan's fallback in Step 2b was not needed. A streaming turn held on
`confirm_action` with a reminder fired in the gap (its `TimesFired` asserted first) was run against the Step 1
commit in a throwaway worktree, with only the new tests and the fake's helper copied in:
- `ReminderFiringMidStream_DoesNotResetTheStreamingTurnsPushBudget` failed: "Turn 4" was sent.
- `ReminderFiringMidStream_DoesNotUnsetTheStreamingTurnsIncognito` failed: the incognito turn was persisted.

Both pass on the new code. ADR 0106 records this.

**Where the implementation diverged from the plan:**
- **Steps 2a and 2b landed in one commit.** The 2b code was written while the 2a full-suite run was still going.
  The other steps are one commit each.
- **The Copilot test twin now suppresses `ExecutionContext` flow.** `ChatClientCopilotAgentFactory` used to call
  tools with `Task.Run`, which flows the context, so every Copilot grain test would have passed without the
  registry. It now calls them under `SuppressFlow`, so the existing Copilot grain tests exercise the registry. A
  mutation that made `SoleCopilotRun` return null fails 3 of them.
- **AC5's two-runs case is a unit test.** `CopilotTool_SeesItsRunsTurnContext`, `CopilotTool_TwoRunsInFlight_Refuses`
  and `CopilotTool_KeepsAFlowedContext` are in `CopilotSeamTests`, over `SchedulerBoundAIFunction`. A Copilot chat
  gets no client tools, so two concurrent Copilot runs cannot be staged on a grain. The grain half is the existing
  `CopilotTool_InvokedFromForeignThread_RunsOnGrainScheduler`, which now runs with flow suppressed.
- **`SchedulerBoundAIFunction` resolves the registry on the activation's scheduler.** It reads grain state there,
  never on the RPC thread, and it keeps a context that did flow.
- **`CurrentElicitationSink` now also checks `!_copilotChat`.** This keeps a Copilot MCP call from eliciting now that
  its context can carry a broker. That was the pre-change behaviour, by accident of the context not flowing.
- **FakeChatClient gained `CallToolsSequencePrompt`,** a scripted sequence across several tools. AC2 needs push ×2,
  `confirm_action`, push ×2. `CallToolSequencePrompt` delegates to it unchanged.
- **The search projection takes a history delegate, not a list.** It is constructed with a delegate over the
  grain's projection (`() => ProjectHistory()…`), not a per-call `SearchProjectionContext` list. The delegate is
  invoked inside each best-effort try, so a projection failure is still swallowed, as before. A list computed by the
  caller would have moved that read outside the try.
- **`ApplyRetentionAsync` became the synchronous `ApplyRetention`.** Nothing in it awaited once `GetHistoryAsync`
  (a `Task.FromResult`) was replaced by the projection count. The remap now happens in `ApplyRetention`, from the
  returned delta. It is the same moment the shift happens.
- **The services are constructed in `OnActivateAsync`** beside `_userId`: `_search`, `_retention` and `_forking`.
  `_toolSelection` is a readonly field initializer, because it is stateless.
- **`BuildAgent` kept its name and its seven call sites.** It is `ApplyComposition(ComposeAgent(modelId))`. The plan
  counted nine sites, but two of those go through `RebuildAgentPreservingSessionAsync`.
- **`ToolSelectionContext` carries no capabilities.** Capabilities are passed to the calls that need them
  (`SelectedSkills`, `SelectedMcpTools`). `EffectiveEnabledTools` and `ExpandedEnabledTools` do not depend on them.
- **A few members changed visibility.** `UserTurnIndices` (on `HistoryRetention`) is `public static`, because the
  repo has no `InternalsVisibleTo` and the tests call it. `UserIdFromGrainKey`, `ConversationIdFromGrainKey`,
  `AssistantImageId`, `CreateArtifactTool`, `UpdateArtifactTool` and `ParseArtifactResult` became `internal`, so the
  services can use them.
- **ADR bodies keep their historical member names.** The ADRs whose mechanism changed carry an "Amended by 0106"
  note. The others name members only as history, and no ADR names a file path that moved. ADR 0051 is untouched, as
  planned.
- **AC4c needed no new test.** `MemorySelected_RecallsAndRetains` already asserts retain after a streamed turn, and it
  passes.
