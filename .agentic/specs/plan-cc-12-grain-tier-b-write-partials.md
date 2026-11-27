# Plan cc-12: AgentGrain — write-based tier-B partials behind narrow ports

planned-against: main @ 02414ff
parent: `plan-cc-00-programme.md`; follow-up to cc-10 (ADR 0148), which moved only the read-only partials and left these
for "a separate plan with a narrow state-mutation port". **Optional refactor, no feature value.** Do it only if making
`AgentGrain` smaller is still wanted; the programme's main gains (single persist tail, short methods, named tool sets) shipped in 05-10.
Branch per step: `feature/cc-12-<n>-<slug>`, one MR per step, each from up-to-date `main`.

## Overview

`WebTools`, `WebDocumentTool`, `ArtifactTools` (tool half), `McpApps` and part of `Documents` are tool-shaped but write
`_state`. ADR 0106 makes the grain the only owner of `_state`/`_session`/`_agent`, so a moved collaborator may not hold them.
This plan introduces **narrow ports**: small classes the grain builds in `OnActivateAsync` that expose only the operations a
collaborator needs, each reading `_state.State` live through a `Func` (ADR 0148: a captured list goes stale, `ClearStateAsync`
replaces state). Then it moves each partial behind its port, one MR each, riskiest (the exfiltration allow-list) first
so that a bad seam stops the plan early.

## Step-0 survey result (2026-11-27, three scouts; evidence = the partials named below)

| Partial | Verdict | Why |
|---|---|---|
| `Web/AgentGrain.WebTools.cs` (281) | **move** | writes only `FetchableUrls`/`SeededFetchableUrls`; no `_session`/`_agent`/`WriteStateAsync` (the turn tail persists) |
| `Documents/AgentGrain.WebDocumentTool.cs` (292) | **move, rebuild stays grain-side** | writes `WebDocumentNames/Urls` + one `WriteStateAsync`; `RebuildForIngestedWebDocumentAsync` touches `_agent`/`_session` and stays |
| `Artifacts/AgentGrain.ArtifactTools.cs` (412) | **move the tool half only** | tool half writes only `Artifacts`; compaction half (`CompactToolPayloads`, `CompactToolCall`, `ToolText`, ...) is used by the persist tail, fork, image turn, retention: stays |
| `Mcp/AgentGrain.McpApps.cs` (122) | **move** | writes `McpApps`; reads session call ids; ordering vs. persist/retention/truncation must hold |
| `Documents/AgentGrain.Documents.cs` (167) | **move the read-only pieces (stretch)** | search provider + document awareness; `RegisterDocumentAsync` is public and swaps `_agent`/`_session`: stays |
| `Attachments/AgentGrain.Attachments.cs` (208) | **stays** | mutates the live session and the positional `TurnAttachments` join (ADR 0004/0013); would need ~4 ports |
| `Conversations/AgentGrain.ModelRouting.cs` (193) | **stays** | writes `AutoModel`, swaps `_chatClientFactory`, reads grain-wide `_runsInFlight` (ADR 0106), feeds `SwitchModelAsync` |

## Context & constraints

- **Pattern:** ADR 0147/0148; exemplars `ChatGateway/ChatDeliveryRelay.cs`, `Documents/ReadDocumentTool.cs`, `Search/PastChatTools.cs`.
  Classes are `public` (no `InternalsVisibleTo`), built with `new` in `OnActivateAsync`, not DI-registered (grain ctor unchanged, ADR 0105).
- **Port shape (decision for this plan):** one small class per state area, built by the grain, e.g. `FetchableUrlList`, `WebDocumentList`,
  `ArtifactStore`-style, each taking `Func<AgentGrainState>` (or the one list it owns) and exposing named operations, never the list.
  Rejected: handing the collaborator `Func<AgentGrainState>` directly (that is `_state` by another name); one aggregate "state access"
  object (hides dependencies, ADR 0147 rejected the same shape).
- **Provenance of `FetchableUrls` (critic finding 1, verified):** `RememberFetchableUrlsInUserText` has exactly one caller,
  `BuildUserTurnAsync` (`AgentGrain.cs:~1390`), reached via `PrepareUserTurnAsync` inside `ChatCoreAsync`. Every turn kind that runs it
  (`ChatAsync`/`ForChat`, `EnqueuePromptAsync`/`ForBackground` agent-run, `ReceiveFleetResultAsync`/`ForFleetResult`) has an OPEN
  `TurnContext`, so a `TurnContext` check in the port discriminates nothing. A reminder is excluded **structurally**: `ReceiveReminder`
  calls `_agent.RunAsync` directly and never reaches `BuildUserTurnAsync`. Therefore: no fail-closed/TurnContext check and no flag in the
  port; the single call site is the control. **Agent-run and fleet-result turns feeding the list is existing behaviour this refactor
  preserves** (gotchas.md ~1469); whether that is a hole is a separate question for the user, not this plan.
  Because the method becomes public on a public class, any assembly code could feed the exfiltration list from model-authored text:
  add a source guard (`ProjectLayoutTests` style) that `RememberFromUserText` has exactly one call site (`BuildUserTurnAsync`) and that
  `RememberSeededUrl`'s only caller is the `UrlHarvestingMcpTool` delegate. If that guard cannot be written cleanly, stop: WebTools stays a partial.
