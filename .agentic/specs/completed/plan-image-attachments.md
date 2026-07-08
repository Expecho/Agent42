# Plan — Image attachments (multimodal prompts)

`planned-against: main @ a6d6603`

> **Phase 1 of 2.** This plan delivers *image* attachments end-to-end (attach → send to a
> multimodal model → persist for reload). *Document RAG* is deliberately deferred to a
> separate plan — see **Future work: document RAG** at the bottom. Scope confirmed with the
> user in the planning interview.

## Overview

Let a user attach one or more images to a prompt and send them to the conversation's
model for analysis. Today the composer is text-only: `ChatRequest.Message` is a bare
string and `AgentGrain.ChatAsync` sends the user turn as a bare `string` to
`RunStreamingAsync(message, _session)` (**`AgentGrain.cs:177`** — the site that must become
multimodal; the only existing `ChatMessage` construction is the *reminder* path,
`AgentGrain.cs:388`). The default deployment `gpt-4o` is already multimodal, and the
transitively-referenced `Microsoft.Extensions.AI` (pulled by `Microsoft.Agents.AI` 1.13.0;
version 10.6.0 confirmed in the package cache at
`Z:\Packages\.nuget\packages\microsoft.extensions.ai.abstractions\10.6.0`) already ships the
`TextContent`/`DataContent`/`UriContent` parts and the `ChatMessage(role, IList<AIContent>)`
constructor — so the work is plumbing images through the wire, the grain, storage, and the
UI, not new model capability.

Per the interview, images are **stored in Azure Blob storage and referenced**, not inlined
in grain state. Investigation showed *why this matters* (see constraints): a real phone
photo base64-encodes to several MB, and the Agent Framework `AgentSession` — the full
message history — is serialized into a single Cosmos item on every turn, where Cosmos
enforces a hard **2 MB per-item** limit. Inlining bytes would blow that limit on the first
real photo. Blob keeps the durable bytes out of the serialized session.

## Affected areas (verified against the tree)

**New files**
- `infra/modules/storage.bicep` — Storage account + private `attachments` container.
- `src/Agent42.Agents/IAttachmentStore.cs` — store **interface only** (mirror the plain-interface shape of `IChatClientFactory.cs`).
- `src/Agent42.Agents/AttachmentRef.cs` — the Orleans-serializable ref record that crosses the grain boundary. **Mirror `ChatTurn.cs`** (`[GenerateSerializer]` + `[Alias]` + `[property: Id(n)]`) — NOT the plain-interface shape (see constraint 9).
- `src/Agent42.Agents/InMemoryAttachmentStore.cs` — offline impl backed by **process-global static state** so DevHost and the two-container test harness share it (constraint 10).
- `src/Agent42.Backend/BlobAttachmentStore.cs` — prod blob-backed impl.
- `tests/Agent42.Tests/AttachmentTests.cs` — store + endpoint + grain coverage.
- `.agentic/decisions/0004-attachments-in-blob.md` — ADR (added as a plan step).

**Modified files**
- `src/Agent42.Backend/AgentApi.cs` — upload + display endpoints; `ChatRequest` gains `AttachmentIds`; chat handler passes attachments to the grain.
- `src/Agent42.Agents/IAgentGrain.cs` + `AgentGrain.cs` — `ChatAsync` gains an attachments parameter; grain builds a multimodal `ChatMessage`; `GetHistoryAsync` surfaces attachment refs.
- `src/Agent42.Agents/ChatTurn.cs` — add an optional `[property: Id(2)] IReadOnlyList<AttachmentRef>? Attachments` so a reopened chat re-renders images.
- `src/Agent42.Agents/AgentGrainState.cs` — add `[Id(6)]` (state currently ends at `[Id(5)]`, `AgentGrainState.cs:43`): an **ordered** per-user-turn list of attachment metadata (small: id, blob path, media type, filename), one entry per user turn so history can join by index (constraint 11).
- `src/Agent42.Backend/Program.cs` — register `BlobAttachmentStore` + `BlobServiceClient`.
- `tools/Agent42.DevHost/Program.cs` — register the in-memory store.
- `src/Agent42.AppHost/AppHost.cs` — add an Azure Storage (Azurite emulator) resource + reference.
- `infra/main.bicep` + `infra/modules/container-app.bicep` — wire the storage module, pass the blob endpoint as a container env var.
- `src/Agent42.Backend/wwwroot/{index.html,app.js,styles.css}` — attach control, thumbnails, image rendering.
- `Directory.Packages.props` — add `Azure.Storage.Blobs` (version resolved by tooling, not written from memory — hard rule 4).
- `CLAUDE.md` / `.agentic/gotchas.md` — doc updates (see steps).

