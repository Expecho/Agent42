planned-against: main @ ed3871a

# Plan: per-agent "auto-delete messages after…" (retention) setting

## Overview

A stored agent gets a retention setting (a preset duration). Chats started from it hard-delete messages older than that
duration — including regular (non-continuous) chats, which ADR 0068 deliberately never touched — and an **idle sweep**
removes expired text even when nobody returns to the chat (the whole chat is deleted once everything in it has expired).
Why now: the user wants control over how long an agent's conversations are kept, per agent, without needing the
continuous mode. This **reverses ADR 0068's "fixed 24 h constant, not per-agent, not relitigable" rule**; that reversal is
a deliberate user decision (this session) and needs a new ADR that amends 0068 (Step 0).

## Decisions made with the user (do not relitigate)

1. **Idle chats are swept, not just pruned on next turn.** A per-conversation Orleans reminder deletes expired text while
   the chat is idle.
2. **When every message is expired, the sweep deletes the WHOLE chat** — the same effect as the user pressing Delete
   (session, search index, documents, reminders, sidebar entry, share link). Otherwise it prunes the expired leading turns.
3. **One setting for both modes.** A continuous agent uses the same field. *Unset* keeps today's exact behaviour for a
   continuous chat (24 h prune on the next persisted turn, no sweep, chat never deleted) and means *never* for a per-chat
   agent. A continuous agent can pick a different duration but **cannot pick "never"** (brick protection, ADR 0016/0068).
   An explicit value gets the sweep, for both modes.
4. **Preset dropdown**: never / 1 hour / 24 hours / 7 days / 30 days. Stored as minutes.
5. **Snapshot at bind time, new chats only** (ADR 0003): editing the agent changes only chats started afterwards. Caveat to
   state in the UI/docs: an ongoing continuous chat keeps the value it was first bound with until it is deleted (same
   trap the gotchas already record for prompt/model edits).

## Affected areas (verified against the tree at ed3871a)

| Area | Files |
|------|-------|
| Persona/data model | `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` (trailing `[Id(13)]`), `AgentBinding.cs` (`[Id(6)]`), `src/Agent42.Agents/Conversations/AgentGrainState.cs` (`[Id(21)]` — 20 is the current max), `ConversationFork.cs` (`[Id(11)]`) |
| Registry rules | `src/Agent42.Agents/StoredAgents/AgentRegistryGrain.cs` (`SaveAsync` insert + update branches, beside the `Continuous` forcing), `AgentKinds.IsPerChatOnly` |
| API | `src/Agent42.Backend/StoredAgents/AgentContracts.cs` (`AgentUpsertRequest`), `AgentApi.StoredAgents.cs` (POST/PUT build the definition **by name**; `StartAgentRunAsync` bind) |
| Bind sites to pass the value | `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` (~:201), `src/Agent42.Backend/ChatGateway/ChatTurnPipeline.Turn.cs` (~:144), `AgentApi.StoredAgents.cs` (~:161, agent-run + inbound email). **NOT** `Mcp/AgentMcpRunner.cs` (stays fixed 24 h) and **NOT** `Fleet/AgentApi.FleetAnalyzer.cs` |
| Grain | `AgentGrain.cs` (`BindAgentAsync`, `GetAgentBindingAsync`, `ReceiveReminder`, `PurgeAsync`), `AgentGrain.Retention.cs`, `AgentGrain.EditFork.cs` (`CreateForkAsync` + `InitializeFromForkAsync`), `Scheduling/AgentGrain.Scheduling.cs` (`CancelAllScheduledPromptsAsync`, `SchedulePromptAsync`) |
| Retention core | `Conversations/HistoryRetention.cs`, `IHistoryRetention.cs` (`RetentionContext`), `RetentionLimits.cs` |
| UI | `src/Agent42.Backend/wwwroot/index.html` (General panel, next to `#agentContinuous`), `js/dom.js`, `js/agents.js` (`openAgentDialog`, `applyKindLock`, `saveAgent`), `wwwroot/sw.js` (`CACHE`, currently `agent42-shell-v87`) |
| Docs | `.agentic/decisions/` (new ADR + 0068 amendment note), `.agentic/gotchas.md` (entry at ~:249 + new one), `docs/features/custom-agents.md`, `src/Agent42.Backend/wwwroot/legal/privacy.html` (:98 says only continuous chats auto-delete) |

