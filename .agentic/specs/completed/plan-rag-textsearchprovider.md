# Plan: swap document RAG from an agentic tool to an always-on TextSearchProvider

`planned-against: main @ 05af23b`

## Overview
Adopt Microsoft Agent Framework's first-class `TextSearchProvider` (an `AIContextProvider`) to
perform document retrieval **automatically before every model invocation** (`BeforeAIInvoke`),
replacing the hand-rolled agentic `search_documents` tool. Goal: align our RAG wiring with the
Agent Framework `AgentWithRAG_Step02` sample's context-provider approach, get vague-followup
handling for free (`RecentMessageMemoryLimit`), and delete a tool we now get from the framework.

Why now: a comparison against the AF sample + the SK Cosmos NoSQL connector (the request that
spawned this plan) showed our storage layer is sound but our retrieval *mechanism* was a manual
reimplementation of what 1.13 ships as `TextSearchProvider`. **Verified: `TextSearchProvider`
exists in the pinned `Microsoft.Agents.AI` 1.13.0** (`.../microsoft.agents.ai/1.13.0/lib/net10.0/Microsoft.Agents.AI.xml`,
type `Microsoft.Agents.AI.TextSearchProvider`, ctor takes a plain
`Func<string, CancellationToken, Task<IEnumerable<TextSearchProvider.TextSearchResult>>>` +
`TextSearchProviderOptions`) — no new package or version bump.

This reverses one ADR 0007 decision (it rejected "always-on AIContextProvider auto-injection" in
favour of a tool). Step 1 records that supersession via `/decide`. The storage/ingest/scoping
design (ingest-only, owner-scoped Cosmos vector container, no create-on-use) is **unchanged**.

## Scope decisions (from the planning interview)
- **Option 2 (migrate storage to `Microsoft.Extensions.VectorData`) is deferred** to its own plan —
  see `.agentic/specs/plan-rag-vectordata-abstractions.md`. Reason: `TextSearchProvider` needs no
  `VectorStoreCollection` (its ctor takes a plain delegate), and a faithful VectorData migration
  would mean hand-writing two custom connectors — only `Microsoft.Extensions.VectorData.Abstractions`
  is stable (10.1.0); both concrete connectors (`Connectors.CosmosNoSql`, `Connectors.InMemory`) are
  preview-only (`1.67.0-preview`), which violates hard rule 4.
