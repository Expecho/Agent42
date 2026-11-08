planned-against: main @ 9cb2f64

# Plan: read-only share links for agent fleet tasks

## Overview

Chats can be shared through a public, read-only, revocable link (ADR 0035). Agent fleet tasks (`/fleet/`, ADR 0097)
cannot be shared: the result is only viewable inside the authenticated fleet page. The user wants to share a fleet
task like a chat. **Scope chosen by the user (interview):** the public link shows the task's **request, status and
result, its canvas pages (sandboxed), and its workspace files (downloads)**. Agent transcripts, the event log and
tool calls are **never** exposed (the ADR 0080 line for chat share links, applied here too).

Branch: `feature/fleet-share-links` from `main`.

## Affected areas (verified against the tree at 9cb2f64)

| Area | Files |
|------|-------|
| New grains (flat namespace `Agent42.Agents`, folder `Sharing/`) | `src/Agent42.Agents/Sharing/IFleetShareLinkDirectoryGrain.cs`, `FleetShareLinkDirectoryGrain.cs`, `IUserFleetShareLinksGrain.cs`, `UserFleetShareLinksGrain.cs` (new). Reuse `src/Agent42.Agents/Sharing/ShareToken.cs` unchanged |
| Owner + public endpoints | `src/Agent42.Backend/Fleet/AgentApi.FleetSharing.cs` (new partial `MapFleetSharingEndpoints`), `src/Agent42.Backend/Api/AgentApi.cs` (call it from `MapAgentApi`), `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs` (extract shared file/canvas cores; delete revokes the link) |
| Public viewer page | `src/Agent42.Backend/wwwroot/shared/fleet.html`, `src/Agent42.Backend/wwwroot/shared/fleet-viewer.js` (new); `src/Agent42.Backend/wwwroot/shared/viewer.css` (small additions only if needed) |
| Owner UI | `src/Agent42.Backend/wwwroot/fleet/index.html` (`#shareBtn` next to `#deleteBtn`, a share `<dialog>`), `src/Agent42.Backend/wwwroot/fleet/fleet.js`, `src/Agent42.Backend/wwwroot/fleet/fleet.css` |
| Tests | `tests/Agent42.Tests/FleetShareLinkGrainTests.cs` (new), `tests/Agent42.Tests/FleetShareApiTests.cs` (new, on `FleetApiFixture`), `tests/Agent42.Tests/EasyAuthExclusionTests.cs`, `tests/Agent42.Tests/GrainStorageSerializerTests.cs`, `tests/Agent42.Tests/ShareLinkAssetsTests.cs` and/or `tests/Agent42.Tests/FleetPageAssetsTests.cs` |
| Docs | new ADR `.agentic/decisions/0120-fleet-share-links.md` (via `/decide`), amend notes on ADR 0035 / 0097 front matter, `.agentic/gotchas.md`, `CLAUDE.md` feature map |

**No Bicep change.** The public routes live under `/api/share/*` and the page under `/shared/*`, which are already
wildcard entries in `infra/modules/container-app.bicep` `excludedPaths` (ADR 0042). The new rows in
`EasyAuthExclusionTests` prove that coverage rather than assume it.

## Context & constraints (what the executor can't cheaply re-derive)

