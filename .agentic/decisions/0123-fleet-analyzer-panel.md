---
id: 0123
date: 2026-11-09
status: accepted
tags: [fleet, agents, tools, selection, diagnostics, ui, egress, untrusted, no-migration]
supersedes: []
extends: [0082, 0097, 0122]
amends: [0003, 0122]
---

# The fleet analyzer moves from a selectable chat key to a panel on /fleet/ with fixed tools

## Context
ADR 0122 shipped the analyzer as the selectable chat tool key `fleet_analyzer`. Diagnosing a stuck task meant leaving
`/fleet/`, picking the key in a chat and re-explaining which task. The user asked for the analyzer **inside the fleet UI**:
a panel where they talk to an analyzer that has only the fleet tools, with no tool picker.

This reverses 0122's decision 5 (key on for a `null` selection, appended last) and its rejected alternative "a `/fleet/`
panel with its own agent" (a second agent surface with its own model choice, history and streaming path). That cost is
now cut by reusing the grain, `ChatAsync` and the chat's NDJSON event shape; only a thin endpoint and a panel are new.

## Decision
- **A built-in agent kind `fleet-analyzer`** (`AgentKinds.FleetAnalyzer`), the ADR 0082 research-kind shape without a
  registry seed. It is never stored, never user-selectable and not editable. The kind itself composes the agent
  (`AgentGrain.FleetAnalyzerAgent.cs`): a plain `ChatClientAgent` with exactly the eight analyzer tools plus `get_date`;
  no context providers (memory, skills, document search), no hosted tools, no MCP, no Copilot loop, whatever
  `EnabledTools` or the user's default say. A no-tools model gets no tools at all.
- **The persona is the constant `FleetAnalyzerDefaults.SystemPrompt`**, read at composition time and not from the pinned
  snapshot. That amends ADR 0003 (persona snapshot) for this kind, as 0082 did for research: editing the constant
  changes existing analyzer chats, which is wanted for a prompt the user cannot edit.
- **One persisted conversation per task**, `fleet-analyzer-{taskId}` under the caller's own `AgentKey`, so it cannot cross
  users. It is never touched into `IConversationIndexGrain` (no sidebar entry) and its turns are **not search-indexed**
  (`/api/search` does not filter by the index, so skipping the projection is the only guard). Each turn's text is prefixed
  `[Open task: <id>]` so "why did it stop?" needs no id; the prefix is persisted and cut from the history response.
- **Endpoints** (`AgentApi.FleetAnalyzer.cs`): `POST/GET/DELETE /api/fleet/tasks/{id}/analyzer`. POST and GET check the
  task is the caller's (404 otherwise) before any grain is addressed. POST requires a model, refuses an unknown,
  no-tools or Copilot-only one (400), binds once (idempotent) and passes the model on every turn, so the picker keeps
  working. DELETE has no task check, on purpose (the key is the caller's own, and a deleted task must stay clearable);
  it stops a running turn before purging so the turn cannot write the history back. Deleting a task purges its analyzer
  conversation, since nothing could reach it afterwards.
- **The chat key is removed**: `fleet_analyzer` leaves `ToolCatalog` (key, descriptor, the eight name rows), the
  `BuildAgent` tool list and `AgentMcpRunner.ExcludedFromAskAgent`. A stored selection that still holds the key is
  dropped by `Sanitize`. `IsExcludedFromCopilot` keeps the eight names on purpose: with no catalog row they would read
  as always-on to the selection, and a Copilot chat runs a shell unasked (ADR 0107).
- **Frontend**: a docked element in `/fleet/` (its own node, so a detail re-render never tears down a streaming answer),
  model text only through `setMarkdown` (DOMPurify + marked). `/fleet/` is outside SHELL: no `sw.js` bump.

Everything in ADR 0122 that is not decision 5 or the delivery still holds: reads scoped by `_userId`, output framed as
data, a re-run is not an origin task and only narrows, telemetry is the caller's own spans, one start per turn.
0122 keeps `status: accepted` with a forward pointer here (no `superseded_by`, the 0017 pattern).

## Egress (rule 13)
No new destination: the same user-own fleet content goes to the user's own chosen provider as in 0122. The panel's model
is one of the user's own providers (ADR 0018), and nothing is sent unless they open the panel and send a message.

## Consequences
- The chat no longer offers the analyzer. `start_fleet_task`/`get_fleet_task` (ADR 0115) stay.
- A fork (`/api/conversations/{id}/fork`) of an analyzer id would copy `AgentKind` into a listed chat. The id is the
  caller's own and the panel never offers it; accepted.
- Not built: Stop/reconnect parity with the chat, and a global analyzer outside a task.

## Alternatives considered
- **Keep the chat key and add a panel that preselects it.** Rejected: the user wanted no picker, and a selectable key can
  be switched off or widened.
- **A stored agent in the registry.** Rejected: it would be editable and listed, and its tools would again come from a
  selection.
