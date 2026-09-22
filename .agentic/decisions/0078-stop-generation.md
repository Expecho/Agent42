---
id: 0078
date: 2026-09-20
status: accepted
tags: [frontend, composer, orleans, agent-framework, ux, cancellation]
supersedes: []
extends: []
amends: [0037, 0043]
---

# A stop button that cancels the turn server-side and keeps what it streamed

> **Amended by [ADR 0082](0082-seeded-research-agent-loop.md) (2026-09-22): "the framework persists nothing" does not hold under a research loop.**
> Iterations that finished are already stored, the user message included, so the hand-append adds the user
> message only when nothing of the turn is stored and the reply only when its text came from the unfinished
> iteration.

> **Amended by [ADR 0079](0079-half-duplex-voice-on-speakers.md) (2026-09-20) — the stop button gains a
> second effect in voice mode.** Stopping the server does not stop the *speaking*: a voice turn plays out
> of a queue of sentences already fetched, so a stopped reply kept being read aloud. `voice.js` adds its
> own listener on `#stopBtn` that cancels playback and hands the mic back. It deliberately does **not**
> call `stopGeneration` — the handler in `send.js` is what does that, and two handlers would mean two
> POSTs. Since ADR 0079 closes the mic while the assistant speaks, this button is also one of the three
> gestures that replace talking over a reply. Everything here stays authoritative, including the
> abort-before-the-awaited-stop ordering, which is what makes the voice interrupt immediate.

## Context

The chat had no way to stop a reply. The client-side half of one already existed — `sendAbort`, the
`AbortController` voice barge-in uses (ADR 0037) — and ADR 0043 explicitly parked the button when it
removed the send button: *"Replace it with a stop-generation button. Rejected here as a new feature…
it would reuse `sendAbort` and deserves its own decision."*

Wiring a button to that abort alone would have been a lie, and the repo already knew why. ADR 0046's
measured property is that **Kestrel discards writes to an aborted connection instead of faulting
them**, so the endpoint keeps draining after the browser is gone, the turn completes, and the grain
persists the whole reply. That is load-bearing — it is how a backgrounded Android PWA recovers its
answer — and it means an abort-only stop spends every token and shows the full "stopped" reply on
reload. The opposite shortcut is just as bad: ADR 0070's gotcha records, mutation-checked, that a
consumer which merely **stops enumerating** leaves the iterator suspended before the persist, so the
turn is *lost* — tokens spent, conversation listed with a title and no messages.

So a real stop needed a third thing: cancel the model run, then persist anyway.

## Decision

An explicit signal, end to end: `POST /api/chat/{conversationId}/stop` → `IAgentGrain.StopAsync()`
→ cancel this turn's `CancellationTokenSource` → catch the cancellation inside the streaming loop →
**append the partial turn to the session and persist it like any other**. What the user saw is what
the conversation keeps. The stream then ends with a normal `Done` event carrying `stopped`.

Five things make it work, each measured or forced rather than chosen:

1. **The framework persists nothing on a cancelled run.** Measured on Agent Framework 1.19 with a
   fake slow `IChatClient` and an `InMemoryChatHistoryProvider` mirroring `BuildAgent`: after
   cancelling mid-stream, `SerializeSessionAsync` returns
   `{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[]}}}` — **empty, not partial**: no user
   message and no reply. The control run returns both. AF notifies the history provider only at *end
   of run*; the cancellation path takes the failure notification and rethrows. Identical when
   cancelling **during a tool call**. So the turn is appended by hand, by mutating the list
   `TryGetInMemoryChatHistory` hands back — the same list retention already prunes from — which was
   measured to serialize, round-trip through JSON, and reach the model on the next turn in order.
   A consequence worth stating: `reply` is the persistence path on this one branch, and nowhere else.
2. **A dead socket must never cancel a turn.** `HttpContext.RequestAborted` is deliberately not
   wired in: it is indistinguishable from the backgrounded-PWA case, so using it would silently
   truncate ordinary replies and break ADR 0046.
3. **`StopAsync` is `[AlwaysInterleave]`, and not `[OneWay]`.** A turn has long stretches where the
   activation is genuinely busy — before the first token, across a tool call — and a plain call would
   queue behind them, arriving after the turn it was meant to stop. One-way was rejected because it
   returns before the grain runs the method and swallows failures, which is exactly what the client's
   fallback branch needs to see.
