# Plan: the fleet grills the user on the task prompt before it plans

planned-against: main @ a5948c7

## Overview

Today a fleet task starts planning the moment the user presses Run; a vague prompt becomes a vague team and a wasted
token budget. The user wants the agent to **interview them about the task, one question at a time, until the
requirements are clear**, and only then plan and run. This adds a "clarify" phase at the start of a page-started
fleet task, reusing the fleet's existing question slot (`prompt` events, `POST .../answer`), so no new endpoint and no
new page chrome is needed beyond a checkbox and a read-only "Requirements" block.

Design choices made by the planner (user was not interviewed; each is cheap to reverse, redirect if wrong):

1. **On by default for the composer, off for everything else.** The composer gets an "Ask me questions first"
   checkbox, checked by default and remembered in `localStorage`. The API field `clarify` defaults to `false`, so
   `/api/fleet/tasks` callers and tests keep today's behaviour. A task started from a chat (`start_fleet_task`, ADR
   0115, `OriginConversationId` set, `AutoApprove`) never clarifies: nobody watches it, and the interaction
   would decline.
2. **Model-driven loop with two tools**, shaped like `PlanSubmission` in `FleetTeamRunner.Planning.cs` (the result
   arrives via tool calls so providers that ignore structured output still work): `ask_user(question, choices?, freeTextHint?)`
   blocks on `IFleetInteraction.AskAsync` and returns the answer; `requirements_clear(brief)` ends the phase.
3. **Hard stops so it cannot grill forever**: `Fleet:MaxClarifyingQuestions` (default 8). Every question also carries
   a fixed "Enough, start the task" choice. Past the cap, or after that choice, `ask_user` returns "no more
   questions; call requirements_clear now, state your assumptions". If the model ends without calling
   `requirements_clear`, the original request is used unchanged (fail open, never fail the task).
4. **The brief replaces the request as the planner's input only.** `FleetTaskRecord.Request` stays the user's own words
   (list, Run again, follow-up history); the refined brief (original request + decisions + stated assumptions) is what
   `RunAsync` feeds to `FleetTeamRunner`/`followup` prompt. It is kept as an event (`clarify.done`), not a new
   persisted field beyond the `Clarify` flag.
5. **Follow-ups clarify only the new request**, with the previous request as one line of context.
6. **The clarifier sees the attached files list** (`AttachedFiles(record)`) so it does not ask what a file answers. It
   has no tools besides the two above (no search, no MCP): out of scope.
7. Tokens spent count against the task's token budget (it runs on `run.DefaultClient`, already wrapped by
   `FleetBudgetChatClient`).

## Affected areas (verified against the tree at a5948c7)

- `src/Agent42.Backend/Fleet/FleetTaskService.cs` — `FleetSubmit` record, `SubmitAsync` builds `FleetTaskRecord`.
- `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs` — `RunAsync` (input composition: `previous is null ? record.Request : FollowUpInputAsync(...)`), `FollowUpInputAsync`, `BuildRunAsync`.
- `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs` — `FleetSubmitRequest`, `SubmitFleetAsync` (two submit routes share it).
- `src/Agent42.Agents/Fleet/FleetTaskRecord.cs` — plain `init` properties on a Blob-JSON record; add `Clarify` (default false), no `[Id]`.
- `src/Agent42.Backend/Fleet/FleetPrompts.cs` — new editable `clarify` definition (default text + placeholders).
- `src/Agent42.Backend/Fleet/FleetOptions.cs` — `MaxClarifyingQuestions`.
- NEW `src/Agent42.Backend/Fleet/FleetClarifier.cs` — the loop (class shaped like `PlanSubmission` / `FleetTodos`).
- `src/Agent42.Backend/wwwroot/fleet/fleet.js`, `index.html`, `fleet.css` — checkbox, `clarify.*` events, Requirements block.
- Tests under `tests/Agent42.Tests/`: `FleetTaskServiceTests.cs`, `FleetTeamRunnerTests.cs` (scripted-client pattern), `FleetPageAssetsTests.cs`, `FleetApiTests.cs`, prompt tests (grep `FleetPrompts.Definitions` in tests for key-count pins).
- Docs: new ADR 0118 (amends 0097), `.agentic/decisions/0097-...md` `amended_by`, `CLAUDE.md` feature-map row for the agent fleet (add 0118), `.agentic/decisions/README.md` index.

