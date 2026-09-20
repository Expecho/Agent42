---
planned-against: main @ aeaf4dc
---

# Stop generation: a button that actually cancels the turn

## Overview

The chat has no way to stop a reply once it starts. The client-side machinery for *stopping to
watch* already exists (`sendAbort`, ADR 0037 barge-in), but nothing stops the **server**: Kestrel
discards writes to a dead socket, the endpoint keeps draining, and the grain persists the full
reply (ADR 0046). So an abort-only button would burn every token and show the whole answer on
reload — a button that lies.

This plan adds a real stop: an explicit signal reaches the grain mid-turn, cancels the model run,
and **still persists what was already streamed**, so what the user sees is what the conversation
keeps. ADR 0043 deferred exactly this feature ("deserves its own decision"), so it needs an ADR.

## Affected areas

| File | Change |
|------|--------|
| `src/Agent42.Agents/Conversations/IAgentGrain.cs` | new `StopAsync` — `[AlwaysInterleave]`, **not** `[OneWay]` |
| `src/Agent42.Agents/Conversations/AgentGrain.cs` | `_turnCts` handle (published only by an interactive turn); `StopAsync`; streaming loop extracted + cancellation caught; append-partial-turn helper; suggestions skipped when stopped; class-summary paragraph updated |
| `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` | `Stopped` flag on the `Done` event (`[Id(12)]`, **nullable**) |
| `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` | `POST /api/chat/{conversationId}/stop` |
| `src/Agent42.Backend/wwwroot/index.html` | `#stopBtn` in `.composer__frame` |
| `src/Agent42.Backend/wwwroot/styles.css` | `.composer__stop`, `.thread__stopped` |
| `src/Agent42.Backend/wwwroot/js/dom.js` | `stopBtn` export |
| `src/Agent42.Backend/wwwroot/js/send.js` | `stopGeneration()`; button visibility; `stopped` handling |
| `src/Agent42.Backend/wwwroot/js/thread.js` | live-only stopped marker, **including the drop-the-empty-bubble branch** |
| `src/Agent42.Backend/wwwroot/js/voice.js` | barge-in fires the stop **and** keeps the abort; two stale comments |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` v38 → v39 (no new module, `SHELL` unchanged) |
| `tests/Agent42.Tests/FakeChatClient.cs` | cancellable slow delay **+ a deterministic stream gate** |
| `tests/Agent42.Tests/StopGenerationTests.cs` | **new** — grain-side suite, `SharedClusterTests<SharedClusterFixture>` |
| `tests/Agent42.Tests/AgentApiTests.cs`, `EasyAuthExclusionTests.cs`, `ComposerAssetsTests.cs`, `VoiceConversationAssetsTests.cs`, `StreamRecoveryAssetsTests.cs`, `PwaAssetsTests.cs`, `SearchAssetsTests.cs` | endpoint, exclusion, asset, barge-in, recovery-invariant and cache-generation guards |
| `.agentic/decisions/0078-stop-generation.md` + amendment notices in `0037`, `0043` | ADR, bidirectional per repo convention |
| `.agentic/gotchas.md`, `CLAUDE.md` | two gotchas; feature-map row; Orleans version drift (says 10.2, pinned 10.3.1) |

## Context & constraints

### Measured during planning — this is the whole design (AF 1.19.0, spike, 2026-09-20)

1. **A cancelled `RunStreamingAsync` leaves the session EMPTY.** Not "partial" — empty. Measured
   with a fake slow `IChatClient` + `InMemoryChatHistoryProvider`, mirroring `BuildAgent`'s wiring:
   after cancelling mid-stream, `SerializeSessionAsync` returns
   `{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[]}}}` — **neither** the user message
   **nor** the partial reply. The control run (no cancellation) returns both. AF only notifies the
   history provider at *end of run*; the cancellation path takes the failure notification and
   rethrows. Same empty result when cancelling **during a tool call**.
   **Consequence:** "catch the cancellation and let it fall through to the persist" is NOT
   sufficient. Persisting alone would write an empty session — the title is derived *before* the
   turn (`AgentApi.Conversations.cs:116-120`), so the result is the exact failure shape the ADR 0070
   gotcha names: *a conversation listed with a title and no messages*. The turn must be appended
   to the session **by us**.
2. **Appending it ourselves works, and round-trips.** Measured: `TryGetInMemoryChatHistory` hands
   back the live `List<ChatMessage>`; adding the user message + an assistant message with the
   partial text serialises, survives serialize → JSON → deserialize, and the **next** turn's
   request carries all three messages in order (`user`, `assistant` partial, new `user`). Same list
   `AgentGrain.Retention.cs` already prunes from, and that file's header states the round-trip
   property this relies on.
3. **The exception surfaced is `TaskCanceledException`** (an `OperationCanceledException`), so catch
   the base type.
4. **No dangling tool call is possible on this path** — nothing is written, so a cancelled tool call
   cannot leave a `FunctionCallContent` without its result. The pre-plan worry was the opposite of
   the real risk.
5. **The exemplar for "record a turn without inference" is `RecordImageTurnAsync`**
   (`src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs:162`). **Do not copy its
   snapshot → adapter → run → restore dance** — that exists to synthesise a *model-shaped* image
   response. Copy only its mutate-then-serialize half; direct list mutation is measured (2) and is
   what Retention already does.
6. **`TurnAttachments` alignment survives the stop by construction**: `BuildUserTurnAsync` appends
   this turn's entry *before* the run (`AgentGrain.cs:936-937`), so hand-appending the user message
   restores the 1:1 join `ProjectUserTurn` and `RemoveLeadingTurns` rely on. This is also the
   decisive argument against the discard-the-turn option: it would leave a dangling entry.

### Constraints the implementation must respect

- **Never wire `HttpContext.RequestAborted` into the turn.** A dead socket and an intentional stop
  are indistinguishable there, and a backgrounded Android PWA produces the former on every turn —
  wiring it would silently truncate replies and break ADR 0046 recovery. The stop must be its own
  explicit signal.
- **`ChatCoreAsync` is an iterator, so its streaming loop cannot be wrapped in try/catch** — CS1626
  forbids `yield return` inside a try that has a `catch` (a try/`finally` is fine). Catch around
  `MoveNextAsync()` on a **manual enumerator**, yielding outside the try. AF's own
  `ChatClientAgent.RunCoreStreamingAsync` is built the same way, and for the same reason.
- **The stop handle must NOT be a plain activation-scoped field, and the `_pushesThisTurn`
  two-site shape does NOT transfer.** Two turns can be in flight on one activation: `ChatAsync`
  suspends between `yield return`s, so `ReceiveReminder` / `EnqueuePromptAsync` can run in the gap —
  that is the documented reason `_turnLocation` needs explicit clearing (`AgentGrain.cs:186-190`).
  A field blindly assigned per turn would (a) let a background turn steal the handle so the user's
  stop cancels *it* instead, (b) let one turn's disposal fault another turn's live run, and (c) with
  a null-on-entry in `ReceiveReminder`, let a reminder silently disarm the button. **Required
  shape:**
  ```csharp
  using var turnCts = new CancellationTokenSource();       // owned by THIS turn, always
  if (_interactiveTurn) { _turnCts ??= turnCts; }          // publish, never steal
  try { /* the turn */ }
  finally { if (ReferenceEquals(_turnCts, turnCts)) { _turnCts = null; } }
  ```
  So: every turn gets a token; only a browser turn is *reachable* by `StopAsync`; a turn only ever
  clears its own handle; and `ReceiveReminder` gets **no** reset line. This is what keeps
  "background turns are out of scope" true rather than aspirational.
- **`StopAsync` must not throw into the grain.** `Cancel()` throws `ObjectDisposedException` after
  disposal and `AggregateException` if a registered callback throws, and it runs those callbacks
  **inline on the grain's thread**. Use `CancelAsync()` inside a try/catch that swallows and logs.
  The unpublish-then-dispose order above is load-bearing and must be stated in code.
- **`[AlwaysInterleave]` on the interface declaration** (not the implementation — Orleans documents
  that explicitly). It is load-bearing for the long gaps in a turn: before the first token, and
  across a tool call, where a plain call would queue behind the in-flight request. It must **not**
  be `[OneWay]`: a one-way call returns before the grain runs it and swallows failures, which would
  make the client's fallback unreachable.
- **Appending directly to the history list bypasses both storage filters**
  (`AgentGrain.BuildAgent.cs:295-302`). Safe *here*, and the code must say why rather than assume:
  the user message is the one `BuildUserTurnAsync` built, not an `AIContextProvider` message (the
  request filter would have kept it verbatim), and the assistant message is built from `reply`,
  which accumulates **text only** — reasoning goes to its own event (so the response filter would
  have had nothing to strip).
- **`reply` becomes a persistence path on this ONE branch.** The gotcha "`reply` is NOT the
  persistence path" stays true everywhere else; the stopped branch is the deliberate exception,
  because AF hands us nothing to persist. Both the gotcha and the code must say so.
- **The `.msg` ↔ history 1:1 invariant is load-bearing and this feature can break it.**
  `recoverPersistedReply` matches on `turns.length >= priorTurns + 2` against
  `thread.querySelectorAll(".msg").length` (`send.js:112, 208-211`, pinned by
  `StreamRecoveryAssetsTests.cs:64`). A stop *before any text* stores one turn but would leave two
  bubbles on screen, permanently skewing the yardstick for the rest of the session — so a
  backgrounded PWA would then get "Transmission failed" over turns that persisted fine, the exact
  lie ADR 0046 exists to remove. **Therefore: on a stop with no text, remove the empty agent bubble
  and show the notice as a non-`.msg` element.**
- **Egress:** nothing new leaves the process; stopping *reduces* egress. Not an ADR 0013 extension.
- A stopped turn **does not undo side effects already taken** — a tool that ran, a push the model
  sent, a web document ingested. Stop halts generation; it is not a rollback.

### Decisions taken in the interview (do not re-litigate)

- **Placement:** a fourth button in `.composer__frame`, right of `#voiceBtn`, hidden unless
  streaming, matching the 34×34 / 9px-radius `.composer__mic` shape. This revisits ADR 0043's "two
  composer layouts" objection: accepted because the control is *additive and transient*, not a
  second permanent layout.
