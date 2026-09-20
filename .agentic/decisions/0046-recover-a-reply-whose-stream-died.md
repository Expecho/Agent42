---
id: 0046
date: 2026-08-22
status: accepted
tags: [frontend, pwa, mobile, conversations, streaming, resilience, ux]
supersedes: []
extends: [0004, 0032]
amends: []
---

# Recover a reply whose stream died while the app was backgrounded

## Context
Switching away from the installed PWA during a reply and coming back shows **"Transmission failed."**
Refreshing then shows the complete answer. The reply was never lost — only its delivery was.

The mechanism, measured rather than assumed:

- Android freezes a backgrounded PWA. The renderer stops, and the response socket can die with it, so
  `reader.read()` in `send()` rejects — with a `TypeError`, not an `AbortError` — the instant the app is
  resumed. `send()`'s `catch` has exactly one non-abort branch, so it paints the error.
- **The server never noticed.** Kestrel *discards* writes to an aborted connection instead of faulting
  them (verified in `ChatStreamDisconnectTests` with a real loopback RST: `Response.WriteAsync` produced
  no exception at all), so the endpoint's `await foreach` keeps draining, the turn runs to completion,
  and `AgentGrain` writes its session as usual. That is precisely why a refresh shows the reply.

So the failure the user sees is a lie about the only thing they care about, and the true answer is
already sitting one `GET /api/conversations/{id}/messages` away.

## Decision
**Recover client-side. No server change at all.** On a non-abort stream failure, `send()` re-reads the
conversation history and adopts the persisted reply into the bubble it was already streaming into; only
if nothing shows up does it paint the error.

Rejected alternatives:

1. **A resumable stream** — per-turn event ids plus a reattach endpoint (or SSE with `Last-Event-ID`).
   The honest fix for "rejoin a turn still in flight", and much larger: the grain would have to buffer a
   turn's events somewhere replayable, and its state item is already the thing ADR 0004 guards at 2 MB.
   It also rewrites the NDJSON wire format for a case where the final answer is durable anyway.
2. **A Web Push on completion** (ADR 0028 already has the plumbing). Tells the user it finished; leaves
   the failed bubble exactly as wrong as it was.
3. **Keeping the connection alive** (heartbeats, `navigator.wakeLock`). A wake lock keeps the *screen*
   on; nothing keeps a frozen app's socket alive when the OS decides otherwise.
4. **Poll on every failure, for as long as it takes.** Hides a genuinely broken connection behind a
   caret that never stops blinking — see the backgrounded gate below.

### Four load-bearing details
- **The backgrounded flag must be latched by a listener.** A frozen PWA runs no JS, so the rejection is
  not observed until the app is *already visible again*: reading `document.hidden` inside the `catch`
  always says "no". `send()` therefore seeds `backgrounded` from `document.hidden` and keeps it updated
  through a `visibilitychange` listener registered for the life of the turn (removed in the `finally`).
- **The gate exists to keep honest failures fast.** A turn that was backgrounded polls up to 15 × 2 s —
  enough for a tool-calling turn that was still running when the app came back. A turn that never left
  the foreground gets **one** cheap look and then fails, because there the error is almost always real
  and the user is watching.
- **`priorTurns` is the yardstick, taken before the turn's own bubbles are appended.** Rendered `.msg`
  elements are 1:1 with history turns (`renderTurns` builds one each), so the server is holding *this*
  turn once its history has grown by two and ends on a non-user turn. Snapshot it after `addMessage` and
  it counts the new bubbles and never matches.
- **Recovery is a success, so it runs the success tail** — `setPendingAgentId(null)` (the persona *is*
  bound server-side; leaving it set would rebind on the next turn) and `setTopbarShare(...)` (the
  conversation persisted, so it is shareable — ADR 0044). It also refreshes the ADR 0032 offline mirror
  with the history it just fetched, and re-renders any generated image (ADR 0020) from the history,
  since the live `image` event went down with the stream.

### The server guarantee is implicit, and is now pinned by a test
Nothing in `AgentApi.Conversations.cs` says "finish the turn even if the client leaves" — Kestrel's
discard-on-abort gives it away for free. That makes it invisible and easy to delete: honouring
`HttpContext.RequestAborted` so an abandoned turn stops burning tokens is an obvious-looking improvement
that would silently break this whole feature. `ChatStreamDisconnectTests` hosts the endpoint on **real
Kestrel** (TestServer's in-memory pipe cannot reproduce a dead transport — measured: cancelling a
TestServer request leaves the server none the wiser, so the test would be vacuous there), kills the
socket mid-reply with `SO_LINGER 0`, and asserts the reply lands in the history anyway.

## Consequences
- **No egress change (ADR 0013), no new endpoint, no state, no infra.** Recovery reuses the history read
  the UI already performs on every conversation open.
- **An incognito chat is never recovered.** ADR 0034 turns are never written, so `recoverPersistedReply`
  returns immediately — otherwise every ephemeral failure would poll for half a minute for something
  that cannot exist.
- **A recovered turn loses its live-only detail**: the token-usage line and the ADR 0036 follow-up chips do
  not come back. That is exactly what a reload gives, and the reply itself is complete. *(Tool-call rows
  were in that list until ADR 0080. They now come back — recovery calls the same `replayToolCalls` helper a
  reload does, which is what keeps the equivalence claimed here true. It is in fact a strict improvement:
  re-completing anything not already done **heals** a chip the disconnect left pulsing.)*
- **A turn slower than ~30 s still shows the error.** Self-healing rather than wrong: the conversation is
  in the sidebar and opening it renders the reply.
- **Abandoning a turn has never saved the generation**, and this decision now depends on that. Closing a
  tab mid-reply pays for the whole turn — pre-existing behaviour, documented here rather than introduced.
- **Verified in a real browser, both directions.** Headless Chrome over CDP against DevHost: forcing
  `document.hidden`, sending, then `window.stop()` mid-reply. With the change the bubble ends up carrying
  the full persisted answer, no `is-error`, copy button present, share enabled. With `send.js` reverted,
  the same script yields `is-error` and *"Transmission failed. network error"* — the reported bug,
  reproduced and then removed. Shell cache bumped `agent42-shell-v17` → `-v18`.
