# Plan cc-07: split FleetLessonService, ConfigHealth and the three long endpoint maps

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md`; run after plan cc-06 (same backend areas; avoids conflicts)

## Overview

Three classes with several jobs each, and three `Map*Endpoints` methods over 100 lines. No test reads these files as text
(scout-verified for `FleetLessonService`/`ConfigHealth`); the endpoint partials are pinned only by one table.

## Affected areas

- `src/Agent42.Backend/Fleet/FleetLessonService.cs` (606 lines, partial) — five jobs: lessons store (`GetAsync :83`,
  `ActiveAsync :100`, settings `:143`, Disable/Enable/Delete `:160-168`, `RecordApplicationAsync :175`); prompt proposals
  (`PromptsAsync :207`, Approve `:216`, Dismiss `:244`, `MutateAsync :275`, `WithUserLockAsync :290`); dream scheduler
  (`RunManualAsync :308`, `TickAsync :316`, `WindowStart :387`); dream run (`ExecuteAsync :419`, `DreamAsync :452`,
  Start/Finish `:529/:546`, `MergeRun :560`); model pick (`PickModelAsync :588`). Sibling files: check for
  `FleetLessonService.*.cs` partials. **Correction (critic, verified):** `FleetSkillProposalService.cs` does not exist. The
  skill-proposal logic is the **`Fleet/FleetLessonService.Skills.cs` partial (161 lines)**, which also uses `WithUserLockAsync`
  — a sixth job the "five jobs" list omits. **Name collision:** `FleetPromptProposals` (`public static class`, prompt apply/merge
  helpers, `Fleet/FleetPromptProposals.cs`), `FleetSkillProposals` and `FleetDreamer` already exist: pick non-colliding names
  (e.g. `FleetProposalWorkflow`, `FleetDreamRunner`) and check `Fleet/` first.
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` (488, static) — static report (`BuildStatic :56`, `SignalCliCheck
  :148`, `AliasCheck :192`, `AddAuthChecks :214`, `AddInfrastructureChecks :245`); live probes (`ProbeAsync :294`,
  `ConfigHealthProbeSet :395`, probe methods `:429-488`); shared check helpers (`UriCheck :352`, `PresenceCheck :372`,
  `NotProbed :379`).
- Endpoint maps (re-measure with plan 01's tool): `Models/AgentApi.Models.cs` `MapModelEndpoints` (149 lines, helpers
  from `:162`), `Mcp/AgentApi.Mcp.cs` `MapMcpEndpoints` (120; helpers `:138`), `Sharing/AgentApi.Sharing.cs`
  `MapSharingEndpoints` (108; helpers `:123`).
- Pin: `ProjectLayoutTests.cs:170-201` table of `AgentApi` partial path + Map method name (`AgentApiParts_MatchesReflectedMapMethods
  :205`, `AgentApiPart_ExistsInItsFeatureFolder :216`); `AgentApiReachabilityTests` (a partial `MapAgentApi` never calls
  silently deletes its endpoints — CLAUDE.md rule 7). Tests of behaviour: `ConfigHealthApiTests`, `ConfigHealthProbeTests`,
  `AuthConfigHealthTests`, fleet lesson/dream tests (`FleetLessonServiceTests.cs`, `FleetLessonServiceTests.cs`; confirm whether `FleetSkillProposalServiceTests.cs` exists).

## Context & constraints

- **Lessons blob and per-user lock are state.** ADR 0143: an approval holds the per-user lock **across reading and writing both
  `lessons.json` and `prompts.json`**. "One lock owner, the others call it" therefore has to support a lock held across a
  *caller-supplied body* (`WithUserLockAsync(userId, Func<Task<T>> body)`), not just single-store methods. Spell the signature
  out in step 3 and add a test that two concurrent approvals serialise. Read ADR 0143/0146 and the dream gotchas first.
- Both hosts register through **one file**, `Fleet/FleetRegistration.cs` (`AddAgentFleet` and `AddAgentFleetInMemory`, the
  latter called by DevHost `Program.cs:193`): register any new class in both methods (CLAUDE.md rule 7).
- Target: a lessons store (owns the lock), a proposal workflow (prompt + skill proposals, using the lock), a dream runner (scheduler
  + run + model pick); `FleetLessonService` remains a facade only if callers need it (prefer updating callers).
- `ConfigHealth` is a **`static class`**: to split by file it must become `static partial class` (`ConfigHealthStatic.cs`,
  `ConfigHealthProbes.cs`, `ConfigHealthChecks.cs`). Pure moves, same public surface. **Secret-leak guarantee:** gotchas.md ~2202
  and `ConfigHealthTests` (`FailingProbe_NeverLeaksSecret`, `Report_NeverContainsSecretValues`) pin it; splitting `ProbeAsync` and
  `ConfigHealthProbeSet` touches it, so `ConfigHealthTests` belongs in the AC list below.
- Endpoint maps: split each `Map*Endpoints` into named private `Map<Area>Group` methods (each ≤ ~40 lines; the reflection in
  `ProjectLayoutTests` requires the `…Endpoints` suffix on the *public* map method only, so the group names are safe) — **not**
  new files unless the table in `ProjectLayoutTests` is updated deliberately. Handler bodies over ~25 lines move to named static
  handler methods. Do not change routes, auth filters or response shapes. **Prove it with a route-table snapshot test**
  (enumerate `EndpointDataSource` of a test host: method + pattern + auth metadata) taken **before** the split and compared
  after; a `grep MapGet|MapPost` count is insufficient (misses `MapPut/MapDelete/MapPatch`) and `AgentApiReachabilityTests`
  (`:26-35`) only proves each `Map*Endpoints` part is *called*, not that a route survived inside it.

## Steps

1. `ConfigHealth` split (lowest risk, no pins). Verify the three ConfigHealth test classes.
2. Endpoint maps, one per commit (Models, Mcp, Sharing). Verify `AgentApiReachabilityTests`, `AgentApiTests`, the table
   tests, plus the area's API tests. Record route counts before/after.
3. `FleetLessonService`: first read `FleetLessonService.Skills.cs` (the skill-proposal partial) and decide whether it shares the proposal workflow; extract the store, then
   proposals, then dream runner; one commit each. Verify fleet lesson/dream tests after each.
4. Register new classes in both Backend and DevHost fleet registrations. Verify `DevHost` builds
   (memory: SDK 11 can break the DevHost build locally; if so, say so and rely on CI).

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Route table (method, pattern, auth metadata) unchanged | `RouteTable_MatchesSnapshot` (snapshot taken before step 2) | `AgentApiRouteTableTests.cs` (new) |
| Every `Map*Endpoints` part is still mapped | `AgentApiReachabilityTests` | `AgentApiReachabilityTests.cs` |
| Config health never leaks a secret | `ConfigHealthTests.FailingProbe_NeverLeaksSecret`, `Report_NeverContainsSecretValues` unchanged | `ConfigHealthTests.cs` |
| Lessons blob still single-writer under the user lock | existing concurrency/lock tests in `FleetLessonServiceTests`; add one if none asserts two concurrent mutations serialise | `FleetLessonServiceTests.cs` |
| Config health output identical | `ConfigHealthApiTests`, `ConfigHealthProbeTests`, `AuthConfigHealthTests` unchanged | existing |

## Quality gates

coverage_new_code_pct_min 80; cognitive_complexity_max 15; `dotnet format` clean.

## Out of scope

Dream behaviour/prompts; new endpoints; the Fleet runner (`FleetTaskService`, `FleetTeamRunner`).

## Review