- **ADR bookkeeping:** ADR 0147 ("a tool that must write `_state`, `_session` or `_agent` stays a partial") and ADR 0148's Consequences say
  the opposite of this plan. The step-1 ADR has `extends`/`amends: [0147, 0148]` and `amended_by` back-links are added to both (`AdrLinkTests`).
- **Tools read `TurnContext.CurrentOrClosed`** (ADR 0106, gotchas.md ~1465); they are invoked by the framework and have no turn parameter.
  Only grain-side consume sites take `turn`.
- **Security (do not weaken):** `FetchableUrls` is the exfiltration control (ADR 0011/0066/0067, gotchas.md 314-336, 344-434). Whole-URL
  match on the normalized form, user-text and search seeds in `FetchableUrls` (cap `MaxFetchableUrls`, oldest-first trim), MCP seeds in
  the **separate** `SeededFetchableUrls` (trim evicts only its own list, `AbsoluteUrlPattern` stricter than `UrlPattern`), no change to
  *how it is consulted*. `Normalize(Uri)` is shared by WebTools and WebDocumentTool: move it to one static home first.
- **Public consts** `AgentGrain.MaxFetchableUrls/MaxSeededFetchableUrls/MaxSeededUrlsPerResult` are referenced by tests
  (`McpUrlSeedingTests`, `AgentGrainTests.FetchableUrls_AreBounded`): keep them on the grain or re-point the tests in the same step.
- **Compaction statics stay in the grain** (ADR 0147/0148): `CompactToolPayloads` (instance, uses `_session`), `CompactToolCall`, `ToolText`,
  `PartArgument`, `ParseArtifactResult`, `ArtifactsFromToolCalls`, `IsCompacted`; plus statics other partials call
  (`UnknownArtifact`, `VersionList` from `ClientTools.cs`; `CreateArtifactTool`/`UpdateArtifactTool` from `ConversationForking`).
  Gotchas.md ~3935-3959: new compaction families are a branch of the single pass.
- **Persisted types untouched** (programme constraint 3); `ProjectLayoutTests.cs:133-150` lists the grain part files and must be updated in
  any step that deletes or renames a partial; `.agentic/code-shape-baseline.json` keys change (a report, not a gate).
- Every step keeps the tool catalog golden (`ToolCatalogSnapshotTests`) unchanged: keys, descriptions, parameter names verbatim.
- ADR numbers from `origin/main` at merge time; one ADR (0149 or next) for the whole plan, written in step 1 and amended per step.

## Steps

0. (Already done: the survey above.) At the start of each step re-read the partial against then-current `main`.
1. **Shared `Normalize` + `FetchableUrlList` port + `WebTools` collaborator.** Files: `Web/AgentGrain.WebTools.cs` → `Web/WebTools.cs`
   (+ `Web/FetchableUrlList.cs`, static `UrlNormalizer`). Moves `WebSearchAsync`, `IsFetchable`, `RememberFetchableUrl(s)`,
   `RememberSeededUrl(s)`, the regexes. Grain call sites: `BuildAgent.cs:244` (web_search), `:370` (`UrlHarvestingMcpTool` method group),
   `AgentGrain.cs:1390` (`BuildUserTurnAsync`), and `IsFetchableUrl` from WebDocumentTool (temporarily via the port).
   Collaborator takes: `IWebSearch`, logger, grain key, the port. Port takes `Func<AgentGrainState>`-derived list accessors (live).
   Verify: `McpUrlSeedingTests`, `WebDocumentToolTests`, `AgentGrainTests.FetchableUrls_AreBounded`, `GrainStorageSerializerTests` (lists round-trip), `ToolCatalogSnapshotTests`.
   New tests: port unit tests (trim only its own list, normalized contains, user-vs-seed separation); a grain test that a reminder run adds no URL;
   the single-call-site source guard (Context).
