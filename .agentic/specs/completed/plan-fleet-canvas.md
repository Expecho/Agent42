planned-against: main @ f091432

# Plan: a shared canvas for agent fleet tasks

## Overview

Fleet agents share a workspace of plain files (`FleetWorkspace`), but the user can only download those files. Nothing
renders them, and concurrent sub-teams writing one file lose each other's updates (last write wins). The user wants a
**canvas**: named, versioned pages (html, svg, markdown, code) that every agent of a task can write, Copilot agents
included, and that the user **watches live** on `/fleet/` in the sandboxed artifact pane. The user does not edit pages
(no two-way editing). Writes use optimistic concurrency, so an update is never silently lost.

## Affected areas

- `src/Agent42.Agents/Fleet/FleetStore.cs`, `IFleetStore.cs`: the canvas blob layout, and deleting it with the last task.
- `src/Agent42.Backend/Fleet/FleetCanvas.cs` (**new**): pages, the gate, the tools, load, describe, events.
- `src/Agent42.Backend/Fleet/FleetRun.cs`, `FleetTaskService.Run.cs`, `FleetTeamRunner.cs`: wiring into the run
  (agent functions, the judge input).
- `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs`: the canvas tools as Copilot custom tools.
- `src/Agent42.Backend/Fleet/FleetToolCatalog.cs`: reserving the canvas tool names (`Usable`).
- `src/Agent42.Backend/Fleet/FleetPrompts.cs`: one sentence each in the planner and Copilot default prompts.
- `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`: canvas list, content and frame routes.
- `src/Agent42.Backend/Artifacts/AgentApi.Artifacts.cs`: `AddArtifactHeaders` and `WrapSvg` go from `private` to
  `internal` for reuse. No behaviour change.
- `src/Agent42.Backend/wwwroot/fleet/index.html`, `fleet.js`, `fleet.css`: the canvas card, the pane, and the live handler.
- Tests: `tests/Agent42.Tests/FleetCanvasTests.cs` (**new**), `FleetStoreTests.cs`, `FleetTaskServiceTests.cs`,
  `FleetTeamRunnerTests.cs`, `FleetCopilotAgentFactoryTests.cs`, `FleetToolCatalogTests.cs`, `FleetApiTests.cs`,
  `FleetPageAssetsTests.cs`, `FleetTestDoubles.cs` (`FleetRunHarness` builds a `FleetWorkspace` at :46 and needs a canvas), and
  `FleetReviewFixTests.cs` (`StaleFirstReadStore` at :99 implements `IFleetStore`, so it must delegate the new members).
- Docs: a new ADR (next free number, **0102** at planning time), `docs/features/agent-fleet.md`, the CLAUDE.md
  feature-map row for the agent fleet, and `.agentic/gotchas.md`.

## Context & constraints

### User decisions (this session, 2026-10-18)

- **No grain.** The user asked whether a grain would avoid locking. A grain serializes calls but doesn't prevent lost
  updates (read v3, read v3, write, write). A version check does. The fleet already depends on the single replica
  (ADR 0097 decision 1, CLAUDE.md rule 12), and ADR 0097 rejected hosting the fleet in grains. So the solution is an
  in-process gate plus a version check, in a per-task `FleetCanvas` beside `FleetWorkspace`. Record this as an
  alternative in the ADR.
- **No two-way editing.** Agents write, the user views. No in-pane editor, and no user write route.
- **Keep embeds.** The fleet canvas uses the same `AgentApi.ArtifactCsp` as the chat artifacts, `frame-src https:`
  included. It is one constant with no fleet variant. The ADR records this as an ADR 0013 extension (see Egress).
- **Copilot agents included.** They get the canvas tools as custom tools.
- **Version cap: keep the latest 20.** At `ArtifactLimits.MaxVersionsPerArtifact`, a new version prunes the oldest
  one (its blob is deleted). This differs from ADR 0084, which refuses at the cap. The reason is that agents refining
  a page with `canvas_edit` would otherwise get stuck mid-task.
- **No share links** for fleet tasks (none exist today, and none are added).

### Design (decided in this plan; the executor follows it)

