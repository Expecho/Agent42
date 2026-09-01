# Plan — Search inside chat history

planned-against: main @ b5dd615

## Overview

Today a user can search conversation **titles** and nothing else. Message text exists only
inside `AgentGrainState.SerializedSession`, one opaque JSON string per conversation grain.

This plan adds a **searchable plain-text projection** of chat messages in a new Cosmos
container, an owner-scoped search endpoint, and a sidebar search box. v1 queries with
`CONTAINS` over a clean `/text` path; the container is created *with* a full-text policy so
switching to BM25 `ORDER BY RANK FullTextScore` later is a query change, not a migration.

Why now: the corpus only grows, and the projection is the expensive half. Building it late
means a bigger backfill.

## Affected areas

Verified against the tree at b5dd615.

| Area | Files |
|---|---|
| New Agents feature folder | `src/Agent42.Agents/Search/` (port, hit record, in-memory twin) |
| New Backend feature folder | `src/Agent42.Backend/Search/` (Cosmos adapter, registration, endpoints, contracts) |
| Grain write/delete path | `src/Agent42.Agents/Conversations/AgentGrain.cs`, `src/Agent42.Agents/ImageGeneration/AgentGrain.ImageTurn.cs` |
| Grain contract | `src/Agent42.Agents/Conversations/IAgentGrain.cs` |
| Infra | `infra/modules/cosmos.bicep`, `infra/main.bicep`, `infra/modules/container-app.bicep` |
| Test grain-dep registration | `tests/Agent42.Tests/TestServiceRegistration.cs` **and** `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` (`CapSizedFetcherSiloConfigurator` registers inline) |
| Host registration | `src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs` |
| **Layout guard (WILL FAIL until updated)** | `tests/Agent42.Tests/ProjectLayoutTests.cs` — `ExpectedFolders()` asserts the folder set **by name**; also its "sixteen endpoint groups" comment |
| API composition root | `src/Agent42.Backend/Api/AgentApi.cs` |
| Frontend | `src/Agent42.Backend/wwwroot/js/search.js` (new), `js/main.js`, `js/sidebar.js`, `index.html`, `styles.css`, `sw.js` |
| Docs | `.agentic/decisions/0061-*.md` (new), `.agentic/gotchas.md`, `CLAUDE.md`, `docs/features/conversation-history.md` |

## Context & constraints

Judgment and findings the executor cannot cheaply re-derive. Code facts are pointers, not copies.

### Why a projection at all (the core rationale)

Cosmos full-text search indexes a **named string path** and tokenizes it. `SerializedSession`
is one JSON string per conversation, so full-text indexing it would tokenize JSON syntax and
keys (`messages`, `role`, `assistant`) alongside content, polluting BM25's inverse-document-
frequency, and "document length" would mean *whole conversation*. FTS therefore **requires** a
clean-text corpus. This is the whole reason for the new container; do not try to index the
grain-state container.

### Measured facts about Orleans' Cosmos layout

From reflection over Microsoft.Orleans.Persistence.Cosmos 10.2.2 during planning. Do not re-derive.

- Envelope is `GrainStateEntity<T>` → `{ id, PartitionKey, GrainType, State, ETag }`.
- `DefaultPartitionKeyProvider` returns the **grain TYPE** (e.g. `"agent"`), *not* the grain key —
  so every conversation grain of every user shares one logical partition. This is why the new
  container partitions by `/userId` instead: per-user single-partition, owner-scoped by construction.
- `CosmosIdSanitizer.Sanitize` maps `/` to `~0`.
- `SerializedSession` really is readable escaped JSON in the stored document — see the golden doc
  in `tests/Agent42.Tests/GrainStorageSerializerTests.cs`. Background only: we are *not* querying it.

### Verified externally, 2026-09-01 — evidence paths, re-check only if a step fails

- **Cosmos FTS is GA and needs no portal enrollment** for base search, BM25, `ORDER BY RANK`,
  hybrid and weighted RRF. Only **multi-language** and **fuzzy** are preview and need the
  "Preview Capabilities for Full-Text Search" enrollment; `en-US` uses neither.
  Source: <https://devblogs.microsoft.com/cosmosdb/new-generally-available-and-preview-search-capabilities-in-azure-cosmos-db-for-nosql/>
- `fullTextPolicy` (`defaultLanguage`, `fullTextPaths[{path, language}]`) and
  `indexingPolicy.fullTextIndexes[{path}]` are in the ARM schema; `2026-03-15` — the version this
  repo already uses — is a documented stable API version.
  Source: <https://learn.microsoft.com/en-us/azure/templates/microsoft.documentdb/databaseaccounts/sqldatabases/containers>
- Supported FTS languages: `en-US`, `de-DE`, `es-ES`, `fr-FR`, `it-IT`, `pt-PT`, `pt-BR`.
  **Dutch is not supported.** Source: <https://learn.microsoft.com/en-us/azure/cosmos-db/gen-ai/full-text-search>
