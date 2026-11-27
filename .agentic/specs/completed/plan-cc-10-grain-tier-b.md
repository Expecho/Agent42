# Plan cc-10: AgentGrain — tier B tool partials (CONDITIONAL; decide after plan cc-09)

planned-against: main @ 95b1f2d (survey redone there)
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

- (Applies to the later, write-based partials only; steps 1-2 below are read-only and use `Func`s.) The only acceptable seam is **a narrow state-mutation port**: the grain hands a collaborator an `IGrainStateAccess`-style
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

## Step 0 survey (2026-11-27, main @ 95b1f2d; cc-09 merged as #302 with no workaround recorded)

| Partial | `_state` use | Writes? | Verdict |
|---|---|---|---|
| ChatDelivery | `Origin`, `AgentName` (reads in 2 deliver methods); `SetChatOriginAsync` writes `Origin` | write-once, public grain method | **Move the two `Deliver*` methods** into `ChatDeliveryTools`-style collaborator taking `Func<ChatOrigin?>` + `Func<string?>`; `SetChatOriginAsync` and `AwayReplyText` stay |
| ReadDocumentTool | `DocumentNames`, `WebDocumentNames` (reads only) | no | **Move** with `Func<IReadOnlyList<string>>` snapshots; `CompactReadDocumentResult`/`PartArgument` stay static helpers (compaction) |
| WebTools | `FetchableUrls`, `SeededFetchableUrls` read + add + trim | yes | needs the narrow port; allow-list consult unchanged |
| WebDocumentTool | `WebDocumentNames/Urls` add/trim, `WriteStateAsync` | yes | needs the port; `_session`, `_agent` touched |
| Documents / Attachments / ArtifactTools / McpApps / ModelRouting | writes, session or `_agent` touched | yes | later, only if the port stays narrow |

Decision: this MR does **steps 1 and 2 only** (the two read-only partials, no port needed). Port-based partials
(WebTools onward) are a separate plan if wanted; stop-rule from above applies.

## Steps

1. `ChatDelivery` collaborator: `ChatGateway/ChatDeliveryRelay.cs`, built in `OnActivateAsync` **before `_push`** (after `_userId`),
   with `_chatDelivery`, `_userId`, `_logger`, conversation id, `Func<ChatOrigin?>` (reads `_state.State.Origin` live; `SetChatOriginAsync`
   can set it later) and `Func<string?>` (agent name). `PushTools` (`AgentGrain.cs:289`) receives `relay.DeliverNotificationAsync`; call sites
   at `:552` and `:1736` use the relay. `AgentGrain.ChatDelivery.cs` keeps `SetChatOriginAsync` and `AwayReplyText`.
   Add small direct tests: skip on Ephemeral, on empty text, and on null Origin with PushesSent > 0. AC: `ChatDeliveryTests` green.
2. `ReadDocumentTool` collaborator: `Documents/ReadDocumentTool.cs` with TWO Funcs, `documentNames` and `webDocumentNames`
   (union/distinct stays in the collaborator; `untrusted:` comes from `webDocumentNames` alone, ADR 0066). Call site `BuildAgent.cs:294`.
   `AgentGrain.ReadDocumentTool.cs` stays, holding only `CompactReadDocumentResult` and `PartArgument`. AC: a web document still reads as
   untrusted (add a test if `ReadDocumentToolTests` lacks one); `ReadDocumentToolTests`, `ToolCatalogSnapshotTests` (golden unchanged),
   `ToolResults_DoNotBlowTheSessionSizeBudget` and `ReadDocumentParts_DoNotBlowTheSessionSizeBudget` green.
3. ADR 0148 (extends 0147; pointer in 0147's front matter and its "Moving the `_state`-reading partials" alternative), gotchas (add the
   new files as evidence beside the existing ChatDelivery/ReadDocument entries), `grep -rn` doc paths, programme status table, full gates.

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean; full `dotnet test Agent42.slnx`.

## Out of scope

Tier C (Research, ImageTurn, Copilot, ClientTools, SharedState, EditFork, Scheduling, RetentionSweep, BuildAgent hub):
entangled with the turn and session; a split there is a behaviour risk and is not planned.

## Review
