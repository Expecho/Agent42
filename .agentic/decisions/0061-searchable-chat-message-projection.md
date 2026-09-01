---
id: 0061
date: 2026-09-01
status: accepted
tags: [search, cosmos, projection, privacy, data-at-rest, no-migration]
supersedes: []
extends: [0004, 0034, 0039]
amends: []
---

# Search inside chat history via a plain-text message projection

## Context
A user can search conversation **titles** and nothing else. Message text exists only inside
`AgentGrainState.SerializedSession` — one opaque JSON string per conversation grain, in the
Orleans-managed `agent-state` container.

The corpus only grows, and the projection is the expensive half of any search feature. Building it
later means a strictly bigger backfill over strictly more conversations, so the cost of deferring is
monotonically increasing.

### Why the existing state cannot simply be indexed
Cosmos full-text search indexes a **named string path** and tokenizes it. `SerializedSession` is one
JSON string holding an entire conversation, so full-text indexing it would tokenize JSON syntax and
keys — `messages`, `role`, `assistant`, `contents` — alongside the actual content. Two things break:
BM25's inverse-document-frequency is polluted by tokens that appear in literally every document, and
"document length" comes to mean *whole conversation* rather than *message*, which is the unit a user
searches for.

FTS therefore **requires** a clean-text corpus. That is the entire reason a second container exists.
This is not an optimisation that could be skipped for a v1 — it is the precondition. Do not attempt to
index the grain-state container, and do not treat the projection as a cache that could be dropped.

### Measured facts about the Orleans Cosmos layout
Established by reflection over `Microsoft.Orleans.Persistence.Cosmos` 10.2.2 during planning:

- The stored envelope is `GrainStateEntity<T>` to `{ id, PartitionKey, GrainType, State, ETag }`.
- `DefaultPartitionKeyProvider` returns the grain **TYPE** (e.g. `"agent"`), *not* the grain key. So
  every conversation grain of every user shares one logical partition. That is tolerable for
  point-reads by id, and unusable for a scoped query.

The new container therefore partitions by **`/userId`**, which makes every search single-partition
*and* owner-scoped by construction rather than by a `WHERE` clause a future edit could drop.

## Decision
Chat messages get a **searchable plain-text projection** in a new Cosmos container, `chat-messages`,
partitioned by `/userId`. One document per message. A new port `IChatSearchIndex` (Agents) has a
Cosmos adapter (Backend) and an in-memory twin — the `IDocumentIndex` / `IAttachmentStore` split this
repo already uses three times.

The container is created **with** a full-text policy (`defaultLanguage: en-US`,
`fullTextPaths: [{ path: '/text' }]`) and a matching `fullTextIndexes` entry, but **v1 queries with
`CONTAINS`**. The policy is set at creation because a container's full-text policy cannot be added
afterwards — so paying for it now makes the eventual switch to BM25 `ORDER BY RANK FullTextScore` a
**query change, not a migration**. Shipping without it would mean a second backfill later.

### The corpus is both sides of every turn
User **and** assistant text. A reply is findable, not just the prompt that produced it. The accepted
cost is roughly an order of magnitude more storage than a user-only index, which is the right trade at
this scale: the thing a user actually remembers is usually something the *assistant* said.

### The write is best-effort, and this is NOT ADR 0008's rejected shape
**Do not describe this feature as "following ADR 0008."** ADR 0008's *Alternatives* section explicitly
**rejects** "always-on but ungated", and its per-conversation `DocumentNames.Count > 0` gate is a
load-bearing property of that decision. This plan takes a shape 0008 refused, so the relationship is a
**distinction, not a precedent**.

What 0008 rejected was a per-turn **third-party embedding round-trip**, whose failure mode was *a
broken embedding deployment breaking every conversation*. This feature's per-turn work is a **local
Cosmos upsert** — no third party, no embedding, no new egress, and no new availability dependency.

What 0008's reasoning **does** bind, and what is honoured here: the index write must never be able to
break a turn. **Every index write is wrapped in its own try/catch, logged, and never rethrown**, at all
three write sites.

**The acknowledged tension, stated rather than hidden.** Best-effort means a dropped write is an
*invisible gap* in search results — the same invisibility that the planning interview rejected when it
chose a full backfill over "new messages onward". These two decisions pull against each other. What
closes the gap is that **`POST /api/search/reindex` is idempotent and reachable by the owner**, so a
suspected gap is repairable without an operator. That repairability is what makes best-effort
acceptable here; without it the two decisions would genuinely conflict. **It currently has no UI —
see Consequences**, which weakens this argument in practice without changing its shape.

### Ordinals come from one projection function, or the two paths diverge
A document's id is `{conversationId}:{ordinal}`, where the ordinal is the index into
**`GetHistoryAsync()`'s projected list**. Both the live write and the backfill derive it from *that
same projection call*.

The cursor that decides which turns are "new" (`_indexedTurnCount`) is **activation-scoped and
starts at zero**, so the first indexed turn of each activation re-upserts the **whole** conversation
and only later turns in that activation append. That is a deliberate trade — see Consequences for
what it costs — and it is not "upserts the trailing turns", which is what an earlier draft of this
ADR said.

