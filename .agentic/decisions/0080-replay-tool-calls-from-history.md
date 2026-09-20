---
id: 0080
date: 2026-09-20
status: accepted
tags: [frontend, tool-calls, history, replay, sharing, egress, projection, no-migration]
supersedes: []
extends: [0041]
amends: [0072, 0035, 0046]
---

# Replay a turn's tool calls when a chat is reloaded from history

## Context

ADR 0072 built the folded tool section: a turn that makes more than three tool calls collapses its chips
into one counted disclosure when the turn ends, so a long chain costs a single 25 px line instead of
pushing the reply off the screen. It shipped as a **live-only** feature — `renderTurns` never replayed
chips, so the section existed only for the turn currently streaming. Reload the page, or reopen the chat
from the sidebar, and every chip was gone.

ADR 0072 recorded that as acceptable and gave a reason: "a tool call that scrolled away is unrecoverable
(nothing persists it)". **That reason was wrong.** `FunctionCallContent` and `FunctionResultContent` have
been persisted in the session since the feature shipped — they are what the model reads back on every
turn, and what `McpToolLimits`, `WebToolLimits`, `SkillToolLimits` and `GeoToolLimits` all exist to bound
(ADR 0004). The data was there the whole time; `GetHistoryAsync` simply discarded it on the way out.

The fold is what makes replaying it affordable. Before ADR 0072, repainting five chips would have pushed
every historical reply below a wall of disclosures; with the fold, a long chain costs one line.

## Decision

`GetHistoryAsync` **projects** the tool calls already in the session onto the assistant turn they belong
to, and both frontend painters of a stored `ChatTurn` replay them into the same section the live turn
showed — grouped and collapsed above the threshold, flat and open below it.

- **New `ChatToolCall` DTO** (`toolCallId`, `toolName`, `toolInput`, `toolResult`), carried as a trailing
  `ToolCalls` member on `ChatTurn`. The member names are load-bearing: under `JsonSerializerOptions.Web`
  they serialize to byte-for-byte the live `toolCallStart`/`toolCallEnd` chunk shape, so the replay path
  hands the object straight to `addToolCall`/`completeToolCall` with **no mapping layer**. Both sides
  serialize through the same `SerializeOrNull`, so a replayed payload is identical to the streamed one.
- **This is a projection, not a migration.** `ChatTurn` appears in no `*State.cs` file — it is built
  fresh from the session on every call — so there is no stored-document shape to round-trip and
  `GrainStorageSerializerTests` is the wrong test to reach for. The trailing `[property: Id(3)]` with a
  `= null` default is still required, because the record crosses the **grain call boundary**.
- **The projection may never add, remove or reorder a turn.** Calls are harvested into a pending list as
  the walk proceeds and flushed onto the next assistant turn actually projected; a tool-only assistant
  message stays blank-text and still projects to `null`. Two features count that list and neither throws
  when it shifts: `ProjectForSearch` numbers turns by position and that ordinal is half of the Cosmos
  document id (ADR 0061), and `recoverPersistedReply` gates recovery on `turns.length >= priorTurns + 2`.
- **Results are collected over the whole history FIRST, in their own pass**, and joined onto each call by
  id. A single forward pass is the obvious shape and it is wrong: a result never arrives on the message
  that made the call, so when a model emits `content` **and** `tool_calls` together — an ordinary provider
  shape — that message projects to a real turn, the chain flushes onto it immediately, and the result
  arriving on the next tool message has nothing left to match. The chip would replay with an input and no
  result, silently and permanently. `ANarratedToolCall_StillCarriesItsResult` pins it.
- **One shared replay path, two callers.** `replayToolCalls` lives in `thread.js` and is called by
  `renderTurns` (a reopened chat, and the offline-cache replay) **and** by `recoverPersistedReply` (a
  turn whose stream died, ADR 0046). It de-dupes on call id and branches on three states — absent,
  present-but-running, present-and-done — because neither primitive is idempotent: `addToolCall` appends
  a chip unconditionally and `completeToolCall` appends a result body unconditionally, so a naive loop
  over a recovered bubble duplicates the chips and gives the finished ones two results each.
- **Every replayed call is completed, unconditionally.** A tool that returned null has no result to show,
  and a call the history holds no result for at all would otherwise replay stuck `is-running`, pulsing
  forever in a finished transcript, because `finishToolCalls` clears the *group's* lamp and not the chip's.
  Passing every call through `completeToolCall` marks it done and appends no result body. *(Deliberately
  **not** justified by ADR 0078: cancelling an Agent Framework run persists nothing at all, so a stopped
  turn cannot leave a dangling `FunctionCallContent` behind — measured on AF 1.19 and recorded in
  `gotchas.md`. The unconditional call is defence against a missing result, not against a stop.)*
- **Public share links are stripped, server-side.** `/api/share/{token}` maps every turn through
  `turn with { ToolCalls = null }` before responding.
- **Two deliberate drops.** Calls still pending at a user turn boundary are discarded (their reply never
  materialised, so the chips have no bubble to live in), and a call with no result replays without a
  result body. ADR 0068 retention needs no special handling: `RemoveLeadingTurns` always leaves a prefix
  starting at a user message, so no orphaned result can flush onto a later turn.

