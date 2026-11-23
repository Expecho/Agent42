---
id: 0068
date: 2026-09-05
status: accepted
tags: [conversations, retention, orleans, cosmos, search, no-migration]
supersedes: []
extends: [0004]
amends: [0016, 0061]
---

# A continuous agent's history is pruned to a 24-hour window

> **Amended by [ADR 0140](0140-per-agent-message-retention.md) (2026-11-23): the window is now a per-agent setting.** A stored agent can carry its own retention (never / 1 h / 24 h / 7 d / 30 d) for per-chat and continuous chats alike, enforced by an idle sweep. Unset keeps this ADR's 24 h behaviour for a continuous chat.

> **Amends [ADR 0016](0016-per-agent-history-mode-deterministic-conversation-id.md)** — it recorded the
> 2 MB brick as "a designed-in failure mode" and closed with **"No code mitigation is in scope beyond
> documenting this."** That scope call is reversed here. ADR 0016 otherwise stands entirely: the flag
> stays client-side, routing stays client-owned, delete-to-reset is still the escape hatch.
> **Amends [ADR 0061](0061-searchable-chat-message-projection.md)** — its best-effort licence for index
> writes gains one documented exception. Neither is superseded.

## Context

A continuous agent (ADR 0016) routes every start onto one deterministic conversation id
`` `agent-${agent.id}` ``, so one grain — `{userId}/agent-{agentId}` — accumulates history forever
while re-serializing the whole session into a single Cosmos item on every turn, hard-capped at 2 MB
(ADR 0004). ADR 0016 named the consequence honestly and declined to act on it.

The declining was reasonable when written: the feature was new and the failure was distant. It is less
reasonable now that continuous agents are the normal shape for a standing assistant, and the failure
mode is the worst one available — a `WriteStateAsync` that fails **after** the model has already
answered, silently, in production only (the in-memory test store has no 2 MB limit).

## Decision

Messages older than **24 hours** are **deleted** from the session of a **continuous** agent chat, on
**every persisted turn**.

Four parameters, each a fixed constant in `RetentionLimits` and each the user's decision, not to be
relitigated: a **fixed 24 h window** (not per-agent, not per-user, not operator config); **every
persisted turn** as the cadence; **continuous chats only** as the scope; and **hard delete** for what
falls out — no summarization, no keep-on-disk.

It is a durable prune of stored history, not a trim of what is sent to the model. Only deletion
reclaims the 2 MB budget; a smaller request does not.

## What this does NOT do, stated first