## Context & constraints

- **Reuse the question slot, don't invent a channel.** `FleetQuestions` (`src/Agent42.Backend/Fleet/FleetQuestions.cs`) serialises questions per task, replays them as persisted `prompt` events, and the page already renders choices + free text (`fleet.js` `renderPrompt`, `answer`). Stale-answer protection is by question id.
- **Auto tasks decline every question** (`DecliningFleetInteraction`, ADR 0115): the clarifier must be skipped for `AutoApprove`, otherwise it would burn tokens on declined answers.
- **Events other than text are persisted and replayed** (gotcha "fleet event log coalesces", `FleetEventLog.cs`): `clarify.started` / `clarify.done` survive reload; the UI must rebuild from them. `prompt` while the page is closed just waits (task shows as running with an open question) — same as the plan approval today.
- **Cancellation:** a stop cancels the open question (`FleetQuestions`) → `OperationCanceledException`; `RunAsync` already treats that as Stopped. The clarifier must not swallow it (the planner's catch uses `when (ex is not OperationCanceledException)`).
- **`/fleet/` is not in the service worker SHELL**, so no `sw.js` CACHE bump (`FleetPageAssetsTests`, ADR 0116). Do not touch `wwwroot/js/`.
- **Never `GetUserId()`/Orleans here**; the only persistence change is one defaulted bool on a Blob-JSON record (ADR 0097 decision 2: no migration; not grain state, so no `[Id(n)]`).
- **Two guards, one authority:** `SubmitAsync` forcing `Clarify` false for chat-origin tasks is the authority (the record is truthful); the `AutoApprove` skip in the run is defence in depth. Keep both.
- **Cancellation token:** the clarifier (and its `AskAsync` calls) must use `task.Budget.Token`. `RunAsync` only swallows `OperationCanceledException` `when (task.Budget.IsCancelled)`, and `StopAsync` cancels through the budget.
- **Complexity:** S3776 ≤ 15 is a build error. Keep the loop in its own class with small methods.
- Governing ADRs: 0097 (fleet), 0115 (auto tasks), 0101 (attached files), 0117 (the latest fleet ADR; copy its shape). Grep `.agentic/gotchas.md` for "fleet" entries before editing.
- Exemplar shape: `FleetTodos` (tools as `AIFunction`s with caps that refuse instead of truncate) and `PlanSubmission` (planner agent built with `run.DefaultClient.AsAIAgent(new ChatClientAgentOptions{...})`, tools with defaulted args so a bad call returns a message instead of throwing).

## Steps

1. **Record + submit plumbing** (`FleetTaskRecord.Clarify`, `FleetSubmit.Clarify`, `FleetSubmitRequest.Clarify`, `SubmitFleetAsync` pass-through, `SubmitAsync` sets it **false** when `OriginConversationId` is set). Verify: `dotnet test --filter FleetTaskServiceTests` — new tests: flag stored; chat-origin forces false.
2. **Options + prompt**: `FleetOptions.MaxClarifyingQuestions = 8` (`FleetOptions` has no validation; the clarifier clamps to ≥ 1 itself); `clarify` prompt definition with placeholders `{{request}}`, `{{files}}`, `{{maxQuestions}}`. Prompt text: ask ONE question at a time, only what changes the plan, offer choices when the answer space is small, never ask what the request or attached files already say, stop when requirements are clear, then call `requirements_clear` with a brief that restates the goal, decisions, constraints, deliverables and assumptions. Verify: `Clarify_Prompt_ListedAndRendersPlaceholders` (no existing key-count pin was found).
3. **`FleetClarifier`** (new): ctor `(FleetRun run, string request, string files, string? previousRequest)`; `RunAsync(CancellationToken)` (callers pass `task.Budget.Token`) returns the brief (or the request on fail-open). Publishes `clarify.started {maxQuestions}` and `clarify.done {brief, questions, skipped}`. `ask_user` appends the fixed "Enough, start the task" choice, counts questions, handles the cap and the skip. Verify: `dotnet test --filter FleetClarifier` with a scripted chat client: asks 2 questions then clears; cap reached; skip choice; model never clears → original; cancellation propagates.
4. **Wire into `RunAsync`** (after `BuildRunAsync`, before the input is composed; skip when `!record.Clarify || task.AutoApprove`). **Do not grow `RunAsync`** (S3776 ≤ 15): extract the input composition, clarify included, into a helper such as `ComposeInputAsync(task, record, previous, workspace, run)`. Pass the brief into both the first-run input and `FollowUpInputAsync` (give it a `request` parameter instead of reading `record.Request`). Verify: a service-level test through the in-memory store: submit with `Clarify`, answer through `AnswerAsync`, assert the planner received the brief and `Record.Request` is unchanged; a stop during a question ends as `Stopped`.
5. **Frontend** (parallelizable with 3–4 once the event names are fixed): composer checkbox (default checked, `localStorage` try/catch), send `clarify`; handle `clarify.started` (label the banner "The fleet is clarifying your task"), `clarify.done` (store `S.clarified`, render a collapsible "Requirements" block in Details); reset `S.clarified` on task switch like `S.prompt`. Verify: `FleetPageAssetsTests` (checkbox present, event handlers named) + DevHost manual check (`dotnet run --project tools/Agent42.DevHost`, `/fleet/`; the canned LLM will not call the tools, so verify the fail-open path there and the loop in unit tests — say so in the final report; a live check with a real model is a post-deploy item).
6. **Docs (same MR)**: ADR 0118 via `/decide` (alternatives: page-side pre-submit chat; planner-asks-via-a-tool; always-on. Note: no new egress — same model, same provider; amends 0097), `amended_by` in 0097, CLAUDE.md fleet row, decisions README index; `/gotcha` only if a trap is found while building.

Steps 1→2→3→4 are sequential (shared files); 5 can run alongside 3–4 in a worktree if desired, but the whole plan is small enough to run sequentially.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| `clarify` defaults to false; stored when true | `Submit_ClarifyFlag_IsStored` / default case | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| A chat-started task never clarifies | `Submit_FromChat_ForcesClarifyOff` | `FleetTaskServiceTests.cs` |
| Questions are asked one at a time through the question slot and the answers reach the model | `Clarifier_AsksThenClears_ReturnsBrief` | new `tests/Agent42.Tests/FleetClarifierTests.cs` |
| Question cap ends the interview | `Clarifier_CapReached_TellsModelToClear` | `FleetClarifierTests.cs` |
| "Enough" choice ends the interview | `Clarifier_SkipChoice_StopsAsking` | `FleetClarifierTests.cs` |
| A model that never clears falls back to the original request | `Clarifier_NoClear_FallsBackToRequest` | `FleetClarifierTests.cs` |
| Stop during a question stops the task (cancellation not swallowed) | `Clarifier_Cancelled_Propagates` + service-level `Run_StopDuringClarify_IsStopped` | `FleetClarifierTests.cs`, `FleetTaskServiceTests.cs` |
| The planner gets the brief; the record keeps the user's words | `Run_Clarified_PlannerGetsBrief_RecordKeepsRequest` | `FleetTaskServiceTests.cs` |
| Follow-up clarifies only the new request | `Run_FollowUpClarified_UsesBriefInFollowUpInput` | `FleetTaskServiceTests.cs` |
| `clarify.started`/`clarify.done` are persisted and replayed | `Events_Clarify_ReplayedAfterReload` | `FleetTaskServiceTests.cs` |
| Page has the checkbox and handles the events | `FleetPage_HasClarifyCheckbox_AndHandlers` — keep it to a minimal presence pin (commit 68a0770 pruned source-text tests; behaviour is covered by the service tests) | `tests/Agent42.Tests/FleetPageAssetsTests.cs` |
| The `clarify` prompt is listed, editable, and renders its placeholders | NEW `Clarify_Prompt_ListedAndRendersPlaceholders` (API listing via `GET /api/fleet/prompts` + `FleetPrompts.Render`) | `tests/Agent42.Tests/FleetApiTests.cs` (next to `Prompts_SetResetRoundTrip_PerUser`) |
| `clarify` accepted on both submit routes | `Submit_Clarify_OnJsonAndMultipart` | `tests/Agent42.Tests/FleetApiTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `coverage_new_code_pct_min`: 80 — `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff)
- `cognitive_complexity_max`: 15 — S3776 at `dotnet build Agent42.slnx`
- `vulnerable_packages`: 0 (no package is added)
- Also: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- Giving the clarifier tools (document search, web, MCP) or letting it read attached files' content.
- Clarifying mid-run, or per sub-team (planner/agents asking the user during execution).
- A separate "interview" chat UI, voice, or the Android app.
- Making the interview default-on for API callers, or an operator/Config switch (the per-task checkbox + the editable prompt are the controls).
- Persisting the interview transcript beyond the event log.
- Chat-started fleet tasks (ADR 0115): they never ask.

