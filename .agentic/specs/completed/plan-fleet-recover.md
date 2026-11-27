# Plan: Recover an interrupted fleet task

planned-against: main @ 7103630

## Overview

An interrupted fleet task (the app restarted mid-run) offers only **Run again**: a new workspace, only the attached `inputs/`
files copied, no earlier results, and a brand-new planner call. Everything the run produced is still in storage — the
workspace files, the canvas pages, and the event log with the accepted team plan, each agent's text and the todo list — but
nothing reads it. Add **Recover**: start a new task that shares the dead task's workspace and canvas, rebuilds the team
the user already approved (no planner call), and hands the agents a bounded digest of what was done so they continue
instead of redo. Why now: deploys restart the single replica, so long tasks get interrupted routinely.

## Affected areas (verified on the tree)

- `src/Agent42.Agents/Fleet/FleetTaskRecord.cs` — a trailing `RecoveredFrom` field; a `CanRecover` property next to `CanFollowUp`.
- `src/Agent42.Backend/Fleet/FleetTaskService.cs` (`FleetSubmit`, `SubmitAsync`), `FleetTaskService.Inputs.cs` (`PrepareInputsAsync`),
  `FleetTaskService.Run.cs` (`RunAsync`, `ComposeInputAsync`, `BuildRunAsync`, `SyncWorkspaceAsync`).
- New `src/Agent42.Backend/Fleet/FleetRecovery.cs` (event-log reader + digest + the stored plan), in the style of `FleetDigest.cs`.
- `src/Agent42.Backend/Fleet/FleetTeamRunner.Planning.cs` (`CreateTeamAsync`, `PlanSubmission`), `FleetModels.cs` (`FleetRun`).
- `src/Agent42.Backend/Fleet/FleetPrompts.cs` (one new prompt `recover`), `AgentApi.Fleet.cs` (`FleetSubmitRequest`, `SubmitFleetAsync`).
- `src/Agent42.Backend/wwwroot/fleet/fleet.js` (`runAgainCard`, the `task.started` / new event handler) — the fleet page is
  standalone and outside `SHELL`, so no `sw.js` `CACHE` bump (verify `FleetPageAssetsTests`).
- Tests: `tests/Agent42.Tests/FleetTaskServiceTests.cs`, `FleetApiTests.cs`, `FleetPageAssetsTests.cs`, `FleetDigestTests.cs` (style), new `FleetRecoveryTests.cs`.
- Docs: `docs/features/agent-fleet.md`, CLAUDE.md Feature-map row (fleet), ADR 0152 (next free; re-check at execution, `AdrNumberingTests`).

## Context & constraints (what the executor cannot cheaply re-derive)

**What is recoverable, and from where** (investigated; each claim has its code evidence):

- **Workspace files and canvas pages: yes.** `AfterAttempt` syncs the workspace after every attempt and `FinishAsync` syncs once more
  (`FleetTaskService.Run.cs`); a graceful shutdown still reaches `FinishAsync` (status `interrupted`). A hard kill loses only the
  current attempt's unsynced files. The canvas is stored outside the workspace prefix and per workspace id
  (gotcha "The fleet canvas lives OUTSIDE the workspace prefix"), so a task that shares the workspace id sees it (`canvas.LoadAsync`).
- **The accepted team plan: yes.** `plan.submitted` carries pattern, rationale, termination criteria, stages and every agent's
  `name`, `description`, `model` and full `yaml` (`FleetTeamRunner.Planning.cs`, `PublishSubmitted`), and `plan.accepted` follows
  once it passed validation, approval and the budget reservation. Both are structural events: they survive the log's text cap
  (gotcha "The fleet's event log coalesces streamed text"), and `FleetDigest` parses only `plan.submitted`, `verdict`, `attempt.failed`, `planner.failed` and `task.finished` (`FleetDigest.StructuralTypes`, `FleetDigest.cs:26`), so recovery needs its OWN type list (step 3). `plan.submitted` carries no depth, so identify the root team from `team.started` (`depth == 0`, `FleetTeamRunner.cs:41-49`). Pair a `plan.submitted` with the
  `plan.accepted` of the same `(teamId, attempt)`: a submitted-but-unaccepted plan (interrupted on the approval prompt) is NOT a plan to reuse.
- **Per-agent partial output: yes, best effort.** `agent.text` lines are coalesced per `(team, attempt, executor)` and past
  `Fleet:MaxEventLogBytes` text stops being persisted — recovery must work from structural events alone and say so.