- **Pages.** A page has a name, a kind, a title and an optional language, plus versions. The name matches
  `^[a-z0-9][a-z0-9-]{0,63}$`, so it is safe as a blob segment and a URL segment. The kinds and caps come from
  `ArtifactKind` (`src/Agent42.Agents/Artifacts/ArtifactRecord.cs`) and `ArtifactLimits` (`MaxContentBytes`
  64 KB, refused and never truncated; `MaxArtifactsPerConversation` reused as the page cap per task;
  `MaxTitleLength`; `IsValidLanguage` for `code`). Reuse the constants. Don't reuse `ArtifactRecord`, because it is a
  grain-state type.
- **Tools** (names reserved in `FleetToolCatalog.Usable` like the workspace tools):
  - `canvas_list()` lists the pages with their kind, title and latest version.
  - `canvas_read(name)` returns the latest content and its `version`.
  - `canvas_write(name, kind, content, expectedVersion, title?, language?)`. `expectedVersion = 0` creates the page.
    If the page's latest version ≠ `expectedVersion`, the write is refused with the current version and content, so
    the agent can merge and retry. An existing page's kind can't change. The tool refuses rather than converting.
  - `canvas_edit(name, oldText, newText)` makes an exact replacement. `oldText` must occur exactly once in the latest
    version, else the edit is refused with the reason (absent / ambiguous) and the current version. It needs no
    version argument, because it is atomic under the gate and conflicts only when its own snippet changed. That is
    what lets concurrent sub-teams edit different parts of one page.
  - The tool descriptions carry these rules. The model reads the description, not the prompt (CLAUDE.md rule 11).
    User prompt overrides (`FleetPromptSet`) replace the default text entirely, so the description must stand alone.
- **One gate per task canvas** (`SemaphoreSlim`). Writes are short (one blob put plus the manifest), and a per-page gate
  adds code for no measured gain. Copilot custom-tool callbacks and chat-client agents call the same instance.
- **Storage is write-through, OUTSIDE the workspace prefix.** It goes to `{user}/canvas/{escaped workspaceId}/manifest.json`
  plus `{user}/canvas/{escaped workspaceId}/{name}/v{n}`.
  - **Why not `workspaces/{id}/canvas/…`:** `FleetWorkspace.SyncCoreAsync` deletes every stored blob under the
    workspace prefix that isn't on local disk (gotcha "The fleet workspace sync DELETES stored files…",
    `FleetWorkspace.cs:148-154`). It would delete the canvas after the first attempt.
  - **Why not on disk:** Copilot shells write the workspace directory directly. An on-disk canvas would get around
    the gate.
  - Every write puts the version blob first and then the manifest, under the gate. A failed put leaves the manifest
    unchanged, and the tool returns the error. The bytes are stored as octet-stream, as in the gotcha "Artifact blobs
    are stored as octet-stream…". `IFleetBlobs.PutAsync` takes bytes and no type, and `AzureFleetBlobs` sets no
    content type (the critic checked this). The routes, not the blob type, decide what is served.
  - **Prune order:** write the new manifest first, then delete the pruned blob. A viewer still showing the pruned
    version gets a 404, and the next `canvas.updated` reopens the latest version. If a delete fails, it only
    orphans a blob; it never leaves the manifest pointing at a missing version.
  - Go through `FleetStore` with its `Escape` and refusal rules. Never call `IFleetBlobs` directly (the
    `FleetStore.cs` doc and the exemplar `.agentic/gotchas.md:34`).
- **Lifecycle.**
  - `RunAsync` builds `FleetCanvas` next to the workspace (`FleetTaskService.Run.cs:31`) and **always** calls
    `LoadAsync`. A follow-up reuses `WorkspaceId` (`FleetTaskService.cs:90`), so it sees the pages from earlier tasks.
  - "Run again" of a new task gets a new workspace and an empty canvas. Copying pages is out of scope.
  - `FleetStore.DeleteTaskAsync` deletes the canvas prefix together with `DeleteWorkspaceAsync`, under the same
    "last task sharing the workspace" condition (`FleetStore.cs:44-59`).
  - `FleetTaskService.Inputs.cs:167` also calls `DeleteWorkspaceAsync`, when an input ingest fails at submit. That
    happens before any run, so no canvas exists yet and the canvas needs no delete there.
  - `FleetRun.Canvas` is `required`, like `Workspace`. Update the direct construction sites:
    `FleetCopilotAgentFactoryTests.cs:101`, `FleetRunHarness` and `FleetTaskService.Run.cs`. The
    `FleetCopilotAgentFactory` constructor (built at `Run.cs:130`) gains a `FleetCanvas` parameter.
