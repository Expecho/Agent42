# Plan — RAG document Q&A on Cosmos vector search

planned-against: main @ 122bc90

## Overview

Let a user upload a document (PDF / Office / text) into a conversation and ask
questions grounded in its contents. Ingestion extracts text, chunks it, embeds the
chunks with Azure OpenAI, and stores them in an **Azure Cosmos DB for NoSQL vector
container**. Retrieval is **agentic**: the agent gains a `search_documents` tool it
calls when a question needs document context, and its answers **cite the source**
(file + page/chunk). This is the "document RAG" that ADR 0004 explicitly deferred as
"a separate future plan" — so this plan carries a new ADR (0007).

Why now: images already flow through the attachment plumbing (ADR 0004); documents are
the natural next attachment type, and the vector-store abstractions the Agent Framework
uses are GA.

## Decisions locked with the user (this planning session)

- **Vector store:** Cosmos DB for NoSQL (already operated; no new stateful service).
- **Extraction:** local libraries — PdfPig (PDF) + DocumentFormat.OpenXml (docx/pptx/xlsx)
  + plain read (txt/md). No Azure Document Intelligence (rejected: new resource + cost;
  scanned-PDF/OCR is out of scope).
- **Retrieval:** a `search_documents` **tool** (agentic RAG), consistent with the existing
  `schedule_*` tools — *not* an always-on `AIContextProvider`. Rejected auto-inject because
  it queries every turn and can inject irrelevant context.
- **Citations:** in scope — chunks carry source metadata, the tool returns it, and the
  agent is instructed to cite it.
- **Document scope:** per-conversation (reuses the `{userId}/{conversationId}` owner-key
  scoping, ADR 0001). A cross-conversation per-user library is **out of scope**.
- **Ingestion timing:** synchronous within the upload request (personal-scale, small docs).
  Background/async ingestion with progress streaming is out of scope.
- **Documents are ingest-only, NOT chat attachments.** A document is extracted+indexed at
  upload and reaches the model *only* via the `search_documents` tool — it is never turned into
  a user-message content part. This keeps documents entirely off the image `UriContent` path
  (see constraint 8).

## Context & constraints (what the executor can't cheaply re-derive)

1. **Cosmos vector search is an account capability that is NOT enabled.** `infra/modules/cosmos.bicep`
   line 38 lists only `EnableServerless`. The account needs the NoSQL vector-search capability
   added, plus a new container carrying a `vectorEmbeddingPolicy` + vector index. This edits a
   **stateful, region-pinned, `disableLocalAuth` account** — treat as sensitive.
   Evidence: `infra/modules/cosmos.bicep`; gotcha "Cosmos containers must be pre-declared in Bicep".

2. **1536-dim embeddings force `quantizedFlat` (or DiskANN), never `flat`.** Cosmos `flat`
   vector index caps at 505 dims; `text-embedding-3-large` is 1536. At per-conversation scale
   (<1000 vectors) `quantizedFlat` falls back to a full scan — acceptable (slightly higher RU),
   and correct for this scale. Evidence: Microsoft Learn "Vector search in Azure Cosmos DB for NoSQL".

3. **The embedding model deployment is a MANUAL prerequisite the user owns.** The AI Foundry
   account `Agent42` lives in RG `agent42` (a different RG), not this repo's Bicep — the same
   reason the OpenAI RBAC grant is a manual `az rest` step. A `text-embedding-3-large` deployment
   must be created there by the user. **No new RBAC:** the container app identity's existing
   *Cognitive Services OpenAI User* grant already authorizes embeddings on that account.
   Evidence: gotcha "OpenAI role grant is a CLI step in the deploy workflow, not Bicep";
   `src/Agent42.Backend/Program.cs` (single `DefaultAzureCredential` for the account).