## Context & constraints the executor cannot cheaply re-derive

- **The grain does not know the agent definition.** `Continuous` is on `AgentDefinition [Id(7)]` only; the grain detects
  continuity from the `agent-` id prefix (`AgentGrain.Retention.cs` `IsRetentionWindowed`). The new value therefore travels
  `AgentDefinition → AgentBinding → AgentGrainState`, exactly like `Kind`. (ADR 0003, ADR 0068 "Detecting a continuous chat".)
- **Nullable on purpose.** `int? RetentionMinutes = null` on all four records: the Cosmos JSON path binds a missing key to
  `null`, not to the C# default, so a non-null declaration would be a lie about every stored document
  (`AgentDefinition.cs` remarks on `Kind`). Prove on the Cosmos JSON path in `GrainStorageSerializerTests`, not only the
  binary one (CLAUDE.md cross-cutting rule 2). Never put a collection expression into `IReadOnlyList<T>` grain state.
- **`AgentDefinition` is constructed positionally in places** — `AgentApi.StoredAgents.cs:51-55` already passes the later
  fields **by name** because "a positional append would silently land in `ApiKeyHash` (ADR 0065's scar)". Pass
  `RetentionMinutes:` by name everywhere.
- **Stamping is gated on being windowed** (ADR 0068): `createdAt` costs ~35 B/message (~1.2 % of the 2 MB item, ADR 0004).
  The gate must become "this conversation has an effective window", so a per-chat agent chat WITHOUT the setting stays
  unstamped and the session-budget canary test stays unchanged. A chat WITH the setting pays the 35 B; that is the accepted
  cost of the feature.
- **Effective window rule** (replace the boolean `IsRetentionWindowed`):
  `state.RetentionMinutes` set → that duration; else conversation id starts with `agent-` (`RetentionLimits.ContinuousConversationIdPrefix`)
  or `AgentMcpLimits.ConversationIdPrefix` → `RetentionLimits.Window` (24 h); else none. MCP conversations never carry a
  value because `AgentMcpRunner` does not pass one.
- **The prune keeps the newest turn** (`RetentionLimits.MinimumPreservedTurns = 1`) and runs only from persist sites
  (verified `ApplyRetention()` callers: `AgentGrain.cs:1287`, `AgentGrain.cs:1699` (`ReceiveReminder`), `AgentGrain.ImageTurn.cs:122`,
  and `AgentGrain.EditFork.cs:222` (`PersistTruncationAsync`, the edit/regenerate path) — **re-grep before editing**).
  Changing the gate/window changes ALL of them: a per-chat agent chat with a window now also prunes on edit/regenerate and
  forces a reconcile there. The idle sweep is one more persist site and one
  more `ApplyRetention` caller; it must do the same four things in the same order: `ApplyRetention()` (stamp+prune+`ReconcileMcpApps`+
  `RemapAfterPrune`), serialize, `WriteStateAsync`, then `_search.ReconcileAfterPruneAsync(pruned)` AFTER the write
  (gotchas: "After a retention prune, REMAP `_ephemeralOrdinals`", and the reconcile-ordering entry at ~:3543).