- **Stop before any text arrived:** persist **the user message alone** (so the prompt is not lost),
  and drop the empty agent bubble client-side per the invariant above. Accepted residual: the next
  request then carries two consecutive user messages — fine for the OpenAI chat API, unverified
  against every provider configured here.
- **Voice barge-in routes through the stop, and KEEPS the abort.** Both, unconditionally. The abort
  is what makes barge-in immediate — `onDelta` is not gated by `interrupted` (`voice.js:344`), so a
  stop-only barge-in would keep speaking the buffered tail for a full round trip, reversing ADR
  0037's stated property. The stop is what fixes "the server stored a reply nobody heard".
- **The stopped mark is live-only** — no new persisted field, no `ChatTurn` change. A reload shows
  the partial text plainly, exactly as reasoning and tool chips already behave.

### Pointers

- Gotchas to read first: "A chat turn outlives its client"; "Bounding a drained agent turn";
  "A grain's turn-scoped field lives for the ACTIVATION"; "The push-per-turn counter resets in TWO
  places"; "`reply` is NOT the persistence path"; "Every MR that touches `wwwroot/js/` must bump
  `CACHE`"; "The usage dashboard counts tokens from `chat` spans ONLY".
- ADRs: 0046 (the thing not to break), 0070 (losing a turn), 0043 + 0037 (both amended), 0036, 0004,
  0034, 0031, 0055 (test-cluster convention).