## Context & constraints (what the executor can't cheaply re-derive)

1. **Session bytes must stay out of Cosmos (the core design driver).** The serialized
   `AgentSession` is written to the `agent-state` Cosmos item every turn
   (`AgentGrain.cs:206-208`); Cosmos caps items at 2 MB. Therefore the model must receive
   images in a form the session serializes as a *reference, not bytes*. Primary strategy:
   pass the image to the model as a **`UriContent` pointing at a blob user-delegation SAS
   URL** (a short string in history), never `DataContent` (raw bytes). This is the reason
   blob was chosen over inline; do not "simplify" it back to `DataContent` in the prod path.

2. **⚠ Unverified assumption — gate with a spike (Step 1).** Two things are not proven and
   change the design if false: (a) Azure OpenAI accepts a **private-blob user-delegation SAS
   URL** as image input; (b) in a multi-turn chat, whether AF re-sends and Azure OpenAI
   **re-fetches** a *prior* turn's image URL — which would fail once the SAS expires. Step 1
   is a `/spike-dotnet` that answers both against the real `gpt-4o` deployment before any
   production code is written. Branch on the outcome (recorded in the spec after the spike):
   - **URL accepted + prior-turn URLs not re-fetched** → primary strategy as-is; pick a SAS
     lifetime comfortably longer than a turn.
   - **Prior-turn URLs re-fetched** → on grain activation, refresh attachment SAS URLs in the
     deserialized session before use (rehydrate-on-read), OR mint long-lived SAS scoped to the
     blob. Still blob, still no bytes in Cosmos.
   - **SAS URL not accepted at all** → fall back to `DataContent` *for the sending turn only*,
     with an aggressive per-image size cap, and accept the documented Cosmos-size risk; record
     the reversal as an ADR consequence. (Least preferred.)

3. **RBAC is a manual step, not Bicep (hard rule 5 + [[iam-changes-need-explicit-user-execution]]).**
   The Cosmos data-plane grant works in Bicep only because Cosmos RBAC is a *distinct*
   resource type a Contributor can write (`infra/modules/cosmos-role-assignment.bicep:7-10`).
   **Storage Blob Data Contributor is classic `Microsoft.Authorization/roleAssignments`**,
   which a Contributor deploy SP cannot create. Mirror the OpenAI grant pattern
   (`gotchas.md` "OpenAI role grant is a CLI step"): the plan **prepares an exact
   `az role assignment create` command and hands it to the user** — the agent never executes
   it. The role also enables user-delegation SAS (`generateUserDelegationKey` is included in
   Storage Blob Data Contributor), so no account key is needed and `disableLocalAuth`-style
   key-free access is preserved.

4. **Offline dev + tests must stay Azure-free.** DevHost uses a canned client and in-memory
   grains (`tools/Agent42.DevHost/Program.cs`); tests use a fake client + Orleans TestCluster
   (`AgentApiTests.cs`, `AgentGrainTests.cs`). The store is therefore an **interface in the
   Agents project** (mirror `IChatClientFactory` + `SingleChatClientFactory` living together
   in `IChatClientFactory.cs`) with a blob impl in Backend and an in-memory impl for
   DevHost/tests. The canned/fake clients ignore image content, so the in-memory store may
   return `DataContent` — the "no bytes in Cosmos" property is a *prod-blob* concern only.

