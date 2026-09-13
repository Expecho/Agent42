---
id: 0072
date: 2026-09-13
status: accepted
tags: [frontend, chat, streaming, tools, no-migration]
supersedes: []
extends: [0041]
amends: []
---

# A turn's tool calls fold into one counted disclosure once there are more than three

> **Do not write "following ADR 0071".** The reasoning disclosure (0071) and this one look alike — both are
> a `<details>` above `.prose`, both are live-only, both are settled from `send.js`'s `done` arm *and* its
> `finally` — but their default states are **opposite for a reason**. Reasoning is collapsed from the first
> delta because the thinking is not what the user asked for; a tool group is **open while it streams**,
> because the running chips are the only progress indicator a long tool chain has. Copying 0071's
> collapsed-on-creation shape here would hide exactly the thing the user is waiting on.

## Context

Since the feature shipped, every `toolCallStart` has appended its own chip to a flat `.msg__tools` list
(`thread.js`), one per call, for the life of the turn. That was written when a turn made one or two calls.
It no longer does: an MCP server (ADR 0024/0058), a seeded-URL read (ADR 0067) or a web-search-then-fetch
chain (ADR 0011/0027) routinely produce five or more in a single turn, and on a phone the reply is then
below the fold behind a wall of chips the user has already read. Tool chips are **live-only** — `renderTurns`
never replays them — so the clutter is confined to the turn being streamed, which is also the turn the user
is staring at.

## Decision

Chips live inside a per-turn `<details class="msg__toolgroup">`. It is created on the first call of the turn
and **open**; once the turn has made more than `TOOL_GROUP_THRESHOLD` (3) calls it gains `is-grouped`, which
is the only thing that draws its summary — `"N tool calls"` with a status lamp. `finishToolCalls(msg)`,
called from the same two places `finishReasoning` is, stops the lamp and **collapses the group, but only if
it crossed the threshold**. A turn of three calls or fewer therefore renders exactly as it always has.

## Alternatives considered

- **Fold only the older chips, keeping the running one visible.** No end-of-turn transition, so no `send.js`
  change at all. Lost because the tidying happens at the wrong moment: it fires mid-turn, when the history
  is the useful part, and leaves the finished turn permanently half-expanded — the state the user least
  needs once the answer has arrived.
- **Always one disclosure, no threshold.** The simplest rule, and the only one with no magic number. Lost
  because it costs the at-a-glance "it is searching the web right now" feedback on the common single-tool
  turn, to solve a problem that turn does not have.
- **Collapse as soon as the threshold is crossed, mid-stream.** Rejected for the reason in the callout
  above: during the turn the chips *are* the progress bar.
- **Cap the list and drop the oldest chips.** Rejected — a tool call that scrolled away is unrecoverable
  (nothing persists it), and the folded summary keeps every one of them one click away instead.

## Consequences

- A long tool chain costs one 25 px line in the finished transcript instead of ~5 chips (measured in
  DevHost: a folded five-call group is 25 px tall; the open one is ~190 px). The chips stay reachable — it
  is a real disclosure, and clicking the summary reopens it.
- **The group must never be collapsed on the way in**, only by `finishToolCalls`. That is pinned by
  `ToolCallFoldAssetsTests.TheGroup_IsCollapsedOnlyWhenTheTurnEnds`, which asserts the absence of
  `open = false` in both `toolGroup` and `syncToolGroup`.
- `finishToolCalls` is wired into the `finally` as well as the `done` arm, for the reason ADR 0071 gives for
  `finishReasoning`: three tails never see a `done` (a voice barge-in, ADR 0037; a recovered stream, ADR
  0046; an outright failure), and nothing heals a stuck group afterwards because chips are never replayed.
  The guard for this is **narrowed to `send`'s own body first** — three other modules contain a `finally`,
  and a bare `"} finally {"` marker can anchor in one of them and widen the slice far enough to swallow
  `send.js`'s `import` line, which names `finishToolCalls` and makes the assertion vacuous. That was
  observed, not theorised: the first version of the test passed against a `send.js` with the call deleted.
  `ReasoningAssetsTests.EveryStreamTail_SettlesTheReasoningDisclosure` has the same shape and is likely
  vacuous for the same reason — noted here, deliberately not changed in this MR.
- Frontend-only: no endpoint, grain, `[Id]`, migration, secret, container, Bicep or `excludedPaths` change,
  and no new JS module (`thread.js` gains the group; `send.js` gains one call in two places). Shell cache
  bumped `agent42-shell-v35` → `-v36` per ADR 0041.
- The threshold is a constant in `thread.js`, not a preference. A user-facing setting for it would be a
  third layer over a thing that is already self-correcting — a short turn never folds — and is not worth a
  row in Preferences.
- The offline browser leg **was** run (headless Chrome over CDP against DevHost, both themes): a real
  one-call turn through `send.js` renders flat and ungrouped exactly as before (`summaryDrawn: "none"`,
  `grouped: false`), a synthetic five-call turn streams open (`is-running`, lamp pulsing, `"5 tool calls"`)
  and folds to `open: false` with every chip hidden and the lamp green. What DevHost cannot show is a real
  model producing more than two tool calls in one turn — `CannedChatClient` tops out at the ADR 0067 seeding
  chain — so the above-threshold path was driven through the shipped `thread.js` functions directly rather
  than off a real stream. Deliberately not fixed by adding a canned five-call demo: that is DevHost surface
  bought for one screenshot.
