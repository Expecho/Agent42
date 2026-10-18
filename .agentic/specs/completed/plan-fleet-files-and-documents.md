planned-against: main @ 5807561

# Plan: attach files to a fleet task, with a workspace-scoped vector store

## Overview

Today, agent fleet tasks (ADR 0097) only work on the user's text request. The user wants to attach files (reports,
spreadsheets, code, data) to a task:

- The files land in the task's workspace, where agents and Copilot agents can read them.
- The ones that are documents are also embedded into a vector store scoped to the workspace.
- Agents get `search_documents` (semantic search) and `read_document` (sequential reading) over that store.

Follow-ups share the workspace, so they share the files and the store too.

## Affected areas

- **`src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`**:
  - A **separate** multipart route, `POST /api/fleet/tasks/with-files`, with `.DisableAntiforgery()`.
  - Its form has a `request` field (the `FleetSubmitRequest` JSON) and `files` parts.
  - The existing JSON route `POST /api/fleet/tasks` is unchanged.
- **`src/Agent42.Backend/Fleet/FleetTaskService.cs`**:
  - `FleetSubmit` gains the uploaded files and `CopyInputsOf`.
  - `SubmitAsync` validates the files, then writes them to Blob under `inputs/`.
  - `DeleteAsync` deletes the index chunks **before** the workspace when this is the workspace's last task.
- **`src/Agent42.Backend/Fleet/FleetTaskService.Run.cs`**:
  - Materialize the workspace when there are inputs, not only on a follow-up.
  - The new ingest step runs after `task.started`.
  - The run input lists the attached files.
  - `BuildRunAsync` passes the documents to the catalog.
- **New `src/Agent42.Backend/Fleet/FleetDocumentTools.cs`**: `search_documents` and `read_document` over
  `IDocumentIndex` for one workspace.
- **New `src/Agent42.Backend/Fleet/FleetDocumentIngest.cs`** (or a partial of the service): extract, write the text
  copy, ingest, report.
- **`src/Agent42.Backend/Fleet/FleetToolCatalog.cs`**: adds the document tools when the task has documents, and
  reserves their names in `Usable`.
- **`src/Agent42.Backend/Fleet/FleetOptions.cs`**: new options `MaxInputFiles` and `MaxInputFileBytes`.
- **`src/Agent42.Agents/Fleet/FleetTaskRecord.cs`**: new properties `Inputs`, `Documents` and `TextCopies`.
- **`src/Agent42.Backend/Documents/DocumentIngestionService.cs`**: an overload that ingests pre-extracted segments, so
  a PDF is extracted once.
- **`src/Agent42.Agents/Documents/AgentGrain.ReadDocumentTool.cs`**: its private `FormatPart` and the pure read-part
  pieces move to a shared static formatter in `src/Agent42.Agents/Documents/` that the grain and the fleet both call.
- **`src/Agent42.Backend/wwwroot/fleet/fleet.js`, `fleet.css`**: attach control, file chips and the multipart submit
  in the composer, Run again with `copyInputsOf`, and a handler for the new event.
- **Registration**:
  - `FleetTaskService` gets `IDocumentIndex? documents = null` and `DocumentIngestionService? ingestion = null`,
    appended **after** `time`. `FleetTaskServiceTests.cs:32` and `FleetReviewFixTests.cs:33` construct it
    positionally.
  - DI resolves both in all three hosts, already verified:
    - Backend: `Program.cs:211-217`.
    - DevHost: `Program.cs:160-162`, registered before `AddAgentFleetInMemory` at `:196`.
    - Tests: `TestServiceRegistration.AddDocumentRagFakes`.
  - When either is null, the fleet runs "files only".
- **Tests**:
  - `FleetTaskServiceTests`, `FleetApiTests`, `FleetStoreTests`, `FleetToolCatalogTests` and `FleetPageAssetsTests`.
    In the last, the existing `FleetJs_RunAgain_ResendsTaskOverride` **changes**, because it pins the Run again call
    text.
  - A new `FleetDocumentToolsTests`.
  - The existing `read_document` tests (the refactor must keep them green) and `DocumentIngestionServiceTests`.
