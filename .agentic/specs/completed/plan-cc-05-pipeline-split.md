# Plan cc-05: split ChatTurnPipeline by responsibility

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md`; **depends on plan cc-04 merged** (both touch `ChatGateway/`; avoid conflicts)

## Overview

`ChatTurnPipeline` has a 13-param ctor (six optional) and four partial files by concern: `.Commands.cs` (231 lines),
`.Files.cs` (170), `.Voice.cs` (127), `.Turn.cs` (202), core `ChatTurnPipeline.cs`. Same shape as `AgentGrain` at small
scale: a partial class that splits the file, not the responsibilities. The optional ctor params are the seams.

## Affected areas

- `src/Agent42.Backend/ChatGateway/ChatTurnPipeline.cs` (ctor `:64-77`), `.Commands.cs` (`/new /stop /model /agent /home
  /status`, auto-router setup: `HasRouterAsync`, `AutoDefaultAsync`, `SetAutoModelAsync`, `ModelAsync` `:58-96`),
  `.Files.cs` (download, prepare, prompt assembly, `ImageUploader` `:158`; uses attachments + ingestion),
  `.Voice.cs` (transcription via the speech transcriber), `.Turn.cs` (run turn, start conversation, drain).
- Registration: `ChatGatewayRegistration.cs` (`AddChatGateway`, `:21`), which both hosts call.
- Tests: `ChatTurnPipelineTests.cs` (behavioural), `ChatDeliveryTests.cs`, `ChatGatewayServiceTests.cs`. No source-text
  pin on these files (scout checked adapters; **re-grep tests for `ChatTurnPipeline` source reads** before moving files).
- Uses `ProjectLayoutTests` pins for `AgentApi.ChatGateway*.cs` only (`:200-201`).

## Context & constraints

- Target: three collaborators, each an `internal sealed class` in `ChatGateway/`, **registered in `AddChatGateway`** (it registers
  `services.AddSingleton<ChatTurnPipeline>()` by type, so DI picks the ctor: the collaborators must be registered too):
  `ChatModelCommands` (model/agent/auto-router commands — the largest cut), `ChatInboundFiles` (download+prepare+prompt
  assembly), `ChatVoiceTranscription`. The pipeline keeps command dispatch, the turn driver and queueing and takes the
  three as ctor params.
- **Honest ctor arithmetic (critic):** the pipeline still needs `grains`, `deduper`, `sessions`, `streamer`, `adapters`,
  `timeProvider`, `logger`, `maxTurnWait` + 3 collaborators ≈ **11 params, not ~7**. The win is cohesion (three named,
  separately testable classes), not the number; do not chase ≤ 8 by adding a parameter object. The transcriber is not a ctor
  param: it is derived from `_chatClients.GetForUserAsync(...).GetTranscriptionService()` (`ChatTurnPipeline.Voice.cs:77-86`), so
  `ChatVoiceTranscription` takes `chatClients`, not a transcriber.
- **Test call sites that construct the pipeline and must change shape:** `ChatTurnPipelineTests.cs:664` and
  `ChatDeliveryTests.cs:354` (`new ChatTurnPipeline(`). List any others with `grep -rn "new ChatTurnPipeline(" tests/`.
- **Docs that name the deleted partials** (hard CI check `check-doc-paths.mjs`): gotchas.md ~4496 names `ChatTurnPipeline.Turn.cs`;
  grep `CLAUDE.md .agentic` for each of `ChatTurnPipeline.Commands.cs`, `.Files.cs`, `.Voice.cs`, `.Turn.cs`. Fix path-only.
- Optional deps (`= null`) are how DevHost and test setups omit features. Each collaborator must keep **the same
  "absent feature" behaviour** (a missing transcriber means voice notes get today's reply; a missing router means today's
  `/model` message). Write a characterisation test per absent-feature path *before* moving code.
- Pipeline is single-replica (cross-cutting rule 12): per-chat queues and inbound dedupe stay in the pipeline/service,
  not in the new collaborators.
- Do not change reply text; chat-app tests assert strings.

## Steps

1. Characterisation tests for absent-feature paths (no transcriber, no router, no attachments/ingestion) if not already
   covered. Verify they pass on current code.
2. Extract `ChatVoiceTranscription` (smallest, 127 lines). Verify `ChatTurnPipelineTests`.
3. Extract `ChatInboundFiles`. Verify.
4. Extract `ChatModelCommands`. Verify.
5. Delete the now-empty partials; update `AddChatGateway`; check `Hosts_RegisterChatGateway` (`ProjectLayoutTests.cs:724`)
   still passes. Re-run `code-shape`, record ctor param count before/after.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Voice note with no transcriber behaves as before | `VoiceNote_WithoutTranscriber_RepliesAsBefore` | `ChatTurnPipelineTests.cs` |
| `/model` with no router behaves as before | `ModelCommand_WithoutRouter_Unchanged` | `ChatTurnPipelineTests.cs` |
| Attachments absent → file messages as before | `Files_WithoutAttachments_Unchanged` | `ChatTurnPipelineTests.cs` |
| Pipeline ctor params recorded before/after (no numeric target; ≈ 11 expected) | code-shape output in the MR | — |
| Collaborators are registered | `AddChatGateway` resolves the pipeline with its collaborators (build a `ServiceCollection`, `GetRequiredService<ChatTurnPipeline>()`), `HindsightRegistrationTests` style | `ChatGatewayRegistrationTests.cs` (new or existing: check) |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean.

## Out of scope

The adapters (04); the single-replica/lease design; any reply-text change.

## Review

## Divergence from the plan

- The collaborators are `public sealed`, not `internal`: the pipeline's public constructor takes them and the repo has no
  `InternalsVisibleTo` (tests build the pipeline by hand). Their members stay `internal`.
- `ChatTurnPipeline.Turn.cs` was kept (the turn driver is not empty); only `.Commands`, `.Files` and `.Voice` were deleted.
  `/new`, `/stop` and the dispatch table moved into `ChatTurnPipeline.cs` since `/stop` needs the queue.
- `ChatVoiceTranscription` returns its one reply in `VoiceNote.Reply` and the pipeline sends it, instead of the collaborator
  sending it. Order and text are unchanged.
- Pipeline constructor: 13 params before, 11 after.
- The test harness gained a `bare` option (no optional ports) for the three characterisation tests.
- Full-suite failures seen were the Live* tests (Azure 401, no credentials) and one flaky `EmailAddressDirectoryGrainTests`
  case that passes alone.