## Alternatives considered

- **Persist a separate tool-call record per turn.** Rejected: it is a migration, a second source of truth
  and a second thing to keep in step with retention, to recover data that is already in the session.
- **`[JsonIgnore]` on `ToolCalls` instead of a server-side strip** (the shape `AttachmentRef.BlobPath`
  uses). Rejected: it would hide the field from the **owner's** `/messages` response too, which is the
  very response the replay reads. The strip has to be selective to the public route.
- **Relying on the public viewer ignoring the field.** The viewer
  (`src/Agent42.Backend/wwwroot/shared/viewer.js`) is a standalone IIFE reading only
  `role`/`text`/`attachments`, so it would render nothing either way — but the field would still be in the
  JSON at `/api/share/{token}`, one DevTools tab away. Not rendering is not not-disclosing.
- **Showing tool calls on share links.** Declined by the user. Tool arguments and results carry search
  queries, fetched URLs and MCP/calendar payloads; widening a public link to include them is an ADR 0013
  egress extension, and would be recorded as one.
- **Replaying reasoning (ADR 0071) and follow-ups (ADR 0036) too.** Out of scope.
  `StripReasoningFromStorage` keeps reasoning out of the session deliberately, so there is nothing to
  replay; putting it back is a separate egress and storage decision.
- **Truncating replayed payloads.** Rejected as unmeasured. Tool returns are already capped at the tool
  boundary for the 2 MB session budget, so both the HTTP response and the offline mirror are bounded by
  construction. If a real conversation's `/messages` payload proves to be a problem, that is a new,
  measured decision.

## Consequences

- **A reopened chat now shows what the turn actually did.** The transcript stops being a text-only
  shadow of the turn the user watched.
- **Stream recovery gets strictly better, not merely equal.** ADR 0046 justified a recovered turn's
  missing chips by equivalence with a reload; that equivalence no longer holds on its own, so recovery
  replays too — and because the helper re-completes anything not already done, a chip the disconnect left
  pulsing is now **healed** rather than left running. That ADR's consequence line is corrected in this MR.
- **ADR 0072's "live-only" framing is retired**, and so is its stated reason. Its `finally` wiring stays
  — it is still right for the *live* turn — but the claim that nothing can heal a stuck group afterwards
  is now false, and is corrected in this MR along with the same claim in `send.js` and in
  `ToolCallFoldAssetsTests`. ADR 0072's "DevHost cannot show more than two tool calls in one turn" is also
  stale: ADR 0075's push-cap demo emits four.
- **ADR 0035's "full current history — text, images, and anything it contains" is no longer literal.**
  Tool calls are the first `ChatTurn` content deliberately withheld from a public link. `/api/share/*` is
  the only Easy-Auth-excluded surface carrying `ChatTurn`; ADR 0070's `AgentMcpRunner` and ADR 0022's
  `/api/agent-run` never call `GetHistoryAsync`.
- **Offline replay comes free, and the mirror gets fatter.** `renderTurns` serves both the network and the
  ADR 0032 cache path and `cachePut` mirrors the turn array verbatim, so a cached chat replays its chips
  with no extra work — and the IndexedDB mirror now also holds every serialized argument and result.
  `cache.js` has no pruning and no size ceiling; a quota failure degrades to "no offline copy", which is
  what it already documents. Bounded by the same tool-boundary caps as the HTTP response. Not worth code.
- **Incognito needs no new guard.** An ephemeral turn's calls sit in the live `_session` and replay in the
  owner's own UI for the rest of that activation — exactly as its *text* already does. ADR 0034's promise
  is "not persisted, not listed"; `ProjectForSearch` reads only `Text`, so tool calls never reach the
  index, and the share strip covers the public path. `recoverPersistedReply` returns early on ephemeral.
- **Degradation is free and asserted rather than engineered.** `StripToolHistory` (ADR 0013) removes tool
  content when a conversation moves to a tools-less model; the projection then finds nothing to attach and
  the reply renders exactly as it did before. A test pins that; no code does.
- **Coverage limit, stated rather than faked.** The three frontend criteria (one shared path, every call
  completed, de-dupe by id) are *source-text* guards, not behavioural tests — this repo ships no JS test
  runner by design (ADR 0041). The `renderTurns` guard was mutation-checked during execution: deleting the
  call from `renderTurns` fails `BothHistoryPainters_ReplayToolCalls`.
- **The offline browser leg was run**, headless Chrome over CDP against DevHost, both themes — send a real
  turn through `send.js`, reload the page, reopen the chat from history, and compare. The replayed group is
  identical to the live one in every measured field. `spam notifications` (4 calls, ADR 0075's push-cap
  demo): `grouped: true`, `open: false`, label `"4 tool calls"`, 4 chips all `is-done`, none `is-running`,
  lamp green, 8 io blocks (an input and a result per chip). `look up something` (1 call): `grouped: false`,
  `open: true`, summary `display: none` — flat and open, exactly as a one-call turn looked before this
  feature existed. **This is the verification ADR 0072 could not do**, because nothing survived a reload to
  look at.
