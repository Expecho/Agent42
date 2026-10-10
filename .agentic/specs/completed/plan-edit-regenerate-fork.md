# Plan — Edit the last message, regenerate the last reply, fork a conversation

planned-against: main @ 2c1dbf3

## Overview

**Goal:** the three missing chat basics. **Edit** rewrites the last user message and re-runs the turn; **Regenerate** re-runs the last user message for a fresh reply; **Fork from here** copies the conversation up to and including a chosen exchange into a new listed conversation. The user chose a **hybrid** model (2026-10-10): edit and regenerate truncate the last turn **in place**; fork never rewrites the source.

**Why now:** the most-requested missing chat basics; the API has no endpoint that truncates or forks a conversation. The in-place history mutation they need has a proven precedent: the ADR 0068 retention prune and the ADR 0078 stopped-turn append both mutate the `TryGetInMemoryChatHistory` list in place.

## Affected areas (verified in the tree at 2c1dbf3)

| Area | Files |
|------|-------|
| Grain contract | `src/Agent42.Agents/Conversations/IAgentGrain.cs` (adds `TruncateLastTurnAsync`, `CreateForkAsync`, `InitializeFromForkAsync`) |
| Grain core / projection | `src/Agent42.Agents/Conversations/AgentGrain.cs` (`GetHistoryAsync` + projection helpers, the persist-site pipeline, `BindAgentAsync`'s rebuild shape) |
| New grain partial | `src/Agent42.Agents/Conversations/AgentGrain.EditFork.cs` (feature-sliced like `AgentGrain.Retention.cs`) |
| New boundary records | `src/Agent42.Agents/Conversations/ConversationFork.cs`, `TruncateOutcome` (flat `Agent42.Agents` namespace) |
| Search projection | `src/Agent42.Agents/Search/AgentGrain.Search.cs` (`_ephemeralOrdinals`, `ReconcileSearchIndexAfterPruneAsync`, `IndexNewTurnsForSearchAsync`) |
| Artifacts | `src/Agent42.Agents/Artifacts/AgentGrain.ArtifactTools.cs` (harvest (id, version) from create/update results; `ParseArtifactResult`), `ArtifactRecord.cs` |
| Attachment store | `src/Agent42.Agents/Attachments/IAttachmentStore.cs`, `InMemoryAttachmentStore.cs`, `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`; the test fakes `tests/Agent42.Tests/AttachmentRehydrateTests.cs` (`CountingAttachmentStore`) and `tests/Agent42.Tests/LiveModelChatTests.cs` (`DataUrlAttachmentStore`) also implement the interface |
| Endpoints | `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (`ChatAsync`, new fork route), `src/Agent42.Backend/Conversations/ChatContracts.cs` (`ChatRequest`, new `ForkRequest`/`ForkResponse`) |
| Frontend | `src/Agent42.Backend/wwwroot/js/thread.js` (bubble actions), `send.js` (`send(prompt, replay)`), `conversations.js` (`renderTurns`, `selectConversation`), `sidebar.js` (`refreshConversations`), `styles.css`, `sw.js` (`CACHE`, currently `agent42-shell-v52`) |
| `CACHE` pins | every test that pins the `sw.js` `CACHE` string — `grep -rn "agent42-shell-v" tests` before bumping (at plan time: `ClientToolsAssetsTests.cs`, `PwaAssetsTests.cs`, `SearchAssetsTests.cs`, `ResearchAssetsTests.cs`) |
| Docs | new ADR (`/decide`), `.agentic/gotchas.md`, `CLAUDE.md` feature map ("Conversations" row) |

## Context & constraints

### Investigation findings (with evidence)

1. **History is a mutable list; in-place mutation is the established pattern.** `_session.TryGetInMemoryChatHistory(out List<ChatMessage>)` returns the live list, and removing from it survives serialize → JSON → deserialize. Precedents: `RemoveLeadingTurns` in `Conversations/AgentGrain.Retention.cs`, `AppendStoppedTurnToSession` in `Conversations/AgentGrain.cs`, gotcha "A cancelled Agent Framework run persists NOTHING". **Copy `AgentGrain.Retention.cs`'s shape, mirrored for the tail.** A "turn" is a user message plus everything up to the next user message; removal is always whole turns, so a `FunctionCallContent` never loses its `FunctionResultContent`. Maintained by hand: `TurnAttachments` (positional, one per user turn, `ProjectUserTurn`), `GeneratedImages` (keyed by `AssistantImageId`), `_ephemeralOrdinals`, the search index.
2. **Every reader uses the `GetHistoryAsync()` projection.** It skips blank user turns and turns with neither text nor image; one user exchange can project to **several** assistant `ChatTurn`s (text before a tool call, plus the final text). Search ordinals and ADR 0046 recovery count this list (gotcha "Chat-search ordinals must come from ONE `GetHistoryAsync()` projection"). **Any mapping from a client-visible position to a session position must come from that same walk, never a second, parallel count.**
3. **No grain-state write while a run is in flight.** `_runsInFlight` (`SharedState/AgentGrain.SharedState.cs`) is counted at both run sites. `ChatAsync` suspends between `yield`s, so another grain call can interleave mid-turn (gotchas "A grain's turn-scoped field…" and "Shared state: … no write while a run is in flight"). Truncate and fork **refuse while `_runsInFlight > 0`**. A running research turn is additionally hidden by `VisibleHistory` (`Research/AgentGrain.Research.cs`).
4. **Blobs are addressed by owner key.** `BlobAttachmentStore` stores at `{ownerKey}/{id}`, `ownerKey = {userId}/{conversationId}`; the download proxy (`AgentApi.Attachments.cs`, `DownloadAttachmentAsync`) and the artifact routes (`AgentApi.Artifacts.cs`, `ServeArtifactVersionAsync`) both resolve by `(ownerKey, id)`. **A fork that merely referenced the source's blobs would 404 in the UI.** So a fork **copies** every blob it keeps (user attachments, generated images, artifact versions) to `{newOwnerKey}/{sameId}` with the **same id**, so nothing inside the serialized session needs rewriting: `assistantImageId` lives in message `AdditionalProperties`, and session image URLs are re-minted from `AttachmentRef.BlobPath` every turn (`RehydrateAttachmentUrlsAsync`). Only `AttachmentRef.BlobPath` changes. Purge never deletes blobs ("the blobs stay", `PurgeAsync`), so copies are never double-owned and deleting either conversation cannot break the other.
5. **Attachment ids are re-sendable within a conversation.** `ChatAsync` resolves `AttachmentIds` via `attachments.GetAsync(AgentKey(userId, conversationId), id)`. Edit and regenerate therefore re-send the original turn's ids, which `/messages` already exposes on `ChatTurn.Attachments`. No server-side recall of the removed turn is needed.
6. **The client already has a re-send path.** `send(prompt, replay)` in `send.js` (ADR 0087's `resendAfterReconnect`) re-sends with `replay.attachments` and leaves the composer alone. Extend `replay`; don't fork `send`.
7. **ADR 0046 recovery counts `.msg` elements against stored turns** (`recoverPersistedReply`, `priorTurns`). It is **not** 1:1 for live tool turns, and an errored live turn leaves a user bubble the server never stored. So the client always **re-renders the thread from a fresh `/messages`** before editing or regenerating, and fork compares DOM and server counts before trusting a DOM position (D4).
8. **Copilot kind (ADR 0085):** the model's real transcript is SessionFs; the grain session is only a mirror (gotcha "A Copilot AF session is only a POINTER"). Truncating or copying the mirror would silently diverge from what the model sees. **Refuse all three operations for `AgentKinds.IsCopilot`.**
9. **Persist-site pipeline** (`ChatCoreAsync`, `ReceiveReminder`, `GenerateImageTurnAsync`): `CompactArtifactCalls()` → `ApplyRetentionAsync()` → serialize → `WriteStateAsync()` → `ReconcileSearchIndexAfterPruneAsync(pruned)` → `IndexNewTurnsForSearchAsync()`. Truncate and fork writes reuse this order; passing `pruned: true` to the reconcile is what drops and re-projects the index (there is no delete-by-ordinal).
10. **Persona (ADR 0003, amended by 0082):** `AgentId`, `AgentName`, `SystemPrompt`, `DeploymentName`, `AgentKind`, `EnabledTools` are an immutable snapshot. `BindAgentAsync` shows the rebuild shape: snapshot the session, `BuildAgent(model)`, deserialize, then `StripToolHistory()` when the model can't use tools.
11. **No new persisted fields.** `AgentGrainState` is unchanged. The new records only cross the grain boundary: `[GenerateSerializer]` + `[Alias("Agent42.Agents.…")]` in the flat namespace (rule 1), no Cosmos golden document.

### Decisions (the user's, and this plan's)

- **D1 — hybrid (user, 2026-10-10).** Edit-last and regenerate-last truncate the **last session user turn** in place and re-run it. Fork creates a new conversation and never touches the source.
- **D2 — one request for edit and regenerate.** `POST /api/chat/{id}` gains trailing `ReplaceLastTurn` (bool), `ExpectedUserTurnCount` (int?) and `ExpectedLastUserTextHash` (string?). When set, the endpoint calls `TruncateLastTurnAsync` **as late as possible — after `GetAsync(definition)`, `TouchAsync` and `BindAgentAsync`, immediately before `ContentType` is set** (`AgentApi.Conversations.cs` ~line 160), so a throw in those earlier calls never loses the exchange; on refusal it answers 409 (stale count, run in flight, copilot, nothing to truncate) or 400 (`ReplaceLastTurn` without both token parts) with no body stream; then it streams the normal turn. The truncation is **written immediately** (when not ephemeral). If the following run fails, the last turn is gone and the client puts the text back in the composer. The grain also refuses (`NoModel`) when no model provider is configured, so the no-model early exit (`AgentGrain.cs` ~680-695) can never follow a truncation. Alternatives rejected: (a) a deferred write — it leaves a truncated in-memory session that the *next* unrelated write would persist anyway, less predictable, not safer; (b) snapshot the session and restore it if the following run fails — it needs the snapshot to live across two grain calls (truncate, then the run), i.e. a new turn-scoped field with the rule 6 reset ritual at every run site, for a failure the client already recovers from by restoring the text to the composer. Record both in the ADR.
- **D3 — concurrency token = projected user-turn count + a hash of the last projected user message's text.** `ExpectedUserTurnCount` (count of `role == "user"` entries in `GetHistoryAsync()`) plus `ExpectedLastUserTextHash` (hex SHA-256 of that message's text; the browser computes it with `crypto.subtle.digest`, available on HTTPS and localhost). A count alone can be replayed: a second tab loaded at count N, sending after a completed edit restored the count to N, would silently truncate the freshly generated turn. The hash makes the token an identity. A double-click or stale tab gets a 409, never a second truncation. Fork uses the same pair. The total-ordinal count was rejected because it differs between live and reloaded renders (finding 7). `Busy` is checked **before** the token is computed, so a count taken during a running research turn (when `VisibleHistory` hides it) is never compared.
- **D4 — fork boundary = a whole exchange.** "Fork from here" sits on assistant bubbles and sends `throughUserTurn` (0-based index among projected user turns) plus `expectedUserTurnCount`. The fork includes every session message up to the session user message that begins the *next* exchange. The client takes the index from the DOM only when the DOM's `.msg--user` count equals the server's projected user count; otherwise it re-renders from `/messages` and asks the user to choose again. Cutting mid-exchange was rejected (it can orphan a tool result).
- **D5 — what a fork carries.**
  - **Copied:** the persona snapshot verbatim (not re-resolved from the registry, ADR 0003), the model, `EnabledTools` (null stays null), the truncated session, the matching `TurnAttachments` prefix, the `GeneratedImages` referenced by kept messages, artifacts minus versions created by excluded messages. All kept blobs are copied.
  - **Not copied:** `ScheduledPrompts` (durable per grain); `DocumentNames` and the document index chunks (no copy API in `IDocumentIndex` — re-upload); `WebDocumentNames`/`WebDocumentUrls`; `FetchableUrls`/`SeededFetchableUrls` (a security allowlist — fail closed); `SharedStateJson` (no per-turn history, so the current value may describe exchanges the fork lacks); share link; pin.
  - **Index entry:** title = the source title **capped first to leave room for the suffix** (strip a trailing `…`, cut to `60 − " (fork)".Length`, re-add `…` if cut) then `" (fork)"` appended, so `ConversationIndexGrain.Cap` (60 chars + `…`, `ConversationIndexGrain.cs:23, 218`) never eats the suffix; the source's group (ADR 0073), the source's agent id and name. The new conversation id is generated **server-side** (`Guid.NewGuid().ToString()`, same shape as `crypto.randomUUID()`), never caller-supplied (gotcha "A caller-supplied conversation id needs THREE defences").
- **D6 — artifacts on truncation.** Remove exactly the `(artifactId, version)` pairs harvested from create/update results **in the removed messages**, and drop a record left with no versions. "Keep only what retained history references" was rejected: a continuous chat's retention prune already drops early turns while their artifact records stay, so that rule would delete live artifacts. Blobs of dropped versions are orphaned (the accepted purge gap).
- **D7 — ephemeral (ADR 0034).** Edit and regenerate work in an incognito chat: with `ephemeral: true` the truncation is **in memory only** (no write, no index call); it drops `_ephemeralOrdinals` at or above the new projected count and clamps `_indexedTurnCount`. Fork requires the source to be **listed** in `IConversationIndexGrain`, otherwise 404 — one check that refuses incognito, non-existent and foreign ids. The UI hides Fork in incognito.
- **D8 — continuous agent chats (ADR 0016/0068).** Edit and regenerate are allowed (tail truncation never interacts with the leading-prefix prune). Fork is allowed; the fork gets a random id, so it is an ordinary per-chat conversation bound to the same persona snapshot and **not** retention-windowed.
- **D9 — refusal set for all three:** `_runsInFlight > 0`; `AgentKinds.IsCopilot(AgentKind)`; a stale count; and (truncate) a last session user turn that does not project (blank). Research-kind chats are allowed once their turn has finished.
- **D11 — this narrows ADR 0087's rejected alternative, and the ADR must say so.** `0087-resend-the-turn-after-an-mcp-reconnect.md:34-36` rejected "rewind or replace the failed turn on the server" as high risk because it rewrites the stored session of a grain whose history can grow without bound (ADR 0016/0068). This plan does rewrite the stored session. The new ADR records that it **narrows** (not reverses) that rejection, and why the risk is now acceptable: the rewrite is user-initiated and explicit, tail-only and whole-turn (the exact mirror of the ADR 0068 prune that already mutates the same list in place), refused while any run is in flight, and guarded by a concurrency token. ADR 0087's own automatic resend stays as it is (Out of scope). Add `amended_by` / a body note on ADR 0087.
- **D10 — needs an ADR** recording D1–D9 and D11 with the rejected alternatives. Next free number at execution time (parallel plans may claim 0091).

### Governing ADRs and gotchas

ADRs 0001, 0003 (+0082 amendment), 0004, 0016, 0034, 0035 (share links are live views — a truncation is visible to share viewers; residual), 0036, 0046, 0061, 0068, 0070 (the `mcp-` id shape; the server-generated fork id sidesteps its three defences), 0073, 0078, 0080, 0082, 0084, 0085, **0087 (narrowed — D11)**, 0089 (`.agentic/decisions/`). Gotchas in `.agentic/gotchas.md`: "An agent tool's RETURN value is persisted…", "A continuous agent's history is pruned to 24 h…", "A grain's turn-scoped field…", "Ephemeral chat: `_ephemeral`…", "Chat-search ordinals must come from ONE projection", "After a retention prune, REMAP `_ephemeralOrdinals`", "A pruned-turn delta is a PROJECTED-turn count", "A cancelled Agent Framework run persists NOTHING", "Shared state: … no write while a run is in flight", "A Copilot AF session is only a POINTER", "`LoopAgent` persists each iteration MID-TURN", "Every MR that touches `wwwroot/js/` must bump `CACHE`", "A frontend module unreachable from `main.js`…".

None of the operations sets a turn-scoped field; they are not model runs, so the reset ritual (rule 6) is untouched — don't add them to it.

## Steps

1. **Projection refactor (no behaviour change).** In `Conversations/AgentGrain.cs`, extract the `GetHistoryAsync` walk into a private `ProjectHistory()` that returns each `ChatTurn` with the **session user-turn index** it belongs to; `GetHistoryAsync` maps to `ChatTurn`. Add `ProjectedUserTurnCount()` and `SessionUserTurnForProjectedUserTurn(int)`, both built on that walk only.
   *Verify:* `dotnet test --filter "FullyQualifiedName~AgentGrainTests|FullyQualifiedName~ChatSearchIndexingTests|FullyQualifiedName~StopGenerationTests"` stays green.
2. **Attachment copy.** Add `Task<AttachmentRef> CopyAsync(AttachmentRef source, string targetOwnerKey, CancellationToken ct = default)` to `IAttachmentStore` — keeps `source.Id`, content type and file-name metadata; returns a ref with `BlobPath = {target}/{id}`. Implement in `InMemoryAttachmentStore`; in `BlobAttachmentStore` as download → upload (no SAS or copy-auth dependency; mirror `SaveAsync`'s headers, metadata and Azurite `CreateIfNotExistsAsync`); add to the two test fakes.
   *Verify:* a new test in `tests/Agent42.Tests/AttachmentTests.cs`: a copied in-memory ref opens under the target key with identical bytes, type and name, and the source still opens.
3. **Boundary records.** `ConversationFork` (persona fields, `DeploymentName`, `EnabledTools`, serialized session string, `TurnAttachments`, `GeneratedImages`, `Artifacts`, source owner key) and `TruncateOutcome` (`Truncated`, `Stale`, `Busy`, `Unsupported`, `Nothing`, `NoModel`), both `[GenerateSerializer]` + `[Alias]` in the flat namespace. The three new `IAgentGrain` methods also get `[Alias("…")]` like their neighbours.
   *Verify:* `dotnet build Agent42.slnx`; `GrainIdentityTests` / `SerializationCompatibilityTests` green.
4. **`TruncateLastTurnAsync(int expectedUserTurnCount, string expectedLastUserTextHash, bool ephemeral)`** in new `Conversations/AgentGrain.EditFork.cs`: D9 refusals; remove the last whole session turn, its `GeneratedImages` and its artifact versions (D6), and **truncate `TurnAttachments` to the new session user-turn count** (`RemoveRange(newCount, Count - newCount)` when longer). Never "remove the last entry": `BuildUserTurnAsync` (`AgentGrain.cs:1321-1322`) appends the entry before the run, so a run that failed without storing its user message leaves an orphan trailing entry, and removing only the last one would keep the real turn's refs and mis-join the next projection. Ephemeral → D7. Otherwise drop `_ephemeralOrdinals` at or above the new count, then the persist pipeline with `ReconcileSearchIndexAfterPruneAsync(true)` then `IndexNewTurnsForSearchAsync()`. Keep each helper under S3776.
   *Verify:* grain tests in new `tests/Agent42.Tests/EditRegenerateForkTests.cs` (ACs 1–7, 11, 12).
5. **Chat endpoint flag.** Trailing `ReplaceLastTurn` / `ExpectedUserTurnCount` on `ChatRequest` (positional callers keep compiling). In `ChatAsync`, truncate immediately before `ContentType` is set (after `GetAsync(definition)`, `TouchAsync`, `BindAgentAsync`) and map the outcome to 400/409 there.
   *Verify:* API tests in new `tests/Agent42.Tests/ConversationEditForkApiTests.cs` (shape `SharedApiFixture` + `SharedStateApiTests`): stale count → 409 with no stream; another user's conversation cannot be truncated.
6. **Fork, grain side.**
   - `CreateForkAsync(int throughUserTurn, int expectedUserTurnCount, string expectedLastUserTextHash)` on the source: D9 refusals; builds the payload from a **copy** of the session (`SerializeSessionAsync` → `DeserializeSessionAsync` → `RemoveRange` → serialize), never `_session` itself; filters artifacts and images per D5/D6.
   - `InitializeFromForkAsync(ConversationFork)` on the target: refuses unless it has no `SerializedSession` and no `AgentId`; copies blobs via `CopyAsync` (step 2) — artifact versions hold only `AttachmentId` (`ArtifactRecord.cs:50`), so first `GetAsync(sourceOwnerKey, id)` to build the `AttachmentRef`; a null (dangling version) is dropped from the fork's artifact record and logged, never fatal; sets state fields; resolves `_modelId` with the same empty-factory rule as `OnActivateAsync`; rebuilds as `BindAgentAsync` does (including `StripToolHistory` for a no-tools model); deserializes the session; `CompactArtifactCalls()`; writes; then `IndexNewTurnsForSearchAsync()`.
   *Verify:* grain tests (ACs 8, 10, 13, 14) in `EditRegenerateForkTests.cs`.
7. **Fork endpoint.** `POST /api/conversations/{conversationId}/fork`, body `ForkRequest(int ThroughUserTurn, int ExpectedUserTurnCount, string ExpectedLastUserTextHash)`: (1) 404 when the source isn't listed in the user's `IConversationIndexGrain`; (2) `CreateForkAsync` (409 on refusal); (3) new id server-side; (4) `InitializeFromForkAsync`; (5) **then** `TouchAsync(newId, ForkTitle(sourceTitle), agentId, agentName)` (the D5 capping rule, a small pure helper) and `SetGroupAsync` when the source is grouped; (6) 201 `ForkResponse(conversationId)`. The order makes a failure leave an unlisted grain, never a listed empty conversation (the ADR 0070 shape). Register inside `MapConversationEndpoints` (no new `AgentApi.*` partial, rule 7); update the route-count sentence in `Api/AgentApi.cs`'s doc comment.
   *Verify:* API tests (ACs 9, 15, 16).
8. **Frontend.**
   - `thread.js`: an action row on the last user bubble (✎ Edit — inline textarea + Send/Cancel), the last agent bubble (↻ Regenerate) and every agent bubble (⑂ Fork). All hidden while `streaming`, in a copilot chat (`activeAgentKind === "copilot"`) and offline; Fork also hidden while `ephemeral`.
   - Edit/regenerate: fetch `/messages`, re-render the thread from it **minus the last exchange** (via the `conversations.js` renderer), then `send(text, { attachments, replaceLastTurn: true, expectedUserTurnCount, expectedLastUserTextHash })`. On a non-OK response, restore the text to the composer and show the server's message.
   - Fork: compare the DOM `.msg--user` count with the server count (D4), call the fork route, `refreshConversations()`, then `selectConversation(newId)`.
   - Bump `CACHE` in `sw.js` and move **every** pin (see Affected areas). Any new module must be imported from `main.js`.
   *Verify:* new `tests/Agent42.Tests/EditRegenerateForkAssetsTests.cs` (shape `PinChatAssetsTests`), plus `dotnet test --filter "FullyQualifiedName~PwaAssetsTests|FullyQualifiedName~ClientToolsAssetsTests|FullyQualifiedName~SearchAssetsTests|FullyQualifiedName~ResearchAssetsTests"`, plus a manual pass with `dotnet run --project tools/Agent42.DevHost`.
9. **Docs.** `/decide` the ADR (D1–D10, alternatives, residuals). `/gotcha` two entries: "a fork must copy blobs — both proxies resolve by owner key" and "truncation is whole session turns and re-anchors the search index and the ephemeral ordinals". Add the ADR number to the `CLAUDE.md` "Conversations" row. Add `amended_by` and a body note to ADR 0087 (D11). Update the route-count sentence in `Api/AgentApi.cs` (no test pins it — check by hand). `/sync-docs`.
   *Verify:* `DocPathCheckerTests`, `dotnet format Agent42.slnx --verify-no-changes`.

Steps 2 and 3 can run in parallel (different files; parallel execution needs worktree isolation). Steps 4 and 6 both edit `AgentGrain.EditFork.cs`, so they run sequentially. Step 8 depends only on the HTTP contracts from steps 5 and 7.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|----|------|------|
| 1 | Regenerate removes the last whole turn (user + all assistant/tool messages); the re-run leaves exactly one copy of that exchange | `ReplaceLastTurn_ReplacesTheWholeLastExchange` | `EditRegenerateForkTests.cs` |
| 2 | Edit re-runs with the new text; the old text is gone from `/messages` and from the search index | `EditLastTurn_RemovesOldTextFromHistoryAndIndex` | `EditRegenerateForkTests.cs` |
| 3 | A stale token changes nothing (`Stale`) — a wrong count, and a right count with the wrong last-message hash (the replayed second-tab case) | `Truncate_StaleCount_ChangesNothing`, `Truncate_ReplayedCountWithOldHash_IsStale` |
| 3a | A blank (non-projecting) user turn before the chosen exchange does not shift fork or truncate positions; a blank last session user turn refuses truncate (`Nothing`) | `Fork_SkipsBlankUserTurns_InMapping`, `Truncate_BlankLastUserTurn_IsNothing` |
| 3b | No configured model refuses truncate (`NoModel`) before anything is removed | `Truncate_NoModel_IsRefused` |
| 3c | Edit on a retention-windowed continuous chat (`agent-` id) truncates and re-indexes correctly | `Truncate_RetentionWindowedChat_ReindexesCorrectly` | `EditRegenerateForkTests.cs` |
| 4 | Truncate during an in-flight run — a chat run **and** a `ReceiveReminder` run — is refused (`Busy`) and the run's turn persists intact | `Truncate_DuringARun_IsRefused` (`[Theory]`: chat, reminder) | `EditRegenerateForkTests.cs` |
| 5 | `TurnAttachments` stays aligned: an earlier turn's image still renders after truncation, including when the list carries an orphan trailing entry from a failed run | `Truncate_KeepsEarlierTurnImagesAligned`, `Truncate_WithOrphanTurnAttachment_StaysAligned` | `EditRegenerateForkTests.cs` |
| 6 | Generated images and artifact versions from the removed turn are removed; earlier versions remain servable | `Truncate_DropsOnlyTheRemovedTurnsImagesAndArtifactVersions` | `EditRegenerateForkTests.cs` |
| 7 | An ephemeral truncate writes nothing; the next normal turn is indexed while the removed incognito text never is | `EphemeralTruncate_WritesNothing_AndOrdinalsStayCorrect` | `EditRegenerateForkTests.cs` |
| 8 | A fork contains exactly the exchanges through `throughUserTurn`; the source is unchanged; the fork continues with a new turn | `Fork_CopiesThroughTheChosenExchange_AndLeavesSourceUntouched` | `EditRegenerateForkTests.cs` |
| 9 | A fork's user images, generated images and artifact versions are served under the **fork's** id (copied blobs) | `Fork_AttachmentsAndArtifacts_AreServedUnderTheForkId` | `ConversationEditForkApiTests.cs` |
| 10 | A fork copies the persona snapshot (agent id, name, prompt, model, kind, tools) without re-resolving a since-edited or deleted agent | `Fork_CopiesThePersonaSnapshot_NotTheCurrentAgent` | `EditRegenerateForkTests.cs` |
| 11 | Copilot-kind chats refuse truncate and fork | `CopilotChat_RefusesTruncateAndFork` | `EditRegenerateForkTests.cs` |
| 12 | A finished research turn truncates whole (no iteration messages left) | `ResearchTurn_TruncatesWhole` | `EditRegenerateForkTests.cs` |
| 13 | A fork of a continuous chat gets a non-`agent-` id and is not retention-windowed | `Fork_OfAContinuousChat_IsAPerChatConversation` | `EditRegenerateForkTests.cs` |
| 14 | `InitializeFromForkAsync` refuses a target that already has a session or binding; a fork into a no-tools model strips tool history; a dangling artifact version is dropped, not fatal | `InitializeFromFork_RefusesANonEmptyTarget`, `Fork_IntoNoToolsModel_StripsToolHistory`, `Fork_DanglingArtifactVersion_IsDropped` | `EditRegenerateForkTests.cs` |
| 15 | The fork endpoint 404s for an unlisted (incognito, unknown, other user's) source and 409s on a stale count | `Fork_UnlistedSource_Is404`, `Fork_StaleCount_Is409` | `ConversationEditForkApiTests.cs` |
| 16 | The fork is listed with the "(fork)" title (kept even for a source title at the 60-char cap or ending in `…`), the source's group and agent label, unpinned and unshared | `Fork_IsListedInTheSourcesGroup`, `ForkTitle_KeepsSuffixForLongTitles` (`[Theory]`) | `ConversationEditForkApiTests.cs` |
| 17 | The chat endpoint answers 409 (no NDJSON) on a stale `ReplaceLastTurn`, and 400 without both token parts | `ReplaceLastTurn_Stale_Is409`, `ReplaceLastTurn_WithoutCount_Is400` | `ConversationEditForkApiTests.cs` |
| 18 | `CopyAsync` preserves id, bytes, type and name and leaves the source | `CopyAsync_PreservesTheAttachment` | `AttachmentTests.cs` |
| 19 | The UI wires edit/regenerate through `send(..., { replaceLastTurn, expectedUserTurnCount })` after a `/messages` re-render; hides actions for copilot and while streaming; hides Fork in incognito; posts the fork route and opens the new chat; `CACHE` bumped with every pin moved | `EditRegenerate_ReRendersThenReplays`, `Actions_HiddenForCopilotStreamingAndIncognitoFork`, `Fork_PostsTheForkRoute_AndOpensTheNewChat` + the existing `CACHE` pins | `EditRegenerateForkAssetsTests.cs`, `PwaAssetsTests.cs`, `ClientToolsAssetsTests.cs`, `SearchAssetsTests.cs`, `ResearchAssetsTests.cs` |
| 20 | History, search, stop and retention behaviour is unchanged by the projection refactor | existing suites | `AgentGrainTests.cs`, `ChatSearchIndexingTests.cs`, `StopGenerationTests.cs`, `ResearchAgentTests.cs` |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Tool |
|------|-----------|------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build (`dotnet build Agent42.slnx`) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| Branch | never commit to `main` (`branches.protected`) | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

Also `dotnet test Agent42.slnx` and `dotnet format Agent42.slnx --verify-no-changes`. Coverage risk: `BlobAttachmentStore.CopyAsync` may have no automated test without an Azurite harness. If the gate fails on it, report it and cover or justify — never estimate the number.

## Out of scope

- Editing or regenerating **any earlier** message in place (D1: only the last turn; use Fork for earlier points).
- A branch tree / version switcher on one conversation ("< 2/3 >") — a fork is a separate conversation.
- Copying RAG documents or index chunks into a fork, or carrying over fetch allowlists, shared state, reminders or share links (D5).
- Un-retaining long-term memory (ADR 0056: no delete path) — an edited/regenerated turn's original text stays in Hindsight. Residual for the ADR.
- Cancelling reminders a removed turn scheduled — they keep firing until cancelled. Residual for the ADR.
- Deleting orphaned blobs of removed turns or artifact versions (the existing purge gap).
- Retitling a conversation when its first message is edited (`TouchAsync` only bumps the timestamp on an existing entry).
- Removing user-text URLs from the fetch allowlist on truncation (`RememberFetchableUrlsInUserText`, `AgentGrain.cs:1273-1276`) — an edited-away URL stays fetchable in that conversation. Residual for the ADR.
- Faster re-indexing: truncation re-projects the whole conversation to the search index (`pruned: true`) before the first stream byte; bounded by the 2 MB session. Residual for the ADR.
- Hiding a truncation from share-link viewers (ADR 0035 links are live views). Residual for the ADR.
- Changing ADR 0087's resend-after-reconnect to replace the failed turn (could now use `ReplaceLastTurn`; follow-up).
- Edit, regenerate or fork for Copilot-kind chats (D9) — waits until the transcript and the mirror can be truncated together.

## Review

Critic pass 2026-10-10 (fresh-context `critic` agent). No blocking defects; all requested code claims held (in-place list mutation precedents, persist pipeline order, `ReconcileSearchIndexAfterPruneAsync` semantics, owner-key blob layout, `PurgeAsync` keeps blobs, the four `IAttachmentStore` implementers, the four `CACHE` pins, all test classes and gotcha headings). `VisibleHistory` does not affect counts once `Busy` is checked first.

| # | Finding | Severity | Handling |
|---|---|---|---|
| 1 | Contradicts ADR 0087's rejected "rewind/replace the turn on the server" without naming it | Medium-high | **Fixed** — D11: the ADR narrows 0087's rejection, with reasons; 0087 added to governing ADRs and gets `amended_by` |
| 2 | "Remove the last `TurnAttachments` entry" mis-joins when a failed run left an orphan entry | Medium | **Fixed** — truncate to the session user-turn count; orphan-entry AC |
| 3 | Truncate before `TouchAsync`/`BindAgentAsync` and before the no-model exit loses the exchange on early failure; snapshot/restore unconsidered | Medium | **Fixed** — truncate moved immediately before `ContentType`; `NoModel` refusal; snapshot/restore recorded as rejected with reason |
| 4 | `"{title} (fork)"` gets cut by the 60-char index cap | Medium | **Fixed** — cap first, then append; long-title theory test |
| 5 | Blank-user-turn mapping untested; count token replayable by a stale tab | Medium | **Fixed** — ACs 3a; token is now count + SHA-256 of the last user text |
| 6 | No AC for retention-windowed edit, no-tools fork, reminder `Busy` | Low-med | **Fixed** — ACs 3c, 4 theory, 14 |
| 7 | Artifact version → `AttachmentRef` lookup; grain-method aliases; untested route count | Low | **Fixed** — spelled out in steps 3, 6, 9 |
| 8 | Fetch-allowlist and synchronous re-index residuals | Low | **Accepted as residuals** — Out of scope + ADR |
| 9 | Governing list missing 0087/0070; share links are live | Low | **Fixed** |
| — | Step 8 has only source-text asset tests, no behavioural JS test | Info | **Accepted** — matches the `PinChatAssetsTests` precedent; manual DevHost pass covers behaviour |

## Implementation divergences (2026-10-10)

Executed on `feature/edit-regenerate-fork`. HEAD had moved one commit past `planned-against` (ADR 0091 export).
That commit touched only `sw.js` `CACHE`, the route-count comment and the CACHE pins, so the plan held. Two
mechanical consequences: the ADR is **0092**, and `CACHE` went v53 → v54.

- **Outcome enum:** it is `HistoryEditOutcome` (`Ok`, `Stale`, `Busy`, `Unsupported`, `Nothing`, `NoModel`), not
  `TruncateOutcome`. Fork shares it, so "Truncated" would have been the wrong name. `CreateForkAsync` returns a
  `ConversationForkResult(Outcome, Fork?)` wrapper. Both live in `Conversations/ConversationFork.cs`.
- **Projection helpers:** step 1's `ProjectedUserTurnCount()` and `SessionUserTurnForProjectedUserTurn(int)` were not
  added as separate helpers. `ProjectHistory()` returns `ProjectedTurn(Turn, SessionUserTurn)`, and the edit/fork
  slice reads the count and the mapping from that list directly. It is the same single walk.
- **Frontend module:** the actions live in a new module, `js/editfork.js` (`wireEditFork` in `main.js`, in `SHELL`,
  in the `ProjectLayoutTests` wire table), not in `thread.js`. The actions need `conversations.js`, `send.js` and
  `sidebar.js`, and `thread.js` sits below all three. `renderTurns` is now exported for the repaint.
- **Action visibility:** the buttons are an always-visible quiet row (`.msg__actions`), not a hover reveal (the
  touch gotcha). Edit is on the last user bubble, Regenerate on the last agent bubble, Fork on every agent bubble.
- **AC4, reminder case:** the reminder test asserts `Stale`, not `Busy`. The grain is non-reentrant, so a truncate
  queues behind a fired reminder and then finds the token stale. `Busy` is reachable only between the yields of a
  streaming chat run, which the chat test holds open with `confirm_action`. A gate on the fake client's
  non-streaming path (`GetResponseAsync`) was added to keep the reminder in flight.
- **AC6:** it is split into three tests (artifact versions, the creating turn, generated images), plus fork
  counterparts.
- **Artifact version numbering:** a new version now follows the LAST version, not the count. Without that, a
  fork that drops a dangling version would reissue a taken number. The "versions 1 to N" model-facing messages
  now list the actual numbers.
- **Critic pass (2026-10-10), all fixed:**
  - A client `ephemeral` flag on a conversation with a stored record now still persists the truncation
    (`EphemeralFlag_OnAPersistedChat_StillPersistsTheTruncation`).
  - A fork of a source that served an incognito turn on its activation is refused `Unsupported`
    (`Fork_OfAConversationHoldingIncognitoTurns_IsRefused`).
  - A failed artifact blob copy drops that version instead of failing the fork.
  - The purge-gap orphaning is now noted in the gotcha.
- **Manual pass:** the Chrome extension was not connected, so the UI could not be driven. DevHost was smoked over
  HTTP instead: regenerate → 200 and replaced, replayed stale token → 409 with the server's message, fork → 201,
  listed as "… (fork)" with the prefix only, and `editfork.js` served. A click-through in a browser is still owed.
