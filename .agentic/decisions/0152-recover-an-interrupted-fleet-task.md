---
id: 0152
date: 2026-11-27
status: accepted
tags: [fleet, recovery, interrupted, workspace, canvas, digest, untrusted-input, single-replica]
supersedes: []
amends: [0097]
amended_by: []
---

# Recover an interrupted fleet task: a new task on the same workspace, the approved team rebuilt from the log

## Context
Deploys restart the single replica, so long fleet tasks are interrupted routinely. ADR 0097 says an interrupted task
"can be run again, but it cannot be resumed": Run again starts a new workspace, copies only `inputs/`, and calls the
planner afresh. Yet the workspace files, the canvas pages and the event log (accepted team plan, per-agent text, todo
list, verdicts) are all still in storage and nothing reads them.

## Decision
Add **Recover** for tasks that are `interrupted`, `stopped` or `failed`: it starts a NEW task (the old record stays as
history) that shares the old task's workspace id, hence its canvas. The root team is rebuilt from the last
**accepted** plan in the log, through the same validation, agent loading and budget reservation as a planner
submission but with no planner call and no approval prompt; on any rejection it falls back to the planner. The agents
get a bounded digest of what was done in their input. The digest is untrusted data (agent text, judge feedback),
framed like `FleetDigest`. A recovered task is not an origin task (ADR 0115), never clarifies, and inherits tools,
token cap, planner model and self-review. Notes are now saved after each attempt, not only at the end.

## Alternatives considered
- **Resume the agents mid-conversation** — agent sessions are not persisted and a sub-team's result is a tool result
  inside its parent's run; not possible without a different runtime.
- **Extend Run again to copy the whole workspace** — still replans, loses the canvas, and gives the agents no account of
  what was done, so they redo it.
- **Treat a recover as a follow-up (`FollowUpOf`)** — follow-up reads the parent's `Result`, which an unfinished task
  lacks, and `CanFollowUp` would have to weaken for everyone.
- **Keep the origin for chat-started tasks** — the digest is model and tool output, and an origin task auto-approves
  Copilot shell and MCP writes (ADR 0115); a recovery asks on the page instead.

## Consequences
- A restart no longer throws away the approved team, files and canvas.
- Limits: no mid-agent resume and no sub-team replay (the root team's agents restart with the digest); per-agent text
  is best effort (it stops being persisted past `Fleet:MaxEventLogBytes`); notes survive only attempt boundaries, so a
  task interrupted in its first long attempt still loses them; the inherited token cap may be hit again.
- The recovered task's `Inputs` stays empty, so attached files are not re-ingested; `Documents`/`TextCopies` come
  from the interrupted task.
- Known pre-existing hazard, not fixed here: during a revision rollover the new replica reads the old replica's live
  task as `interrupted`, and a recover would then run beside it (CLAUDE.md rule 12).
- Spec: `.agentic/specs/completed/plan-fleet-recover.md`.