1. **A separate grain pair, never a widened chat directory.** `.agentic/gotchas.md` → "Each credential namespace gets
   its own directory grain type". A fleet share token must not resolve on `/api/share/{token}` (chat), and a chat
   token must not resolve on the fleet route. The shape is copied from ADR 0035:
   - `FleetShareLinkDirectoryGrain` (global, **key = token hash**, state name `"fleet-share-link-directory"`) →
     `FleetShareLinkTarget(UserId, TaskId)` or null.
   - `UserFleetShareLinksGrain` (per user, state name `"fleet-share-links"`, list of `FleetShareLink(TaskId, Token,
     CreatedAt)`) with `GetAsync` / `CreateOrGetAsync` / `RemoveAsync`. Both use `AgentGrain.StorageProviderName`
     (shared `agent-state`, so no new Cosmos container; Orleans partitions by grain type).
   - Keep the two ADR 0035 inversions (gotcha "A read-only share link stores the RAW token and revokes by
     deregistering the directory FIRST"): store the raw token. **Create** writes the list first and then registers.
     **Revoke** deregisters first and then drops the entry.
   - `ShareToken.Generate()/Hash()` is reused as is: the `shr_` format is fine because the namespaces are disjoint by
     grain type, not by token prefix.
   - New state types need `[GenerateSerializer]`, `[Alias("Agent42.Agents.<Name>")]` and `[Id(n)]` fields, plus their
     `$type` names in `GrainStorageSerializerTests.PersistedTypeNames` (CLAUDE.md cross-cutting rule 2, ADR 0039).
     Use `List<T>` in state, never a collection expression into `IReadOnlyList<T>`.
2. **Public routes self-enforce and fail closed** (CLAUDE.md rule 4, gotcha "An Easy-Auth excludedPaths endpoint is
   publicly reachable…"). The userId and taskId come **only** from the directory, **never** `GetUserId()`. Every miss
   is **404**: unknown or revoked token, fleet not registered on this host (don't answer 503 here), task deleted, bad
   canvas name or version, missing file. A path-traversal attempt is 400 or 404, never a file. The token is the only
   credential.
3. **Routes.** Owner routes sit behind Easy Auth, scoped by `GetUserId()`:
   `GET|POST|DELETE /api/fleet/tasks/{id}/share`. They return `{ url }`, or `{ url: null }` from GET when the task isn't
   shared. POST on a task that isn't the caller's is 404. Public routes, all under the existing `/api/share/*` exclusion:
   - `GET /api/share/fleet/{token}` → an **explicit allowlist DTO** with exactly these properties: `request`,
     `status`, `result`, `startedAt`, `finishedAt`, and `reason`, where `reason` is a **fixed string per status**
     (stopped / interrupted / failed), never `FleetTaskRecord.Reason`. The stored reason is raw `ex.Message`
     (`FleetTaskService.Run.cs:67`, stored in `FinishAsync`) and can carry provider, endpoint, MCP or path text, which
     ADR 0080/0013 withhold from public links. Everything else on `FleetTaskRecord` stays off the wire (`UserId`,
     `WorkspaceId`, `Notes`, `EnabledTools`, `OriginConversationId`, `PlannerModel`, `Documents`, `TextCopies`,
     `Inputs`, `Agents`, `Clarify`, `SelfReview`, `Tokens`, `MaxTokens`, `FollowUpOf`). Never serialize
     `FleetTaskRecord` itself. AC 3 asserts the **exact property set**, not a list of absent fields.
   - `GET /api/share/fleet/{token}/files` and `GET /api/share/fleet/{token}/files/{**path}`.
   - `GET /api/share/fleet/{token}/canvas` and `…/canvas/{name}/versions/{version:int}/content|frame`.
   - **Route precedence:** for `/api/share/fleet/artifacts`, ASP.NET Core's literal-beats-parameter rule on segment 3
     means the fleet route matches with token `artifacts`, so the chat `/api/share/{token}/artifacts` route is not hit
     (chat tokens always start with `shr_`, so none is literally `fleet`). A test pins that this path answers 404 and
     not a 500 from an ambiguous match.
   - Share URL: `{scheme}://{host}/shared/fleet.html#{token}`. The token stays in the fragment, as `ShareUrl` does in
     `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`, and `Request.Scheme` is correct because `UseForwardedHeaders`
     runs first (ADR 0042).
4. **Reuse the owner serving rules, don't fork them.** Extract the body of `DownloadFleetFile` and
   `ServeFleetCanvasAsync` (`src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`) into private helpers that take
   `(FleetTaskService fleet, string userId, FleetTaskRecord task, …)`. Both the owner and public routes call them, so
   the XSS rules apply to both identically:
   - Files: attachment, `nosniff`, `text/plain` or `application/octet-stream`, never HTML/SVG (gotcha "Fleet workspace
     downloads are an XSS sink").
   - Canvas: `AddArtifactHeaders` goes on **first**, so a 404 is sandboxed too, and the manifest is checked before any
     blob read (ADR 0084/0102).
   - Keep S3776 under 15 per method (ADR 0050/0051). Split helpers rather than nest.
5. **Deleting a task revokes its link.** This is the belt-and-braces rule ADR 0035 applies to conversation delete,
   at `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:73`. On `FleetOutcome.Ok` in `DeleteFleetTask`, call
   `IUserFleetShareLinksGrain(userId).RemoveAsync(id)`. `FleetTaskService.DeleteAsync` is the only delete path
   (`grep DeleteTaskAsync`). The public routes also re-read the task through `FleetTaskService.GetAsync(userId, taskId)`,
   so a deleted task is 404 even if a deregister was missed. Known side effect, to note in the ADR: for a task stored
   as running and absent from the registry, `GetAsync` persists the `interrupted` rewrite (`NormalizeAsync`), so an
   anonymous read can trigger that write. It is the same record the owner would produce and it is idempotent, so it is
   accepted rather than adding a read-only variant.
6. **What a recipient sees is the WORKSPACE, not just this task.** A follow-up shares its predecessor's workspace and
   canvas (ADR 0097/0101/0102). So the files and canvas of a shared task are **live**: they include earlier tasks'
   uploaded `inputs/` and later follow-ups' changes until revoked. That matches ADR 0035's "live view, revocation is
   the control". It must be stated in the ADR (as an ADR 0013 egress extension, CLAUDE.md rule 13) **and** in the
   owner's share dialog text before anyone presses Create. The listing is everything `FleetWorkspace.SyncCoreAsync`
   stored: user uploads under `inputs/`, generated `.txt` document copies, and whatever a Copilot or shell agent wrote.
   A task started from a chat has the same workspace. **User decision:** files are in scope in full. The ADR records
   the narrower alternative (outputs only, `inputs/` and text copies excluded) as **rejected because the user chose
   the wide scope**, and says it is the first thing to tighten if this proves too open. Minting requires an explicit press, as ADR 0035 requires.
   Any status may be shared (running included). The view is live, and a running task shows its status and no result.
7. **Viewer page is self-contained.** `wwwroot/shared/fleet.html` + `fleet-viewer.js` load only relative `lib/…`
   assets under `/shared/lib/` (`marked`, `purify`, `richtext.js`, `highlight`, `artifactpane.js`) and `viewer.css`.
   They load nothing from `/lib/`, `/styles.css`, `/js/` or `/fleet/`, and use no `../`. Build every API URL as a
   literal starting `/api/share/fleet/` (template literals with `${…}` are fine): `ViewerRequestedUrls` only scans
   string literals that start with `/api/`, so a URL assembled by concatenation would escape the exclusion check. Copy `index.html`'s
   `noindex` and `no-referrer` metas and its no-FOUC theme script. `no-referrer` is fine because this page never writes
   (gotcha "A page that writes must not set no-referrer" applies only to writers). Markdown goes through
   `DOMPurify.sanitize(marked.parse(…))`, then `rich.enhance`. Model-written names and titles render as `textContent`
   only. Canvas uses `Agent42ArtifactPane.create` with `urls.list/content/frame` pointed at the token routes (the same
   list shape `fleet.js` `ensureCanvasPane` consumes). File links use `download` attributes.
   **`EasyAuthExclusionTests.ViewerRequestedUrls` currently parses only `index.html` + `viewer.js`**, so it must be
   extended to also parse `fleet.html` + `fleet-viewer.js`. Otherwise the new page's URLs are unchecked, which is
   exactly the ADR 0042 failure.
8. **Owner UI** (`/fleet/`): add a `#shareBtn` "Share" next to `#deleteBtn`, shown when a task is selected, and a
   `<dialog id="shareDialog">` modelled on the chat `#shareLinkDialog` (`src/Agent42.Backend/wwwroot/js/sharelinks.js`):
   - The dialog opens with a GET. It shows Create when unshared, and the URL with Copy/Share + Revoke when shared.
   - Create POSTs, renders the URL **before** calling `navigator.share` (gotcha "`navigator.share` — render the link
     first"). The native share is gated on a pure `typeof navigator.share === "function"` feature detect (ADR 0047).
   - The warning text says the link exposes the request, result, canvas pages and **all workspace files, including
     attached files and later follow-ups**.
   - `fleet.js` lives in `wwwroot/fleet/`, not `wwwroot/js/`, so CLAUDE.md rule 3's `CACHE` bump does not apply
     (`sw.js` skips `/fleet` entirely). Bump `CACHE` only if a file under `wwwroot/js/` changes, which this plan does
     not expect.
9. **Endpoint registration** (rule 7): the new partial's `MapFleetSharingEndpoints` must be called from `MapAgentApi`,
   or `AgentApiReachabilityTests` fails. Resolve `FleetTaskService` optionally (`services.GetService<…>()`), the
   existing fleet pattern. The grains need no DI registration.

## Steps

1. **ADR first: `/decide`** → `.agentic/decisions/0120-fleet-share-links.md`. Record the scope (request + result +
   canvas + files), the egress extension of ADR 0013/0035 (live workspace, inputs included, transcripts and tool calls
   withheld), the separate grain namespace, routes under the existing exclusions (no Bicep change), and the rejected
   alternatives:
   - widening `ShareLinkDirectoryGrain` with a kind field (rejected by the credential-namespace gotcha);
   - a snapshot at share time (ADR 0035 rejected it: live view, revocation is the control);
   - a new `/api/fleet-share/*` exclusion (needs Bicep and widens the hole list);
   - result-only (smaller surface, but the user chose the wider scope);
   - files limited to outputs (`inputs/` and text copies excluded), rejected because the user chose full files.
   Also record the anonymous-read `NormalizeAsync` write (Context 5) and the fixed-string `reason` (Context 3).
   Add `amended_by: [0120]` to 0035 and 0097 front matter. *Verify:* the doc-path integrity test
   (`dotnet test Agent42.slnx --filter "FullyQualifiedName~Doc"`) stays green.
2. **Grains** (Context 1). Add the four files in `src/Agent42.Agents/Sharing/`, then `FleetShareLinkGrainTests.cs`
   copied in shape from `tests/Agent42.Tests/ShareLinkGrainTests.cs`: register/resolve/deregister round trip,
   survives reactivation, create is idempotent and registers, revoke deregisters (link dead), revoke of an unknown
   task is a no-op. Add a **disjointness** test: a token registered in the fleet directory doesn't resolve in
   `IShareLinkDirectoryGrain`, and vice versa. Add `"Agent42.Agents.FleetShareLinkDirectoryState"` and
   `"Agent42.Agents.UserFleetShareLinksState"` to `GrainStorageSerializerTests.PersistedTypeNames`. *Verify:*
   `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetShareLinkGrainTests|FullyQualifiedName~GrainStorageSerializerTests"`.
3. **Extract serving cores** in `AgentApi.Fleet.cs` (Context 4): owner behaviour unchanged. *Verify:* existing
   `FleetApiTests` + `FleetCanvasTests` pass unchanged
   (`dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetApiTests|FullyQualifiedName~FleetCanvasTests"`).
4. **Owner + public endpoints** in `AgentApi.FleetSharing.cs`, wired from `AgentApi.cs`, and revoke-on-delete in
   `DeleteFleetTask` (Context 2, 3, 5, 9). Then `FleetShareApiTests.cs` on `FleetApiFixture` (see the AC table).
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetShareApiTests|FullyQualifiedName~AgentApiReachabilityTests"`.
5. **Public viewer** `shared/fleet.html` + `shared/fleet-viewer.js` (Context 7). Extend
   `EasyAuthExclusionTests.ViewerRequestedUrls` to both viewer pages, and add the public fleet routes to
   `Exclusions_CoverBothPublicShareRoutes` (or a sibling fact). Add `/api/fleet/tasks/x/share` to
   `Exclusions_DoNotCoverAnyAuthenticatedPath`. Add asset tests: the page is served, says read-only, wires to
   `/api/share/fleet/` and `location.hash`, and is self-contained (no `/lib/`, `../`, `styles.css`, `/fleet/`, `/js/`).
   *Verify:* `dotnet test Agent42.slnx --filter "FullyQualifiedName~EasyAuthExclusionTests|FullyQualifiedName~ShareLinkAssetsTests"`.
   *Parallelizable with step 6* once step 4 has fixed the route shapes (different files).
6. **Owner UI** in `fleet/index.html`, `fleet.js`, `fleet.css` (Context 8). Add asset tests in `FleetPageAssetsTests`:
   the share button and dialog exist; `fleet.js` calls `/api/fleet/tasks/…/share`; render comes before
   `navigator.share`; there is a pure feature detect with no `userAgent`. *Verify:*
   `dotnet test Agent42.slnx --filter "FullyQualifiedName~FleetPageAssetsTests"`.
7. **Docs sync** (`/sync-docs`):
   - CLAUDE.md feature map: add 0120 to the Agent fleet row ("share link") and mention it in the Conversations row's
     share-links phrase only if it reads naturally.
   - `.agentic/gotchas.md`: add evidence to "Each credential namespace…" (the fleet share directory). Add a short new
     entry: "A fleet share link exposes the live WORKSPACE (follow-ups, inputs), not one task; public routes reuse the
     owner serving cores". Update the excludedPaths gotcha's prose only if a count or claim changed (none expected).
   *Verify:* doc-path integrity tests.
8. **Full gates:** `dotnet build Agent42.slnx` (S3776 at 15 is a build error), `dotnet test Agent42.slnx`,
   `dotnet format Agent42.slnx --verify-no-changes`, then the new-code coverage script. DevHost smoke: run
   `tools/Agent42.DevHost`, finish a canned task, Share → open the URL in a private window → see request, result,
   canvas and files. Revoke → 404 page.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| 1 | Owner can mint (idempotent), read and revoke a link for their own task; URL is `/shared/fleet.html#shr_…` | `Share_Create_IsIdempotent_Get_Revoke` | `FleetShareApiTests.cs` |
| 2 | Another user's task can't be shared: POST is 404 and mints nothing; the other user's GET answers `{url:null}` | `Share_OtherUsersTask_PostNotFound_GetNull` | `FleetShareApiTests.cs` |
| 3 | Public view returns **exactly** `request, status, result, startedAt, finishedAt, reason` (exact property set); `reason` is a fixed string, so a failed task whose stored reason is a secret-looking exception message never shows it | `Public_Task_ReturnsExactlyTheAllowlist`, `Public_FailedTask_ReasonIsFixedText` | `FleetShareApiTests.cs` |
| 4 | Unknown token, revoked link and deleted task are all 404 | `Public_UnknownOrRevokedToken_404`, `DeleteTask_RevokesLink` | `FleetShareApiTests.cs` |
| 5 | Public file list + download: attachment, nosniff, never HTML; traversal refused | `Public_Files_ListAndDownload_AreSafe`, `Public_File_PathTraversal_Refused` | `FleetShareApiTests.cs` |
| 6 | Public canvas list/content/frame carry the sandbox headers, a miss is 404 with headers | `Public_Canvas_ServesSandboxed_MissIs404WithHeaders` | `FleetShareApiTests.cs` |
| 7 | Chat and fleet tokens never cross-resolve (grain + HTTP) | `Directories_AreDisjoint`; `ChatToken_DoesNotOpenFleetRoute_AndViceVersa` | `FleetShareLinkGrainTests.cs`, `FleetShareApiTests.cs` |
| 8 | `/api/share/fleet/artifacts` is a clean 404 and chat artifact share routes still resolve | `Public_RoutePrecedence_NoAmbiguity` | `FleetShareApiTests.cs` |
| 9 | Grain create/revoke ordering and reactivation (ADR 0035 shape) | `FleetShareLinkGrainTests` (5 facts) | `FleetShareLinkGrainTests.cs` |
| 10 | New state `$type` names resolve | `PersistedTypeName_StillResolves` rows | `GrainStorageSerializerTests.cs` |
| 11 | Public fleet routes + every URL the fleet viewer requests are carved out; owner share route is NOT | new rows/facts + `ViewerRequestedUrls` extension | `EasyAuthExclusionTests.cs` |
| 12 | Fleet viewer is served, read-only, self-contained, token from fragment | `FleetViewer_Served_SelfContained` | `ShareLinkAssetsTests.cs` |
| 13 | Owner UI: share button + dialog, renders before native share, pure feature detect | `ShareDialog_Wired_RendersBeforeNativeShare` | `FleetPageAssetsTests.cs` |
| 14 | New endpoint part reachable from `MapAgentApi` | `EveryEndpointPart_IsReachableFromMapAgentApi` (existing) | `AgentApiReachabilityTests.cs` |
| 15 | Owner file/canvas behaviour unchanged after the extraction | existing `FleetApiTests`, `FleetCanvasTests` | — |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool |
|------|-----------|------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at `dotnet build` (error) |
| `vulnerable_packages` | 0 | CI package-freshness job |
| Build / tests / format | must pass | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` |

No new packages are expected. If one turns out to be needed, it is resolved by tooling, never from memory (hard rule 4).

## Out of scope

- Agent transcripts, the event log/SSE stream, tool calls, the graph view, plan YAML, todos, judge verdicts, notes.
- Expiry timestamps (ADR 0035 deferred them too; additive later).
- A snapshot/frozen copy of the workspace at share time.
- Sharing a whole follow-up chain as one page (each task gets its own link; the workspace is shared anyway).
- Bicep / `excludedPaths` changes (none needed).
- Listing all of a user's fleet share links in one place.
- The Android app.

## Review

Critic pass (fresh context, read-only): no blocking ADR contradictions. Routing precedence, Easy Auth coverage by the
existing `/api/share/*` and `/shared/*` entries, the single delete path, ADR numbering and front matter, and the
no-`CACHE`-bump claim were all verified against code.

| # | Finding | Handling |
|---|---------|----------|
| 1 | HIGH: public `reason` would leak raw `ex.Message` | **Accepted.** Fixed string per status (Context 3), new AC 3 test |
| 2 | MEDIUM: DTO deny-list incomplete (`Inputs`, `Agents`, `Clarify`, `SelfReview`) | **Accepted.** Rewritten as an allowlist, AC 3 asserts the exact property set |
| 3 | MEDIUM: workspace exposure only partly stated; limit files to outputs? | **Partly rejected.** The user chose full files in the interview, so the scope stays. Exposure now stated in full (Context 6) and the narrower option recorded as a rejected alternative in the ADR |
| 4 | LOW: AC 2 vs Context 3 on the owner GET | **Accepted.** AC 2 reworded and the test renamed |
| 5 | LOW: anonymous read can trigger the `interrupted` write | **Accepted as a known, benign effect**, documented in the ADR (Context 5), no read-only variant |
| 6 | LOW: `ViewerRequestedUrls` only scans `/api/` literals | **Accepted.** Literal-URL rule added (Context 7) |

## Divergences from the plan (as implemented)

- `ProjectLayoutTests.AgentApiParts()` keeps its own list of endpoint parts and `AgentApiParts_MatchesReflectedMapMethods`
  fails on a new `Map*Endpoints`. The plan listed only `AgentApiReachabilityTests`. Added the `Fleet/AgentApi.FleetSharing.cs`
  row (`tests/Agent42.Tests/ProjectLayoutTests.cs`).
- The owner `DeleteFleetTask` handler became a block body taking `IGrainFactory`, to revoke the link on `FleetOutcome.Ok`.
  This was planned under step 4 but touches `AgentApi.Fleet.cs`, so it landed with the step 3 extraction file.
- The public viewer needed small CSS additions to `shared/viewer.css` (the plan said "only if needed"): section labels, the
  request block and the file list. The canvas list reuses the existing `.artifact-card` styles.
- The ShareLinkAssetsTests case and the FleetPageAssetsTests cases were named `FleetViewer_Served_WiredToFleetShareApi_SelfContained`,
  `FleetPage_HasShareButtonAndDialog_WiredToTheOwnerRoute` and `FleetShare_RendersTheLinkBeforeTheSheet_PureFeatureDetect`
  rather than the AC table's working names. `Public_EndedTask_ReasonIsFixedText` is a theory over failed/stopped/interrupted.
- `new-code-coverage` can only be measured after the commit (`check-new-code-coverage.mjs` diffs `merge-base...HEAD`).
