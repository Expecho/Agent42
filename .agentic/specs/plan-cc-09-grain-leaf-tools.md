# Plan cc-09: AgentGrain — move leaf tool sets into collaborators built per activation

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md`; **depends on plan cc-08 merged.** Revised after critic review (see Review).

## Overview

Leaf tool partials are cohesive tool sets that reach one service through a grain field. Moving each into its own class
(constructed with `new` in `OnActivateAsync`, the ADR 0106 pattern) gives each a name, a constructor that states its real
dependencies, and a unit-testable surface. **The goal is cohesion, not a ctor-count number.** Measured: only four ctor deps
are used by tier-A partials alone (`_geocoder`, `_fleet`, `_pushSender`, `_memory`); `_chatSearchIndex` also feeds
`Search/AgentGrain.Search.cs` and stays. Expect the ctor to go from 19 to about 15, **not 10**. The programme no longer
targets a ctor number for the grain (see master plan).

An earlier draft proposed one DI-registered `AgentToolSets` aggregate. **Rejected** (critic, verified): it hides four deps
from ADR 0105's direct-ctor-dependency startup check (`ThrowIfPortsMissing`,
`src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs`), the Backend cannot be booted in a test to prove the transitive
resolve (ADR 0105 "Alternatives rejected"), it reverses ADR 0106 §4 ("not DI-registered, the grain ctor does not change"),
and it is a parameter object that games the metric.

## Affected areas (scout-verified at 02ebdf0; partial → fields it uses)

Tier A: `Location/AgentGrain.LocationTool.cs` (151; `_geocoder`, `_logger`), `Search/AgentGrain.PastChatTools.cs` (156;
`_chatSearchIndex`, `_userId`, `_logger`), `Fleet/AgentGrain.FleetTools.cs` (139) + `FleetAnalyzerTools.cs` (117) +
`FleetAnalyzerAgent.cs` (41) (`_fleet`, `_userId`, `_modelId`, `_chatClientFactory`), `Push/AgentGrain.PushTool.cs` (127;
`_pushSender`, `_modelId`, `_userId`; uses `TurnContext`) + `Push/AgentGrain.Push.cs` (26; **also reads `_state`**),
`Memory/AgentGrain.Memory.cs` (78; `_memory`, `_userId`, `_userDefaultEnabledTools`), `Conversations/AgentGrain.FollowUps.cs`
(122), `Webhooks/AgentGrain.Webhooks.cs` (62; **2 `_state` reads**, treat as tier B: leave unless trivial),
`RenderingHints.cs` (21, static; leave).
Wiring hub: `Tools/AgentGrain.BuildAgent.cs:200-265` (`AIFunctionFactory.Create(this.Method)`), `:297-316`.
**Copilot:** every moved `AIFunction` still goes through `SchedulerBoundAIFunction` via `CopilotFunctions`
(`.agentic/gotchas.md` ~line 4000: Copilot tools run on the SDK's RPC thread). Keep that wrap on the new instances.
Pins: `ProjectLayoutTests.cs:131-165 AgentGrainParts` (update when a partial is deleted);
`PushToolLimitsTests.cs:118-123 PushTool_DoesNotLogNotificationText` (reads `Push/AgentGrain.PushTool.cs`, asserts exactly
one `_logger.` call: the privacy guarantee moves with the code, re-point the test and keep the assertion).
**Docs that name these paths** (hard CI check `.agentic/scripts/check-doc-paths.mjs` scans CLAUDE.md, gotchas.md and every
non-superseded ADR): ADR 0039 lines ~95-107 (`AgentGrain.FollowUps/LocationTool/Push/PushTool/Memory.cs`), ADR 0075:56
(`AgentGrain.PushTool.cs`), gotchas.md at ~592, 618, 998, 2153-2194, 2933-2984, 3285, 4732. For every deleted/renamed path
run `grep -rn "<path>" CLAUDE.md .agentic` and fix the reference **path-only** (the decisions README forbids editing an
accepted ADR's decision content; a path-only edit in an ADR's evidence/reference text is allowed; state it in the MR).

## Context & constraints

- **Pattern:** `internal sealed class XxxTools(deps…)` in the same feature folder, namespace `Agent42.Agents`, constructed in
  `OnActivateAsync` next to the four ADR 0106 services (`new`), taking grain values it needs (`userId`, `modelId`,
  `TurnContext`) per call or through a small identity record. The grain ctor shrinks only because the *field* moves into
  the tools class; the DI param remains on the grain (the grain still receives the service and passes it in). So **ctor
  params do not drop**, the grain just stops *using* the services directly. If, at the end, a service has no remaining
  grain use, say so in the ADR note; do not hide deps behind an aggregate to reduce the count.
- Tool descriptions (the text the model reads, CLAUDE.md rule 11) and keys move **verbatim**; a renamed key lands OFF for
  anyone who pressed Save.
- **Do not touch** anything that writes `_state`, `_session` or `_agent`.
- Optional ctor params (`= null`) exist for hosts that omit a feature (DevHost: canned fleet/router). Keep "absent" semantics.
- Never log more than is logged today (push text, location, past-chat content).
- ADR: this amends ADR 0106's service list (adds tool sets built with `new`). That is an amendment *with alternatives*
  (aggregate rejected above): record it via `/decide` as a new ADR that `amends: [0106]` with `AdrLinkTests`-valid links, not
  a note. Take the **next free number from origin/main at merge time**, not from the working tree (decisions README).

## Steps

0. Golden snapshot: from current code, dump `SelectableTools` keys + tool descriptions + parameter schemas into a test
   fixture (check `ToolCatalog` tests first: one may already exist). This is the byte-identical guard for steps 1-5.
1. Extract `LocationTools`. Verify `AgentGrainTests.GetCurrentLocation_ExposesNoParameters` (`AgentGrainTests.cs:2121`) + location tests.
2. Extract `PastChatTools`. Verify past-chat tests (ranking is ADR 0112).
3. Extract Fleet tool sets. Verify fleet-from-chat and analyzer tests.
4. Extract `PushTools` (+ `Push.cs` only if `_state` access can stay in the grain by passing a value in); re-point
   `PushTool_DoesNotLogNotificationText`. Verify `PushToolLimitsTests`, push tests,
   `ReminderFiringMidStream_DoesNotResetTheStreamingTurnsPushBudget` (`AgentGrainTests.cs:848`).
5. Extract memory provider builder, `FollowUps` if they cleanly fit (they may stay: say so).
6. Delete emptied partials; update `AgentGrainParts`; doc-path fixes (see Affected areas). `/decide` ADR; `/sync-docs`.
7. Re-run `code-shape`; record grain total lines and ctor params before/after.

Steps 1-5 edit `BuildAgent.cs`: sequential.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Tool keys, descriptions and schemas byte-identical | golden snapshot (step 0), run after every step | `ToolCatalogSnapshotTests.cs` (new, or the existing catalog test) |
| Copilot still receives the scheduler-bound wrap for moved tools | existing Copilot function tests (`CopilotFunctions` wrap); add one asserting a moved tool is wrapped | `CopilotAgentTests.cs` (find the real file) |
| Push text never logged | `PushTool_DoesNotLogNotificationText` (re-pointed) | `PushToolLimitsTests.cs` |
| Doc references to deleted paths fixed | `check-doc-paths.mjs` clean | CI |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean; full `dotnet test Agent42.slnx`.

## Out of scope

Tier B/C partials (plan 10 / never); changing tool behaviour, keys or descriptions; the reminder API; any ctor-count target.

## Review

Critic (2025-11 review of this plan set): HIGH #1 accepted — aggregate rejected, per-activation `new` instead, ctor target
dropped, ADR via `/decide`, ADR 0105 safeguard preserved, Copilot wrap cited. LOW (Push.cs/Webhooks read `_state`) accepted —
re-tiered above. Doc-path finding (#8) accepted — see Affected areas.