## Steps

### Step 1 — `StopAsync` reaches a busy grain, and only a browser turn

`IAgentGrain.cs`, `AgentGrain.cs`.

- `[Alias("StopAsync")] [AlwaysInterleave] Task StopAsync();` — documented as "cancels the turn a
  browser is currently streaming from this activation; a no-op otherwise".
- `_turnCts` declared beside the turn-scoped family, with a comment stating why it is **not** a
  member of that family's reset ritual: it is published/unpublished by identity (see the required
  shape above), has **no** `ReceiveReminder` line, and a background turn never publishes it.
- `StopAsync` → `await _turnCts.CancelAsync()` guarded by a null check and a try/catch that logs.
- Update the class-summary paragraph at `AgentGrain.cs:22-29`, which enumerates the turn-scoped
  clears and justifies `ReceiveReminder` living in that file — a reader hits it first.

**Verify:** `dotnet test Agent42.slnx --filter StopGenerationTests` (ACs 9, 10, 11).

### Step 2 — the fake must be cancellable *and* deterministic (test infra)

`tests/Agent42.Tests/FakeChatClient.cs`.

- The slow-stream delay (`:216`) is `Task.Delay(SlowStreamChunkDelayMs)` with **no token**, so a
  compiler-generated async iterator never observes cancellation and every stop test would hang or
  pass vacuously. Pass `cancellationToken`; note that this mirrors a real provider (the OpenAI SDK
  passes the token into the HTTP read).
