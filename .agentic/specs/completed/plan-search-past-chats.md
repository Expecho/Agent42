# Plan: `search_past_chats` + `read_past_chat` — recall across conversations as a selectable model tool

planned-against: main @ 41f0a41

## Overview

Chat search (ADR 0061) lets the **user** search every message they ever exchanged, through the sidebar.
The **model** cannot: a conversation knows only its own history, Hindsight memory (ADR 0056/0057) recalls
extracted facts rather than what was said, and RAG is limited to one conversation's documents. This plan
gives the model the same corpus. Two new built-in tools sit behind one new selectable key, `past_chats`.
`search_past_chats` returns relevance-ranked snippets with each conversation's title and date.
`read_past_chat` returns a bounded window of turns around a hit. Both read the **existing** `chat-messages`
projection, with no new container, no backfill and no new grain state. The search side adds a
**ranked any-term query** (BM25 via `FullTextContainsAny` + `ORDER BY RANK FullTextScore`). ADR 0061
declared the full-text policy at container creation for exactly this switch. The sidebar search is
unchanged.

Why now: the 21 Oct 2026 comparison with Hermes Agent ("FTS5 search across past sessions") names this
as the cheapest borrowable idea. The projection, the owner scoping and the incognito exclusion already
exist, so the feature is mostly a tool plus two port methods.

## Affected areas (verified against the tree @ 41f0a41)

