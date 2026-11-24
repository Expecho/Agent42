---
id: 0124
date: 2026-11-10
status: accepted
tags: [fleet, agents, lessons, unattended, egress, untrusted, reminders, blob, ui, no-migration]
supersedes: []
extends: [0097, 0122, 0123]
amends: []
---

# The fleet learns lessons about team design in a nightly, opt-in "dream" and applies them to every planner

## Context
The judge writes concrete feedback on every failed fleet result, but each task starts from zero: only the in-task
`PreviousAttempt` note carries over. A user wants what one task taught to reach the next planner, with no human step,
and wants to SEE what was learned, when, from which tasks and where it was applied (2026-11-10). The data is already
persisted: each task's event log holds `plan.submitted`, `verdict`, `attempt.failed`, `planner.failed`, `task.finished`.

## Decision
- **Nightly, unattended, opt-in per user.** A model reads a digest of the user's recent finished tasks and calls
  `submit_lessons`. The toggle (Lessons view, default OFF) is the consent, because it spends the user's own tokens. The
  operator kill switch is `Fleet:DreamEnabled`. A "Dream now" button runs the same code (trigger `manual`).
- **Auto-applied.** A lesson that passes the code checks is `active` at once; the user can disable it (kept as a tombstone, so it is
  not proposed again) or delete it. There is no `proposed` state.
- **Applied at every depth.** Active lessons (newest-supported first, at most `LessonsInPrompt`) are appended to the planner's USER
  prompt in `CreateTeamAsync` at any depth and on a re-plan, never the system prompt: derived text gets user-role authority (ADR 0104
  amended 0056 on exactly this line) and the editable `planner` prompt stays untouched. The block says lessons describe past outcomes
  and are never a reason to add tools, change approvals or use a particular agent kind; `PlanSubmission` enforces tools, budget and
  approval in code regardless.
- **Storage: one Blob `{userId}/lessons.json`** (the `prompts.json` shape), OUTSIDE every workspace prefix because the workspace sync
  deletes unmatched blobs. New persisted fields are trailing with defaults (rule 2); `FleetTaskRecord.AppliedLessons` holds an id and a
  text snapshot per applied lesson.
- **Schedule: an HOURLY Orleans reminder on a per-user `FleetDreamGrain`**, with the decision in the Backend. The reminder fires once
  cluster-wide and is persisted, which a hosted-service timer in each of the two replicas of a rollover would not be (rule 12). It ticks
  hourly because Orleans realigns to the next period boundary and skips ticks missed while the app was down, and every push to `main`
  deploys. `FleetLessonService.TickAsync` runs a night's dream only inside a window (`Fleet:DreamAtUtc`, default 01:00 UTC for
  `DreamWindowHours` = 4), only when `LastRunAt` is before the window start and at least `DreamMinNewTasks` tasks finished since. The
  tick is typed (`FleetDreamTick`): `Disabled` and `Unavailable` make the grain unregister its own reminder. The grain reaches the
  Backend through the existing `IFleetLauncher` port (ADR 0122 decision 6). Enabling schedules, disabling unschedules, and every
  `GET /api/fleet/lessons` with the toggle on re-schedules (idempotent), which heals a reset reminder table.
- **Digest in code, one model run** (`FleetDigest`, `FleetDreamer`), bounded: logs are read ONE at a time, newest task first, only the
  structural events kept, reading stops at `DreamMaxDigestChars`. The digest is framed with the new `FleetToolLimits.FrameAsData`
  (not `WrapAsData`, which cuts at 12,000 characters).
- **Usage** is attributed to the user's telemetry context and its own application name, "Agent42 fleet dream" (ADR 0031/0110).

