# Plan: cancel scheduled prompts (agent tools)

planned-against: main @ e06ea2e

## Overview

Add the ability to **remove** scheduled prompts, mirroring the existing
`schedule_prompt` tool. Today a conversation can create/update durable reminders
via `schedule_prompt` (`AgentGrain.SchedulePromptAsync`) but has no way to cancel
one. Give the model two explicit tools:

- `cancel_scheduled_prompt(name)` — cancel one reminder by name.
- `cancel_all_scheduled_prompts()` — cancel every reminder in this conversation.

Two separate tools (chosen over one tool with an optional/omitted name) so the
model must deliberately opt into a mass-cancel; omitting a name can never silently
wipe everything.

## Affected areas (verified against the tree)

- `src/Agent42.Agents/AgentGrain.cs` — add the two tool methods + register them in
  `BuildAgent`'s `Tools` array. This is the exemplar for "grain + agent tools".
- `src/Agent42.Agents/Skills/scheduledprompt/SKILL.md` — runtime agent guidance;
  document when/how to cancel.
- `tests/Agent42.Tests/FakeChatClient.cs` — deterministic tool driver for tests;
  emit the cancel tool calls so the new behaviour is exercised end-to-end.
- `tools/Agent42.DevHost/CannedChatClient.cs` — offline-UI twin of FakeChatClient;
  keep it in parity so the offline UI and `/verify` can drive cancellation.
- `tests/Agent42.Tests/AgentGrainTests.cs` — new tests.

**No `IAgentGrain` change:** cancellation is model-driven (a tool on the grain),
exactly like `schedule_prompt`. Reading scheduled prompts already exists
(`GetScheduledPromptsAsync`); no new grain surface is needed.

## Context & constraints (what the executor can't cheaply re-derive)

- **User decision (interview):** two explicit tools, NOT one tool with an optional
  name. Rejected "omit name = cancel all" for accidental-mass-cancel safety.
- **Durable reminders must be explicitly unregistered — clearing state is not
  enough.** `PurgeAsync` (`AgentGrain.cs:192`) documents this: reminders keep
  ticking on their own schedule unless `UnregisterReminder` is called. Both new
  tools MUST call `this.GetReminder(name)`/`this.GetReminders()` + `UnregisterReminder`
  before removing the `ScheduledPrompts` entry, then `WriteStateAsync`. Copy the
  unregister loop shape from `PurgeAsync` for cancel-all.
- **`ScheduledPrompts` state is the mirror of the Orleans reminders**
  (`AgentGrainState.cs:19`, keyed by name). Keep the two in sync: unregister the
  reminder AND remove the dict entry in the same call.
- **Tool method shape:** private `async Task<string>` returning a human-readable
  confirmation/error string (never throw for "not found"), `[Description]` on each
  param, registered via `AIFunctionFactory.Create(..., name:, description:)` — copy
  `SchedulePromptAsync` (`AgentGrain.cs:218`). Return a clear message when the named
  reminder doesn't exist; return a count/summary for cancel-all (including the
  "nothing scheduled" case).