- **NOT verified, and Step 3 must settle it before deploy:** whether this **serverless** account
  needs an account-level capability for FTS, the way it needed `EnableNoSQLVectorSearch` for vector
  search (see `infra/modules/cosmos.bicep`, capabilities block). The GA announcement implies no, but
  the vector precedent is right there in the same file and a `what-if` validates ARM *schema*
  acceptance, not data-plane behaviour. Treat "no capability needed" as **unproven**.

### Decisions taken by the user in the planning interview

1. **Corpus = user + assistant text.** Both sides of every turn, so a reply is findable, not just a
   prompt. Accepted cost: roughly an order of magnitude more storage than user-only.
2. **Backfill = yes, one-time.** Rejected: "new messages onward", because the gap would be invisible.
3. **UI = sidebar filter box.** Rejected: a full-page overlay (ADR 0031 shape) and both-of-them.
4. **FTS language = `en-US`.** Chosen knowing chats may be partly Dutch: `en-US` is GA and needs no
   preview enrollment, and Dutch text still tokenizes and matches exactly — it just will not stem
   (`vergaderingen` will not match `vergadering`). `de-DE` was rejected: still not Dutch, it is
   preview, and it would mis-stem the English chats.

### Resolved design decisions (these were open questions; do not re-open)

- **Ordinal scheme.** Document id is `{conversationId}:{ordinal}`, where ordinal is the index into
  **`GetHistoryAsync()`'s projected list**. Both the live write and the backfill derive it from
  *that same projection call* — the live path re-projects the conversation after the session is
  updated and upserts the trailing turns by their projected index. One projection function, so the
  two paths cannot disagree by construction. This is what makes reindex idempotent, and it is the
  single easiest thing to ship subtly broken, so AC9 and AC14 both guard it.
- **No `createdAt` on the hit record.** `ChatTurn` carries only `Role`, `Text`, `Attachments` — there
  is no per-message timestamp anywhere, so the backfill could not reproduce one the live path
  invented, and the two would write different values for the same document. If the UI needs a date,
  read the conversation-level `UpdatedAt` from `ConversationIndexGrain`.
- **Case-insensitive matching, explicitly, on both sides.** Cosmos: `CONTAINS(c.text, @q, true)` —
  the third argument is required, the default is case-sensitive. In-memory twin:
  `StringComparison.OrdinalIgnoreCase`. The two are written in different steps and the Cosmos half is
  unreachable by test, so they will diverge silently unless pinned. AC15 pins the twin.
- **`ChatCoreAsync` complexity is NOT an open question.** ADR 0051 already cleared it —
  `.agentic/decisions/0051-*.md` lists `ChatCoreAsync | 39 | Four yield-free blocks extracted`. The
  39 in CLAUDE.md is the **pre-0051** figure, quoted in the present tense. That is stale doc debt for
  Step 10, not something to investigate. This plan does not touch `ChatCoreAsync` regardless.

### Governing ADRs and gotchas — read before writing code

- **ADR 0008, "Alternatives" — required reading.** It explicitly **rejects** "always-on but
  ungated". Do **not** describe this plan as "following ADR 0008". What 0008 rejected was a per-turn
  **third-party embedding round-trip**, whose failure mode was *a broken embedding deployment
  breaking every conversation*. This plan's per-turn work is a **local Cosmos upsert** — no third
  party, no embedding, no new egress. What 0008's reasoning **does** bind: the write must never be
  able to break a turn. **The index write is best-effort — its own try/catch, logged, never
  rethrown.**
  *Acknowledged tension:* best-effort means a dropped write is an invisible gap in search results —
  the same invisibility interview decision 2 rejected for the backfill. What closes it is that
  `POST /api/search/reindex` is **user-invocable and idempotent**, so a suspected gap is repairable.
  Say this in the ADR rather than presenting the two decisions as unrelated.
- **ADR 0034 (ephemeral/incognito).** `_ephemeral` is turn-scoped and lives for the *activation*.
  **Read the exact shapes before editing — they are not uniform** (see Step 6): one site is an early
  return, one has no guard at all, one is a real `if (!_ephemeral)`. An incognito chat must produce
  **zero** index documents.
- **ADR 0039 + the `$type` gotcha.** Every new type stays in the **flat** `Agent42.Agents` /
  `Agent42.Backend` namespace however deep its folder. This plan adds no grain state and no
  `AgentGrainState` field, so there is genuinely no migration and no `$type` exposure.
- **ADR 0041 + the CACHE gotcha.** Any MR touching `wwwroot/js/` MUST bump `CACHE` in `sw.js`
  (`agent42-shell-v25` → `-v26`), and `SHELL` must list exactly the module directory.
- **Gotcha "Cosmos containers must be pre-declared in Bicep."** `disableLocalAuth: true`; AAD
  data-plane RBAC cannot create containers. The adapter must **never** create on use.
- **ADR 0004 (2 MB item cap).** Each message is its own item, so the cap is per-message. Still a hard
  ceiling: cap indexed text per document well below it (Step 2).