| Area | Files |
|------|-------|
| Index port + twins | `src/Agent42.Agents/Search/IChatSearchIndex.cs` (two new methods), `src/Agent42.Agents/Search/InMemoryChatSearchIndex.cs`, `src/Agent42.Backend/Search/CosmosChatSearchIndex.cs` (new pure SQL builders beside `BuildSearchSql` ~:144), test wrapper `tests/Agent42.Tests/TestChatSearchIndex.cs` (it wraps the in-memory twin and injects per-user failures; it must forward the new methods and honour the failure switch) |
| Query terms + limits | new `src/Agent42.Agents/Search/PastChatQuery.cs` (term extraction, pure), new `src/Agent42.Agents/Search/PastChatLimits.cs` (budgets, header prefixes, stubs). Reuse `ChatSearchLimits.SnippetAround` / `CapText` (`src/Agent42.Agents/Search/ChatSearchLimits.cs`) |
| Tools (grain partial) | new `src/Agent42.Agents/Search/AgentGrain.PastChatTools.cs`. Shape copied from `src/Agent42.Agents/Documents/AgentGrain.ReadDocumentTool.cs`: factory method, guard/catch shell, TurnContext budget, compaction branch |
| Per-run budget | `src/Agent42.Agents/Conversations/TurnContext.cs` (new counter + `Record…`, the `RecordDocumentPartRead` ~:151 / `DocumentPartsRead` ~:61 shape) |
| Catalog / registration | `src/Agent42.Agents/Tools/ToolCatalog.cs` (const, `AllSelectableToolKeys` ~:217, `ToolNameToKey` ~:231, `Descriptors` ~:259), `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (unconditional list ~:307-322, add `.. PastChatTools()`) |
| Compaction | `src/Agent42.Agents/Artifacts/AgentGrain.ArtifactTools.cs` `CompactToolCall` (~:302): add a branch for both new tool names. The persist call sites already route through it (ADR 0098 added `read_document` there) |
| `ask_agent` bind filter (ADR 0070) | `src/Agent42.Backend/Mcp/AgentMcpRunner.cs`: `WithoutMemoryOrPush` (~:227, called ~:66, doc comment ~:196-225) must also drop `past_chats`. Rename it (e.g. `WithoutOwnerScopedTools`) and extend the "Why these … and nothing else" paragraph. Test beside `AskAgent_MakesNoMemoryCalls` in `tests/Agent42.Tests/AgentMcpServerTests.cs` (~:275) |
| Copilot | `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` `CopilotFunctions` (~:108). **No code change expected**: new built-ins flow through `SelectableTools`. Pin it with a test |
| Infra | **None.** `infra/modules/cosmos.bicep` already declares `fullTextPolicy` + `fullTextIndexes` on `/text` (~:184, ~:223). The new queries add no multi-property `ORDER BY`, so no composite index is needed (see Step 3) |
| Frontend | **None expected.** The picker is data-driven from `/api/tools` (`wwwroot/js/tools.js:28`, `:119`). If the executor finds a tool-name→label map and touches `wwwroot/js/`, bump `CACHE` in `wwwroot/sw.js` (rule 3) |
| Docs | new ADR `.agentic/decisions/0112-past-chats-as-a-model-tool.md`; amended-by notes in `0070-expose-stored-agents-as-an-mcp-server.md` (the bind-time exclusion now covers `past_chats`), `0061-searchable-chat-message-projection.md` (BM25 now used by the tool path; correct the "GA, no enrollment" claim if Step 0 shows otherwise) and `0096-*.md` (~:81, the "point email at an agent whose EnabledTools excludes memory and the fetch tools" mitigation must now also name `past_chats`); `docs/features/tool-selection.md`; `docs/features/conversation-history.md` (chat search section); CLAUDE.md feature map (add 0112 to the Conversations row next to "chat search"); `.agentic/gotchas.md` (see Step 7) |
| Tests | `tests/Agent42.Tests/ChatSearchIndexTests.cs`, `ChatSearchIndexRegistrationTests.cs`, `CompositeIndexMatchesTheSearchQueryTests.cs`, `ToolCatalogTests.cs`, `CopilotAgentTests.cs`, new `PastChatQueryTests.cs`, new `PastChatToolTests.cs` |

## Context & constraints

**User decisions (interview, 2026-10-21)**
- **Corpus: everything the user owns, unlabelled.** Search covers every indexed conversation, including
  stored-agent, continuous-agent, email-started and `/api/agent-run` conversations. Hits are **not** fenced
  or marked untrusted. The user chose this over "own chats only" and "all, labelled untrusted" with the
  injection trade-off spelled out. Record it in ADR 0112 as an **accepted residual**, and do not quietly
  add a fence. Note the asymmetry: `read_document` *does* fence web-fetched text
  (`DocumentPartReader.ReadAsync(..., untrusted:)`, `AgentGrain.ReadDocumentTool.cs:~70`). ADR 0112 must
  say why this tool differs (user decision), so a reviewer doesn't "fix" it.
- **Shape: search + read-around.** Two tools under one key. Both results are compacted out of the session
  after the turn (the ADR 0098 `read_document` precedent).
- **Matching: any-term + ranked** through a **new** port method. `GET /api/search` keeps `CONTAINS`
  unchanged, because the sidebar's substring semantics are what users type against.
- **Default: ON.** `past_chats` goes into `AllSelectableToolKeys`, the ADR 0057 `memory` precedent. Per
  ADR 0021 it lands **OFF** for anyone who ever pressed Save, and for agents bound before the release
  (the bind snapshot). Document this; do not migrate.

**Design decisions made in planning (record each, with its rejected alternative, in ADR 0112)**
- **`read_past_chat` reads the index, not the other conversation's grain.** It uses a new
  `GetTurnsAsync(userId, conversationId, fromOrdinal, toOrdinal)` on `IChatSearchIndex`.
  *Rejected: `IAgentGrain(other).GetHistoryAsync()`.* That approach (a) deadlocks on a self-call when the
  target is the current conversation, since the grain is non-reentrant; (b) activates the target grain,
  whose `OnActivateAsync` connects MCP servers (`ChatSearchProjection`/ADR 0061 note on activation cost);
  and (c) would serve incognito turns still held in that activation's memory. The index never holds
  those (gotchas `:3308`). Cost of the chosen path: the index is best-effort, so a dropped write is a gap.
  The tool must say "some turns are missing from the index", never pretend a window is complete. A turn
  is capped at `ChatSearchLimits.MaxIndexedTextLength` (32 KB) in the index, which is far above the
  tool's per-turn cap anyway.
- **Ownership is structural plus one explicit check.** Every index call is single-partition on the user id
  from the grain key (`AgentGrain.UserIdFromGrainKey`, the same id `ChatSearchProjection` writes with,
  `ChatSearchProjection.cs:27`). On top of that, both tools join hits against
  `GrainFactory.GetGrain<IConversationIndexGrain>(_userId).GetConversationsAsync()` (the
  `IUserProfileGrain(_userId)` call at `AgentGrain.cs:283` is the precedent for an AgentGrain→per-user
  grain call). Hits for a conversation **not in the list are dropped**. That gives titles and dates
  (`ConversationInfo.Title` / `UpdatedAt`, `IConversationIndexGrain.cs:23-41`) and keeps orphaned index
  documents from resurfacing (ADR 0068's permanent-orphan residual). `ConversationIndexGrain` makes **no**
  outbound grain calls (grep-verified), so AgentGrain→index cannot form a cycle.
- **The current conversation is excluded** from search hits (it is already in context), and
  `read_past_chat` on it is refused with a short message. Over-fetch from the index at the full
  `ChatSearchLimits.MaxSearchResults` (50). Then drop the current conversation and orphans, and cap hits **per
  conversation** (e.g. 2) before taking the top N. Without the per-conversation cap, one chat with many
  matching turns (a cron agent) fills every slot. With it, the result spans conversations, which is what recall
  needs. The model reads deeper with `read_past_chat`.
- **Term handling is a pure function** (`PastChatQuery.Terms`). Split on whitespace and punctuation, trim,
  lower-invariant, distinct, drop single-character terms, cap the count (e.g. 8) and each term's length
  (e.g. 64). An empty result is a refusal string, not a query.
- **Cosmos SQL is built by pure, tested functions, never executed in tests** (the ADR 0061
  `BuildSearchSql` / `SearchSql_AsksForCaseInsensitiveMatching` pattern). The current Cosmos reference gives
  `FullTextScore(<path>, <array of string literals>)`. If literals are required, the terms are inlined, so
  **each inlined term must be JSON-escaped** (`JsonSerializer.Serialize(term)`) and a test must prove that a
  term containing `"`, `\` and `]` cannot break out of the literal. Use `@` parameters wherever Cosmos
  accepts them (at least `FullTextContainsAny`, conversationId and the ordinal bounds), and confirm in
  Step 0.