- **Finished attempts and reviews: yes.** `attempt.result`, `verdict` (score, feedback), `todos.updated` (the WHOLE list per event, so the last one is the state).
- **Notes: NO today.** `record.Notes` is written only in `FinishAsync`, so a crash loses them (`workspace.Notes`, `Run.cs:330`).
  Step 2 saves them after each attempt. Limit (state it in the ADR): a task interrupted during its FIRST long attempt, the common case, still loses them.
- **In-flight agent conversations and sub-team results: NO.** An agent cannot be resumed mid-conversation, and a sub-team's result
  is a tool result inside its parent agent's run. Recover restarts the root team's agents with the digest in their input; it does not
  replay delegation. State this in the ADR as the limit of "where possible".

**Decisions made while planning** (no interview round was needed; the user's brief fixed scope; flag any you disagree with at approval):

1. **Recover is a NEW task**, like a follow-up: the interrupted record stays as history. It shares the workspace id (hence the canvas),
   and materializes the workspace first (gotcha "The fleet workspace sync DELETES stored files that aren't on local disk": `RunAsync` must
   materialize when `RecoveredFrom is not null`, same condition as a follow-up).
   **Everything about a shared workspace in `SubmitAsync` is keyed off the local `previous`** (`WorkspaceId`, `Notes`/`Documents`/`TextCopies`, `WorkspaceRoomErrorAsync(userId, previous, …)`,
   `RunAsync`'s materialize condition, and the catch block's `DiscardInputsQuietlyAsync(record, ownWorkspace: previous is null)` at `FleetTaskService.cs:133`, which DELETES the whole workspace when it
   thinks the task owns it and has inputs). So a recover resolves the interrupted task INTO `previous` (the shared-workspace role) and the `CanFollowUp` check applies to the `FollowUpOf` branch only;
   every existing sharing rule then holds unchanged and `ownWorkspace` is false. Never add a second variable that leaves `previous` null: a failed start would wipe the dead task's workspace.
2. **Scope: `interrupted`, `stopped` and `failed`** — the page already treats the three alike ("Didn't finish", `fleet.js` `runAgainCard`) and the machinery is
   identical. `done` keeps Follow up; `running` is refused. New trailing record field `RecoveredFrom` (a default-null JSON blob field, no migration);
   a recovered task is NOT given `FollowUpOf`, because `CanFollowUp` and the follow-up history read the parent's `Result`, which an unfinished task lacks.
3. **Team reuse = rebuild from the stored plan, no planner call**, for the root team's first attempt only. The seeded plan goes through the
   SAME validation, agent loading and budget reservation as a planner submission (so a model the user has since removed, or a tool that
   no longer exists, is rejected there), and skips only the approval prompt (the user approved this exact team already). On any
   rejection, or when no accepted plan exists, fall back to the normal planner path with the old plan described in the `previousAttempt`
   text. The new events on a recovered run are the ordinary ones, so the page draws the team unchanged.
4. **The recovered task is NOT an origin task** (no `OriginConversationId`), for the reason in gotcha "…a re-run it starts is NOT an
   origin task": the digest is built from model and tool output, and an origin task auto-approves Copilot shell and MCP writes (ADR 0115).
   A chat-started task that is recovered therefore asks for approvals on the page and does not report back to its chat.
5. **The digest is untrusted data** (agent text, judge feedback): framed as data like `FleetDigest`, each field flattened/capped, whole digest capped by a
   new option, and placed in the task's input (user-role), never a system prompt. No clarify phase on a recovery; if the original ran one, its
   `clarify.done` brief is part of the digest. Tool selection, token cap, planner model, `SelfReview` are inherited from the interrupted record.
6. **No double-run in one workspace needs no new check:** `Register` already refuses any second running task for the user (`FleetTaskService.cs:161-164`) and a same-workspace task is always the
   same user's. Rely on it; the test only pins that the existing refusal applies to a recover.
   **Inputs are not re-ingested:** a recovered task's own `Inputs` stays EMPTY (like a follow-up's, which lists only its new files), so `IngestInputsAsync` is skipped; the already-indexed `Documents`/`TextCopies`
   come from `previous`, and the digest/`AttachedFiles` text is derived from them. Re-ingesting would cost embeddings and could drop `Documents` of a task that died during ingest.
   `MaxTokens` is inherited, so a task stopped by the token cap will probably hit it again: say so in the ADR and the docs (changing the cap is out of scope).
   Known pre-existing hazard (record in the ADR, do not fix here): during a revision rollover two replicas overlap and the new one reads the
   old one's live task as `interrupted`; a recover then would run beside it. Single replica is load-bearing (CLAUDE.md rule 12).