4. **Container pre-creation vs connector create-on-use is a real risk.** Orleans runs its Cosmos
   providers with `IsResourceCreationEnabled = false` because AAD data-plane RBAC can't create
   containers (403). Any vector-store client that tries to `EnsureCollectionExists` on first use
   will hit the same wall. The container schema (partition key, vector path) declared in Bicep must
   exactly match what the store implementation reads/writes. **Step 1 is a spike to settle this**
   before committing the prod implementation. Evidence: `src/Agent42.Backend/Program.cs` L88-110;
   gotcha "Cosmos containers must be pre-declared in Bicep".

5. **The offline harness must keep working without Azure.** DevHost uses a canned `IChatClient`,
   in-memory grain storage, and `InMemoryAttachmentStore`; tests use the same. RAG therefore needs
   the **same interface + prod-impl + in-memory-impl shape as `IAttachmentStore`** (exemplar:
   `src/Agent42.Agents/IAttachmentStore.cs` + `InMemoryAttachmentStore.cs` + `BlobAttachmentStore.cs`):
   an `IDocumentIndex` with an in-memory impl and a Cosmos impl, and an `IEmbeddingGenerator` that is
   real (Azure) in Backend and a deterministic fake in DevHost/tests.
   Evidence: `tools/Agent42.DevHost/Program.cs` L20-39 (fake-service registration).

6. **Cosmos emulator vector support is uncertain** for the local full-stack AppHost. This matters
   because `src/Agent42.AppHost/AppHost.cs` runs the **real** `Agent42.Backend` against
   `AddAzureCosmosDB("cosmos").RunAsEmulator()` — so a naive unconditional `CosmosDocumentIndex`
   registration (mirroring how `Program.cs` L67 registers `BlobAttachmentStore`) would drive
   vector queries at the emulator during `dotnet run --project src/Agent42.AppHost`. Mitigation:
   `Program.cs` **branches the index registration** — `CosmosDocumentIndex` only when a real
   vector endpoint/capability is configured, else `InMemoryDocumentIndex` — exactly like the
   existing `blobConnection` branch (`Program.cs` L53-65). The emulator therefore never does vector
   search; the Cosmos path is prod-only (verified by the spike + a prod smoke, like ADR 0004's SAS
   behavior). AppHost provisions no `doc-chunks` container.

7. **Documents must be kept off the image `UriContent` path.** `AgentGrain.ChatAsync`
   (`AgentGrain.cs` L193-202) turns **every** resolved `AttachmentRef` into
   `new UriContent(url, mediaType)` unconditionally, and `RehydrateAttachmentUrlsAsync` (L499)
   re-mints SAS for each. Azure OpenAI expects *image* content there, so a `application/pdf`
   `UriContent` would 400. Therefore documents are **ingest-only**: they are not stored as
   `IAttachmentStore` chat attachments, their ids are never sent as `attachmentIds` on a chat turn
   (`app.js` L683), and `ChatAsync`/rehydrate never see them. Retrieval happens solely through the
   `search_documents` tool. Evidence: `src/Agent42.Agents/AgentGrain.cs` L193-202, L499;
   `src/Agent42.Backend/wwwroot/app.js` L448, L683.

8. **Governing docs:** ADR 0001 (grain-per-conversation owner-key scoping — attachments/documents
   reuse it), ADR 0004 (attachments-in-blob — this plan is its deferred sequel; **additive, not a
   supersession**), gotcha "Image attachment bytes must stay out of the session" (documents follow
   the same rule: raw bytes → blob, only references/derived chunks are persisted; chunk *text* lives
   in the Cosmos vector item, not the 2 MB session item).

## Affected areas (verified against the tree)

- **New abstractions (Agents):** `IDocumentIndex` + `InMemoryDocumentIndex`, `IDocumentTextExtractor`
  + impl, `DocumentChunker`, chunk/citation records — placed to mirror `IAttachmentStore`.
