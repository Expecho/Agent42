---
id: 0102
date: 2026-10-18
status: accepted
tags: [fleet, canvas, artifacts, concurrency, copilot, blob, sandbox, egress, no-migration]
supersedes: []
amends: [0097]
---

# Fleet tasks get a shared canvas: versioned pages that every agent writes and the user watches live

## Context
Fleet agents (ADR 0097) share a workspace of plain files, which the user can only download. Nothing renders them,
and sub-teams writing one file concurrently lose each other's updates (last write wins). On 2026-10-18 the user
asked for a **canvas**: named, versioned pages (html, svg, markdown, code) that every agent of a task can write,
Copilot agents included, and that the user watches live on `/fleet/` in the sandboxed artifact pane of ADR 0084.
The user only views pages; there is no two-way editing.

## Decision
1. **A per-task `FleetCanvas` beside `FleetWorkspace`, keyed by the workspace id**, so a follow-up sees the pages of
   the tasks before it, and a "Run again" of a new task (a new workspace) starts empty. `RunAsync` always calls
   `LoadAsync`. The kinds and caps are ADR 0084's (`ArtifactKind`, `ArtifactLimits`: 64 KB per version, refused and
   never truncated; 20 pages per canvas). Page names match `^[a-z0-9][a-z0-9-]{0,63}$`, one safe blob and URL segment.
2. **No lost update: one gate per canvas plus optimistic versions.** `canvas_write(name, kind, content,
   expectedVersion, title?, language?)` creates with `expectedVersion = 0` and otherwise commits only when the page
   is still at `expectedVersion`. A stale write is refused with the current version and content, so the agent
   merges and retries. `canvas_edit(name, oldText, newText)` replaces one exact, unique snippet atomically under the
   gate and needs no version, so sub-teams can edit different parts of one page. `canvas_list` and `canvas_read`
   complete the four tools. The rules live in the **tool descriptions** (CLAUDE.md rule 11). A page keeps its kind.
3. **Prune at the version cap instead of refusing.** At `MaxVersionsPerArtifact` (20) a new version drops the
   oldest one. This differs from ADR 0084, which refuses at the cap, because an agent refining a page with
   `canvas_edit` would otherwise get stuck mid-task. Version numbers only grow, so a pruned number is never reused.
4. **Write-through to Blob, outside the workspace prefix**: `{user}/canvas/{workspaceId}/manifest.json` and
   `{user}/canvas/{workspaceId}/{name}/v{n}`, through `FleetStore` (its escaping and refusals). Under the gate, each
   write puts the version blob, then the manifest, then updates memory and publishes. A failed put leaves the
   previous state and publishes nothing; the agent gets the error. A prune writes the manifest first and deletes
   the blob after, so the manifest never names a missing blob. **Not under `workspaces/{id}/`**: the workspace sync
   deletes every stored blob there that isn't on disk. **Not on disk**: a Copilot shell would write around the gate.
   The canvas is deleted with the workspace's last task.
5. **Events and judge.** Each committed write publishes `canvas.updated {name, version, kind, title}`, with no agent
   name: no per-call agent identity is proven inside a workflow executor, and the `tool.call` event already shows it.
   The judge's input appends `FleetCanvas.Describe()` (latest versions, 8,000 characters per page, 20,000 in total).
6. **Amends ADR 0097 decision 6 (tools):** agents get the canvas tools beside the workspace tools; the planner,
   Copilot and judge default prompts mention them; the four names are reserved against MCP tools.
7. **Amends ADR 0097 decision 7 (Copilot agents):** fleet Copilot agents now get **custom tools**, the four canvas
   tools only (not the workspace tools, which their file access covers, and not MCP). `SessionConfig.Tools` is set
   and `AvailableTools` is NOT: in the fleet's default mode it would hide the shell and file built-ins.
   `PermissionRequestCustomTool` is approved without asking, as before.
8. **Amends ADR 0097 decision 9 (serving):** beside the download-only files, a second, sandboxed route family:
   `GET /api/fleet/tasks/{id}/canvas` (the pane's list shape), `…/canvas/{name}/versions/{v}/content` (always
   `text/plain`) and `…/frame` (html and svg only, svg wrapped, `ArtifactTheme` applied). It **reuses ADR 0084's
   serving rules** without amending them: the same `AgentApi.ArtifactCsp`, `AddArtifactHeaders` first (so misses
   carry the headers), owner-scoped task lookup, and name and version resolved against the manifest before any blob
   read. Read-only, behind Easy Auth, no exclusion. No share links.
9. **Egress (an ADR 0013 extension, CLAUDE.md rule 13).** The canvas inherits ADR 0084's accepted residuals, now on a
   fleet route: a page can navigate itself and leak data, and a `frame-src https:` embed sends a request as soon as
   the pane opens. The user kept embeds (one CSP, no fleet variant). The fleet has **more prompt-injection inputs**
   than chat (fetched web pages, uploaded `inputs/`, MCP results, Copilot fetches), so an injected agent can stage
   content into a page that leaves when the user opens it. Content CAN leave this way; there are no anonymous
   viewers because there are no share links.

## Alternatives considered
- **A grain per canvas (the user asked whether it avoids locking)** — a grain serializes calls but doesn't prevent a
  lost update (read v3, read v3, write, write); the version check does. The fleet already relies on the single
  replica (rule 12), and ADR 0097 rejected hosting the fleet in grains.
- **Pages as workspace files** — the sync deletes them, Copilot shells bypass any gate, and files have no versions.
- **Reusing `AgentGrainState.Artifacts` / `ArtifactRecord`** — grain state of one conversation, not a fleet task.
- **A per-page gate** — more code for no measured gain; writes are one blob put plus the manifest.
- **Refusing at the version cap (ADR 0084's rule)** — strands agents that refine a page by edits.
- **A fleet-specific CSP without `frame-src https:`** — the user kept embeds.

## Consequences
- The user watches pages appear and change live; the pane reopens an open page at each new version.
- `artifactpane.js` now takes the newest version's NUMBER rather than the count (versions can start above 1 after
  a prune); the chat behaves the same, and `CACHE` was bumped.
- **Unverified live:** that a default-mode BYOK Copilot session offers custom `Tools` alongside its built-ins (no
  Copilot model key in the repo), the same open item as ADR 0097's Copilot live check.
- Spec: `.agentic/specs/completed/plan-fleet-canvas.md`.