**It does not close the brick.** A window bounds *age*, not *size*. A busy continuous agent, or one
oversized tool return (an agent tool's return value is persisted verbatim), can still exceed 2 MB
inside 24 hours. ADR 0016's consequence survives, narrowed. Anyone reading this ADR as "solved" is
reading it wrong, and the gotcha entry says so at the point of use.

## The grain has to stamp the timestamps itself

Four facts were established by spike on Agent Framework 1.19 / M.E.AI 10.9, not inferred, because the
whole design rests on them.

1. **Nothing stamps `ChatMessage.CreatedAt` on either run path.** After `RunAsync` every message reads
   back `<null>`; after `RunStreamingAsync` — the production chat path — likewise. The repo never set
   it either.
2. **`createdAt` IS part of the session's serialized form and survives the round trip.** Stamping
   messages in place on the list from `TryGetInMemoryChatHistory` and then serializing preserves the
   values through `SerializeSessionAsync` → JSON → `DeserializeSessionAsync`.
3. **The restored list is mutable and a prune survives re-serialization.**
4. **The two run paths differ on a provider-supplied `CreatedAt`.** With a stub client stamping every
   `ChatResponseUpdate` with `DateTimeOffset.UnixEpoch`, the coalesced assistant message on the
   **streaming** path still reads `<null>` — the coalescer drops it. On **`RunAsync`** (the reminder
   path) a provider-set value **is** preserved into the session.

Fact 2 is why **no new `[Id(n)]` state field is needed**: the timestamps live on the messages, inside
`SerializedSession`. That is the entire migration story — zero state change, zero golden document.

Fact 4 is why the stamping rule is a **clamp** and not a bare `??=`. `CreatedAt` is replaced with "now"
when it is **null**, **in the future** (clock skew), or **older than `MaxPlausibleAge` (365 d)**. A
provider stamping epoch or default-zero would otherwise mark a live turn instantly expired and delete
it on the next persist — silently, in production only, which is precisely ADR 0016's failure class.
A provider setting a *plausible but wrong* past value still wins; that is a residual below, not a
closed hole.

Existing conversations carry no timestamps, so the first persisted turn after deploy stamps their whole
history with "now": **nothing is pruned until a window after the deploy, and never retroactively.** The
original times were never recorded and cannot be recovered.

**Only continuous conversations are stamped at all.** The continuity gate sits in `ApplyRetentionAsync`,
above the stamp, not merely inside the prune. A per-chat conversation is never pruned, so a timestamp on
its messages is never read — and it is not free: measured at ~35 bytes a message, ~1.2% of the very
2 MB item this feature exists to defend. Charging that to every conversation in the app to serve the
handful that are continuous is the wrong trade, and it is a real number rather than a rounding one — it
was enough to push the session-budget test's 256 KB canary over on its own before the gate went in. Safe
because a conversation cannot change its id, and the id is what makes it continuous.

## A clock seam, because without one most of this is untestable

There was **no clock abstraction anywhere in the repo**. Tests drive a real `TestCluster` through
`IAgentGrain` and cannot reach `_session`, so without a seam no test could produce a turn "older than
the window" — leaving a new file whose main branch is unreachable, against a
`coverage_new_code_pct_min: 80` gate.

`TimeProvider` is therefore injected into `AgentGrain`. It is an **optional constructor parameter**
defaulting to `TimeProvider.System`, and that was **verified, not assumed**: Orleans activates grains
through `ActivatorUtilities`, which honours default parameter values — measured by running the grain
suite with no `TimeProvider` registered in any silo. So this is deliberately **not** another instance of
the "every grain host must register X" hazard (`IPushNotificationSender`, `IUserMcpToolProvider`): the
Backend, AppHost and DevHost hosts are **unchanged**, and only the test silo registers a controllable
one. A dependency with a correct default should not cost three host edits and a way to break one silo.

`AgentGrain.GetDate()` — the model-facing `get_date` tool — deliberately still reads `DateTime.Now`. It
answers a question about the user's wall clock, not about retention.

## The prune is a prefix of WHOLE user turns

A *turn* is a user message plus every following message up to the next user message. Only a **leading
run** of expired turns is dropped; the walk stops at the first turn that is not expired. Three rules
that do not follow from that sentence are written into the code:

- **A leading non-user prefix is never dropped.** Removal starts at the first user message. What else
  may reach the stored list is not pinned anywhere (the storage filter's own comment notes the
  framework default excludes only `ChatHistory`-sourced messages), and such a message is invisible to
  every existing test — `GetHistoryAsync` projects `User` and `Assistant` only — while still counting
  in the walk. Guarded by a mutation-checked test that injects one into the serialized session, since
  no shipped write path produces that state.
- **A turn is expired only when EVERY message in it is**, so a turn whose reply landed inside the
  window survives with its prompt.
- **`TurnAttachments` is shortened by the same count.** The list joins user turns *positionally*, so
  dropping user turns without it moves every surviving image onto the wrong turn.

Three properties follow. A `FunctionResultContent` is never orphaned from its `FunctionCallContent`
(most providers reject that outright) — results always follow their call inside the same user-led turn,
and both are non-`User` messages, so whole-turn dropping cannot split the pair. `TurnAttachments` stays
aligned with `ProjectUserTurn`'s unconditionally-advancing index. And ordinals shift by a constant,
which is what makes the search reconciliation tractable at all.

`StripToolHistory` is the existing precedent for deleting from the session in place, but it is safe
today only because it never removes a **user** message. This does, so it maintains the join itself.

## The search index has to be re-anchored, and that is the sharp part

`IChatSearchIndex` (ADR 0061) has **no delete-by-ordinal-range**. The only removal is
`DeleteConversationAsync`, and document ids are `{conversationId}:{ordinal}` where the ordinal is the
position in `GetHistoryAsync()`'s projection. A prune renumbers every surviving turn, so without a
reconcile the next write upserts over the low ordinals and **leaves the tail documents orphaned** — a
three-turn conversation returning five hits, two of them text that exists nowhere.

So a prune that dropped anything runs `DeleteConversationAsync` → reset `_indexedTurnCount = 0` → let
the existing tail `IndexNewTurnsForSearchAsync()` re-project from ordinal 0. Four constraints:

1. **The cursor is reset only after the awaited delete succeeds**, inside the `try` — the ordering
   `DeleteSearchIndexForConversationAsync` already uses.
2. **A failed delete is retried, and this is the one exception to ADR 0061's best-effort licence.**
   That licence rests explicitly on `ReindexAsync` being a user-invocable repair — and `ReindexAsync`
   only **upserts**. `DeleteConversationAsync` is the sole removal, so a swallowed delete after a
   *destructive* prune is the one index failure nothing can ever clean up. An activation-scoped
   `_searchReconcilePending` **bool** carries it to the next persisted turn.
   **It is a bool and deliberately not an accumulated delta — this was got wrong first and caught in
   review.** `DeleteConversationAsync` removes the whole conversation whatever the delta is, so a carried
   count had only one consumer, the ephemeral remap, and carrying it across turns put it in the wrong
   reference frame. An ephemeral turn taken *between* a failed reconcile and its retry records its
   ordinals in the already-pruned frame; re-applying the earlier shift to them walks them backwards onto
   real turns, so the incognito text gets indexed into Cosmos and two genuine turns are skipped. Measured
   on the pre-fix build: `the incognito token` written at ordinal 2.
3. **`_ephemeralOrdinals` is REMAPPED, never cleared, and remapped WHERE THE SHIFT HAPPENS.**
   `DeleteSearchIndexForConversationAsync` clears, and is right to — the conversation is going away.
   Here the re-projection that follows would sweep a still-resident incognito turn (ADR 0034) straight
   into the index at its new ordinal. The remap lives in `PruneExpiredTurnsAsync`, the only point at
   which the shift and the recorded ordinals are certainly in the same frame (see 2).
4. **The delta is a projected-turn count, not a user-turn count**, measured as `countBefore -
   countAfter` across one `GetHistoryAsync()` projection either side of the removal — the same
   single-projection rule ADR 0061 binds. `GetHistoryAsync` emits both roles and skips blank turns, so
   *N* dropped user turns is not *N* dropped ordinals.

The reconcile runs **after** `WriteStateAsync`. Once a chat is older than the window most turns prune
something, so it fires on most turns; it is a filtered-query delete plus N deletes and N upserts on a
non-reentrant grain, and it must not sit in front of the durable write ADR 0046's stream recovery reads.

**Measured cost of a failed reconcile, since it is now a documented state:** the index goes stale in
*both* directions — the pruned text is still there, and the surviving turn is not written either,
because the cursor deliberately was not reset and now sits past the shortened history. The next
persisted turn repairs both.

## Detecting a continuous chat without touching ADR 0016

The grain does not know it is continuous: `Continuous` is `AgentDefinition [Id(7)]` only, absent from
`AgentBinding` and `AgentGrainState`, because ADR 0016 keeps it client-side as routing rather than
persona. The only server-side signal is the conversation-id shape, so detection is
`ConversationId().StartsWith("agent-", StringComparison.Ordinal)` — safe because `crypto.randomUUID()`
cannot emit that prefix, a property `agents.js` already relies on and comments. **This is why no ADR
0016 reversal is needed beyond the scope call:** the flag stays exactly where 0016 put it.

## Three persist sites, and they guard `_ephemeral` three different ways

`grep -rn "SerializedSession" src/` yields exactly three assignments of a serialized session (plus the
`null` in `PurgeAsync`); the `Documents/` occurrences are rebuild-only snapshots, checked rather than
assumed. Each takes the retention call immediately before its serialize, and each guards differently —
`PersistTurnUnlessEphemeralAsync` by **early return**, `GenerateImageTurnAsync` by a real
`if (!_ephemeral)` block (so the call goes **inside** it), and `ReceiveReminder` by **no guard at all**
(a fired reminder always persists, and it clears `_ephemeral` on entry). `ReceiveReminder` does its own
serialize rather than delegating, which is the same "two consume sites" shape ADR 0066 hit and the same
way to ship it half-wired.

## Alternatives considered

- **`Microsoft.Agents.AI.Compaction`.** Investigated and rejected; do not reopen casually. Compaction
  reduces what is **sent**, not what is **stored** — every persist site re-serializes the whole session
  regardless — so it does not touch the 2 MB cap, which is the entire point. There is also no
  time-based `CompactionTrigger` (tokens / messages / turns / groups / `HasToolCalls` / `Always` /
  `Never`), and the whole namespace is `[Experimental("MAAI001")]`, which would make it this repo's
  first experimental-API dependency and need a build suppression. Its one real benefit —
  `CompactionMessageGroup` keeping tool-call pairs together — falls out of turn-boundary pruning free.
- **Summarizing expiring history before deleting it.** Out of scope by decision. It is a per-turn model
  call on the away paths and a second source of truth about what was said.
- **A size-based cap instead of an age window.** Would address the brick more directly, and is the
  honest follow-up. Rejected here because "delete until under N bytes" has no natural turn boundary the
  user can predict, while "the last day" is explainable without a UI — and there is no UI.
- **Per-agent or operator-configurable retention.** Rejected by the user. Retention that varies per
  conversation is a durability contract something would have to explain.
- **Threading `Continuous` into `AgentBinding`/`AgentGrainState`.** Rejected: ADR 0016 stands, and the
  id shape already carries the signal.

## Consequences

- **The 2 MB brick is narrowed, not closed** (see above). ADR 0016's consequence and its
  delete-to-reset escape hatch both survive.
- **The agent forgets.** Hard delete was the user's call. ADR 0056 memory, when enabled, is the
  continuity story — it is per-user and entirely unaffected by this prune.
- **A public share link to a continuous agent chat silently loses content over time** — the share view
  reads the same `GetHistoryAsync()`.
- **ADR 0032's client IndexedDB mirror keeps pruned text** unencrypted on the device until that
  conversation is next refreshed.
- **Attachment blob bytes are orphaned.** `IAttachmentStore` has no delete, and conversation delete
  already leaves bytes behind. `GeneratedImages` **dictionary entries** *are* removed, because those
  are grain state and would otherwise grow unbounded in the one conversation that never rotates.
- **A `createdAt` on every message is not free, which is why only continuous chats get one.** Measured
  on the session-budget test's conversation with stamping ungated: **261,106 → 264,270 bytes**, 3,164
  bytes over ~90 messages — about 35 bytes each, or **1.2%**, charged against the same 2 MB item the
  feature exists to protect. That was enough to push a *regular* chat's 256 KB canary (already at 99.6%
  of its ceiling on `main`) over the line. With the gate in `ApplyRetentionAsync` the canary is
  unchanged at 256 KB and only continuous conversations pay.
