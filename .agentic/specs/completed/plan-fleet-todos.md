planned-against: main @ 68a0770

# Plan: a todo list for fleet agents

## Overview
Fleet agents (ADR 0097) plan nothing visible: a long-running agent, or a team of them, works through a task with
no checklist, and the user watching `/fleet/` sees only text and tool calls. The research agent (ADR 0082) already
gives a chat a live checklist through Agent Framework's `TodoProvider` (the same provider the AF `HarnessAgent`
preset bundles, tools `todos_add`, `todos_complete`, `todos_remove`, `todos_get_remaining`, `todos_get_all`). On
2026-11-08 the user asked for the same in the fleet: agents keep a todo list "like in the harness agent", and the
user sees it. This plan adds team-scoped todo tools that agents opt into by name, a `todos.updated` event, and a
checklist in the Details view and the graph.

## Affected areas (verified against the tree at 68a0770)
- `src/Agent42.Backend/Fleet/FleetCanvas.cs` — the exemplar for "a set of `AIFunction`s with rules in their
  descriptions, a `ToolNames` constant, an event published per committed change".
- `src/Agent42.Backend/Fleet/FleetTeamRunner.cs` (`AgentFunctions`) — where agent functions are assembled.
  `FleetTeamRunner.Planning.cs` calls `AgentFunctions(context)` TWICE per attempt (the planner's tool listing, line
  ~63, and `CreateAgentFactory`, line ~106).
- `src/Agent42.Backend/Fleet/FleetModels.cs` (`FleetTeamContext`: `TeamId`, `Attempt`, `Depth`).
- `src/Agent42.Backend/Fleet/FleetToolCatalog.cs` (`Usable`, the reserved-name set).
- `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs` (`BuildSessionConfig`, `Tools = [.. canvas.Tools]`).
- `src/Agent42.Backend/Fleet/FleetPrompts.cs` (planner, copilot and judge default prompt text).
- `src/Agent42.Backend/Fleet/FleetRun.cs`, `FleetTaskService.Run.cs` (`BuildRunAsync`) — per-task wiring.
- `src/Agent42.Backend/wwwroot/fleet/fleet.js` (`handlers`, `buildGraph`, the team/attempt Details render) and
  `fleet.css`.
- `tests/Agent42.Tests/FleetCanvasTests.cs`, `FleetToolCatalogTests.cs`, `FleetTeamRunnerTests.cs`,
  `FleetCopilotAgentFactoryTests.cs`, `FleetPageAssetsTests.cs` — the shapes to copy.
- New: `src/Agent42.Backend/Fleet/FleetTodos.cs`, `tests/Agent42.Tests/FleetTodosTests.cs`,
  a new ADR (`.agentic/decisions/0117-…`, next free number: verify with `ls .agentic/decisions`).

## Context & constraints
- **Do not use `TodoProvider` itself.** It is an `AIContextProvider` that keeps state in an `AgentSession`'s
  StateBag and offers tools only through a session. Fleet agents are built by the declarative loader
  (`ChatClientPromptAgentFactory(client, functions)`) and Copilot's `AsAIAgent`, which reference plain
  `AIFunction`s by YAML name and have no hook for a provider. (Evidence: `FleetTeamRunner.Planning.cs`
  `CreateAgentFactory`.) So the plan reimplements the five tools' *semantics* as plain functions, the way
  `FleetCanvas` does, and records "why not the provider / HarnessAgent" in the ADR (ADR 0082 already rejected
  `HarnessAgent` as a preset that removes control).
