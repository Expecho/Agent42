# Plan: the fleet analyzer as a panel on /fleet/ (fixed tools, no picker)

planned-against: main @ 139b617

## Overview
ADR 0122 shipped the analyzer as a selectable chat tool key (`fleet_analyzer`, eight tools). The user wants it **inside the
fleet UI instead**: a panel on `/fleet/` where they talk to an analyzer that has ONLY the fleet tools, with no tool picker.
Why now: diagnosing a stuck task meant leaving the page, picking the key in a chat, and re-explaining which task.

## Design (the user's request + the most likely reading; the executor does not re-decide these)
- **A new built-in agent kind `fleet-analyzer`** (`AgentKinds`), NOT a stored/user-editable agent and NOT user-selectable
  (`IsUserSelectable` stays false for it). It is the ADR 0082 research-kind shape, minus the registry seed: the kind itself
  decides the tools, so there is no selection, no picker, and nothing the user can widen.
- **One persisted `AgentGrain` conversation per task**, conversation id `fleet-analyzer-{taskId}`, never touched into the
  `IConversationIndexGrain` (so it never shows in the chat sidebar). The panel reloads its history; a "Clear" button purges it.
- **Composition**: for this kind `ComposeAgent` builds a plain `ChatClientAgent` with exactly `FleetAnalyzerTools()` plus
  `get_date_time`; NO context providers (memory, skills, RAG), no hosted tools, no MCP, no Copilot loop, whatever
  `EnabledTools`/`_userDefaultEnabledTools` say. A no-tools model gets no analyzer (the panel's picker lists tool-capable models only).
- **Persona**: a fixed system prompt (in a new `FleetAnalyzerDefaults`, like `ResearchAgentDefaults`) stating the evidence-
  not-instructions rule (ADR 0122, CLAUDE.md rule 11) and telling the model to diagnose before acting.
- **Scoped to the open task**: the endpoint prefixes each turn's text with the task id the panel is on
  (`[Open task: <id>]`), so "why did it stop?" needs no id. The tools still take any of the user's own task ids.
- **Endpoints** (in `AgentApi.Fleet.cs`, already reachable from `MapAgentApi`): `POST /api/fleet/tasks/{id}/analyzer`
  (NDJSON, same event shape as `/api/chat/{id}` via `ChatStreamJson.Options`), `GET .../analyzer` (history, via
  `GetHistoryAsync`), `DELETE .../analyzer` (purge). All verify the task exists for `GetUserId()` first (404 otherwise), so an
  id never creates a grain for a task that is not the caller's. The model comes from the request; the endpoint binds
  `AgentBinding(agentId "fleet-analyzer", name "Fleet analyzer", fixed prompt, model, null, Kind)` itself.
- **Chat key removed**: `fleet_analyzer` leaves `ToolCatalog` (key, its eight tool-name rows, the catalog entry), the
  `FleetAnalyzerTools()` spread in `AgentGrain.BuildAgent.cs`, the Copilot exclusion and `AgentMcpRunner.ExcludedFromAskAgent`.
  The tools themselves, the port methods and `FleetAnalyzerLimits` are unchanged. The chat no longer offers the analyzer;
  `start_fleet_task`/`get_fleet_task` (ADR 0115) stay.
- **Frontend**: `fleet.js` is a classic script (cannot import `js/` modules). Add a small panel (a toggle in the task
  detail, a message list, a composer, Clear) with its own NDJSON reader, rendering model text ONLY as
  `DOMPurify.sanitize(marked.parse(...))` and everything else as text (the file's existing rule). Reuse the existing
  tool-capable model pick (`toolCapable`, `S.models`). Tool calls render as folded one-liners. `/fleet/` is outside SHELL,
  so no `sw.js` CACHE bump is needed (verify: `sw.js` line ~104 returns early for `/fleet`).

## Affected areas (verified against the tree)
- `src/Agent42.Agents/StoredAgents/AgentKinds.cs` — new kind + `IsFleetAnalyzer`; `IsPerChatOnly` includes it.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (`ComposeAgent`, the `.. FleetAnalyzerTools()` spread ~l.328) — branch for
  the kind; remove the spread. Watch S3776 (15): put the kind's composition in its own partial (`Fleet/AgentGrain.FleetAnalyzerAgent.cs`).
- `src/Agent42.Agents/Fleet/AgentGrain.FleetAnalyzerTools.cs`, `FleetAnalyzerLimits.cs` — keep; `ToolNames` still used.
- `src/Agent42.Agents/Tools/ToolCatalog.cs` (~l.194, 248, 277-284, 360-363) — remove `FleetAnalyzer`.
- `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs:99-102` — drop the `FleetAnalyzerLimits.ToolNames` exclusion (no longer reachable); keep it
  only if a test shows the names can still leak into a Copilot chat.
- `src/Agent42.Backend/Mcp/AgentMcpRunner.cs:240` — drop `ToolCatalog.FleetAnalyzer`.
- `src/Agent42.Backend/Fleet/AgentApi.Fleet.cs` (+ a new `AgentApi.FleetAnalyzer.cs` partial, mapped from the same `Map*` method) — endpoints.
- `src/Agent42.Backend/wwwroot/fleet/{index.html,fleet.js,fleet.css}` — the panel.
- Tests: `FleetAnalyzerTests.cs`, `FleetAnalyzerToolTests.cs`, `ToolCatalogTests.cs`, `ToolSelectionApiTests.cs`, `AgentMcpServerTests.cs`,
  `CopilotAgentTests.cs` (they pin the key); new `FleetAnalyzerPanelApiTests.cs`.
- Docs: `.agentic/decisions/0123-*.md` (via `/decide`), `0122` status/`amends`, `CLAUDE.md` feature-map row (fleet row: "chat-side
  analyzer" → "analyzer panel on /fleet/"), `.agentic/gotchas.md`.

## Context & constraints
- ADR 0122 decisions that STILL hold and must be preserved: reads scoped by `_userId` never an argument; output framed as data;
  a re-run is NOT an origin task (questions go to the page); a re-run only narrows; telemetry = caller's own spans only;
  `FleetStartRefusal` (one start per turn). Evidence: `.agentic/decisions/0122-fleet-analyzer.md`, `AgentGrain.FleetAnalyzerTools.cs`.
- ADR 0122 decision 5 ("on for a `null` selection, appended last") and its rejected "`/fleet/` panel with its own agent" are what this
  reverses: that alternative was rejected for the first slice ("a second agent surface with its own model choice, history and
  streaming path"). Here it is the user's explicit ask, and the cost is cut by reusing the grain, `ChatAsync` and the NDJSON shape;
  only the thin endpoint and panel are new. The new ADR must say so and supersede 0122's delivery (decisions 1-4, 6 and the egress
  section carry over). Grep `.agentic/decisions/` for other mentions before writing it.
- Egress: no new destination (same user-own fleet content to the user's own provider as 0122). Say so in the ADR (rule 13).
- Rule 6 (`TurnContext`) and rule 7 (registrations): no new registration is expected; `IFleetLauncher` is already a direct grain dependency.
- Conversation id `fleet-analyzer-{taskId}` lives in the user's own `AgentKey`, so it cannot cross users. The kind also changes persisted
  state: `AgentKind` is an existing field, a new string value needs no migration; still prove a round-trip on the Cosmos JSON path only if a new field is added (none planned).
- Read before editing: gotchas for AgentGrain composition (grep "ComposeAgent"), the research-kind gotchas, and the fleet `index.html` script rule.

## Steps
1. **Kind + composition** (Agents). `AgentKinds.FleetAnalyzer`, `FleetAnalyzerDefaults` (prompt), the composition partial, the `ComposeAgent` branch.
   Verify: new tests in `FleetAnalyzerTests.cs` — the composed agent has exactly the eight tools + date, no providers, whatever `EnabledTools` says.
2. **Endpoints** (Backend). POST (stream), GET (history), DELETE; 404 for another user's/unknown task; never indexed. Depends on 1.
   Verify: `FleetAnalyzerPanelApiTests` — streams for own task, 404 for foreign, not in `/api/conversations`, history round-trips, DELETE purges.
3. **Remove the chat key** (Agents/Backend). ToolCatalog, BuildAgent spread, Copilot + ask_agent exclusions; update the pinning tests. Parallelizable with 4 after 1.
   Verify: `ToolCatalogTests`, `ToolSelectionApiTests`, `AgentMcpServerTests`, `CopilotAgentTests` updated and green; a grep for `fleet_analyzer`/`ToolCatalog.FleetAnalyzer` finds none outside the ADR history.
4. **Panel** (wwwroot/fleet). Toggle, history load, streaming render, tool-call one-liners, Clear, model pick reuse, error states (no model, 404, 409 busy).
   Verify: existing fleet source-text guards still pass; add a guard that the panel sets model text only via `DOMPurify.sanitize(marked.parse(`; run DevHost (`dotnet run --project tools/Agent42.DevHost`) and check the panel in the browser (canned LLM, so tool calls will not occur: say so).
5. **Docs** — `/decide` for ADR 0123 (supersedes 0122's delivery; update 0122's frontmatter), CLAUDE.md fleet row, a gotcha only if a trap surfaced. `/sync-docs`.
6. **Full gate** — `dotnet build`, `dotnet test Agent42.slnx`, `dotnet format --verify-no-changes`.

## Acceptance criteria → tests
| AC | Test | File |
|----|------|------|
| The analyzer kind composes only the eight fleet tools + date, regardless of `EnabledTools` | `Kind_ComposesOnlyFleetTools` | `tests/Agent42.Tests/FleetAnalyzerTests.cs` |
| It has no context providers, hosted tools or MCP/skills | `Kind_HasNoProviders` | `FleetAnalyzerTests.cs` |
| `fleet_analyzer` is no longer a selectable key and no chat gets the tools | updated `ToolCatalogTests` + `ChatDoesNotOfferAnalyzerTools` | `ToolCatalogTests.cs`, `FleetAnalyzerToolTests.cs` |
| The panel endpoint streams for the user's own task | `Panel_StreamsForOwnTask` | `FleetAnalyzerPanelApiTests.cs` |
| Another user's / unknown task id is a 404 and creates no grain | `Panel_ForeignTask_404` | `FleetAnalyzerPanelApiTests.cs` |
| The analyzer conversation never appears in the sidebar index | `Panel_NotListed` | `FleetAnalyzerPanelApiTests.cs` |
| History reloads and Clear purges it | `Panel_HistoryAndClear` | `FleetAnalyzerPanelApiTests.cs` |
| The ADR 0122 tool guarantees still hold through the panel (scoped by user, one start per turn) | existing `FleetAnalyzerToolTests` kept green | `FleetAnalyzerToolTests.cs` |
| Panel renders model text only through DOMPurify+marked | source-text guard | `FleetAnalyzerPanelApiTests.cs` (or the existing fleet guard file; executor greps) |

## Quality gate checklist (from `.agentic/quality-gates.yaml`, read 2026-11-09)
- `coverage_new_code_pct_min`: 80 — `node .agentic/scripts/check-new-code-coverage.mjs` after `dotnet test ... --collect:"XPlat Code Coverage"`.
- `cognitive_complexity_max`: 15 — S3776, enforced by `dotnet build`.
- `vulnerable_packages`: 0 — no package is added by this plan.
- Report any gate not measurable as SKIPPED; never estimate an actual.

## Out of scope
- App Insights / Log Analytics access, a task id on spans, logs (ADR 0122's "not built" list stands).
- Making re-runs origin tasks (auto-approved) or giving the panel Copilot/shell tools.
- A global analyzer outside a task (the panel is per open task; the model can still list other tasks via the tool).
- The share link view (`/shared/fleet`): the panel is owner-only and not shown there.
- Streaming reconnect/stop-generation parity with the chat (Stop reuses `POST /api/chat/{id}/stop` only if trivial; else a follow-up).

## Divergences from the plan (as built)
- **Tool name:** the date tool is `get_date` (the plan said `get_date_time`).
- **Copilot exclusion kept:** `IsExcludedFromCopilot` still lists the eight names. With the catalog rows gone they read as
  always-on to the selection, and a Copilot chat runs a shell unasked, so the cheap defence stays (plan step 3 proposed dropping it).
  `ExcludedFromAskAgent` and the catalog key/rows/descriptor were removed as planned.
- **Search leak (not in the plan):** `GET /api/search` does not filter by the conversation index, so the grain skips
  `IndexNewTurnsAsync` for the kind. Pinned by `Panel_NotListed` (with a positive control). A gotcha records it.
- **Task delete purges the analyzer conversation** (not in the plan); `DELETE .../analyzer` has no task check, so a deleted
  task stays clearable, and it stops a running turn before purging.
- **Model handling:** POST requires a model and refuses an unknown, no-tools or Copilot-only one (400); the model is passed on
  every turn, bound once. The panel's picker filters `!copilotOnly` too.
- **History strips the persisted `[Open task: <id>]` prefix.**
- **Wiring:** `MapFleetAnalyzerEndpoints` is called from `MapAgentApi` and listed in `ProjectLayoutTests` (the layout test
  requires it), not from `MapFleetEndpoints`.
- **Tests landed in:** `FleetAnalyzerToolTests.cs` (kind tests, cluster-based; the tool tests now bind the kind),
  `FleetAnalyzerPanelApiTests.cs` (on `FleetApiFixture`), `FleetPageAssetsTests.cs` (the panel guard, slicing the panel
  section). `FleetAnalyzerTests.cs` is the Backend launcher suite with no silo, so the plan's mapping to it was wrong.
- **ADR 0123** amends 0003 and 0122 (0122 stays `accepted`, with a forward note, the 0017 pattern).
- **Not done:** a live browser check of the panel in DevHost (the canned LLM makes no tool calls anyway), Stop/reconnect parity.

## Review
(critic findings and how each was handled go here)