- **In-memory twin ranking is an approximation, and only its contract is pinned.** Rank by the number of
  distinct terms matched (`OrdinalIgnoreCase` substring), then total occurrences, then
  `(conversationId, ordinal)` for determinism. It is not BM25: no IDF, no stemming. Tests pin
  "any-term match", "more matched terms ranks first" and owner scope. They do not pin a BM25 order. The
  Cosmos order rests on the Step 0 smoke test.
- **Budgets (ADR 0004 — tool results are persisted).** In new `PastChatLimits`: hits per search (e.g. 8),
  snippet radius via `SnippetAround` (120 → ~240 chars), total search text cap (e.g. 4 KB), read window
  (e.g. 2 turns before, 3 after the given ordinal), per-turn text cap in a read (e.g. 2 KB), total read cap
  (e.g. 10 KB, never-binding given the per-turn cap × window: pin `window × perTurn <= total` with a test,
  the `ReadDocumentLimits` invariant pattern), and a per-turn **call** cap shared by both tools (e.g. 10),
  counted on `TurnContext` (ADR 0106). A closed `TurnContext` means the budget is exhausted, so the tool
  refuses.
- **Compaction.** A *successful* result starts with a fixed header prefix (`PastChatLimits.SearchHeaderPrefix`
  / `ReadHeaderPrefix`). After the turn it becomes a stub built from the call's own arguments (query, or
  conversationId + ordinal). Refusals stay verbatim, and a stub no longer starts with the prefix, so a
  second pass is a no-op (the `CompactReadDocumentResult` shape, `AgentGrain.ReadDocumentTool.cs:88-100`).
  Result and call are joined by CallId, never by position (gotcha cited at `AgentGrain.ArtifactTools.cs:~300`).
- **Never throws.** A failing index call (e.g. `TestChatSearchIndex` failure injection) returns
  "I could not search past chats: …" and the turn completes (`ThrowingIndex_DoesNotBreakTheReply` precedent).
- **Copilot chats get the tools automatically** through `CopilotFunctions` → `SelectableTools`. They are
  safe there: the tools use only `_userId`, the index and a grain call, all of which run on the activation
  scheduler via `SchedulerBoundAIFunction`. They need no browser, document or run-only broker
  (`IsExcludedFromCopilot` rationale, `AgentGrain.Copilot.cs:95-100`). Compaction does not reach Copilot's own
  SessionFs transcript. That is accepted and noted in ADR 0112.
- **Research agent and fleet are out of scope.** `ResearchAgentDefaults.EnabledTools = [WebSearch]` keeps it
  off there, and `FleetToolCatalog` never calls `SelectableTools`.