- **ADR 0054** pins `minReplicas = maxReplicas = 1` — the backfill runs on one replica.
- **Gotcha "must NEVER call `IUserMcpToolProvider`" (the `GET /api/tools` entry).** Relevant to
  Step 7: activating a grain runs `OnActivateAsync`, which calls `_mcpToolProvider.GetForUserAsync` —
  a **sequential** connect with a 10 s timeout per server, and an InteractiveOAuth refresh **writes**.
  A reindex is a POST so the write is acceptable, and the provider is version-cached per user so only
  the first activation pays — but that is why the sweep must be bounded, not merely polite.

### Exemplars to copy (shape, not invention)

- `src/Agent42.Backend/Documents/CosmosDocumentIndex.cs` — direct container query, parameterized,
  never-create-on-use, and its "prod-only, no unit test" comment. **Note where it does NOT transfer:**
  its delete is a whole-partition sweep because `doc-chunks` partitions by `/ownerKey` =
  `{userId}/{conversationId}`. Ours partitions by `/userId`, so delete must filter by
  `conversationId` within the user's partition (see Step 6).
- `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs` — the registration shape, including
  *why* it lives in the feature folder rather than `Program.cs` (ADR 0050 complexity).
- `src/Agent42.Agents/Documents/InMemoryDocumentIndex.cs` + `tests/Agent42.Tests/DocumentIndexTests.cs`
  — the offline twin and how it is tested. The store is **static / process-global by design**: the
  convention is a **unique owner key per test and no reset**. Do **not** add a row to
  `SharedClusterFixture.ResetSiloState()` — it is a class fixture and classes run in parallel.
- `tests/Agent42.Tests/TestServiceRegistration.cs` — **the canonical place for a new grain dependency
  in tests.** Its own comment says a dep "lives here rather than being repeated per configurator",
  and names `CapSizedFetcherSiloConfigurator` as the exception that registers inline, so **anything
  added here must be added there too**.
- The `/api/speech`, `/api/config-health`, `/api/skills`, `/api/google/*` pattern: an endpoint
  resolves an optional dependency via **`IServiceProvider` + `GetService`**, adding no new mandatory
  registration to the thirteen files that call `MapAgentApi`.
- `infra/modules/cosmos.bicep` `docChunksContainer` — container resource shape and comment style.

## Steps

Steps 2 and 3 are independent and **parallelizable**. Everything else is ordered.

### Step 1 — ADR 0061 (`/decide`)

Record: the projection rationale; the ADR 0008 relationship as a *distinction, not an amendment*,
plus the best-effort rule **and its acknowledged invisible-gap tension**; second-copy-at-rest as the
headline consequence; the `en-US` call and the Dutch tradeoff; `CONTAINS` now / FTS later and why the
policy is set at creation anyway; hybrid/RRF as an explicit non-goal; and the honest residual that
the Cosmos wire path is prod-only.

**Verify:** `dotnet test --filter QualityGateTests` and the CI doc-path integrity check pass.

### Step 2 — Agents port + in-memory twin (parallelizable with Step 3)

New `src/Agent42.Agents/Search/`: `IChatSearchIndex` (`IndexTurnsAsync`, `SearchAsync`,
`DeleteConversationAsync`), a `ChatSearchHit` record (`conversationId`, `role`, `text`, `ordinal` —
**no `createdAt`**), and `InMemoryChatSearchIndex` (static store, `OrdinalIgnoreCase` matching, text
cap constant well under 2 MB). Flat namespace.

**Also in this step:** add `"Search"` to `ProjectLayoutTests.ExpectedFolders()` for `Agent42.Agents`,
or the suite fails immediately with an error attributed to the wrong change.

**Verify:** new `tests/Agent42.Tests/ChatSearchIndexTests.cs` modelled on `DocumentIndexTests.cs`,
unique owner key per test, covering match, owner scoping, delete-by-conversation, empty query, the
text cap, and case-insensitivity. Run `dotnet test --filter "ChatSearchIndex|ProjectLayout"`.

### Step 3 — Bicep container (parallelizable with Step 2)

Add a `chatMessagesContainerName` param to `infra/modules/cosmos.bicep` and a container resource:
partition key `/userId`; `fullTextPolicy` with `defaultLanguage: 'en-US'` and
`fullTextPaths: [{ path: '/text', language: 'en-US' }]`;
`indexingPolicy.fullTextIndexes: [{ path: '/text' }]`. Thread through `infra/main.bicep` (default
`'chat-messages'`) and `infra/modules/container-app.bicep` as env var
`Cosmos__ChatMessagesContainerName`. Comment in the style of the `docChunksContainer` block.

**Settle the open capability question first** (see Context): determine whether this serverless
account needs an account-level capability for FTS as it did `EnableNoSQLVectorSearch` for vector
search. If yes, add it to the capabilities array and note it as an in-place account update.

**Verify:** `az deployment group what-if` against `rg-agent42` shows exactly one container added and
the API version accepts `fullTextPolicy`. Do **not** deploy in this step.