4. **The stop handle is published by identity, and only by an interactive turn.** Two turns can be in
   flight on one activation: `ChatAsync` suspends between `yield return`s, so a fired reminder or an
   agent-run turn executes in the gap. A blindly assigned field would let a background turn steal the
   handle (so the user's press cancels *it*), or let one turn's disposal fault another's live run; a
   `ReceiveReminder` reset line — the shape every other turn-scoped field here uses — would let a
   reminder silently disarm the button. Hence `_turnCts ??= mine` when the turn is interactive, and
   `if (ReferenceEquals(_turnCts, mine))` on the way out, with no reminder site at all. The interactive
   flag is **captured before the first yield** rather than read at publish time, for the same reason: it
   is itself one of the blindly-assigned fields, so a background turn starting in a yield gap would
   otherwise leave it false and the live turn would never publish at all.
5. **An iterator cannot catch around a `yield`** (CS1626), so the streaming loop moved into its own
   iterator with the `catch` wrapping `MoveNextAsync()` alone — the same shape AF's own
   `ChatClientAgent.RunCoreStreamingAsync` uses.

**Stopped before any text arrived** stores the user message alone. The prompt is not lost, and there
is no empty assistant message to send back to the provider on every later turn. The client then
**removes the empty agent bubble**, because `recoverPersistedReply` counts rendered `.msg` elements
against stored turns (ADR 0046): leaving it would skew that yardstick by one for the rest of the
session, and every later backgrounded turn would be painted "Transmission failed" over a reply that
persisted fine.

**Barge-in (ADR 0037) now does both** — the local abort *and* the server stop. The abort is what keeps
the interruption immediate, because `onDelta` speaks whatever it is handed and is not gated by
`interrupted`; the stop is what makes the stored turn honest.

**Placement:** a fifth control in `.composer__frame` (after attach, tools, mic and voice), right of
`#voiceBtn`, `hidden` unless a turn is streaming, in the same 34×34 box as the mic. Visibility is the
`hidden` attribute, never `opacity: 0`, whose hit test survives (ADR 0052).

## Alternatives considered

- **Client-side abort only** (a button on `sendAbort`). Rejected: the model keeps running, the tokens
  are spent, and a reload shows the answer the user stopped. Would have been honest only if the button
  were labelled "hide".
- **Stop pulling the enumerator server-side.** Rejected: mutation-checked in ADR 0070 to lose the turn
  entirely, since the persist sits after the loop inside the iterator body.
- **Wire `HttpContext.RequestAborted` into the turn.** Rejected — see decision 2. It looks like an
  improvement ("don't burn tokens for an abandoned turn") and would break recovery.
- **Store an empty assistant message on a no-text stop**, keeping every turn a pair. Rejected: it is
  re-sent to the provider on every later turn and some reject it. The client-side bubble removal solves
  the invariant that motivated it.
- **Discard the turn entirely on a stop.** Rejected: the title is stamped before the turn runs, so a
  brand-new chat would be left titled with no messages — and `BuildUserTurnAsync` has already appended
  this turn's `TurnAttachments` entry, which would dangle.
- **Copy `RecordImageTurnAsync`'s snapshot → adapter → run → restore dance** to record the partial
  turn. Rejected: that exists to synthesise a model-shaped image response; direct list mutation is
  measured to work and is what retention already does.
- **Making background turns stoppable** (agent-run, reminders, `ask_agent`). Rejected as scope: nobody is
  watching them, so cancelling one loses a turn for no one's benefit. Agent-run and `ask_agent` drain the
  same core, so their run does carry a token and they simply never publish the handle; a **fired reminder
  is not cancellable at all**, because it calls `RunAsync` directly and never comes through that core.

## Consequences

- **A stopped turn's tokens are spent but uncounted.** Measured at the wire on DevHost: a stopped turn's
  `Done` event is `{"type":"done","stopped":true}` — no `inputTokens`/`outputTokens` at all, because usage
  arrives with the provider's final chunk, which never comes. The dashboard is fed by `gen_ai.usage`
  attributes on completed `chat` spans rather than by that event (ADR 0031), so the dashboard consequence
  is *reasoned from the same cause, not separately observed*. It undercounts; it never over-counts.
- **Stop halts generation; it does not roll anything back.** A tool that already ran has run — a push
  sent, a document ingested, a URL seeded. Only the model's own continuation stops.
- **Stop during image generation (ADR 0020) or the no-model notice does nothing.** Both branches
  `yield break` before the model run and observe no token, so the button is on screen and inert there.
  Gating it would require the client to know which branch the server took. Accepted residual.
- **Two browser tabs streaming the same conversation share one handle**, so a stop from either cancels
  whichever turn published first. A conversation is one grain; concurrent turns on it were already odd.
- **The tool calls of a stopped turn are not in its stored history** — only the prompt and the text. A
  partial tool chain can therefore never leave a `FunctionCallContent` without its result, which is the
  one pleasant surprise in the measurement.
- **`FakeChatClient` gained a stream gate**, and its slow-stream delay now observes the cancellation
  token. Without the token the fake was structurally uncancellable, so every test here would have hung
  or passed vacuously; without the gate, "stop after exactly one chunk" is a ~400 ms race against a
  shared TestCluster. A test that opens a gate and then runs a second turn **must** close it, or the run
  hangs instead of failing — that cost one 6.5-minute timeout during implementation.
- **Verified in this change:** the full suite is green (1534 passed, 1 skipped, including the 12 new
  `StopGenerationTests` and the extended endpoint/asset guards),
  `dotnet format` clean, and the two central claims are mutation-checked — replacing the append with
  `true` turns 7 of the 12 new tests red, and dropping the `_interactiveTurn` gate turns exactly
  `Stop_DoesNotCancelABackgroundTurn` red.
  **The server leg was exercised against the running app** (DevHost over HTTP, not only tests): a chat
  stream was opened, `POST /api/chat/{id}/stop` answered 204 mid-stream, the stream ended with
  `{"type":"done","stopped":true}`, and `GET /api/conversations/{id}/messages` returned the prompt plus a
  reply cut off mid-sentence. The **no-text** branch could not be reached that way — the canned client
  emits its first chunks faster than a second HTTP request can be dialled, so two deltas land even at a
  20 ms delay — and rests on `Stop_BeforeAnyText_StoresTheUserMessageOnly`, which reaches it
  deterministically with a stream gate.
  **The DOM leg is unverified**, and recorded as a residual rather than skipped: no browser driver was
  available in the session (the extension was not connected), so "the button appears with the turn and
  disappears with it", "the note renders", and "the empty bubble is removed" are pinned by source-text
  guards only — the same treatment ADR 0043 and ADR 0037 gave their interactive halves. The first real
  browser to load this is also the first proof of the bubble-removal behaviour that protects ADR 0046's
  yardstick.
- Returning PWA users need a new cache generation (`agent42-shell-v38` → `-v39`): this diff adds a
  `dom.js` export and a `thread.js` export that `send.js` imports, exactly the cross-module skew that
  becomes a `SyntaxError` and a blank app.