- **Events.** Each committed write publishes `canvas.updated { name, version, kind, title }` through the run's
  `IFleetEventSink` (`task.Log`, which `FleetQuestions` already uses the same way). There is **no agent name**. No
  ambient per-call agent identity is proven inside a workflow executor (`AIAgent.CurrentRunContext` is unverified),
  and the existing `tool.call` event already shows which agent called `canvas_write`.
  `FleetJs_HandlesEveryPublishedEventType` will then require a handler in fleet.js.
- **Judge.** `JudgeAsync` (`FleetTeamRunner.cs`, the `Describe()` call at ~:198; locate it by symbol) appends the canvas `Describe()` after the workspace
  `Describe()`. It shows the latest version of each page, at most 8,000 characters per page and 20,000 in total, in
  the same format as `FleetWorkspace.Describe`. Update the judge prompt's "files in the shared workspace" wording
  (`FleetPrompts.cs:121-122`) to "and canvas pages".
- **Copilot.**
  - `FleetCopilotAgentFactory.BuildSessionConfig` sets `Tools = [.. canvas.Tools]`. It must **not** set
    `AvailableTools`. The chat kind sets it (`Copilot/CopilotAgentFactory.cs:166`) to restrict a `Mode.Empty`
    session to custom tools. In the fleet's default mode it would hide the shell and file built-ins, which ADR 0097
    decision 7 keeps.
  - Copilot agents get only the canvas tools, not the other workspace tools (they have native file access) and not MCP.
  - `FleetCopilotPermissionPolicy` already approves `PermissionRequestCustomTool` without asking
    (`FleetCopilotPermissionPolicyTests.CustomTool_ApprovedWithoutAsking`).
  - **Unverified live:** that a default-mode BYOK session offers `Tools` alongside the built-ins. The repo has no
    Copilot model key. It is the same open item as gotcha "The fleet's Copilot client is NOT ADR 0085's client".
    Record it there and in the ADR.
- **Serving** (`AgentApi.Fleet.cs`, behind Easy Auth like every fleet route, CLAUDE.md rule 4, with no exclusion):
  - `GET /api/fleet/tasks/{id}/canvas` returns the page list in the shape `artifactpane.js` `urls.list()` expects:
    `id` (= name), `kind`, `title`, `language`, `versions[{version}]`.
  - `GET /api/fleet/tasks/{id}/canvas/{name}/versions/{version:int}/content` always returns `text/plain`.
  - `GET /api/fleet/tasks/{id}/canvas/{name}/versions/{version:int}/frame` serves html and svg only
    (`ArtifactKind.IsFramed`), passing svg through `WrapSvg` and applying `ArtifactTheme.Apply(page, query theme)`.
  - Copy the shape of `ServeArtifactVersionAsync` (`Artifacts/AgentApi.Artifacts.cs:55-85`): call `AddArtifactHeaders`
    **first**, so failures carry the headers too. Resolve the task owner-scoped (`fleet.GetAsync(http.GetUserId(), id)`,
    404 if missing). Resolve the name and version against the **manifest** before any blob read.
  - The gotcha at `.agentic/gotchas.md` ~4003-4017 ("…CSP sandbox header…") says a same-origin GET with a side effect is reachable from an
    artifact. These routes are read-only, so keep them that way.