- **`src/Agent42.Agents/AgentGrain.cs`** — inject `IDocumentIndex` + `IEmbeddingGenerator`
  (constructor L34-44, fields L25-28); add a `search_documents` `AIFunctionFactory.Create` tool
  (alongside L68-92) and append it to the `Tools` array (L114); extend instructions to require
  citations. **Do NOT touch the `UriContent` construction (L193-202) — documents never enter it**
  (constraint 7). Add owner-scoped chunk cleanup to `PurgeAsync` (L311-329) so deleting a
  conversation removes its indexed text (Minor: privacy).
- **`src/Agent42.Backend/AgentApi.cs`** — the upload handler (L93-120) branches on media type:
  images keep the existing `IAttachmentStore` path; document MIME types (relaxing the `image/*`
  check at L105-109) go to the **ingestion service and are NOT registered as chat attachments**,
  so their ids are never sent back as `attachmentIds` (constraint 7). Keep the 10 MB
  `MaxAttachmentBytes` cap (L19). Return a document-kind marker on the metadata (L227) so the
  frontend renders a chip, not a thumbnail, and does not add it to `pendingAttachments`.
- **New (Backend):** `CosmosDocumentIndex` (prod impl, mirrors `BlobAttachmentStore`) and a
  `DocumentIngestionService` (extract → chunk → embed → upsert).
- **`src/Agent42.Backend/Program.cs`** — construct an `AzureOpenAIClient` (none exists today;
  `new AzureOpenAIClient(new Uri(endpoint), credential)` reusing the `credential` at L38) and
  register `IEmbeddingGenerator<string, Embedding<float>>` from `GetEmbeddingClient(...)`;
  **branch** the `IDocumentIndex` registration between `CosmosDocumentIndex` and
  `InMemoryDocumentIndex` on whether a real vector endpoint/capability is configured (mirror the
  `blobConnection` branch L53-65, constraint 6); new config key
  `AzureOpenAI:EmbeddingDeploymentName`, eager-validated.
- **`src/Agent42.AppHost/AppHost.cs`** — no new resource, but the reason `Program.cs` must branch:
  the emulated full-stack run must land on `InMemoryDocumentIndex`, never the emulator's vector path
  (constraint 6).
- **`infra/main.bicep` + `infra/modules/container-app.bicep`** — thread
  `azureOpenAIEmbeddingDeploymentName` to the container-app env as
  `AzureOpenAI__EmbeddingDeploymentName`, mirroring how `DeploymentName` is threaded
  (`main.bicep` -> `container-app.bicep` L150). Without this, eager validation crashes the prod
  container.
- **`src/Agent42.Backend/appsettings.json`** — add `AzureOpenAI:EmbeddingDeploymentName`
  (mirroring `DeploymentName`).
- **`tools/Agent42.DevHost/Program.cs`** — register the fake `IEmbeddingGenerator` +
  `InMemoryDocumentIndex` (alongside L20-32).
- **`tests/Agent42.Tests/TestSiloConfigurator.cs` (L19-26) + `FakeChatClient.cs`** — register the
  fake `IEmbeddingGenerator` + static-backed `InMemoryDocumentIndex` in the test silo; extend
  `FakeChatClient` to emit a `search_documents` `FunctionCallContent` (needed by AC5).
- **`infra/modules/cosmos.bicep`** — enable the vector-search capability; add the `doc-chunks`
  container with `vectorEmbeddingPolicy` + `quantizedFlat` vector index; thread a container-name
  param (as `containerName`/`remindersContainerName` are threaded from `main.bicep`).
- **`src/Agent42.Backend/wwwroot/index.html` (L109), `app.js` (`handleFiles` L439-442, send L683)** —
  accept document MIME types; render a document chip; do NOT push documents into
  `pendingAttachments`/`attachmentIds` (constraint 7); light citation display.
- **`Directory.Packages.props`** — new package versions (resolved by tooling, never from memory).
- **Docs:** new ADR 0007; `CLAUDE.md` repo-notes; `.agentic/gotchas.md` (vector capability, manual
  embedding deployment, emulator caveat).

## Steps