- **Incognito chats may *call* the tools.** Reading is not writing, and nothing from an incognito chat is
  ever in the index (ADR 0034/0061), so incognito content can never be *found*.

**Egress and trust (rule 13: ADR 0112 must record both as ADR 0013 extensions / residuals)**
- **Cross-provider movement.** Providers are per user (ADR 0018). A chat held on Azure OpenAI can now be
  quoted into a chat running on Anthropic or GitHub Copilot, so a provider receives text it never saw. The
  text stays within one user's own providers. With `memory` on, a reply built on recalled text is also
  retained to Hindsight.
- **Untrusted text reaches trusted chats (accepted, user decision).** Inbound email (ADR 0096) and
  `/api/agent-run` content is indexed and therefore findable.
- **Externally driven runs gain a read path over the whole history.** This residual was **not** part of
  the interview, so surface it at approval. It differs per surface:
  - **`ask_agent` (`/api/agent-mcp`, ADR 0070): code gate, in scope.** `AgentMcpRunner` already strips
    `memory` at bind time precisely because "recall would hand that caller content from the owner's
    unrelated conversations" (`AgentMcpRunner.cs:~210`). `past_chats` is that risk in a stronger form (raw
    messages, not extracted facts), so it joins the same narrowing-only filter. This is following an
    existing decision, not a new one.
  - **Inbound email (ADR 0096) and `/api/agent-run` (API key, ADR 0022): documented mitigation only (plan
    default; ask at approval).** An email sender, or an API-key holder (a weaker credential than the owner's
    login), can instruct a `null == all` agent bound after release to search the owner's chats and leak the
    result through the agent's outbound channels (fetch, webhook; ADR 0096 ~:78). The plan extends ADR 0096's
    "point email at an agent whose EnabledTools excludes memory and the fetch tools" note to name `past_chats`,
    and states the same for agent-run in ADR 0112. ADR 0070 itself kept push on `/api/agent-run` while stripping
    it from `ask_agent`, so per-surface asymmetry has precedent. Extending the bind filter to these two doors
    instead is a small change if the user prefers it at approval.
- **Titles are a second unfenced channel.** `ConversationIndexGrain.TouchAsync` derives a title from the first
  prompt (capped at 60 chars). For email and agent-run conversations that is attacker-written, and it is
  printed in every hit header. Record it in the ADR 0112 residual alongside the unlabelled-hits decision.

**Governing ADRs / gotchas to read first:** ADR 0061 + 0068 (projection, ordinals, best-effort, orphans),
0021/0057 (selectable keys, `null == all`, lands-OFF), 0098 (read tool + compaction exemplar), 0106
(TurnContext), 0004 (2 MB session), 0096 (untrusted inbound), 0070 (`ask_agent` bind-time exclusion),
0022 (`/api/agent-run`). Gotchas: `:187` (tool return persisted),
`:534` (TurnContext), `:1631` (null == all), `:3290`/`:3308`/`:3332`/`:3353` (chat search), `:4145` (Copilot
calls tools off-scheduler).

## Steps

### Step 0 — Verify the Cosmos ranked query on the real container (USER-run, read-only) — gates Step 3 only
The executor prepares, and the **user runs** in Azure Portal → Cosmos account → Data Explorer →
`agent42` / `chat-messages`, a query using two words the user knows occur in their chats:
```sql
SELECT TOP 10 c.conversationId, c.ordinal, c.text FROM c
WHERE FullTextContainsAny(c.text, "word1", "word2")
ORDER BY RANK FullTextScore(c.text, ["word1", "word2"])
```
plus the same with `@`-parameters if the portal's parameter pane allows it. Record in the spec's Review
section: (a) does it run without a full-text enrollment / capability error (the current docs mention an
enrollment; ADR 0061 asserted GA without one)? (b) which argument form `FullTextScore` accepts, array or
variadic? (c) does `FullTextContainsAny` accept parameters? (d) does `TOP` with `ORDER BY RANK` work?
Also confirm the resolved `Microsoft.Azure.Cosmos` version (3.61.0 in the main checkout's
`project.assets.json` at plan time) supports `ORDER BY RANK` in the SDK query pipeline. Check its
changelog; do not guess.
**Fallback if (a) or (d) fails:** implement `SearchRankedAsync` in the Cosmos adapter as one `CONTAINS`
query per term (≤ the term cap), scored in-process with the twin's ranking function (shared, pure). The
port signature does not change, so Steps 1, 2 and 4–7 are unaffected. Record which path shipped in ADR 0112.
Steps 1–2 and 4–6 can proceed in parallel with Step 0.