- **Frontend** (fleet.js is a standalone classic IIFE, outside the ES-module graph; `sw.js` skips `/fleet`):
  - `index.html` loads `/shared/lib/artifactpane.js` before `fleet.js`.
  - fleet.js calls `Agent42ArtifactPane.create({ host: document.body, urls, renderMarkdown })` behind an `if (!Pane)`
    guard (the `js/artifacts.js` pattern). `renderMarkdown` uses the marked and DOMPurify the page already loads.
  - A "Canvas" card in `taskFooter` (next to `filesCard`, `fleet.js` ~715) lists the pages and opens the pane. It
    also shows while the task runs, not only at the end, so the user can watch.
  - The `canvas.updated` handler reloads the list. If the pane shows that page, it reopens it at the new version.
    fleet.js tracks the open page name itself, because the pane exposes only `isOpen()`.
  - **Pane CSS: copy it into `fleet.css`.** The pane rules live in `shared/viewer.css` (:363-480: `html.has-artifact-pane
    body`, `.artifact-pane`, `.artifact-pane--full`, `.artifact-pane__head`, …), and `fleet/index.html` loads only
    `fleet.css`. Linking `viewer.css` would bring its global share-viewer rules onto the fleet page, so copy just the
    pane block, using fleet's theme tokens.
  - Check that the pane's `?theme=` source (`document.documentElement.dataset.theme`) is set on `/fleet/`.
  - Untrusted text (page names and titles) is set as text only (the fleet.js header rule).
- **No sw.js `CACHE` bump.** Rule 3 covers `wwwroot/js/`. `artifactpane.js` is already in SHELL and is not edited, and
  `/fleet` is skipped by the SW. If the executor edits `artifactpane.js`, bump `CACHE` and the literal in
  `PwaAssetsTests.cs` (~305).
- **No new DI registration.** `FleetCanvas` is per task, built in `RunAsync`, so rule 7 isn't triggered.
- **Egress (ADR 0013 extension, rule 13):** the canvas inherits the ADR 0084 consequences, now on a fleet route: a
  page can navigate itself and leak data, and a `frame-src https:` embed sends a request as soon as the pane opens.
  The fleet has **more prompt-injection inputs** than chat (fetched web pages, user-uploaded `inputs/`, MCP results,
  and Copilot fetches), so an injected agent can stage content into a page. The ADR states this. It does not say "no
  content leaves". Since there are no share links, there are no anonymous viewers.
- **ADR:** a new ADR that **amends** ADR 0097 decisions 6 (tools), 7 (Copilot fleet agents now get custom tools) and
  9 (a second served route, sandboxed, beside the download-only files). It **reuses** ADR 0084's serving rules and
  doesn't amend 0084. Check the next free number when you run `/decide` (0102 at planning time; numbers have
  collided twice before, see f091432).

## Steps

1. **Store: the canvas layout.**
   - Files: `IFleetStore.cs`, `FleetStore.cs`.
   - Add `GetCanvasManifestAsync`, `PutCanvasManifestAsync`, `PutCanvasVersionAsync`, `GetCanvasVersionAsync`,
     `DeleteCanvasVersionAsync` and `DeleteCanvasAsync`. Keys come from a `CanvasPrefix(user, ws)` built with
     `Escape`, and names are validated against the name rule.
   - `DeleteTaskAsync` calls `DeleteCanvasAsync` next to `DeleteWorkspaceAsync`.
   - The manifest is our own Blob JSON (no `[Id]`). A missing manifest means an empty canvas.
   - Update `StaleFirstReadStore` in `FleetReviewFixTests.cs` to delegate the new members.
   - Verify: `dotnet build Agent42.slnx`, then `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetStoreTests|FullyQualifiedName~FleetReviewFixTests"`.
2. **`FleetCanvas`.**
   - Files: `src/Agent42.Backend/Fleet/FleetCanvas.cs`, `tests/Agent42.Tests/FleetCanvasTests.cs`.
   - The constructor takes `(IFleetStore, userId, workspaceId, IFleetEventSink)`. It has `LoadAsync`, `Tools`, and
     `Describe(maxCharsPerPage, maxTotalChars)`.
   - Implement the four tools under the gate, with version checking, edit uniqueness, caps, pruning at 20, and
     write-through followed by publish.
   - Keep each method under S3776 15. Split validation into its own helpers.
   - Verify: `--filter FullyQualifiedName~FleetCanvasTests`.
3. **Run wiring.** Depends on 2. Can run in parallel with 4 in a worktree.
   - Files: `FleetRun.cs` (a `Canvas` property), `FleetTaskService.Run.cs` (construct and `LoadAsync`),
     `FleetTeamRunner.cs` (`AgentFunctions` adds `run.Canvas.Tools`; `JudgeAsync` appends `Describe()`),
     `FleetCopilotAgentFactory.cs` (`Tools`, no `AvailableTools`), `FleetToolCatalog.cs` (reserved names),
     `FleetPrompts.cs` (one canvas sentence each in the planner text at :59-61, the Copilot text at :103-113, and the
     judge text at :121-122), and `FleetTestDoubles.cs` (`FleetRunHarness`).
   - The factory gets the canvas through its constructor, the same way it gets `workspace`.
   - Verify: `--filter "FullyQualifiedName~FleetTeamRunnerTests|FullyQualifiedName~FleetTaskServiceTests|FullyQualifiedName~FleetCopilotAgentFactoryTests|FullyQualifiedName~FleetToolCatalogTests"`.