- **Reminder plumbing traps.**
  - `ReceiveReminder` currently runs `RunReminderAsync`, which looks the name up in `ScheduledPrompts` and **runs the
    model**. The sweep reminder (reserved name, e.g. `__retention`) must be dispatched BEFORE that, run no model, send no
    push, publish no usage, and not touch `_runsInFlight`.
  - `CancelAllScheduledPromptsAsync` unregisters **every** reminder (`GetReminders()`), so the model tool
    `cancel_all_scheduled_prompts` would silently kill the sweep: skip the reserved name there. `SchedulePromptAsync` must
    refuse a model-chosen name with the reserved prefix (reusing a name updates the existing reminder).
  - `PurgeAsync` already unregisters all reminders, which is right for the whole-chat delete path.
  - Orleans reminders fire at most once a minute; period for a window W is `Clamp(W/4, 15 min, 6 h)` (pin it as a single
    function in `RetentionLimits` so a test can assert it).
  - `BindAgentAsync(..., ephemeral: true)` writes no state, so it must register **no** reminder (incognito leaves no trace,
    ADR 0034/0134). Register after the `WriteStateAsync`.
  - A tick that finds no retention value, no agent binding, or an already-purged grain unregisters itself and returns.
- **Never run the sweep over an in-flight turn.** Rule 6 / ADR 0106: `ChatAsync` streams as interleaving `MoveNext` calls
  (`IAgentGrain.cs:118-122`) and `ReceiveReminder` is not `[AlwaysInterleave]`, so a tick can land between them. Guard on
  `_runsInFlight > 0` (the condition `HistoryEditRefusal` uses, `EditFork.cs:188`); the tick skips and the next one retries. The
  skip happens BEFORE `PublishUsageApplication()` (the first line of `ReceiveReminder`). A whole-chat delete under a running
  turn would be the worst failure here.