> Package/version note (hard rule 4): every package is added with `dotnet add package X`
> (latest stable) and pinned in `Directory.Packages.props` — never a version from memory.
> Candidate packages: `UglyToad.PdfPig`, `DocumentFormat.OpenXml`,
> `Microsoft.Extensions.VectorData.Abstractions`, the Cosmos NoSQL vector connector
> (`Microsoft.SemanticKernel.Connectors.AzureCosmosDBNoSQL`) + its in-memory sibling, and the
> `Microsoft.Extensions.AI` embedding-generator surface (already transitively present via
> `Microsoft.Agents.AI`). The Cosmos connector may be replaced by a thin raw-`CosmosClient`
> impl per Step 1's outcome.

1. **[spike] De-risk the Cosmos vector container** (`/spike-dotnet`, throwaway file outside the
   tree). Confirm: (a) a container created with a `vectorEmbeddingPolicy`(path `/embedding`,
   float32, cosine, 1536) + `quantizedFlat` index can be **read/written via managed-identity
   data-plane with no create rights**; (b) a `VectorDistance()` top-k query filtered by an owner
   partition returns results; (c) whether the chosen connector insists on creating the container
   (→ if so, prod impl uses raw `CosmosClient`). *Output:* the exact container JSON for Bicep + the
   connector-vs-raw-SDK decision. **Goal (a) cannot be validated on the Cosmos emulator** — the
   emulator uses a well-known key and does not enforce AAD data-plane RBAC, so it can't reproduce the
   403-on-create / `disableLocalAuth` wall (constraint 4). Honest de-risking of (a) needs a real
   scratch Cosmos account with the app identity's RBAC + an ARM-created container; the emulator can
   only smoke-test the query shape (b/c). Record which environment each sub-goal ran against.
   **Blocks Steps 9-10 only**; Steps 2-8 proceed in parallel. Verify: spike prints top-k hits for a
   known query against a real account.

2. **[parallel] Text extraction** — `IDocumentTextExtractor` + impl returning ordered
   `(text, page?)` segments: PdfPig per-page (real page numbers), OpenXml for docx/pptx/xlsx
   (chunk-index granularity — no true pages), plain UTF-8 for txt/md. Files touched: new files in
   `Agent42.Agents`. Verify: `DocumentTextExtractorTests` over small committed fixtures (one per
   type) asserts extracted text + page attribution.

3. **[parallel] Chunking** — `DocumentChunker` splits segments into overlapping chunks
   (~1000 chars / ~200 overlap, tunable consts) each carrying `{fileName, page?, chunkIndex, text}`.
   Verify: `DocumentChunkerTests` (boundary/overlap/metadata-propagation).

4. **[parallel] Index abstraction** — `IDocumentIndex` (`UpsertChunksAsync(ownerKey, chunks+embeddings)`,
   `SearchAsync(ownerKey, queryEmbedding, topK)`) + `InMemoryDocumentIndex` (static
   `ConcurrentDictionary`, cosine top-k, owner isolation — mirrors `InMemoryAttachmentStore`).
   Verify: `DocumentIndexTests` (cosine ranking correctness + one owner can't read another's
   chunks). Include `DeleteAsync(ownerKey)` on the interface (used by `PurgeAsync` in Step 6).

5. **Ingestion service + upload wiring** — `DocumentIngestionService` (extract → chunk → embed via
   `IEmbeddingGenerator` → `UpsertChunksAsync`). The `AgentApi.cs` upload handler (L93-120)
   **branches on media type**: images keep the existing `IAttachmentStore` path; document MIME types
   (relaxing the check at L105-109) go to ingestion and are **NOT registered as chat attachments** —
   the response marks them document-kind so the frontend never sends their ids as `attachmentIds`
   (constraint 7). Keep the 10 MB cap. Depends on 2,3,4. Verify: `AgentApiTests` — uploading a PDF
   returns 200 + a document-kind marker, the chunks are searchable via the in-memory index (fake
   embedding generator), and the returned id is not a resolvable `AttachmentRef`.

