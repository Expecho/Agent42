planned-against: main @ fdc61c3

# Plan: fleet analyzer (chat tools to diagnose and act on fleet tasks)

## Overview
A fleet task that cannot complete is today diagnosable only by opening `/fleet/` and reading the event log by hand (or
Blob/App Insights with operator RBAC). Goal: the user asks their own chat agent "why can't my latest fleet run
complete?" and the agent can read the task, its event log, workspace, the fleet config and the live telemetry, and can
act: stop a task, or start a corrected re-run. **No new team, no new grain, no new page**: it is one new selectable chat
tool key, `fleet_analyzer`, a sibling of `fleet` (ADR 0115), reaching the fleet through the existing `IFleetLauncher`
port.

## Affected areas (verified against the tree at fdc61c3)
- `src/Agent42.Agents/Fleet/IFleetLauncher.cs` — port; `DisabledFleetLauncher` lives here. Add the analyzer operations.
- `src/Agent42.Agents/Fleet/FleetToolLimits.cs` — names, descriptions, caps, `WrapAsData` framing (reuse).
- `src/Agent42.Agents/Fleet/AgentGrain.FleetTools.cs` — exemplar for the grain-side tools (`FleetStartRefusal`,
  `TurnContext.CurrentOrClosed.RecordFleetStart()`).
