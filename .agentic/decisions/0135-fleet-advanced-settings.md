---
id: 0135
date: 2026-11-16
status: accepted
tags: [fleet, settings, per-user, options, dream, blob, ui, no-migration]
supersedes: []
extends: [0097, 0124]
amends: []
---

# Users override the fleet's limits per user, in an "Advanced settings" view

## Context
Most `Fleet:*` limits (team depth, judge and fix counts, the soft budget, the dream's window and thresholds) could only be
changed by the operator in config. The composer exposes the per-task knobs (model, token cap, tools, clarify, self-review) and
nothing else. A user wanted to tune the rest for their own tasks and their own dream (2026-11-16).

## Decision
- **Per user, never global.** The overrides are one Blob `{userId}/settings.json` (the `prompts.json` / `lessons.json` shape,
  outside every workspace prefix), a record `FleetUserSettings` whose every field is nullable: null means "use the operator's
  value". A body on `PUT /api/fleet/settings` is the FULL override set, so one call both sets and clears; `DELETE` clears all.
  Unknown JSON members are ignored. It is a Blob document, not grain state, so rule 2's trailing `[Id]` does not apply.
- **25 fields**: 13 behaviour (`MaxTeamDepth`, `MaxAgentsPerTask`, `MaxConcurrentSubTeamsPerLevel`, `DefaultMaxGroupChatRounds`,
  `BudgetSoftLimit`, `JudgeDepth`, `PassingScore`, `MaxAttempts`, `MaxFixRounds`, `ApprovalDepth`, `MaxClarifyingQuestions`,
  `SelfReviewMaxIterations`, `DefaultCopilotModel`) and 12 dream/lesson ones. **Operator-only, never in the record or the
  request:** `MaxTokensPerTaskLimit`, `DefaultMaxTokensPerTask`, `MaxConcurrentTasks`, `CopilotAgentsEnabled`, `DreamEnabled`,
  `DreamTimeout`, `SseKeepAlive`, `MaxEventLogBytes`, `MaxWorkspace*`, `MaxInput*`, `MaxRetryResultLength`, `MaxToolTextLength`.
  The token cap stays the hard cost bound no matter what a user sets.
- **One resolved `FleetOptions` per task and per dream.** `FleetSettingsService.ResolveAsync(userId)` returns a CLONE of the
  operator's singleton with the user's clamped overrides applied. The engine already reads one `FleetOptions` per run
  (`FleetRun.Options`), so nothing in it changed. `FleetTaskService.SubmitAsync` resolves once, before `Register`, and the result
  lives on the running task: the `FleetBudget` caps, `FleetRun.Options`, the lessons the planners get and the Copilot default
  model all read that snapshot. **A running task keeps the options it started with; a save changes the next task.** The dream
  resolves at tick time (`TickAsync`, `DreamAsync`) and passes the result to `NothingToLearn`, `WindowStart`, the digest, the
  dreamer, `Merge` and `Expire`. No snapshot is persisted on `FleetTaskRecord`.
- **Bounds in ONE data-driven table** (`FleetSettingsService.Descriptors`: key, group, kind, min, max). It drives server
  validation, the clamp on resolve and the `GET /api/fleet/settings` payload the page renders from, so the page hardcodes no
  bound. `JudgeDepth` and `ApprovalDepth` are bounded by the effective `MaxTeamDepth` (the user's, else the operator's).
  Out of range on PUT is a 400 naming the field; resolve clamps a hand-edited blob instead and never throws. `DreamAtUtc` is
  strict `HH:mm`. `DefaultCopilotModel` must be a tool-capable catalog model OR a GitHub Copilot one (ADR 0108), the set a run
  gets, which is NOT `FleetTaskService.IsToolCapable` alone (that excludes Copilot-only models).
- **Callers that showed the operator's value now show the user's**: the composer's `GET /api/fleet/config`, the Lessons view's
  thresholds and schedule, and the analyzer's `DescribeConfigAsync(userId)` (the launcher port is now async and takes a user).
- **Auth and egress**: behind Easy Auth, scoped by `GetUserId()`, never in `excludedPaths`; the service is resolved optionally and
  answers 503 without a fleet. Settings are config, so nothing new leaves the process (rule 13).

## Consequences
- **A user can raise the operator's soft caps** (`MaxAgentsPerTask` up to 100, `MaxTeamDepth` up to 10,
  `MaxConcurrentSubTeamsPerLevel` up to 12), which raises what one task can spawn and the in-process work on the single replica
  (rule 12), Copilot shells included. The token cap still bounds the spend, and one user runs one task at a time. Sign-in is invite-only
  (ADR 0121), so this was accepted as written. Alternatives, not taken: clamp these three to `min(user, operator)`, or add
  operator-set `Max*` ceilings. Either is a change to the bounds table only.
- Lowering `LessonsMaxStored` or `LessonTtlDays` removes nothing at once: the TTL applies at the next dream's `Expire`, and a lower
  cap only blocks new lessons.
- Every hourly dream tick now reads one small settings blob per user, even outside the window, because the window is the user's.
- `FleetTaskService`, `FleetLessonService` and `FleetCopilotAgentFactory` take the new dependency as an optional trailing parameter
  (null: the operator's options), so a host or test that does not register `FleetSettingsService` behaves as before.
- `/fleet/` is outside `SHELL` (ADR 0116), so there is no `sw.js` bump.
- Out of scope: operator-only fields, a global or owner-set layer, per-task overrides, retroactive pruning, the Android app.