- **Docs**:
  - A new ADR (next free number, 0100 at planning time; 0101 after PR #195 took 0100) that amends 0097 and records the 0008 scoping.
  - An amend note on 0097. Its trust-boundary note must mention that uploaded content now reaches Copilot fleet
    agents.
  - The CLAUDE.md feature map rows for the fleet and for document RAG.
  - `docs/features/agent-fleet.md`.
  - `.agentic/gotchas.md` if a trap turns up.

## Context & constraints

### User decisions (interview, 2026-10-17)

1. **Ingest at run start, not in the submit request.** Submit only stores the bytes, so it is fast. The run embeds the
   files as its first step and reports the outcome as an event. **No ingest failure of any kind fails or blocks the
   task** (see "Ingest step").
2. **No embedding model means files only.** The files still land in the workspace, with the extracted text copy, but
   nothing is indexed and the document tools are absent. The run's event says so. The composer hint states it
   statically ("Documents become searchable when you've chosen an embedding model in Settings"). There is no
   per-user check in `/api/fleet/config`.
3. **The document tools are on whenever the workspace has indexed documents.** They are not a key in the ADR 0099
   Tools picker, and the task's `EnabledTools` override doesn't gate them.
   - The fleet picker only renders `web_search` and `mcp:*` rows (`fleet.js:414`, `fleetToolKey`).
   - So the chat key `read_document` ("Read whole documents") never shows there, and there is no contradictory
     checkbox.
4. **Any file type may be attached.** All files go into `inputs/`. Only the types `DocumentMediaTypes.IsSupported`
   accepts (PDF, DOCX, PPTX, XLSX and `text/*`) are extracted and indexed. Resolve the type with
   `DocumentMediaTypes.ResolveMediaType(fileName, contentType)`, never from the browser's Content-Type alone
   (gotcha "A browser's upload Content-Type is unreliable…", `.agentic/gotchas.md` ~line 85).

### Key findings (evidence paths)

**Owner key.**
- The vector store is keyed by an opaque `ownerKey`. Use `{userId}/fleet/{workspaceId}`.
- `DocumentIngestionService.UserIdFromOwnerKey` takes the part before the first `/` (`DocumentIngestionService.cs:99-103`),
  so it resolves the right user's embedding model.
- `CosmosDocumentIndex` only uses the key as the partition key, and item ids are GUIDs (`CosmosDocumentIndex.cs:36-41`).
- A chat key is `{userId}/{conversationId}` (`AgentApi.cs:65-66`), and a route segment can't contain `/`, so the two
  key spaces can't collide.
- No Bicep change: same `doc-chunks` container, partition `/ownerKey` (gotcha "Cosmos vector search is prod-only…").

**Upload route: separate, not a content-type branch on the JSON route.**
- `SubmitFleetTask` binds `FleetSubmitRequest` from the JSON body. Minimal APIs can't combine that with form binding,
  and route-level size metadata would raise the JSON route's limit too. So:
  - Add a new handler, `POST /api/fleet/tasks/with-files`. It takes `HttpContext`, and **before reading the body** it
    sets `IHttpMaxRequestBodySizeFeature.MaxRequestBodySize` and reads the form with
    `ReadFormAsync(new FormOptions { MultipartBodyLengthLimit = … })`.
  - Its limit is `min(MaxInputFiles * MaxInputFileBytes, MaxWorkspaceBytes)` plus 1 MB for the form overhead.
  - It deserializes the `request` field with `JsonSerializerDefaults.Web`. A missing or invalid field gets a 400 with
    a JSON `error`.
  - Both routes then call the same `SubmitAsync`.
- Buffering: files are read into `byte[]` (at most ~51 MB per request at the defaults). That's acceptable on the
  single replica (rule 12), and matches the chat upload, which also buffers.
- **Unverified:** whether ACA Easy Auth limits request bodies above chat's proven 10 MB. `/api/email-in` (30 MB) is
  in `excludedPaths`, so it proves nothing. After deploy, check a ~20 MB upload. Record the outcome in the ADR or a
  gotcha.
- New options in `FleetOptions`, following the existing property style:
  - `MaxInputFiles`, default 10.
  - `MaxInputFileBytes`, default 10 MB, matching chat's `AgentApi.MaxAttachmentBytes`, `AgentApi.cs:27`.

**Workspace lifecycle and caps.**
- New task: `WorkspaceId` is a new GUID. Follow-up: it inherits the previous task's (`FleetTaskService.cs:80`).
- `RunAsync` materializes the workspace from Blob only when `previous is not null` (`Run.cs:31-34`). Change it to
  materialize also when the workspace has inputs.
- The sync **skips** overflow, without deleting it, beyond `MaxWorkspaceBytes` (50 MB) and `MaxWorkspaceFiles` (500),
  in path order (`FleetWorkspace.cs:108-162`).
- At submit, the size the workspace already has comes from `IFleetStore.ListWorkspaceAsync` (`FleetWorkspaceFile(Path, Size)`).
  It is needed for follow-ups and `copyInputsOf`. `SubmitAsync` refuses when existing + new inputs exceed either cap.
- **Text copies are written at run start, so the submit check can't know their size.** The ingest step therefore
  checks remaining room, using the local workspace size and count against the caps, before writing each copy.
  - If a copy doesn't fit, it is skipped and the event says "text copy skipped: workspace full". The document is still
  indexed and readable with `read_document`.
  - Each copy is also capped at `MaxInputFileBytes`, truncated with a marker line.
  - Generated copy names go in `TextCopies`.

**Delete ordering (privacy).**
- `FleetStore.DeleteTaskAsync` deletes the workspace only when no other task of the user shares it
  (`FleetStore.cs:44-59`).
- `FleetTaskService.DeleteAsync` decides "last task of this workspace" **first**, using `store.ListTasksAsync`. When it
  is the last task, it calls `IDocumentIndex.DeleteAsync(ownerKey)` **before** `store.DeleteTaskAsync`.
- If the index delete throws, nothing is deleted and the error surfaces, so the user can retry. That keeps the record
  that a retry needs.
- `IFleetStore.DeleteTaskAsync` keeps its signature. That avoids changing the `FleetReviewFixTests.cs:113` wrapper.
- `AgentGrain.PurgeAsync` (`AgentGrain.cs:1520`) also deletes the chunks first.

**Submit ordering.**
- Run the cheap refusals first:
  - empty task, token cap, model;
  - follow-up eligibility;
  - file count, sizes, names and the workspace caps;
  - the `copyInputsOf` / `followUpOf` combination.
- Then write the input blobs **after** `Register` succeeds (outside its `lock`) and **before** the task starts.
- On failure, roll back the registry slot (the existing `SaveTaskAsync` try/catch shape, `FleetTaskService.cs:~90-100`).
  That way a refused task never leaves orphan blobs.
- Extract `ValidateInputs` and `StoreInputsAsync` so `SubmitAsync` stays under S3776 15.

**File names.**
- Take `Path.GetFileName` of the part's file name and refuse empty names and control characters.
- Store under `inputs/{name}` through `FleetStore.NormalizeWorkspacePath` (`FleetStore.cs:113-128`).
- Two files with the same name in one submit are refused.
- File names are user-supplied text: the page sets them as text only (the fleet.js header rule).

**Record fields** (Blob JSON, `FleetStore` uses `JsonSerializerDefaults.Web`, no `[Id]`, all optional with empty
defaults):
- `Inputs`: the names uploaded or copied with **this** task.
- `Documents`: the workspace's indexed file names, carried forward from `previous` the way `Notes` is
  (`FleetTaskService.cs:85`), plus the ones this run indexed with more than 0 chunks.
- `TextCopies`: the workspace's generated `.txt` copy paths, carried forward the same way, so a user file named
  `report.pdf.txt` is never mistaken for one.
- Save the record after ingest.

**Ingest step.**
- **Order:** `task.started` is published first, before the ingest. Today it is published after `BuildRunAsync`, and
  nothing in its payload depends on the run. The order is now `task.started` → ingest and `documents.ingested` →
  `BuildRunAsync`. So the log still starts with `task.started` (existing tests assert `types[0]`), and the new
  handler can rely on `S.live`.
- For each indexable input of this task:
  1. extract;
  2. write the text copy if room;
  3. call the segments overload with `maxChunks: null`. These are user-chosen files, like chat's browser path, so
     `truncated` never applies.
- **Catch-all per file:** any exception except the task's own cancellation (`task.Budget.Token`) is recorded as that
  file's reason, and the loop continues. This covers:
  - the three embedding exceptions;
  - extraction errors;
  - `HttpRequestException`;
  - a timeout;
  - a Cosmos failure.
- **Re-upload in a follow-up** of a name that's already in `Documents`:
  - If the re-ingest succeeds, the chunks are replaced (ADR 0098).
  - If it fails, the old chunks survive, so call `IDocumentIndex.DeleteFileAsync(ownerKey, name)`, best effort, and
    drop the name from `Documents`. Search and read must never return a stale version of a file whose bytes changed.
- Publish one event, `documents.ingested`, with the per-file outcome: name, chunks, and `indexed` / `not indexed:
  <reason>` / `stored only` for non-indexable files, plus a text-copy note.
  - `FleetJs_HandlesEveryPublishedEventType` fails unless `fleet.js` has a handler for every published type.
- Embedding cost is not counted against the task's token budget. Accept that and say so in the ADR.

**Run input.** Append an "Attached files" section to the planner input. It lists:
- the `inputs/` paths;
- the text copy of each;
- which files are searchable with `search_documents` / `read_document`.

Do this for the new-task input in `RunAsync`. Also make sure the follow-up input (`FollowUpInputAsync`, `Run.cs:~114-118`,
which already lists workspace files) states which files are searchable.

Agents learn the tool names from the planner's tool list (`PlannerInstructions` lists every `AgentFunctions` entry
with its description, `FleetTeamRunner.Planning.cs:61-74`) and from this section. Don't edit the user-editable prompt
templates (`FleetPrompts.cs`): users may have overridden them.