- **Whole-chat delete from inside the grain.** The HTTP delete does three calls (`AgentApi.Conversations.cs:66-75`):
  `PurgeAsync()`, `IConversationIndexGrain(userId).RemoveAsync(id)`, `IUserShareLinksGrain(userId).RemoveAsync(id)`. The sweep
  must do the equivalent via `GrainFactory` with the same ordering. Extract a shared helper rather than copying the sequence
  if a natural home exists; do not invent a new structure (hard rule: copy the exemplar's shape).
- **Whole-chat delete leaves two live pointers to the deleted id — handle them (critic finding, verified).**
  1. *Chat-app route.* `ChatRouteGrain` (`src/Agent42.Agents/ChatGateway/ChatRouteGrain.cs`) keeps the route's current
     `ConversationId`. After a sweep delete the next inbound Telegram/Slack/Discord message continues in the purged id as
     `Minted: false`, so `StartConversationAsync` (`ChatTurnPipeline.Turn.cs:113-149`, "a continued conversation never reaches
     this method") never re-lists, re-binds or re-sets `ChatOrigin`: the turn runs persona-less. **Decision:** when the swept
     chat has `_state.State.Origin` set, the sweep's delete also resets that route (the route grain's start-new operation;
     executor finds the exact call from the `/new` command path in `ChatTurnPipeline`), so the next message mints a fresh bound
     conversation. Gateway chats are the likeliest to sit idle, so this is required, not optional.
  2. *Browser tab left open.* The client sends `agentId` only on the first turn (`AgentApi.Conversations.cs:170-175`) while
     `TouchAsync` runs every turn, so a turn typed into a swept chat re-lists it persona-less under the new message's title.
     This is the same outcome as deleting the chat from another tab today. **Decision (user to confirm at approval): accept
     and document it** in the ADR/consequences rather than change the client protocol; a continuous chat reuses `agent-<id>`
     and hits the same path.
- **A fork must not be swept at its first tick.** Forked messages carry the SOURCE's original `CreatedAt` stamps, so a fork
  cut at an early exchange can hold only turns older than the window and would be deleted while the user looks at it.
  `InitializeFromForkAsync` therefore re-stamps every copied message with "now" when the fork carries a retention value (the
  window starts at the fork).
- **An empty bound chat is deleted by the sweep.** `BindAgentAsync` registers the reminder before any turn persists; a chat
  whose first turn failed (or whose `EnqueuePromptAsync` never persisted) has an empty session, so there is no newest message
  to compare. Rule: a tick that finds the session empty (and no run in flight) deletes the chat (nothing to preserve). The first
  tick is due after one full sweep period (≥ 15 min), far beyond a normal first turn, so a live bind is not raced. This is
  what makes "a chat's lifetime ends at most (window + one period) after its last activity" true for every bound chat.
- **The "everything expired" test uses stamped data only.** Unstamped messages (should not occur for a bound chat, but a
  provider path could leave one) are stamped "now" and keep their turn: unknown age means keep (`HistoryRetention.TurnIsExpired`
  already reads it that way). The sweep deletes the chat only when the newest message's `CreatedAt` is older than the window.
- **Forks carry the value** (`ConversationFork`/`InitializeFromForkAsync`, ADR 0092/0003): a fork is a new conversation and
  must expire like its source; `InitializeFromForkAsync` also registers the sweep reminder (only when persisting, as bind does).
- **Per-chat-only kinds** (`AgentKinds.IsPerChatOnly`: research, copilot) get `RetentionMinutes` forced to null by the
  registry, like `Continuous`. Rationale: a copilot chat's transcript lives outside the grain session (SessionFs) so a
  session prune would not remove it, and research turns' fold interaction with a prune is unverified. Relaxing this is a
  possible follow-up, deliberately not in scope.
- **What still survives a prune/sweep** (ADR 0068's hard-delete exceptions, unchanged and to be restated in the new ADR):
  text already POSTed to Hindsight (no delete path, ADR 0056), the `/logs` ring until it rolls (ADR 0054), orphaned
  attachment blob bytes (no blob delete), and the client IndexedDB mirror (ADR 0032) until refreshed. A whole-chat delete
  by the sweep leaves exactly what a manual delete leaves.
- **Residual from 0068 that now matters more:** a failed search-index delete that is never retried leaves searchable
  orphans permanently. The sweep makes pruning routine for per-chat agents; the ADR must restate it as a known residual,
  not claim it closed.
- **Egress/owner rules unaffected** (rules 13/14): no new egress, no owner gate.
- **Single replica** is already load-bearing; reminders add rows per retention chat until it is deleted. Bounded: a chat's
  lifetime is at most (window + one period) after its last activity.

## Steps

### Step 0 — ADR (`/decide`) — do first, alone
New ADR (next free number from `origin/main` at merge time; today 0140) with `amends: [0068]` (follow how 0068 amended
0016: frontmatter `amends` + an "Amended by" pointer added in 0068's body; accepted ADR decision text is not rewritten).
Content: reverses the fixed-constant rule; per-agent preset; same field for continuous (unset = legacy 24 h, no sweep, no
"never" for continuous); idle sweep + whole-chat delete; snapshot semantics; residuals above; alternatives rejected (live
lookup per turn; free-form duration; prune-on-turn-only; keep-newest-turn on sweep; "never" for continuous).
Verify: `AdrNumberingTests` passes (`dotnet test --filter AdrNumberingTests`).

### Step 1 — Data model + registry + API contract  *(blocks 2–5)*
- Add the four trailing nullable fields; add `RetentionLimits.Presets` (minutes: 60, 1440, 10080, 43200) and
  `RetentionLimits.SweepPeriod(TimeSpan)`.
- `AgentUpsertRequest` trailing `int? RetentionMinutes = null`; POST/PUT pass it **by name**; unknown non-null value → 400
  (text/plain, like the Kind/auto refusals).
- `AgentRegistryGrain.SaveAsync`: force `RetentionMinutes = null` for per-chat-only kinds on insert and update.
- Verify: `dotnet test --filter "GrainStorageSerializerTests|AgentRegistryGrainTests|AgentApiTests"`.

### Step 2 — Bind plumbing  *(after 1)*
- `BindAgentAsync` snapshots to state; `GetAgentBindingAsync` returns it; pass `RetentionMinutes:` at the three bind sites
  listed above; `CreateForkAsync`/`InitializeFromForkAsync` carry it.
- Verify: `dotnet test --filter "AgentGrainTests|EditRegenerateForkTests|ConversationForkingTests"`.

### Step 3 — Retention core: variable window  *(after 2)*
- `RetentionContext` gains the window (or `Apply` takes it); `HistoryRetention` uses it instead of the static;
  `RetentionLimits.Window` stays as the default for the legacy/MCP case.
- Replace `IsRetentionWindowed()` with the effective-window resolver; keep the stamp inside the gate.
- Verify: `dotnet test --filter "HistoryRetentionTests|AgentGrainTests|ChatSearchIndexingTests"` plus the session-budget
  canary test (name via `grep -rn "256" tests/Agent42.Tests/*Budget*`), which must stay unchanged.

### Step 4 — Idle sweep  *(after 3)*
- Reminder registration at bind (non-ephemeral) and in `InitializeFromForkAsync`; reserved-name dispatch in
  `ReceiveReminder`; sweep method (in-flight skip → stamp-if-unstamped → all-expired ⇒ whole-chat delete, else prune +
  persist + post-write reconcile); self-unregister on stale tick; `cancel_all` and `schedule_prompt` guards.
- Verify: `dotnet test --filter "AgentRetentionSweepTests|AgentGrainTests"`; tests drive the tick directly with
  `agent.AsReference<IRemindable>().ReceiveReminder("__retention", default)` and a `TestTimeProvider` (existing shape:
  `AgentGrainTests.cs:647`).

### Step 5 — UI  *(parallelizable with 3–4 once Step 1's field name is fixed; touches only wwwroot)*
- `index.html`: a `<select id="agentRetention">` + note under the continuous checkbox. `dom.js` export.
- `agents.js`: `openAgentDialog` preselects from `agent.retentionMinutes`; option set depends on Continuous — per-chat:
  Never / 1 h / 24 h / 7 d / 30 d; continuous: **Default (24 hours)** (sends null) / 1 h / 7 d / 30 d; the continuous checkbox
  change swaps the set; `applyKindLock` disables and resets it for research/copilot; `saveAgent` sends `retentionMinutes`
  (null for Never/Default). Note text: applies to chats started after saving; an ongoing continuous chat keeps its value.
- **Bump `CACHE` in `sw.js`** and update the tests that pin it: grep `agent42-shell-v` in `tests/` — today that is
  `ClientToolsAssetsTests`, `PwaAssetsTests`, `ResearchAssetsTests`, `SearchAssetsTests` (the Fleet/Export asset tests only
  fetch `/sw.js`). No new module is expected, so nothing new to make reachable from `main.js`.
- Verify: `dotnet test --filter "AssetsTests"` and a manual DevHost check (`dotnet run --project tools/Agent42.DevHost`,
  http://localhost:61326): open the editor, toggle continuous, save, reopen.

### Step 6 — Docs  *(last)*
- `/gotcha`: extend the entry at `.agentic/gotchas.md` ~:249-281 (the flag is no longer the only retention signal; value is
  snapshotted; refresh its `Verified:` date) and add the reserved-reminder-name trap (`cancel_all_scheduled_prompts` +
  `schedule_prompt`) and the two dangling-id pointers (chat route, open tab).
- `/sync-docs`: `docs/features/custom-agents.md`, `privacy.html` :98, and `CLAUDE.md` — add the new ADR to the "Stored agents"
  feature-map row (~:189) and to the "ADR 0016/0068" mention (~:140).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | `RetentionMinutes` survives Cosmos JSON + binary round-trip on `AgentDefinition`/`AgentGrainState`; a document without the key reads null | `AgentGrainState_RetentionMinutes_*`, `AgentDefinition_*` | `tests/Agent42.Tests/GrainStorageSerializerTests.cs` (+ golden in `GoldenStates.cs` untouched = old-doc proof) |
| AC2 | POST/PUT stores a preset; an unknown value is 400; research/copilot kinds are forced to null | `Save_*Retention*` | `AgentApiTests.cs`, `AgentRegistryGrainTests.cs` |
| AC3 | A chat bound after the setting carries it; editing the agent afterwards leaves the existing chat unchanged; a fork carries it | `Bind_SnapshotsRetention`, `Fork_CarriesRetention` | `AgentGrainTests.cs`, `EditRegenerateForkTests.cs` |
| AC4 | Per-chat agent chat with 60 min: turns older than 60 min are deleted on the next persisted turn, newest kept, search ordinals reconciled, `TurnAttachments` aligned | `PerChatRetention_PrunesOnNextTurn` | `AgentGrainTests.cs` / `HistoryRetentionTests.cs` |
| AC5 | Per-chat agent chat with no setting is never stamped or pruned (canary byte budget unchanged) | existing session-budget test + `NoSetting_PerChat_NotStamped` | `AgentGrainTests.cs` |
| AC6 | Continuous + unset = 24 h as today; continuous + 7 d = 7 d; MCP conversation stays 24 h even if bound with a value | `Continuous_Unset_Keeps24h`, `Continuous_Explicit_UsesValue`, `Mcp_Ignores…` | `HistoryRetentionTests.cs`, `AgentGrainTests.cs` |
| AC7 | Bind registers the sweep reminder iff a value is set and the bind is not ephemeral; a fork registers it | `Bind_RegistersSweepReminder`, `IncognitoBind_RegistersNone` | `AgentRetentionSweepTests.cs` (new) |
| AC8 | Sweep tick, all messages expired: whole chat deleted — state cleared, index entry removed, share link removed, reminders unregistered | `Sweep_AllExpired_DeletesWholeChat` | `AgentRetentionSweepTests.cs` |
| AC9 | Sweep tick, some expired: leading turns pruned and persisted, chat stays listed, search reconciled after the write | `Sweep_PartlyExpired_PrunesAndKeepsChat` | `AgentRetentionSweepTests.cs` |
| AC10 | Sweep skips while a turn is in flight; unstamped history is stamped, not deleted; a stale tick unregisters itself | `Sweep_SkipsInFlightRun`, `Sweep_UnstampedIsKept`, `Sweep_StaleTickUnregisters` | `AgentRetentionSweepTests.cs` |
| AC11 | `cancel_all_scheduled_prompts` leaves the sweep reminder; `schedule_prompt` refuses the reserved prefix; the sweep never runs the model/push/usage path | `CancelAll_KeepsSweep`, `SchedulePrompt_RefusesReservedName`, `Sweep_RunsNoModel` | `AgentRetentionSweepTests.cs` |
| AC12 | Period function: `Clamp(W/4, 15 min, 6 h)` | `SweepPeriod_*` | `HistoryRetentionTests.cs` |
| AC14 | Sweep delete of a chat-app conversation resets its route, so the next inbound message mints a fresh bound chat | `Sweep_ResetsChatRoute` | `tests/Agent42.Tests/` chat-gateway test file (grep `ChatRouteGrain`) |
| AC15 | A fork of an old chat is not swept at its first tick (messages re-stamped at fork) | `Fork_WithRetention_RestampsMessages` | `EditRegenerateForkTests.cs` / `AgentRetentionSweepTests.cs` |
| AC16 | An empty bound chat is deleted at its tick; a run in flight prevents it | `Sweep_EmptyBoundChat_Deletes` | `AgentRetentionSweepTests.cs` |
| AC17 | Edit/regenerate (`PersistTruncationAsync`) on a windowed per-chat agent chat prunes and reconciles the index | `EditRegenerate_PrunesWindowedChat` | `EditRegenerateForkTests.cs` |
| AC13 | Editor has the select, option sets per mode, disabled for per-chat-only kinds, payload carries `retentionMinutes`; `sw.js` CACHE bumped | source-text guard in a new `AgentRetentionAssetsTests.cs` (follow `ResearchAssetsTests` shape) + updated CACHE pins | `tests/Agent42.Tests/*AssetsTests.cs` |

Mutation-check at least: the in-flight guard (remove it → AC10 test red), the reserved-name guard in `CancelAll` (AC11), and the
`ReconcileAfterPruneAsync`-after-write order (AC9), as ADR 0068 did for its rules.

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|------|-----------|-------------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff) |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776, enforced by `dotnet build` (split the sweep: decision / delete / prune+persist) |
| `vulnerable_packages` | 0 | CI package-freshness job (no new packages planned) |

Plus the standing commands: `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.
Report SKIPPED, never an estimate, for any gate not actually measured.

## Out of scope

- Per-conversation (not per-agent) retention, a user-level default, or operator config.
- Applying a changed value to chats that already exist (a "live" mode).
- Retention for research/copilot agents, plain (agentless) chats, MCP conversations, fleet tasks, or the fleet analyzer.
- Deleting attachment/blob bytes, Hindsight memory, or `/logs` content (ADR 0068's residuals stand).
- A UI showing when/what was pruned or a warning before a chat disappears.
- A size-based cap (the "honest follow-up" ADR 0068 names).

## Review

`critic` pass on the plan (2026-11-23). No ADR contradiction beyond the intended 0068 reversal; Id numbers, bind sites,
reminder-enumeration sites, incognito bind behaviour and test-silo reminder support were verified against code.

| # | Finding | Handling |
|---|---------|----------|
| 1 HIGH | Sweep delete leaves the chat-app route and an open browser tab pointing at the deleted id | **Accepted.** Route reset added (decision + AC14). Open-tab case accepted and documented (same as deleting from another tab today); flagged for user confirmation at approval |
| 2 MEDIUM | Fork keeps the source's old `CreatedAt` and would be swept at its first tick | **Accepted.** Re-stamp at fork (context bullet + AC15) |
| 3 MEDIUM | Empty bound chat has no newest message; would tick forever | **Accepted.** Empty chat is deleted at a tick (context bullet + AC16) |
| 4 LOW-MED | Persist-site list missed edit/regenerate | **Accepted.** Callers listed, AC17 added |
| 5 LOW | In-flight guard wording | **Accepted.** Reworded to `_runsInFlight > 0`, before `PublishUsageApplication()` |
| 6 LOW | Doc debt (CLAUDE.md feature map, CACHE pin list, gotcha line numbers/date) | **Accepted.** Step 5 and Step 6 corrected |

No findings rejected.

## Divergences from the plan (as executed, 2026-11-23)

- **ADR number 0140** as predicted; it carries `amends: [0068]` and 0068 got an "Amended by" pointer.
- **MCP precedence:** `EffectiveRetentionWindow()` checks the MCP id prefix BEFORE the snapshotted value, so an MCP conversation is always 24 h (AC6 says "even if bound with a value"; the plan's rule order would have let the value win).
- **Empty-session detection:** a fresh session has no in-memory history list, so the sweep treats "no list and nothing persisted" as an empty bound chat (AC16) and "no list but a persisted session" as unknown (left alone).
- **Sweep order:** `ApplyRetention()` (stamp + prune) runs first, then the "newest message expired" check, so the 365-day stamp clamp applies to the decision. Persist happens when something was pruned or stamped.
- **Whole-chat delete is duplicated** in `AgentGrain.RetentionSweep.cs` (purge, index remove, share-link remove, route reset) rather than extracted: Backend and Agents have no shared home for it.
- **Route reset only when the route still points at the swept conversation** (a route that moved on via `/new` or a rollover is left alone).
- **Mutation checks:** in-flight guard (red) and cancel-all guard (red) confirmed. The reconcile-after-write order check stayed GREEN: the in-memory index cannot observe write order, and ADR 0068 has no order test either. Unproven residual.
- **Not done:** the manual DevHost check of the editor (a Backend instance was already running and holding the ports).
- **Build note:** a running `Agent42.Backend` locked `bin/`, so all runs used `--artifacts-path .build-alt` (gitignored via `bin/`/`obj/`).
- **Critic follow-ups taken:** ADR + docs now state that API-key and email runs are swept too, the editor 24 h edge, and that a PUT omitting the field clears it; the unused `agentRetentionNote` export was removed.
- **Open for the user:** the plan flagged the open-tab dangling id as "user to confirm at approval"; it was implemented as accepted-and-documented.
