---
id: 0100
date: 2026-10-18
status: accepted
tags: [fleet, documents, rag, embeddings, blob, upload, copilot, egress, no-migration]
supersedes: []
amends: [0097]
---

# Fleet tasks take attached files, indexed into a workspace-scoped vector store

## Context
Agent fleet tasks (ADR 0097) worked only on the user's text request. On 2026-10-17 the user wanted to attach files
(reports, spreadsheets, code, data) to a task: agents, Copilot agents included, should read them, and documents
should be searchable. A task runs many agents (planner, workers, judges, moderators), not one chat turn, and
follow-ups share one workspace.

## Decision
1. **Upload.** `POST /api/fleet/tasks/with-files` is a separate multipart route (`request` JSON + `files` parts). It
   raises the body limit in the handler to `min(MaxInputFiles × MaxInputFileBytes, MaxWorkspaceBytes)` + 1 MB, so the
   JSON route keeps its limit. Any file type is accepted. Submit validates count, size, names, the workspace caps
   (a follow-up's existing files included) and `followUpOf` + `copyInputsOf` before writing anything. It then stores
   the bytes under `inputs/{name}` after the registry slot is taken. `FleetTaskRecord` gains `Inputs`, `Documents` and
   `TextCopies` (Blob JSON, empty by default, no migration).
2. **Ingest at run start, never in the request**: after `task.started` and before the run is built. Per file whose
   extension maps to a `DocumentMediaTypes.IsSupported` type: extract once, write `{name}.txt` next to a PDF or Office
   file (room-checked against the workspace caps, cut at `MaxInputFileBytes`), then embed with
   `DocumentIngestionService.IngestSegmentsAsync` under owner key **`{userId}/fleet/{workspaceId}`**. Chat keys are
   `{userId}/{conversationId}`, so the two key spaces can't collide, and the user's embedding model resolves from the
   key's first segment. **No ingest failure fails the task**: each one is that file's reason in the
   `documents.ingested` event. Without an embedding model the task runs "files only". A failed re-ingest of a name
   already indexed deletes its old chunks and drops the name, so no tool returns a stale version.
3. **Model-invoked `search_documents` + `read_document`** (`FleetDocumentTools`) over that one owner key, present only
   when the workspace has indexed documents. They are not keys in the ADR 0099 picker, so an override never removes
   them, and their names are reserved against MCP tools. They never throw into a run. The read path is the chat
   tool's (`DocumentPartReader`, shared), without the per-turn part cap: the task's token budget bounds it.
   **A scoped exception to ADR 0008**, which stays the rule for chat. Always-on retrieval would embed and search
   before every call of every agent and inject excerpts into agents that don't need them.
4. **Lifecycle.** Follow-ups inherit `Documents`/`TextCopies` like `Notes`. Run again of a new task sends
   `copyInputsOf`, which copies the old workspace's `inputs/` (minus `TextCopies`) into the new one, where it is
   re-indexed. Deleting a workspace's last task deletes its chunks **before** the workspace. If the index delete
   fails, task and workspace stay for a retry.
5. **Egress (ADR 0013).** Document text and search queries go to the user's own embedding provider. That is ADR
   0030's existing path for chat documents, with no new third party.
6. **Amends ADR 0097 decision 7.** Copilot fleet agents get no Agent42 tools, but their built-in shell and file tools
   now read **user-uploaded content** (the inputs and their text copies) in the workspace directory.

## Alternatives considered
- **Embed in the submit request** — makes submit slow and fails the whole upload on a provider error. Ingest at run
  start keeps submit fast and reports per file.
- **One route that branches on content type** — minimal APIs can't bind the JSON body and a form in one handler, and
  route-level size metadata would raise the JSON route's limit too.
- **Always-on `TextSearchProvider` for fleet agents (ADR 0008's shape)** — rejected for the per-agent cost above.
- **Gate the document tools by the ADR 0099 picker** — the picker only shows `web_search` and MCP rows. A hidden key
  would land OFF for an override (ADR 0021 rule 11) while the user attached files on purpose.
- **A shared per-user document library across tasks** — out of scope. A workspace is the unit that follow-ups share,
  and it deletes cleanly with its last task.

## Consequences
- Agents can work on user files, and documents are searchable when the user has an embedding model.
- **Accepted:** embedding tokens are not counted against the task's token budget. Images are stored only (no vision
  input), and scanned PDFs are "not indexed: no extractable text". The media type is resolved from the extension
  alone, because the browser's type isn't stored.
- **Accepted:** the ingest runs inside the task, so a large PDF delays the first team by its embedding time.
- **Unverified (post-deploy):** whether ACA Easy Auth limits request bodies above chat's proven 10 MB. Check a ~20 MB
  upload after deploy and record the outcome here or as a gotcha.
- Spec: `.agentic/specs/completed/plan-fleet-files-and-documents.md`.
