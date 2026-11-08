planned-against: main @ 863f458

# Plan: a live graph of a fleet task's teams and agents

## Overview

`/fleet/` shows a task as a team tree in the left panel (`#tree`, one row per team) and a text-heavy detail pane.
There is no picture of **which agents run, which are done, which are still pending, and how they relate**. Add a
DevUI-style node graph (the reference is the workflow canvas of the Microsoft Agent Framework DevUI screenshot: boxed
nodes, status-coloured borders, edges between them, a running node that stands out). Frontend only: every fact the
graph needs is already in the event log that `fleet.js` folds into `S.teams`, so there is no new event, no backend
change and no persisted field.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/wwwroot/fleet/fleet.js` — model (`S.teams`, `attempt.turns`, `currentPlan`, `agentState`,
  `S.calls`), `render()`, `renderTree()`, `renderFlow()`, `openPanel()`; the `handlers` table.
- `src/Agent42.Backend/wwwroot/fleet/fleet.css` — `.node`, `.dot`, `.chip`, `.flow`, theme tokens (`--run`, `--ok`,
  `--bad`, `--warn`, `--faint`, `--agent-s/-l`).
- `src/Agent42.Backend/wwwroot/fleet/index.html` — markup for the view switch; script tags.
- `tests/Agent42.Tests/FleetPageAssetsTests.cs` — the source-text guards for this page (pattern to copy).
- Docs: a new ADR (0116), the ADR 0097 `amended_by` list and the feature-map row "Agent fleet" in `CLAUDE.md`.
- NOT touched: `wwwroot/js/**` and `sw.js`. `/fleet/` is a classic script outside `wwwroot/js`, so CLAUDE.md
  cross-cutting rule 3 (bump `CACHE`) does not apply (see the comment in `index.html`). Do not bump it.

## Context & constraints

- **What the graph is built from** (all client-side, evidence `fleet.js` `handlers`):
  - Team nodes/edges: `team.started` gives `parentTeamId` and `parentCallId`; `tool.call` of `delegate_to_team` links
    the call to the sub-team (`call.subTeamId`, `team.delegatedBy` = the delegating agent). So the edge to a sub-team
    starts at the **agent** that delegated, not at the team.
  - Agent nodes: `currentPlan(attempt).agents` lists every agent the planner designed, so an agent with no turn yet is
    **pending** (`agentState` returns `"idle"`), one with an open turn is **working**, a closed turn is **done**,
    `turn.failed` is **failed**. This already exists in `agentState` — reuse it, extend it to return `failed`.
  - Agent→agent edges come from `plan.pattern` exactly as `renderFlow` draws them: Sequential (chain), Concurrent
    (fan out/in), Staged (`plan.stages`), Handoff (first agent ⇄ specialists), GroupChat (one cluster, no edges),
    Magentic (a Manager node → participants). Reuse that switch's structure; do not invent a second source.
  - Judge/retry: an attempt's `verdict`, `review` and `fix` mark retries. Show only the latest attempt's graph, with
    the existing attempt tabs unchanged; a retry is not a new node kind.
- **Delegation is not attempt-aware today (critic finding, verified).** `tool.call` and `team.started` set only
  `call.subTeamId` / `team.delegatedBy`; `S.calls` entries carry `teamId` but no attempt number, so after a judge
  retry attempt 2's graph could show attempt 1's sub-team under the same agent. The `tool.call` event already carries
  `attempt` (`d.attempt`, the same field `openTurn` uses), so store it on the call and draw a sub-team only under the
  attempt its delegating call belongs to. This is client state only, no backend change.
- **Per-node states** (critic finding): `agentState` yields only idle/working/done and a failed turn reads as done;
  `turnState` (`fleet.js`) already yields failed/done/working per turn — build node state from it. Pending is
  `idle` and needs an explicit `.gnode.idle` rule (not the implicit default dot colour). `stopped`/`interrupted`/
  `failed` exist at team and task level only, so a node in a stopped team is drawn from its **team's** status; there
  is no agent-level stopped state. The Magentic "Manager" is not in `plan.agents`: it is a synthetic node, neutral
  colour, no state. Handoff and GroupChat edges are **structural** (what the pattern means), not observed hops.
- **No new events, no backend change.** Rule 2 applies: `FleetJs_HandlesEveryPublishedEventType` must stay green.
  The graph reads the same state, so it also works on replay of an old task, and on a reload mid-run.
- **Rendering: inline SVG edges over absolutely positioned HTML nodes**, no library, no build step (ADR 0041; fonts and
  libs are already limited on this page). Model text is never put in `innerHTML` (the page's invariant in the file
  header): node labels and snippets are `textContent` only. Layout is a simple layered one (rank = depth in the
  pattern/delegation DAG, columns spread within a rank) — a dozen nodes, not a general graph library.
- **Placement (my call, user did not specify):** a view switch **Details | Graph** at the top of `#detail`, default
  Details so nothing existing moves. The graph fills `#detail`; clicking an agent node opens the existing
  `openPanel()` output panel, clicking a team box selects the team (`select()`), as the tree does today. The narrow
  left `#tree` stays as it is. Rejected: replacing the tree (loses the cheap overview and the task header), and
  squeezing a graph into the 260px aside.
- **Follow live** (`S.follow`) already moves `S.selectedTeam`; the graph shows the whole task, so it highlights the
  followed team and pans/scrolls it into view instead of switching graphs.
- Keep `render()` cheap: the graph is a structural render (rebuilt in `render()` under `structural`), streamed text
  must not rebuild it. Handlers that return `false` (text, thinking, budget, canvas, prompts) already skip
  `invalidate()`; the graph only changes on turn open/close, plan and team events, which already invalidate.
- Governing ADR: 0097 (page, events, replay), 0041 (no build step, composition), 0102 (the `canvas` card stays in
  the detail view, not the graph). Gotchas read: replay-then-live exactly once (a node must be idempotent on
  replay: derive from state, never append on render); `interrupted` is computed on read.
- Style: IBM Plex Mono / Newsreader and theme tokens only; node borders reuse the `.dot` colours (pending = `--faint`,
  working = `--run` + the `pulse` animation, done = `--ok`, failed/stopped/interrupted = `--bad`, retry = `--warn`).
  `prefers-reduced-motion`: the pulse is already the only motion; keep it that way.

## Steps

1. **State helper (pure)** — in `fleet.js`, add `buildGraph(task)` that returns `{ nodes, edges }` from `S.teams`
   (team clusters, agent nodes with `state`, delegation edges, pattern edges). Pure: no DOM, no `S` mutation.
   Derive node state from `turnState` (adds `failed`), record `attempt` on `S.calls` entries in the `tool.call`
   handler and filter delegation edges to the drawn attempt, give the synthetic Manager node no state. Verify: temporary node/console check against a recorded event log (step 6)
   and the source-text guard in step 5.
2. **Layout + render** — `renderGraph()` builds an `<svg>` for edges and positioned `<div class="gnode">` nodes;
   team clusters are labelled boxes, sub-teams nest under the delegating agent. Cap rendering for large fleets
   (collapse a done sub-team to one node, expandable) so a 40-agent task stays legible (best-effort: no automated test, checked by eye in step 6 only if the DevHost can
   produce that many nodes). Verify: step 6.
3. **View switch + wiring** — `S.detailView` (`"details" | "graph"`), the switch in `renderDetail()`/`index.html`,
   click handlers reusing `openPanel()`/`select()`, follow-live scroll. Persist the choice in `localStorage` inside a
   try/catch (a per-viewer convenience only). "Default Details" is therefore true on first use only: a stored
   `graph` preference also applies on deep-links and on replay of old tasks. Guards pin stable literals
   (`S.detailView`, `localStorage` inside try/catch, the `openPanel(`/`select(` call sites in the click handlers). Verify: step 5 + 6.
4. **CSS** — `.gnode`, `.gedge`, state modifiers, cluster boxes, switch; light and dark via existing tokens. Verify:
   `FleetPage_UsesThemeTokensAndSharedLibs` still green (no hard-coded colours), step 6 in both themes.
5. **Guards** (parallelizable with 4) — add tests to `FleetPageAssetsTests.cs` in the shape of the existing ones.
6. **Live check** — run `dotnet run --project tools/Agent42.DevHost`, submit a fleet task, and look at the graph
   in a real browser (claude-in-chrome) for: pending → working → done colours, a sub-team nested under the delegating
   agent, a retry (attempt 2 must not show attempt 1's sub-team), a reload mid-run, an old finished task, light +
   dark theme, phone width. If the canned DevHost LLM never
   produces a team or a delegation, say so and report which parts were verified by reading only.
7. **Docs (`/decide`, `/sync-docs`)** — ADR 0116 "Fleet graph view" (decision, alternatives above), add it to ADR 0097's
   `amended_by`, extend the CLAUDE.md "Agent fleet" feature-map row with 0116. Update the evidence line of any
   gotcha that cites `renderTree`/`renderFlow` if it moves.

Steps 1→2→3 are sequential (same file). 4 and 5 can run beside 3 after 1's names are fixed.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| The page offers a Details/Graph switch, defaulting to Details | `FleetJs_OffersAGraphView_DefaultingToDetails` | `tests/Agent42.Tests/FleetPageAssetsTests.cs` |
| Pending (`idle`), working, done, failed and retry nodes have distinct explicit `.gnode` rules, coloured by theme tokens only (no hex/`rgb(`; `color-mix` stays allowed) | `FleetCss_StylesTheGraphNodesWithThemeTokens` (carries the no-hex check itself; `FleetPage_UsesThemeTokensAndSharedLibs` does not) | same |
| Graph code never uses `innerHTML`/`DOMPurify` (the existing `Assert.Single` innerHTML guard also fails on any new use) | `FleetJs_GraphBuildsNodesAsText` — asserts the `buildGraph`/`renderGraph` bodies contain neither | same |
| A delegation is stored with its attempt, so a retry never shows an earlier attempt's sub-team | `FleetJs_DelegationCallsRecordTheirAttempt` (source guard on the `tool.call` handler) + manual check in step 6 | same |
| The `/fleet` exclusion in `sw.js` stays, which is why no `CACHE` bump is needed | `ServiceWorker_LeavesFleetOutOfTheShell` — source guard on `startsWith("/fleet")` in `sw.js` | same |
| The graph reads only state the handlers already build (no new `Publish`, `FleetJs_HandlesEveryPublishedEventType` stays green) | existing `FleetJs_HandlesEveryPublishedEventType` | same |
| A node click opens the agent panel, a team click selects the team | `FleetJs_GraphNodesReuseThePanelAndSelect` | same |
| Live rendering of states, nesting, reload, themes, phone width | manual, step 6 (reported with what was actually seen) | n/a — no JS runtime in the test project |

The test project has no JS runner, so behaviour is checked by source-text guards plus step 6. State that plainly in
the final report rather than claiming runtime coverage.

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- `coverage_new_code_pct_min: 80` — measured by `node .agentic/scripts/check-new-code-coverage.mjs` over coverlet
  cobertura × git diff. The change adds no C#, so there are no new C# lines; report the tool's actual output, or
  SKIPPED if it has nothing to measure. Never estimate.
- `cognitive_complexity_max: 15` — S3776 at `dotnet build`. Applies to C# only; JS is not analyzed, but keep
  `buildGraph`/layout functions small anyway (split by pattern, as `renderFlow` does).
- Also: `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`.

## Out of scope

- Any backend/event change, new persisted state, or a new endpoint.
- Zoom/pan libraries, drag-to-rearrange, edge animation, a mini-map.
- Showing every attempt's graph at once; the existing attempt tabs stay.
- Token/cost per node, canvas pages in the graph, and replacing the left `#tree`.
- The Android app and the chat UI.

## Review

Critic verdict: sound overall; 2 HIGH, 1 MEDIUM, 2 LOW findings. The critic did not read `gotchas.md` or the other
ADRs; I read the fleet gotchas (replay exactly-once, `interrupted` computed on read) when planning. I verified the
`sw.js` `/fleet` exclusion (`sw.js:104`) and `turnState` myself.

| Finding | Handling |
|---------|----------|
| HIGH: delegation edges not attempt-aware | Accepted. Context note, step 1 (record `attempt` on `S.calls`, filter by drawn attempt), AC + guard, retry case in step 6 |
| HIGH: per-node states not in the model (failed read as done; no `idle` style; stopped/interrupted only at team level; Manager synthetic; Handoff/GroupChat edges structural) | Accepted. Context note, step 1 uses `turnState`, explicit `.gnode.idle` rule in the CSS AC |
| MEDIUM: CACHE and no-hard-coded-colour ACs cited tests that don't assert them | Accepted. New `ServiceWorker_LeavesFleetOutOfTheShell`; the CSS test carries the no-hex check, allowing `color-mix` |
| LOW: `FleetJs_GraphNeverUsesInnerHtml` redundant | Accepted in part: kept as `FleetJs_GraphBuildsNodesAsText`, scoped to the graph function bodies, since the existing guard only counts total `innerHTML` uses |
| LOW: persisted view preference vs "default Details"; large-fleet cap has no test; empty Review | Accepted. Step 3 wording, step 2 marked best-effort, this section filled |

## Implementation notes — where it diverged from the plan

- `buildGraph(rootId)` returns a nested team tree (ranks of cells, links), not `{ nodes, edges }`; the layout (measure/place) turns that into positioned nodes and edges. ADR 0116 documents the tree.
- The Details | Graph switch is built in JS (`viewSwitch`); `index.html` is unchanged.
- Concurrent and GroupChat draw one rank with no edges (no fan-out/in anchors).
- Graph always draws each team's latest attempt (critic finding): the selected attempt tab is not applied.
- Added after critic review: a node whose turn a stop cut off takes the team's state (`turn.cut`), delegations from non-node agents are adopted under the first agent node, staged duplicates are de-duplicated, and the working node no longer pulses its whole box (only the dot does).
- `FleetJs_GraphBuildsNodesAsText` scans the whole graph section, not just two function bodies. Added `FleetJs_GraphKeepsCutOffNodesAndOrphanSubTeams`.
- Extra doc: a line in `docs/features/agent-fleet.md`.
- **Step 6 (live check) was NOT done**: the Claude in Chrome extension was not connected. Layout geometry, edge routing, themes, phone width, retry, reload, follow-live scroll and collapse above 24 agents are unverified at runtime. Only source-text guards cover this change.