One projection function, so the two paths **cannot disagree by construction**. This is what makes
reindex idempotent (a re-run overwrites the same ids rather than appending duplicates), and it is the
single easiest thing in this feature to ship subtly broken — a live path counting raw session messages
and a backfill counting projected turns would silently double-index every conversation that ever had a
skipped turn. AC9 and AC14 both guard it.

### No `createdAt` on the hit record
`ChatTurn` carries only `Role`, `Text`, `Attachments` — there is no per-message timestamp anywhere in
the session. So the backfill could not reproduce a timestamp the live path invented, and the two paths
would write **different values for the same document id**, making reindex non-idempotent in a way no
test would obviously catch. The field is dropped. A date, if the UI ever needs one, comes from the
conversation-level `UpdatedAt` on `ConversationIndexGrain`.

### `en-US`, knowing the chats are partly Dutch
Cosmos FTS supports `en-US`, `de-DE`, `es-ES`, `fr-FR`, `it-IT`, `pt-PT`, `pt-BR`. **Dutch is not
supported.** `en-US` is GA and needs no preview enrollment, and Dutch text still tokenizes and matches
**exactly** — it simply will not *stem*, so `vergaderingen` will not match `vergadering`.

*Rejected: `de-DE`.* It is still not Dutch, it is preview (requiring the portal enrollment this
decision avoids), and it would mis-stem the English chats, which are the majority.

### Matching is case-insensitive on both sides, explicitly
Cosmos: `CONTAINS(c.text, @q, true)` — **the third argument is required**; the default is
case-sensitive. In-memory twin: `StringComparison.OrdinalIgnoreCase`. The two halves are written in
different steps and the Cosmos half cannot be *executed* by a test, so they would diverge silently
unless pinned. AC15 pins the twin. The Cosmos half is pinned too, without a Cosmos account: the query
text is built by a pure `BuildSearchSql`, and `SearchSql_AsksForCaseInsensitiveMatching` asserts the
third argument is there. The same split pins the delete as a filtered query rather than the
partition sweep its exemplar uses. What still rests on a prod smoke is only that Cosmos *executes*
that SQL as expected — not that we asked for the right thing.

### The search branch lives inside `renderSidebar`
`renderSidebar` is called from nine sites that fire independently of the search box — a background
refresh, ADR 0032 cache hydration, an agent save, `send()`'s success path. So the search render is an
**early return inside `renderSidebar`**, never a separate render path beside it. Any other shape means
a background refresh silently wipes an active result list while the user is reading it. AC16 guards
this by source text (the ADR 0058 precedent for a client rule with no runtime test).

## Consequences

**The headline consequence: a second copy of every message now exists at rest.** Conversation text
lived in exactly one place — the grain-state item, reachable only by grain key. It now also lives as
one queryable document per message in a container built to be searched. Nothing about the *egress*
surface changes (no third party, no new network call), but the at-rest surface roughly doubles, and
the new copy is in a shape designed for retrieval rather than one designed for point-reads. Anyone
reasoning about where conversation content lives — ADR 0035's share links, ADR 0032's IndexedDB
mirror, ADR 0054's `/logs` — must now count this container too.

**Deleting a conversation deletes its indexed messages, and `PurgeAsync` is the only path.** The
delete is *not* a copy of `CosmosDocumentIndex`'s whole-partition sweep: `doc-chunks` partitions by
`/ownerKey` = `{userId}/{conversationId}`, so its delete can drop a whole partition. This container
partitions by `/userId`, so a conversation delete is a **filtered query plus a point-delete per
message inside the user's partition**. That runs in a **non-reentrant** grain which
`DELETE /api/conversations/{id}` awaits synchronously, so it is bounded (filtered, bounded
concurrency) and best-effort — a slow index delete must not hang the endpoint.

**Incognito chats produce zero index documents.** ADR 0034's guarantee is extended to the new
container, and the three write sites have **three different `_ephemeral` shapes** — an early return, a
real `if (!_ephemeral)` block, and one site with no guard at all because a fired reminder always
persists. This asymmetry is exactly the kind of thing that invites a fresh, wrong guard; it is
recorded in gotchas rather than left to be rediscovered.

**Reindex costs a grain activation per conversation, so it is bounded rather than merely polite.**
Activating a conversation grain runs `OnActivateAsync`, which calls `_mcpToolProvider.GetForUserAsync`
— a **sequential** connect with a 10 s timeout per configured MCP server, and an InteractiveOAuth
refresh **writes**. A reindex is a POST so the write is acceptable, and the provider is version-cached
per user so only the first activation pays; but a naive unbounded sweep over a user with several dead
MCP servers would stall for minutes. The endpoint takes a batch size and a per-call cap, and returns a
count plus whether more remain.

