planned-against: main @ 66883b6

# Plan: push notifications when a fleet task finishes, is aborted, or is stuck

## Overview

Today a fleet task started from `/fleet/` pushes only when it opens a question (`FleetTaskService.NotifyNeedsInput`) or when
a model calls `send_push_notification` (ADR 0144). A task that finishes, fails, hits its token cap or hangs is silent: the
user has to open the page to find out. A task started from a chat already reports back as a background turn with the away
push (ADR 0115), so it needs no completion push. Add one generic, content-free push per terminal state and a "stuck" push,
with no duplicate of what chat-started tasks or the model already send.

## Affected areas

- `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs`: `FinishAsync` (terminal push after `NotifyOriginAsync`), `FlushLoopAsync` (the stuck check rides its existing 1 s tick).
- `src/Agent42.Backend/Fleet/FleetTeamRunner.Planning.cs:398`: the plan-decline `Cancel("stopped by the user")` switches to the shared constant.
- `src/Agent42.Backend/Fleet/FleetTaskService.cs`: `RunningFleetTask` (carries the push counter and the stuck state), `NotifyNeedsInput` (stays, reuse its shape).
- `src/Agent42.Backend/Fleet/FleetEventLog.cs`: expose the time of the last published event.
- `src/Agent42.Backend/Fleet/FleetPushTools.cs` + `FleetToolCatalog.cs` (`FleetToolContext`): count the model's own pushes into a per-task counter the service can read.
- `src/Agent42.Backend/Fleet/FleetOptions.cs`: one new operator option (`StuckAfter`).
- New `src/Agent42.Backend/Fleet/FleetTaskNotifier.cs`: the message texts, the rules (which status, which origin), the send. Keeps `FinishAsync` and `FlushLoopAsync` small (S3776 / S138).
- Tests: `tests/Agent42.Tests/FleetTaskServiceTests.cs` (+ a new `FleetTaskNotifierTests.cs`), `FleetPersonalToolsTests.cs`.
- Docs: new ADR 0154, `.agentic/gotchas.md`, `docs/features/` fleet page (find it by grep), `CLAUDE.md` feature-map fleet row (add 0154).

No JS change, so no `sw.js` `CACHE` bump (rule 3): `sw.js` already focuses or opens the payload `url`, and `/fleet/?task={id}` is the deep link ADR 0115 introduced.

## Context & constraints

Findings (each verifiable in code):

- `FinishAsync` computes the final status: `Interrupted` (shutdown), `Stopped` (`budget.IsCancelled`, reason in `CancelReason`: "stopped by the user" from `StopAsync`, or a token-cap reason), `Failed`, `Done`. It saves the record, completes the log, leaves the registry, then calls `NotifyOriginAsync` (`FleetTaskService.Run.cs:395`). The terminal push belongs right after it, on the same "after the final save" rule.
- `NotifyOriginAsync` skips `Interrupted` and tasks without an origin conversation. A chat-started task gets the result as a background turn, and that turn sends the away push (`AgentGrain.RunBackgroundTurnAsync`, `AgentGrain.cs:572`). So the generic terminal push must skip tasks with an origin, or the user gets two pushes.
- `Interrupted` never notifies: ADR 0115 decision, and gotchas "interrupted is computed on read, nothing scans at startup". A graceful shutdown cancels with reason "the app is shutting down", the replica is going away, so a push there is not reliable and not worth it. Keep it silent.
- A user stop is the user's own action: no push. A token-cap stop is not: push. There are TWO user-stop sites: `StopAsync` (`FleetTaskService.cs:250`) and the user declining "Run this team?" (`FleetTeamRunner.Planning.cs:398`), both `Cancel("stopped by the user")`. `FleetBudget.Cancel` is first-reason-wins and `final.Reason` carries `CancelReason`, so the notifier discriminates on `record.Reason` against ONE shared constant that both sites use (`FleetBudget.cs:99`).
- The sender (`WebPushNotificationSender`) never throws, is bounded by a 5 s budget and fans out to all devices; `PushMessage` has a trailing optional `Url` (ADR 0144). The chat's rule "the model already pushed this turn, the generic one yields" is `turn.PushesSent == 0` (ADR 0075); the fleet equivalent needs a counter `FleetPushTools` writes and the service reads. `FleetPushTools._sent` is private and per instance; the catalog builds one instance per task.
- A question already pushes when it opens. "Waiting for your answer for a long time" is NOT stuck: the stuck check must be off while `FleetQuestions.HasOpenQuestion`.
- "Stuck" has no existing definition. `FleetEventLog` stamps every event with `_time.GetUtcNow()`, and a running task streams `agent.text` per token, so a long silence is a real stall. The log has no last-event accessor today. The 1 s `FlushLoopAsync` already runs for every running task on the injected `TimeProvider`, so the check needs no new timer or thread.
- Text is generic on purpose (ADR 0013, `NotifyNeedsInput` comment): no request text, no result text, no error message (an exception message can carry content). Title "Agent 42 fleet". The tap opens the task via `FleetPushTools.TaskUrl`.
- Single replica is load-bearing (rule 12): the stuck state lives in `RunningFleetTask`, in-process, same as the registry. Nothing to coordinate.