- Add a **stream gate**: an opt-in `TaskCompletionSource` pair (`FirstChunkStreamed` signalled after
  the first reply chunk, `ReleaseStream` awaited before continuing). Without it, "stop after exactly
  one chunk" is a ~400 ms race against TestCluster scheduling and ACs 1/2/4 would flake. The gate is
  inert unless a test sets it, so no existing test changes behaviour.

**Verify:** `dotnet test Agent42.slnx --filter ChatStreamDisconnectTests` stays green — it cancels
nothing, so its behaviour must not move.

### Step 3 — catch the cancellation, append the turn, persist (the core)

`AgentGrain.cs`, `AgentStreamEvent.cs`.

- Extract the `await foreach` over `RunStreamingAsync` into
  `private async IAsyncEnumerable<AgentStreamEvent> StreamModelReplyAsync(ChatMessage userMessage,
  StringBuilder reply, TurnTokenTotals tokens, TurnOutcome outcome, CancellationToken turnToken)`:
  manual `GetAsyncEnumerator(turnToken)`, `try { … MoveNextAsync() … } catch
  (OperationCanceledException) { outcome.Stopped = true; break; }`, yields **outside** the try,
  `DisposeAsync` in a `finally`. The extraction keeps the `catch` off the yielding path and keeps the
  added branching out of `ChatCoreAsync`, which is already close to the S3776 limit (a build error at
  15). `TurnOutcome` is a tiny class for the same reason `TurnTokenTotals` is one — an iterator
  cannot hand out a `ref`.
- When `outcome.Stopped`: `AppendStoppedTurnToSession(userMessage, reply)` —
  `TryGetInMemoryChatHistory`, add `userMessage`, then add
  `new ChatMessage(ChatRole.Assistant, reply.ToString()) { AuthorName = … }` **only when
  `reply.Length > 0`**. **If `TryGetInMemoryChatHistory` returns false, log a warning and skip the
  persist entirely** — persisting then would write a session with neither message, which is the
  failure shape this whole design exists to avoid. (Every other caller early-returns harmlessly;
  this one must not.)
- Then fall through to `PersistTurnUnlessEphemeralAsync()` unchanged.
- Skip `SuggestFollowUpsAsync` when stopped — a second model call on a reply just cancelled.
- `Done` carries the flag: `[property: Id(12)] bool? Stopped = null` — **nullable**, because the
  NDJSON serializer is `WhenWritingNull` (`AgentApi.Conversations.cs:14-18`) and a non-nullable bool
  would add `"stopped":false` to every per-token event on every turn. `TurnTokenTotals.ToDoneEvent`
  takes the flag. The flag settles the race where the reply finishes as the button is pressed: that
  turn completed and must not be labelled.

**Verify:** `dotnet test Agent42.slnx --filter StopGenerationTests` (whole class).

### Step 4 — the endpoint *(parallelizable with Step 5)*

`AgentApi.Conversations.cs` — `app.MapPost("/api/chat/{conversationId}/stop", …)` beside the chat
route, `grains.GetGrain<IAgentGrain>(AgentKey(http.GetUserId(), conversationId)).StopAsync()`,
`Results.NoContent()`. No new partial, so `MapAgentApi` is untouched. Authenticated by omission:
`/api/chat` is not in Easy Auth's `excludedPaths` — add the route to the authenticated-paths list
`EasyAuthExclusionTests` pins.