7. **API shape:** `RecoverOf` trailing field on `FleetSubmitRequest` and `FleetSubmit` (same `POST /api/fleet/tasks`), mutually exclusive with
   `FollowUpOf` and `CopyInputsOf`; the request text is the interrupted task's request. New `recover` prompt in `FleetPromptSet` (editable
   like `followup`), NOT added to the dream's `AllowedKeys` (`FleetPromptProposals`).
8. Governing ADRs: 0097 (interrupted "cannot be resumed" — this ADR amends that: still no resume, but a recovery), 0101 (workspace files),
   0102 (canvas), 0115 (origin), 0117 (todos), 0118/0119 (clarify, self-review), 0124 (digest style), 0135 (resolved options). Gotchas to
   read first: the fleet entries listed above plus "A fleet task's `interrupted` status is computed on read" and "The fleet's run path reads the task's RESOLVED options".

## Steps

0. **ADR 0152 via `/decide`:** Recover (new task sharing workspace, plan rebuilt from the log without planner/approval, bounded
   untrusted digest, not an origin task, limits: no mid-agent resume, no sub-team replay, notes). Amends 0097. Add to the fleet Feature-map row.
1. **Record + service contract.** `FleetTaskRecord.RecoveredFrom` (trailing, default null) and `CanRecover` (status interrupted/stopped/failed);
   `FleetSubmit.RecoverOf`; a NEW helper `ResolveRecoveryAsync` (own task only, `CanRecover`, exclusive with `FollowUpOf`/`CopyInputsOf`) returns the interrupted record to be used AS `previous` (decision 1), keeping `SubmitAsync` under S138
   (it is ~88 physical lines now, `FleetTaskService.cs:57-145`); it inherits request, tools, model, cap, self-review, shares `WorkspaceId`, and takes `Notes`/`Documents`/`TextCopies` from `previous` as a follow-up does
   (`FleetTaskService.cs:112`), with its own `Inputs` empty (decision 6). `RunAsync` already materializes for `previous is not null`. `FleetSubmit` has 10 positional parameters (`FleetTaskService.cs:11-14`) and
   `RecoverOf` makes the 11th, exactly S107's ceiling: add nothing else to it. Verify: `dotnet test --filter FleetTaskServiceTests`.
2. **Notes survive a crash.** After each attempt (`AfterAttempt` in `BuildRunAsync`), save the record with the workspace's current `Notes`
   (assign `task.Record` first, then save, the ordering rule from `ApplyLessonsAsync`, `Run.cs:189-195`; the existing `AfterAttempt` closure captures the stale `record` local, `Run.cs:173`, so use `task.Record`). Verify: a test that kills a run after an attempt and
   reads the notes from the stored record.
3. **`FleetRecovery` reader.** Reads the interrupted task's log once with its OWN type list (not `FleetDigest.StructuralTypes`): `team.started` (root = depth 0), `attempt.started`, `plan.submitted`, `plan.accepted`,
   `agent.done`, `attempt.result`, `verdict`, `todos.updated`, `clarify.done`, plus `agent.text` for each agent's last text (may be absent past the log cap). Bounded like `FleetDigest.ReadStructuralAsync`. Keeps: the last accepted root-team plan, `attempt.result`s, last verdict, last todo list, `clarify.done` brief, per-agent
   done/not-done. Produces `(FleetTeamPlan? Plan, string Digest)`. Pure and unit-testable against synthetic event lines. Verify: new `FleetRecoveryTests`.
4. **Seed the plan.** `FleetRun.SeedPlan`; in `CreateTeamAsync` the root team's first attempt consumes it exactly once (guard against a second call or a sub-team) and
   goes through `PlanSubmission`'s validate/load/reserve path without the approval prompt (build that into the extracted half, not a flag on the planner-facing `SubmitAsync`; publish `plan.submitted` with `needsApproval=false`,
   or the page shows "awaiting approval" until `plan.accepted`, `fleet.js:257,269`). The seed must NOT be consumed by a later depth-0 "new team" round, which calls `CreateTeamAsync` again (fix rounds skip it, `FleetTeamRunner.cs:99`); rejection falls back to the planner with the old plan in `previousAttempt`. Extract
   the post-parse half of `SubmitAsync` into a method both paths call rather than copying it. Verify: tests with the fake planner client asserting the planner is NOT called on
   the happy path and IS called after a rejected seed. *Depends on 3.*