### Step 1 — Port + in-memory twin + test wrapper
Add to `IChatSearchIndex`:
- `Task<IReadOnlyList<ChatSearchHit>> SearchRankedAsync(string userId, IReadOnlyList<string> terms, int maxResults, CancellationToken ct = default)`
  matches any term, returns the most relevant first, and is clamped to `ChatSearchLimits.MaxSearchResults`.
- `Task<IReadOnlyList<ChatSearchTurn>> GetTurnsAsync(string userId, string conversationId, int fromOrdinal, int toOrdinal, CancellationToken ct = default)`
  returns an inclusive range in ordinal order. Missing ordinals are simply absent.
Implement both in `InMemoryChatSearchIndex`, with the ranking as a pure static the Cosmos fallback can reuse.
Forward both through `TestChatSearchIndex`, honouring its failure injection. Check whether anything else in
`tests/` implements the port (grep at execution time).
Verify: `dotnet test --filter FullyQualifiedName~ChatSearchIndexTests`.

### Step 2 — `PastChatQuery` + `PastChatLimits` (pure) — parallelizable with Step 1
Term extraction and all budgets, prefixes and stub builders. Include the `window × perTurn <= totalRead`
invariant test.
Verify: `dotnet test --filter FullyQualifiedName~PastChatQueryTests`.

### Step 3 — Cosmos adapter (after Step 0)
Add the pure `BuildRankedSearchSql(terms, take)` and `BuildTurnsSql()`, and implement both methods
(single-partition `PartitionKey(userId)`, like `SearchAsync` ~:72). The turns query orders by `c.ordinal`
only: a single-property `ORDER BY` is served by the range index (`/*` included, only `/text` excluded).
Extend `CompositeIndexMatchesTheSearchQueryTests` to iterate **every** SQL builder, not just
`BuildSearchSql`, so a future multi-property `ORDER BY` on any of them fails offline (gotcha `:3353`). This
is a **rewrite** of that file's `OrderByPaths()` (`:~64-84`), not a tweak. Today it calls `BuildSearchSql(25)`
directly and splits the `ORDER BY` tail on commas. `ORDER BY RANK FullTextScore(c.text, ["a","b"])` must be
recognised as a rank clause (no composite index needed), never parsed as bogus paths. Keep the AC9 test name.
`SearchSql_AsksForCaseInsensitiveMatching` must stay green **unmodified**: that proves the sidebar query
did not change.
Verify: `dotnet test --filter "FullyQualifiedName~ChatSearchIndexRegistrationTests|FullyQualifiedName~CompositeIndexMatchesTheSearchQueryTests"`.

### Step 4 — TurnContext counter
Add a past-chat call counter + `RecordPastChatCall()` following the `RecordDocumentPartRead` shape (closed
reads as exhausted). No grain field (rule 6).
Verify: the existing TurnContext tests + a new case in `PastChatToolTests` (Step 5).

### Step 5 — Tools, catalog, BuildAgent, compaction
- `AgentGrain.PastChatTools.cs`: `PastChatTools()` factory returning both `AIFunction`s (names
  `search_past_chats`, `read_past_chat`). Write each description as the policy the model reads: when to use
  the tool (the user refers to an earlier conversation, or asks "what did we decide about X"), that results
  come from the user's other conversations, how to follow a hit with `read_past_chat`, and to search short
  distinctive keywords. Parameters get `[Description]`s.
  Keep each method under S3776 = 15. Split guard/catch shells from the fetch/format halves, as
  `ReadDocumentAsync`/`ReadDocumentPartAsync` do.