**The live write re-upserts the whole conversation once per activation, and on a serverless account
that is real write amplification.** A 400-message chat costs 400 upserts of up to 32 KB the first
time it is touched after activation — and reminders and agent-runs activate grains too, with no user
present. What it buys is **self-healing**: a write dropped by the best-effort rule in an earlier
activation is silently repaired the next time the conversation is used, which narrows the invisible
gap that best-effort would otherwise leave to the reindex endpoint alone. If the RU cost ever bites,
persisting the cursor is the obvious fix and it gives up exactly that property.

**Incognito is enforced per activation, and a mixed conversation has a residual that is ADR 0034's,
not this feature's.** Ephemeral ordinals are tracked for the activation and excluded by *both* the
live path and `ReindexAsync`. But `PersistTurnUnlessEphemeralAsync` serializes the **whole session**,
so once a *later normal* turn persists, that write carries the earlier ephemeral messages with it —
pre-existing ADR 0034 behaviour, independent of search. After a deactivation those turns are
therefore indistinguishable from ordinary history and a reindex will index them. The shipped UI
cannot produce such a conversation (the incognito toggle locks after the first send); the API can.
Recorded rather than papered over.

**The reindex endpoint has no UI affordance, so "user-invocable" means "by hand".** The best-effort
argument above leans on repairability, and `POST /api/search/reindex` is reachable only via curl or
devtools — there is no button anywhere in `wwwroot/`. Nothing backfills existing history on deploy
either, so in practice a first deployment behaves like the "new messages onward" option the planning
interview rejected until someone calls it. A Preferences affordance is the obvious follow-up and was
out of this plan's scope; saying so is better than letting the word "user-invocable" imply a control
that does not exist.

**No grain state changes, so there is genuinely no migration.** This adds no `AgentGrainState` field
and no new persisted type, so ADR 0039's `$type` hazard has no exposure here. `ReindexAsync` is a new
`IAgentGrain` method and carries an `[Alias]` like every other method in that file.

**`/api/search` is Easy-Auth gated and takes its userId from `http.GetUserId()`, never a query
parameter.** It is not on `excludedPaths`; no token path (ADR 0022's `/api/agent-run`, ADR 0035's
share links) reaches the index at all.

## Alternatives considered
- **Index `SerializedSession` in place with a full-text policy.** Rejected — see Context; it tokenizes
  JSON structure and makes "document" mean "conversation". This is the alternative that looks cheapest
  and is actually unworkable.
- **BM25 ranking in v1** (`ORDER BY RANK FullTextScore`). Deliberately deferred: the policy is created
  so this is a later query change, not a migration. `CONTAINS` is adequate at personal-corpus scale and
  has no ranking to tune or explain.
- **Hybrid / semantic search** (Cosmos RRF over `FullTextScore` + `VectorDistance`). Rejected for v1:
  it would re-import ADR 0030's per-user embedding-model dependency and add a per-turn embedding
  round-trip — **precisely the shape ADR 0008 rejected**, and precisely the property that lets this
  feature claim it is not that shape. It is the natural upgrade path, and it stops being free the
  moment it is taken.
- **Multi-language or fuzzy search.** Both preview, both need the "Preview Capabilities for Full-Text
  Search" portal enrollment this decision avoids.
- **"New messages onward", no backfill.** Rejected by the user in the planning interview: the gap
  would be invisible, and a search box that silently cannot see last month is worse than no search box.
- **User text only, no assistant replies.** Rejected: an order of magnitude cheaper in storage, but it
  cannot find the answer, only the question.
- **A full-page search overlay** (the ADR 0031 usage-dashboard shape), and both-of-them. Rejected by
  the user in favour of a sidebar filter box, which reuses the list the user is already looking at.
- **Cross-user or admin-wide search.** Out of scope and structurally excluded: the partition key *is*
  the owner.

## Verification
See the plan's AC-to-test mapping. The tests that carry the real risk are `Reindex_IsIdempotent` and
`Reindex_AfterLiveIndexing_ProducesNoDuplicates` (the ordinal-divergence trap — the second is the
load-bearing one, since live-then-reindex is the case the two paths can disagree on),
`EphemeralTurn_IsNotIndexed` plus `Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne` (the ADR 0056
pattern: the resume test is the half that fails when a gate is stuck OFF as well as when it is
captured), and `ThrowingIndex_DoesNotBreakTheReply` for the best-effort rule.

## Unverified residual
**The Cosmos wire path is prod-only and has no automated test** — the exact position
`CosmosDocumentIndex` has been in since ADR 0007, and for the same reason: DevHost and the Cosmos
emulator use the in-memory twin, so `CONTAINS(c.text, @q, true)` and the full-text policy are never
executed by CI. Verified by prod smoke after deploy, not by the suite.

**Whether this serverless account needs an account-level capability for FTS was treated as unproven.**
Vector search needed `EnableNoSQLVectorSearch` in this same account (see the capabilities block in
`infra/modules/cosmos.bicep`), and the FTS GA announcement implies no equivalent is required — but a
`what-if` validates ARM *schema* acceptance, not data-plane behaviour. See Divergences for what was
actually determined.