6. **Retrieval tool in the grain** — inject `IDocumentIndex` + `IEmbeddingGenerator` into `AgentGrain`
   (ctor L34-44); add `search_documents(query)` (`AIFunctionFactory.Create`, alongside L68-92),
   embed the query, return top-k chunk text **with citation metadata**; append to `Tools` (L114);
   extend `Instructions` to require citing returned sources. **Leave the `UriContent` path (L193-202)
   untouched** (constraint 7). Add `_documentIndex.DeleteAsync(ownerKey)` to `PurgeAsync` (L311-329)
   so conversation deletion removes indexed text. Depends on 4. Verify: `AgentGrainTests`
   (`AgentChat_UsesDocumentSearch_WithCitations`: TestCluster + fake embedding + in-memory index + a
   `FakeChatClient` that invokes the tool) asserts the tool is called and its result carries
   `{fileName, page/chunk}`; a purge test asserts chunks are gone after delete.

7. **Prod DI wiring** — `Program.cs`: construct `new AzureOpenAIClient(new Uri(endpoint), credential)`
   (none exists today; reuse the `credential` at L38), register
   `IEmbeddingGenerator<string, Embedding<float>>` from
   `GetEmbeddingClient(config["AzureOpenAI:EmbeddingDeploymentName"]).AsIEmbeddingGenerator()`, and
   **branch** `IDocumentIndex` between `CosmosDocumentIndex` and `InMemoryDocumentIndex` on config
   (constraint 6). Also thread `AzureOpenAI__EmbeddingDeploymentName` through `main.bicep` →
   `container-app.bicep` (L150) and add the key to `appsettings.json`, else prod eager-validation
   crashes (Major 3). Depends on 10. Verify: `dotnet build`; app starts with the embedding config
   eager-validated (like the chat client at L115-117); `az bicep build` on the infra change.

8. **Offline fakes** — DevHost `Program.cs` (L20-32 area) + test host: register a deterministic fake
   `IEmbeddingGenerator` (stable pseudo-vectors from text hash) + `InMemoryDocumentIndex`. Verify:
   `dotnet run --project tools/Agent42.DevHost` starts and a document upload → question round-trips
   offline; full `dotnet test` green.

9. **Infra — Bicep** — in `cosmos.bicep`: add the vector-search capability to the account; add the
   `doc-chunks` container with the `vectorEmbeddingPolicy` + `quantizedFlat` vector index from Step 1;
   thread a `docChunksContainerName` param from `main.bicep`. Depends on 1. Verify: `az bicep build`
   / `az deployment group what-if` (offer the command to the user; the agent does not deploy).

10. **`CosmosDocumentIndex` prod impl** — per Step 1: SK connector or raw `CosmosClient` doing a
    `VectorDistance` top-k SQL query filtered by the owner partition; **no create-on-use** (container
    pre-created in Bicep). Depends on 1,4. No unit test (prod-only, like the blob SAS behavior) —
    covered by the Step 1 spike + a prod smoke; documented in the ADR.

11. **[parallel] Frontend** — `index.html` L109 `accept` to include the document types; `app.js`
    `handleFiles` L441-442 to accept non-image types and render a document chip; surface the answer's
    citations lightly. Verify: `PwaAssetsTests` if applicable + manual `/verify` of the upload→ask flow.

12. **ADR + docs** — `/decide` ADR 0007 (Cosmos vector store; `quantizedFlat`/1536; agentic-tool
    retrieval; per-conversation; synchronous ingestion; manual embedding-deployment prerequisite);
    `/gotcha` for the manual embedding deployment + the vector-capability/container-pre-creation
    coupling + emulator caveat; update `CLAUDE.md` repo-notes. Verify: `/audit-docs` clean.

## Manual prerequisites (user action items — not code)

- Create a **`text-embedding-3-large`** deployment in the `Agent42` AI Foundry account (RG `agent42`).
  Confirm the deployment name and set `AzureOpenAI:EmbeddingDeploymentName`.