**Document tools** (`FleetDocumentTools`, one instance per run, bound to the owner key and the `Documents` set):
- `search_documents(query)`:
  - Embed the query with the user's factory, the same chain as `AgentGrain.SearchDocumentContextAsync`
    (`AgentGrain.Documents.cs:132-166`): `GetForUserAsync`, then the `DefaultEmbeddingModelId` check, then
    `GetEmbeddingGenerator`, then `SearchAsync(ownerKey, vector, topK: 5)`.
  - Return the excerpts with a source label per hit (`{file}, p.{page}` or `{file}, chunk {n}`), capped in total
    length by a stated constant. The return value becomes model context in every agent's history.
  - With no model, or **any** exception at call time (provider, dimension mismatch after a model change, Cosmos), it
    returns a plain refusal string and logs a warning. It never throws into the agent run.
- `read_document(fileName, part = 1)`:
  - Use the same ordering, stitching, part size and footer as the chat tool, through the shared formatter.
  - Only names in `Documents` are readable. Anything else gets the refusal listing the readable names.
  - The fleet has no per-turn part cap (the grain's `MaxPartsPerTurn` is turn-scoped grain state). The task's token
    budget bounds it.
  - The header drops the "web document" wording. The tool **description** still tells the model to treat document
    text as data, not instructions, because uploads can carry third-party text.
  - Read failures also return a refusal string.

**ADR 0008.**
- ADR 0008 chose always-on `TextSearchProvider` retrieval for chat and rejected a model-invoked retrieval tool
  (`0008-always-on-textsearchprovider-retrieval.md:31-35`).
- The fleet differs: many agents per task (planner, workers, judges, moderators). Always-on injection would embed and
  search before every agent call, and inject excerpts into agents that don't need them.
- So the fleet uses an agentic `search_documents`, **scoped to the fleet only**, and chat is unchanged.
- Record this in the new ADR as a scoped exception to 0008, not a supersession.

**Egress (cross-cutting rule 13).** Document text goes to the user's own embedding provider at ingest, and queries go
to it at search time. ADR 0030 already accepts this for chat documents. The new ADR states that this is the same
egress, with no new third party.

**Interactions with other features.**
- **Copilot fleet agents** have no Agent42 tools (ADR 0097 decision 7). They see the files on disk
  (`FleetCopilotAgentFactory.cs:147`, `WorkingDirectory = workspace.Root`), including the text copies. Their
  built-in shell and file tools now read user-uploaded content: note that in the ADR and the 0097 amend note.
- **The ADR 0099 picker is untouched.** Put the document tools before web and MCP in `FleetToolCatalog.Build`, and
  add their names to `Usable`'s reserved set (`FleetToolCatalog.cs:64`), so a same-named MCP tool is skipped.

**Run again and `copyInputsOf`.**
- Run again of a **new** task (no `followUpOf`) creates a new workspace, which would lose its inputs. The submit
  request gains `copyInputsOf` (a task id):
  - `SubmitAsync` checks that the task is the user's.
  - It copies every `inputs/` blob of that task's workspace except the paths in its `TextCopies`, and these become
    the new task's `Inputs`.
  - The run then re-ingests them under the new owner key.
- **Refused:** `followUpOf` together with `copyInputsOf`. A follow-up already shares the workspace.
- New files attached together with `copyInputsOf` win on a name clash.
- **The page:** Run again passes `followUpOf` when the task has one (as today). Otherwise it passes
  `copyInputsOf: task.id`, always, and the server copies whatever inputs exist, possibly none.
  - It doesn't key on `task.inputs`, because a follow-up's inputs can live in the shared workspace without being in
    its own `Inputs`.

**Frontend.**
- `fleet.js` is a classic script outside the module graph: no `sw.js` `CACHE` bump (rule 3 covers `wwwroot/js/` only).
- Add an "Attach files" button with a hidden `<input type="file" multiple>`, chips for the chosen files (name and
  size, set as text, each removable) and a drop target on the composer.
- When files are chosen, `submitTask` posts a `FormData` (`request` JSON + `files`) to `/api/fleet/tasks/with-files`.
  Otherwise it posts JSON to `/api/fleet/tasks` as today.
  - `api()` sets `content-type: application/json` for any body, so a FormData body must skip that.
- Clear the chips on success, and show the composer hint (decision 2).
- The task header or summary lists the inputs.
- The `documents.ingested` handler renders one line per file.

## Steps

Each step lists its files and how to verify it.

1. **Shared document helpers (refactor, no behaviour change).**
   - Files:
     - the new static formatter in `src/Agent42.Agents/Documents/`;
     - `AgentGrain.ReadDocumentTool.cs`, which calls it;
     - the segments overload in `DocumentIngestionService.cs` (the bytes overload delegates to it).
   - Verify: `dotnet test Agent42.slnx --filter "FullyQualifiedName~ReadDocument|FullyQualifiedName~DocumentIngestionService"`,
     then the **full suite**, because a shared helper changed.
2. **Record and options.**
   - Files: `FleetTaskRecord.cs` (`Inputs`, `Documents`, `TextCopies`), `FleetOptions.cs`.
   - Verify: `--filter FullyQualifiedName~FleetStoreTests`.
3. **Submit with files and delete.**
   - Files:
     - `FleetTaskService.cs`: `FleetSubmit`, validation, blob writes after `Register`, `copyInputsOf`, and the
       `DeleteAsync` ordering;
     - `AgentApi.Fleet.cs`: the new multipart route and its scoped limits.
   - Verify: `--filter "FullyQualifiedName~FleetTaskServiceTests|FullyQualifiedName~FleetApiTests"`.
4. **Ingest at run start, plus the run input.**
   - Files: `FleetTaskService.Run.cs` (the event order, materialize), and the new `FleetDocumentIngest.cs`.
   - Verify: `--filter FullyQualifiedName~FleetTaskServiceTests`.
5. **Document tools in the catalog.**
   - Files: the new `FleetDocumentTools.cs`; `FleetToolCatalog.cs`; `FleetTaskService.Run.cs` `BuildRunAsync`.
   - Verify: `--filter "FullyQualifiedName~FleetDocumentToolsTests|FullyQualifiedName~FleetToolCatalogTests|FullyQualifiedName~FleetTaskServiceTests"`.
6. **Frontend.**
   - Files: `wwwroot/fleet/fleet.js`, `fleet.css`.
   - Verify: `--filter FullyQualifiedName~FleetPageAssetsTests`, then a manual DevHost run of `/fleet/`. DevHost has an
     in-memory index and a deterministic embedding generator (`tools/Agent42.DevHost/Program.cs:157-162`).
     - Attach a `.md` and a `.csv`.
     - The event shows the `.md` as indexed and the `.csv` as stored only.
     - The files list shows `inputs/…`.
     - Run again posts `copyInputsOf`.
   - If port 61325 is blocked locally, run with `-- --urls http://localhost:5198`. A headless check works with
     `playwright-core` in the scratchpad, with `executablePath` set to the cached
     `%LOCALAPPDATA%\ms-playwright\chromium-1223\chrome-win64\chrome.exe`.
7. **Docs.**
   - `/decide` the new ADR (amends 0097; a scoped exception to 0008; the 0030 egress; Copilot agents read uploads),
     plus the amend note on 0097.
   - CLAUDE.md feature map: fleet row and document RAG row.
   - `docs/features/agent-fleet.md`, then `/sync-docs`.
   - After deploy: the ~20 MB Easy Auth body check (see "Upload route").

**Parallelization:**
- Step 1 is independent of steps 2-3.
- Steps 2 → 3 → 4 → 5 are sequential.
- Step 6 depends only on the route from step 3 and the event name from step 4.

## Acceptance criteria → tests

| AC | Test | File |
|----|------|------|
| The grain's `read_document` output is unchanged after the refactor | existing `read_document` tests green, expectations unedited | existing `ReadDocument*` tests |
| The segments overload ingests the same chunks as the bytes overload | `IngestSegments_MatchesBytesPath` | `DocumentIngestionServiceTests.cs` |
| A record without `inputs`/`documents`/`textCopies` loads with empty lists | `LegacyRecord_WithoutInputs_LoadsEmpty` | `FleetStoreTests.cs` |
| Submit stores inputs under `inputs/` and records their names | `Submit_WithFiles_StoresInputs` | `FleetTaskServiceTests.cs` |
| Too many files, a file over the cap, an empty or duplicate name, existing + new over a workspace cap, or `followUpOf` + `copyInputsOf` is refused before anything is written | `Submit_InvalidFiles_RefusedWithoutBlobs` (theory) | `FleetTaskServiceTests.cs` |
| A refused submit (task already running) leaves no input blobs | `Submit_Refused_LeavesNoInputs` | `FleetTaskServiceTests.cs` |
| The multipart route stores the files; a body over its limit is refused; the JSON route is unchanged | `SubmitWithFiles_Multipart_StoresFiles`, `SubmitWithFiles_OverLimit_Refused` (+ existing JSON tests green) | `FleetApiTests.cs` |
| At run start an indexable input is extracted, its text copy written and listed in `TextCopies`, ingested under `{userId}/fleet/{workspaceId}` and listed in `Documents`; a non-indexable one is stored only; `task.started` is still the first event | `Run_IngestsDocuments_AndStoresOthers` | `FleetTaskServiceTests.cs` |
| No embedding model: the task runs, files are present, nothing is indexed, the event says why, and there are no document tools | `Run_NoEmbeddingModel_FilesOnly` | `FleetTaskServiceTests.cs` |
| Any ingest exception (not `ClientResultException`, e.g. `HttpRequestException`, an index failure) is reported per file and the task still runs | `Run_IngestFailure_DoesNotFailTask` (theory over a throwing embedder or index) | `FleetTaskServiceTests.cs` |
| A text copy that doesn't fit the workspace caps is skipped and reported; the document stays indexed | `Run_TextCopy_SkippedWhenWorkspaceFull` | `FleetTaskServiceTests.cs` |
| A follow-up sees the predecessor's `Documents` and inputs without re-ingesting them; a failed re-ingest of a re-uploaded name drops it from `Documents` and deletes its chunks | `FollowUp_InheritsDocuments`, `FollowUp_FailedReingest_DropsStaleDocument` | `FleetTaskServiceTests.cs` |
| Deleting the last task of a workspace deletes the index chunks before the workspace; a shared workspace keeps them; an index failure leaves task and workspace intact | `Delete_LastTask_DropsIndexChunksFirst` (theory incl. the failure case) | `FleetTaskServiceTests.cs` |
| `search_documents` returns labelled excerpts from this workspace only (not another workspace or a chat key), capped; a provider or dimension failure returns a refusal string | `Search_ScopedToWorkspace_Capped`, `Search_ProviderFailure_Refuses` | `FleetDocumentToolsTests.cs` |
| `read_document` reads parts in order, refuses unknown names listing the readable ones, and has no "web document" header | `Read_InOrder_RefusesUnknown` | `FleetDocumentToolsTests.cs` |
| The catalog adds the document tools only when the task has documents, ahead of MCP, and skips a same-named MCP tool; the `EnabledTools` override doesn't remove them | `Build_DocumentTools_OnlyWithDocuments_Reserved` | `FleetToolCatalogTests.cs` |
| The run's planner sees the document tools and the "Attached files" section | `Run_PlannerSeesDocumentTools` (the `SystemPromptRecorder` in `FleetTaskServiceTests`) | `FleetTaskServiceTests.cs` |
| `copyInputsOf` copies inputs except `TextCopies`, new files win on a name clash, and another user's task id is refused | `Submit_CopyInputsOf_CopiesInputs_OwnOnly` | `FleetTaskServiceTests.cs` |
| The page handles the new event; posts FormData to the files route only when files are chosen, without forcing a JSON content-type; sets file names as text; Run again passes `followUpOf`, else `copyInputsOf` | `FleetJs_HandlesEveryPublishedEventType` (existing), `FleetJs_Files_MultipartOnlyWithFiles`, `FleetJs_RunAgain_ResendsTaskOverride` (**updated** for the new call text) | `FleetPageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at planning time)

- [ ] `coverage_new_code_pct_min: 80` (`node .agentic/scripts/check-new-code-coverage.mjs main`, after committing)
- [ ] `cognitive_complexity_max: 15` (S3776, a build error)
- [ ] `vulnerable_packages: 0` (no new packages are planned)
- [ ] `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`

## Out of scope

- Always-on retrieval (`TextSearchProvider`) for fleet agents.
- Search tools for Copilot fleet agents. They read the files on disk.
- Adding files to a task that is already running, or to a finished task's workspace other than through a follow-up.
- A workspace or document library shared across unrelated tasks.
- Images as vision input to agents. Images are stored as files only.
- OCR of scanned PDFs (0 chunks → "not indexed: no extractable text").
- Counting embedding tokens against the task's token budget.
- Changing the chat upload path or the ADR 0099 Tools picker.

## Review

A critic subagent reviewed the first draft of this plan on 2026-10-17. It found no ADR contradiction, and the path
and line claims it checked held. How each finding was handled:

1. **(High) One route for JSON and multipart was under-specified, and the body limit leaked onto JSON.** Accepted.
   There is now a separate `/api/fleet/tasks/with-files` route whose limits are set in the handler and capped by
   `MaxWorkspaceBytes`. The Easy Auth body size is flagged as unverified, with a post-deploy check.
2. **(High) The text copies could break the workspace caps unseen.** Accepted. The run-time room check skips and
   reports a copy that doesn't fit, each copy is capped, `ListWorkspaceAsync` gives the existing size at submit, and
   there is a new test.
3. **(High) The ingest catch was too narrow.** Accepted.
   - Ingest catches everything per file except the task's own cancellation.
   - `search_documents` and `read_document` never throw into a run.
   - `maxChunks` is null, so nothing is truncated.
   - New tests cover these.
4. **(Medium) Run again copy semantics.** Accepted.
   - The page passes `followUpOf`, else always `copyInputsOf`.
   - The two together are refused.
   - New files win on a name clash.
   - `TextCopies` records the generated names.
5. **(Medium) The existing Run-again guard changes.** Accepted, and listed as updated.
6. **(Medium) Delete ordering could orphan chunks.** Accepted. The service decides "last task" first and deletes the
   chunks before the workspace, and a failure leaves everything intact. The `IFleetStore` signature is unchanged, so
   the `FleetReviewFixTests` wrapper is untouched.
7. **(Medium) Event order and stale re-ingest.** Accepted.
   - `task.started` moves ahead of the ingest.
   - A failed re-ingest deletes the stale chunks and drops the name from `Documents`.
8. **(Medium) Picker key `read_document` and how agents learn tool names.** Partly rejected. The fleet picker only
   renders `web_search` and `mcp:*` rows (`fleet.js:414`), so no "Read whole documents" checkbox appears there. The
   tool-name discovery path (the planner's tool list plus the "Attached files" section) is now stated.
9. **(Low) Smaller fixes.** Accepted.
   - The constructor parameters come after `time`.
   - The DI check is closed.
   - The untrusted-data wording stays in the tool description.
   - The Copilot trust-boundary note is now in the doc list.

## Implementation notes (divergences from the plan)

Implemented on `feature/fleet-files-and-documents` on 2026-10-18. Where it differs from the plan above:

- **Text copies only for PDF and Office documents.** A `text/*` input is already readable as it is, so it gets no
  `.txt` copy. The AC test uses a DOCX (`DocumentTextExtractorTests.BuildDocx`, now `internal`) for the copy.
- **Media type from the extension alone** (`ResolveMediaType(name, null)`): the browser's content type isn't stored
  with the input. The upload gotcha holds, since the extension is the reliable half.
- **`DocumentIngestionService.Extract`** is a public passthrough to the extractor. That way the fleet extracts once
  without a third constructor dependency on `FleetTaskService`.
- **Input-side code lives in a new partial, `FleetTaskService.Inputs.cs`**: `PrepareInputsAsync`, `ValidateFiles`,
  `CopiedInputsAsync`, `WorkspaceRoomErrorAsync`, `StoreInputsAsync`, `DiscardInputsQuietlyAsync`,
  `DeleteDocumentsIfLastAsync`, plus `DocumentOwnerKey`/`InputPath`.
- **Failed start after `Register`.** If storing the inputs or saving the record throws, a new workspace's inputs are
  deleted, best effort. A follow-up's shared workspace is left alone.
- **`RunningFleetTask.Record` is settable.** The ingest replaces it once, before `BuildRunAsync` reads it, so
  `FinishAsync` persists the new `Documents`/`TextCopies`.
- **Duplicate input names are compared case-insensitively.** The page dedupes the same way.
- **`task.started` gained an `inputs` field.**
- **The shared read core is `DocumentPartReader`** (`ReadAsync` returns `DocumentPartResult(Text, IsPart)`, plus
  `UnknownName`). The grain keeps its per-turn counter and its catch.
- **The catalog's `Build`/`BuildAsync` take an optional `documentTools`.** They prepend it after `Usable`, which
  reserves the two names, so an MCP tool named `read_document` is skipped even when a task has no documents.
- **Critic finding (diff review) fixed in-branch.** A user input that takes a carried-forward copy's path drops that
  path from `TextCopies`. A re-uploaded document whose copy isn't rewritten loses its stale copy.
  Test: `FollowUp_TextCopies_StayAccurate`.
- **Pre-existing gate failure fixed in-branch.** `System.Security.Cryptography.Xml` 10.0.12 had three High advisories
  (`vulnerable_packages: 0`). The pin is bumped to 10.0.13, the feed's latest, and the vulnerable check is clean.
- **Still open:** the post-deploy ~20 MB Easy Auth body-size check (ADR 0101 Consequences).