- **`BeforeAIInvoke`, not `OnDemandFunctionCalling`.** The user asked for always-on retrieval.
  (`OnDemandFunctionCalling` would just re-create today's tool via the framework — not the goal.)
- **Keep `DocumentNames` / `RegisterDocumentAsync` awareness.** Retrieval becomes automatic, but the
  filename list still answers meta questions ("what did I upload?") that vector search answers poorly,
  and keeping it avoids a grain-state migration. Only its wording changes (drop "call the tool").
- **Gate the provider on `DocumentNames.Count > 0` (rebuts ADR 0007's rejection reason).** The
  `TextSearchProvider` is attached to `AIContextProviders` **only when the conversation has at least one
  ingested document**. `BuildAgent` is already rebuilt on `RegisterDocumentAsync`
  (`AgentGrain.cs:200-204`), so the wiring is in place. This is what makes the always-on switch safe:
  ADR 0007 rejected always-on injection because it "queries the index every turn" (an Azure embedding
  round-trip + possible irrelevant context) — gating confines that to conversations that actually have
  documents, so **document-free chats make zero embedding calls and are entirely unaffected**, and a
  broken embedding deployment can only degrade document conversations, not every chat. Within a
  document conversation, retrieval does run each turn — the deliberate, accepted cost of the always-on
  behavior the user chose (option 4).

## Affected areas (verified against the tree)
| File | Change |
|------|--------|
| `src/Agent42.Agents/AgentGrain.cs` | Core: build `TextSearchProvider`, add to `AIContextProviders`, remove `search_documents` tool + its `Tools` entry, repurpose `SearchDocumentsAsync` as the provider callback, reword `Instructions` + `WithDocumentAwareness`. |
| `tests/Agent42.Tests/FakeChatClient.cs` | Remove the `search_documents` tool-call branch; add a context-echo so a test can assert injected retrieval reached the model. |
| `tests/Agent42.Tests/AgentGrainTests.cs` | Rewrite the AC5 tool test as an injection test; add a session-persistence test; keep awareness + purge tests. |
| `.agentic/decisions/0008-*.md` (new) | `/decide` supersession of ADR 0007's retrieval-mechanism decision. |
| `src/Agent42.Backend/AgentApi.cs` | Comment reword (l.132-134 "calls search_documents"). |
| `src/Agent42.Backend/DocumentIngestionService.cs` | Comment reword ("only via the search_documents tool"). |
| `src/Agent42.Backend/wwwroot/app.js` | Comment reword (l.52). No code change — tool-call chips are generic. |
| `src/Agent42.Agents/AgentGrainState.cs` | Comment reword (l.59, `DocumentNames` XML doc names the tool). |
| `src/Agent42.Agents/IAgentGrain.cs` | Comment reword (l.48, `RegisterDocumentAsync` XML doc names the tool). |
| `tests/Agent42.Tests/TestServiceRegistration.cs` | Comment reword (l.11 names the tool). |
| `CLAUDE.md` | Repo-note bullet: "agentic `search_documents` grain tool" → always-on `TextSearchProvider`. |

**Full `search_documents` blast radius (from `grep -ri search_documents src/ tests/ CLAUDE.md`):**
the files above are every product/test reference. `.agentic/gotchas.md` does **not** contain the
literal `search_documents` (verified: `grep -c` = 0) — the two RAG gotchas describe the Cosmos schema
and embedding prerequisite, which stay valid, so **no gotcha edit is required** by this change (a new
`/gotcha` is optional after Step 1 if the persistence knob turns out non-obvious).
`.agentic/specs/completed/plan-rag-cosmos-document-qa.md` and ADR 0007 keep their historical wording;
ADR 0007's *decision* is amended by ADR 0008 (Step 2), not by editing 0007's prose beyond a
superseded-by note.

Not changed: `IDocumentIndex` + both impls, `DocumentIngestionService` logic, extractor/chunker,
`AgentGrainState.DocumentNames` (state shape) and `IAgentGrain.RegisterDocumentAsync` (behavior) —
only their XML-doc comments change, `CosmosDocumentIndex`, `infra/**`, the embedding registration in
`Program.cs`.

## Context & constraints (what the executor can't cheaply re-derive)
- **`TextSearchProvider` API shape (verified, 1.13.0):** ctor
  `(Func<string,CancellationToken,Task<IEnumerable<TextSearchProvider.TextSearchResult>>> searchCallback, TextSearchProviderOptions options, ILoggerFactory loggerFactory)`.
  `TextSearchResult` has `SourceName`, `SourceLink`, `Text`, `RawRepresentation`.
  `TextSearchProviderOptions` has `SearchTime` (`TextSearchBehavior.BeforeAIInvoke` |
  `OnDemandFunctionCalling`), `RecentMessageMemoryLimit`, `CitationsPrompt`, `ContextPrompt`,
  `ContextFormatter`, and `StorageInputRequestMessageFilter`/`StorageInputResponseMessageFilter`.
  Evidence: `Z:\Packages\.nuget\packages\microsoft.agents.ai\1.13.0\lib\net10.0\Microsoft.Agents.AI.xml`
  lines ~9463-9717.
- **Serialization/2MB risk (the reason for the spike, Step 0).** Our grain serializes the whole
  `AgentSession` to one Cosmos `agent-state` item every turn, capped at 2 MB (ADR 0004; gotcha
  "attachment bytes can never be inlined"). `BeforeAIInvoke` "injects results as additional messages"
  (per the type's XML doc). If those injected chunk-text messages are persisted into the serialized
  session, the session grows every turn and can approach the cap + re-inject stale context. The AF
  sample defends against this with a `ChatHistoryProvider` filter excluding
  `AgentRequestMessageSourceType.AIContextProvider` messages from storage. Step 0 determines whether
  1.13 already excludes them, or whether we must set a filter — and which knob
  (`TextSearchProviderOptions.Storage*Filter` vs a `ChatHistoryProvider`).
- **Offline-testable, unlike the Cosmos path.** `FakeChatClient` already echoes what the model
  receives (`InstructionsEcho`/`ImageEcho`, `tests/Agent42.Tests/FakeChatClient.cs`). Extending it to
  echo injected `AIContextProvider` message text lets us assert retrieval-reached-the-model in the
  existing Orleans TestCluster with `InMemoryDocumentIndex` + `DeterministicEmbeddingGenerator` — no
  Azure. This is the exemplar test harness (gotcha exemplar: `AgentGrainTests.cs`).
- **The existing seam is clean:** `AgentGrain.SearchDocumentsAsync` (`AgentGrain.cs:402-413`) already
  does embed-query → `_documentIndex.SearchAsync(grainKey, vec, k)` → `DocumentChunk`. The provider
  callback is that body plus a `DocumentChunk → TextSearchResult` map. `_embeddings` and
  `_documentIndex` are already injected.
- **Frontend needs no code change:** `app.js` renders tool-call chips generically off
  `toolCallStart`/`toolCallEnd` stream events (`app.js:741-744`); removing the tool simply means
  retrieval no longer shows a chip (acceptable — retrieval is now silent/automatic). Only a stale
  comment at `app.js:52` changes.
- **Governing docs:** ADR 0007 (`.agentic/decisions/0007-document-rag-cosmos-vector-search.md`,
  esp. "Alternatives considered → Always-on AIContextProvider ... Rejected"); gotchas "Cosmos vector
  search is prod-only" and "Document RAG embedding deployment"; ADR 0004 (2 MB session cap).

## Steps
1. **Spike — `BeforeAIInvoke` injection mechanics + session-persistence (blocks Steps 2-4).** Use
   `/spike-dotnet`: a throwaway `dotnet run app.cs` pinned to `Microsoft.Agents.AI` 1.13.0 +
   `Microsoft.Extensions.AI`. Build a `ChatClientAgent` with a `TextSearchProvider(BeforeAIInvoke)`
   whose callback returns one fixed, **uniquely-tokened** `TextSearchResult` (e.g. `Text` contains a
   sentinel like `ZZQ_MARKER`), plus a trivial fake `IChatClient` that records the messages it
   receives. Run 3 turns. Capture and print, per turn:
   - **(a) Did the sentinel reach the fake client**, and on **which message** — its `ChatRole` and its
     `AgentRequestMessageSourceType` (via `GetAgentRequestMessageSourceType()`). This is the datum
     Step 4's `ContextEcho` predicate depends on — do **not** assume "non-user/non-system".
   - **(b) Persistence:** after each turn `SerializeSessionAsync` and test whether the **sentinel
     string reappears in the serialized blob** (substring check — do **not** use raw byte-length,
     which is confounded: `TextSearchProvider` legitimately fills `RecentMessagesText` up to
     `RecentMessageMemoryLimit`, so the session grows regardless). Run this measurement with
     `RecentMessageMemoryLimit = 0` to isolate injected-result persistence from the recent-message
     buffer.
   *Decide:* if the sentinel is persisted into the serialized session, find the knob that excludes it
   (`TextSearchProviderOptions.StorageInputRequestMessageFilter`/`StorageInputResponseMessageFilter`,
   or a `ChatHistoryProvider` filter on `AgentRequestMessageSourceType.AIContextProvider` like the AF
   sample) and confirm it stops persistence while retrieval still reaches the client. Note: the
   options' `StorageInput*MessageFilter` default is documented as "external messages only", which is
   reassuring but governs the *recent-message* memory, not necessarily the agent's main chat-history
   persistence — so this must be measured, not assumed. **Verify:** spike prints, per turn,
   `reached=<bool> role=<r> source=<s> persisted=<bool>` and a chosen-knob line. Record all four in
   this spec's Review section and carry `role`/`source` into Step 4 and the knob into Step 3.
2. **`/decide` — ADR 0008 supersedes ADR 0007's retrieval mechanism.** Records: always-on
   `TextSearchProvider` (`BeforeAIInvoke`) replaces the agentic `search_documents` tool. Rationale
   **must explicitly confront ADR 0007's rejection reason** ("queries the index every turn and can
   inject irrelevant context"), not just list upsides: the reversal is justified because 1.13 ships
   `TextSearchProvider` natively, `RecentMessageMemoryLimit` handles vague followups a tool misses, and
   crucially the provider is **gated on `DocumentNames.Count > 0`** — so the per-turn embedding round-trip
   ADR 0007 feared happens **only in conversations that have documents**, never in plain chats; the
   remaining per-turn cost inside a document conversation is the accepted price of automatic retrieval.
   Scope: amends **only** the retrieval mechanism — ingest-only, owner-scoped Cosmos vector store, and
   no-create-on-use are unchanged. Include the 2 MB-safety knob chosen in Step 1. Link to ADR 0007 +
   0004. Files: `.agentic/decisions/0008-*.md`. **Verify:** `/decide` conflict check passes; ADR 0007
   gets a "Superseded-by (partial): 0008" note on its retrieval decision.
3. **Core: wire `TextSearchProvider` into the grain.** In `AgentGrain.cs`:
   - Repurpose `SearchDocumentsAsync` into the provider's search callback (the `searchAsync` ctor
     param): `query → _embeddings.GenerateAsync([query]) → _documentIndex.SearchAsync(GetPrimaryKeyString(), vec, 5)`,
     mapping each `DocumentChunk c` → `new TextSearchProvider.TextSearchResult { Text = c.Text,
     SourceName = c.FileName + (c.Page is int p ? $", p.{p}" : $", chunk {c.ChunkIndex}"),
     RawRepresentation = c }`.
   - Construct `TextSearchProvider` with `TextSearchProviderOptions { SearchTime = BeforeAIInvoke,
     RecentMessageMemoryLimit = 5, <storage filter from Step 1 if needed>, CitationsPrompt = "…cite the
     source file and page/chunk shown with each excerpt." }`. Pass an `ILoggerFactory` if required by
     the ctor (inject `ILoggerFactory` into the grain, or pass the null-object).
   - **Gate the provider on documents (resolves the HIGH review finding):** attach `docSearchProvider`
     to `AIContextProviders` **only when `_state.State.DocumentNames.Count > 0`** — otherwise
     `AIContextProviders = [skillsProvider]` exactly as today. So:
     `AIContextProviders = _state.State.DocumentNames.Count > 0 ? [skillsProvider, docSearchProvider] : [skillsProvider]`.
     Because `BuildAgent` is already rebuilt on `RegisterDocumentAsync` (`AgentGrain.cs:200-204`), a
     conversation gains the provider the moment its first document is ingested and never before.
   - Remove the `searchDocuments` `AIFunctionFactory.Create(...)` and its entry in `ChatOptions.Tools`
     (leave `schedule*`/`get_date`).
   - Reword `Instructions` (drop "call the search_documents tool"; say excerpts from uploaded
     documents are provided automatically — ground answers in them and cite file + page/chunk) and
     `WithDocumentAwareness` (drop "call the search_documents tool"; keep the filename list).
   Files: `src/Agent42.Agents/AgentGrain.cs`. **Verify:** `dotnet build Agent42.slnx`.
4. **Tests.** Files: `tests/Agent42.Tests/FakeChatClient.cs`, `tests/Agent42.Tests/AgentGrainTests.cs`.
   - `FakeChatClient`: delete `BuildSearchDocumentsCall` + the `text.Contains("document")` branch in
     `SelectToolCall`; add a `ContextEcho(messages)` that surfaces the text of the injected retrieval
     message into the reply, e.g. `[ctx:<text>]`. **Its predicate is the Step 1 finding** (the injected
     message's `ChatRole` + `AgentRequestMessageSourceType`), not a guess — filter on
     `GetAgentRequestMessageSourceType() == AIContextProvider` if that's what the spike observed. Also
     re-check `ImageEcho`'s `LastOrDefault(User)` (`FakeChatClient.cs:83-84`): if the spike shows
     injection lands as a trailing `User` message it could shadow the real user turn — but the
     `DocumentNames` gate means the image-attachment tests (no documents) never attach the provider, so
     they stay green; confirm against the spike role finding.
   - Rewrite `AgentChat_UsesDocumentSearch_WithCitations` → `AgentChat_InjectsDocumentContext_BeforeInvocation`:
     seed the "Magrathea" chunk (via `RegisterDocumentAsync` **so the provider is attached** + `SeedChunkAsync`),
     chat "what does the document say about magrathea", assert the reply echo contains the chunk text +
     its `SourceName` citation, and assert **no** `ToolCallStart` for `search_documents` is emitted.
   - Add `AgentChat_NoDocuments_NoRetrieval` (locks in the gating fix): a conversation with no
     registered documents replies normally with **no** `[ctx:…]` echo — proving the provider isn't
     attached and no embedding call happens for document-free chats.
   - Add `AgentChat_DoesNotPersistInjectedResultText`: register a doc + seed a chunk, run 3 turns,
     assert the serialized session / history does **not** accumulate the injected **chunk/result text**
     turn-over-turn (scoped to result text — the `RecentMessagesText` buffer is legitimately persisted;
     locks in the Step 1 finding).
   - Keep `RegisterDocument_MakesTheModelAwareOfIt` (reword expected instruction text only if changed)
     and `Purge_DeletesIndexedDocumentChunks` (unchanged).
   **Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~AgentGrainTests"`.
5. **Doc sweep (same MR — hard rule 2).** Reword every `search_documents`-naming comment the grep
   surfaced: `AgentApi.cs` (l.132-134), `DocumentIngestionService.cs`, `app.js` (l.52),
   **`AgentGrainState.cs` (l.59), `IAgentGrain.cs` (l.48), `TestServiceRegistration.cs` (l.11)**; update
   the `CLAUDE.md` repo-note bullet. **Do not** hunt for gotchas entries naming the tool — there are
   none (`grep -c search_documents .agentic/gotchas.md` = 0). Confirm `tests/Agent42.Tests/AgentApiTests.cs`
   has no `search_documents` assertion left dangling.
   **Verify:** `grep -ri "search_documents" src/ tests/ CLAUDE.md` returns only historical references
   (completed spec / ADR 0007 prose); `dotnet test Agent42.slnx`; `dotnet format Agent42.slnx --verify-no-changes`.

Steps 3-5 are sequential (all touch the grain / its tests / its docs); Steps 1 and 2 precede them.

## Acceptance criteria → test mapping
| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A question about an uploaded doc is answered from auto-injected context, with **no** `search_documents` tool call | `AgentChat_InjectsDocumentContext_BeforeInvocation` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | Injected excerpts carry citation metadata (file + page/chunk) that reaches the model | same test asserts `SourceName` in the delivered context | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC3 | A document-free conversation triggers **no** retrieval/embedding call and replies normally (provider gated on `DocumentNames.Count > 0`) | `AgentChat_NoDocuments_NoRetrieval` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | Injected **chunk/result text** is **not** persisted into the serialized session across turns (2 MB safety; `RecentMessagesText` buffer is exempt by design) | `AgentChat_DoesNotPersistInjectedResultText` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC5 | Model stays aware of uploaded filenames | `RegisterDocument_MakesTheModelAwareOfIt` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC6 | Purge still deletes indexed chunks | `Purge_DeletesIndexedDocumentChunks` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC7 | `search_documents` tool fully removed; no orphan refs in product code | build + `grep` gate (Step 5) | — |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 05af23b)
- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block)
- Branch: `fix/*` or `feature/*` + MR; never push to `main` (hook-enforced).
- CI hard blocks: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope
- Option 2 — the `Microsoft.Extensions.VectorData` storage migration (its own deferred spec).
- `OnDemandFunctionCalling` mode / keeping any tool-based retrieval.
- Hybrid/keyword search, re-ranking, cross-conversation library (already out of scope in ADR 0007).
- Removing `DocumentNames`/awareness, or any change to ingestion, extraction, chunking, the Cosmos
  schema, Bicep, or the embedding config.

## Review
A fresh-context `critic` reviewed this spec. It verified every `TextSearchProvider` API claim against
the 1.13.0 package XML (clean) and confirmed `planned-against: main @ 05af23b` is main's tip. Findings
and how each was handled:

- **HIGH — always-on retrieval regresses cost/latency/reliability for every turn; supersession didn't
  rebut ADR 0007's actual rejection reason.** *Accepted & fixed.* Added the **gate on
  `DocumentNames.Count > 0`** (Scope decisions + Step 3): the provider is attached only in
  document-bearing conversations, so document-free chats make zero embedding calls and a broken
  embedding deployment can't break every turn. Step 2's `/decide` rationale now must explicitly confront
  the original rejection. New AC3 locks the gating behavior in.
- **MEDIUM — three more files name `search_documents` (`AgentGrainState.cs:59`, `IAgentGrain.cs:48`,
  `TestServiceRegistration.cs:11`), omitted from the sweep and mislabeled "Not changed".** *Accepted &
  fixed.* Added all three to the affected-areas table and Step 5; corrected the "Not changed" note.
- **MEDIUM — plan invented "two gotchas entries that name `search_documents`" (there are none).**
  *Accepted & fixed.* Removed the phantom instruction; documented `grep -c = 0` and that the RAG
  gotchas stay valid.
- **MEDIUM — Step 4 `ContextEcho` assumed the injected message's role ("non-user/non-system") without
  verification.** *Accepted & fixed.* Step 1 now captures the injected message's `ChatRole` +
  `AgentRequestMessageSourceType`; Step 4 derives the predicate from that finding, and re-checks the
  `ImageEcho` shadowing risk (neutralized by the gate).
- **MEDIUM — Step 1's byte-length persistence metric is confounded by `RecentMessageMemoryLimit`.**
  *Accepted & fixed.* The spike now uses a unique sentinel substring check (not byte growth) and runs
  the persistence measurement with `RecentMessageMemoryLimit = 0`; AC4 wording scoped to result text.
- **LOW nits** (param is `searchAsync` not `searchCallback`; AC-numbering vs the prior plan; AC4 wording
  breadth). *Accepted & fixed* in the edits above.

No findings were rejected.

## Step 1 spike results (appended by executor)

Ran a throwaway `dotnet run` app pinned to `Microsoft.Agents.AI@1.13.0` + `Microsoft.Extensions.AI@10.6.0`
(the version `Microsoft.Agents.AI` 1.13.0 depends on). A `ChatClientAgent` over a recording fake
`IChatClient`, a `TextSearchProvider(BeforeAIInvoke)` whose callback returns one fixed result whose
`Text` contains the sentinel `ZZQ_MARKER`. 3 turns per config; persistence measured via
`(await agent.SerializeSessionAsync(session)).GetRawText()` substring check with
`RecentMessageMemoryLimit = 0` (and re-confirmed at `= 5`).

**Injection datum (feeds Step 4 predicate):** the retrieved result reaches the model as a **`ChatRole.User`**
message tagged **`AgentRequestMessageSourceType.AIContextProvider`**, read via
`message.AdditionalProperties[AgentRequestMessageSourceAttribution.AdditionalPropertiesKey]` →
`AgentRequestMessageSourceAttribution.SourceType.Value == "AIContextProvider"`. It is appended **after** the
real user turn (so it is the *last* `User` message delivered — see the `ImageEcho` note below).

**BLOCKER found (the spike's main deliverable — bigger than the anticipated 2 MB risk):** with the default
chat-history provider, attaching the `TextSearchProvider` makes the injected `AIContextProvider` message get
**stored into the session's chat history**, and it carries the `AgentRequestMessageSourceAttribution` struct in
`AdditionalProperties`. The grain's existing per-turn `SerializeSessionAsync` then **throws
`NotSupportedException`** ("JsonTypeInfo metadata for type `Microsoft.Agents.AI.AgentRequestMessageSourceAttribution`
was not provided by [the source-gen] TypeInfoResolver … Path: `$.Messages.AdditionalProperties`"). This would break
**every turn's `_state.WriteStateAsync()`** in a document conversation, not merely grow the session. It also
re-loads the stored injected message as `source=ChatHistory` on later turns and re-injects it (the stale-context
+ growth concern the plan raised).

**Chosen knob (verified fix):** set `ChatClientAgentOptions.ChatHistoryProvider` to an
`InMemoryChatHistoryProvider` whose `InMemoryChatHistoryProviderOptions.StorageInputRequestMessageFilter`
**excludes messages sourced from `AIContextProvider` and `ChatHistory`** (keeps only `External`). Its `StateKey`
defaults to `"InMemoryChatHistoryProvider"` — identical to the implicit default provider — so **existing serialized
sessions round-trip with no state migration**. This is *not* `TextSearchProviderOptions.StorageInput*Filter` (those
govern the provider's own recent-message memory, exactly as the plan's caveat warned). With the filter:

| config | reached | role / source | `SerializeSessionAsync` | injected text persisted |
|--------|---------|---------------|-------------------------|-------------------------|
| baseline (no provider) | false | – | **OK** | no |
| provider + **default** history | true | User / AIContextProvider | **THREW `NotSupportedException`** | – (crash) |
| provider + **filtered** history | true | User / AIContextProvider | **OK** (228→373→518 b) | **no** |
| provider(`RecentMessageMemoryLimit=5`) + filtered history (**Step-3 config**) | true | User / AIContextProvider | **OK** (279→453→627 b) | **no** |

Filtered-config stored history contained only the real `User` + `Assistant` messages (no `AIContextProvider`
message), and session size grew at the same per-turn slope as baseline — i.e. no accumulation of injected result
text. `RecentMessageMemoryLimit = 5` serialized cleanly (its `RecentMessagesText` buffer holds plain user-query
strings, filtered to `External` by the provider's own default store filter, and never contained the sentinel).

**Carried into implementation:**
- **Step 3:** in addition to constructing/gating the `TextSearchProvider`, set
  `ChatClientAgentOptions.ChatHistoryProvider = new InMemoryChatHistoryProvider(new
  InMemoryChatHistoryProviderOptions { StorageInputRequestMessageFilter = <keep non-AIContextProvider,
  non-ChatHistory> })`. Safe to set **unconditionally** (document-free chats have no `AIContextProvider`
  messages, so the filter is a no-op and matches the default `StateKey`).
- **Step 4:** `ContextEcho` predicate = message whose `AgentRequestMessageSourceAttribution.SourceType.Value`
  is `"AIContextProvider"` (not "non-user/non-system" — the message *is* `ChatRole.User`). `ImageEcho`'s
  `LastOrDefault(User)` would otherwise pick the injected message, but the `DocumentNames` gate means
  image tests never attach the provider, so they stay green. Add a `DoesNotPersistInjectedResultText` test
  (AC4) — the filter is the thing it locks in.

## Implementation divergences (executor)

The plan was implemented as written except where the Step 1 spike changed the facts. All 97 tests pass;
`dotnet build`, `dotnet format --verify-no-changes`, and the `grep -ri search_documents src/ tests/ CLAUDE.md`
gate are clean. Divergences:

1. **The 2 MB "risk" was actually a hard crash (biggest change).** The plan's Context framed injected-result
   persistence as a *possible* growth issue needing a *possible* filter. The spike found it is **mandatory**:
   with the default history provider the injected `AIContextProvider` message is stored and carries an
   `AgentRequestMessageSourceAttribution` struct the default serializer can't handle, so `SerializeSessionAsync`
   **throws `NotSupportedException` on every turn** of a document conversation. Fix shipped in Step 3.
2. **The knob is a `ChatHistoryProvider` filter, not a `TextSearchProviderOptions` filter.** The plan listed
   `TextSearchProviderOptions.StorageInput*Filter` as the first candidate; the spike confirmed those govern the
   provider's *recent-message memory* (exactly the plan's own caveat), and the fix is
   `InMemoryChatHistoryProviderOptions.StorageInputRequestMessageFilter` excluding `AIContextProvider` +
   `ChatHistory` messages, wired via `ChatClientAgentOptions.ChatHistoryProvider` in `BuildAgent`
   (set unconditionally; same `StateKey` as the implicit default → no state migration).
3. **`TextSearchProvider` ctor loggerFactory** is passed `null` (the ctor accepts it); no `ILoggerFactory`
   was injected into the grain.
4. **AC4 is asserted at the history level, not the raw blob.** The grain doesn't expose its serialized blob,
   so `AgentChat_DoesNotPersistInjectedResultText` runs 3 turns + a deactivate/reactivate round-trip and
   asserts exactly the 3 real user turns survive with no injected context text — which also proves the
   serialization fix (without the filter the loop crashes on turn 1) and the deserialize-with-provider path.
5. **Test rename** `AgentChat_UsesDocumentSearch_WithCitations` → `AgentChat_InjectsDocumentContext_BeforeInvocation`
   (per Step 4).
6. **Housekeeping:** added `TestResults/` to `.gitignore` (coverage-collection artifact; was untracked).
7. **Critic doc-debt fix:** reworded a stale "retrieval tool" comment on `SeedChunkAsync`
   (`AgentGrainTests.cs`) that the literal-`search_documents` sweep missed. LOW-2 (an optional `/gotcha` for
   the serialization trap) was **not** taken — it is already captured by the inline comment in `BuildAgent`,
   ADR 0008's Consequences, and a test that hard-fails if the filter is removed (the gotchas file's own rule:
   "if a test can express it, it does not belong here").
