---
id: 0154
date: 2026-11-28
status: accepted
tags: [fleet, push, notifications, stuck, egress, single-replica, no-migration]
supersedes: []
amends: [0097, 0115]
extends: [0144, 0028, 0075]
amended_by: []
---

# Fleet tasks push when they finish, fail, hit a limit or go quiet

## Context
A task started from `/fleet/` pushed only when it opened a question, or when a model called `send_push_notification` (ADR 0144).
One that finished, failed, hit its token cap or hung was silent. A task started from a chat already reports back as a
background turn whose away push (ADR 0115) covers it.

## Decision
1. **Terminal pushes:** Done ("finished"), Failed ("failed"), Stopped by a cap ("limit reached"). Never a user stop, never Interrupted.
2. **Only for a task without an origin conversation.** A chat-started task keeps ADR 0115's turn push, so it never gets two.
3. **Done yields to the model's own push** (the fleet form of ADR 0075's `PushesSent == 0`): `FleetPushCounter`, created by the
   service and spent by `FleetPushTools`, so the per-task cap and the count are one number. Failed and Stopped always push.
4. **Stuck:** running, no open question, no event for `Fleet:StuckAfter` (10 minutes, `TimeSpan.Zero` = off). One push per quiet
   spell, re-armed by an event, at most 2 per task (`FleetStuckWatch`). Checked on the existing 1 s flush tick, fire-and-forget.
   Applies to chat-started tasks too.
5. **`StuckAfter` is operator-only**, not one of the ADR 0135 user settings.
6. **No per-event or per-device preference.**

The user stop has two sites (the stop button, declining "Run this team?") and one constant, `FleetBudget.UserStopReason`.
The texts are constants: no request, result, reason or exception text, so this is not an ADR 0013 extension.

## Alternatives considered
- Push on a user stop: the user did it, the push is noise.
- Push on Interrupted via a startup scan: ADR 0097 decision 3 rejects the scan, and the replica is going away.
- A stuck timer per task: the flush tick already runs per running task on the injected `TimeProvider`.
- A user-overridable `StuckAfter`: its own change (ADR 0135).

## Consequences
- ADR 0115's "never notifies" means the conversation, not this generic push: a chat-started task can still get a stuck push.
- Known limit: a Copilot agent or a long tool run can emit no event for more than `StuckAfter`, so a false "stuck" is possible.
  The threshold is operator-tunable and 0 disables it.
- The stuck state is in-process (`RunningFleetTask`), correct under the single replica (CLAUDE.md rule 12).
- Spec: `.agentic/specs/completed/plan-fleet-notifications.md`.
