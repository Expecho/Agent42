# Plan — Conversation export (Markdown, JSON, ZIP, and bulk "export all")

planned-against: main @ 2c1dbf3

## Overview

**Goal:** the owner can download one conversation as a readable **Markdown** transcript, a full-fidelity **JSON**
document, or a **ZIP** that bundles both with the conversation's image attachments and every artifact version — and
can bulk-export **all** their conversations as ZIP parts.

**Why now:** share links (ADR 0035) are online-only and deliberately strip tool calls; the owner has no way to keep or
archive a copy of their own chats. Requested in the 2026-10-10 feature review; the user picked all four forms.

## Affected areas

Verified against the tree at `2c1dbf3`.

- **New feature folder `src/Agent42.Backend/Export/`** (flat namespace `Agent42.Backend`, ADR 0039/0040 — IDE0130 is off):
  `AgentApi.Export.cs` (new `MapExportEndpoints` partial), `ConversationExport.cs` (export records),
  `ConversationExporter.cs` (gather from grains), `ConversationMarkdown.cs` (pure renderer),
  `ConversationZipWriter.cs` (streamed ZIP), `ExportFileNames.cs` (sanitization), `ExportLimits.cs`.
- `src/Agent42.Backend/Api/AgentApi.cs` — call `MapExportEndpoints(app)` from `MapAgentApi`; update the route-count
  sentence in its doc comment (75 → 77 — the rule is stated there).
- Frontend: `src/Agent42.Backend/wwwroot/index.html` (download section in `#shareLinkDialog`; export-all control in
  `#panelGeneral`), `wwwroot/js/dom.js` (refs), `wwwroot/js/sharelinks.js` (per-conversation download links), **new**
  `wwwroot/js/export.js` (bulk loop, imported from `main.js`), `wwwroot/styles.css`, `wwwroot/sw.js` (`SHELL` +
  `/js/export.js`, bump `CACHE`).
- Tests: new `tests/Agent42.Tests/ConversationMarkdownTests.cs`, `tests/Agent42.Tests/ConversationExportApiTests.cs`,
  `tests/Agent42.Tests/ConversationExportAssetsTests.cs`; edit **every test that pins the `sw.js` `CACHE` string** —
  run `grep -rn "agent42-shell-v" tests` before bumping. At plan time that is four files:
  `ClientToolsAssetsTests.cs:98`, `PwaAssetsTests.cs:288`, `SearchAssetsTests.cs:127`, `ResearchAssetsTests.cs:33`.
- Docs: new ADR via `/decide` (number = next free at execution time — other plans written 2026-10-10 also claim ADRs;
  do not assume 0091), `CLAUDE.md` feature map "Conversations" row, `.agentic/gotchas.md` only if step 3 hits a trap.

**No grain or persisted-state change.** Everything needed is already on `IAgentGrain` / `IConversationIndexGrain`
(`GetHistoryAsync`, `GetArtifactsAsync`, `GetAgentBindingAsync`, `GetSelectedModelAsync`, `GetSharedStateAsync`,
`GetConversationsAsync`), so no `[Id(n)]` fields and no `GrainStorageSerializerTests` path.

## Context & constraints

- **Reuse the `/messages` projection; never build a second one.** `GET /api/conversations/{id}/messages` returns
  `IAgentGrain.GetHistoryAsync()` (`src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:44`). That
  projection is load-bearing for search ordinals (ADR 0061) and stream recovery (ADR 0046), already joins tool calls
  to their results (ADR 0080, gotcha "A tool result never arrives on the message that made the call"), attaches
  artifact cards and hides an in-flight research turn (ADR 0082). The export serializes `ChatTurn` as-is.
- **Owner export keeps tool calls — that is not the share-link decision.** The public share route strips
  `ToolCalls` server-side because widening a public link is an ADR 0013 egress decision that was declined
  (`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`, the strip above `SharedConversationResponse`). The owner's own
  `/messages` already returns them, so a download to the owner is not third-party egress. The new ADR must state this
  explicitly so nobody later "fixes" export to match the share strip.