- **Fake/canned client guard is currently turn-global and blocks multi-turn tool
  use.** `ShouldCallReminderTool` returns false if *any* message carries a
  `FunctionResultContent` (`FakeChatClient.cs:64`, `CannedChatClient.cs:90`). A
  schedule-then-cancel test is two turns in one session; after turn 1 the history
  holds the schedule tool's result, which would suppress the cancel call in turn 2.
  Re-scope the guard to only messages **after the last user message** (i.e. "did a
  tool already run for *this* user turn"). Apply the same change to both clients to
  avoid drift.
- **Exemplars / gotchas read:** `.agentic/gotchas.md` (grain + tools exemplar =
  `AgentGrain.cs`; grain-test exemplar = `AgentGrainTests.cs`). No ADR touches
  reminders/tools (`0001` is grain-per-conversation, `0002` is Easy Auth) — no
  supersession needed.

## Steps

1. **Add the two tools to `AgentGrain`** (`src/Agent42.Agents/AgentGrain.cs`).
   - `CancelScheduledPromptAsync(string name)`: validate name non-empty; if the
     name isn't in `_state.State.ScheduledPrompts`, return a "no such reminder"
     message; else `var r = await this.GetReminder(name); if (r is not null) await
     this.UnregisterReminder(r);`, remove the dict entry, `WriteStateAsync`, log,
     return confirmation.
   - **Copy-fix (critic #4):** `SchedulePromptAsync`'s `<summary>` points at a stale
     path `see <c>Skills/reminders.md</c>` (`AgentGrain.cs:216`); the real file is
     `Skills/scheduledprompt/SKILL.md`. Fix that source line AND write the two new
     summaries with the correct path — don't propagate the stale pointer (hard rule 2).
   - `CancelAllScheduledPromptsAsync()`: if none scheduled, return a "nothing to
     cancel" message; else iterate `await this.GetReminders()` → `UnregisterReminder`
     (the `PurgeAsync` loop), capture the count, `ScheduledPrompts.Clear()`,
     `WriteStateAsync`, log, return "cancelled N reminder(s)".
   - Register both in `BuildAgent` via `AIFunctionFactory.Create` and add them to
     the `Tools = [...]` array.
   - *Verify:* `dotnet build Agent42.slnx`.

2. **Drive the tools from the deterministic clients** (`tests/Agent42.Tests/FakeChatClient.cs`,
   `tools/Agent42.DevHost/CannedChatClient.cs`).
   - Re-scope the "already ran this turn" guard to messages after the last user
     message (both files).
   - Dispatch on the last user message: contains `cancel all` →
     `cancel_all_scheduled_prompts()`; else contains `cancel` →
     `cancel_scheduled_prompt(name: "drink-water")`; else existing `remind`→schedule
     / greeting behaviour. Keep the two files' logic identical in shape.
   - **Ordering is load-bearing (critic #5):** the cancel checks MUST precede the
     `remind` check — "reminder" contains the substring "remind", so a
     "cancel …reminder" message would mis-dispatch to schedule if remind were tested
     first. Add a short comment saying so; don't reorder the branches.
   - *Verify:* `dotnet build Agent42.slnx` (tests project compiles).

3. **Document the tools in the runtime skill**
   (`src/Agent42.Agents/Skills/scheduledprompt/SKILL.md`).
   - **Extend the frontmatter `description` (critic #1, the important one):** the
     `AgentSkillsProvider` advertises only `name`+`description` and loads the body
     lazily via `load_skill` when a task matches (`AgentGrain.cs:79-82`). The current
     description (line 3) lists only *scheduling* intents, so a "cancel my reminder"
     turn won't load the skill and the new body guidance never reaches the model.
     Add cancel/remove/stop intents to the `description` so cancellation triggers
     disclosure. (The tools themselves are always advertised via the `Tools` array,
     so they still work — this is about the guidance reaching the model.)
   - Add a "Cancelling" section to the body: `cancel_scheduled_prompt` takes the
     reminder's `name` (same stable id used to schedule); `cancel_all_scheduled_prompts`
     clears every reminder in the conversation. Note the model should confirm what it
     cancelled and only claim success if the tool call succeeded (mirror the existing
     "After scheduling" guidance).
   - *Verify:* re-read; no build impact (copied to output as content).

4. **Tests** (`tests/Agent42.Tests/AgentGrainTests.cs`), following the
   `AskingToBeReminded_SchedulesAReminderViaTheTool` / `Purge_...` exemplars:
   - `AskingToCancel_RemovesTheReminderViaTheTool`: schedule via "remind me to
     drink water"; assert one scheduled prompt exists; then `ChatAsync("cancel the
     drink water reminder")`; assert a `cancel_scheduled_prompt` ToolCallStart/End
     pair and that `GetScheduledPromptsAsync()` is now empty.
   - `AskingToCancelAll_RemovesEveryReminder`: schedule; then `ChatAsync("cancel all
     reminders")`; assert a `cancel_all_scheduled_prompts` tool round-trip and
     `GetScheduledPromptsAsync()` empty. **Then call cancel-all a second time** (or a
     first time on a fresh conversation) to cover the "nothing scheduled" branch
     (critic #3) — assert it still returns cleanly and the list stays empty.
   - Add a comment on the cancel tests (mirroring `Purge` at
     `AgentGrainTests.cs:133-134`) noting they schedule a *real* reminder first, so
     they exercise the live `GetReminder`/`UnregisterReminder` path even though the
     assertion reads the state dict (critic #2).
   - `CancellingAnUnknownReminder_ReportsItGracefully` (unit-level, drives the tool
     via chat "cancel the drink water reminder" on a conversation that never
     scheduled): assert no throw, conversation still answers, scheduled list stays
     empty. *(If wiring an unknown-name cancel through the fake client proves
     awkward, cover the not-found branch by cancelling twice — second cancel hits
     the "no such reminder" path — and note that in the test.)*
   - *Verify:* `dotnet test Agent42.slnx`.

5. **Doc sweep** — `/sync-docs` over the diff. Expected touch: README line 25's
   `AgentGrain (... reminders ...)` phrasing already covers it; add "cancel" only if
   the diff makes the existing wording stale. `gotchas.md` unlikely to need a new
   entry (no new invisible constraint), but add one if cancel surfaces a trap.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| Model can cancel one reminder by name (state cleared; live unregister path exercised) | `AskingToCancel_RemovesTheReminderViaTheTool` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| Model can cancel all reminders in one call; state cleared | `AskingToCancelAll_RemovesEveryReminder` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| Cancelling a non-existent reminder fails gracefully (no throw, clear message) | `CancellingAnUnknownReminder_ReportsItGracefully` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| Both tools are advertised to the model | build + tool-call assertions in the above | `AgentGrain.cs` / tests |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- new_issues: 0
- new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80
- cognitive_complexity_max: 15 (per new tool method — both are trivially under)
- vulnerable_packages: 0 (no new packages added)
- Branch: `feature/*` or `fix/*` + MR; never push to `main`.
- Gates: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
  `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- No REST endpoint / web-UI control for cancelling (scheduled prompts have no UI
  surface today; keeping symmetry with `schedule_prompt`).
- No new `IAgentGrain` method (cancellation stays model-driven).
- No change to reminder scheduling semantics, granularity, or the one-minute floor.
- No ADR (no architectural decision superseded).

## Review

Critic (fresh context) reviewed the spec: no blockers, five findings — all accepted
and folded in.

1. **(Medium) SKILL.md `description` trigger not updated** → accepted. Step 3 now
   extends the frontmatter `description` with cancel intents so the skill actually
   loads on a cancel turn (progressive disclosure keys off description).
2. **(Low-Med) AC overstates what the test proves** (state dict, not the Orleans
   reminder table) → accepted. AC wording softened; step 4 adds a `Purge`-style
   comment that the test *exercises* the unregister path (can't assert it — 
   `GetReminders()` isn't on `IAgentGrain`, same limitation the Purge exemplar has).
3. **(Low) cancel-all "nothing to cancel" branch untested** → accepted. Step 4 adds
   a second cancel-all call to cover the empty branch (80% new-code gate).
4. **(Low) copying `SchedulePromptAsync` propagates a stale `Skills/reminders.md`
   doc pointer** → accepted. Step 1 now fixes the source line and writes correct
   summaries.
5. **(Low) dispatch-order substring collision ("reminder" ⊃ "remind")** → accepted.
   Step 2 marks the cancel-before-remind ordering as load-bearing with a comment.

Verified sound by the critic (no change): no ADR contradiction (0001 endorses
grain-bound reminder tools), no missed affected area (no scheduled-prompt REST/UI
surface), the guard re-scope is correct and preserves all three existing reminder
tests, and the tool-shape / PurgeAsync-loop references are accurate.