2. **`WebDocumentList` port + `WebDocumentTool` collaborator** (depends on 1). Files: `Documents/AgentGrain.WebDocumentTool.cs` → `Documents/WebDocumentTool.cs`.
   Port ops: contains-url, paired append-and-trim of names+urls (lockstep, `MaxWebDocuments`), count. Collaborator also takes
   `IWebDocumentFetcher`, `IDocumentIngestor`, logger, grain key, an embed-query `Func` that reads `_chatClientFactory` LIVE (it is reassigned by
   `RefreshChatClientFactoryAsync`; preserve `EmbeddingModelNotConfiguredException` / `EmbeddingDimensionMismatchException`), and reads
   `TurnContext.CurrentOrClosed` itself (incognito gate, `RecordWebDocumentIngested`).
   **Persistence decision:** the `WriteStateAsync` at `WebDocumentTool.cs:~183` is a write handle on grain state. Preferred: pass a named
   `Func<Task> persistNow` built by the grain, with an ordering test (append+trim → `RecordWebDocumentIngested` → persist → rank, before the
   excerpts are ranked). If that reads as a back-door `_state` handle in review, apply the stop rule: leave `WebDocumentTool` a partial.
   **Stays in grain:** `RebuildForIngestedWebDocumentAsync` (keep a slim `Documents/AgentGrain.WebDocumentTool.cs` partial holding it, so the
   `ProjectLayoutTests:138` entry and the gotchas.md ~1495 path stay valid) + the `TakeWebDocumentIngested` consume (two sites, gotchas.md 1458-1475),
   `PurgeAsync` clears.
   Verify: all of `WebDocumentToolTests` (reminder rebuild, incognito refusal before fetch, bounds, purge), `ReadDocumentToolTests`.
3. **`ArtifactTools` tool half** (parallelizable with 4). `Artifacts/AgentGrain.ArtifactTools.cs` keeps the compaction statics (rename the file
   `AgentGrain.ArtifactCompaction.cs`, update `ProjectLayoutTests` and gotchas paths) and the tool half moves to `Artifacts/ArtifactTools.cs`
   with an `ArtifactList` port (get, count, add record, append version + title). Name the collaborator `ArtifactToolSet` (the grain method `ArtifactTools()` at
   `BuildAgent.cs:297` coexists). Collaborator takes: port, `IAttachmentStore`, `TimeProvider`, logger, grain key. Keep `GetArtifactsAsync` on the grain (public `IAgentGrain` method).
   **Visibility decisions (a public collaborator cannot call a private grain static):** `UnknownArtifact` and `VersionList` (used by `ClientTools.cs:~105,109`
   AND the moved read tool) move to the collaborator as `internal`/public statics and `ClientTools` re-points; `ReadArtifactTool` const becomes shared
   (compaction `CompactToolCall` uses it too); `CreateArtifactTool`/`UpdateArtifactTool` stay `internal const`. Stays in the grain: `CompactToolPayloads`,
   `CompactToolCall`, `ToolText`, `IsCompacted`, `RefusedContentStub`, `ParseArtifactResult`, `ArtifactsFromToolCalls`.
   The port is not the only accessor: `ClientTools.cs:~103`, `AgentGrain.cs:~1644` (purge) and `EditFork.cs` (reassigns the dictionary) read/replace
   `Artifacts` directly; that is fine because the port reads state live.
   **Coupling to pin:** `ArtifactResult` (moves) must stay the exact JSON shape `ParseArtifactResult` (stays) parses; add a round-trip test
   if `ArtifactToolTests` does not already cover it. Rename the grain file to `AgentGrain.ArtifactCompaction.cs`; update `ProjectLayoutTests:147`, gotchas paths, ADR 0084 line.
   Verify: `ArtifactToolTests` (incl. both compaction tests), `ArtifactsApiTests`, `EditRegenerateForkTests`, `ConversationExportApiTests`.
4. **`McpApps` collaborator** (parallelizable with 3). `Mcp/AgentGrain.McpApps.cs` → `Mcp/McpAppStore.cs` with a `Func<List<McpAppRecord>>`,
   a read-only session-call-id `Func`, the attachment store, logger, grain key. Moves the whole partial: `StoreMcpAppsAsync`, `ReconcileMcpApps`,
   `WithMcpApps` (history projection, `AgentGrain.cs:~1445`), `DropOldestMcpApps` (cap), `McpAppBlobName`, `GetMcpAppResultAsync` body; the purge
   `McpApps.Clear()` (`AgentGrain.cs:~1645`) stays. No `ProjectLayoutTests` edit (this partial is not in its list). Ordering contract (a test): `StoreMcpAppsAsync` before the write at
   `AgentGrain.cs:~1331`; `ReconcileMcpApps` after prune (`Retention.cs:43`) and truncation (`EditFork.cs:232`). `GetMcpAppResultAsync` stays a grain method delegating.
   Verify: `McpAppsGrainTests`, `McpAppsLifecycleTests`, `McpAppsApiTests`, `GrainStorageSerializerTests` (Id 19 shape).