Decisions made by the planner (no interview; the user can redirect any of these):

1. Terminal pushes: Done ("finished"), Failed ("failed"), Stopped by a cap ("stopped, limit reached"). Not: user stop, interrupted.
2. Terminal push only for tasks without an origin conversation (chat-started tasks keep ADR 0115's turn push).
3. Skipped when the model already sent a push during the task (mirrors ADR 0075). A failed or stopped task is NOT skipped for that reason alone: the model's push most likely said something good-news. Rule: skip only on Done; failures always push.
4. Stuck = running, no open question, no event for `Fleet:StuckAfter` (default 10 minutes; `TimeSpan.Zero` turns it off). One push per quiet spell; re-armed when an event arrives; at most 2 per task so a flapping task cannot spam. Applies to chat-started tasks too (nobody is watching them, and the result push only comes at the end).
5. `StuckAfter` is operator-only (not one of the user-overridable ADR 0135 settings): adding a user setting is its own change.
6. No per-event user preference and no per-device filtering (out of scope below).

Governing records: ADR 0097 (fleet), 0115 (chat-started tasks, interrupted never notifies), 0144 (push url, `FleetPushTools`), 0028 and 0075 (push shape, "model pushed, generic yields"), 0013 (egress), 0135 (user overrides, why `StuckAfter` stays operator-only). Gotchas: "interrupted is computed on read", "Fleet personal tools are fleet-owned", the push gotchas by grep. Exemplars: `FleetPushTools` (a fleet push), `NotifyNeedsInput` (a service-side push), `FleetConversationNotifier` (a small port called after the final save).

## Steps

1. **Shared constants and the event-log accessor** (independent, parallelizable with 2).
   - `FleetEventLog`: add `LastEventAt` (set under `_gate` in `Publish`, initial value the construction time so a task with no events yet still ages).
   - A shared constant for the user-stop reason, used by BOTH sites that cancel with it (`FleetTaskService.StopAsync` and `FleetTeamRunner.Planning.cs:398`, the plan decline) and by the notifier's check.
   - Verify: a new test that `LastEventAt` moves on `Publish` (fake `TimeProvider`); the decline path is covered by `Stopped_ByPlanDecline_DoesNotPush` (see the AC table).
2. **Per-task push counter** (independent, parallelizable with 1).
   - A tiny `FleetPushCounter` (thread-safe count; agents of one task run concurrently). The SERVICE creates it on `RunningFleetTask` and sets it on the `FleetToolContext` it builds in `FleetTaskService.Run.cs:138` (a default created inside the context could never be read by the service). `FleetPushTools` takes it and increments on a successful send, replacing its private `_sent` so the cap and the count are one number.
   - Verify: existing `FleetPersonalToolsTests` push cases still pass, plus a test that the counter equals the sends.
3. **`FleetTaskNotifier`** (depends on 1, 2).
   - Pure rules + send: `NotifyFinishedAsync(FleetTaskRecord final, int modelPushes)` returns without sending for Interrupted, user-stop, an origin conversation, or Done with `modelPushes > 0`; otherwise one `PushMessage("Agent 42 fleet", <generic text by status>, "", FleetPushTools.TaskUrl(id))`. `NotifyStuckAsync(userId, taskId)` likewise. Texts as constants; never interpolate the request, result, reason or exception text.
   - Both methods catch everything from the send themselves (the sender should not throw; a faulty sender must not change how the task ended, and the stuck push is fire-and-forget, so an exception there would be an unobserved faulted task). Same defensive shape as `NotifyOriginAsync`. Logs the task id only, never text.
   - Built INSIDE `FleetTaskService` from its existing `push` parameter (`push is null` -> no notifier): no new registration, no new constructor parameter, nothing to break in a host without a sender (rule 7).
   - Verify: new `FleetTaskNotifierTests` with `RecordingPushNotificationSender`, one test per row of the AC table.
4. **Wire the terminal push** (depends on 3).
   - `FinishAsync` calls the notifier after `NotifyOriginAsync`, passing the counter's value (a plain object, it survives `task.Dispose()`, which only disposes the budget). The test helper `Service(...)` in `FleetTaskServiceTests.cs` gains a `push` parameter (and a `time` one for step 5).
   - Verify: `FleetTaskServiceTests` end to end: Done pushes once with the task url; Failed pushes; user stop and plan decline do not; chat-started does not.
5. **Wire the stuck push** (depends on 1, 3).
   - `FleetOptions.StuckAfter` (10 min). In `FlushLoopAsync`, after the existing per-tick work: if enabled, no open question (`task.Questions is FleetQuestions { HasOpenQuestion: true }` is the open case; a chat-started task has a `DecliningFleetInteraction`, never a question, so its check always runs, as `AnswerAsync` does the same cast) and `now - Log.LastEventAt >= StuckAfter` and not already fired for this quiet spell, fire the push (do not await it inside the tick: the tick must keep flushing; use the same fire-and-forget as `NotifyNeedsInput`, the sender is self-bounded). Re-arm when `LastEventAt` advances. Cap 2 per task. Keep the added code in a method on the notifier or a small helper so `FlushLoopAsync` stays under S3776 / S138.
   - Verify: `FleetTaskServiceTests` with a fake `TimeProvider` advanced past the threshold: one push, no second push while still quiet, a new event re-arms, an open question suppresses, `StuckAfter = 0` disables, a finished task stops checking (the flush loop is cancelled by `FinishAsync`).
6. **Docs** (after 4, 5): ADR 0154 (`/decide`): extends 0097, 0115, 0144, amends none; records the six decisions above, the egress note (generic text only, so no ADR 0013 extension), and the rejected alternatives (notify on user stop, notify on interrupted via a startup scan, a stuck timer per task, a user-overridable `StuckAfter`). `.agentic/gotchas.md`: one entry saying the terminal push is skipped for tasks with an origin (their background turn pushes) and that a user stop has TWO sites sharing one reason constant; and UPDATE the existing push-origins gotcha (around `gotchas.md:2131-2181`, "the file split is the documentation that these are two rules") to list the fleet terminal and stuck pushes. Add 0154 to ADR 0097's `amended_by` header, and a note in ADR 0115 decision 84's neighbourhood that "never notifies" means the conversation, not the generic fleet push (a chat-started task can still get a stuck push). Add `StuckAfter` to `OperatorOnlyFields_NotExposed` (`FleetSettingsServiceTests.cs`) to pin decision 5. In the ADR's known limits: a Copilot agent or a long tool run can emit no event for more than `StuckAfter`, so a false "stuck" is possible; the threshold is operator-tunable and 0 disables it. Update the fleet page and the `CLAUDE.md` fleet row ADR list (`/sync-docs`).
7. **Run the gates**: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, and `node .agentic/scripts/check-new-code-coverage.mjs` after a coverage run.

Steps 1 and 2 are parallelizable; the rest are sequential. If dispatched to `step-implementer` subagents, run 1 and 2 in separate worktrees.

## Acceptance criteria -> test mapping

| AC | Test | File |
|----|------|------|
| A task started from `/fleet/` that is Done sends exactly one push, generic text, url = `/fleet/?task={id}` | `Done_FromPage_PushesOnceWithTaskUrl` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| A Failed task pushes, text names no error content | `Failed_PushesGenericText` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| A task stopped by the token cap pushes; stopped by the user does not | `Stopped_ByCap_Pushes_ByUser_DoesNot` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| Declining the plan ("Run this team?" -> no) is a user stop: no push | `Stopped_ByPlanDecline_DoesNotPush` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| A throwing sender in the stuck path is swallowed (no unobserved fault) | `Stuck_ThrowingSender_Swallowed` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| An interrupted task never pushes | `Interrupted_NeverPushes` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| A chat-started task gets no terminal push (its turn pushes) | `OriginTask_NoTerminalPush` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| Done after the model pushed sends no generic push; Failed after the model pushed still does | `Done_AfterModelPush_Yields`, `Failed_AfterModelPush_StillPushes` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| The model's push count and the per-task cap are one number | `PushTool_CounterMatchesSends` | `tests/Agent42.Tests/FleetPersonalToolsTests.cs` |
| The push text never carries the request, the result or the reason | `Texts_ContainNoTaskContent` | `tests/Agent42.Tests/FleetTaskNotifierTests.cs` |
| A sender that throws does not change the task's final status | `ThrowingSender_DoesNotChangeStatus` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| Quiet for `StuckAfter` pushes once; stays quiet; a new event re-arms; capped at 2 | `Stuck_PushesOncePerQuietSpell_CappedAtTwo` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| An open question suppresses the stuck push; `StuckAfter = 0` disables it | `Stuck_NotWhileQuestionOpen`, `Stuck_ZeroDisables` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| `LastEventAt` advances on publish | `LastEventAt_AdvancesOnPublish` | `tests/Agent42.Tests/FleetEventLogTests.cs` (or the nearest existing event-log test file) |
| A chat-started task that goes quiet still gets the stuck push | `Stuck_AppliesToOriginTasks` | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- coverage_new_code_pct_min: 80 (`node .agentic/scripts/check-new-code-coverage.mjs`)
- cognitive_complexity_max: 15 (S3776, build error)
- method_lines_max: 80 (S138, build error)
- method_params_max: 11 (S107, build error)
- e2e_failing_tests_max: 0 (CI e2e jobs; no UI change here)
- vulnerable_packages: 0; no new package is added.
- Status of each is reported only from the commands' real output, never estimated.

## Out of scope

- A per-event or per-device notification preference (every event goes to every subscribed device, as all pushes do).
- A user-overridable `StuckAfter` (ADR 0135 territory).
- Notifying an `interrupted` task (needs a startup scan ADR 0097 rejected, and the replica is going down).
- Any change to the chat's away-reply push, to the chat-app home-chat delivery (ADR 0111) or to `sw.js`.
- Clearing a notification on other devices once the task page is open.
- Stuck detection for the dream, the analyzer, or anything that is not a fleet task.

## Review

Critic verdict: no ADR contradictions; 2 medium and 4 low findings, all accepted and fixed in this spec. Code claims it verified: `FinishAsync` order, the 1 s flush tick, first-reason-wins `Cancel`, the per-task `FleetPushTools` instance, the origin turn's away push, ADR 0115 "interrupted never notifies", 0154 free.

- Medium 1 (second "stopped by the user" site in `FleetTeamRunner.Planning.cs:398`): accepted, I confirmed it by grep. Added to Affected areas, step 1 and the AC table.
- Medium 2 (the "read the counter before dispose" gotcha was false, and the counter must be created by the service): accepted. The claim is gone; step 2 says the service creates it.
- Low 3 (DI wiring): accepted, the notifier is built inside `FleetTaskService` from its `push` parameter.
- Low 4 (stuck send exception handling): accepted, both notifier methods catch internally; test added.
- Low 5 (`HasOpenQuestion` needs the `FleetQuestions` cast): accepted, step 5 says so.
- Low 6 (doc debt, false-positive limit): accepted, step 6 names the push-origins gotcha, ADR 0097 `amended_by`, ADR 0115, `OperatorOnlyFields_NotExposed` and the known-limit line.

Rejected: none.

## Divergences from the plan (as implemented)

- Added `FleetStuckWatch` (in `FleetTaskNotifier.cs`) to hold the once-per-spell, re-arm and cap-of-2 rules; `RunningFleetTask` owns one. Re-arm and cap are tested at that unit level (`Watch_*` in `FleetTaskNotifierTests`); the service-level test `Stuck_PushesOncePerQuietSpell` covers the first push and no repeat.
- `LastEventAt_AdvancesOnPublish` lives in `FleetTaskServiceTests` (no event-log test file exists).
- Added the test helper `ManualTimeProvider` (timer-capable fake clock) since the repo has no `FakeTimeProvider`.
- `FinishAsync` hit S3776, so the post-save notifications moved into `NotifyAfterSaveAsync`, and the stuck check is `CheckStuck`.
- ADR 0154 amends 0097 and 0115 and extends 0144, 0028, 0075.
- Known: the model-push counter is spent before the send (as the old `_sent` was), so a dropped push still counts.
- Pre-existing and unrelated: 3 `LiveCopilot*` tests fail with a `MissingMethodException` inside the Copilot SDK package.
