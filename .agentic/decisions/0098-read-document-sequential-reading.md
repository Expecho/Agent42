---
id: 0098
date: 2026-10-17
status: accepted
tags: [documents, rag, tools, selectable-tools, compaction, session-size, cosmos, no-migration, no-egress]
supersedes: []
amends: [0007, 0066, 0084]
---

# `read_document`: sequential reading of ingested documents

> **Amended by [ADR 0106](0106-agentgrain-services-and-turn-context.md) (2026-10-19): the per-turn part budget counts on `TurnContext.DocumentPartsRead`.** It does not use a field, so it is no longer reset in each run entry.

## Context
Document RAG (ADR 0007/0008) answers targeted questions well and fails at whole-document tasks ("summarise all
of it", "list every requirement"), because the model only ever sees the top-K excerpts the always-on
`TextSearchProvider` injects. The chunks needed to read a document end to end already live in `doc-chunks`
with a document-global `chunkIndex`. Two facts made a naive reader wrong: every ingest restarts `chunkIndex` at 0
and gives each chunk a fresh id, so a re-upload of a file name left **two interleaving copies**, and consecutive
chunks overlap by up to `ChunkOverlap` (200) characters, so concatenation repeats about a fifth of the text.

## Decision
A selectable, model-callable tool `read_document(fileName, part = 1)` (key `read_document`) returns a
conversation document's chunks **in chunk order**, ten chunks per part, stitched into readable text.

- **Not a reversal of ADR 0008.** 0008 removed the agentic `search_documents` *retrieval* tool and rejected
  on-demand function calling for *retrieval*. This is sequential reading; the always-on provider is unchanged.
- **Index contract.** `IDocumentIndex` gains `ReadChunksAsync` (ordered, distinct by `chunkIndex`, first wins,
  plus the file's total) and `DeleteFileAsync`. On Cosmos the total is `MAX(chunkIndex) + 1` (not `COUNT`, which
  over-counts legacy duplicates; an empty `MAX` maps to 0) and the range query uses a **single-property**
  `ORDER BY c.chunkIndex`, served by the default range index, so there is no Bicep change. The SQL lives in public
  constants that `DocumentChunkQueryShapeTests` pins.
- **Re-ingest replaces (amends 0007).** `DocumentIngestionService` deletes a file name's chunks after embedding and
  the dimension check succeed and right before the upsert, on all three ingest paths. A failed re-ingest keeps the
  old copy. Reads stay defensive against duplicates already in prod, so no migration is needed. This also resolves
  ADR 0066's accepted residual that an evicted, re-read URL duplicates its chunks.
- **Stitching.** `DocumentStitcher` removes the longest exact suffix/prefix overlap between consecutive chunks of
  one page or block, bounded by `ChunkOverlap` and at least `MinOverlap` (10) characters, and joins a page change
  with a paragraph break. A part reads one chunk before its range only to strip that overlap.
- **Budgets (`ReadDocumentLimits`).** `ChunksPerPart = 10`; `MaxPartTextLength = 10 KB`, a never-binding
  invariant at today's constants that is still enforced in code; `MaxPartsPerTurn = 20`, a turn-scoped counter with
  the two-site `_pushesThisTurn` reset shape.
- **Compaction (amends 0084).** The post-turn pass, renamed `CompactArtifactCalls` → `CompactToolPayloads`, now
  covers a second family: every successful part (it starts with `PartHeaderPrefix`) is replaced with a stub built
  from the call's own arguments. Refusals stay verbatim and stubs are left alone. The pass runs at the **five**
  persist sites, not the three 0084 recorded.
- **Provenance (ADR 0066).** A part of a web-fetched document carries an explicit UNTRUSTED marker in its header;
  an uploaded document's part does not.
- **Reach.** Registered unconditionally (no hosted tool provides it) and gated by its key, which lands OFF for
  anyone who ever pressed Save (ADR 0021). Copilot chats never get it: they hold no documents. The agent-MCP runner
  keeps it; the fleet and the research agent have no document tools.
- **No new egress (rule 13).** It reads only this conversation's own chunks and sends nothing anywhere.

## Alternatives considered
- **Summarise each document at ingest.** Rejected: a model call per upload, slow, costly, and one fixed summary
  that cannot answer "list every X".
- **Raise the retrieval provider's top-K.** Rejected: still a similarity sample, never the whole document in order,
  and it would inflate every turn's context, not just whole-document turns.
- **Leave re-uploads duplicating and dedupe only on read.** Rejected as the only fix: retrieval would keep doubling
  excerpts, and first-wins dedupe is nondeterministic across parts. The read dedupe stays as a defence for legacy data.
- **Keep part results in the session.** Rejected: twenty 10 KB parts per turn would reach the 2 MB item (ADR 0004)
  in a few turns. The model is told to take notes because later turns see stubs.

## Consequences
- Accepted residuals: delete-then-upsert is not atomic (a failure mid-upsert, a sequential per-chunk loop, leaves a
  partial copy and no old one); concurrent uploads of one name through the non-grain-serialised upload endpoint can
  interleave; legacy duplicate `chunkIndex` rows are deduped first-wins, nondeterministically across parts because
  Cosmos ORDER BY has no tiebreak; the stitcher's longest-exact match can over-strip genuinely periodic text such as
  repeated XLSX rows (it never invents text).
- Known in-turn cost: a turn at the cap holds about 200 KB (≈ 50k tokens) until compaction.
- Two new `IDocumentIndex` members widen the surface of the deferred VectorData migration
  (`.agentic/specs/plan-rag-vectordata-abstractions.md`) a little.
- The Cosmos path is prod-only; its smoke test (order, no boundary repeat, page range, replace on re-upload,
  retrieval still injecting) is manual after deploy.