### Step 4 — Backend Cosmos adapter + registration

`src/Agent42.Backend/Search/CosmosChatSearchIndex.cs` — copy `CosmosDocumentIndex`'s shape; **query
term as a `@parameter`, never interpolated**; `CONTAINS(c.text, @q, true)`; never create on use.
`ChatSearchIndexRegistration.cs` in the feature folder following `DocumentIndexRegistration`; absent
`Cosmos:ChatMessagesContainerName`, register the in-memory twin so DevHost and the emulator are
unaffected. Add `"Search"` to `ExpectedFolders()` for `Agent42.Backend`.

**Coverage note:** this adapter is ~100 lines of deliberately untested production C# (the
`CosmosDocumentIndex` precedent), and `check-new-code-coverage.mjs` has no prod-only carve-out. The
branch's other new C# must carry it above `coverage_new_code_pct_min: 80`. If CI fails on this,
the fix is more tests elsewhere on the branch — **not** editing the gate file.

**Verify:** `dotnet build`; `dotnet test --filter ProjectLayout`.

### Step 5 — Register the grain dependency

`IChatSearchIndex` is a mandatory `AgentGrain` dependency, so a missed host fails **activation**, not
compilation. Register in:
- `src/Agent42.Backend/Program.cs` and `tools/Agent42.DevHost/Program.cs` (real hosts);
- `tests/Agent42.Tests/TestServiceRegistration.cs` — the canonical helper, **not** per configurator;
- `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` (`CapSizedFetcherSiloConfigurator`) — the
  documented exception that registers inline.

Do **not** touch `SharedClusterFixture.ResetSiloState()` (see Exemplars).

**Verify:** `dotnet test Agent42.slnx` — a missed host shows as activation failures across many tests.

### Step 6 — Grain write + purge

Inject `IChatSearchIndex` into `AgentGrain`. **The three sites have three different shapes — verify
each before editing:**

- `PersistTurnUnlessEphemeralAsync` — an **early return** (`if (_ephemeral) { log; return; }`), not an
  `if (!_ephemeral)` block. The index write goes **after** the existing `WriteStateAsync`, on the
  non-ephemeral path the early return already guarantees. Covers interactive chat and the agent-run drain.
- `ReceiveReminder` — has **no ephemeral guard at all**; it sets `_ephemeral = false` on entry because
  a fired reminder always persists. The write goes after its `WriteStateAsync`. Add no guard.
- `GenerateImageTurnAsync` — a real `if (!_ephemeral)` block; write inside it.

Each write is wrapped in its **own try/catch that logs and swallows**. `DeleteConversationAsync` goes
in `PurgeAsync` beside the existing `_documentIndex.DeleteAsync` call. Derive ids with the existing
`UserIdFromGrainKey` / `ConversationIdFromGrainKey` helpers.

**Delete cost — do not copy `CosmosDocumentIndex` blindly.** With `/userId` partitioning, deleting a
conversation is a filtered query plus a point-delete per message inside a **non-reentrant** grain that
`DELETE /api/conversations/{id}` awaits synchronously. Bound it: filter by `conversationId` within the
user's partition, delete with bounded concurrency, and keep it best-effort so a slow delete cannot
hang the endpoint.

**Verify:** new `tests/Agent42.Tests/ChatSearchIndexingTests.cs` — see AC4–AC7, AC14.

### Step 7 — Backfill

Add `ReindexAsync()` to `IAgentGrain` **with an `[Alias]`** (every method in that file has one).
It re-projects `GetHistoryAsync()` and upserts every turn by ordinal. Add
`POST /api/search/reindex`, which walks the caller's `ConversationIndexGrain.GetConversationsAsync()`
and calls it per conversation. **Per-user, Easy-Auth gated, never cross-user.** Bounded: a batch size
plus a per-call cap, returning a count and whether more remain — because each conversation costs a
grain activation whose `OnActivateAsync` does real network work (see the MCP gotcha in Context).

**Verify:** AC8, AC9, AC14.

### Step 8 — Search endpoint

`src/Agent42.Backend/Search/AgentApi.Search.cs` + `SearchContracts.cs`; `GET /api/search?q=`; userId
from `http.GetUserId()`, **never** from a query parameter. Resolve `IChatSearchIndex` via
**`IServiceProvider` + `GetService`** (the `/api/speech` pattern), so none of the thirteen files that
call `MapAgentApi` gain a mandatory dependency; return 409 when unresolved. Register
`MapSearchEndpoints` from `src/Agent42.Backend/Api/AgentApi.cs` — an unregistered part compiles
cleanly and silently deletes every endpoint in it. Update `ProjectLayoutTests`' "sixteen endpoint
groups" comment to seventeen.

**Verify:** AC10, AC11, AC3.

### Step 9 — Frontend

New `src/Agent42.Backend/wwwroot/js/search.js` exporting `wireSearch()`; a search `<input>` inside the
`.conversations` nav above `#conversationList` in `index.html`; styles in `styles.css`. Debounce input.
Import and call `wireSearch()` from `main.js`. Add `/js/search.js` to `SHELL` and bump `CACHE` to
`agent42-shell-v26`.