5. **(Stretch) `Documents` read-only pieces.** Move `BuildDocumentSearchProvider`/`SearchDocumentContextAsync`/`WithDocumentAwareness`
   behind Funcs (chat-client factory as a `Func`, it is reassigned by `RefreshChatClientFactoryAsync`; `IsResearchLoopFeedback` made reachable).
   `RegisterDocumentAsync` and `ExcludeInjectedContextMessages` (called from ImageTurn/FleetAnalyzer) stay. Skip the step if it needs more than two Funcs
   beyond the factory. Verify: `WebDocument_IsNotDescribedAsUserUploaded`, document RAG tests in `AgentGrainTests`, `ResearchAgentTests`.
6. **Close-out:** ADR amendments, gotchas evidence paths, `grep -rn` for every renamed path in `CLAUDE.md .agentic docs`, programme status row, move plan to `completed/`.
   Record in the ADR that Attachments, ModelRouting, `RegisterDocumentAsync` and the compaction half stay, with the reasons above.

Steps 3 and 4 are behaviourally independent but both edit `OnActivateAsync`, the purge region (`AgentGrain.cs:~1641-1645`), the ADR and the
code-shape baseline: run them as separate MRs each rebased on `main`, and expect trivial conflicts in those shared spots.

Stop rule: stop at the first step whose seam needs more than its named port (a back-door `_state` handle, a second port per partial, or a
behaviour change). Record it in the MR and in this plan's Review; later steps are then dropped, not forced.

## Acceptance criteria → tests

| AC | Test | File |
|---|---|---|
| No `FetchableUrls` consult change: whole normalized URL, user vs seed lists separate, trim own list | existing + new port tests | `McpUrlSeedingTests`, `AgentGrainTests`, new `FetchableUrlListTests` |
| A reminder run adds no fetchable URL (structural; agent-run and fleet-result turns keep feeding it, unchanged) | new grain test | `WebDocumentToolTests` or `McpUrlSeedingTests` |
| `RememberFromUserText` / `RememberSeededUrl` keep their single call sites | new source guard | `ProjectLayoutTests` |
| `ArtifactResult` JSON shape still parses in `ParseArtifactResult` | existing or new round-trip | `ArtifactToolTests` |
| ADR 0147/0148 back-links | existing | `AdrLinkTests` |
| web document ingest: bounds, lockstep, purge, reminder rebuild, incognito refusal | existing | `WebDocumentToolTests` |
| Artifact tools + both compaction tests unchanged | existing | `ArtifactToolTests` |
| MCP app record/reconcile order and caps | existing + new ordering test | `McpAppsLifecycleTests` |
| Tool keys/descriptions/params unchanged | existing golden | `ToolCatalogSnapshotTests` |
| Persisted shape unchanged | existing | `GrainStorageSerializerTests` |
| Tool results still bounded | existing | `ToolResults_DoNotBlowTheSessionSizeBudget`, `ReadDocumentParts_DoNotBlowTheSessionSizeBudget` |
| Moved layout files still registered | updated | `ProjectLayoutTests` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`, re-read at execution)

`coverage_new_code_pct_min: 80` (`node .agentic/scripts/check-new-code-coverage.mjs`; moved lines count as added, so new port/collaborator
tests must execute them); `cognitive_complexity_max: 15` (S3776, build error); `vulnerable_packages: 0`; plus `dotnet build`, full
`dotnet test Agent42.slnx` (live Azure/Copilot tests fail without credentials: report, don't hide), `dotnet format --verify-no-changes`,
`check-doc-paths.mjs`, `AdrLinkTests` (an ADR that amends another must be listed in its `amended_by`).

## Out of scope

Attachments, ModelRouting, `RegisterDocumentAsync`, the compaction half, Tier C (Research, ImageTurn, Copilot, ClientTools, SharedState,
EditFork, Scheduling, RetentionSweep, BuildAgent hub), any behaviour change, any persisted-type change, the frontend.

## Review

Critic (2026-11-27): do-not-approve-as-written, 10 findings, all accepted and folded in above.
- 1 (high) fail-closed framing wrong: reworded; no TurnContext check; single-call-site guard added; AC corrected.
- 2 ADR 0147/0148 amendment, 3 TurnContext in tools, 4 persist handle decision, 5 artifacts stay-list and visibility, 6 McpApps pieces,
  7 shared-file conflicts, 8 slim WebDocumentTool partial, 9 dependency lists, 10 `ArtifactToolSet` name: all applied.
Rejected: none.