- new `src/Agent42.Agents/Fleet/AgentGrain.FleetAnalyzerTools.cs` — the new tools.
- `src/Agent42.Agents/Tools/ToolCatalog.cs` — `Fleet` key (:185), `ToolNameToKey` (:266), descriptors (:338), appended last.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:325` — `.. FleetTools()` registration site.
- `src/Agent42.Backend/Fleet/FleetLauncher.cs` (+ new `FleetLauncher.Analyze.cs` partial or `FleetAnalyzer` helper class)
  — Backend implementation over `FleetTaskService`/`IFleetStore`/`FleetOptions`/`LiveTelemetryBuffer`.
- `tests/Agent42.Tests/FakeFleetLauncher.cs` — must implement the new members.
- docs: new ADR 0122, `.agentic/gotchas.md` entry, CLAUDE.md feature-map row (fleet area).

## Context & constraints
- **Why the port, not a new service:** the grain holds `IFleetLauncher` as a direct ctor dependency and 7 test silos
  register it (`grep IFleetLauncher tests`). A second port would touch every one; extending the existing port touches
  `FleetLauncher`, `DisabledFleetLauncher`, `FakeFleetLauncher` only. ADR 0105 rule 7 stays satisfied.
- **Reads already exist** and are user-scoped by `userId` on every `FleetStore` method: `GetAsync`/`ListAsync`,
  `Store.ReadEventsAsync`, `ListWorkspaceAsync`/`GetWorkspaceFileAsync`, canvas getters (report: FleetTaskService.cs
  :182-284, IFleetStore.cs:22-64). userId comes from the grain's `_userId`, never a tool argument (as `get_fleet_task`).
- **The plan and agent definitions are NOT on `FleetTaskRecord`** — only in the event log (`plan.submitted`,
  `plan.rejected`, `team.started`, `verdict`, `attempt.failed`). The analyzer must derive them from events.
- **Failure vocabulary** the summary must surface (FleetTaskService.Run.cs, FleetTeamRunner*.cs): status
  `running|done|stopped|failed|interrupted`; budget exhaustion is `stopped` with the budget reason, not `failed`;
  judge rejection and max attempts are NOT task statuses (events `verdict`, `attempt.failed`, `team.finished.failed`);
  `delegation.refused`; `planner.failed`; `agent.failed`; `workflow.error`; `tool.result.failed`; restart gives
  `interrupted`. Event schema: `FleetEventLog.cs:8-22` envelope `{seq,type,time,data}`.
- **No retry/resume exists.** `CanFollowUp` is only true for `done` with a result. "Fix" for a failed/stopped/
  interrupted task = start a NEW task with the same request (`CopyInputsOf` to keep attached files) plus corrected
  options (`EnabledTools` override ADR 0099, `MaxTokens`), via `FleetTaskService.SubmitAsync`.
- **Decision (mine, user said "act on the fleet"): re-runs started by the analyzer are NOT origin tasks.** An origin
  task (`OriginConversationId` set) is auto-approved, including Copilot shell/MCP writes (ADR 0115 decision 2).
  Analyzer-started tasks would otherwise inherit that for text the model composed from untrusted event logs. So: no
  origin, no auto-approve; questions surface on the `/fleet/` page; the tool returns the deep link. Cost: the result is
  not delivered back into the chat. Rejected: origin tasks (auto-approved shell steerable by injected log text).
- **Untrusted content.** Event text, tool results and workspace files are model/tool output. Every returned blob goes
  through `FleetToolLimits.WrapAsData`-style framing (new tag pair is fine, but reuse the neutralizer) and a hard cap
  (rule 5, 2 MB session, ADR 0004).
- **Loop guard.** The mutating tools reuse the `fleet` guards: refuse when `Ephemeral`, refuse in a `FleetResultTurn`,
  at most `MaxStartsPerTurn` starts per turn (`RecordFleetStart`). `stop` is not rate-limited (it is the safe action).
- **Telemetry scope (findings):** `/api/diagnostics/*` and `LiveTelemetryBuffer` are GLOBAL and carry every user's
  full prompts (`AgentApi.Diagnostics.cs:25-32`, ADR 0054). No fleet task id is stamped on any span or log
  (`grep SetTag` in `Agent42.Agents/Fleet` finds nothing), so telemetry cannot be filtered by task. Therefore:
  - **spans:** returned only when `user.id` equals the caller (the tag `UserTelemetrySpanProcessor` stamps; verify the
    exact key name in step 3) — never another user's prompts.
  - **logs: NOT in this slice** (critic #3). Log lines carry only TraceId/SpanId, no `user.id`, so they cannot be
    scoped to the caller; handing them to even the owner's model provider would be egress of OTHER users' content
    (an ADR 0013 extension nobody approved). Follow-up, recorded in the ADR.
  - Span filter is fail-closed: a span without a matching `user.id` tag is excluded; compare against the canonical
    (aliased) id the grain's `_userId` holds; cap every tag/attribute value (rule 5). Windowed by time/text because
    there is no task correlation; the tool says so.
- **Azure Monitor / Log Analytics is OUT of this slice.** No query client exists (`Directory.Packages.props` pins only
  the exporter), the managed identity (system-assigned, `container-app.bicep:146`) has no monitoring role, and App
  Insights has chat content stripped and no logs (ADR 0077). Follow-up needs `Azure.Monitor.Query` resolved via
  `dotnet add package` (hard rule 4) and a Bicep role, with the `az role assignment` commands handed to the user
  (hard rule 5). Record as a follow-up in the ADR; do not execute any RBAC.
- **Selectable key:** `fleet_analyzer` appended LAST in `AllSelectableToolKeys`; ON for `null` selection, OFF for
  anyone who ever pressed Save (CLAUDE.md rule 11, ADR 0021) — not migrated. The tool descriptions are the rule the
  model reads. An `ask_agent` binding strips it like `fleet` (ADR 0070/0115): check `AgentMcpRunner.ExcludedFromAskAgent`.
  Research agent and fleet agents do not get it (same as `fleet`).
- **Copilot chats (critic #1): the key is EXCLUDED from `CopilotFunctions`** (`AgentGrain.Copilot.cs:108`). Copilot
  chats run auto-approved shell (ADR 0107); untrusted log text must never reach such a turn. Exclusion + test next to
  `CopilotAgentTests` (`CopilotFunctions_IncludeTheFleetTools` is the sibling).
- **Re-run can only NARROW (critic #5).** The parameter is `disableTools` (keys to turn off), not `enabledTools`; the
  base is the original task's override, or the user's default selection when none (if that can't be resolved, refuse
  `disableTools`). `note` is length-capped and appended to the request as framed data. `maxTokens` is pre-checked
  against `FleetOptions` (`SubmitAsync` REJECTS an over-limit cap, it does not clamp). A re-run is refused with plain
  text while a task is running (one per user), and the result tells the user the task may ask a question on
  `/fleet/` (non-origin tasks use FleetQuestions, FleetTaskService.cs:165).
- **Egress (ADR 0013 extension, rule 13):** no new destination. Fleet content already goes to the user's providers
  (ADR 0097 decision 8); the analyzer returns the user's own task content and own spans to the conversation's model.
  Record it in the ADR as such.
- **Frontend:** no JS change expected (the tool picker is catalog-driven — verify in step 3). If any
  `wwwroot/js/` file changes, bump `CACHE` in `sw.js` (rule 3). `/fleet/` is not in the SW SHELL.
- Governing docs: ADR 0097, 0099, 0105, 0106, 0115, 0062, 0054, 0077; gotchas "fleet's interrupted status computed on
  read", "event log coalesces streamed text", "tool return value persisted into 2 MB session".

## Steps
1. **Port + disabled stand-in + fake** (sequential first). Extend `IFleetLauncher` with: `ListRecentAsync`,
   `AnalyzeAsync(userId, taskId)`, `ReadEventsAsync(userId, taskId, types?, afterSeq, limit)`,
   `ReadWorkspaceAsync(userId, taskId, path?)`, `GetConfigText()`, `ReadSpansAsync(userId, search, limit)`, `StopAsync(userId, taskId)`, `RerunAsync(userId, taskId, note?, enabledTools?, maxTokens?)`. Return text
   (already framed/capped) or small records — decide by what keeps `Agent42.Agents` free of Backend types; prefer text
   for read ops and a `FleetLaunchResult` for rerun. `DisabledFleetLauncher` returns `Unavailable`. Update
   `FakeFleetLauncher`. Verify: `dotnet build Agent42.slnx`.
2. **Backend implementation** `FleetLauncher` (partial) over `FleetTaskService`/`IFleetStore`/`FleetOptions`/
   `LiveTelemetryBuffer`/`IConfiguration`. `AnalyzeAsync` = record fields + event-type histogram + failure events +
   plan summary + last N events, all capped. Rerun = `SubmitAsync` with `CopyInputsOf`, no origin, model per the
   launcher's existing predicate, options clamped by `FleetOptions` limits. Verify: new `FleetAnalyzerTests` over the
   in-memory blobs (reuse `FleetTaskServiceTests` helpers / `FleetRunHarness`).
3. **Grain tools + catalog.** Also: `AgentMcpRunner.ExcludedFromAskAgent` edit, `CopilotFunctions` exclusion, update
   the "Fleet is last" pins (`ToolCatalogTests.cs:202-207,280`), check `AgentGrainTests.cs:2178`,
   `CopilotAgentTests.cs:187/204/216`, `ToolSelectionTests.cs:33`, and `FleetToolCatalog.cs:46` so the analyzer tools
   never reach fleet agents. New `AgentGrain.FleetAnalyzerTools.cs` (shape of `AgentGrain.FleetTools.cs`: try/catch
   returns text, guards from `TurnContext`), `FleetToolLimits` additions (names, descriptions, caps), `ToolCatalog`
   key/mapping/descriptor appended last, `BuildAgent` registration, `AllSelectableToolKeys`, ask_agent strip.
   Verify: `ToolCatalogTests` (append-last, mapping), new `FleetAnalyzerToolTests` (shape of `FleetToolTests`).
4. **Docs.** ADR 0122 (`/decide`: extends 0115/0097, egress paragraph, the no-origin and owner-gated-logs decisions,
   the Azure Monitor follow-up with the exact RBAC/package steps *prepared, not run*), gotchas entry (global
   telemetry + no task id on spans; analyzer reruns are not origin tasks), CLAUDE.md feature-map row. Verify:
   `dotnet test` doc-path/ADR numbering tests; take next free ADR number from `origin/main` before the PR.
5. **Gates + PR.** `dotnet format --verify-no-changes`, `dotnet build`, `dotnet test`, coverage script. Branch
   `feature/fleet-analyzer`, PR to `main`.
Steps 1→2→3→4 are sequential (each compiles against the previous). Nothing is parallelizable.

## Acceptance criteria → tests
| AC | Test | File |
|----|------|------|
| `list`/`analyze` return only the caller's tasks; a foreign id reads as not found | `Analyze_ForeignTask_NotFound` | tests/Agent42.Tests/FleetAnalyzerTests.cs |
| Analyze surfaces status, reason and the failure events (planner/attempt/agent/verdict/budget) | `Analyze_FailedTask_SurfacesFailures`, `Analyze_BudgetStopped_SaysBudget` | FleetAnalyzerTests.cs |
| Event read pages by seq, filters by type, is capped and framed as data (closing tag neutralized) | `Events_Paged_Capped_Framed` | FleetAnalyzerTests.cs |
| Workspace read is capped, path-normalized, user-scoped | `Workspace_Read_Capped` | FleetAnalyzerTests.cs |
| Telemetry spans: only the caller's (aliased id), missing tag excluded, capped | `Spans_OnlyOwn_FailClosed_Capped` | FleetAnalyzerTests.cs |
| Copilot chats don't get the analyzer tools; ask_agent binding strips the key | `CopilotFunctions_ExcludeAnalyzer`, `AskAgent_BindingExcludesFleetAnalyzer` | CopilotAgentTests.cs, AgentMcpServerTests.cs |
| Re-run only narrows tools, caps the note, rejects over-limit maxTokens, refuses while running | `Rerun_DisableTools_OnlyNarrows`, `Rerun_NoteCapped`, `Rerun_OverLimitTokens_Error`, `Rerun_WhileRunning_Refused` | FleetAnalyzerTests.cs |
| Stop/rerun of another user's task reads as not found | `StopRerun_ForeignTask_NotFound` | FleetAnalyzerTests.cs |
| Rerun starts a NEW task with same request + override, not origin, input files copied | `Rerun_StartsNonOriginTask` | FleetAnalyzerTests.cs |
| Mutating tools refuse in incognito, in a fleet-result turn, and a second start in one turn | `Rerun_Ephemeral_Refused`, `Rerun_InFleetResultTurn_Refused`, `Rerun_SecondInTurn_Refused` | tests/Agent42.Tests/FleetAnalyzerToolTests.cs |
| Stop works on a running task and is not start-limited | `Stop_RunningTask`, `Stop_NotLimitedByStartGuard` | FleetAnalyzerToolTests.cs |
| Tool errors come back as text, never throw | `Tool_LauncherThrows_ReturnedAsText` | FleetAnalyzerToolTests.cs |
| Key appended last, tools map to it, off for saved selections, stripped for ask_agent | `Catalog_FleetAnalyzer_*` | tests/Agent42.Tests/ToolCatalogTests.cs |
| Disabled host answers "unavailable" | `Disabled_Analyzer_Unavailable` | FleetAnalyzerToolTests.cs |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)
- `coverage_new_code_pct_min`: 80 — `node .agentic/scripts/check-new-code-coverage.mjs`
- `cognitive_complexity_max`: 15 — S3776, build error (split `AnalyzeAsync` into small helpers)
- `vulnerable_packages`: 0 — no package is added in this slice
- protected branch: `main` only — work on `feature/fleet-analyzer`
- CI also blocks on: build, tests, `dotnet format`, doc-path integrity, ADR numbering.

## Out of scope
- Azure Monitor / Log Analytics / App Insights querying and any RBAC (follow-up, commands prepared for the user only).
- A UI panel on `/fleet/`, an "Analyze with Agent42" button, or any frontend change.
- Resume/retry as a first-class fleet feature; deleting tasks; answering a task's pending prompt from chat.
- Stamping a fleet task id on spans/logs (would make telemetry per-task; worth its own ADR).
- Telemetry LOGS for the analyzer (cross-user; needs a per-user stamp first).

## Review
Critic verdict: 3 HIGH, 4 MEDIUM, rest LOW. All accepted.
- #1 Copilot exposure: exclusion + test. #2 pinned catalog tests: updated in step 3.
- #3 owner logs = cross-user egress: logs dropped from the slice.
- #4 SubmitAsync rejects, not clamps: pre-check + AC. #5 injection into re-run: narrow-only `disableTools`, capped
  note, data framing. #6 non-origin question block / running-task race: refusal text + AC.
- #7 span filter: fail-closed, canonical id, caps. #8 port choice kept (extend `IFleetLauncher`); the ADR records the
  separate-port alternative. #9/#10 ask_agent edit and extra tests added. #11 no frontend change: agreed.