5. **Compose the input.** `ComposeInputAsync` for a recovered task renders the new `recover` prompt (request, digest, workspace files, canvas description) instead of `followup`/the bare
   request; `task.started` carries `recoveredFrom`; one new event `recover.prepared` (`from`, `planReused`, `agentsDone`, `files`) so the page can say what was reused. Add the prompt to
   `FleetPrompts` with the usual description/placeholders. Verify: prompt tests + `FleetTaskServiceTests`. *Depends on 3.*
6. **API.** `FleetSubmitRequest.RecoverOf`, passed through `SubmitFleetAsync`. Verify: `FleetApiTests` (success, other user's task 404/409, done task refused, with `FollowUpOf` refused).
7. **Page.** `runAgainCard` becomes a two-button card: **Recover** (primary; calls `submitTask` with `recoverOf`) and **Run again** (unchanged); the task header and thread grouping treat
   `recoveredFrom` like a parent link (`↳ #n`, listed under the chain's first task); handle `recover.prepared` in the page's event-handler table (`fleet.js:298` style). No existing guard enforces that every published event type is handled (searched `tests/`), so add a source-text guard in
   `FleetPageAssetsTests` that `fleet.js` handles `recover.prepared` and offers the Recover button.
   Verify: `FleetPageAssetsTests`, and run the page against DevHost if DevHost can seed an interrupted task (otherwise state that
   the page was not exercised in a browser). *Parallelizable with 4 and 5 after step 6.*
8. **Docs + close out.** `docs/features/agent-fleet.md` (Recover next to Follow up and the "interrupted" sentence, line 21), fix the stale claim in the gotcha "A fleet task's `interrupted` status is computed on read"
   (`.agentic/gotchas.md` ~4228-4240: "An interrupted task can't be followed up; the page offers 'Run again'"), `/gotcha` entries for anything found, `/sync-docs`, full `dotnet test`,
   `dotnet format --verify-no-changes`, move the spec to `completed/`.

## Acceptance criteria → test mapping

| AC | Proof | Lives in |
|----|-------|----------|
| An interrupted/stopped/failed task of mine can be recovered; done, running, other user's, unknown are refused | `Recover_RefusedUnlessUnfinishedAndOwn` | `FleetTaskServiceTests.cs` |
| The recovered task shares the workspace and records `RecoveredFrom`; the original stays interrupted | `Recover_SharesWorkspace_KeepsOriginal` | `FleetTaskServiceTests.cs` |
| A recover is refused while any task of the user runs (existing `Register` rule) | `Recover_RefusedWhileATaskRuns` | `FleetTaskServiceTests.cs` |
| A failed recover start keeps the shared workspace and canvas intact | `Recover_FailedStart_KeepsWorkspace` | `FleetTaskServiceTests.cs` |
| A seeded plan is used once, never by a later depth-0 new-team round or a sub-team | `Recover_SeedConsumedOnce` | `FleetTaskServiceTests.cs` |
| A recover of a task with attached files does not re-ingest or wipe `Documents` | `Recover_DoesNotReingest_KeepsDocuments` | `FleetTaskServiceTests.cs` |
| Files and canvas pages written before the interruption are visible to the recovered run | `Recover_SeesWorkspaceFilesAndCanvas` | `FleetTaskServiceTests.cs` |
| The accepted plan is rebuilt from the log with no planner call and no approval prompt | `Recover_ReusesAcceptedPlan_WithoutPlannerOrApproval` | `FleetTaskServiceTests.cs` |
| A submitted-but-unaccepted plan, a rejected seed or an empty log falls back to the planner | `Recover_FallsBack_ToPlanner` (theory: 3 cases) | `FleetTaskServiceTests.cs` |
| The digest holds last result, verdict feedback, todos, per-agent output, bounded and framed as data; works with text events absent | `Digest_*` | `FleetRecoveryTests.cs` |
| Notes written before a crash are in the stored record | `Notes_SavedAfterEachAttempt` | `FleetTaskServiceTests.cs` |
| A recovered task is not an origin task and never clarifies | `Recover_IsNotAnOriginTask_NoClarify` | `FleetTaskServiceTests.cs` |
| API: `recoverOf` works; exclusive with `followUpOf`/`copyInputsOf` | `Submit_RecoverOf_*` | `FleetApiTests.cs` |
| The page offers Recover on unfinished tasks and handles `recover.prepared` | `FleetJs_OffersRecover_AndHandlesItsEvent` (new source-text guard) | `FleetPageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

- `coverage_new_code_pct_min`: 80 · `cognitive_complexity_max`: 15 (S3776) · `method_lines_max`: 80 (S138) · `method_params_max`: 11 (S107)
- `e2e_failing_tests_max`: 0 (Playwright smoke jobs; this plan adds no e2e spec, a Recover browser spec is out of scope)
- `dependencies.vulnerable_packages`: 0 — no package is added.
- `SubmitAsync` and `FleetSubmit` are already long/wide: do not push either over S138/S107 (split rather than add a 12th parameter; `FleetSubmit` is a record with trailing defaults).

## Out of scope

Resuming an agent mid-conversation; replaying or re-injecting sub-team results; changing the model, token cap or tools at recovery time; an addendum
instruction on the recovery; recovering a `done` task (that is Follow up); automatic recovery after a restart; fixing the rollover-overlap
`interrupted` misread; a browser e2e spec for the fleet page; the analyzer's `rerun_fleet_task` (unchanged).

## Review

Critic verdict on the first draft: sound design, 3 high, 5 medium, 3 low. Every finding was checked against the code (items 1, 2 and 6 re-read by hand) and **accepted**:

- HIGH failed start wipes the shared workspace (`DiscardInputsQuietlyAsync(ownWorkspace: previous is null)`): **accepted**, decision 1 now resolves a recover into `previous`; new AC `Recover_FailedStart_KeepsWorkspace`.
- HIGH named guard test `FleetJs_HandlesEveryPublishedEventType` does not exist: **accepted**, step 7 adds a source-text guard instead.
- HIGH "`FleetDigest` already parses them" was false for four of the event types: **accepted**, step 3 has its own type list and the root team comes from `team.started`.
- MED approval must be built into the extracted half; seed must not feed a later depth-0 new team: **accepted**, step 4 + `Recover_SeedConsumedOnce`.
- MED inheriting `Inputs` would re-ingest: **accepted**, decision 6: the recovered task's `Inputs` stays empty; `Recover_DoesNotReingest_KeepsDocuments`.
- MED "refuse while workspace busy" is redundant with `Register`: **accepted**, decision 6 and the AC reworded.
- MED stale gotcha line and ADR 0097 wording: **accepted**, step 8 and step 0.
- MED `SubmitAsync` size / `FleetSubmit` at the S107 ceiling: **accepted**, helper `ResolveRecoveryAsync` named in step 1.
- LOW notes only survive attempt boundaries; token cap inherited; thin evidence: **accepted**, limits stated in decisions and for the ADR; evidence paths added.

## Divergences from the plan (as implemented)

- **Follow-up and recover resolution share one helper.** `ResolvePreviousAsync` (not `ResolveRecoveryAsync`) resolves both `FollowUpOf` and `RecoverOf`
  into `previous`; moving the follow-up lookup out of `SubmitAsync` was needed to keep S3776 at 15. A recover's request, model, cap, tools and
  self-review come from the interrupted record, so the request's `Task` may be empty. A cap above the operator's current limit is refused by the normal cap check.
- **New option `Fleet:MaxRecoveryDigestChars`** (default 12,000), on `FleetOptions` only, not one of the user's overridable settings.
- **`FleetRecovery.Build` takes raw log lines** (`FleetAnalysis.Event` is internal and the repo has no `InternalsVisibleTo`), and `ReadAsync` wraps the store read.
- **Tests:** the service tests live in `FleetTaskServiceTests.Recover.cs` (the class is now `partial`) rather than the 1,400-line file; `Recover_SeedConsumedOnce`
  runs on `FleetRunHarness` (new `seedPlan` parameter) against `CreateTeamAsync`; `FaultyFleetBlobs` is a new test double (failing reads and writes on demand) used by
  `Recover_FailedStart_KeepsWorkspace`, `Notes_SavedAfterEachAttempt` and a `FleetRecovery` test. Most recover tests run a REAL task and rewrite its record as
  interrupted, so the log, workspace and canvas are the real ones.
- **`recover.prepared.planReused`** says an accepted plan was found; whether the run could rebuild it shows in the `plan.*` events (a rejected seed logs `plan.rejected`, then the planner runs).
- **Not exercised in a browser:** DevHost has no way to seed an interrupted task, so the page change is covered by source-text guards only (`FleetJs_OffersRecover_AndHandlesItsEvent`,
  `FleetJs_TaskList_GroupsFollowUpsIntoThreads`). No `sw.js` `CACHE` bump: `/fleet` is outside `SHELL` (`ServiceWorker_LeavesFleetOutOfTheShell`).
- **Gotcha added:** "A fleet recover is resolved INTO `previous`…". The stale "Run again" line in the `interrupted` gotcha is fixed.
