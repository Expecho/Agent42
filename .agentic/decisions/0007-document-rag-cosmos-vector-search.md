---
id: 0007
date: 2026-07-09
status: accepted
tags: [rag, documents, cosmos, vector-search, retrieval, multimodal]
superseded_by: 0008   # partial — only the retrieval mechanism (agentic search_documents tool); storage/ingest/scoping remain authoritative
---

# Document Q&A via Cosmos vector search and an agentic search_documents tool

> **Amended by [ADR 0127](0127-self-hosting-local-storage-provider.md) (2026-11-14): with `Storage:Provider = local`, the document index is pgvector on Postgres (exact scan, no ANN index).** The Cosmos vector store below is the `azure` provider.

> **Amended by [ADR 0030](0030-user-configured-embedding-model.md) (2026-07-21) — embedding is no
> longer an operator prerequisite.** The `text-embedding-3-large` deployment is no longer a fixed,
> operator-owned model authenticated by `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName`; it is now a
> per-user model chosen in Preferences and resolved through the per-user chat-client factory. The
> ingest/chunk/owner-scoped Cosmos vector container (fixed at 3072 dims — ingest now validates-and-rejects
> a mismatch) is otherwise unchanged.

> **Superseded-by (partial): ADR 0008.** The *retrieval mechanism* below — the agentic
> `search_documents` tool and its "call the tool" instruction wording — is replaced by an always-on
> Microsoft Agent Framework `TextSearchProvider` (`BeforeAIInvoke`), gated on `DocumentNames.Count > 0`.
> Everything else in this ADR (ingest-only documents, the owner-scoped Cosmos NoSQL vector container with
> no create-on-use, extraction/chunking/embedding, prod-only vector path, `DocumentNames` awareness)
> **remains authoritative and accepted**. See ADR 0008 for the rationale that rebuts the "Always-on
> AIContextProvider auto-injection" alternative rejected below.

> **Amended by [ADR 0017](0017-foundry-provider-api-key-auth.md) (2026-07-16) — embedding credential only.**
> Where this ADR says the embedding client authenticates with the managed identity's *Cognitive Services
> OpenAI User* grant ("no new RBAC"), it now uses the Azure OpenAI **API key** (`AzureOpenAI:ApiKey`, the
> same key as chat) — no RBAC grant at all. The rest (embedding model, chunking, vector storage) is
> unchanged.

## Context
Users upload a document (PDF / Office / text) to a conversation and ask questions grounded in
it. This is the "document RAG" that ADR 0004 explicitly deferred as "a separate future plan".
Images already flow through the attachment plumbing (ADR 0004); documents are the natural next
upload type, but they are fundamentally different: a document must reach the model as *retrieved
text*, never as an image content part.

Two hard constraints shaped the design. (a) `AgentGrain.ChatAsync` turns every resolved
attachment into an image `UriContent`; a `application/pdf` `UriContent` would 400 at Azure OpenAI.
(b) The Cosmos account has `disableLocalAuth: true`, and AAD data-plane RBAC cannot create
containers — a store that tried to create-on-use would hit the same 403 wall as the Orleans
providers.

## Decision
> **Amended by [0098](0098-read-document-sequential-reading.md):** re-ingesting a file name replaces its chunks,
> and a selectable `read_document` tool reads a document's chunks in order.

Documents are **ingest-only**. On upload the backend extracts text, chunks it (~1000 chars,
~200 overlap), embeds each chunk with Azure OpenAI `text-embedding-3-large`, and upserts it into
an **Azure Cosmos DB for NoSQL vector container** (`doc-chunks`), partitioned by the
`{userId}/{conversationId}` grain key (ADR 0001 scoping). A document is **never** stored as a chat
attachment, never sent back as an `attachmentId`, and never enters the image `UriContent` path.

Retrieval is **agentic**: the grain gains a `search_documents` tool (alongside the `schedule_*`
tools) that embeds the query and returns the top-k chunks with citation metadata (file + page/chunk);
the agent is instructed to cite the source it used. Ingestion is **synchronous** within the upload
request (personal-scale, small documents).

Because a document is ingest-only and therefore **invisible to the model** (it is never in the chat
history), the model would otherwise have no signal a file was uploaded and would answer "I don't see a
file" instead of calling the tool. So on a successful ingest the upload endpoint registers the file
name with the grain (`AgentGrainState.DocumentNames`), and the grain **folds the document names into
its instructions** — telling the model the documents exist and to call `search_documents` for
questions about them. Without this awareness injection the agentic tool is unreliable.