- **A failed index delete that is never retried leaves orphaned documents PERMANENTLY.**
  `_searchReconcilePending` is activation-scoped, so a grain that deactivates with a retry outstanding —
  routine on an idle chat — loses it, and nothing else can remove those documents: reindex only upserts
  and the only other `DeleteConversationAsync` caller is `PurgeAsync`. The text then survives in the
  `chat-messages` container, searchable, against a decision whose whole point is hard deletion. This is
  narrower than it sounds (it needs a Cosmos failure on exactly that call) but it is a durability
  residual, not merely a stale-search one, and it is **not** self-healing. A delete-then-reindex
  `ReindexAsync` would close it and is the recorded follow-up; it was left out of scope because it makes
  the existing repair destructive, which is its own decision.
- **"Hard delete" has two exceptions where the text SURVIVES, both outside the Entra tenant.** A pruned
  turn's transcript was already POSTed to Hindsight if memory is enabled (ADR 0056), which has **no
  delete path**, and its full text sits in ADR 0054's `/logs` ring until that ring rolls. Pruning the
  session does not reach either. The share view and the IndexedDB mirror below are the opposite case —
  content *lost* — and should not be confused with these.
- **The reconcile is in the user's latency path.** `PersistTurnUnlessEphemeralAsync` is awaited before
  the `Done` event is yielded, so once a continuous chat is older than the window, most turns pay a
  filtered-query delete plus a full re-index of the conversation against Cosmos before the reply
  completes — on a code path CI never exercises (see Verification). Bounded by the same 2 MB the
  conversation is capped at, and the ordering is deliberate (it must not precede the durable write), but
  it is a recurring cost an operator should know about rather than discover.
