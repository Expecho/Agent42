---
id: 0116
date: 2026-11-08
status: accepted
tags: [fleet, ui, frontend, graph, no-build-step]
supersedes: []
extends: [0097, 0041]
amends: [0097]
amended_by: [0117]   # 0117: the team box shows done/total when the latest attempt has todos
---

# The fleet page can show a task as a live graph

## Context
`/fleet/` shows a task as a team tree and a text-heavy detail pane. Nothing shows which agents run, which are done
or pending, and how they relate. Every fact needed is already in the event log `fleet.js` folds into `S.teams`.

## Decision
A **Details | Graph** switch at the top of the detail pane (default Details; the choice is a per-viewer
`localStorage` preference read and written inside try/catch). The graph is frontend only: no event, endpoint or
persisted field, so it is the same on replay, on a reload mid-run and live.

- `buildGraph()` is pure: it reads `S.teams` and `S.calls` and returns a tree of team boxes, ranks of agent nodes
  and edges. Edges come from the plan pattern, as `renderFlow` draws them. Handoff and GroupChat edges are
  structural (what the pattern means), not observed hops. Concurrent and GroupChat draw one rank with no edges (no
  fan-out/in anchors). The Magentic "Manager" is a synthetic neutral node.
- A sub-team hangs under the agent that delegated to it, and only under the attempt that call belongs to: the
  `tool.call` handler now stores `attempt` on the `S.calls` entry.
- Node state comes from the turns (idle = pending, working, done, failed). A pending node of a stopped, interrupted
  or failed team is drawn in the team's state; a done node of a team in `retry` is drawn as retry.
- Rendering is inline SVG edges over absolutely positioned HTML nodes with a simple layered layout, no library
  (ADR 0041). Labels are `textContent` only. Past 24 agents a finished sub-team collapses to one box (click opens it).
- Clicking a node opens the existing agent panel; clicking a team box selects the team. Follow live highlights the
  followed team and scrolls it into view when it changes.
- The graph always draws each team's latest attempt (the attempt tabs belong to Details). The view preference persists across tasks and deep links. A delegation whose agent is not a plan node hangs under the first agent node rather than vanishing. A node whose turn a stop cut off is drawn in the team's state.
- `/fleet/` is outside the service worker SHELL, so there is no `CACHE` bump.

## Alternatives considered
- **Replace the left tree**: loses the cheap overview and the task header.
- **A graph in the 260px aside**: far too narrow.
- **A graph library**: a build step or another vendored lib for about a dozen nodes; rejected under ADR 0041.
- **A new backend event or endpoint**: unnecessary, the event log has it all.

## Consequences
The test project has no JS runner. `FleetPageAssetsTests` pins only a few source-text facts (the shell exclusion,
the cut-off node handling, the todos handler); the graph's behaviour is checked by hand in DevHost, and nothing
enforces that every published event has a `handlers` entry in `fleet.js`.
