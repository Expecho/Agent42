planned-against: main @ 666915b

# Plan: opt-in self-review loop for fleet agents (`LoopAgent`)

## Overview

Let a fleet task ask that the agent(s) producing a team's result re-check their own output against the task and fix gaps
before the team's judge sees it. The engine is Agent Framework's `LoopAgent` + `AIJudgeLoopEvaluator`, which live in the
**already pinned** `Microsoft.Agents.AI` 1.25.0 (no new package; the `Microsoft.Agents.AI.Harness` package and
`HarnessAgent` stay out, ADR 0082/0117). Why now: the team judge (ADR 0097) only reviews at depth <= `JudgeDepth` and
reruns a whole team; a cheap per-agent self-check may avoid some of those fix rounds. **That benefit is unproven**, so the
plan starts with a no-code baseline and a spike, and ships the loop off by default, per task.

Governing prior art: ADR 0097 (fleet; judge/fix/new-team, budget), ADR 0117 (todos are team-scoped; rejected
`TodoProvider`/`HarnessAgent`), ADR 0082 (research agent has its OWN loop/judge; rejected `HarnessAgent`), ADR 0118
(exemplar for a per-task opt-in flag, end to end).

## Affected areas

(verified against the tree at the commit above)

- `src/Agent42.Backend/Fleet/FleetTeamRunner.Planning.cs` — `LoadAgentsAsync` (agent creation point, `CheckAgent`),
  `CreateAgentFactory`, `ModelRecordingPromptAgentFactory` (records each agent's model id by name).
- `src/Agent42.Backend/Fleet/FleetTeamRunner.Workflows.cs` — `BuildWorkflow`, `RunWorkflowAsync`, `FinalResult`,
  `RunStagesAsync` (how a team's result is read from workflow events).
- `src/Agent42.Backend/Fleet/FleetTeamRunner.cs` — `RunNewTeamAsync` / `ReviewAsync` (judge, `JudgeDepth`).
- `src/Agent42.Backend/Fleet/FleetEventMapper.cs` — maps workflow events to fleet events (extra iterations will pass here).
- `src/Agent42.Backend/Fleet/FleetOptions.cs`, `src/Agent42.Agents/Fleet/FleetTaskRecord.cs`,
  `src/Agent42.Backend/Fleet/FleetTaskService.cs` (`FleetSubmit`, `SubmitAsync`), `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs`
  (where `FleetRun` is built), `src/Agent42.Backend/Fleet/FleetRun.cs`, `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`
  (`FleetRequest`, both submit endpoints) — the per-task flag, modelled on `Clarify` (ADR 0118) plus the `FleetRun` plumbing
  `Clarify` does not need (Context 9).
- Test doubles that build a `FleetRun`: `FleetRunHarness` in `tests/Agent42.Tests/FleetTestDoubles.cs`, and the inline
  `new FleetRun` in `CopilotModelDiscoveryTests` and `FleetCopilotAgentFactoryTests`.
- `src/Agent42.Backend/wwwroot/fleet/index.html`, `fleet.js` (composer checkbox, remembered in `localStorage`), and
  `sw.js` if the fleet assets are in `SHELL` (see Step 5).
- Tests: `tests/Agent42.Tests/FleetTeamRunnerTests.cs`, `FleetTestDoubles.cs`, `FakeChatClient.Fleet.cs`,
  `FleetApiTests.cs`, `FleetTaskServiceTests.cs`, `FleetOptionsTests.cs`, `FleetPageAssetsTests.cs`.
- Docs: new ADR 0119 (next free number: 0118 is the latest in `.agentic/decisions/`), `CLAUDE.md` feature map (Agent
  fleet row), ADR 0097 `amended_by`.

## Context & constraints

Findings the executor cannot cheaply re-derive (each with where to look):

1. **`LoopAgent` API (read from the 1.25.0 and 1.28.0 package XML docs in `Z:\Packages\.nuget\packages\microsoft.agents.ai\<v>\lib\net10.0\Microsoft.Agents.AI.xml`; never compiled here).**
   `LoopAgent` re-runs the wrapped agent until its `LoopEvaluator`s stop; options: `MaxIterations` (hard cap),
   `FreshContextPerIteration`, `OnBehalfOfAuthorName`, `ExcludeOnBehalfOfMessages`, `NonStreamingReturnsLastResponseOnly`.
   `AIJudgeLoopEvaluator` asks a separate chat client whether the original request was fully addressed, with optional
   `Criteria`, and feeds its gap analysis back as the next input. Built-in `TodoCompletionLoopEvaluator` needs a
   `TodoProvider` and is **not usable** (finding 3). Treat every behavior claim here as UNVERIFIED until Step 2.
2. **One creation point.** Every non-Copilot, non-planner agent is created in `LoadAgentsAsync` via
   `factory.CreateFromYamlAsync`; the plan (`plan.Pattern`, `plan.Stages`) and `context.Depth` are known there, but NOT in
   `CreateAgentFactory(context)`. So wrap in `LoadAgentsAsync` after `CheckAgent`, not in the factory. Copilot agents come
   from the same factory; detect them with the model ids `ModelRecordingPromptAgentFactory` records by agent name
   (a plain `copilot` hint is recorded verbatim; an agent with no hint records `run.DefaultModel`, never Copilot):
   treat a value that `StartsWith("copilot", StringComparison.OrdinalIgnoreCase)` as Copilot (the dictionary is
   case-insensitive on its KEY only) and leave it unwrapped (own loop; ADR 0085/0097).
3. **`FleetTodos` is per team attempt, shared by all its agents** (ADR 0117, `FleetTeamRunner.TodosFor`). A todo-based
   stop condition would make one agent loop on a teammate's open items. Do not use todo state as an evaluator.
4. **The team result is read from workflow events** (`FinalResult`): the LAST assistant message for non-Concurrent
   patterns. `LoopAgent` injects "on-behalf-of" feedback as user-role messages, which `FinalResult` ignores
   (`m.Role == ChatRole.Assistant`) — but that is an inference; Step 2 must show it, in streaming mode, through
   `InProcessExecution`.
5. **Which agents loop (decision, reviewed by the user at approval).** Only the agent(s) that PRODUCE the team's result:
   the last agent of a `Sequential` team, the agents of the last stage of a `Staged` team. Handoff / GroupChat / Magentic
   / Concurrent are not wrapped: they depend on one turn per agent, a moderator or a manager (looping could speak
   extra turns or write teammates' messages, `FleetPrompts` planner text already warns about the latter), and
   Concurrent's result is a join. Earlier pipeline agents are refined by later ones, so looping them costs without a
   clear gain. Revisit after measurements.
6. **Depth.** Loop only at depth <= `Options.JudgeDepth` (same gate the team judge uses in `ReviewAsync`), because
   deeper teams are deliberately unjudged and single-attempt for cost; recursion would multiply self-review cost.
7. **The judge client.** Use `run.DefaultClient`, as the team judge and clarifier do — it is wrapped in the per-task token
   budget, so every self-review run and judge call counts against it and a budget cancel stops the loop (the budget
   client throws like it does for the judge). Do not create a client outside `run`.
8. **Budget/cost shape.** Each re-run is a full model+tools run of that agent plus one judge call per iteration. Cap with
   a new option; default small. The team judge's fix rounds and new teams re-run the SAME loaded agents (`ReviewAsync`
   -> `RunNewTeamAsync`), so the loop runs inside every attempt: the worst case for a judged team is
   `MaxAttempts x (1 + MaxFixRounds) x SelfReviewMaxIterations` runs of the wrapped agent, plus a judge call per
   iteration, all inside the task's token budget (which cancels the task when used up). Wrap the agent once per
   `LoadAgentsAsync`; decide in Step 2 whether the `LoopAgent`'s own session persists across fix rounds or is fresh
   each time, and state it in the ADR. Beyond `JudgeDepth` a team has one attempt and is not wrapped (Context 6).
9. **Per-task flag convention (ADR 0118), and where it differs.** `FleetTaskRecord` is plain JSON, not Orleans grain
   state (its own comment says so), so the flag is a plain `bool X { get; init; }` with a default of false: a record
   stored before the field loads as false, no migration, and CLAUDE.md rule 2's `[Id(n)]` does NOT apply. Carried by
   `FleetSubmit`, `FleetRequest`, the composer checkbox (remembered in `localStorage`), default OFF for API callers.
   **Chat-started tasks (ADR 0115) never self-review**, exactly like `Clarify` (`FleetTaskService.SubmitAsync` stores
   `request.X && string.IsNullOrWhiteSpace(request.OriginConversationId)`; a test pins it). There is NO global
   default option; the only new option is the iteration cap.
   **Difference from `Clarify`:** `Clarify` is read from the record before planning and never reaches `FleetRun`.
   Self-review is read inside `FleetTeamRunner`, so it needs a new, non-required `FleetRun` property (so the
   `FleetRunHarness` in `FleetTestDoubles.cs` and the inline `new FleetRun` in `CopilotModelDiscoveryTests` /
   `FleetCopilotAgentFactoryTests` keep compiling), set where `FleetRun` is built in `FleetTaskService.Run.cs`.
10. **Prompt-only alternative.** Telling the planner (editable `planner` prompt, `FleetPrompts.cs`) to add a "re-check
    your output against the task before finishing" line to each agent's instructions costs nothing in code. It is the
    baseline the loop must beat.
11. Conventions that apply: flat namespace for `Agent42.Backend` types (rule 1); S3776 <= 15 is a build error — keep
    `LoadAgentsAsync` from growing, put the wrapping in its own class/file like `HindsightRegistration` (rule 9);
    never write a package version from memory (rule 4) — and none is needed here; read `.agentic/gotchas.md` entries for
    the fleet (event log coalescing, replay-then-live) before touching event publishing.

User decisions recorded so far: opt-in per task, off by default; ship only after the spike confirms the shape;
Copilot agents excluded; no `HarnessAgent`.

## Steps

### Step 1 — Baseline: the prompt-only variant (no code; user-run, not parallelizable)
Add the "self-check before finishing" wording to the `planner` prompt through the fleet page's prompt editor
(the prompts are user-editable, stored per user, `prompts.json`) and run 3-5 representative tasks with and without it,
reading the usage dashboard (agent fleet application) for tokens and the event log for judge scores and fix rounds.
Record the numbers in this spec's **Review/Findings** section.
**Verify:** numbers written down. **Gate:** if the prompt-only variant already removes most fix rounds, stop and tell the
user; proceed only on their go. (This step needs live models, so the executor asks the user to run it; it is not
automatable here.)

### Step 2 — Spike (`spike-dotnet` skill, file outside the repo tree, pinned to the repo's package versions)
One file-based app referencing `Microsoft.Agents.AI` 1.25.0 and `Microsoft.Agents.AI.Workflows` 1.25.0 with a scripted
fake `IChatClient`. Answer, with output pasted into the spec's Findings:
a. Does a `LoopAgent`-wrapped agent forward `GetService<ChatOptions>()` and the agent's name/description/id (needed by
   `CheckAgent`, `ModelRecordingPromptAgentFactory` lookups, and the workflow's executor ids)? If not, how to expose them.
b. Inside `AgentWorkflowBuilder.BuildSequential` run through `InProcessExecution.RunStreamingAsync`: what
   `AgentResponseUpdateEvent`s arrive for iteration 2+ (author, executor id, message ids), do on-behalf-of messages
   appear and as which role, and does the final `WorkflowOutputEvent` still end with the last assistant message so
   `FinalResult` stays correct? Try `ExcludeOnBehalfOfMessages` true/false and `FreshContextPerIteration` true/false.
c. Does a looped agent work as an executor across the multi-turn session the workflow gives it (history not replayed
   twice, no duplicated user input)?
d. Cancellation: cancel mid-iteration; does the loop stop and surface `OperationCanceledException` (the runner relies on
   that for stop and budget cancel, `FinalResult`'s `WorkflowErrorEvent` case)?
e. `AIJudgeLoopEvaluator` with a fake judge client: does a judge that returns no/garbled verdict stop or loop (the team
   judge fails open, `JudgeAsync`; this must too — an evaluator error must not retry forever)? Confirm `MaxIterations`
   is the real bound.
f. Does a `Staged` last stage with several agents work (each wrapped agent loops independently)? `RunStagesAsync` builds a
   fresh sub-workflow per stage: a single-agent last stage is Sequential, a multi-agent one is Concurrent, whose result
   is a `[Author]: text` join (`FinalResult`). Show the join text stays clean (no feedback text, correct author names),
   and that name-based lookups survive wrapping (depends on a).
**Verify:** each of a-f answered with evidence. If a or b fails with no workable workaround, STOP and report; do not
force the integration (rule: report what you ruled out).

### Step 3 — Options and the per-task flag (parallelizable with Step 4 after Step 2)
- `FleetOptions`: `SelfReviewMaxIterations` (cap on loop iterations, default chosen from the spike; below 1 means
  off, matching how `MaxClarifyingQuestions` clamps) and document it in `FleetOptionsTests`.
- `FleetTaskRecord`: a plain `bool ... { get; init; }` with a default, beside `Clarify` (Context 9; no `[Id]`,
  no Orleans). Prove an old stored record without the field loads as false (`FleetStoreTests`).
- Plumb: `FleetSubmit`, `FleetRequest`, both `POST /api/fleet/tasks[/with-files]` paths, `SubmitAsync` (stored false
  for a task with an `OriginConversationId`, like `Clarify`), a new non-required `FleetRun` property set where
  `FleetRun` is built in `FleetTaskService.Run.cs`. The task JSON the page reads must expose the field so Run again
  can repeat it (check how `clarify` is serialized to the page).
**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetOptionsTests|FullyQualifiedName~FleetApiTests|FullyQualifiedName~FleetTaskServiceTests"`.

### Step 4 — The wrapper (parallelizable with Step 3 after Step 2; write-capable steps need worktree isolation or run sequentially)
New file `src/Agent42.Backend/Fleet/FleetSelfReview.cs` (flat namespace): a small class that, given the plan, the loaded
agents, the recorded model ids, the depth and `run`, returns the agent list with the producing agent(s) wrapped in
`LoopAgent` + `AIJudgeLoopEvaluator` (judge client = `run.DefaultClient`, fail-open, `MaxIterations` from the option).
`LoadAgentsAsync` calls it in one line after the agents loaded (keep its complexity flat). It publishes a
`selfreview.iteration` event (teamId, attempt, agent, iteration, gap analysis cut with `FleetTeamRunner.Cut`) so the
log and page can show it; check `fleet.js` ignores unknown events (a fleet.js change is needed only if it does not).
Apply the exclusions from Context 5/6 (pattern, Copilot, depth). Truncate gap analysis/feedback that reaches events
(rule 5 spirit; `Options.MaxToolTextLength`).
**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetTeamRunnerTests|FullyQualifiedName~FleetSelfReviewTests"`.

### Step 5 — Composer checkbox (depends on Step 3)
`index.html` + `fleet.js`: "Self-review the result" checkbox next to "Ask me questions first", remembered in
`localStorage` (try/catch, same shape as `loadClarify`/`saveClarify`), default off, sent as the new request field via
a new parameter of `submitTask`. **Run again** (`runAgainCard`) currently passes `!!task.clarify` as the last
argument: it must pass the new flag the same way. No `sw.js` `CACHE` bump: `/fleet` is outside `SHELL`
(`sw.js` returns early for `/fleet`; `FleetPageAssetsTests.ServiceWorker_LeavesFleetOutOfTheShell` pins it).
**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetPageAssetsTests"`.

### Step 6 — Tests alongside (see the mapping table), then full gates
Write each test with its step; at the end `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
`dotnet build Agent42.slnx`, and the new-code coverage script (`node .agentic/scripts/check-new-code-coverage.mjs`)
after a `dotnet test ... --collect:"XPlat Code Coverage"` run.

### Step 7 — Docs (same MR)
`/decide` ADR 0119 (amends 0097): the shape, the pattern/depth/Copilot exclusions, the cost multiplier
(`MaxAttempts x (1 + MaxFixRounds) x SelfReviewMaxIterations`, Context 8), fail-open, and the
alternatives (HarnessAgent: rejected by 0082/0117; todo evaluator: team-scoped todos; prompt-only baseline results from
Step 1; always-on; looping every agent). `/gotcha` entry for whatever Step 2 found (for example the on-behalf-of
messages or `GetService` forwarding). Update `CLAUDE.md` Agent fleet row (add 0119) and ADR 0097 `amended_by`. Run
`/sync-docs`. Move this spec to `.agentic/specs/completed/`.

## Acceptance criteria -> test mapping

| AC | Test | File |
|----|------|------|
| Flag off (default): agents are not wrapped, behavior unchanged | `SelfReview_Off_AgentsUnwrapped` | `tests/Agent42.Tests/FleetSelfReviewTests.cs` |
| Sequential team: only the last agent loops; a judge "not done" triggers one re-run, "done" stops | `SelfReview_Sequential_LastAgentReruns` | same |
| Staged team: only last-stage agents loop | `SelfReview_Staged_LastStageOnly` | same |
| Handoff/GroupChat/Magentic/Concurrent are never wrapped | `SelfReview_OtherPatterns_Unwrapped` | same |
| Copilot-model agents are never wrapped | `SelfReview_CopilotAgent_Unwrapped` | same |
| Teams deeper than `JudgeDepth` are not wrapped | `SelfReview_BeyondJudgeDepth_Unwrapped` | same |
| Iterations are capped by `SelfReviewMaxIterations`; below 1 means off | `SelfReview_Cap_Respected`, `Options_SelfReviewBelowOne_IsOff` | `FleetSelfReviewTests.cs`, `FleetOptionsTests.cs` |
| A judge that errors or returns nothing fails open (result kept, no retry loop) | `SelfReview_JudgeFails_ResultKept` | `FleetSelfReviewTests.cs` |
| The team result is still the final assistant answer, never the injected feedback | `SelfReview_Result_IsLastAssistantMessage` | `FleetTeamRunnerTests.cs` |
| Tokens of the re-runs and judge calls count against the task budget; a budget cancel stops the loop | `SelfReview_CountsAgainstBudget_StopsOnCancel` | `FleetSelfReviewTests.cs` |
| A stop cancels a running loop and is not swallowed | `SelfReview_Stop_Cancels` | `FleetSelfReviewTests.cs` |
| `selfreview.iteration` events are published, truncated, and replay | `SelfReview_PublishesEvents` | `FleetSelfReviewTests.cs` |
| The flag is accepted by both submit endpoints, stored, repeated by Run again; absent means false | `Submit_SelfReview_Stored`, `RunAgain_RepeatsSelfReview` | `FleetApiTests.cs`, `FleetTaskServiceTests.cs` |
| An old stored record without the field loads as false (no migration) | `OldRecord_SelfReviewDefaultsFalse` | `FleetStoreTests.cs` |
| A task started from a chat never self-reviews (stored false even if requested) | `Submit_SelfReview_IsStoredOnlyForPageTasks` | `FleetTaskServiceTests.cs` |
| The composer sends the flag and remembers it | `FleetPage_HasSelfReviewCheckbox` | `FleetPageAssetsTests.cs` |
| The page's Run again repeats the flag | `FleetPage_RunAgain_RepeatsSelfReview` | `FleetPageAssetsTests.cs` |
| A multi-agent last stage still yields a clean joined result | `SelfReview_StagedConcurrentLastStage_CleanJoin` | `FleetSelfReviewTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (re-read it at execution; never hardcode):

- [ ] `coverage_new_code_pct_min: 80` — `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet x git diff)
- [ ] `cognitive_complexity_max: 15` — SonarAnalyzer S3776, a build error (`dotnet build Agent42.slnx`)
- [ ] `vulnerable_packages: 0` — no package is added; confirm `git diff` leaves `Directory.Packages.props` untouched
- [ ] `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test Agent42.slnx` green
- Report SKIPPED for any gate whose tool cannot run; never estimate an Actual (rule 3).

## Out of scope

- The `Microsoft.Agents.AI.Harness` package and `HarnessAgent` (rejected by ADR 0082/0117; `LoopAgent` is in the pinned core package).
- A todo-completion evaluator (todos are team-scoped, Context 3).
- Looping earlier pipeline agents, Concurrent/GroupChat/Handoff/Magentic agents, Copilot agents, or teams beyond `JudgeDepth`.
- An editable self-review judge prompt in the prompt editor (default `AIJudgeLoopEvaluator` instructions first; revisit if tuning is needed).
- A `start_fleet_task` parameter or per-agent YAML switch for self-review.
- Changing the team judge, fix rounds or budget semantics (ADR 0097).
- Any UI beyond the composer checkbox and Run again (no graph/Details rendering of iterations unless the spike shows the page mishandles the new event).
- Exposing `SelfReviewMaxIterations` in the `/api/fleet` config response (`AgentApi.Fleet.cs`, which exposes `judgeDepth`): the page does not need it.

## Review

Critic pass (fresh-context `critic` subagent, 2026-11-08). Verdict: no ADR conflict; paths, the single creation point
and the `FinalResult` reading verified. Seven findings, all accepted and fixed in this spec:

1. Context 9 referred to a nonexistent global option -> removed; chat-started tasks never self-review (like `Clarify`),
   a test added to the mapping.
2. Run again / page path under-specified -> Step 5 now changes `submitTask` and `runAgainCard`, and the task JSON must
   expose the field; AC and test added.
3. Flag shape and plumbing were mis-modelled on Orleans `[Id]` -> corrected to a plain `init` property; the new
   `FleetRun` property and the test doubles that build `FleetRun` are named in Affected areas.
4. Fix rounds/new teams re-run the same wrapped agents -> Context 8 states the worst-case multiplier; Step 7 puts it in
   the ADR; Step 2 decides session persistence across fix rounds.
5. Copilot detection -> explicit ordinal-ignore-case `StartsWith("copilot")` on the recorded value.
6. Staged multi-agent last stage -> spike item f extended; AC and test added.
7. Doc/test gaps -> the `sw.js` hedge replaced by evidence (`/fleet` is outside `SHELL`, no CACHE bump); the `/api/fleet`
   config endpoint explicitly out of scope.

Rejected findings: none. Not re-verified by the critic: the quality-gate values (they were copied from
`.agentic/quality-gates.yaml` at plan time and the file is the authority at execution).

### Step 2 spike results (2026-11-08; `Microsoft.Agents.AI` + `.Workflows` 1.25.0, scripted fake `IChatClient`, run for real)

- **Experimental API, with in-repo precedent.** `LoopAgent`, `AIJudgeLoopEvaluator`, `LoopAgentOptions` raise `MAAI001`
  ("evaluation purposes only"). The research agent (ADR 0082) ALREADY uses them: `src/Agent42.Agents/Research/AgentGrain.Research.cs`
  (`BuildAgent`: `new LoopAgent(... DelegateLoopEvaluator, TodoCompletionLoopEvaluator, AIJudgeLoopEvaluator ...)`) under a local
  `#pragma warning disable MAAI001 // ... LoopAgent are [Experimental]`, with the iteration cap in `ResearchLimits`. That file is
  the exemplar for the wrapper's shape and pragma. (An earlier statement in the planning session that no ADR used
  `LoopAgent` was wrong; ADR 0082 and 0117 rejected only the `HarnessAgent` preset.)
- **a. Forwarding: yes.** `Name`, `Id` (equals the inner agent's), `Description` and `GetService<ChatOptions>()` (tools
  included) all pass through `LoopAgent`, so `CheckAgent` and executor ids work unchanged.
- **b. Workflow events and result.** In `BuildSequential` via `RunStreamingAsync`, iteration 2 arrives as further
  `AgentResponseUpdateEvent`s from the SAME executor id. With `ExcludeOnBehalfOfMessages = false` the injected feedback
  arrives as an update with `role=user`, author = `OnBehalfOfAuthorName`; with `true` it does not. `FinalResult`'s "last assistant
  message" is correct in all four option combinations ('draft two (fixed)'). **`FleetEventMapper` ignores role and author**
  (`Publish` maps every `Contents` item to `agent.text`), so with `false` the feedback would be published as the agent's own
  text. Decision: `ExcludeOnBehalfOfMessages = true`. Both drafts then stream as consecutive `agent.text` of one agent
  (the event log coalesces consecutive text deltas of an executor, so they would merge); the `selfreview.iteration` event
  published between iterations is what separates them for the page.
- **c. Context.** `FreshContextPerIteration = false`: the re-run sees task + its own previous draft + the feedback, with the
  task NOT duplicated. `true`: it sees task + feedback only and NOT its own draft. A self-review needs the draft, so
  **`FreshContextPerIteration` must stay false.**
- **d. Cancellation/exceptions.** A cancel during iteration 2 surfaces as `OperationCanceledException`; an exception thrown
  by the writer client in iteration 2 propagates unchanged. So a budget cancel and a stop behave like before.
- **e. Judge failures are NOT fail-open.** A judge client that throws propagates its exception (it would fail the agent and
  the team: "The team failed: ..."); a judge that returns no verdict marker means "more" and loops until `MaxIterations`
  (3 writer calls at cap 3); a "done" verdict stops after one run. So: wrap `AIJudgeLoopEvaluator` in a small
  `LoopEvaluator` that catches every non-cancellation exception and returns `LoopEvaluation.Stop()` (fail open, like
  `JudgeAsync`), and rely on the small `MaxIterations` as the hard bound. (The fake judge returned a bare marker with no
  gap text, hence "<unknown>" in the feedback; a real judge's structured gap analysis replaces it.)
- **f. Concurrent join is clean.** Two looping agents in `BuildConcurrent`, as `FinalResult` joins them: one answer per agent,
  the LAST draft (`[AgentA]: A draft2`, `[AgentB]: B only`), no feedback text.

Step 1 (baseline) is NOT run: it needs live models and only the user can run it. The loop ships OFF by default, so the
build proceeds; the baseline is the check on whether to turn it on.

## Divergences from the plan (as implemented, 2026-11-08)

- **`ExcludeOnBehalfOfMessages` is `false`, not `true`.** The Step 2 spike (b) decided `true`, but the first integration test
  failed with the team result "draft onedraft two": with `true`, drafts whose updates carry no `MessageId` merge into one
  assistant message in the workflow output (re-measured in the spike: Sequential and Concurrent). With `false` the user-role
  feedback separates them. So `FleetEventMapper` now skips updates authored `FleetSelfReview.LoopAuthorName` instead.
  Recorded in ADR 0119 and a gotcha.
- **`SelfReviewMaxIterations` counts runs, first included**: default 2 (one fix pass), below **2** is off. The plan said "below 1
  means off" while describing a cap on iterations; `LoopAgent.MaxIterations` is the number of runs of the wrapped agent.
- **Events carry no `attempt`.** Fix rounds re-run the same wrapped agents, so an attempt captured at wrap time goes stale;
  `selfreview.iteration` and `selfreview.failed` carry `teamId`, `agent`, `iteration`. A `selfreview.failed` event was added
  (the plan only listed the iteration event), for the fail-open path.
- **Test mapping changes.** The integration tests drive the judge with the fake's existing structured AI judge
  (`FakeChatClient.JudgeUnsatisfiedMarker`, ADR 0082), not scripted verdict replies; "stop cancels" and "budget cancel stops the
  loop" became one cancellation test on the judge guard plus a budget-accounting test, and event-text truncation is `Cut`'s
  (already covered), not a new test. Added: `Runner_ASatisfiedJudge_TheAgentRunsOnce`, the legacy-record test, and a
  mapper assertion (no feedback in `agent.text`). The Staged multi-agent join has a runner test (`Runner_StagedLastStage_JoinsTheLastDraftOfEachAgent`, added after the critic pass), as does a fix round re-running the same wrapped agent (`Runner_FixRound_ReRunsTheWrappedAgent_AndLoopsAgain`) and a no-verdict judge (`Loop_AJudgeWithoutAVerdict_IsBoundedByTheCap`). `RunAgain_RepeatsSelfReview` in `FleetTaskServiceTests` was dropped: Run again is client-side (the page resubmits the task), so the server side is the task JSON carrying `selfReview` (`FleetApiTests`) and the page side is a source-text guard in `FleetPageAssetsTests`.
- **Test double.** `FleetRunHarness` gained `selfReview` and `defaultClient` parameters (before `answers`; all callers pass
  `answers` by name).
- **Step 1 (baseline) was NOT run** (needs live models; the user runs it). The loop ships off by default, so the build went ahead.
- No `sw.js` change (as planned: `/fleet` is outside `SHELL`). No `Directory.Packages.props` change (no new package).
- **Critic pass on the diff (2026-11-08)** found no blocking issue; it asked for the three tests above, a stricter guard
  (`FleetSelfReview.CanLoop` checks `agent is ChatClientAgent` as well as the `copilot` model prefix) and a rewrapped doc line,
  all done.