**The load-bearing rule:** `renderSidebar` is called from nine sites that fire independently of the
search box (`sidebar.js` ×5, `conversations.js` ×2, `agents.js`, `main.js`) — a background refresh,
cache hydration (ADR 0032), an agent save, or `send()`'s success path. So the search branch must live
**inside `renderSidebar`** (an early return to the hit-list render when a query is active), never as a
separate render path outside it, or any of those callers silently wipes an active result list. Do not
disturb the two-pass pinned/unpinned walk (ADR 0045) on the no-query path.

**Verify:** `ProjectLayoutTests` (module reachable from `main.js`; `SHELL` matches the directory;
named imports exported; `wireSearch` called from the composition root); `PwaAssetsTests` CACHE string
updated; AC12, AC16. Manual DevHost pass over CDP: type a query, see hits, click one, land in that
conversation; clear, list returns **with pins intact**; then trigger a background refresh while a
query is active and confirm the hits survive.

### Step 10 — Docs (`/sync-docs`)

CLAUDE.md repo-note bullet; gotchas entries for (a) the ordinal-must-come-from-one-projection trap,
(b) the best-effort-write rule and why it is not ADR 0008's rejected shape, and (c) the three
different `_ephemeral` shapes in `AgentGrain`. Extend `docs/features/conversation-history.md` rather
than adding a peer page. **Stale doc debt found during planning, fix in the same MR:** CLAUDE.md says
`ChatCoreAsync` is 39 cognitive (pre-ADR-0051 figure) and says Agents has 19 feature folders when
`ProjectLayoutTests` lists 20 (21 after this change). Move this spec to `.agentic/specs/completed/`.

**Verify:** CI doc-path integrity check; `/audit-docs` spot check.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A user can search their own message text and get hits with conversation id + snippet | `Search_ReturnsMatchingTurns` | `ChatSearchIndexTests.cs` |
| AC2 | Assistant replies are searchable, not just prompts | `Search_MatchesAssistantText` | `ChatSearchIndexTests.cs` |
| AC3 | A user can never see another user's messages | `Search_IsOwnerScoped` + endpoint test | `ChatSearchIndexTests.cs`, `SearchEndpointTests.cs` |
| AC4 | Incognito chats produce zero index documents | `EphemeralTurn_IsNotIndexed` | `ChatSearchIndexingTests.cs` |
| AC5 | A warm grain indexes normally on the turn after an ephemeral one | `Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne` | `ChatSearchIndexingTests.cs` |
| AC6 | Deleting a conversation removes its indexed messages | `PurgeAsync_DeletesIndexedMessages` | `ChatSearchIndexingTests.cs` |
| AC7 | A failing index write never fails the turn | `ThrowingIndex_DoesNotBreakTheReply` | `ChatSearchIndexingTests.cs` |
| AC8 | Backfill makes pre-existing conversations searchable | `Reindex_MakesExistingConversationsSearchable` | `SearchEndpointTests.cs` |
| AC9 | Backfill is idempotent (no duplicates on re-run) | `Reindex_IsIdempotent` | `SearchEndpointTests.cs` |
| AC14 | **Live-indexed then reindexed produces no duplicates** (the ordinal-divergence trap) | `Reindex_AfterLiveIndexing_ProducesNoDuplicates` | `ChatSearchIndexingTests.cs` |
| AC15 | Matching is case-insensitive | `Search_IsCaseInsensitive` | `ChatSearchIndexTests.cs` |
| AC10 | `/api/search` is Easy-Auth gated, not excluded | `SearchEndpoint_IsNotEasyAuthExcluded` | `EasyAuthExclusionTests.cs` |
| AC11 | The endpoint group is registered from the composition root | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` | `ProjectLayoutTests.cs` |
| AC12 | Frontend module wired, reachable, in SHELL, CACHE bumped | existing layout + PWA guards | `ProjectLayoutTests.cs`, `PwaAssetsTests.cs` |
| AC16 | **A `renderSidebar` call during an active query does not wipe the hits** | source-text assertion that the search branch is inside `renderSidebar` | `SearchAssetsTests.cs` |
| AC13 | Per-document text cap keeps items under the Cosmos ceiling | `IndexTurns_CapsOversizedText` | `ChatSearchIndexTests.cs` |

**Not provable by automated test**, to be recorded honestly in the ADR: the Cosmos `CONTAINS` / FTS
wire path is **prod-only** — DevHost and the emulator use the in-memory twin, exactly as
`CosmosDocumentIndex` already is. Verify by prod smoke after deploy. AC16 is a source-text guard, not
a runtime one (the ADR 0058 precedent for client rules with no runtime test).

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (b5dd615):

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura intersected with `git diff` vs merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time — `error` severity, so it **blocks** `dotnet build` |
| `vulnerable_packages` | **0** | CI package-freshness job (a `dependencies:` key, not a `gates:` one) |

Also required before the MR: `dotnet test Agent42.slnx` and
`dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- **BM25 ranking in v1.** The policy is created; the query stays `CONTAINS`.
- **Hybrid / semantic search** (Cosmos RRF over `FullTextScore` + `VectorDistance`) — it would
  re-import ADR 0030's embedding-model dependency and add a per-turn embedding round-trip, precisely
  the shape ADR 0008 rejected. Note it in the ADR as the upgrade path.