- `ToolCatalog`: const `PastChats = "past_chats"`, appended **last** in `AllSelectableToolKeys` and
  `Descriptors`. Map both tool names in `ToolNameToKey`. Label "Past chats", with a description that says it
  searches and reads the user's other conversations. **This breaks three existing ordering pins, and they must
  be edited, not worked around:** `ToolCatalogTests.cs:~201-204` (`BrowserToolsAndSharedState_AreAppendedLast_AndMapTheirTools`,
  which pins `[BrowserTools, SharedState]` as the last two in both lists) and `:~218`
  (`ReadDocument_SitsBeforeBrowserTools_AndIsDescribed`, `[^3]`). Also update the `ToolCatalog.cs` comments
  that say "BrowserTools and SharedState are LAST" / ReadDocument "sits before BrowserTools …".
- `BuildAgent`: `.. PastChatTools()` in the unconditional list.
- `CompactToolCall`: a branch for both names.
- `AgentMcpRunner`: add `past_chats` to the bind-time exclusion (rename + doc comment, see Affected areas).
Verify: `dotnet test --filter "FullyQualifiedName~PastChatToolTests|FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~AgentGrainTests.EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool|FullyQualifiedName~CopilotAgentTests|FullyQualifiedName~AgentMcpServerTests"`.

### Step 6 — DevHost check
DevHost registers `InMemoryChatSearchIndex` directly (`tools/Agent42.DevHost/Program.cs:150`), so the new
methods come for free. Run DevHost, chat twice in two conversations, then ask in a third to recall the
first. Confirm the tool call and result. The canned LLM may not call tools on its own; if not, record that
the silo tests are the evidence. Do not claim a live check that did not happen.

### Step 7 — Docs (same MR)
ADR 0112 via `/decide`: all decisions, rejected alternatives and residuals above, plus the Step 0 outcome.
Add amended-by notes to 0061 and 0096. Update `docs/features/tool-selection.md` (new key) and
`docs/features/conversation-history.md` (the model can now search too, and the sidebar still uses
`CONTAINS`). Add 0112 to the CLAUDE.md feature map. Add gotchas via `/gotcha`: (1) "the sidebar search and
the tool search are different queries on purpose (substring vs ranked); don't unify them without an ADR";
(2) the twin is not BM25, so a test asserting a Cosmos order offline is wrong; (3) `read_past_chat` reads
the index, never the target grain (deadlock/activation/incognito reasons). Run `/sync-docs`.