- **Scope is per team attempt, shared by that team's agents** — not per agent. ADR 0102 decision 5 records that no
  per-call agent identity is proven inside a workflow executor, so a per-agent list can't be keyed. A shared list
  is also the more useful checklist for a team (agents see and tick each other's items). Choice made by the
  planner of this spec (the user did not specify); the ADR must list "per agent" as a rejected alternative.
  A sub-team has its own list (its own `TeamId`); a retry/fix attempt starts empty.
- **The list must be memoized per `(TeamId, Attempt)`**, because `AgentFunctions(context)` is called twice per
  attempt. Create it in a small registry on `FleetTeamRunner` (or `FleetRun`), not inside `AgentFunctions`
  directly, or the planner's listing and the agents' functions hold different lists.
- **Opt-in by YAML tool name, like the canvas.** The planner prompt tells it the todo functions exist and which
  agents should get them (long multi-step agents, not one-shot writers). Rule 11 of CLAUDE.md applies in spirit:
  the model obeys the **tool descriptions**, so each description carries its own rules.
- **Copilot agents** get the todo functions as custom tools beside the four canvas tools (ADR 0102 decision 7:
  `SessionConfig.Tools` set, `AvailableTools` NOT set). Verified: `FleetCopilotAgentFactory` is built ONCE per task
  (`FleetTaskService.Run.cs:136-138`, takes `FleetCanvas canvas`, uses `Tools = [.. canvas.Tools]` at line 154), so
  it cannot see a per-attempt list. But `FleetTeamRunner.CreateAgentFactory(context)`
  (`FleetTeamRunner.Planning.cs:104-111`) runs per attempt with the full `FleetTeamContext` and already wraps
  `run.CopilotFactory` in an `AggregatorPromptAgentFactory`. So the per-attempt list reaches Copilot agents by
  making the factory attempt-aware, no `AsyncLocal`: give the factory a `ForAttempt(FleetTodos todos)` method
  (returns a `PromptAgentFactory` that adds `todos.Tools` to `BuildSessionConfig`'s `Tools`), and have
  `CreateAgentFactory` call it. `FleetRun.CopilotFactory` is typed `PromptAgentFactory?` (`FleetRun.cs:50`), so
  change it to the concrete `FleetCopilotAgentFactory?` (or a small interface) — check the tests that fake it
  (`FleetTestDoubles.cs`).
- **Persistence = the event log.** No new blob, no Cosmos, no `FleetTaskRecord` field. Each change publishes
  `todos.updated {teamId, attempt, todos:[{id,title,done}]}` carrying the WHOLE list (like ADR 0082's `Todos`
  event), so a replay or reload rebuilds the latest state by REPLACING, and the event coalescing in
  `FleetEventLog` (ADR 0097 gotcha at gotchas.md "The fleet's event log coalesces streamed text") only merges text
  events, so these are kept. The list is not needed after the attempt, so a follow-up starts empty (unlike the
  canvas). Cap: 50 items, 200 characters per title, a longer title or a 51st item is **refused with the reason,
  never truncated** (the ADR 0102 convention).
- **Concurrency:** agents of a team run concurrently (Concurrent, GroupChat). One `SemaphoreSlim`/lock per list
  makes add/complete/remove atomic and ids unique (monotonic int, never reused), the publish happens under it so
  event order matches state order.
- **Tool names:** `todo_add`, `todo_complete`, `todo_remove`, `todo_list` (list returns remaining + done flags, so
  one read tool, not the harness's two). Reserve all four in `FleetToolCatalog.Usable`'s name set, and in the
  Copilot custom-tool set. Check for clashes with MCP names the same way `FleetCanvas.ToolNames` is reserved.
- **Judge:** append the team's final todo list (open items first) to the judge input next to
  `run.Canvas.Describe()` in `FleetTeamRunner.cs:198`, so an unfinished checklist counts against delivery. Keep
  it short (the list is capped).
- **Frontend (ADR 0041, ADR 0116):** `/fleet/` is outside the service worker SHELL (verified: `sw.js:104` returns
  early for `/fleet`, and `ServiceWorker_LeavesFleetOutOfTheShell` pins it), so **no `sw.js` CACHE bump**. The
  comment above `handlers` in `fleet.js` claims `FleetPageAssetsTests` checks every `Publish("…")` has a handler, but
  commit 60f9517 pruned that test: `PublishRegex`, `InnerHtmlRegex` and `HexColourRegex` are declared in
  `FleetPageAssetsTests.cs` and used by nothing. So nothing enforces the handler; the new event needs a
  `handlers["todos.updated"]` by discipline, and Step 5 adds the guard as a NEW test. Labels are `textContent` only.
- **Stale docs to fix (hard rule 2), mandatory:** ADR 0116's Consequences ("behaviour is pinned by source-text guards
  in `FleetPageAssetsTests`") and the `fleet.js` comment above `handlers` both describe guards that no longer exist.
  Fix both in this MR (also delete the three dead regex declarations, or use them, in the test you add).
- Governing ADRs: 0097 (fleet, tools decision 6), 0102 (canvas, the exemplar), 0116 (graph), 0082 (research todo,
  the prior art), 0041 (frontend). Gotchas to re-read before editing: the fleet entries in `.agentic/gotchas.md`
  (event log coalescing ~L4380, replay-then-live ~L4394, interrupted-on-read ~L4407, canvas outside the workspace
  prefix ~L4436).

## Steps
1. **`FleetTodos` (new, `Fleet/FleetTodos.cs`).** Class modelled on `FleetCanvas`: ctor `(IFleetEventSink events,
   string teamId, int attempt)`, `Tools` (4 `AIFunction`s with rule-bearing descriptions), `ToolNames` constant,
   `UpdatedEvent = "todos.updated"`, `Items` snapshot, `Describe()` for the judge. Add/complete/remove accept
   arrays (like the harness: "one or more items") so one call updates many. Under one lock: validate, mutate,
   publish the whole list. Refusals are returned as the tool result text (unknown id, cap, empty/over-long
   title). Idempotent complete (already done is not an error). Verify: `dotnet test --filter FleetTodosTests`.
2. **Per-attempt registry + wiring.** In `FleetTeamRunner`, a `ConcurrentDictionary<(string TeamId, int Attempt),
   FleetTodos>` with `TodosFor(FleetTeamContext)`; `AgentFunctions` appends `TodosFor(context).Tools` when
   `context.TeamId` is not null. Reserve `FleetTodos.ToolNames` in `FleetToolCatalog.Usable`. Verify: extend
   `FleetTeamRunnerTests` (the two `AgentFunctions` calls of one attempt return the SAME instance; two attempts
   and two teams return different ones) and `FleetToolCatalogTests` (an MCP tool named `todo_add` is skipped).
3. **Copilot agents.** Add `FleetCopilotAgentFactory.ForAttempt(FleetTodos)` as described in Context, retype
   `FleetRun.CopilotFactory`, call it from `CreateAgentFactory`, and put `[.. canvas.Tools, .. todos.Tools]` in
   `SessionConfig.Tools`, never setting `AvailableTools`. Verify: extend `FleetCopilotAgentFactoryTests` (the
   session config's tool names include the four todo names and the four canvas names) and
   `FleetTeamRunnerTests` (the Copilot factory used by an attempt is the one bound to that attempt's list).
4. **Prompts + judge.** Add one short paragraph to the planner prompt (todo functions: for agents with several
   steps; the team shares one list; tick items as you finish), one sentence to the `copilot` prompt (it also gets
   them), and append `todos.Describe()` to the judge input. Prompts are user-overridable text: the descriptions in
   Step 1 must stand alone. Verify: `FleetTeamRunnerTests` judge-input test sees the open items; a prompt test
   (copy how the canvas sentence is asserted, if any exists) or a snapshot of the default planner prompt.
5. **Frontend.** Also fix the stale `handlers` comment (see Context). `handlers["todos.updated"]`: store `attempt.todos = d.todos` (replace) on `attemptOf(d)`, call
   `invalidate()`, return normally. Details view: a small checklist panel at the top of the attempt (done/total
   counter, items with a check), re-rendered from `attempt.todos`, hidden when empty. Graph (`buildGraph`): show
   `done/total` on the team box when the latest attempt has todos. `fleet.css`: styles for the panel, no new
   colors beyond existing tokens. `textContent` only. Verify: a NEW source-text test in `FleetPageAssetsTests` (a
   `handlers["todos.updated"]` entry exists and item titles are not set via `innerHTML`), plus a manual check
   in DevHost (Step 7).
6. **Docs.** Fix ADR 0116's stale Consequences sentence. `/decide` the ADR 0117 (team-scoped, plain functions not `TodoProvider`/`HarnessAgent`, event-log
   persistence, refuse-never-truncate caps, Copilot custom-tool note, judge sees open items). Amend ADR 0097
   (`amended_by`, decision 6 tools line) and ADR 0116 (graph badge). Add to the feature map row "Agent fleet" in
   CLAUDE.md. Add a `.agentic/gotchas.md` entry only if Step 2/3 uncover a trap (e.g. the double `AgentFunctions`
   call: that one qualifies, with evidence paths to the test). Run `/sync-docs`.
7. **Live check in DevHost** (`dotnet run --project tools/Agent42.DevHost`, UI at http://localhost:61326/fleet/):
   needs the canned LLM to call the todo tools. Check how `tests/Agent42.Tests/FakeChatClient.Fleet.cs` scripts
   tool calls; if DevHost's canned model can't, say so in the final report rather than claiming it was seen live.

Steps 1→2→3 are sequential (3 depends on 2's wiring). Step 5 (frontend) is independent of 2–4 once the event shape
in Step 1 is fixed, and may run in parallel with them (worktree isolation required).

## Acceptance criteria → test mapping
| AC | Test | File |
|----|------|------|
| `todo_add` adds several items, ids are unique and never reused after a remove | `Add_AssignsUniqueIds_NeverReused` | `tests/Agent42.Tests/FleetTodosTests.cs` |
| `todo_complete` marks items done, is idempotent, unknown id is refused with the reason | `Complete_IsIdempotent_UnknownIdRefused` | same |
| Every change publishes ONE `todos.updated` carrying the whole list, in state order under concurrent calls | `ConcurrentCalls_PublishOrderedWholeLists` | same |
| Over 50 items or a title over 200 chars is refused, nothing truncated | `Caps_RefuseNeverTruncate` | same |
| Two `AgentFunctions` calls for one attempt share the list; another attempt/team gets its own | `TodosFor_SameAttemptSameList` | `tests/Agent42.Tests/FleetTeamRunnerTests.cs` |
| An MCP tool named like a todo tool is skipped | `McpTool_NamedLikeTodoTool_IsSkipped` | `tests/Agent42.Tests/FleetToolCatalogTests.cs` |
| Copilot agents get the todo tools as custom tools without `AvailableTools` | `SessionConfig_HasTodoTools_NoAvailableTools` | `tests/Agent42.Tests/FleetCopilotAgentFactoryTests.cs` |
| The judge input lists open items | `JudgeInput_IncludesOpenTodos` | `tests/Agent42.Tests/FleetTeamRunnerTests.cs` |
| The event survives the log (not coalesced away) and replays | `TodosUpdated_IsPersistedAndReplayed` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| Page has a `todos.updated` handler and does not use `innerHTML` for titles (new test) | `TodosHandler_Exists_NoInnerHtml` | `tests/Agent42.Tests/FleetPageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)
| Gate | Threshold | Measured by |
|------|-----------|-------------|
| New-code coverage | >= 80 % (`coverage_new_code_pct_min`) | `node .agentic/scripts/check-new-code-coverage.mjs` |
| Cognitive complexity | <= 15 (`cognitive_complexity_max`, S3776, build error) | `dotnet build Agent42.slnx` |
| Vulnerable packages | 0 | CI package-freshness job |
| Format | clean | `dotnet format Agent42.slnx --verify-no-changes` |
| Tests | green | `dotnet test Agent42.slnx` |

Report actual measured values, never estimates; if a tool can't run, report SKIPPED.

## Out of scope
- Per-agent todo lists and anything keyed by agent identity (ADR 0102 decision 5: unproven).
- A loop that re-runs an agent until its todos are done (`TodoCompletionLoopEvaluator`/`LoopAgent`, ADR 0082): the
  fleet's judge/fix cycle already owns "is the work complete".
- Adding the `Microsoft.Agents.AI.Harness` package or `HarnessAgent` (ADR 0082 rejected it).
- Persisting the list across follow-ups, a blob, or a `FleetTaskRecord` field.
- User editing of the list (read-only in the UI, like the canvas).
- The chat's research checklist and `research.js` (untouched; the fleet page does not import it).
- Any `sw.js` change (fleet is outside SHELL).

## Review
Critic pass (2026-11-08), no blocking findings, two factual corrections, both accepted:
1. Accepted: the handler-per-`Publish` guard in `FleetPageAssetsTests` no longer exists (pruned in 60f9517; verified,
   the regexes are declared and unused). Context, Step 5, Step 6 and the AC row now say a NEW test is added and the
   stale ADR 0116 sentence and `fleet.js` comment are fixed.
2. Accepted: the Copilot factory question has a clear answer (attempt-aware factory via `CreateAgentFactory`,
   `ForAttempt(FleetTodos)`), so the "spike it / fall back" hedging was replaced by a concrete Step 3.
Confirmed by the critic: `AgentFunctions` is called twice per attempt (memoization needed), `TeamId`/`Attempt` are
set before those calls, a fix attempt keeps its `TeamId` (the `(TeamId, Attempt)` key handles it), `/fleet` is
outside SHELL, coalescing only merges `agent.text`/`agent.thinking`, next free ADR is 0117, no ADR contradiction.
Not checked by the critic: the `FleetToolCatalog.Usable` reserved-name set and the prompt/judge-input tests; the
executor verifies these in Steps 2 and 4.

## Implementation notes (divergences from the plan)
Executed 2026-11-08 on `feature/fleet-todos`. Everything in Steps 1-6 shipped as planned, with these differences:
- `FleetCopilotAgentFactory.ForAttempt(todos)` returns a public nested `AttemptFactory` (exposes `Todos`), so a test can
  assert which list a factory is bound to. `FleetRun.CopilotFactory` was retyped to the concrete
  `FleetCopilotAgentFactory?`; no test double needed changing.
- `TodosFor` returns `null` for the root context (no `TeamId`), so `AgentFunctions(root)` carries no todo tools. In a
  real run every call has a team id.
- The judge input gets the todo section only when the team planned a list (no empty heading).
- The AC "the Copilot factory used by an attempt is the one bound to that attempt's list" is covered by
  `ForAttempt_BindsThatAttemptsList` and `SessionConfig_HasTodoTools_NoAvailableTools`, not through
  `CreateAgentFactory` itself: building a real Copilot agent needs the runtime.
- Extra tests beyond the AC table: `TodoTools_NeedATeam_AndAreListedForThePlanner` (also asserts the default planner
  prompt text), `JudgeInput_HasNoTodoSection_WhenNoneWerePlanned`, `List_ShowsIdsAndDoneFlags`, `Describe_...`,
  `Remove_UnknownIdRefused_NothingChanges`.
- `FleetPageAssetsTests` lost its three dead regex declarations and is no longer `partial`.
- Step 7 (live check in DevHost) was NOT done: DevHost's canned client does not script tool calls, so the checklist
  and the graph badge were not seen live. Only source-text and unit tests cover the UI.
- Gotcha added for the double `AgentFunctions` call.