4. **API routes.** Depends on 1. Can run in parallel with 3.
   - Files: `AgentApi.Fleet.cs`, `Artifacts/AgentApi.Artifacts.cs` (`internal` helpers), `FleetApiTests.cs`.
   - Verify: `--filter "FullyQualifiedName~FleetApiTests|FullyQualifiedName~ArtifactsApiTests"`. The second filter
     proves the visibility change broke nothing.
5. **Frontend.** Depends on 4 (the API shape) and on 3 (the event name).
   - Files: `wwwroot/fleet/index.html`, `fleet.js`, `fleet.css`, `FleetPageAssetsTests.cs`.
   - Verify: `--filter "FullyQualifiedName~FleetPageAssetsTests|FullyQualifiedName~ArtifactAssetsTests"`.
   - Then a manual check on DevHost (`dotnet run --project tools/Agent42.DevHost`, `/fleet/`). The canned LLM may
     never call `canvas_write`. If it doesn't, seed a page from a test harness or the in-memory store, and check that:
     - the card lists the page,
     - the pane renders html in the sandboxed frame,
     - a second write (`canvas.updated`) refreshes an open pane.
     Record what could not be exercised.
6. **Docs.**
   - Run `/decide` for the amending ADR, covering: the no-grain alternative, kept embeds as an egress extension,
     Copilot custom tools, prune at the cap, and the write-through outside the workspace prefix.
   - Add the ADR to the CLAUDE.md agent fleet row ("…, shared canvas").
   - Update `docs/features/agent-fleet.md`: the tools list, the diagram node at :53, and the Copilot section at ~80.
   - Add gotchas via `/gotcha`:
     1. The canvas lives outside the workspace prefix because the sync deletes it.
     2. A fleet Copilot `SessionConfig` must not set `AvailableTools`.
   - Run `/sync-docs` over the diff.

Order: 1 → 2 → (3 ∥ 4) → 5 → 6.

## Acceptance criteria → tests

