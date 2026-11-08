---
id: 0120
date: 2026-11-08
status: accepted
tags: [fleet, sharing, public-endpoint, easy-auth, excluded-paths, egress, canvas, workspace, frontend, no-migration]
supersedes: []
extends: [0035, 0097]
amends: [0035, 0097]
---

# A fleet task can be shared by a read-only link: request, result, canvas and workspace files, in a grain namespace of its own

## Context
Chats can be shared by a public, revocable, read-only link (ADR 0035). A fleet task (ADR 0097) could not: its result
was only readable on the authenticated `/fleet/` page. The user asked to share a task like a chat, and chose the
widest scope when asked: the request and result, the canvas pages (ADR 0102) and the workspace files (ADR 0101).

Two facts shape it. A follow-up task shares its predecessor's workspace and canvas, so what a link reaches is the
**workspace**, not one task. And the public `/api/share/*` and `/shared/*` Easy Auth exclusions already exist as
wildcards (ADR 0042), so the new routes need no Bicep change.

## Decision
Copy ADR 0035's two-grain split for a task, in a namespace of its own, and serve the same safe views the owner gets.

- **Grains.** `FleetShareLinkDirectoryGrain` (global, key = token hash, state `fleet-share-link-directory`) resolves a
  hash to `FleetShareLinkTarget(userId, taskId)`. `UserFleetShareLinksGrain` (per user, state `fleet-share-links`)
  mints, lists and revokes, storing the raw token. `ShareToken` is reused as is. Create writes the list first and
  registers second, and revoke deregisters first, exactly as in ADR 0035.
- **Routes.** Owner, behind Easy Auth and scoped by `GetUserId()`: `GET|POST|DELETE /api/fleet/tasks/{id}/share`.
  Public, under the existing `/api/share/*` exclusion and self-enforcing (owner and task from the directory, never
  `GetUserId()`, every miss a 404): `GET /api/share/fleet/{token}`, `…/files`, `…/files/{**path}`, `…/canvas` and
  `…/canvas/{name}/versions/{version}/content|frame`. The page is `/shared/fleet.html#{token}`, self-contained under
  `/shared/`.
- **What the task view carries.** An allowlist: `request`, `status`, `result`, `startedAt`, `finishedAt` and `reason`.
  `reason` is a **fixed string per status**, never the stored text, which is a raw exception message.
- **Serving.** The file and canvas routes call the same cores as the owner routes (attachment, `nosniff`, a type that
  never renders; the artifact sandbox headers first), so the XSS rules cannot drift between the two.
- **Delete revokes.** Deleting a task revokes its link, and the public routes re-read the task, so a deleted task is a
  404 even if a deregister was missed.

## Alternatives considered
- **Widen `ShareLinkDirectoryGrain` with a kind field.** Rejected: a credential namespace gets its own directory grain
  type (gotchas), so a chat token can never open a task and a task token can never open a chat.
- **A snapshot at share time.** Rejected as in ADR 0035: the live view is simpler and revocation is the control.
- **A new `/api/fleet-share/*` exclusion.** Rejected: it needs a Bicep change and one more hole in the ADR 0002 gate,
  for no benefit over the existing wildcard.
- **Result only.** Smaller surface, but the user chose the wider scope.
- **Files limited to outputs (`inputs/` and the generated `.txt` copies excluded).** Rejected because the user chose
  full files. It is the first thing to tighten if this proves too open.
- **Serialize `FleetTaskRecord` and hide fields.** Rejected: a deny-list drifts as the record grows (the first
  deny-list here already missed four fields). The public view is an allowlist DTO.

## Consequences
- **Egress (extends ADR 0013 and ADR 0035).** A task's token exposes, to anyone holding the link outside the Entra
  tenant, its request and result, its canvas pages and **every file in its workspace**: the user's own uploads under
  `inputs/`, generated text copies of documents, and whatever a Copilot or shell agent wrote. The view is **live**:
  because a follow-up shares the workspace, later follow-ups and earlier tasks' files appear until the link is revoked.
  A task started from a chat has the same workspace. Revocation is the only control. Minting needs an explicit press in
  a dialog that says all of this, and deleting the task revokes the link. Transcripts, the event log, tool calls, the
  plan, notes and the other record fields are never sent.
- **A new inbound surface** that must forever self-enforce and fail closed. The ingress carve-out is a deploy-time
  property, so `EasyAuthExclusionTests` pins that the routes and every URL the new page requests are covered, and that
  the owner share route is not.
- **An anonymous read can write.** For a task stored as running and absent from the registry, `FleetTaskService.GetAsync`
  persists the `interrupted` rewrite. It is the record the owner would produce and it is idempotent, so it is accepted
  rather than adding a read-only variant.
- **Route precedence.** `/api/share/fleet/…` has a literal second segment, so it never matches a chat route
  (`/api/share/{token}/…`), and chat tokens start `shr_`, so none is literally `fleet`. A test pins it.
- No new Cosmos container (the shared `agent-state` provider, a distinct state name), no new secret, no DI
  registration. New persisted types are `[Id(n)]` records with `$type` names pinned in `GrainStorageSerializerTests`.
- Spec: `.agentic/specs/completed/plan-fleet-share-links.md`.
