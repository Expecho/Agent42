# Plan cc-10: AgentGrain — tier B tool partials (CONDITIONAL; decide after plan cc-09)

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md`; **do not start unless cc-09 is merged AND its MR description records that the collaborator
pattern needed no workaround.** If 09 hit friction (e.g. tools needing `_state` writes through back-doors), close this plan
by moving it to `completed/` with a note, and record the stopping decision in the master plan.

## Overview

Tier B partials write `_state` or share the session but are otherwise tool-shaped: `ChatGateway/AgentGrain.ChatDelivery.cs`
(117), `Web/AgentGrain.WebTools.cs` (281), `Documents/AgentGrain.ReadDocumentTool.cs` (106),
`Documents/AgentGrain.Documents.cs` (167), `Documents/AgentGrain.WebDocumentTool.cs` (292), `Attachments/AgentGrain.Attachments.cs`
(208), `Artifacts/AgentGrain.ArtifactTools.cs` (412), `Mcp/AgentGrain.McpApps.cs` (122),
`Conversations/AgentGrain.ModelRouting.cs` (193). Writing `_state` is the blocker: ADR 0106 states the grain is the only
owner of `_state`/`_session`/`_agent` and that moving tool functions "writes `_state`" was "left for a separate plan".

## Context & constraints

- The only acceptable seam is **a narrow state-mutation port**: the grain hands a collaborator an `IGrainStateAccess`-style
  object exposing just the operations that partial needs (e.g. `AddFetchableUrls`, `RecordArtifactVersion`), each of which
  mutates `_state` **inside the grain's turn** and persists via the grain. The collaborator never holds `_state`.
  Prove with a test that the port is only valid during a turn (fails closed outside, like `TurnContext.CurrentOrClosed`).
- Sub-order by risk: `ChatDelivery` and `ReadDocumentTool` first (≤ 6 `_state` refs), then `WebTools`/`WebDocumentTool`
  (security-critical: `FetchableUrls` allow-list, ADR 0011/0066; gotchas lines ~320-430, the allow-list is the exfiltration
  control — **no change to how it is consulted**), then `Artifacts`/`McpApps`/`Attachments`. `ModelRouting` last or never.
- `CompactToolPayloads` (in `ArtifactTools`) is used by core and retention (tool results are persisted in a 2 MB session):
  keep it as a static helper, not tied to the artifact collaborator.
- Every step must keep `ToolResults_DoNotBlowTheSessionSizeBudget` and the web-document guard tests green.

## Steps (to be detailed at the start of the session that picks this up, against the then-current tree)

0. Re-survey with a scout (field-touch table, as plan 09 step 0) and write the concrete steps + AC table into this file;
   run `critic` on the updated plan before executing.
1–n. One partial per commit, in the order above; stop at the first one that needs more than the narrow port.

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean; full `dotnet test Agent42.slnx`.

## Out of scope

Tier C (Research, ImageTurn, Copilot, ClientTools, SharedState, EditFork, Scheduling, RetentionSweep, BuildAgent hub):
entangled with the turn and session; a split there is a behaviour risk and is not planned.

## Review