| AC | Test | File |
|----|------|------|
| Canvas blobs round-trip under the user's escaped prefix; another user sees nothing; bad names are refused | `Canvas_PutGetList_PerUser_NamesValidated` (theory over bad names) | `FleetStoreTests.cs` |
| The canvas is deleted only with the last task that shares the workspace | `DeleteTask_KeepsSharedCanvasUntilLastTask` | `FleetStoreTests.cs` |
| The canvas survives a workspace sync (it's outside the workspace prefix) | `Canvas_SurvivesWorkspaceSync` | `FleetCanvasTests.cs` |
| `canvas_write` with `expectedVersion=0` creates; a stale `expectedVersion` is refused and returns the current version and content | `Write_Create_ThenStaleVersionRefused_ReturnsCurrent` | `FleetCanvasTests.cs` |
| **No lost update:** two concurrent writes with the same `expectedVersion` → exactly one commits | `ConcurrentWrites_SameExpectedVersion_ExactlyOneWins` | `FleetCanvasTests.cs` |
| Two concurrent `canvas_edit`s of different snippets both apply (version +2, both changes present) | `ConcurrentEdits_DifferentSnippets_BothApply` | `FleetCanvasTests.cs` |
| `canvas_edit` refuses an absent or ambiguous `oldText` | `Edit_AbsentOrAmbiguous_Refused` (theory) | `FleetCanvasTests.cs` |
| Over-cap content (> `ArtifactLimits.MaxContentBytes`), a bad kind, a kind change, a bad language and the page cap are refused, never truncated | `Write_Limits_Refused` (theory) | `FleetCanvasTests.cs` |
| The 21st version prunes the oldest (manifest and blob) | `Write_PastVersionCap_PrunesOldest` | `FleetCanvasTests.cs` |
| Each committed write publishes `canvas.updated {name, version, kind, title}`; a refused write publishes nothing | `Write_PublishesCanvasUpdated_OnlyOnCommit` (uses `RecordingFleetSink`) | `FleetCanvasTests.cs` |
| `LoadAsync` restores the pages from the store (follow-up) | `Load_RestoresPagesFromStore` | `FleetCanvasTests.cs` |
| `Describe` shows the latest versions, capped per page and in total | `Describe_LatestVersions_Capped` | `FleetCanvasTests.cs` |
| **Write-through atomicity:** a failing version put, or a failing manifest put, leaves the previous state readable, publishes no event, and returns an error to the agent. Prune writes the manifest before deleting the blob | `Write_StoreFailure_LeavesStateAndPublishesNothing` (theory over the failing step, with a throwing `IFleetBlobs` double), `Write_PastVersionCap_PrunesOldest` (asserts the order) | `FleetCanvasTests.cs` |
| The tool descriptions state the rules the model needs: `expectedVersion=0` creates, a stale version is refused, and `oldText` must be unique | `Tools_DescriptionsStateTheRules` | `FleetCanvasTests.cs` |
| Planner and chat-client agents get the canvas tools | `Planner_SeesCanvasTools` | `FleetTeamRunnerTests.cs` |
| The judge input includes the canvas | `Judge_InputIncludesCanvas` | `FleetTeamRunnerTests.cs` |
| End to end: a run whose agent calls `canvas_write` stores the page and logs `canvas.updated`; a follow-up sees the page | `Run_CanvasWrite_PersistsAndLogs_FollowUpSeesIt` | `FleetTaskServiceTests.cs` |
| An MCP tool named like a canvas tool is skipped | extend `InvalidOrTakenNames_Skipped` | `FleetToolCatalogTests.cs` |
| The Copilot `SessionConfig` carries the canvas tools and leaves `AvailableTools` null (the built-ins are kept) | `SessionConfig_CarriesCanvasTools_KeepsBuiltins` | `FleetCopilotAgentFactoryTests.cs` |
| The canvas routes send the artifact sandbox headers on content, frame **and** a miss; content is `text/plain`; frame is html for html/svg and 404 for markdown/code | `Canvas_Frame_And_Content_SendSandboxHeaders` | `FleetApiTests.cs` |
| The canvas list has the pane's shape; another user gets 404 on the list, content and frame | `Canvas_List_Shape`, extend `OtherUser_Gets404_Everywhere` | `FleetApiTests.cs` |
| **Manifest-first resolution:** an unknown page, an unknown or pruned version, and a name that fails the name rule all return 404 with the sandbox headers on content and frame | `Canvas_Unresolved_404_WithHeaders` (theory) | `FleetApiTests.cs` |
| The fleet page loads `artifactpane.js` before `fleet.js`, guards `if (!Pane)`, handles `canvas.updated`, sets page names and titles as text, and `fleet.css` carries the pane rules (`.artifact-pane`, `html.has-artifact-pane`) | `FleetPage_LoadsArtifactPane_Guarded`, `FleetCss_StylesTheArtifactPane`, plus the existing `FleetJs_HandlesEveryPublishedEventType` | `FleetPageAssetsTests.cs` |
| The planner, Copilot and judge default prompts mention the canvas | `Prompts_DefaultsMentionCanvas` | `FleetTeamRunnerTests.cs` |
| The `AddArtifactHeaders`/`WrapSvg` visibility change keeps chat artifacts intact | existing `Frame_And_Content_SendSandboxHeaders` | `ArtifactsApiTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at planning time)

- [ ] `coverage_new_code_pct_min: 80` (`node .agentic/scripts/check-new-code-coverage.mjs`)
- [ ] `cognitive_complexity_max: 15` (S3776, a build error)
- [ ] `vulnerable_packages: 0` (no new packages are planned)
- [ ] `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`

## Out of scope

- User editing of pages, and any user write route (two-way editing).
- Share links for fleet tasks or canvas pages.
- Copying canvas pages into a new task on "Run again" (only follow-ups share them).
- Sandboxing Copilot agents (ADR 0097's open follow-up).
- A fleet-specific CSP. The user kept embeds, so there is one `ArtifactCsp`.
- Merging the fleet canvas with the chat's artifacts (`AgentGrainState.Artifacts`), or a grain-hosted canvas.
- Renaming the existing `write_artifact`/`read_artifact`/`list_artifacts` workspace tools.
- Attributing a canvas write to an agent in the event (no proven ambient identity, and `tool.call` already shows it).

## Review

A critic subagent reviewed this plan on 2026-10-18 and found no ADR contradiction. Most of its line references
checked out. How each finding was handled:

1. **(Medium) `StaleFirstReadStore` (`FleetReviewFixTests.cs:99`) implements `IFleetStore` and would stop compiling.**
   Accepted. The file is in Affected areas and step 1, and step 1 now verifies with a full build.
2. **(Medium) The pane CSS was left as a check.** Accepted and decided: copy the pane block from `viewer.css` into
   `fleet.css`, guarded by `FleetCss_StylesTheArtifactPane`.
3. **(Medium) No AC covered write-through atomicity, and the prune order was unspecified.** Accepted. The prune
   order is now manifest first, then the blob delete, and `Write_StoreFailure_LeavesStateAndPublishesNothing` was added.
4. **(Medium) No AC covered manifest-first resolution on the routes.** Accepted. `Canvas_Unresolved_404_WithHeaders` was added.
5. **(Low) The prompt and description text was untested.** Accepted. `Tools_DescriptionsStateTheRules` and
   `Prompts_DefaultsMentionCanvas` were added.
6. **(Low) Line references were slightly off.** Fixed. The judge is now located by symbol.
7. **(Low) The other `DeleteWorkspaceAsync` caller (`Inputs.cs:167`) wasn't mentioned.** Accepted. It is noted as
   needing no canvas delete, because it runs before any canvas exists.
8. **(Low) Construction churn.** Accepted. `FleetRun.Canvas` is `required`, and the construction sites and the
   factory constructor parameter are listed.

## Implementation divergences (2026-10-18)

- **`AddArtifactHeaders` and `WrapSvg` stayed `private`.** `AgentApi` is one `partial` class, so
  `AgentApi.Fleet.cs` calls them directly. The planned `internal` change was not needed.
- **`artifactpane.js` was edited** (the plan allowed it with a `CACHE` bump). The pane took `versions.length` as the
  latest version number, which is wrong once a canvas page prunes v1 (versions 2..21 would show v20 as "latest").
  It now uses the last entry's number (`latestOf`), which is the same for chat artifacts. `sw.js` `CACHE` went
  v62 → v63, and the literal moved in `PwaAssetsTests`, `ClientToolsAssetsTests`, `ResearchAssetsTests` and
  `SearchAssetsTests`.
- **The event is published with the literal `"canvas.updated"`**, not the `FleetCanvas.UpdatedEvent` constant:
  `FleetJs_HandlesEveryPublishedEventType` finds event types by grepping `Publish("…")`, so a constant would hide it.
- **`FleetCanvas.ToolNames`** is a new public list. `FleetToolCatalog.Usable` reserves it, and the tests use it.
- **Test doubles:** `FakeChatClient.FleetScript` gained `agentCalls` (a scripted agent function call) and
  `JudgeInputs` (the judge's user text). These drive `Run_CanvasWrite_PersistsAndLogs_FollowUpSeesIt` and
  `Judge_InputIncludesCanvas`. `FleetRunHarness` gained `Store` and `Canvas`.
- **Store failures are returned to the agent as text** ("Could not store …, still at version n") by catching in the
  tool. The exception is not left to the function-invocation layer.
- **The manual DevHost check was not exercised.** The canned LLM never calls `canvas_write`, and DevHost's in-memory
  store has no seeding route. The pane wiring is covered by the source-text guards (`FleetPageAssetsTests`) and the
  route tests (`FleetApiTests`). The live pane render and the refresh on `canvas.updated` are unverified until a real
  run.
- **The ADR was written directly** in the `/decide` shape as 0102. The number was checked against `origin/main` and
  the open PRs.
- **Critic fixes (step 5):** the page-name rule is anchored with `\z`, not `$`. `$` let `"report\n"` through as a
  raw blob segment. Trailing-newline cases were added to both name tests, and `Write_PruneDeleteFailure_StillCommits`
  was added.