**Verify:** `dotnet test Agent42.slnx --filter "AgentApiTests|EasyAuthExclusionTests"`.

### Step 5 — the button *(parallelizable with Step 4)*

- `index.html`: `<button class="composer__stop" id="stopBtn" type="button" hidden
  aria-label="Stop generating">` after `#voiceBtn`, inline glyph like its neighbours.
- `dom.js`: `export const stopBtn = …` beside `micBtn`/`voiceBtn`.
- `styles.css`: `.composer__stop` copying the `.composer__mic` box with its own hover/active colour,
  plus `.thread__stopped` for the no-text notice. Comment that `hidden` is the visibility mechanism,
  so there is no `opacity: 0` hit-test trap (the ADR 0052 lesson).
- `send.js`: export `stopGeneration()` — `POST /api/chat/{id}/stop`, falling back to
  `sendAbort?.abort()` on failure so the button always does something; reveal the button at the top
  of `send()` and hide it in the `finally` beside `sendAbort = null`; on a `done` event carrying
  `stopped`, call the thread marker. Wire the click in `wireSend()`.
- `thread.js`: `markStopped(agentMsg, hasText)` — with text, append a small live-only note inside the
  bubble that the copy button cannot capture (it captures `answer`); **without text, remove the agent
  `.msg` element** and render the notice as a non-`.msg` `.thread__stopped` element, preserving the
  `.msg` ↔ history invariant ADR 0046 rides on.
- `voice.js`: barge-in (`:253`) fires `stopGeneration()` **and** keeps `sendAbort.abort()`; fix the
  stale comments at `:253` and `:267-268`.
- `sw.js`: `CACHE` `agent42-shell-v38` → `-v39`; `SHELL` unchanged. **Two tests pin the old string**
  (`PwaAssetsTests.cs:255`, `SearchAssetsTests.cs:127-128`) — update both.
- `VoiceConversationAssetsTests.cs:43-51` pins barge-in on `sendAbort`/`AbortController`; extend it to
  also pin the stop call, or its assertion survives only incidentally.

**Verify:** `dotnet test Agent42.slnx --filter "ComposerAssetsTests|PwaAssetsTests|SearchAssetsTests|VoiceConversationAssetsTests|StreamRecoveryAssetsTests"`.

### Step 6 — docs, same MR

- `.agentic/decisions/0078-stop-generation.md`, plus the repo's **bidirectional** convention: an
  `> **Amended by [ADR 0078](…) (2026-09-20).**` notice inside `0037` and `0043`.
- `.agentic/gotchas.md` — two entries: (a) *a cancelled AF run leaves the session empty, so the
  stopped turn is appended by hand, and `reply` is the persistence path on that one branch*;
  (b) *the stop handle cannot be a plain turn-scoped field (two turns share an activation), an
  iterator cannot catch around a `yield`, and `StopAsync` must be `[AlwaysInterleave]` and not
  `[OneWay]`*.
- `CLAUDE.md` — 0078 into the conversations row; fix the stack line (`Orleans 10.2` → 10.3, per
  `Directory.Packages.props`).
- Residuals the ADR must record: a stopped turn's tokens are **spent but uncounted** (the dashboard
  reads `gen_ai.usage` from completed `chat` spans — ADR 0031; *asserted, not measured*); stop during
  **image generation** (ADR 0020) or the no-model notice is a no-op, because those branches
  `yield break` before the model run and observe no token; two browser tabs streaming the same
  conversation share one handle, so a stop from either cancels whichever published first; and stop
  does not undo a tool that already ran.