Mirroring `IAttachmentStore`, an `IDocumentIndex` abstraction has a Cosmos impl for prod and an
in-memory impl for dev/tests, and the embedding generator is real (Azure) in the backend and a
deterministic bag-of-words fake offline — so the whole flow round-trips with no Azure.

- **Extraction is local libraries**: Docnet.Core (PDFium) for PDF with real per-page attribution,
  OpenXml for docx/pptx/xlsx (no true pages), plain UTF-8 for text. No Azure Document Intelligence.
- **The Cosmos impl uses the raw `CosmosClient`, not a vector connector**, so it can be guaranteed to
  *never* create the container on use — the container is pre-created in Bicep with its vector policy
  (a connector that insists on `EnsureCollectionExists` would 403 against the `disableLocalAuth`
  account). `VectorDistance` top-k SQL, single-partition by owner key.
- **The 3072-dim embedding forces a `quantizedFlat` vector index** (a `flat` index caps at 505 dims).
  At per-conversation scale (<1000 vectors) `quantizedFlat` is a full scan — correct and cheap.

## Alternatives considered
- **Documents as chat attachments (reuse ADR 0004's path)** — would become an image `UriContent`
  and 400 at Azure OpenAI. Rejected: documents are ingest-only, off the `UriContent`/`attachmentId`
  path entirely.
- **Always-on `AIContextProvider` auto-injection** instead of a tool — queries the index every turn
  and can inject irrelevant context. Rejected in favour of an agentic tool the model calls only when
  a question needs a document, consistent with the existing `schedule_*` tools.
- **A Semantic Kernel Cosmos vector connector** — convenient, but several connectors create the
  collection on first use, which 403s against a `disableLocalAuth` account. Rejected for a thin raw
  `CosmosClient` impl with no create-on-use.
- **PdfPig for PDF extraction** (the plan's candidate) — no *stable* version is reachable from this
  environment's package feeds: nuget.org exposes only prereleases and the corporate `Ibiscore` feed
  a `1.7.0-custom-5` fork, both SemVer-prerelease (violating the "pin latest stable" hard rule).
  Rejected for **Docnet.Core** (stable 2.6.0, MIT, PDFium-backed per-page text) after ruling out
  itext7 (AGPL) and PDFsharp/PdfSharpCore (no real text-extraction API). Docnet's cost is a
  process-global native singleton that must be serialized (see gotchas).
- **Azure Document Intelligence / OCR** — a new billed resource; scanned/image-only PDFs are out of
  scope. Rejected for local extraction libraries.
- **Azure AI Search** — a second stateful service; Cosmos vector search reuses an account already
  operated. Rejected.
- **`flat` vector index** — simpler, but caps at 505 dims and can't hold a 3072-dim embedding.
- **A cross-conversation per-user document library** — out of scope; documents are per-conversation.

## Consequences
- **Two manual prerequisites the user owns.** A `text-embedding-3-large` deployment must be created
  in the `Agent42` AI Foundry account (RG `agent42`); the container app identity's existing
  *Cognitive Services OpenAI User* grant already authorizes embeddings, so **no new RBAC**. The
  NoSQL vector-search capability is added to the existing account in Bicep — an in-place account
  update; if the platform requires a portal/CLI toggle instead, that is a one-time manual step.
- **New infra in `cosmos.bicep`**: the `EnableNoSQLVectorSearch` capability and the `doc-chunks`
  container (partition `/ownerKey`, vector path `/embedding` float32/cosine/3072, `quantizedFlat`
  index). `AzureOpenAI__EmbeddingDeploymentName` and `Cosmos__DocChunksContainerName` are threaded
  through `main.bicep` → `container-app.bicep`; the latter's presence is what switches prod onto the
  Cosmos index (absent under the AppHost emulator, which then uses the in-memory index and never
  runs a vector query).
- **The Cosmos vector path is prod-only** and, like ADR 0004's SAS re-fetch behavior, cannot be
  exercised by the offline unit harness (the emulator uses a well-known key and can't reproduce the
  `disableLocalAuth` no-create-rights wall). It is covered by a real-account spike + a prod smoke
  (AC7), not a unit test. The extractor, chunker, in-memory index, ingestion, and the `search_documents`
  tool are all unit-tested offline.
- Additive to ADR 0001 (grain-key scoping) and ADR 0004 (attachments-in-blob), not a supersession:
  raw document bytes still never enter the 2 MB session item; only derived chunk text lives in the
  vector store.
- **Out of scope**: background/async ingestion, OCR, hybrid/keyword search + re-ranking, document
  re-index/management UI, treating images as RAG documents.
