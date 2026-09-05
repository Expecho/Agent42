# Plan — prune a continuous agent's history to a 24-hour window

planned-against: main @ 941127f

## Overview

A continuous agent (ADR 0016) funnels every chat into one grain forever, and that grain
re-serializes its whole session into a single Cosmos item every turn, hard-capped at 2 MB
(ADR 0004). ADR 0016 calls this "a designed-in failure mode" and says the escape hatch is
delete-to-reset, with **"No code mitigation is in scope beyond documenting this."**

This plan **mitigates** that: messages older than 24 hours are deleted from the session of a
continuous agent chat, on every persisted turn. It is a durable prune of stored history — not
a trim of what is sent to the model — because only deletion reclaims the 2 MB budget.

**It does not close the brick.** A window bounds *age*, not *size*: a busy continuous agent, or
one oversized tool return (`.agentic/gotchas.md:134`, "an agent tool's RETURN value is
persisted"), can still exceed 2 MB inside 24 hours. ADR 0016's consequence survives, narrowed.
The ADR must say so, or the next reader will treat it as solved.

## Affected areas

Verified against the tree at 941127f.

**New files**
- `src/Agent42.Agents/Conversations/RetentionLimits.cs`
- `src/Agent42.Agents/Conversations/AgentGrain.Retention.cs` — a new `AgentGrain` partial

**Modified — source**
- `src/Agent42.Agents/Conversations/AgentGrain.cs` — the `TimeProvider` dependency, the chat
  persist site (`PersistTurnUnlessEphemeralAsync`, ~722) and the reminder persist site
  (`ReceiveReminder`, ~965)
- `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs` — third persist site (~122)
- `src/Agent42.Agents/Search/AgentGrain.Search.cs` — ordinal reconciliation after a prune

**Modified — host registration (the recurring "every grain host must register X" hazard)**
- `src/Agent42.Backend/Program.cs`
- `src/Agent42.AppHost` silo host
- `tools/Agent42.DevHost/Program.cs`

Every host that activates `AgentGrain` must register `TimeProvider`. This is the same shape as
`IPushNotificationSender` / `IUserMcpToolProvider`: miss one and activation fails there only.
Check whether the executor can instead give the ctor parameter a default of `TimeProvider.System`
— Orleans activates grains through `ActivatorUtilities`, which honours default parameter values,
so an unregistered host would still work. **Verify that before relying on it**; if it holds,
prefer it and register only the fake in tests.

**Modified — tests**
- `tests/Agent42.Tests/AgentGrainTests.cs` — behaviour (`SharedClusterFixture`, real TestCluster)
- `tests/Agent42.Tests/ChatSearchIndexingTests.cs` — the ADR 0061 search tests (AC13–AC16)
- `tests/Agent42.Tests/ProjectLayoutTests.cs` — add the new part to `AgentGrainParts()` and
  update the `<summary>` "The thirteen parts" at line 130
- A `TimeProvider` test double (override `GetUtcNow()`). **Do not add
  `Microsoft.Extensions.TimeProvider.Testing`** — a five-line double avoids touching the
  dependency gates.

**Modified — docs (same MR, hard rule 2)**
- `.agentic/decisions/` — new ADR (via `/decide`) amending ADR 0016
- `.agentic/gotchas.md` — the entry at line 167 ("A continuous agent … will eventually brick")
- `CLAUDE.md` — the ADR 0016 bullet, and the ADR 0039 bullet's partial-count sentence

## Context & constraints

### Decisions the user made (do not relitigate)

| Question | Answer |
|---|---|
| Window | **Fixed 24 h constant**, not per-agent, not operator config |
| Cadence | **Every persisted turn** |
| Scope | **Continuous agent chats only** |
| Dropped content | **Hard delete** — no summarization, no keep-on-disk |

### Measured at plan time (Agent Framework 1.19.0 + M.E.AI 10.9.0)

Four facts established by spike, not inferred. The design rests on them.

1. **Nothing stamps `ChatMessage.CreatedAt` on either run path.** After `RunAsync`, all
   messages read back `<null>`; after `RunStreamingAsync` — the production chat path
   (`AgentGrain.cs:572`) — likewise. The repo never sets it either. **So the grain must stamp it.**
2. **`createdAt` is part of the session's serialized form and survives the round trip.** The
   session JSON is
   `{"stateBag":{"InMemoryChatHistoryProvider":{"messages":[{"createdAt":"…","role":"user",…}]}}}`.
   Stamping messages in place on the list from `TryGetInMemoryChatHistory` and then serializing
   preserves the values through `SerializeSessionAsync` → JSON → `DeserializeSessionAsync`.
3. **The restored list is mutable and a prune survives re-serialization.** `RemoveAll` on the
   list from a *deserialized* session, re-serialized, yields a session holding only the kept
   messages.
4. **The two run paths differ on a provider-supplied `CreatedAt`, and this matters because the
   value feeds a DELETION decision.** With a stub client stamping every `ChatResponseUpdate`
   with `DateTimeOffset.UnixEpoch`, the coalesced assistant message on the **streaming** path
   still reads `<null>` — the streaming coalescer drops it. On **`RunAsync`** (the reminder
   path, `AgentGrain.cs:962`) a provider-set `CreatedAt` **is** preserved into the session.

Fact 4 is why the stamping rule below is a clamp and not a bare `??=`: a bad provider value
reaching the reminder path would mark a live turn instantly expired and delete it on the next
persist — silently, in prod only, which is precisely ADR 0016's failure class.

**Consequence: no new `[Id(n)]` state field is needed.** Timestamps live on the messages
themselves, inside `SerializedSession`. Existing conversations carry no timestamps, so the first
turn after deploy stamps everything already in the session with "now" — existing history is
pruned no earlier than 24 h after the deploy, never retroactively. That is the whole migration
story: zero state change, zero golden-document test.

### The stamping rule

At each persist site, before serializing, walk the session list and set

> `CreatedAt = now` when it is **null**, **in the future**, or **older than
> `RetentionLimits.MaxPlausibleAge`** (365 days). Otherwise leave it alone.

Null covers the normal case and the legacy backfill. Future covers clock skew. Implausibly-old
covers epoch/zero, the destructive case fact 4 exposes. A provider setting a *plausible but
wrong* past value (say three days ago) still wins — no provider observed doing this; record it
as a residual rather than pretending it is closed.

### The clock seam — this must exist or most of the plan is untestable

There is **no clock abstraction anywhere in the repo**: `grep -rn "TimeProvider\|FakeTime" src/ tests/`
returns nothing, and `AgentGrain.GetDate()` is `DateTime.Now` (`AgentGrain.cs:907`). Tests drive a
real TestCluster through `IAgentGrain` and cannot reach `_session`, and there is no way to seed a
session with old messages (`GoldenStates.cs:19` is a synthetic blob that never reaches a grain).

So without a seam, no test can produce a turn "older than the window", and most acceptance
criteria below are unwritable — while `coverage_new_code_pct_min: 80` lands on a new file whose
main branch is unreachable.

**Inject `TimeProvider` into `AgentGrain`** and take both `now` and the expiry comparison from
it. Registration is covered under Affected areas. `GetDate()` is deliberately left alone — it is
a model-facing tool, out of scope here.

### Why NOT `Microsoft.Agents.AI.Compaction`

Investigated and rejected; do not reopen casually.

- Compaction reduces what is **sent**, not what is **stored** — every persist site re-serializes
  the whole `_session` regardless — so a `CompactionProvider` does not touch the 2 MB cap, which
  is the point of this feature.
- There is **no time-based `CompactionTrigger`** (tokens / messages / turns / groups /
  `HasToolCalls` / `Always` / `Never`).
- The whole namespace is `[Experimental("MAAI001")]` and would be this repo's first
  experimental-API dependency, needing a build suppression.

Its one real benefit — `CompactionMessageGroup` keeping tool-call pairs together — falls out of
turn-boundary pruning for free.

### The prune is a prefix of WHOLE user turns

Define a *turn* as a user message plus every following message up to (not including) the next
user message. Drop only a **leading run** of expired turns; stop at the first turn that is not
expired; never drop the last `MinimumPreservedTurns`.

Two rules that are NOT implied by that sentence and must be written into the code:

- **A leading non-user prefix is never dropped.** If `messages[0]` is not a user message, keep
  everything before the first user message. The repo's storage filter excludes `AIContextProvider`
  and `ChatHistory` sources (`Documents/AgentGrain.Documents.cs:35-50`) and its own comment notes
  the framework's default excludes only the latter — so what else may reach the list is not
  pinned anywhere. Such a message is **invisible to every existing test**, because
  `GetHistoryAsync` projects only `User` and `Assistant` roles (`AgentGrain.cs:821-823`), while
  still counting in the walk.
- **`TurnAttachments.RemoveRange` must be clamped.** The list is only equal to the user-turn
  count because all three paths Align-then-Add before the run (`AgentGrain.cs:798-799`,
  `:961-963`, `ImageTurn.cs:108-109`), and `AlignTurnAttachmentsToHistory` only **pads**, never
  trims (`Attachments.cs:25-28`). A legacy conversation deserializes it **empty** while its
  session still holds image parts — documented at `Attachments.cs:133-139`. So use
  `RemoveRange(0, Math.Min(droppedUserTurns, Count))`; an unclamped call throws
  `ArgumentException` **inside the turn, after the model has answered** — ADR 0016's brick shape.

With those, three properties hold:

- A `FunctionResultContent` is never orphaned from its `FunctionCallContent` (most providers
  400 on that). Results always follow their call inside the same user-led turn, so whole-turn
  dropping cannot split a pair.
- `TurnAttachments` stays aligned with `ProjectUserTurn`'s unconditionally-advancing
  `userTurnIndex` (`AgentGrain.cs:839-852`).
- Ordinals shift by a constant, which is what makes the search reconciliation tractable.

Precedent for in-place message deletion exists: `StripToolHistory` (`AgentGrain.cs:1025-1044`)
removes content and then `messages.RemoveAll(m => m.Contents.Count == 0)`. It is safe today only
because it never removes a *user* message. This plan does, so it must maintain `TurnAttachments`
itself.

### Search index reconciliation (ADR 0061) — the part most likely to be got wrong

`IChatSearchIndex` has **no delete-by-ordinal-range**; the only removal is
`DeleteConversationAsync` (`IChatSearchIndex.cs:59`), a filtered query over the conversation
within the user partition. Document ids are `{conversationId}:{ordinal}` where the ordinal is the
position in `GetHistoryAsync()`'s projection.

If a prune renumbers turns and nothing else happens, the next index write upserts over the low
ordinals and **leaves the tail documents orphaned** — a 3-turn conversation returning 5 hits, two
of them text that no longer exists.

So a prune that dropped anything must: `DeleteConversationAsync` → reset `_indexedTurnCount = 0`
→ let the existing tail `IndexNewTurnsForSearchAsync()` re-project from ordinal 0.

Four constraints on that, each of which is a defect if missed:

1. **Reset the cursor only after the awaited delete succeeds**, inside the `try` — copy the
   ordering of `DeleteSearchIndexForConversationAsync` (`AgentGrain.Search.cs:165-167`). Index
   writes are best-effort by rule (own try/catch, logged, never rethrown —
   `AgentGrain.Search.cs:10-14`), so a failed delete is swallowed; resetting the cursor anyway
   would re-project over stale ordinals.
2. **A failed delete must be retried, because nothing else can repair it.** `ReindexAsync`
   (`:65-79`) only upserts; `DeleteConversationAsync` is the sole removal. So ADR 0061's
   best-effort licence — which rests explicitly on reindex being a user-invocable repair — does
   **not** extend to a destructive prune. Hold an activation-scoped
   `_searchReconcilePending` flag, set it when the delete throws, and retry on the next
   persisted turn. That restores self-healing without a new endpoint.
3. **Remap `_ephemeralOrdinals`; never clear it.** `DeleteSearchIndexForConversationAsync`
   clears it, which would let a still-resident incognito turn (ADR 0034) be swept into the index
   by the re-projection — exactly what the gotcha at `.agentic/gotchas.md:2772` exists to
   prevent. Subtract the dropped projected-turn count and discard negatives.
4. **The delta is a projected-turn count, not a user-turn count.** `GetHistoryAsync()` emits both
   roles and skips blank turns, so *N* dropped user turns is not *N* dropped ordinals. Compute it
   as `countBefore - countAfter` from `GetHistoryAsync()` either side of the prune — the same
   single-projection rule the gotcha at `:2755` binds.

**Ordering against the state write:** compute the delta either side of the prune (it needs the
pre-prune list), but run the delete/reset/re-project **after** `WriteStateAsync`. Once a chat is
older than the window most turns will drop something, so the reconcile fires on most turns; it is
a filtered-query delete plus N deletes plus N upserts on a non-reentrant grain, and it must not
sit in front of the durable write that ADR 0046's stream recovery depends on.

### Detecting a continuous chat

The grain does not know it is continuous. `Continuous` is `AgentDefinition [Id(7)]` only — absent
from `AgentBinding` (5 fields) and from `AgentGrainState`, because ADR 0016 keeps it client-side
as routing rather than persona. The only server-side signal is the conversation-id shape, minted
in `wwwroot/js/agents.js` as an `agent-` prefix plus the agent id and passed through untouched
into the grain key `{userId}/{conversationId}`.

So detection is `ConversationId().StartsWith("agent-", StringComparison.Ordinal)`, safe because
`crypto.randomUUID()` cannot emit that prefix (already relied upon and commented in `agents.js`).
This is also why **no ADR 0016 reversal is needed** — the flag stays client-side.

### How an incognito turn actually reaches a continuous chat

Not via `/api/agent-run`: `EnqueuePromptAsync` sets `_ephemeral = false` on entry
(`AgentGrain.cs:424`) and the endpoint mints `Guid.NewGuid()` as the conversation id
(`AgentApi.StoredAgents.cs:90`), which the `agent-` predicate excludes anyway.

The reachable path is **`POST /api/chat/{id}` with a client-chosen `agent-…` id and
`Ephemeral: true`** (`AgentApi.Conversations.cs:147`), which the endpoint accepts unvalidated.
AC15 must be written against that path — written against agent-run it can never construct the
state it tests, and the mandated mutation check would report a false green.

### Three persist sites, not one

Verified complete: `grep -rn "SerializedSession" src/` yields exactly three assignments of a
serialized session, plus the `null` in `PurgeAsync`.

1. `PersistTurnUnlessEphemeralAsync` (`AgentGrain.cs:723`) — the chat path
2. `ReceiveReminder` (`AgentGrain.cs:966`) — its **own** serialize, does not delegate
3. `GenerateImageTurnAsync` (`ImageTurn.cs:123`)

`Documents/AgentGrain.Documents.cs:101` and lines 353 / 655 / 1009 are rebuild-only snapshots,
not persist sites — checked, not assumed.

**The three sites guard `_ephemeral` three different ways**, and the helper must respect each:
an early return (`AgentGrain.cs:709-720`), a real `if (!_ephemeral)` block
(`ImageTurn.cs:120-124`), and no guard at all in `ReceiveReminder` (a fired reminder always
persists). At the image site the helper goes **inside** the block.

### Governing ADRs and gotchas to read first

- `.agentic/decisions/0016-*` lines 53-60 — the 2 MB brick and "no code mitigation in scope".
  **This plan changes that scope call**, so it needs an ADR amending 0016. Neither 0016 nor 0004
  ever *rejected* pruning, so nothing is re-adopted against a prior decision — verified.
- `.agentic/gotchas.md:167` — the brick entry; amend it, do not merely cite it.
- `.agentic/gotchas.md:2755` — ordinals from ONE `GetHistoryAsync()` projection.
- `.agentic/gotchas.md:2772` / `:2786` — an incognito turn stays in the in-memory session.
- `.agentic/gotchas.md:451` / `:476` — turn-scoped fields live for the activation.
- `.agentic/gotchas.md:768` — `GeneratedImages` is id-keyed via `AdditionalProperties`.
- `.agentic/gotchas.md:2615` — inside a grain use `.ToList()`, never a collection expression,
  for anything reaching persisted state.

### Accepted residuals (state them in the ADR, do not silently absorb)

- **The 2 MB brick is narrowed, not closed** (see Overview).
- **Attachment blob bytes are orphaned.** `IAttachmentStore` has no delete method, and
  conversation delete already leaves bytes behind. `GeneratedImages` **dictionary entries** are
  removed, because those are grain state and would otherwise grow unbounded.
- **The agent forgets.** Hard delete is the user's decision; ADR 0056 memory, when enabled, is
  the continuity story, and is per-user and unaffected by this prune.
- **A public share link to a continuous agent chat silently loses content over time** — the
  share view reads the same `GetHistoryAsync()` (`Sharing/AgentApi.Sharing.cs:61`).
- **ADR 0032's client IndexedDB mirror keeps pruned text** unencrypted on the device until the
  next refresh of that conversation.
- **A plausible-but-wrong provider `CreatedAt` on the reminder path** survives the clamp.

## Steps

**Step 1 — Clock seam and timestamps.** Inject `TimeProvider` into `AgentGrain` and register it
in the three hosts (or confirm the ctor-default route first — see Affected areas). New
`RetentionLimits.cs` (`Window = 24 h`, `MinimumPreservedTurns = 1`, `MaxPlausibleAge = 365 d`).
New partial `AgentGrain.Retention.cs` with `StampMessageTimestamps()` implementing the clamp rule.
Not yet wired to pruning.
*Verify:* `dotnet test Agent42.slnx --filter FullyQualifiedName~AgentGrainTests` — AC1, AC2.

**Step 2 — The prune.** Add `IsContinuousConversation()` and `PruneExpiredTurnsAsync()`: walk turn
boundaries, keep any leading non-user prefix, drop the leading expired run subject to
`MinimumPreservedTurns`, clamped `TurnAttachments.RemoveRange`, and remove the `GeneratedImages`
entry for every pruned assistant message carrying `assistantImageId`. Return the counts the
caller needs. **This is the method that will approach `cognitive_complexity_max: 15`** — not
`ReceiveReminder`, which gains one statement. If it trips, extract within
`AgentGrain.Retention.cs`; never a ternary.
*Verify:* AC3–AC10.

**Step 3 — Wire the three persist sites.** One helper (stamp → prune) called before each
serialize, respecting each site's `_ephemeral` shape.
*Verify:* `dotnet build Agent42.slnx` (S3776 is an error) plus AC11, AC12.

**Step 4 — Search reconciliation.** In `AgentGrain.Search.cs`, add
`ReconcileSearchIndexAfterPruneAsync(int droppedProjectedTurns)` honouring all four constraints
above, called **after** `WriteStateAsync`, with the `_searchReconcilePending` retry.
*Verify:* AC13–AC16.

**Step 5 — Layout.** Add the new part to `ProjectLayoutTests.AgentGrainParts()` and update its
`<summary>` prose.
*Verify:* `dotnet test --filter FullyQualifiedName~ProjectLayoutTests`.

**Step 6 — Docs.** `/decide` for the ADR amending 0016 (carrying every residual above); `/gotcha`
for the two traps worth an entry — the `_ephemeralOrdinals` remap, and the projected-vs-user-turn
delta; amend `.agentic/gotchas.md:167`; update the two CLAUDE.md bullets. Move this spec to
`.agentic/specs/completed/`.
*Verify:* the doc-path check `pr-checks.yml` invokes, and `dotnet test Agent42.slnx`.

Steps 1→2→3 are strictly ordered; Step 4 depends on Step 2's counts; Step 5 is independent of 2–4
and may run in parallel; Step 6 is last.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A message persisted with no `CreatedAt` is stamped, and the stamp survives deactivate/reactivate | `Turn_StampsMessageTimestamps_ThatSurviveReactivation` | `AgentGrainTests.cs` |
| AC2 | The clamp holds: a provider-supplied epoch `CreatedAt` on the reminder path is replaced with now, not treated as expired | `ReminderTurn_ClampsAnImplausibleProviderTimestamp` | `AgentGrainTests.cs` |
| AC3 | In a continuous chat (`agent-*`), a turn older than the window is dropped on the next persisted turn | `ContinuousChat_PrunesTurnsOlderThanTheWindow` | `AgentGrainTests.cs` |
| AC4 | A turn inside the window is never dropped | `ContinuousChat_KeepsTurnsInsideTheWindow` | `AgentGrainTests.cs` |
| AC5 | A **non**-continuous chat is never pruned, however old | `RegularChat_IsNeverPruned` | `AgentGrainTests.cs` |
| AC6 | Pruning never empties the session — `MinimumPreservedTurns` survives even if all turns are expired | `Prune_AlwaysPreservesTheMostRecentTurn` | `AgentGrainTests.cs` |
| AC7 | Only whole turns are dropped: no `FunctionResultContent` survives without its call | `Prune_NeverOrphansAToolResult` | `AgentGrainTests.cs` |
| AC8 | `TurnAttachments` realigns — surviving turns' images still resolve to the right turns | `Prune_RealignsTurnAttachments` | `AgentGrainTests.cs` |
| AC9 | A legacy conversation whose `TurnAttachments` is shorter than its user-turn count prunes without throwing | `Prune_ToleratesAShortTurnAttachmentsList` | `AgentGrainTests.cs` |
| AC10 | A pruned assistant image's `GeneratedImages` entry is removed | `Prune_DropsGeneratedImageEntriesForPrunedTurns` | `AgentGrainTests.cs` |
| AC11 | The reminder path prunes too | `ReminderTurn_PrunesAContinuousConversation` | `AgentGrainTests.cs` |
| AC12 | The image-generation path prunes too, and its separate-agent session round-trip preserves timestamps | `ImageTurn_PrunesAContinuousConversation` | `AgentGrainTests.cs` |
| AC13 | After a prune, search returns no hit for pruned text and no orphaned tail documents | `Prune_LeavesNoOrphanedSearchDocuments` | `ChatSearchIndexingTests.cs` |
| AC14 | After a prune, ordinals still resolve to the right turns | `Prune_KeepsSearchOrdinalsAnchored` | `ChatSearchIndexingTests.cs` |
| AC15 | An incognito turn still resident after a prune is **not** swept into the index (remap, not clear) | `Prune_DoesNotIndexAnEphemeralTurn_AfterOrdinalsShift` | `ChatSearchIndexingTests.cs` |
| AC16 | A failed index delete leaves the cursor untouched and is retried on the next persisted turn | `Prune_RetriesTheSearchReconcile_AfterAFailedDelete` | `ChatSearchIndexingTests.cs` |
| AC17 | An ephemeral turn never persists, so it never triggers a prune write | `EphemeralTurn_DoesNotPrune` | `AgentGrainTests.cs` |
| AC18 | The new part is listed in `AgentGrainParts()` and exists as a `partial class AgentGrain` | existing `AgentGrainParts` theory | `ProjectLayoutTests.cs` |

**AC15 is load-bearing** — the only test that fails if `_ephemeralOrdinals` is cleared instead of
remapped. Write it against `POST /api/chat/{agent-id}` with `Ephemeral: true` (see above), then
**mutation-check it**: replace the remap with `Clear()` and confirm it goes red. If it stays
green, the setup is wrong, not the code.

AC18 is deliberately worded as existence-and-listing: `ProjectLayoutTests` asserts per-path
existence plus `partial class AgentGrain` (`:151-158`) and has **no count assertion**, so "there
are exactly 14 parts" is not a thing the suite can measure.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time — do not recall these numbers.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura × `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — an **error**, so it blocks `dotnet build` |
| `vulnerable_packages` | **0** | hard block, CI package-freshness job |
| `outdated_packages` | report | soft |

Enforcement: `local: soft_warning`, `ci: hard_block`. No package is added by this plan (hence the
hand-rolled `TimeProvider` double), so the dependency gates should be untouched — but report
them, do not assume.

## Out of scope

- Any per-agent, per-user or operator configuration of the window (fixed constant, by decision).
- Summarizing expiring history before deleting it.
- Adopting `Microsoft.Agents.AI.Compaction` (rejected above, with reasons).
- Deleting orphaned attachment blob bytes, or adding a delete to `IAttachmentStore`.
- Pruning regular (non-continuous) chats.
- Threading `Continuous` into `AgentBinding`/`AgentGrainState` — ADR 0016 stands.
- Replacing `AgentGrain.GetDate()` with the injected `TimeProvider` — it is a model-facing tool.
- Any UI: no indication that pruning happened, no retention control.
- Recovering *real* timestamps for pre-deploy history. Step 1 backfills a **fabricated** "now"
  for those messages, which is what defers their first eligible prune by 24 h; the original
  times were never recorded and cannot be recovered.

## Review

A `critic` subagent reviewed this spec at 941127f and returned four HIGH, six MEDIUM and five LOW
findings. **All were accepted; none rejected.** The material changes:

- **H1 (blocking)** — no clock seam existed, leaving 11 of 15 ACs with no described way to
  produce an expired turn. Added the `TimeProvider` seam, its host-registration ripple, and the
  no-new-package test double.
- **H2** — the original spike measured `RunAsync`, not the production `RunStreamingAsync` path,
  and `??=` would let a provider value drive a deletion. Re-spiked both paths (fact 4) and
  replaced `??=` with the clamp rule.
- **H3** — a swallowed best-effort delete would strand orphans with no repair, since
  `ReindexAsync` cannot delete. Added the cursor-ordering rule and the
  `_searchReconcilePending` retry.
- **H4** — the cited incognito path (`/api/agent-run`) is closed twice over, so AC15 would have
  passed vacuously and the mutation check would have reported a false green. Corrected to
  `POST /api/chat/{id}` with an `agent-` id.
- **M1/M2** — added the `RemoveRange` clamp (a legacy short list would throw mid-turn) and the
  leading-non-user-prefix rule, each with its own AC.
- **M3** — moved the index reconcile after `WriteStateAsync`.
- **M4/M5** — named `ChatSearchIndexingTests.cs`; restated AC18, since `ProjectLayoutTests` has
  no count assertion to satisfy.
- **M6/L1** — corrected the Overview's overclaim and the contradiction between "impossible to
  backfill" and Step 1's fabricated backfill.
- **L2–L5** — S3776 attributed to `PruneExpiredTurnsAsync`; the three `_ephemeral` guard shapes
  spelled out; the share-view and IndexedDB consequences added to residuals; line references
  corrected (`StripToolHistory` 1025, `ProjectUserTurn` 839-852).

The critic also **confirmed** two things the plan had asserted: the persist-site list is complete
(the suspected fourth site is rebuild-only), and no repo path rebuilds `ChatMessage` instances in
a way that would drop `CreatedAt` — the Foundry Responses API is a false suspect, because
serialization belongs to the `ChatClientAgent`'s history provider, not the chat client. AC12 was
added to cover the one round-trip that uses a *different* agent instance.

## Divergences from the plan (recorded at execution, 2026-09-05)

Executed on `feature/prune-continuous-agent-history` off `main @ 941127f` — the exact ref the plan was
written against, so no drift check was needed. Everything in **Steps** and **Acceptance criteria** was
built. What follows is where reality differed, and each entry is something a reader of the plan alone
would get wrong.

### 1. The `TimeProvider` host-registration ripple does not exist — the ctor default holds

The plan hedged: "Check whether the executor can instead give the ctor parameter a default … **Verify
that before relying on it**." Verified empirically, not by reasoning about `ActivatorUtilities`: with
`TimeProvider? timeProvider = null` on the constructor and **no** registration in any silo, the grain
activates and the whole suite passes. So `src/Agent42.Backend/Program.cs`, the AppHost silo and
`tools/Agent42.DevHost/Program.cs` are **untouched**, and only the test silo registers a controllable
clock. Three files fewer, and one fewer way to break a single host.

### 2. Two of the plan's "this is a live bug" claims were measured and are NOT

Both bounds are kept, both are now documented as **defensive**, and neither has a test claiming to guard
it. Writing them up as fixes would have been the more flattering and less true option.

- **M1, the `TurnAttachments.RemoveRange` clamp.** The plan says an unclamped call "throws
  `ArgumentException` **inside the turn, after the model has answered** — ADR 0016's brick shape."
  Mutation-checked: removing the `Math.Min` leaves the **entire suite green**. The reason is upstream —
  all three persist paths call `AlignTurnAttachmentsToHistory` + `Add` **before** their run, and `Align`
  pads using the same user-message count the prune walks, so the legacy short list is repaired before the
  prune can ever see it and the dropped count is always at least one short of `Count`. AC9 still exists
  and still passes; its comment now states exactly what it does and does not prove.
- **`MinimumPreservedTurns`.** Cannot bind through any shipped path, for a related reason: a prune only
  runs from a persist site, and the turn being persisted was stamped moments earlier, so the newest turn
  is always inside the window and the leading-run walk stops before it. AC6 was rewritten to assert the
  property that *is* reachable — a chat whose every earlier turn expired still comes back with history.

### 3. AC15 is driven through the grain, not `POST /api/chat/{id}`

The plan's correction of H4 was right about the *substance* — `/api/agent-run` cannot construct the
state, so a test written against it is a false green — but the endpoint is not the only way to avoid
that. `ChatSearchIndexingTests` has no API host, and the endpoint passes its `Ephemeral` flag straight
into `IAgentGrain.ChatAsync`, so the grain-level call constructs an identical state and matches every
neighbouring ephemeral test in that class. The **mutation check the plan mandated was run and passed**:
replacing the remap with `Clear()` turns that test — and only that test — red.

One thing the plan did not specify and which decides whether AC15 is vacuous: the incognito turn must be
taken **after** the clock advance. A shared advance would have the prune drop the incognito turn too,
leaving nothing to sweep.

### 4. Two acceptance tests were wrong on the first run, in ways worth recording

- **AC10** passed `null` as the model for its second turn, which keeps the conversation on the model it
  is **bound** to — still the image model — so it generated a *second* image and measured the new
  `GeneratedImages` entry rather than the pruned one's absence. Now pinned to `DefaultModel` explicitly.
- **AC13** originally pruned a conversation to the *same* length it had before, in which case the
  re-projection upserts over every old ordinal and an orphan cannot exist whatever the code does. It now
  uses two expired turns and one fresh one, so the conversation genuinely shortens and ordinals 2–3 have
  nothing to overwrite them.
- **AC16** asserted that the surviving turn was indexed despite the failed delete. Measured, it is not:
  a failed reconcile leaves the index stale in **both** directions, because the cursor is deliberately
  not reset and then sits past the shortened history. The test asserts the real shape, and the ADR and a
  gotcha both record it.

### 5. A measured cost the plan did not anticipate: stamping is not free

`ToolResults_DoNotBlowTheSessionSizeBudget` failed on first run. `createdAt` on every message costs
**3,164 bytes over ~90 messages** on that test's conversation — **261,106 → 264,270**, about 35 bytes a
message, or 1.2% — charged against the same 2 MB item this feature exists to protect. The canary was
already at 99.6% of its 256 KB ceiling before this branch, so it was moved to 288 KB **with the
measurement written beside it**, rather than the number being quietly nudged. Recorded in ADR 0068's
Consequences.

### 6. Scope additions, both small

- `AssistantImageId` was extracted from `AssistantImageRef` in `AgentGrain.ImageTurn.cs`: the prune needs
  the **key** to drop the `GeneratedImages` entry, not the ref.
- `TestChatSearchIndex` gained `FailNextDeletesFor(userId, times)` — delete-only, counted failure
  injection. The existing `FailFor` is all-or-nothing per user and latched, and AC16 needs a delete to
  fail while indexing keeps working and a later delete succeeds.

### 7. What the `critic` pass on the finished diff changed (one HIGH, four MEDIUM)

The review ran on the completed diff, before commit. All findings were accepted; none rejected.

- **HIGH — the accumulated reconcile delta remapped `_ephemeralOrdinals` in the wrong frame.** The plan
  prescribed an accumulating `_searchReconcilePending` delta ("a retry that fired on a turn which itself
  pruned nothing would otherwise remap by zero"). That reasoning is wrong: an ephemeral turn taken
  *between* a failed reconcile and its retry records its ordinals in the **already-pruned** frame, so
  re-applying the earlier shift walks them backwards onto real turns — **indexing the incognito text into
  Cosmos** and skipping two genuine turns. Confirmed against the pre-fix build, not argued: `the incognito
  token` written at ordinal 2. Fixed by remapping in `PruneExpiredTurnsAsync`, where the shift happens,
  and reducing the retry state to a bare `bool` (the delete removes the whole conversation regardless of
  delta, so the count had no other consumer). New regression test
  `Prune_DoesNotIndexAnEphemeralTurn_TakenBetweenAFailedReconcileAndItsRetry`; neither AC15 nor AC16 can
  reach that state.
- **MEDIUM — the retry is not self-healing across a deactivation.** The plan claimed it "restores
  self-healing without a new endpoint". It is activation-scoped, so an idle-out with a retry outstanding
  makes the orphans permanent. The claim is corrected in the code, the gotcha and ADR 0068's Consequences,
  with a delete-then-reindex `ReindexAsync` recorded as the follow-up that would actually close it.
- **MEDIUM — stamping was unconditional.** The continuity gate lived inside `PruneExpiredTurnsAsync`, so
  every regular chat paid the `createdAt` tax for a value it would never read. Moved up into
  `ApplyRetentionAsync`. This also **retired divergence 5 above**: with the gate in place the session-budget
  canary stays at its original 256 KB, and `RegularChat_IsNotStamped` pins the decision.
- **MEDIUM — the leading-non-user-prefix rule had no test**, despite this plan's Review section claiming
  "each with its own AC". It now has one (`Prune_KeepsALeadingNonUserPrefix`), mutation-checked by
  replacing `start = userIndices[0]` with `start = 0`. The state is unreachable through the shipped write
  paths, so the test injects a system message into the serialized session directly.
- **MEDIUM — ADR 0016 and ADR 0061 had no `Amended by` back-pointer**, against an 11-instance repo
  convention, leaving ADR 0016's "No code mitigation is in scope" reading as current. Both now carry one at
  the amended claim.
- **MEDIUM — the fake clock's docstring asserted a safety it had not shown.** It skews `GetUtcNow`
  silo-wide, which Orleans also reads, and a +25 h jump is past the activation collector's 2 h default. The
  docstring now carries the evidence instead of the assertion: both AC15 and AC16 fail under mutation,
  which they could only do if the activation-scoped state survived the jump.
- Two LOW findings were folded into ADR 0068's Consequences: pruned text **survives** in Hindsight (no
  delete path) and in the `/logs` ring, and the reconcile sits in the user's latency path.

**Net effect on divergence 5:** the 256 KB → 288 KB canary move described there was undone. The
measurement stands and is still recorded; the gate is what made it unnecessary.