**Verify:** `dotnet test Agent42.slnx` + `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Stop ends the stream and the partial reply is persisted | `Stop_PersistsThePartialReply` | `StopGenerationTests.cs` |
| 2 | The persisted reply is shorter than the full canned reply | `Stop_PersistsLessThanTheWholeReply` | `StopGenerationTests.cs` |
| 3 | The user's own message survives the stop | `Stop_KeepsTheUserMessageInHistory` | `StopGenerationTests.cs` |
| 4 | The partial reply reaches the next turn as history | `Stop_PartialReplyReachesTheNextTurn` | `StopGenerationTests.cs` |
| 5 | Stopped before any text → user message alone, no empty assistant turn | `Stop_BeforeAnyText_StoresTheUserMessageOnly` | `StopGenerationTests.cs` |
| 6 | No suggestion call on a turn stopped **after** a delta (the non-vacuous case) | `Stop_AfterADelta_DoesNotGenerateSuggestions` (asserts the fake's suggestion counter) | `StopGenerationTests.cs` |
| 7 | The stream still terminates with a `Done`, flagged stopped | `Stop_StillEmitsDone_FlaggedStopped` | `StopGenerationTests.cs` |
| 8 | A turn that completed normally is not flagged, and omits the field on the wire | `CompletedTurn_IsNotFlaggedStopped` | `StopGenerationTests.cs` |
| 9 | Stop with no turn in flight is a silent no-op | `Stop_WithNoTurnInFlight_IsANoOp` | `StopGenerationTests.cs` |
| 10 | A stop cannot leak into the following turn | `Stop_AfterTheTurnEnded_DoesNotAffectTheNextTurn` | `StopGenerationTests.cs` |
| 11 | **A background turn is not cancellable** — stop during an `EnqueuePromptAsync` turn leaves it to complete and persist in full | `Stop_DoesNotCancelABackgroundTurn` | `StopGenerationTests.cs` |
| 12 | An incognito stopped turn persists nothing, yet the partial turn is in the live session | `Stop_OnAnEphemeralTurn_PersistsNothing` | `StopGenerationTests.cs` |
| 13 | A client that merely disconnects is still NOT cancelled (ADR 0046 intact) | existing suite stays green | `ChatStreamDisconnectTests.cs` |
| 14 | `POST /api/chat/{id}/stop` → 204, unknown id included | `StopChat_Returns204` | `AgentApiTests.cs` |
| 15 | The stop route is not carved out of Easy Auth | `Exclusions_DoNotCoverAnyAuthenticatedPath` (extended) | `EasyAuthExclusionTests.cs` |
| 16 | The button is served, hidden by default, and labelled | `StopButton_IsServedHiddenAndLabelled` | `ComposerAssetsTests.cs` |
| 17 | The frontend calls the stop endpoint | `SendJs_StopsGenerationServerSide` | `ComposerAssetsTests.cs` |
| 18 | A no-text stopped turn drops its bubble, keeping `.msg` 1:1 with history | `StoppedWithNoText_DropsTheAgentBubble` | `StreamRecoveryAssetsTests.cs` |
| 19 | Barge-in stops the server **and** still aborts locally | extended barge-in assertions | `VoiceConversationAssetsTests.cs` |
| 20 | `CACHE` was bumped | updated assertions | `PwaAssetsTests.cs`, `SearchAssetsTests.cs` |

`StopGenerationTests` derives from `SharedClusterTests<SharedClusterFixture>` (ADR 0055 — the cluster
is reachable only that way, and `SharedClusterSeamTests` pins the convention); each test uses its own
grain key.

## Quality gates (copied from `.agentic/quality-gates.yaml`)

| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer.CSharp S3776, **build error** |
| `vulnerable_packages` | 0 | CI package-freshness job |
| lint | clean | `dotnet format Agent42.slnx --verify-no-changes` |

Branch policy: `branches.protected: [main]` → `feature/stop-generation` + PR.

## Out of scope

- Making a background turn stoppable (`/api/agent-run`, reminders, `ask_agent`). They get a token but
  never publish the handle, so `StopAsync` cannot reach them — AC 11 pins that.
- Stopping image generation (ADR 0020) or the no-model notice — recorded as residuals.
- A persisted "stopped" marker, and any `ChatTurn` change.
- Counting a stopped turn's spent tokens (recorded consequence).
- Resuming or regenerating a stopped turn.
- Re-labelling `.composer__hint`, which still names Enter only (ADR 0043's known gap).

## Where the implementation diverged from the plan

Executed 2026-09-20 on `feature/stop-generation` from `main` @ aeaf4dc. Six deviations, all discovered by
running things rather than by re-reading the plan:

1. **S3776 rejected the first draft at 16/15**, exactly the risk the plan flagged without a number. Fixed
   by moving branching to where it belonged rather than by shuffling it: the stopped-turn guard became
   `TryPrepareStoppedTurn`, and the suggestion skip moved *inside* `SuggestFollowUpsAsync` beside its three
   existing guards — which that helper's own comment already says is the right home for gating. Build clean
   after that.
2. **`StreamModelReplyAsync` needed `[EnumeratorCancellation]`** (CS8425) and `System.Runtime.CompilerServices`
   in `AgentGrain.cs`. Not in the plan.
3. **The stream gate grew a second boundary.** The plan's gate parked after the first chunk; the fake
   prefixes every reply with an echo (tool list, system prompt, counters), so "no text yet" is only
   reachable by parking before *any* update. `OpenStreamGate(afterChunks: 0)` parks at the top of the
   streaming method instead.
4. **Gate teardown turned out to be mandatory, not tidy.** A gate left installed parks the *next* turn at
   the same boundary, so `Stop_PartialReplyBecomesHistoryForTheNextTurn` hung for 6.5 minutes until the run
   was killed — a hang, not a failure. `FakeChatClient.CloseStreamGate()` plus a `CloseStreamGates()` call
   after every stop; `Reset()` routes through it too.
5. **Assertions are `Contains`/`DoesNotContain` on a head and tail marker**, not equality against a whole
   canned reply, for the same echo reason as (3). The plan's `WholeReply` constant does not exist.
6. **The ephemeral AC is proven by deactivate-and-reload**, not by reading a storage provider: the existing
   `SessionStateOf` helper reads the primary silo only, which would pass vacuously if the grain ran
   elsewhere. `DeactivateOnIdle()` then an empty `GetHistoryAsync()` is silo-independent.

Two claims were strengthened beyond the plan, and one weakened:

- **Mutation-checked** (the repo's standard for a load-bearing branch): replacing the append with `true`
  turns 7 of the 12 new tests red; dropping the `_interactiveTurn` gate turns exactly
  `Stop_DoesNotCancelABackgroundTurn` red, so that one test is the whole guard for the critic's H1 fix.
- **The server leg was exercised against the running app**, not only under test: DevHost, a real chat
  stream, `POST .../stop` → 204 mid-stream, `{"type":"done","stopped":true}`, and a persisted reply cut
  mid-sentence. That also measured the "tokens spent but uncounted" consequence at the wire — the stopped
  `Done` event carries no usage fields at all.
- **The DOM leg is unverified.** No browser driver was available in the session, so the button's
  appear/disappear behaviour, the note, and the empty-bubble removal are pinned by source-text guards only.
  Recorded as a residual in ADR 0078 rather than quietly claimed — the same treatment ADR 0043 and 0037
  gave their interactive halves.

## Review

Critic pass on the first draft: **not ready to execute** — three serious findings, all accepted and
folded in above.

| Finding | Disposition |
|---|---|
| H1 — `_turnCts` as a plain activation-scoped field is unsound; a background turn can steal it, dispose it under a live run, or (via a `ReceiveReminder` reset) disarm the button. The `_pushesThisTurn` analogy was the wrong frame | **Accepted.** Replaced with publish-by-identity + `_interactiveTurn` gate + no reminder reset site; added AC 11. This also makes "background turns out of scope" true rather than aspirational |
| H2 — storing the user message alone breaks the `.msg` ↔ history 1:1 invariant, so ADR 0046 recovery would report "Transmission failed" over turns that persisted fine | **Accepted**, with the interview decision kept: the storage shape stands; the *client* drops the empty bubble and uses a non-`.msg` notice. Added AC 18. The critic's alternative (store an empty assistant message) was rejected — it was the interview's rejected option, and it sends an empty assistant turn to the provider forever |
| H3 — a stop-only barge-in reverses ADR 0037's immediacy, because `onDelta` is not gated by `interrupted` | **Accepted.** Barge-in fires both, unconditionally; two stale comments fixed; AC 19 |
| M4 — `bool Stopped` writes `"stopped":false` on every per-token event under `WhenWritingNull` | **Accepted** — nullable `bool?` |
| M5 — `Cancel()` can throw (`ObjectDisposedException`, inlined callback exceptions) | **Accepted** — `CancelAsync()` in a try/catch; unpublish-before-dispose order stated |
| M6 — AC 6 vacuous (suggestions already skip on an empty reply); AC 11/12 weak | **Accepted** — AC 6 now stops *after* a delta and asserts the fake's counter; the ephemeral AC also asserts the live session |
| M7 — ACs 1/2/4 race a 200 ms timer | **Accepted** — deterministic stream gate in Step 2 |
| M8 — `VoiceConversationAssetsTests` missing; bidirectional amendment notices missing; class-summary paragraph goes stale | **Accepted** — all three in Steps 5/6 and the affected-areas table |
| M9 — the `TryGetInMemoryChatHistory` false branch would persist an empty session | **Accepted** — log and skip the persist |
| L10 — stop is a no-op during image generation / the no-model notice | **Accepted as a recorded residual**, not a code change: gating the button would need the client to know which branch the server took |
| L11 — cold-handoff gaps (fixture unnamed, unmeasured usage claim, line drift `:215`→`:216`, overstated `[AlwaysInterleave]` rationale, unverifiable S3776 number) | **Accepted.** Fixture named; the usage claim is now labelled *asserted, not measured*; the rationale narrowed to the long gaps; the complexity claim reworded to "keeps the added branching out of `ChatCoreAsync`" rather than quoting a number I did not measure |

### Second critic pass — on the implemented diff, before the MR

| Finding | Disposition |
|---|---|
| H1 — barge-in's local abort was placed **after** the awaited stop request, so the reply keeps being spoken for a full round trip. Stop-only barge-in wearing the right source text, and a direct regression of the ADR 0037 property this plan's H3 disposition was accepted to protect | **Accepted — real bug, fixed.** The abort now runs before the fetch. The guard that missed it was a `Contains` assertion, which cannot see ordering, so `AppJs_BargeInAlsoStopsTheServer` now asserts `indexOf(abort) < indexOf(fetch)` — **mutation-checked**: restoring the old order turns it red with the explanatory message |
| M2 — the pre-0078 "`reply` is NOT the persistence path" gotcha and the `reply` declaration comment were left stating an invariant this diff breaks | **Accepted.** An exception blockquote on that entry and a cross-reference at the declaration; hard rule 2 |
| M3 — `markStopped` was inserted between `renderSuggestions`' doc comment and its declaration, silently reattributing it | **Accepted.** Moved above the comment block, which is back on its own function |
| M4 — nothing pinned the `stopped` **wire** field, so renaming the record property would keep every C# test green and break the button | **Accepted.** New `ChatEndpoint_StreamsStoppedFlagOnlyWhenStopped` streams a real turn over HTTP, stops it mid-flight, and asserts `"stopped":true` on `done`, its **absence** on every other event, and the partial reply in the history |
| M5 — the publish gate re-read `_interactiveTurn`, which a background turn starting in the skipped-images yield gap could have flipped, silently killing the button for a live turn | **Accepted.** The flag is captured before the first yield and never re-read; the three doc claims that overstated the guarantee now say so |
| L6 — "background turns still get a token" was wrong for a fired **reminder**, which never drains `ChatCoreAsync` at all | **Accepted.** Corrected in the ADR, the interface remarks and the `_turnCts` remarks, which now separate "drains the core, does not publish" from "never reaches the core" |
| L7 — ADR 0078 said "fourth button", its own 0043 amendment said "fifth" | **Accepted** — fifth, counting attach/tools/mic/voice |
| L8 — `StopChat_Returns204` stays green if the handler never calls the grain | **Accepted as scoped.** Its comment now says it pins routing and idempotence only, and names the two tests that prove the stop. The invented leak rationale is gone |
| L9 — the `TryPrepareStoppedTurn == false` early `yield break` also skips ADR 0066's rebuild, which the comment did not mention | **Accepted** — comment now says what else it skips, and why that is acceptable on a branch that is unreachable with an in-memory history provider |
| L10 — a self-contradictory comment in `wireSend`, and the fallback path leaves no "Stopped." mark | **Accepted** — comment rewritten; the fallback's honest outcome (the reply freezes, unmarked, because the server did not stop) is now stated there |