- **Reasoning cannot be exported — it is not persisted.** ADR 0071 strips `TextReasoningContent` before storage
  (`StripReasoningFromStorage`, `src/Agent42.Agents/Conversations/AgentGrain.cs`, comment near line 1140). The user asked for
  "full fidelity (tool calls, reasoning)": tool calls are in, reasoning is out by construction. Record in the ADR and
  the AC; do **not** change ADR 0071's strip to make it exportable (separate decision with 2 MB-session consequences,
  ADR 0004).
- **Document bytes are not stored.** An uploaded document is chunked and indexed, never kept as an attachment
  (ADR 0007, `src/Agent42.Backend/Attachments/AgentApi.Attachments.cs` `IngestDocumentAsync`). The ZIP therefore
  carries images (user uploads + generated images via `ChatTurn.Attachments` on assistant turns) and artifact
  versions only. `DocumentNames` has no grain getter — out of scope.
- **Gate on index membership — it is the incognito guard and the caller-supplied-id guard in one.** An ephemeral chat
  is never touched into `IConversationIndexGrain` (`ChatAsync` in `AgentApi.Conversations.cs`, ADR 0034). Every
  export route resolves the conversation through `GetConversationsAsync()` for the signed-in user first; an id not
  listed → **404 before any `IAgentGrain` is touched**. This keeps an incognito activation's in-memory history out of a
  file and stops export from activating arbitrary grain ids (gotcha "A caller-supplied conversation id needs THREE
  defences"). It also supplies title, agent name and `UpdatedAt` for the export. The frontend gate is the share
  dialog's own gate (step 5); the server gate is the one that matters.
- **Easy Auth: no exclusion needed, and none must be added.** `/api/conversations/*` and `/api/export` are not in
  `excludedPaths` (`infra/modules/container-app.bicep:371`), so they sit behind Easy Auth and `GetUserId()` is correct
  (rule 4, ADR 0042). Nothing under `/api/share/*` changes — no public export.
- **Bulk export is paged because one request cannot hold every activation.** Each conversation costs a grain
  activation, and `OnActivateAsync` connects the user's MCP servers sequentially with a 10 s timeout each;
  `POST /api/search/reindex` is bounded and resumable for exactly that reason
  (`src/Agent42.Backend/Search/AgentApi.Search.cs:50-83`, `ChatSearchLimits.ReindexBatchSize = 25`), and ACA's
  documented request timeout is 240 s (ADR 0089, `ClientToolLimits.cs`). Bulk export returns **one ZIP per batch**,
  with keyset paging on conversation id (`?after=<lastId>`), not `skip`. Why keyset rather than reindex's `skip`:
  offset paging over a list that gains/loses a conversation mid-sweep duplicates or **skips** one — reindex tolerates
  that (idempotent), an export that silently drops a chat does not.
- **Stream the ZIP; never buffer a whole archive in memory.** Use `System.IO.Compression.ZipArchive.CreateAsync` /
  `ZipArchiveEntry.OpenAsync` / `DisposeAsync` — these async APIs exist in the pinned SDK (verified 2026-10-10 in
  `Microsoft.NETCore.App.Ref/10.0.9` `System.IO.Compression.xml`). Kestrel and `TestServer` disallow synchronous IO;
  the sync `ZipArchive.Dispose()` writes the central directory synchronously and throws `InvalidOperationException`
  there — `ConversationExportApiTests` via `SharedApiFixture` (TestServer) is the proof. If async Create mode still
  issues a sync write on a non-seekable body, fall back to `FileBufferingWriteStream` (disk-buffered over its
  threshold) then `DrainBufferAsync`, and record the trap with `/gotcha`.
- **Gather before writing.** Once the first ZIP byte is flushed the status code is committed. All grain reads for a
  conversation happen before its entries are written; a missing blob is skipped and listed in `manifest.json` under
  `missing`; a conversation whose gather throws (bulk only) is listed under `errors` and the part continues. An
  exception after the response started calls `HttpContext.Abort()` so the browser shows a failed download rather than
  a silently truncated archive.
- **HTML artifacts lose their sandbox in a file.** In the app, `/frame` safety is the CSP `sandbox` header (gotcha
  "`/frame` safety is the CSP `sandbox` HEADER", ADR 0084). An exported `.html` opened from disk runs the model's
  script at a `file://` origin with no app cookies or API. Decision for the ADR: export versions as their raw source
  with the extension mapped from kind (`html`→`.html`, `svg`→`.svg`, `markdown`→`.md`, `code`→`.txt`), and record the
  `file://` execution as an accepted residual (user's own download, no app origin; same bytes `/content` already
  serves). Never apply `ArtifactTheme` to the exported source.
- **Continuous agents export what is retained.** A continuous agent's history is pruned to 24 h (ADR 0068, gotcha "A
  continuous agent's history is pruned to 24 h") — the export is that window. Say so in the Markdown header and in
  JSON (`historyMode`) when the binding shows a continuous agent.
- **Copilot chats need nothing special:** `GetHistoryAsync` already reads the mirrored history
  (`src/Agent42.Agents/Copilot/CopilotChatAgent.cs` doc comment, ADR 0085). The raw SessionFs transcript is out of scope.
- **Frontend rules.** Every `wwwroot/js/` MR bumps `CACHE` in `sw.js` (rule 3; currently `agent42-shell-v52`); a new
  module must be reachable from `main.js` and listed in `SHELL` (ADR 0041; gotchas "A frontend module unreachable from
  `main.js`…" and "The service worker's SHELL is all-or-nothing"). The SW never caches `/api`, so download URLs are
  untouched by it.
- **Why the per-conversation download lives in `#shareLinkDialog`, not a new topbar button.** The share dialog
  already has exactly the right gate — enabled iff the chat is persisted, has history and is not ephemeral, across all
  **four** transitions (gotcha "Topbar share availability: `startAgentChat` is a FOURTH transition";
  `TopbarActionsAssetsTests`) — and two entry points (topbar and sidebar row, `sidebar.js:153`). A new button would
  have to re-derive that gate and would be the next place it is missed. The dialog title becomes "Share or export".
- **Complexity:** S3776 fails the build at 15 (rule 9, ADR 0050/0051). Keep endpoint bodies in named methods, not
  inline lambdas (a registration method sums its lambdas' scores — `AgentApi.Attachments.cs` comments on this), and the
  Markdown renderer as small per-part methods.
- **Exemplars to copy:** endpoint group `src/Agent42.Backend/Users/AgentApi.Users.cs`; composition root
  `src/Agent42.Backend/Api/AgentApi.cs`; API test `tests/Agent42.Tests/ArtifactsApiTests.cs` (`SharedApiFixture`,
  `X-MS-CLIENT-PRINCIPAL-ID` header, artifacts created via `FakeChatClient.CallToolPrompt("create_artifact", …)`);
  frontend module `wwwroot/js/conversations.js`; asset tests `tests/Agent42.Tests/ShareLinkAssetsTests.cs`,
  `tests/Agent42.Tests/ClientToolsAssetsTests.cs`.

## Wire contract

- `GET /api/conversations/{conversationId}/export?format=markdown|json|zip` — default `markdown`; unknown format →
  400 `text/plain`; unlisted conversation → 404. `Content-Disposition: attachment` with a sanitized
  `fileDownloadName` `{yyyy-MM-dd}-{title-slug}.{md|json|zip}` (title sanitized, capped at 80 chars, falls back to
  `conversation`). `X-Content-Type-Options: nosniff`, `Cache-Control: private, no-store`.
- `GET /api/export?after={conversationId?}` — one ZIP part of at most `ExportLimits.BulkBatchSize` conversations,
  ordered ordinally by id with `id > after`. Response headers `X-Export-Total` (listed conversations),
  `X-Export-Part` (1-based), `X-Export-Parts` (⌈Total / BulkBatchSize⌉, so the client never needs the batch size) and
  `X-Export-Next` (last id of this part; absent on the final part), set **before** the
  stream starts (part membership is decided up front, so all three are known). Zero conversations → 200 with a ZIP
  holding only `manifest.json`.
- ZIP layout (single): `conversation.md`, `conversation.json`, `attachments/{id}-{fileName}`,
  `artifacts/{artifactId}/v{n}.{ext}`, `manifest.json`. Bulk: the same under
  `conversations/{yyyy-MM-dd}-{slug}-{first8OfId}/`, plus one root `manifest.json`. Every entry name passes through
  `ExportFileNames` (no `..`, no path separators from user/model text, length-capped).
- JSON (camelCase via `JsonSerializerOptions.Web`, indented): `schema: "agent42.conversation-export/1"`, `exportedAt`,
  `conversation {id, title, updatedAt, agentId, agentName}`, `model`, `agent` (binding: name, kind, model,
  systemPrompt — or null), `sharedState` (ADR 0089 JSON object or null), `turns` (the `ChatTurn` list exactly as
  `/messages` returns it — **unchanged in both modes**), `artifacts` (`ArtifactRecord` list; version `attachmentId`
  stays `[JsonIgnore]`). In ZIP mode only, a separate top-level `files` map (`{"attachments": {id: path},
  "artifacts": {"{artifactId}/v{n}": path}}`) gives each file's path inside the archive; `ChatTurn` and
  `AttachmentRef` are records and are never wrapped or extended.
- `ExportLimits`: `BulkBatchSize = 25` (the same bound as `ChatSearchLimits.ReindexBatchSize`, for the same
  activation-cost reason; it also keeps step 4's `BulkBatchSize + 1` seed cheap) and `MarkdownToolResultChars = 2_000`.
- Markdown: header block (title, export date, agent, model), then per turn `## You` / `## {agentName ?? "Agent 42"}`;
  images as `![file](attachments/…)` in ZIP mode, `_[image: file]_` otherwise; tool calls in a `<details>` block
  (name, arguments fenced, result truncated to `ExportLimits.MarkdownToolResultChars` — JSON keeps it whole); artifact
  cards as a bullet list with the path in ZIP mode. Fences are always one backtick longer than the longest backtick
  run in their content.

## Steps

0. **Branch** `feature/conversation-export` from `main @ 2c1dbf3` (or rebase if main moved — re-check
   `GetHistoryAsync`, the share dialog and the `CACHE` value). Never commit to `main` (hard rule 1).
1. **Records, names, Markdown renderer (pure, no IO).** `Export/ConversationExport.cs`, `Export/ExportLimits.cs`,
   `Export/ExportFileNames.cs`, `Export/ConversationMarkdown.cs`. Tests: `tests/Agent42.Tests/ConversationMarkdownTests.cs`
   — fence escaping, image refs in both modes, `<details>` tool calls with result truncation, continuous-history note,
   title sanitization (separators, `..`, empty, length cap), `code`→`.txt` mapping.
   Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~ConversationMarkdownTests`.
   *Parallelizable with step 5's markup/CSS, not its JS.*
2. **Gatherer.** `Export/ConversationExporter.cs`: given `IGrainFactory`, `IAttachmentStore`, userId and a
   `ConversationInfo`, returns a `ConversationExport` by calling the five existing grain methods; opens
   attachment/artifact blobs lazily for the writer (`IAttachmentStore.OpenReadAsync(ownerKey, id)`). No new grain method.
   Verify: via step 3's tests (internal, no endpoint of its own).
3. **Single-conversation endpoint + ZIP writer.** `Export/ConversationZipWriter.cs` (async `ZipArchive`,
   gather-then-write, `missing`/`errors` manifest, `HttpContext.Abort()` after start), `Export/AgentApi.Export.cs`
   (`MapExportEndpoints`, named handler methods, index-membership gate); register in `Api/AgentApi.cs` and fix the
   route-count comment. Tests: `tests/Agent42.Tests/ConversationExportApiTests.cs` (`SharedApiFixture`, shape of
   `ArtifactsApiTests`) — markdown/json/zip for the owner; JSON contains a tool call with its result; ZIP contains the
   user image bytes and every artifact version's bytes and parses with `ZipArchive`; `Content-Disposition` filename;
   400 on unknown format; 404 for another user's id, for an ephemeral chat (sent via `POST /api/chat/{id}` with
   `ephemeral: true`, so not indexed), and for a never-seen id; a deleted blob lands in `manifest.missing` and the
   archive still opens; the request must not throw on sync IO (TestServer disallows it).
   Verify: `dotnet test Agent42.slnx --filter FullyQualifiedName~ConversationExportApiTests`.
   If the sync-IO case fails, apply the `FileBufferingWriteStream` fallback and `/gotcha` it.
4. **Bulk endpoint.** `GET /api/export?after=` in `AgentApi.Export.cs`, reusing step 3's writer per conversation
   under its folder plus the root manifest. Tests (same file): seed `BulkBatchSize + 1` conversations, walk
   `X-Export-Next` to the end, assert every listed id appears exactly once; assert headers; a conversation added
   **before** the cursor mid-walk causes no duplicate and no skip of others; one inserted **after** the cursor
   appears; one deleted mid-walk is simply absent with no error; an ephemeral chat never appears;
   another user's conversations never appear; empty account → manifest-only ZIP.
   Verify: same filter as step 3.
5. **Frontend.** `index.html`: in `#shareLinkDialog` add a "Download a copy" row with three anchors (Markdown, JSON,
   ZIP) and retitle the dialog "Share or export"; in `#panelGeneral` add an "Export all conversations" button with a
   status line. `dom.js` refs. `sharelinks.js`: set the three `href`s to
   `/api/conversations/{encodeURIComponent(id)}/export?format=…` inside `openShareLink` after its null guard (the
   dialog is never openable for an ephemeral/empty chat). New `export.js` (exported `wireExport()`, called from
   `main.js`): on click, loop `fetch('/api/export' + (next ? '?after=' + next : ''))`; for each part `blob()` → object
   URL → temporary `<a download="agent42-export-{date}-part-{n}-of-{N}.zip">` → click → revoke; follow
   `X-Export-Next`; show "Part n of N" from `X-Export-Part` / `X-Export-Parts`;
   disable the button while running; on a failed part stop with an error line. `sw.js`: add `"/js/export.js"` to
   `SHELL`, bump `CACHE` to the next number, and move **every** pin found by `grep -rn "agent42-shell-v" tests`
   (four files at plan time, see Affected areas).
   Tests: `tests/Agent42.Tests/ConversationExportAssetsTests.cs` (shape of `ShareLinkAssetsTests`) — dialog holds the
   three export anchors; `openShareLink` sets them after the null guard; `main.js` imports `export.js` and calls
   `wireExport`; `SHELL` lists `/js/export.js`; the bulk loop reads `X-Export-Next`. (The `CACHE` value itself is
   guarded by the existing pins, which this step moves to the new value.)
   Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ConversationExportAssetsTests|FullyQualifiedName~ClientToolsAssetsTests|FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~SearchAssetsTests|FullyQualifiedName~ResearchAssetsTests|FullyQualifiedName~ShareLinkAssetsTests|FullyQualifiedName~TopbarActionsAssetsTests|FullyQualifiedName~ProjectLayoutTests"`.
6. **Manual check in DevHost** (`dotnet run --project tools/Agent42.DevHost`): chat with an image and an artifact,
   download all three formats from the topbar share entry and the sidebar entry, run "Export all", open the ZIP and
   confirm the files open. Note that `openShareLink` bails while `streaming` (`sharelinks.js:48`), so export is
   unavailable mid-stream — expected; mention it in the ADR. Evidence for the MR, not a gate.
7. **Docs (`/decide`, `/sync-docs`).** New ADR "Owner export of conversations": formats and contents; tool calls kept
   (why different from ADR 0035/0080); reasoning absent (ADR 0071); document bytes absent (ADR 0007); index-membership
   gate as the incognito guard; keyset-paged bulk parts and why not one request or a background job; the `file://`
   HTML-artifact residual; not an ADR 0013 egress. Alternatives: one bulk request, background job writing to blob with
   a push link, client-side ZIP assembly (no build step, ADR 0041), separate topbar export button. Add it to the
   `CLAUDE.md` "Conversations" feature-map row. Run `dotnet test Agent42.slnx` and
   `dotnet format Agent42.slnx --verify-no-changes`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | Owner downloads one conversation as Markdown with title, turns, tool calls folded and images referenced | `Export_Markdown_ForOwner` + renderer unit tests | `ConversationExportApiTests.cs`, `ConversationMarkdownTests.cs` |
| AC2 | JSON export contains every `/messages` turn unchanged (in ZIP mode too; file paths live in a separate `files` map), including tool calls with results, artifacts metadata, agent binding, model and shared state, under `schema: agent42.conversation-export/1` | `Export_Json_MatchesMessagesProjection` | `ConversationExportApiTests.cs` |
| AC3 | ZIP contains `conversation.md`, `conversation.json`, every image attachment's bytes, every artifact version's raw source and a manifest; opens with `ZipArchive` | `Export_Zip_BundlesAttachmentsAndArtifactVersions` | `ConversationExportApiTests.cs` |
| AC4 | A missing blob is listed in `manifest.missing` and the archive stays valid | `Export_Zip_MissingBlob_IsListedNotFatal` | `ConversationExportApiTests.cs` |
| AC5 | An unlisted conversation (other user's, ephemeral, unknown id) → 404, never exported | `Export_Unlisted_Is404` (`[Theory]`, 3 rows) | `ConversationExportApiTests.cs` |
| AC6 | Unknown `format` → 400 | `Export_UnknownFormat_Is400` | `ConversationExportApiTests.cs` |
| AC7 | Download filename is sanitized; ZIP entry names never contain `..` or separators from titles/file names | `FileNames_AreSanitized`, `Export_Zip_EntryNamesAreSafe` | `ConversationMarkdownTests.cs`, `ConversationExportApiTests.cs` |
| AC8 | Bulk export walks all listed conversations exactly once across parts via `X-Export-Next`, including when conversations are added before/after the cursor or deleted mid-walk | `BulkExport_WalksEveryConversationOnce`, `BulkExport_KeysetSurvivesInsertMidWalk`, `BulkExport_InsertAfterCursorAppears`, `BulkExport_DeleteMidWalkIsSkippedQuietly` | `ConversationExportApiTests.cs` |
| AC9 | Bulk export excludes ephemeral chats and other users' chats; empty account gets a manifest-only ZIP | `BulkExport_IsOwnerScopedAndSkipsEphemeral`, `BulkExport_Empty` | `ConversationExportApiTests.cs` |
| AC10 | ZIP streaming does no synchronous IO (TestServer disallows it) | implicit in AC3/AC8 (fails with `InvalidOperationException` otherwise) | `ConversationExportApiTests.cs` |
| AC11 | Reasoning is not exported (not persisted, ADR 0071) and the ADR says so | `Export_Json_HasNoReasoningField` | `ConversationExportApiTests.cs` |
| AC12 | The share dialog offers the three downloads for the conversation it was opened for, inheriting its gate | `ShareDialog_HasExportLinks`, `OpenShareLink_SetsExportHrefsAfterNullGuard` | `ConversationExportAssetsTests.cs` |
| AC13 | Preferences → General has "Export all conversations" driven by `export.js`, which follows `X-Export-Next` | `ExportAll_IsWiredFromMain`, `ExportAll_FollowsNextHeader` | `ConversationExportAssetsTests.cs` |
| AC14 | `export.js` is in `SHELL`; `CACHE` is bumped and every pin moved to the new value | `ServiceWorker_ListsExportModule` + the four existing pins | `ConversationExportAssetsTests.cs`, `ClientToolsAssetsTests.cs`, `PwaAssetsTests.cs`, `SearchAssetsTests.cs`, `ResearchAssetsTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time.

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| build / tests / format | must pass | `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes` |

No new package — `System.IO.Compression` is in the shared framework.

## Out of scope

- Import/restore of an exported conversation.
- Reasoning text (not persisted, ADR 0071) — and changing that.
- Original RAG document bytes (never stored, ADR 0007) and the registered document-name list (no grain getter).
- Per-turn timestamps (`ChatTurn` has none; adding them is a projection change touching ADR 0061/0046 ordinals).
- Public/share-link export or anything under `/api/share/*`.
- Long-term memory (Hindsight, ADR 0056/0057) export.
- Copilot SessionFs raw transcript (ADR 0085).
- Scheduled/background exports, emailed exports, a single all-in-one bulk archive.
- Group names (only `ConversationInfo` fields are exported; `groupId` omitted).

## Review

Critic pass 2026-10-10 (fresh-context `critic` agent). All code claims checked held (grain methods, `OpenReadAsync`,
route count 75, `ZipArchive.CreateAsync` in 10.0.9, `excludedPaths`, share-dialog gate, gate thresholds).

| Finding | Severity | Handling |
|---|---|---|
| `CACHE` is pinned in four test files, not one; step 5's filter missed three | High | **Fixed** — Affected areas, step 5, verify filter and AC14 now name all four and the grep |
| AC2 "turns unchanged" contradicted "each attachment gets its `path`" (records can't carry it) | Medium | **Fixed** — paths moved to a separate top-level `files` map; turns unchanged in both modes |
| `BulkBatchSize` / `MarkdownToolResultChars` undefined; part count N ambiguous | Low | **Fixed** — 25 / 2 000; new `X-Export-Parts` header |
| No test for insert-after-cursor or delete mid-walk; 404 cases as one row | Low | **Fixed** — two tests added; `[Theory]` |
| Line refs off (43→44, ~1189→~1140) | Minor | **Fixed** |
| Export unavailable while streaming (`sharelinks.js:48`) | Minor | **Accepted** — noted in step 6 and for the ADR |
| Weak "CACHE is no longer v52" assertion | Minor | **Fixed** by dropping it; the moved pins guard the value |

## Implementation divergences

Executed 2026-10-10 on `feature/conversation-export` (no drift: HEAD was `2c1dbf3`).

- **The ZIP is buffered and then drained, not streamed directly.** The case the plan anticipated happened: even
  `ZipArchive.CreateAsync` / `OpenAsync` / `DisposeAsync` write synchronously, because the entry stream's
  `WrappedStream` has no `DisposeAsync`, so `DeflateStream.PurgeBuffers` calls a sync `Write`. TestServer threw in
  every ZIP test. Applied the plan's fallback: `FileBufferingWriteStream` (1 MB in memory via
  `ExportLimits.ZipMemoryThresholdBytes`, then a temp file), then `DrainBufferAsync`. Recorded with `/gotcha`. Side
  effect: a failure while *building* now returns a plain 500, because nothing has been sent yet. `HttpContext.Abort()`
  only covers a failure while draining.
- **Gatherer signature.** `ConversationExporter.GatherAsync(grains, ownerKey, info)` plus
  `FindListedAsync(grains, userId, id)` for the index gate. Blob bytes are read by `ConversationZipWriter`, one entry
  at a time, not by the gatherer.
- **Order of entries.** Blobs are written first and `conversation.md` / `conversation.json` last. That way the
  Markdown image links and the JSON `files` map name only files that are in the archive. A missing blob shows as
  `_[image: …]_`.
- **Date in names.** Download names and bulk folder names use the conversation's `UpdatedAt`, not the export date.
  The bulk part's own file name uses the export date. A bulk folder whose name clashes (same date, slug and first 8
  id characters) gets the full id appended.
- **`ExportLimits`** also holds `MaxSlugChars = 80`, `MaxFileNameChars = 100` and `ZipMemoryThresholdBytes`.
- **Extra files touched:** `tests/Agent42.Tests/ProjectLayoutTests.cs` and ADR 0040's folder inventory, which list
  the new `Export/` folder by name (`FeatureFolderSet_IsExactlyWhatTheAdrDescribes`).
- **ADR number** 0091 (next free on `main` at execution time).
- **Step 6** was smoke-tested over HTTP against DevHost (real Kestrel): all three formats, export-all headers and
  ZIP layout, and a 404 for an unknown id. The download links and the export-all button were not clicked in a
  browser. Their wiring is covered by `ConversationExportAssetsTests` only.