## Screening is shape hygiene, NOT an injection defence
The digest holds task requests (a chat-started task's request is model-authored), the planner's rationale, agent names and judge
feedback: all untrusted. `FleetLessons.Validate` bounds what a lesson can look like, nothing more: (1) evidence ids must be in the
digest and belong to at least `DreamMinSupport` distinct WORKSPACES (a follow-up chain counts once; the model picks the ids and nothing
proves they support the text); (2) the text is an allowlist of Unicode letters and numbers, space and `. , ; : ' " ( ) - / % +`, so every
control, format (zero-width, bidi), private-use, surrogate, separator and markup character is refused, plus domain-like tokens, `://`,
`www`, `hxxp`; (3) a denylist of execution-shaped terms (`copilot`, `shell`, `bash`, `command`, `execut`, `script`, `token`, ...,
matched at word starts, pinned by a test) so a lesson cannot name the one agent kind that has a shell; (4) a fixed topic and confidence
set; (5) at most `DreamMaxNewPerRun` new lessons per night and `LessonsMaxStored` in total; (6) the planner framing above; (7) everything
is visible and one click turns a lesson off; (8) opt-in; (9) lessons expire after `LessonTtlDays` without support.
**Residuals, bounded and visible, not prevented:** a poisoned result can still plant a lesson through judge feedback in an unattended
run, including one derived from a task whose results contained fetched web text (not excluded: research tasks benefit most); and an
allowed-vocabulary lesson can still bias team design toward the agents the task already permits.

## Alternatives considered
- **Hindsight as the store** — it has no delete path (ADR 0056), an unattended write-only memory is the wrong shape, it is an
  operator-gated third-party egress, and lessons from fleet content would be a new egress path (rule 13).
- **Cosmos grain state** — the fleet is Blob by design (ADR 0097).
- **An analyzer-style agent browsing tasks with tools (ADR 0123's shape)** — many round trips, unbounded cost, nothing to unit test.
- **A user approval gate (`proposed` state)** — rejected by the user: the run is unattended; visibility plus a one-click off replaces it.
- **A hosted-service timer** — two replicas overlap at a rollover and would both dream; a nightly reminder would skip a night when a
  deploy lands at the wrong time.
- **Lessons in the planner's system prompt** — would give derived text system-level authority and fork the editable `planner` prompt.
- **Applying only at depth 0** — rejected by the user. Cost is at most `LessonsInPrompt` x `LessonMaxChars` (about 600 tokens) per planner
  call; `lessons.applied` carries `chars` so it can be measured.

## Consequences
- Egress (rule 13): no new destination. The digest (short requests, judge feedback, plan names) goes to the user's own chosen provider,
  the same set as ADR 0097 decision 8 and ADR 0122; nothing goes to Hindsight. New: it happens unattended, hence opt-in.
- Single replica is load-bearing: the per-user lock and the running-dream registry are in-process. A rollover overlaps two replicas for a
  few minutes and the store has no ETag, so a disable or delete made on the OTHER replica in that window can be lost (the user clicks
  again). Accepted; no conditional write in slice 1.
- Lock scope: a per-user `SemaphoreSlim` guards short read-modify-writes only (one exception since ADR 0143: approving a prompt proposal holds it across two Blob reads and writes). A run reads without the lock, calls the model unlocked, then
  takes the lock, RE-READS and merges, so a disable or delete made during the run survives. A `running` entry not in the registry reads as
  `interrupted` (persisted under the lock with a re-read), as for fleet tasks.
- A skipped night is recorded once per window, not once per hourly tick. A run skipped for too few tasks does not use up the night.
- "Applied" is recorded when it is true: the first planner of a run that receives lessons assigns `task.Record` FIRST (the final save
  rebuilds from it), saves it, and records the counters in the background (once per task, last 20 applications). A slow or failing
  lessons store never fails or delays planning; a finishing task waits at most 5 seconds for the counters. The one write on the
  planning path is the task-record save of `AppliedLessons` (a small blob write, awaited so the task view shows it while the task
  runs); its failure is caught and logged, and the final save carries the field anyway.
- Re-enabling a disabled lesson does not check `LessonsMaxStored`, so the active count can exceed it; the prompt is still capped by
  `LessonsInPrompt`, and new learning stops until the count is back under the cap.
- ADR 0121's unmerged branch (`feature/open-signin-owner-tier`) also changes the usage projector; whichever lands second rebases this.
- Slice 2 (out of scope): retiring a lesson because tasks that used it scored worse, prompt rewriting (now in scope as
  approval-gated targeted edits: amended by [ADR 0143](0143-fleet-dream-proposes-prompt-edits.md), 2026-11-24), judge calibration, a per-user
  timezone, a push when a night learns something.
- The Lessons UI is a view on `/fleet/` like the prompt editor (not a modal dialog, which would block the page); `/fleet/` is outside
  `SHELL`, so no `sw.js` bump.