5. **Proving the image reached the model needs the fake to observe it** (gotchas.md "Testing
   that a bound persona reaches the model"). Extend `FakeChatClient` to also echo the count of
   non-text content parts it receives (e.g. a `[img:N]` marker), so a grain test can assert an
   image part actually arrived — a storage round-trip alone proves persistence, not delivery.

6. **Two different URLs for two different consumers.** The *model* needs a URL Azure OpenAI
   can fetch → **blob SAS** (the app sits behind Entra Easy Auth, so an authed proxy URL is
   unreachable by Azure OpenAI). The *browser* displays images via a first-party
   **`GET /api/conversations/{id}/attachments/{attachmentId}`** proxy (already authenticated
   by Easy Auth, user-scoped by grain key) — never a SAS in the page.

7. **Endpoint JSON contract (gotchas.md "bare string → text/plain").** The upload endpoint
   must return its metadata via `Results.Json(...)`; the frontend parses every response as
   JSON. The display endpoint returns binary with an explicit image content-type.

8. **Grain-key user scoping (ADR 0001).** Attachments are addressed through the per-user,
   per-conversation grain key `{userId}/{conversationId}` (`AgentApi.cs:128`), so one user
   can never fetch another's attachment. Enforce the same keying on upload/display.

9. **`AttachmentRef` follows a *different* exemplar than the store interface.** The
   *interface* `IAttachmentStore` mirrors `IChatClientFactory.cs` (plain interface). But the
   *data record* `AttachmentRef` crosses the grain boundary (returned inside `ChatTurn` from
   `GetHistoryAsync`, and passed into `ChatAsync`), so it MUST mirror `ChatTurn.cs:6-10`:
   `[GenerateSerializer]`, `[Alias("Agent42.Agents.AttachmentRef")]`, `[property: Id(n)]` on
   every member. A plain record (the interface's shape) will fail Orleans serialization at
   runtime, not compile time — an easy trap. `AgentGrainState`'s new field is additive
   `[Id(6)]`.

10. **The in-memory store must be process-global (the two-container test trap).** In
    production the Backend co-hosts the endpoints and the silo in one process, so a singleton
    store is shared. In tests they are **two separate DI containers**: `AgentApiTests.cs:30-39`
    builds the WebApplication (`_app`) hosting the endpoints, while the grain runs in a
    `TestCluster` silo configured separately by `TestSiloConfigurator.cs:15-25`. An
    instance-field-backed in-memory store registered in each would be *two different stores*,
    and the endpoint→grain path (upload in `_app`, read in the silo) would silently break —
    Step-2 unit tests pass, AC4 fails. Therefore `InMemoryAttachmentStore` must back its data
    with **static process-global state** (e.g. a `static ConcurrentDictionary`). Register it in
    both `_app.Services` and via the silo configurator.

11. **History↔attachment correlation, and the empty-text filter (AC7).**
    `GetHistoryAsync` (`AgentGrain.cs:222-236`) rebuilds `ChatTurn`s from `_session` messages
    and today drops any message with blank `.Text`. Two required fixes: (a) an **image-only
    user turn** (image attached, no typed text) has empty `.Text` and would be filtered out —
    change the filter to retain a user turn that has attachments even when its text is blank,
    or AC7 fails. (b) The session message carries a `UriContent` holding an *ephemeral SAS*,
    not the attachment id the browser needs — so history must join each user turn to the
    ordered per-turn attachment list in `AgentGrainState` (constraint from affected areas) **by
    user-turn index**. For the index to stay aligned, **every** user turn must append exactly
    one (possibly empty) entry — including the reminder-fired turn (`ReceiveReminder`,
    `AgentGrain.cs:388`), which must push an empty entry. This alignment is load-bearing and
    gets its own regression test (see AC table).

## Steps

1. **Spike: validate the SAS-URL model path (gate).** `/spike-dotnet`, pinned to the repo's
   package versions, against the real `gpt-4o` deployment: send an image to the model as a
   `UriContent` blob user-delegation SAS URL; then run a 2-turn conversation to observe
   whether a prior turn's URL is re-fetched. **Record the outcome in this spec's Review
   section and pick the branch from constraint 2 before proceeding.** *Verify:* spike prints
   the model's description of the image and the multi-turn behavior; no repo code changes.

2. **Storage abstraction + offline impl.** Add the `IAttachmentStore` interface (shape after
   `IChatClientFactory.cs`) and the `AttachmentRef` record (shape after `ChatTurn.cs` — see
   constraint 9). Add `InMemoryAttachmentStore` backed by **static process-global state**
   (constraint 10). *Verify:* new `AttachmentTests` unit test for the in-memory store
   (save → read round-trips bytes + metadata) passes: `dotnet test Agent42.slnx`.

3. **Grain: multimodal message + attachment persistence.** Extend `IAgentGrain.ChatAsync`
   with an attachments parameter (update the `[Alias]`'d interface + all callers); at
   `AgentGrain.cs:177` build a `ChatMessage(ChatRole.User, [TextContent, …image content])`
   via `IAttachmentStore` (inject it into the grain); append an ordered per-turn attachment
   entry to `AgentGrainState` `[Id(6)]` for **every** user turn — including an **empty** entry
   on the reminder path (`ReceiveReminder`) to keep the index aligned (constraint 11); fix the
   `GetHistoryAsync` filter to retain image-only user turns and join refs by user-turn index;
   add an `[img:N]` echo to `FakeChatClient` (extend the existing `InstructionsEcho` path,
   `FakeChatClient.cs:61-73`, counting non-text parts on the last user message). Depends on
   Step 2. *Verify:* `AgentGrainTests` — a turn with an attachment makes the fake report ≥1
   image part; `GetHistoryAsync` returns the ref on the correct turn; **and a
   text→reminder→image sequence keeps refs aligned to the right turns** (constraint 11 regression).

4. **Backend: upload + display endpoints + wire.** Add `POST /api/conversations/{id}/attachments`
   (multipart; validate `image/*` + size cap from a const; `Results.Json` metadata) and
   `GET /api/conversations/{id}/attachments/{attachmentId}` (streams bytes, user-scoped);
   add `AttachmentIds` to `ChatRequest` **as a trailing parameter with a default**
   (`AttachmentIds = null`, exactly as `AgentId = null` did at `AgentApi.cs:134`) so the many
   positional `new ChatRequest("hi", null)` callers in `AgentApiTests.cs` and the app.js body
   keep working; pass the ids through the chat handler; register `BlobAttachmentStore` +
   `BlobServiceClient` in `Program.cs` **[contingent — see Step 1; the prod blob path is only
   built if the spike does not select the DataContent fallback]**; register
   `InMemoryAttachmentStore` in DevHost. Depends on Step 3. *Verify:* `AttachmentTests`
   (endpoint-level, TestServer like `AgentApiTests`, sharing the static store per constraint
   10) — upload returns JSON metadata; oversize/non-image → 400; display streams the bytes
   back; a chat with the returned id persists an image turn visible via `GetHistoryAsync`.

5. **Frontend: attach control, thumbnails, rendering.** Add a file input + attach button to
   the composer (`index.html`), upload-on-select with thumbnail previews and remove, include
   attachment ids in the `send()` body, and render images in user messages both live and on
   reload (via the display endpoint), with styles. Depends on Step 4. *Verify:* manual `/run`
   in DevHost — attach an image, send, see it in the thread; reload the conversation and the
   image still renders. (Frontend is deliberately un-unit-tested, per repo convention.)

6. **Infra: storage module + RBAC command. [contingent — see Step 1]** Skip or reduce this
   step if the spike selects the DataContent fallback (blob may then be unnecessary). Otherwise:
   add `infra/modules/storage.bicep` (account + private `attachments` container,
   `minimalTlsVersion Tls12`, no public blob access), wire it in `main.bicep`, pass the blob
   endpoint as a container env var in `container-app.bicep`, and add the Azurite resource to
   `AppHost.cs`. **Prepare the exact `az role assignment create` command (Storage Blob Data
   Contributor for the container app's managed identity on the new account) and hand it to the
   user** — do not execute it (hard rule 5). Parallelizable with Steps 2–5 up to the point of
   local wiring. *Verify:* `dotnet build Agent42.slnx`; Bicep what-if is a manual/user step
   (not run by the agent against prod).

7. **Docs.** Write `.agentic/decisions/0004-attachments-in-blob.md` (`/decide`: blob +
   reference, images as SAS `UriContent`, alternatives = inline/AI-Search-later, informed by
   the Step 1 spike outcome); add a `/gotcha` for the "session bytes ↔ 2 MB Cosmos limit"
   constraint and the Storage-RBAC-is-manual constraint; update `CLAUDE.md` "Where knowledge
   lives"/repo notes if a new Azure resource warrants it; refresh the gotchas exemplar table
   if `BlobAttachmentStore` becomes the canonical store shape. *Verify:* `/audit-docs` clean;
   doc-path integrity check green.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | Uploading an image returns id + metadata as JSON | `Upload_ReturnsAttachmentMetadataAsJson` | `tests/Agent42.Tests/AttachmentTests.cs` |
| AC2 | Non-image or oversize upload is rejected (400) | `Upload_RejectsNonImageAndOversize` | `tests/Agent42.Tests/AttachmentTests.cs` |
| AC3 | A turn with attachment ids delivers image content to the model | `Chat_WithAttachment_DeliversImagePartToModel` (FakeChatClient `[img:N]`) | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4 | History for an image turn includes the attachment ref for re-render (endpoint→grain path over the shared static store, constraint 10) | `History_IncludesAttachmentRefs` | `tests/Agent42.Tests/AttachmentTests.cs` |
| AC4b | Refs stay joined to the correct turn across a text→reminder→image sequence (index alignment, constraint 11) | `History_KeepsAttachmentRefsAlignedAcrossTurns` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC4c | An image-only user turn (blank text) is not dropped from history | `History_KeepsImageOnlyTurn` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC5 | The display endpoint streams the stored bytes, user-scoped | `Display_StreamsStoredImageBytes` | `tests/Agent42.Tests/AttachmentTests.cs` |
| AC6 | Prod path keeps raw bytes out of the serialized session | Spike (Step 1) + code review of `BlobAttachmentStore` returning `UriContent` — **no automated unit test** (in-memory store returns `DataContent`; honestly out of unit scope) | Step 1 spike / review |
| AC7 | Attach → send → reload renders the image | Manual `/run` in DevHost (Step 5) | — |

*AC6/AC7 are honestly marked as not automated: AC6 is a prod-blob property the offline test
harness can't exercise, and the frontend is un-unit-tested by repo convention. Both are
verified out-of-band as noted.*

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80` (backend/grain/store code; frontend excluded by convention)
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block); new `Azure.Storage.Blobs` resolved latest-stable at add time
- Enforcement: local = soft warning (render table, proceed/abort on FAIL); CI = hard block on build, tests, `dotnet format`, vulnerable packages, doc-path integrity
- Branch: `feature/image-attachments` + MR — never commit to `main` (hard rule 1)

## Out of scope

- **Document RAG** (see below) — separate plan.
- Non-image attachments (PDF/audio/video) — images only this phase.
- Multi-replica / distributed Orleans clustering (unchanged; still single-replica per ADR 0001).
- Image generation, thumbnails-server-side, EXIF stripping, virus scanning, CDN.
- Editing/removing an attachment after a turn is sent (compose-time remove only).

## Future work: document RAG (next plan — do not build here)

Confirmed decisions from the interview to carry into the RAG plan:
- **Document scope: per-conversation** — a document belongs to the chat it was attached to;
  retrieval searches only that chat's docs. Extends the existing per-conversation `AgentGrain`
  rather than adding a per-user document-registry grain.
- **Vector store: undecided — the RAG plan chooses.** Record the tradeoff: Cosmos DB NoSQL
  vector search (reuses the existing account/identity, add a vector-indexed container, lowest
  cost, personal-scale) vs Azure AI Search (new resource + RBAC + cost, stronger hybrid/semantic
  retrieval). The transitively-referenced `Microsoft.Extensions.AI` already exposes
  `IEmbeddingGenerator` / `OpenAIEmbeddingGenerator`, but a **new embedding-model deployment** (e.g.
  `text-embedding-3-small`) in the AI Foundry account is required — new infra + a manual RBAC
  grant, same pattern as constraint 3.
- RAG additionally needs: document text extraction (PDF/Office — a new library dependency, no
  BCL option), chunking, per-turn retrieval + context injection (likely an agent tool or a
  pre-turn retrieval step), and a document-management UI. It is materially larger than this
  phase and warrants its own ADR.

## Review

**`critic` pass (pre-approval): not a clean pass — core design sound, 6 findings, all
accepted and fixed in this spec. None rejected.** The critic confirmed the core design
(blob + `UriContent` SAS, spike-gated) is ADR-consistent and adversarially sound (bytes stay
out of the session, 2 MB Cosmos cap accurate, multi-turn SAS re-fetch risk real, SW `/api`
bypass honored, Storage-RBAC-as-manual correct, `FakeChatClient` echo viable).

| # | Finding | Severity | Resolution |
|---|---------|----------|------------|
| 1 | In-memory store not shared across the two test DI containers (`_app` vs `TestCluster`) → AC4 endpoint→grain path silently breaks | HIGH | Added **constraint 10** + made `InMemoryAttachmentStore` static-process-global in Affected areas / Steps 2 & 4; AC4 note updated |
| 2 | `GetHistoryAsync` drops image-only turns; no ref↔turn correlation mechanism | MED | Added **constraint 11** (retain image-only turns, join by user-turn index, empty entry on reminder path); Step 3 rewritten; added AC4b/AC4c |
| 3 | `AttachmentRef` pointed at wrong exemplar (needs Orleans serializer attrs) | MED | Added **constraint 9**; split into its own file mirroring `ChatTurn.cs`; `AgentGrainState` `[Id(6)]` called out |
| 4 | `Microsoft.Extensions.AI 10.6.0` read as recalled version | MED | Re-cited as transitive-from-`Microsoft.Agents.AI 1.13.0` with package-cache evidence path (Overview + Future work) |
| 5 | Spike branch 3 (DataContent fallback) unwinds Steps 4 & 6, presented as fixed | LOW-MED | Marked the blob registration in Step 4 and all of Step 6 **[contingent — see Step 1]** |
| 6 | Wrong line cite (388 vs 177); `ChatRequest` back-compat unstated | LOW | Overview now cites `AgentGrain.cs:177`; Step 4 specifies `AttachmentIds = null` trailing default |

**Spike (Step 1) outcome (recorded 2026-07-08, real `gpt-4o` @ `agent42.services.ai.azure.com`):**

Ran two file-based-app spikes against the live deployment, using a throwaway storage account
(`rg-agent42-spike`, shared-key service SAS so **no RBAC was touched** — hard rule 5; RG deleted
after). Verbatim observations:

- **Q(a) — Azure OpenAI accepts a private-blob SAS URL as `UriContent`: YES.** Turn 1 sent a
  distinctive banded image (red top / blue bottom) as a `UriContent` pointing at a **private**
  container blob (an unauthenticated GET without the SAS returned 409 — confirmed private). The
  model replied *"The image is divided horizontally with red on the top half and blue on the
  bottom half."* — a description impossible to hallucinate, so the fetch genuinely happened.
- **Bytes stay out of the session: CONFIRMED.** The serialized `AgentSession` was **683 chars**
  and contained the SAS **URL string** (no base64 bytes) — the core Cosmos-size property holds.
- **Q(b) — a prior turn's image URL IS re-fetched after reactivation: YES.** After
  serialize→deserialize (simulating grain reactivation) and **deleting the blob**, turn 2 (a
  plain-text follow-up) threw `ClientResultException: HTTP 400 … Failed to download image from
  <the SAS URL>`. So AF re-sends prior-turn `UriContent` and Azure OpenAI re-fetches it every turn.
- **Rehydrate-on-read mechanism VALIDATED (spike 2).** On the reactivated session, patching the
  in-memory history's `UriContent.Uri` with a **freshly minted SAS** worked: after swapping the
  blob's bytes to a green/yellow image and re-signing, turn 2 ("describe the image again") replied
  *"This image has a green top half and a yellow bottom half."* — proving `UriContent.Uri` is
  settable, `TryGetInMemoryChatHistory` returns the **live** backing list AF re-sends, and the
  patched URL is genuinely re-fetched.

**Branch selected (constraint 2): rehydrate-on-read.** A truly long-lived SAS is not an option —
a *user-delegation* SAS (the key-free path constraint 3 preserves) is capped at **7 days** by the
user-delegation key lifetime, so a conversation reopened later would break on re-fetch. Therefore:

1. **`IAttachmentStore` gains a SAS-minting method** — e.g. `Uri GetReadUrl(AttachmentRef ref)` (or
   `string blobPath`) returning a fresh short-lived (≈1 h) read SAS. The prod `BlobAttachmentStore`
   mints a user-delegation SAS; `InMemoryAttachmentStore` returns its `DataContent`/data URL (offline
   path needs no refresh).
2. **`AgentGrain.OnActivateAsync` rehydrates after deserialize (new sub-step in Step 3):** walk
   `TryGetInMemoryChatHistory`, and for every `UriContent` whose URI points at our attachments
   container, replace `.Uri` with `IAttachmentStore.GetReadUrl(...)`. The blob path is recoverable
   from the stored URL (strip the query) **and** cross-checkable against the ordered per-turn
   attachment list in `AgentGrainState[Id(6)]` — use the state list as the source of truth for which
   blob each turn's attachment is (constraint 11 already persists it). This is a **prod-blob concern
   the offline unit harness cannot exercise** (in-memory store returns non-expiring URLs), so it is
   verified by this spike + the Step 5 manual `/run`, and called out in code review — same honesty
   bracket as AC6.

**Step 4/6 contingency RESOLVED: the blob path is taken (NOT the DataContent fallback).** Step 4's
`BlobAttachmentStore` + `BlobServiceClient` registration and **all of Step 6** (storage.bicep, AppHost
Azurite, RBAC command) are **required**, not skipped.

## Implementation notes (divergence from plan)

Implemented on `feature/image-attachments`. Where the build diverged from or extended the plan:

1. **Rehydrate is per-turn, not per-activation.** The spike outcome first said "rehydrate on
   activation." A pre-MR `critic` pass (HIGH) caught that an Orleans grain stays warm across many
   turns, so an activation-only refresh lets a 1 h SAS expire mid-conversation and 400 on a later
   turn. Fixed: `RehydrateAttachmentUrlsAsync` is called before **every** model call — from both
   `ChatAsync` and `ReceiveReminder`. Guarded by a regression test
   (`AttachmentRehydrateTests.PriorTurnImageUrl_IsRefreshedOnEveryTurn_NotJustOnActivation`, using
   a counting store since SAS expiry itself isn't observable offline). ADR 0004 + the gotcha were
   corrected to match.
2. **`IAttachmentStore` gained two methods beyond `GetReadUrlAsync`:** `GetAsync(ownerKey, id)`
   (the chat endpoint resolves client-sent ids into refs server-side, so a turn can't attach a
   forged blob path) and `OpenReadAsync` (the display proxy). `GetReadUrlAsync` returns a `Uri`
   the grain wraps in `UriContent`; the in-memory store returns a short placeholder URI.
3. **`AttachmentRef.BlobPath` is `[JsonIgnore]`** so the internal path never reaches the browser
   via the history endpoint; Orleans serialization keys on `[Id]`, so the grain wire form is
   unaffected. The upload endpoint returns a trimmed `AttachmentMetadata` DTO (id + media type +
   filename), not the ref.
4. **`BlobAttachmentStore` handles both prod and local Azurite:** identity client → user-delegation
   SAS (key-free, `GetReadUrlAsync`); shared-key client (Azurite) → service SAS + a one-time
   `CreateIfNotExists`. The storage account is provisioned key-free (`allowSharedKeyAccess: false`).
5. **Conversation title fallback:** an image-only first turn (blank text) titles the sidebar entry
   from the first attachment's filename (or "Image") instead of a blank string (critic LOW).
6. **`Aspire.Hosting.Azure.Storage` pinned to 13.3.0** to match the rest of the Aspire suite
   (tooling resolved 13.4.6; aligned down for suite consistency).
7. **Coverage:** testable new code is **92.2%** (>80 gate). `BlobAttachmentStore.cs` is 0% — the
   prod-only path with no Azure-free test surface (constraint 4), verified by the Step 1 spike +
   review. This is the AC6 honesty bracket; accepted by the user at the quality gate. Duplication
   and cognitive-complexity gates are SKIPPED (no measuring tool in the repo; self-reported only
   per `quality-gates.yaml`).
