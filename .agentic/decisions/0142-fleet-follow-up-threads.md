---
id: 0142
date: 2026-11-24
status: accepted
tags: [fleet, frontend, ui, follow-up, no-migration]
supersedes: []
amends: [0097]
---

# The fleet task list groups follow-ups into threads

## Context
ADR 0097 persists a follow-up's parent as `FleetTaskRecord.FollowUpOf`, and a chain shares one workspace. The `/fleet/`
sidebar still listed every task flat, newest first, so a chain was scattered between unrelated tasks and a follow-up
looked like any other task.

## Decision
Frontend only (`src/Agent42.Backend/wwwroot/fleet/fleet.js`, `taskThreads` + `renderTaskList`); no API or storage change.
- **Thread = chain root.** Each task is walked up `followUpOf` while the parent is in the list **and older**. A deleted
  parent makes its follow-up a root, and the "older" rule makes a loop impossible.
- **Order:** threads by latest activity (their newest task's start), so a new follow-up moves its thread to the top.
  Follow-ups inside a thread run oldest first.
- **One level of indent**, never a nested tree (a 260 px sidebar). A follow-up whose parent is not the row above it (a
  branch: two follow-ups on one task) shows `↳ #n` (tooltip "Follow-up on #n"), and the chip is just the count, so both fit the sidebar.
- **Collapse:** a thread opens by default while it holds the selected task or a running follow-up. The chip toggles it
  (a per-page `S.threadOpen` override, not persisted), and a collapsed chip shows the newest follow-up's status dot.
  Selecting a follow-up clears the override, so the selected row is never hidden.
- **Search** keeps a thread whose root or any follow-up matches, and opens it on the matching follow-ups only.

## Alternatives considered
- **Flat list with a follow-up marker:** cheapest, but a chain stays scattered.
- **One row per thread with a step strip on the task page:** matches the shared workspace, but hides earlier steps from
  the sidebar and makes them harder to reach or delete.
- **A nested tree for branches:** faithful, but deep indentation does not fit the sidebar and branches are rare.

## Consequences
- `#n` is still the list position (`taskNumber`), so deleting a task renumbers the tasks above it, and a `↳ #n` shifts
  with it. A stable number needs a persisted per-user counter on the record: not done here.
- The open/closed override resets on reload. Accepted for a personal app.
- `/fleet/` is outside the service worker's SHELL (0116), so no `CACHE` bump.
