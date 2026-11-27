# Plan cc-08: AgentGrain — one persist sequence, and phase-split the giant methods

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first). **Highest-risk child: run it alone, not in parallel with 09/10.**

## Overview

ADR 0106 (`.agentic/decisions/0106-agentgrain-services-and-turn-context.md`) already gave `AgentGrain` a per-run
`TurnContext` and four services. What it left: (a) the **persist sequence is hand-copied at six sites**, with ordering that
matters, and (b) methods over 120 lines that are straight-line "do phase 1, phase 2, …" code. Both can be fixed **inside the
grain, without new boundaries**, which is why this comes before extracting collaborators (plan 09).

## Affected areas (scout + critic verified at 02ebdf0; find by name, lines drift)

- **Persist/index sites** (all call `_search.IndexNewTurnsAsync()` after compact → prune → serialize → write):
  `Conversations/AgentGrain.cs` `PersistTurnUnlessEphemeralAsync` (~`:1310`, reached from `ChatCoreAsync`), `RunReminderAsync`
  (~`:1709-1726`), `ImageGeneration/AgentGrain.ImageTurn.cs` (~`:117-142`, with `SkipEphemeralTurnAsync` as the else),
  `Conversations/AgentGrain.EditFork.cs` fork init (~`:184`) and `PersistTruncationAsync` (~`:240`; its comment says "The
  persist-site pipeline (ChatCoreAsync's order)"), and `Conversations/AgentGrain.RetentionSweep.cs` (~`:104`). **Decide per site
  in step 1** whether it joins the shared helper or is *deliberately excluded* (fork init and the sweep legitimately differ:
  they do not run a model turn). The ADR note may only say "one definition" for the sites that actually use it, and must list
  the excluded ones with the reason.
- **Webhook return value.** `.agentic/gotchas.md` (~line 989): the webhook fires "at THREE sites" and depends on
  `PersistTurnUnlessEphemeralAsync`'s **return value**. The extracted helper must preserve that return and the sites' use of it.
- `ChatTurnAsync` (`AgentGrain.cs:580-778`, 199 lines): usage publish; model bind / no-model exit; turn-model resolve + switch;
  copilot refusal; image branch; MCP reauth notices; vision gate + rehydrate + build user turn; token totals + stop-handle
  publish + research begin; streamed reply in try/finally; stopped-turn append + fold; persist; webhook; web-doc rebuild;
  trailing events; Done.
- `RunReminderAsync` (`:1640-1770`, 131): prompt metadata; re-resolve or skip on no model; copilot-unavailable skip; vision +
  rehydrate; attachments + prompt; research begin + copilot run open; `RunInAsync` finally close/release; compact/prune/serialize;
  `TimesFired` + dirty reset + write; search reconcile + index; web-doc rebuild; webhook; chat-delivery away reply; push-suppression
  check + away push.
- `ComposeAgent` (`Tools/AgentGrain.BuildAgent.cs:40-190`, 151) and `SelectableTools` (`:196-349`, 154).
- `GenerateImageTurnAsync` (`ImageGeneration/AgentGrain.ImageTurn.cs:31-156`, 126; 6 params).
- **Structural tests that DO exist:** `ProjectLayoutTests.cs:233-249 TurnScopedState_IsNeverAGrainField`; `:254-273
  BuildAgent_ReturnsItsCompositionInsteadOfWritingFields` (assignments to `_researchTodos/_copilotChat/_copilotUnavailable`
  only inside `ApplyComposition`, count 3); `:275-290 TurnContext_IsReadOnlyThroughTheFailClosedAccessor`; `:131-165 AgentGrainParts`.
  **An earlier draft named `McpElicitationTests.cs:369-372` and `McpUrlSeedingTests.cs:428` as source-text pins on the MCP wrap
  chain: they are not** (critic, verified: a `[Theory]` and a 415-line file with no `ReadAllText`). Step 6 must instead
  re-scout (`grep -rn "BuildAgent" tests/Agent42.Tests`) for any real pin before moving `SelectableTools`.
  Behavioural: `AgentGrainTests.cs` (large), `ReminderFiringMidStream_*`, `ToolResults_DoNotBlowTheSessionSizeBudget`.

## Context & constraints

- **Ordering is behaviour.** Compact before prune, reconcile after prune, index last, webhook after persist. The extracted
  method is the one place that order lives for the sites that use it.
- The sites differ (ephemeral handling, image turn's extra state, reminder's `TimesFired`). Extract the common tail as
  `PersistTurnAsync(TurnContext, …)`; keep differences at call sites; prove equivalence with order-recording tests.
- **Two runs can be in flight on one activation** (ADR 0106). Helpers take `TurnContext`; no new instance field holds per-run
  data (`TurnScopedState_IsNeverAGrainField`).
- Phase methods are private methods of the same partial class; no new type boundary. S3776 < 15 on every extracted method; no
  gaming by lambdas.
- `ChatTurnAsync` is an async iterator: `yield` cannot sit in a `catch` (nor in `try` with `catch`). Phase helpers for the
  streamed section are called around it, not containing `yield` across try/catch. Spike the smallest phase first.
- **Serialize once** (2 MB session, gotchas): the persist helper must serialize the session exactly once.
- Docs: update ADR 0106 via an **amendment note** (this one is genuinely a note: it records that a sequence now has one
  definition; no alternative was rejected). Next ADR number, if any, from origin/main at merge.

## Steps

1. **Characterisation:** one order-recording test per persist site that uses or will use the helper (chat turn, reminder, image
   turn, each ephemeral and not; plus truncation) asserting the sequence of storage/search/webhook calls, using the existing fakes.
   Green on current code. Decide include/exclude per site here, in writing.
2. Extract `PersistTurnAsync`, preserving the webhook-driving return value; switch sites one commit at a time (reminder first:
   it does not stream). Verify step-1 tests + `AgentGrainTests` + `ChatSearchIndexingTests` + `AgentWebhookTurnTests`.
3. `RunReminderAsync` → phase methods. Verify `AgentRetentionSweepTests`, reminder tests, `AgentGrainTests`.
4. `ChatTurnAsync` → phase methods (iterator spike first). Verify `AgentGrainTests`, `ClientToolTests`, `EditRegenerateForkTests`,
   stream-disconnect tests.
5. `GenerateImageTurnAsync` → phases. Verify image tests.
6. `ComposeAgent`/`SelectableTools` → named builders (`BuildContextProviders`, `BuiltInTools`, `WrapMcpTools`, `ApplyToolFilters`).
   Verify `ToolSelectionApiTests`, MCP tests, `BuildAgent_ReturnsItsCompositionInsteadOfWritingFields`.
7. `code-shape`: each of the five methods ≤ 100 lines **of code** (metric defined in plan 01). ADR 0106 note; `/sync-docs`; doc-path
   check for any renamed method named in gotchas.

Sequential: shared files.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Persist call order identical per site, ephemeral and not | `PersistOrder_*` | `AgentGrainPersistOrderTests.cs` (new) |
| Webhook still fires from the helper's return value | `Webhook_FiresAfterPersist_*` (extend existing `AgentWebhookTurnTests`) | `AgentWebhookTurnTests.cs` |
| Session serialized once per persist | `Persist_SerializesTheSessionOnce` | `AgentGrainPersistOrderTests.cs` |
| No per-run field introduced | `TurnScopedState_IsNeverAGrainField` | `ProjectLayoutTests.cs` |
| Composition assigns fields only via `ApplyComposition` | `BuildAgent_ReturnsItsCompositionInsteadOfWritingFields` | `ProjectLayoutTests.cs` |
| Longest methods ≤ 100 lines of code | code-shape output (gate in plan 11) | — |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15 (build error); `dotnet format` clean; full `dotnet test Agent42.slnx`.

## Out of scope

New collaborator types (plan 09); behaviour changes (file any bug the characterisation tests expose); the ctor.

## Review

Critic: HIGH #2 (non-existent MCP pins) and #3 (three → six persist sites; webhook return value) accepted and fixed above;
LOW (serialize-once AC) accepted.

## Divergence from the plan (as implemented)

- **Helper shape.** The plan said `PersistTurnAsync(TurnContext, …)`. What shipped is `WriteSessionAndIndexAsync(bool pruned,
  bool indexTurns = true)`, which takes no `TurnContext`: the tail needs no per-run data, and ephemeral handling stays in
  `PersistTurnUnlessEphemeralAsync` and the image turn, whose return value the webhook still uses.
- **Sites.** Five of the six use it: chat, reminder, image, truncation, sweep. The fork's first init is excluded, with the
  reason in the helper's comment and ADR 0106's amendment note.
- **One behaviour alignment (not a refactor-only change), at two sites.** The retention sweep also clears the flag now (harmless: it returns early while a run is in flight). The image turn now clears `_sharedStateDirty` after its write,
  like every other full-state write. Effect: at most one redundant `WriteStateAsync` at `EndRunAsync` is skipped.
- **Order tests.** Observed through a recording grain-storage decorator and a record in `TestChatSearchIndex`
  (`PersistOrderLog`), not by wrapping the grain. The webhook ordering is left to `AgentWebhookTurnTests`, because the
  webhook send is one-way and its position relative to the write cannot be observed from outside the grain.
- **Serialize-once AC.** Pinned by a source test (`Persist_SerializesTheSessionInOnePlace`) rather than a call counter.
- **Step 7 numbers.** All five methods were already at or under 100 lines of code at the start (code-shape: ChatTurnAsync 99,
  SelectableTools 91, GenerateImageTurnAsync 77, RunReminderAsync 69). After the split the longest is ChatTurnAsync at 93, and
  `BuiltInTools` at 86 (a flat list of tool descriptions). The gain is phase readability, not crossing a threshold.
- **Pre-existing failures.** `LiveModelChatTests.RealModel_DescribesAnUploadedImage` and four `LiveCopilotTests` fail with
  HTTP 401 from the real Azure endpoint (a Foundry key is configured here but rejected). They need live credentials and are
  unrelated to this change.