- Confirm **no new RBAC** is needed (existing *Cognitive Services OpenAI User* covers embeddings).
- The Bicep vector-capability change deploys via the normal pipeline; if enabling the capability on
  the existing account requires a portal/CLI toggle, the agent prepares the command and hands it over.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A PDF/docx/txt upload is accepted (not rejected as non-image) and returns metadata | `Upload_Document_IsAccepted` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC2 | Text is extracted per type with page attribution where available | `Extractor_*` (pdf/docx/txt fixtures) | `tests/Agent42.Tests/DocumentTextExtractorTests.cs` |
| AC3 | Text is chunked with overlap; each chunk carries source metadata | `Chunker_*` | `tests/Agent42.Tests/DocumentChunkerTests.cs` |
| AC4 | Retrieval returns top-k relevant chunks, scoped to the conversation (owner isolation) | `InMemoryDocumentIndex_*` | `tests/Agent42.Tests/DocumentIndexTests.cs` |
| AC5 | Asked about an uploaded doc, the agent calls `search_documents` and the tool result carries citations | `AgentChat_UsesDocumentSearch_WithCitations` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC6 | Full flow round-trips offline (canned LLM + fakes); suite green | existing suite + DevHost smoke | `tests/Agent42.Tests/*` |
| AC7 | Cosmos vector container works against a pre-created container via managed identity | spike + prod smoke (documented, non-unit) | `.agentic/decisions/0007-*.md` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml`)

- `new_issues`: 0
- `new_security_issues`: 0
- `duplication_pct_max`: 3
- `coverage_new_code_pct_min`: 80
- `cognitive_complexity_max`: 15
- `vulnerable_packages`: 0 (hard block)
- Branch policy: never commit to `main` — work on a `feature/*` branch + MR.

## Out of scope

- Cross-conversation / per-user document library (per-conversation only).
- Background/async ingestion + progress streaming (synchronous v1).
- Azure Document Intelligence / OCR for scanned or image-only PDFs.
- Hybrid (keyword + vector) search, re-ranking, query rewriting.
- Document deletion / re-index / management UI.
- Treating images as RAG documents (images keep the existing multimodal `UriContent` path).

## Review

A fresh-context `critic` reviewed this spec (took no part in writing it). Verdict: line anchors
all accurate, ADR 0004 correctly handled as additive, gotchas/gate thresholds correct — but three
**Major** holes in the affected-areas analysis, each a runtime breakage. All accepted and fixed:

- **Major 1 (accepted, fixed):** documents routed through the `/attachments` path would become
  image-style `UriContent` and 400 at Azure OpenAI. → Added **constraint 7** (documents are
  ingest-only, off the `UriContent`/`attachmentIds` path) and reworked Steps 5-6 + the AgentGrain/
  AgentApi/app.js affected-areas bullets accordingly.
- **Major 2 (accepted, fixed):** `AppHost.cs` runs the real Backend against the Cosmos emulator, so
  an unconditional `CosmosDocumentIndex` would drive vectors at the emulator — contradicting the
  "emulator never sees vectors" claim. → Rewrote **constraint 6** and Step 7 to **branch** the index
  registration (mirroring the `blobConnection` branch); added `AppHost.cs` to affected areas.
- **Major 3 (accepted, fixed):** the new eager-validated `EmbeddingDeploymentName` was never threaded
  through infra, so the prod container would fail to start. → Added `main.bicep` +
  `container-app.bicep` + `appsettings.json` to affected areas and Step 7.
- **Minor 4 (accepted, fixed):** conversation deletion would orphan extracted document text. → Added
  `IDocumentIndex.DeleteAsync(ownerKey)` (Step 4) called from `PurgeAsync` (Step 6) + a purge test.
- **Minor 5 (accepted, fixed):** the spike can't validate no-create-rights on the emulator. → Step 1
  now states goal (a) requires a real scratch Cosmos account; the emulator only smoke-tests the query.
- **Minor 6 (accepted, fixed):** test-host files were unnamed and AC4's test file drifted. → Added
  `TestSiloConfigurator.cs` + `FakeChatClient.cs` to affected areas; reconciled Step 4 to
  `DocumentIndexTests`.
- **Minor 7 (accepted, fixed):** `AzureOpenAIClient` doesn't exist in `Program.cs` today. → Step 7
  now constructs it explicitly.

No findings were rejected.

## Implementation notes — divergence from plan (execution 2026-07-09)

Implemented on branch `feature/rag-cosmos-document-qa`. Full suite green (79 tests), `dotnet format`
clean, `az bicep build` clean. Quality gate PASS (coverage 86.9% all new/changed files, 92.6%
excluding the prod-only `CosmosDocumentIndex`; 0 vulnerable packages). Critic: clean pass.

Divergences from the written plan:

- **PDF library: PdfPig → Docnet.Core 2.6.0.** No *stable* PdfPig is reachable from this
  environment's feeds (only prereleases + a corporate `1.7.0-custom-5` fork). Per a user decision
  ("different PDF library"), Docnet.Core (MIT, PDFium, stable, per-page text) was chosen after ruling
  out itext7 (AGPL) and PDFsharp (no text-extraction API). ADR 0007 alternatives + a new gotcha record
  it; `DocumentTextExtractor` serializes the process-global `DocLib.Instance` behind a lock.
- **Step 1 spike (connector-vs-raw): raw `CosmosClient`** chosen (documented in ADR 0007) to guarantee
  no create-on-use against the `disableLocalAuth` account. The spike's **goal (a)** (managed-identity
  read/write with no create rights) and the **prod smoke** (AC7) are NOT run this session — they
  require a real Cosmos account with the app identity's RBAC (the emulator can't reproduce the
  no-create wall). The exact `doc-chunks` container JSON is now in `cosmos.bicep`.
- **Index registration branch keyed on `Cosmos:DocChunksContainerName`** (threaded from Bicep in
  prod; absent under the AppHost emulator → in-memory index), satisfying constraint 6.
- **`DeterministicEmbeddingGenerator`** (Agents project, shared by DevHost + tests) uses FNV-1a
  bag-of-words hashing so term overlap → cosine similarity, making offline RAG retrieval meaningful.
- **`DocumentUploadResult`** (kind marker, no id) is the document upload response; the frontend routes
  it to a display-only `pendingDocuments` array (never an `attachmentId`).
- **Shared test helper `TestServiceRegistration.AddDocumentRagFakes()`** registers the RAG stand-ins
  across all four test hosts (avoids duplication). `AttachmentTests`/`AttachmentRehydrateTests` needed
  it because the grain ctor + upload endpoint now depend on the RAG services;
  `AttachmentTests.Upload_RejectsNonImageAndOversize` was renamed to `...UnsupportedTypeAndOversize`
  (text/plain is now accepted as a document, so it asserts rejection of `application/octet-stream`).
- **Test names** are stronger than the plan's AC→test table (e.g. `Upload_Document_IsAccepted` →
  `Upload_Document_IsAccepted_IndexedAndNotAnAttachment`); all ACs remain covered.

### Outstanding USER actions before prod works (not code)

1. Create a **`text-embedding-3-large`** deployment in the `Agent42` AI Foundry account (RG `agent42`);
   confirm its name matches `AzureOpenAI:EmbeddingDeploymentName`. No new RBAC needed.
2. Deploy the Bicep (adds `EnableNoSQLVectorSearch` + the `doc-chunks` container). Preview first with
   `az deployment group what-if -g rg-agent42 -f infra/main.bicep ...`. If enabling the account vector
   capability requires a portal/CLI toggle rather than the in-place Bicep update, run that once.
3. **Prod smoke (AC7):** upload a document in prod and ask a question; confirm `search_documents`
   returns cited chunks (validates the managed-identity no-create-rights read/write + `VectorDistance`
   ordering the offline harness can't exercise).