- **A plausible-but-wrong provider `CreatedAt` on the reminder path survives the clamp** and would
  expire a live turn early. No provider has been observed doing this.
- Frontend, endpoints, Bicep, secrets, `excludedPaths`, Cosmos containers and packages are all
  **unchanged**. No UI indicates that pruning happened, by decision.

## Verification

- **Full suite: 1225 passed, 0 failed.** `dotnet format --verify-no-changes` clean; `dotnet build`
  clean, which is also the `cognitive_complexity_max` gate (S3776 is an `error`).
- **A `critic` pass on the finished diff found one HIGH defect, which is fixed and now has its own
  regression test** (`Prune_DoesNotIndexAnEphemeralTurn_TakenBetweenAFailedReconcileAndItsRetry`): the
  accumulated-delta remap described in constraint 2. Confirmed against the pre-fix build rather than
  argued — it indexed `the incognito token` at ordinal 2 and dropped two real turns. Neither AC15 (one
  prune, successful delete) nor AC16 (no ephemeral turn) could reach that state.
- **Four mutation checks were run, and two of them corrected this ADR.**
  - `Prune_DoesNotIndexAnEphemeralTurn_AfterOrdinalsShift` (AC15): replacing the remap with
    `_ephemeralOrdinals.Clear()` turns it — and **only** it — red. Confirmed.
  - `Prune_KeepsALeadingNonUserPrefix`: replacing `start = userIndices[0]` with `start = 0` turns it
    red. The rule had **no** test when the diff was reviewed, despite the plan claiming one; the state
    is unreachable through the shipped write paths, so the test injects a system message into the
    serialized session directly.
  - The `TurnAttachments.RemoveRange` clamp was **planned as a fix for a live bug and is not one.**
    Removing the `Math.Min` leaves the entire suite green. Measured why: all three persist paths call
    `AlignTurnAttachmentsToHistory` + `Add` **before** their run, and `Align` pads using the same
    user-message count the prune walks, so by the time the prune runs the list is exactly as long as
    the user turns and the dropped count is always at least one short of it. The clamp is kept as a
    **defensive** bound and is documented as defensive in both the code and the test — the invariant it
    protects is otherwise an argument about three call sites in three files.
- **`RetentionLimits.MinimumPreservedTurns` likewise does not bind through any shipped path**, for the
  same class of reason: a prune only runs from a persist site, and the turn being persisted was stamped
  moments earlier, so the newest turn is always inside the window. Both the constant and its test say
  so rather than implying coverage that is not there.
- **The `TimeProvider` optional-parameter route was measured**, not assumed (see the clock seam above).
- **Unverified residual:** as with every Cosmos path in this repo since ADR 0007, the reconcile's
  `DeleteConversationAsync` runs against `InMemoryChatSearchIndex` in CI and against Cosmos only in
  production. The prune itself is storage-agnostic; the index delete is not.