### Final — gates
`dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
`node .agentic/scripts/check-new-code-coverage.mjs`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Ranked search matches any term, case-insensitively | `SearchRanked_MatchesAnyTerm_CaseInsensitive` | `ChatSearchIndexTests.cs` |
| 2 | More matched terms rank first (twin contract) | `SearchRanked_RanksMoreMatchedTermsFirst` | `ChatSearchIndexTests.cs` |
| 3 | Ranked search and turn reads are owner-scoped | `SearchRanked_IsOwnerScoped`, `GetTurns_IsOwnerScoped` | `ChatSearchIndexTests.cs` |
| 4 | Ranked search clamps to the result ceiling | `SearchRanked_ClampsToTheResultCeiling` | `ChatSearchIndexTests.cs` |
| 5 | `GetTurns` returns the inclusive range in ordinal order, and skips gaps | `GetTurns_ReturnsTheRangeInOrdinalOrder_SkippingGaps` | `ChatSearchIndexTests.cs` |
| 6 | Cosmos ranked SQL uses any-term + `ORDER BY RANK` and is single-partition | `RankedSearchSql_UsesFullTextContainsAnyAndRank` | `ChatSearchIndexRegistrationTests.cs` |
| 7 | An inlined term cannot escape its literal | `RankedSearchSql_EscapesQuotesBackslashesAndBrackets` | `ChatSearchIndexRegistrationTests.cs` |
| 8 | The turns SQL filters by conversation + ordinal range | `TurnsSql_FiltersConversationAndOrdinalRange` | `ChatSearchIndexRegistrationTests.cs` |
| 9 | No SQL builder has a multi-property `ORDER BY` without a composite index | `EveryMultiPropertyOrderBy_HasAMatchingCompositeIndex` (extended to all builders) | `CompositeIndexMatchesTheSearchQueryTests.cs` |
| 10 | Term extraction: split, lowercase, dedupe, drop 1-char, cap count and length, empty → none | `Terms_*` | `PastChatQueryTests.cs` |
| 11 | The read budget invariant holds | `ReadWindow_TimesPerTurnCap_FitsTheTotalCap` | `PastChatQueryTests.cs` |
| 12 | Search finds another conversation's message, with its title | `Search_FindsAnotherConversation_WithTitle` | `PastChatToolTests.cs` |
| 13 | Search excludes the current conversation | `Search_ExcludesTheCurrentConversation` | `PastChatToolTests.cs` |
| 14 | A purged conversation and an orphaned index doc are never returned | `Search_DropsHitsForConversationsNotInTheIndexGrain` | `PastChatToolTests.cs` |
| 15 | An incognito turn is never found | `Search_NeverFindsAnIncognitoTurn` | `PastChatToolTests.cs` |
| 16 | Read returns the window around the ordinal; read of the current conversation is refused | `Read_ReturnsTheWindowAroundTheOrdinal`, `Read_OfTheCurrentConversation_IsRefused` | `PastChatToolTests.cs` |
| 17 | Read of an id the user doesn't own → not found, with no other user's text | `Read_OfAnotherUsersConversation_FindsNothing` | `PastChatToolTests.cs` |
| 18 | Per-turn call cap and closed TurnContext refuse | `PastChatCalls_PastTheCap_AreRefused`, `PastChatTools_OutsideARun_Refuse` | `PastChatToolTests.cs` |
| 19 | Successful results are compacted after the turn; refusals are not | `Results_AreCompactedAfterTheTurn_RefusalsKept` | `PastChatToolTests.cs` |
| 20 | A throwing index yields an error string, and the reply completes | `ThrowingIndex_ReturnsAnError_AndTheReplyCompletes` | `PastChatToolTests.cs` |
| 21 | `past_chats` is selectable, described and maps both tools; appended last | `PastChats_IsSelectable_Described_AndMapsBothTools` (+ ordering assertions updated) | `ToolCatalogTests.cs` |
| 22 | On for `null`, off for an older saved selection | `PastChats_IsOnForNull_AndOffForAnOlderSavedSelection` | `ToolCatalogTests.cs` |
| 23 | Deselecting `past_chats` removes both tools from the agent | `PastChats_Deselected_RegistersNeitherTool` | `PastChatToolTests.cs` |
| 24 | Copilot chats receive both tools | `CopilotFunctions_IncludeThePastChatTools` | `CopilotAgentTests.cs` |
| 25 | Every registered tool maps to a key (existing guard, now covering both names) | `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` | `AgentGrainTests.cs` |
| 26 | An `ask_agent` binding of a `null == all` agent excludes `past_chats` (and still excludes memory + push) | `AskAgent_BindingExcludesPastChats` | `AgentMcpServerTests.cs` |
| 27 | Hits are capped per conversation, so one chat cannot fill the result | `Search_CapsHitsPerConversation` | `PastChatToolTests.cs` |
| 28 | The tool descriptions carry the policy: other conversations, follow with `read_past_chat`, short keywords | `PastChatTools_DescriptionsStateThePolicy` | `PastChatToolTests.cs` |
| 29 | The sidebar search query is unchanged | `SearchSql_AsksForCaseInsensitiveMatching` (existing, unmodified) + `SearchEndpointTests` (existing) | `ChatSearchIndexRegistrationTests.cs`, `SearchEndpointTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (build error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job (no new packages are planned) |
| build / test / format | pass | `dotnet build` / `dotnet test` / `dotnet format --verify-no-changes` |

## Out of scope

- Fencing or labelling hits from agent or email conversations (rejected by the user; recorded as a residual).
- A code gate that removes `past_chats` from **email- or `/api/agent-run`-started** runs (plan default; ask at
  approval). ADR 0096's documented mitigation is extended instead. The `ask_agent` surface **is** gated (ADR 0070
  precedent).
- Changing the sidebar search (`GET /api/search`) to ranked matching.
- Research agent and fleet access.
- Dates per message (ADR 0061: `ChatTurn` has no timestamp; the conversation-level `UpdatedAt` is used).
- A reindex UI (ADR 0061 Consequences, still open), Dutch stemming, and semantic/vector search over chats.
- Summarising a whole past conversation (the model can page with repeated `read_past_chat` calls within the
  call cap).

## Review

Critic pass, 2026-10-21. All findings were accepted. None were rejected.