## Review

Critic verdict: no blocking findings, no ADR contradiction. All six findings accepted and applied above:

1. (medium) cancellation token unnamed — now `task.Budget.Token`, in Context and step 3.
2. (medium) `RunAsync` complexity — step 4 now requires extracting an input-composition helper.
3. (medium) unverifiable prompt/options tests — replaced by a concrete `FleetApiTests` test; options validation clause dropped (none exists).
4. (low) source-text frontend test — kept as a minimal presence pin, noted in the AC table.
5. (low) double guard — Context now says `SubmitAsync` is the authority, the `AutoApprove` skip is defence in depth.
6. (low) `FleetTaskRecord` bullets — replaced with the known path and shape.

## Divergences from the plan (implementation, 2026-11-08)

- **Prompt placeholders:** the `clarify` prompt has one placeholder, `maxQuestions`, not `request`/`files`. The request, the
  attached-files list and a follow-up's earlier request go in the interviewer's user message, so a user's prompt edit
  can't drop them.
- **`clarify.done` payload** is `{brief, questions, startedNow, cleared}` (the plan said `skipped`); the page uses
  `cleared` and `questions`.
- **Run again** repeats the record's `Clarify` flag (the brief is not stored, so it re-asks); the plan did not say.
- **`.agentic/decisions/README.md`** has no per-ADR index, so nothing was added there (CLAUDE.md's fleet row and ADR
  0097's `amended_by` were).
- **Tests added beyond the plan:** `SubmitWithFiles_Multipart_CarriesClarify` (the critic's multipart gap), choice-list,
  cap-below-one, empty-question, overlong-brief and files/previous-request clarifier tests. The fake chat client got
  `clarifyCalls`/`ClarifyInputs` and `ScriptedFleetInteraction` got `Choices`/`Cancel`.
- **Known cosmetic gap (accepted):** a task stopped mid-interview replays `clarify.started` without `clarify.done`; the
  page only shows the "Before it starts…" label while a question is open, so nothing visible is wrong.
- **Not verified live:** the DevHost canned client never calls the interviewer's tools, so only the fail-open path can
  be seen there; the interview itself is covered by unit tests. A real-model check is a post-deploy item.
