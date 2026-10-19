---
id: 0089
date: 2026-10-09
status: accepted
tags: [streaming, tools, frontend, stream-events, shared-state, human-in-the-loop, pwa, no-migration]
supersedes: []
extends: [0004, 0021, 0082, 0084]
amends: [0046]
amended_by: 0093   # get_current_location became a fourth client tool, under its own location key
---

# Client (browser-run) tools and shared agent↔UI state on the NDJSON stream

> **Amended by [ADR 0106](0106-agentgrain-services-and-turn-context.md) (2026-10-19): the broker async-local is generalised into `TurnContext`.** `CurrentClientToolBroker` is gone, and tools read `TurnContext.CurrentOrClosed.Broker`. The context is set by `PullAsync` and `DisposeRunAsync`, as the broker was.

> **Amended by [ADR 0104](0104-prompt-prefix-caching.md) (2026-10-19): shared state is shown to the model as a trailing
> user-role message, not an instruction block.** The state changes whenever anyone edits it, and the instructions are the
> front of the cached prompt prefix. The message is tagged `AIContextProvider`-sourced, so it is still never stored in the
> session and never accumulates.

## Context
AG-UI's two real differentiators are frontend-executed tools and shared state. ADR 0088 deferred the protocol
itself (its revisit trigger 2 names exactly these two). Agent42 owns both ends of its chat wire, so both can be
built on the existing NDJSON stream and the conversation grain. That keeps recovery (0046), stop (0078) and the
MCP reconnect (0087), which an AG-UI endpoint that bypasses the grain would lose. The three first client tools are
`confirm_action` (human-in-the-loop approval), `control_artifact_pane` (drive the 0084 pane) and
`get_browser_context` (time zone, locale, theme, viewport, installed/online, selected text). ADR 0093 later added a
fourth, `get_current_location`, which keeps its own `location` selectable key rather than `browser_tools`.

## Decision
**Client tools.** The tools are server-defined and registered like any other tool. The browser only advertises
the names it can run (`clientTools` on the chat request), and the endpoint intersects that list with
`ClientToolLimits.ToolNames`. The call reaches the browser as the ordinary `toolCallStart`, flagged
`clientTool: true`. The tool then waits on a per-run `ClientToolBroker`. The browser posts the result to
`POST /api/chat/{id}/tool-result`, which goes through `[AlwaysInterleave]` `SubmitClientToolResultAsync`, and the
turn resumes. The broker is routed **per run** through an `AsyncLocal` that is set around that run's own
`MoveNextAsync` pulls (measured, spike 0a: a background run pulled in the gap saw null). It is never routed
through a field. Only an interactive turn whose browser advertised tools has a broker. A background run, a
reminder, `ask_agent` and a client that advertised nothing get `{"status":"unavailable"}` at once. The **wait**
is capped (`confirm_action` 120 s, the others 15 s), never the turn. A timeout is a normal
`{"status":"timeout"}` result, and Stop cancels through the turn token. A late answer is a 404 that the client
ignores. Recovery (`GET /api/chat/{id}/pending-tool-calls`) lets a page whose stream died answer a waiting call.
Results are truncated to 4000 chars.

**Shared state.** One JSON object per conversation, stored as text in `AgentGrainState.SharedStateJson` with an
optimistic-concurrency `SharedStateVersion`. The model changes it with `update_shared_state` (RFC 6902 ops,
all-or-nothing, at most 16 KB, 8 levels deep and 50 ops). After the tool result the run streams a
**whole-state** `stateSnapshot`. The UI reads and edits it through `GET`/`PUT /api/conversations/{id}/state`
(`baseVersion`, 409 when stale). The model sees the current state as an instruction block on every run. A
`checklist` key renders as an editable checklist. Nothing writes the grain state mid-run: a user edit during a
run is held in memory and persisted by the end-of-turn write, or by the last run to finish.

New selectable keys `browser_tools` and `shared_state` are appended last.

## Alternatives considered
- **AG-UI-style client-declared tool schemas.** Lost: browser-supplied descriptions would be model-facing prompt
  text from outside the server, per-request tools fight Agent Framework's `AdditionalTools` overwrite, and tools
  are built per activation, not per request.
- **A broker field on the grain that the tool reads.** Lost: two runs can be in flight on one activation, so a
  background run's `confirm_action` would be routed to the user's open browser as if their own turn asked.
  Mutation-checked: routing through the registry instead of the async local turns
  `BackgroundRun_CannotRouteToTheInteractiveBroker` red.
- **Keying the run by its user `ChatMessage` (the ADR 0082 `ConditionalWeakTable`).** Measured to hold too, but it
  depends on message identity surviving the history provider. The async local depends on nothing in AF.
- **A resumable stream for recovery.** Lost for the reason 0046 gave. Polling a pending-calls endpoint is enough.
- **`stateDelta` events on the wire.** Lost: at a 16 KB cap a snapshot costs nothing, and snapshots are idempotent
  under recovery and reconnection. This is a deliberate deviation from AG-UI's delta events.
- **`JsonNode` in grain state.** Lost: Orleans cannot serialize it. Text round-trips through Cosmos unchanged.
- **Writing a user edit at once, even mid-run.** Lost: `WriteStateAsync` serializes the whole state, so it would
  persist the run's half-done fields beside the previous turn's session.
- **Migrating the research checklist into shared state now.** Lost for now: `TodoCompletionLoopEvaluator` reads
  `TodoProvider`. It is a follow-up, and the checklist renderer is built so that follow-up only supplies data.

## Consequences
- A turn can now pause on the user. Stop, the 0046 recovery and a quiet late answer all work while it waits.
  The Container Apps request timeout is 240 s (documented), so a confirm cannot by itself outlive its request.
  Orleans delivered the call event at 0.2 s and the next item after a 150 s wait with no timeout (spike 0b).
- `confirm_action` is advisory. The model is told in the description to act only on `{"approved":true}`, but no
  existing tool is hard-gated on it. A hard gate on specific tools (push, MCP writes) is a separate decision.
- Both keys land **off for anyone who ever pressed Save** (0021), and are on for a null selection. Copilot chats
  (0085) never get the four tools: Copilot calls tools from its own RPC thread, outside the run's async local,
  and a 120 s wait under its RPC timeout is unmeasured.
- Incognito (0034) has no shared state: the tool refuses on an ephemeral turn, the injection is skipped, and a
  `PUT` on a conversation with no saved record is a 409. Client tools work in incognito; nothing they do persists
  beyond the turn's own (unwritten) session.
- No new egress (0013): the results come from the user's own browser, and the state stays in the conversation.
- The JSON Patch library (`Microsoft.AspNetCore.JsonPatch.SystemTextJson`, first-party) is not atomic by itself
  and silently skips unknown ops (both measured), so `SharedStatePatch` validates op names and patches a copy.
- Amends 0046: recovery now also answers the client tool calls a turn is still waiting on.