- **Multi-language and fuzzy search** — both preview, both need the portal enrollment this plan avoids.
- Cross-user or admin-wide search; searching attachments, generated images, tool payloads, system
  prompts or RAG excerpts (`GetHistoryAsync`'s projection already excludes the last three).
- Offline search over the ADR 0032 IndexedDB mirror.
- Any change to the grain-state container, its partitioning, or `SerializedSession`.

## Review

A `critic` subagent reviewed this plan at b5dd615 and returned **16 findings, 4 blocking**. All 16
were accepted and fixed; none were rejected. The four blocking ones were:

1. **Wrong host list and wrong shape for the new grain dependency.** The plan named 2 of at least 5
   silo configurators and told the executor to edit each, when `TestServiceRegistration.cs` is the
   canonical home and explicitly names `CapSizedFetcherSiloConfigurator` as the inline exception.
   Verified directly. Fixed in Affected areas and Step 5; Step 8 now also specifies the
   `IServiceProvider` + `GetService` pattern so the thirteen `MapAgentApi` hosts gain nothing mandatory.
2. **`ProjectLayoutTests.ExpectedFolders()` asserts the folder set by name**, so both new folders
   break the suite before any new test runs. Verified directly. Now in Affected areas and in the
   Verify of Steps 2 and 4.
3. **`ChatSearchHit.createdAt` had no source** — `ChatTurn` carries no timestamp, so the backfill
   could not reproduce what the live path invented. Field dropped.
4. **The ordinal design was left as an open question with no AC.** Now resolved in Context (both
   paths derive ordinals from one `GetHistoryAsync()` projection) and guarded by new AC14.

Also worth noting among the non-blocking fixes: the instruction to write "inside the existing
`if (!_ephemeral)` blocks" was **factually wrong at two of the three sites** — one is an early return,
one has no guard at all — making Step 6 unexecutable as written and inviting exactly the fresh-guard
mistake ADR 0034 warns against. Verified directly and rewritten site by site. The critic also
identified that `ChatCoreAsync`'s "39 cognitive" is the pre-ADR-0051 figure, converting an open
question into a one-line doc-debt item.

The critic explicitly passed these sections: the ADR 0008 distinction (not special pleading), security
and owner scoping (no `excludedPaths` or token path reaches the index), conversation-delete coverage
(`PurgeAsync` is the only path), ADR 0039/0004/0045/0054 citations, the ADR 0041 CACHE value, and the
quality-gate table.

## Divergences from the plan

All ten steps executed. Six things are worth recording; two of them changed the product.

### 1. The plan's incognito instruction was insufficient, and a test caught it

Step 6 said the ephemeral guard was satisfied by *where* the index write goes — after the early
return in `PersistTurnUnlessEphemeralAsync`, inside the real `if (!_ephemeral)` in
`GenerateImageTurnAsync`, unguarded in `ReceiveReminder`. Those three placements are right, and the
plan's site-by-site correction of them was accurate. **They are not sufficient.**

An incognito turn stays in the **in-memory session** — ADR 0034 guarantees "not persisted, not
listed", explicitly not "not in memory" — so it is still returned by `GetHistoryAsync()`, and the
**next normal turn's re-projection swept it straight into the index**. Skipping the ephemeral write
does not skip the ephemeral *content*.

Found by `Indexing_ResumesOnTheNormalTurnAfterAnEphemeralOne` failing on its first run, with the
incognito text indexed at ordinal 0 — which is exactly the load-bearing role the plan predicted for
AC5 ("asserts that nothing was written, which a broken indexing path also satisfies trivially"; the
resume test is the one that fails in both directions). Fixed by `SkipIndexingForEphemeralTurnAsync`,
which advances the index cursor past an ephemeral turn without indexing it, at both ephemeral
branches. Recorded as a gotcha, since nothing in the code makes it visible.

### 2. A 13th `AgentGrain` partial, not edits to `AgentGrain.cs`

Affected areas named only `AgentGrain.cs` and `AgentGrain.ImageTurn.cs`. The projection, the
backfill, the purge and the cursor logic went into a new `Search/AgentGrain.Search.cs` instead —
ADR 0039's established shape, given the feature now owns a folder. `AgentGrain.cs` keeps only the
field, the constructor wiring and the three call sites. `ProjectLayoutTests.AgentGrainParts()` and
its "twelve parts" docstring were updated to thirteen.

### 3. The grain dependency rides `AddDocumentRagFakes`, so no inline registration was needed

Step 5 said to register in `TestServiceRegistration.cs` *and* inline in
`CapSizedFetcherSiloConfigurator`. On inspection `AddDocumentRagFakes()` is the one helper called by
**every** silo configurator *and* every API host — including `CapSizedFetcherSiloConfigurator`
(which skips `AddWebToolFakes`, the documented exception). `IChatSearchIndex` needs both containers
anyway: it is a mandatory grain dependency *and* the search endpoint resolves it. Registering it
there makes both true everywhere at once, so the inline addition would have been redundant. A
comment in `CapSizedWebPageFetcher.cs` records why it is not there.

### 4. The FTS capability question is settled as far as a what-if can settle it

The plan flagged as **unproven** whether this serverless account needs an account-level capability
for FTS, as it needed `EnableNoSQLVectorSearch` for vector search. Measured, 2026-09-01:

- `az bicep build` compiles the template — the `2026-03-15` ARM schema accepts `fullTextPolicy` and
  `indexingPolicy.fullTextIndexes`.
- `az deployment group what-if` against `rg-agent42` (subscription **"De Heer MSDN"**, not the CLI's
  default one) succeeds and reports **exactly one `Create`**: the `chat-messages` container. The
  Cosmos account's own `Modify` delta contains **no `properties.capabilities` entry**, so no
  capability change is proposed.
- The live account currently holds exactly `EnableServerless` + `EnableNoSQLVectorSearch`.
- Microsoft's FTS doc names a prerequisite only for **multi-language** support (the portal's "New
  features for full-text search" enrollment), which `en-US` does not use.

**What that does and does not prove.** It proves the template is accepted and that container
creation is not gated behind a missing capability. It does **not** prove data-plane behaviour, which
a what-if never can. That residual is narrower than it sounds: **v1 queries with `CONTAINS`, not the
FTS functions**, so even a hypothetically unserved full-text index would not affect v1 — it would
only defer the BM25 upgrade.

### 5. The AC16 guard was mutation checked, not merely asserted

The DevHost browser leg **was** run (headless Chrome over CDP, DevHost at `:61326`), 7/7 checks:
the sidebar renders with a pinned row; typing renders hits; snippets contain the term; a
`renderSidebar()` during an active query keeps the hits; clicking a hit opens that conversation;
clearing returns the list; **pins survive** (the ADR 0045 two-pass walk intact).

Because AC16 is a source-text guard with no runtime test, the claim was **mutated** rather than
trusted: moving the render out of `renderSidebar` into `runSearch` (the exact mistake the rule
forbids) turns the AC16 check from `before=4 after=4` into **`before=8 after=0`**, and the
click-through check fails with it, while the other five still pass. The rule is load-bearing and now
demonstrably so. Files were restored and re-verified 7/7.

The Chrome-extension browser tooling was unavailable this session (an OAuth account mismatch), so
Chrome was driven directly over CDP from Node 22's built-in `WebSocket` — no new dependency.

### 6. Doc debt found and fixed, plus one the plan did not list

As planned: CLAUDE.md's `ChatCoreAsync` "39 cognitive" is the **pre-ADR-0051** figure quoted in the
present tense (rewritten to say so), and the Agents feature-folder count was stale. Rather than
replace one number with another that will drift again, **both** the Agents and Backend counts are
now stated as "the N directories `ProjectLayoutTests.ExpectedFolders()` pins", which names the
authority — the Backend number had drifted by two the same way, and neither list is purely
"feature" folders (`Skills/`, `Properties/`, `wwwroot/` ride along).

**Not in the plan:** `docs/features/conversation-history.md`'s Key files table still pointed at
`src/Agent42.Backend/wwwroot/app.js`, deleted by ADR 0041. Fixed in the same MR per hard rule 2.
`check-doc-paths.mjs` did not catch it.

**Flagged, not fixed:** `.agentic/gotchas.md` now holds **94** entries against its own "roughly 40"
guardrail — 91 of them pre-existing. That is an audit, not a side effect of this MR, and it belongs
to `/audit-docs`.

### Smaller notes

- `ChatSearchLimits` was added (not named in the plan) so the text cap, result ceiling, reindex
  batch size and delete concurrency are one shared source; `CapText` is called by both
  implementations so they cannot disagree about what was indexed.
- The projection **skips empty-text turns**, so an image-only turn does not put an unmatchable
  document in the corpus. This makes ordinal skips ordinary rather than exotic, which is precisely
  why the one-projection rule matters.
- `_indexedTurnCount` is activation-scoped and starts at zero, so the first indexed turn of an
  activation re-projects the whole conversation and later ones append. That costs one O(n) pass per
  activation (bounded by ADR 0004's 2 MB) and buys **self-healing** for writes dropped by the
  best-effort rule. Divergence 1 above deliberately gives that up after an ephemeral turn.
- `OtelConfigurationTests.AddLiveTelemetryCapture_ComposesWithTheDualExporterPipeline` failed once
  mid-execution and passed 3/3 in isolation plus on the final full run. This is the flake ADR 0057
  already **proved** pre-existing (2 of 5 on clean `main`); not absorbed here.

## Critic round (Step 5)

A `critic` subagent reviewed the full diff and returned 12 findings — 1 blocking, 4 high, 7
non-blocking. It passed ADR 0008 (the distinction is stated, never claimed as precedent), ADR 0039
(flat namespaces, no persisted type), ADR 0041 (CACHE/SHELL), ADR 0045 (two-pass walk), ADR
0004/0002/0054, the exemplar shapes, and owner scoping. **Ten findings were accepted and fixed; two
were accepted as documentation-only.** None were rejected.

### The one that was a real bug: `ReindexAsync` had no ephemeral awareness (F1)

`SkipIndexingForEphemeralTurnAsync` kept the *live* path off incognito turns by advancing a cursor —
but `ReindexAsync` re-projects **from ordinal 0** by design, so it wrote exactly the text the live
path had skipped, at the skipped ordinals. It also shifted subsequent ordinals, producing precisely
the orphaned duplicates the ADR claims are impossible by construction.

A cursor was the wrong shape. Replaced with **`_ephemeralOrdinals`**, an activation-scoped set that
*both* paths honour. **Mutation checked:** restoring the old behaviour (passing an empty set to
`ReindexAsync`) fails `Reindex_DoesNotResurrectAnEphemeralTurn`.

The residual this exposed is ADR 0034's, not this feature's, and is now recorded in the ADR's
Consequences: `PersistTurnUnlessEphemeralAsync` serializes the **whole session**, so a later normal
turn persists earlier ephemeral messages regardless of search. After a deactivation those turns are
indistinguishable from ordinary history. The shipped UI cannot produce such a conversation; the API
can.

### The tests that proved nothing (F2)

AC8, AC9 and AC14 all passed **with `ReindexAsync`'s write deleted** — AC8 because the live path had
already indexed the conversation, AC9/AC14 because they compared sizes a no-op also satisfies.

- `Reindex_MakesExistingConversationsSearchable` now **deletes the index first**, so only the
  backfill can restore the hits. **Mutation checked:** disabling the write fails it.
- `Reindex_AfterLiveIndexing_ProducesNoDuplicates` now compares the exact ordinal **set**, not the
  count.

### Also fixed

- **F3** — the image-generation write site (the only one with a genuine `if (!_ephemeral)` block) had
  no test on either arm. `ImageTurn_IndexesItsPromptWhenNormal_AndNotWhenEphemeral` covers both;
  `AgentGrain.ImageTurn.cs` is now 100%.
- **F4** — a single failed point-delete faulted the whole `Task.WhenAll`, the grain swallowed it, and
  `PurgeAsync` then cleared the conversation — leaving orphaned message documents that no path could
  ever delete (`PurgeAsync` is the only delete path; reindex only writes). Deleted conversation text
  would have stayed searchable forever. Each delete now swallows its own failure, and 404 is treated
  as the desired end state. The "losing a write only costs search results" argument does **not**
  extend to a lost delete, and the ADR now says so.
- **F6** — `/text` was both range-indexed and full-text-indexed. Excluded from range indexing, which
  is the same lesson the sibling `docChunksContainer` already records for `/embedding`. Worth
  catching before deploy: changing it later means recreating the container.
- **F7** — the backfill paged with `Skip(n)` over a list ordered pinned-then-recency, which reorders
  under any turn or pin mid-sweep, silently skipping conversations. Now ordered by conversation id.
- **F9** — the Cosmos query had no `ORDER BY`, so over the 50-hit cap production returned an
  arbitrary set while the in-memory twin returned a stable one. Ordering added to match the twin.
- **F10, F12** — two comments that asserted things that were not true (that the two `DocumentId`
  implementations produce identical strings; that the live path sends empty batches).
- **F11** — the endpoint returned each hit's full indexed text (up to 50 × 32 KB per debounced
  keystroke) and the browser discarded ~99% of it building a snippet. Snippeting moved server-side.
  Measured against DevHost afterwards: an assistant hit is 150 chars rather than the full reply.

### Documented rather than changed

- **F5** — the ADR said the live path "upserts the trailing turns". It does not: the cursor is
  activation-scoped and starts at zero, so the first indexed turn of each activation re-upserts the
  **whole** conversation. That is a deliberate trade (it buys self-healing) but it is real write
  amplification on a serverless account, and it appeared in neither the ADR's Consequences nor the
  CLAUDE.md bullet. Both now say it.
- **F8** — the best-effort argument leans on reindex being "user-invocable", but there is **no UI
  affordance anywhere in `wwwroot/`**; it is curl-only, and nothing backfills on deploy. The ADR now
  states this plainly instead of letting the phrase imply a control that does not exist. A
  Preferences affordance is the obvious follow-up and was outside this plan's scope.

Two ADR claims that the fixes themselves falsified were corrected: the Cosmos half of the
case-insensitivity contract is no longer "unreachable by test" (`BuildSearchSql` is pure and
asserted), and "user-invocable" is now qualified.