| Sev | Finding | Handling |
|-----|---------|----------|
| HIGH | ADR 0070 `ask_agent` binds with `WithoutMemoryOrPush`, so a `null == all` agent would expose `past_chats` to MCP key holders. That is the exact reason memory is stripped there | Added: Affected areas row, Step 5 bullet, AC26, ADR 0070 amended-by note |
| MED | `/api/agent-run` residual understated; "out of scope" wording inconsistent with ADR 0070's existing gate | Residual rewritten per surface; ask_agent gated, email + agent-run documented-only as the plan default, flagged for the user at approval |
| MED | Ordering pins `ToolCatalogTests.cs:~201-204`, `:~218` and the `ToolCatalog.cs` comments will break | Named explicitly in Step 5 |
| LOW | Titles (first prompt, 60 chars) are attacker-written for email/agent-run chats | Added to the ADR 0112 residual |
| LOW | One conversation can fill every hit slot; no per-conversation dedupe | Over-fetch at 50 + per-conversation cap, AC27 |
| LOW | No test for descriptions-as-policy or sidebar SQL unchanged; composite-index test needs a rewrite | AC28, AC29; Step 3 now says rewrite `OrderByPaths()` |

Step 0 outcome (2026-10-21, after the PR opened): the user ran the query in Data Explorer. It first needed a
Cosmos data-plane Data Reader assignment for the user, which the user ran themselves. **`FullTextScore(c.text, [..])`
(array) is rejected with `SC2241`**: the term arguments must be string literals. `FullTextContainsAny` parsed, and
there was no enrollment error. The builder now emits the variadic `FullTextScore(c.text, "a", "b")`. A successful
run of that form is still pending. ADR 0112 records the fallback
(per-term `CONTAINS` ranked by `RankByTerms`, local to `CosmosChatSearchIndex`) for a post-deploy smoke failure.

Diff critic pass, 2026-10-21: no HIGH findings. Fixed: `docs/features/custom-agents.md` still named
`WithoutMemoryOrPush` and the two-key set (MED); a stale call-site comment in `AgentMcpRunner` (LOW); a
`Window(int.MaxValue)` overflow (LOW, clamped + test). The historical ADR 0070/0076 mentions of the old name stay as
written, under 0070's amended-by note.

## Implementation divergences from the plan

- **Tool descriptions live in `PastChatLimits`** (`SearchDescription`, `ReadDescription`), not inline in
  `AIFunctionFactory.Create`, so AC28 can pin them. No test could reach a grain tool's description otherwise.
- **`PastChatQuery` also holds `Select`, `Window`, `FormatSearch` and `FormatRead`.** There is no separate formatter
  file. All of them are pure and keep the grain partial's methods under S3776.
- **No whole-result cap on a read.** The per-turn cap × window fits `MaxReadTextLength` by construction (AC11). The
  search result keeps a defensive 4 KB cap.
- **Cosmos terms are JSON-escaped with the relaxed encoder**, which gives `\"` and `\\` rather than `"`, because
  those are the escapes a Cosmos string literal certainly accepts. `FullTextContainsAny` takes inlined literals too,
  not parameters, so both functions share one literal list.
- **A second test double, `ChatSearchProjectionTests.RecordingIndex`,** implements the port and gained the two
  methods. The plan named only `TestChatSearchIndex`.
- **More tests needed updating than the plan named:** the four service-worker cache pins (`*AssetsTests`, v69 → v70)
  and `ToolSelectionApiTests.Tools_Catalog_ReturnsSelectableKeys` (the new key in `/api/tools`).
- **The agent editor's email note** (`wwwroot/index.html`) now names Past chats, Long-term memory and web fetching as
  the tools to turn off, so `sw.js` CACHE was bumped to v70 (rule 3). The plan expected no frontend change.
- **Two gotchas, not three.** "Sidebar vs tool query" and "the twin is not BM25" are one entry.
- **Step 6 (DevHost) was not run.** The canned LLM does not call tools on its own. The silo tests in
  `PastChatToolTests` drive both tools through `FakeChatClient.CallToolPrompt`, and those are the evidence.
- **Three tests flaked once in the first full run** and passed on rerun and in the second full run:
  `McpElicitationTests.Elicitation_PendingCalls_ListIt_AsMcpElicitation`,
  `McpToolImageTests.StoredSession_HoldsNoImageBytes_AfterAScreenshotTurn` and
  `ChromeExtensionBridgeTests.ExtensionCore_DeniedCallNeverReachesTheBrowser`. None touches this feature's code.
