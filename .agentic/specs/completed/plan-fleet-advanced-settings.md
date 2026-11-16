# Plan: Fleet "Advanced settings" (per-user FleetOptions overrides)

planned-against: main @ 278a564

## Overview

Most `Fleet:*` limits (team depth, judge/fix/attempt counts, soft budget, dream tuning, ...) can only be changed by the
operator in config. The user wants an **Advanced settings** view on `/fleet/` where each user sets the ones the composer
cannot, stores them, and has them used for **all of their fleet tasks and their dream**. Unset fields fall back to the
operator's `FleetOptions`.

## Decisions made with the user (do not re-litigate)

- **Scope: per user.** Stored per user, like prompts and lessons. Never global, so there is no owner-tier question.
- **Exposed fields (25):**
  - Behaviour: `MaxTeamDepth`, `MaxAgentsPerTask`, `MaxConcurrentSubTeamsPerLevel`, `DefaultMaxGroupChatRounds`,
    `BudgetSoftLimit`, `JudgeDepth`, `PassingScore`, `MaxAttempts`, `MaxFixRounds`, `ApprovalDepth`,
    `MaxClarifyingQuestions`, `SelfReviewMaxIterations`, `DefaultCopilotModel`.
  - Dream: `DreamAtUtc`, `DreamWindowHours`, `DreamMaxTasks`, `DreamMinTasks`, `DreamMinNewTasks`, `DreamMaxDigestChars`,
    `DreamMinSupport`, `DreamMaxNewPerRun`, `LessonMaxChars`, `LessonsInPrompt`, `LessonTtlDays`, `LessonsMaxStored`.
- **NOT user-settable, stay operator config:** `MaxTokensPerTaskLimit`, `DefaultMaxTokensPerTask`, `MaxConcurrentTasks`,
  `CopilotAgentsEnabled`, `DreamEnabled`, `DreamTimeout`, `SseKeepAlive`, `MaxEventLogBytes`, `MaxWorkspace*`,
  `MaxInput*`, `MaxRetryResultLength`, `MaxToolTextLength`. The first four are the cost/security ceilings: the token cap
  (`MaxTokensPerTaskLimit`) stays the hard cost bound no matter what a user sets here.
- The composer keeps its per-task controls (model, token cap, tools, clarify, self-review). Not duplicated.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/Fleet/FleetOptions.cs`: the operator options; gets a `Clone()` (they are a settable class).
- `src/Agent42.Agents/Fleet/IFleetStore.cs` + `FleetStore.cs`: new `GetSettingsAsync`/`SetSettingsAsync` over one Blob
  `{UserPrefix}settings.json`, next to `PromptsKey`/`LessonsKey` (store logic over the narrow `IFleetBlobs` seam).
  The in-memory twin used by tests lives in `tests/Agent42.Tests/FleetTestDoubles.cs`.
- NEW `src/Agent42.Agents/Fleet/FleetUserSettings.cs`: the stored record, every field nullable (null = use operator value).
- NEW `src/Agent42.Backend/Fleet/FleetSettingsService.cs`: bounds table, validate+clamp, `ResolveAsync(userId) -> FleetOptions`.
  Registered in `FleetRegistration.AddAgentFleetCore` (both overloads share it).
- `FleetTaskService.cs` (`SubmitAsync`, `Register` line ~163 builds `FleetBudget` from `options`) and
  `FleetTaskService.Run.cs` (`FleetRun.Options = options`, line ~143): use the task's resolved options.
- `FleetCopilotAgentFactory.cs:155` (`ModelFor` reads `host.Options.DefaultCopilotModel`): must read the run's value instead.
- `FleetLessonService.cs`: `ActiveAsync`, `TickAsync`, `WindowStart`, `NothingToLearn`, `DreamAsync`, `DreamOverAsync`,
  `FinishRunAsync` all read the singleton `options`; each must use the user's resolved options.
  `FleetDigest.BuildAsync` and `FleetLessons.*` already take `FleetOptions` as a parameter (no change beyond callers).
- `AgentApi.FleetLessons.cs:60-77`: `ttlDays`, `minNewTasks`, `minTasks`, `schedule` shown in the Lessons view are read
  from `lessons.Options`; show the user's effective values.
- `FleetLauncher.Analyze.cs:94-100` (`DescribeConfig`, the analyzer's view of config): show the effective options for the
  user, not the singleton (it takes no user id today, so the signature or its caller needs one).
- NEW `src/Agent42.Backend/Fleet/AgentApi.FleetSettings.cs`, called from `MapAgentApi` (`Api/AgentApi.cs:60-63`, next to
  `MapFleetLessonEndpoints`). `AgentApiReachabilityTests` catches a partial that is never mapped.
- `wwwroot/fleet/index.html` + `fleet.js` + `fleet.css`: an **Advanced** toolbar button (beside `#lessonsBtn`/`#promptsBtn`)
  and a `settings` view in `#detail`, built the way `openLessons`/`openPrompts` do it.
- Docs: new ADR `.agentic/decisions/0135-fleet-advanced-settings.md` (next free number; amends 0097 and 0124),
  `docs/features/agent-fleet.md`, CLAUDE.md feature-map row for the fleet (add 0135), a gotcha.

## Context & constraints (what the executor can't cheaply re-derive)

1. **The engine already takes one `FleetOptions` per run** (`FleetRun.Options`, `FleetTeamRunner.Options => run.Options`).
   So the design is: resolve ONE effective `FleetOptions` per user at task start (a clone of the operator singleton with
   the user's clamped overrides applied) and pass it where the singleton went. No engine rewrite, no per-field plumbing.
   Evidence: `FleetTeamRunner.cs:20`, `FleetSelfReview.cs:45`, `FleetClarifier.cs:31` all read `run.Options`.
2. **Never read the singleton in the run path.** `FleetTaskService.options` is still right for the operator-only fields
   (`MaxConcurrentTasks`, `MaxTokensPerTaskLimit`, size caps, event-log cap) and for `Register`'s caps. Only the 25 exposed
   fields come from the resolved options. The two `FleetBudget` args (`MaxAgentsPerTask`, `BudgetSoftLimit`,
   `MaxConcurrentSubTeamsPerLevel`, `MaxTeamDepth`) are among them, so resolve BEFORE `Register` (async resolve outside the
   submit lock; pass the result into `Register`).
3. **Snapshot per task, not live.** A running task keeps the options it started with; changing settings affects the next
   task. Dream ticks resolve at tick time. Do not persist a snapshot on `FleetTaskRecord` (out of scope).
4. **Bounds live in ONE table** (`FleetSettingsService`): per field a min, max, and (for the string fields) a validator.
   The same table drives server validation AND the `GET /api/fleet/settings` response the UI renders from, so the UI
   never hardcodes a bound. Out-of-range input on PUT is a 400 with the field name; resolve also clamps (defence in depth
   for a hand-edited blob) and never throws. Suggested bounds, executor may tighten with a reason: `MaxTeamDepth` 1-10,
   `MaxAgentsPerTask` 1-100, `MaxConcurrentSubTeamsPerLevel` 1-12, `DefaultMaxGroupChatRounds` 1-200, `BudgetSoftLimit`
   0.1-1.0, `JudgeDepth`/`ApprovalDepth` -1..`MaxTeamDepth`, `PassingScore` 1-10, `MaxAttempts` 1-6, `MaxFixRounds` 0-5,
   `MaxClarifyingQuestions` 1-20, `SelfReviewMaxIterations` 1-6 (1 = off, existing semantic "below 2 turns off"),
   `DreamAtUtc` strict `HH:mm`, `DreamWindowHours` 1-24, the other dream counts 1-n with n generous.
5. **`DefaultCopilotModel` is validated on save** against the user's tool-capable catalog models (the rule
   `FleetLessonService.PickModelAsync` applies), `""`/null = operator value. `copilotAgentsEnabled` stays the operator's.
6. **Merge semantics on PUT:** body is the full override set; a null/absent field means "use the operator value", so one
   PUT can both set and clear. `DELETE /api/fleet/settings` clears all. Storage is a Blob JSON doc, not grain state, so
   the trailing-`[Id]` rule (cross-cutting 2) does not apply; still give every field a default and never fail on unknown
   JSON members (forward compatible).
7. **Auth:** all routes behind Easy Auth, scoped by `GetUserId()`, never in `excludedPaths` (cross-cutting 4); service
   resolved optionally, 503 when the host has no fleet (the `/api/speech` pattern, as `AgentApi.FleetLessons.cs` does).
8. **No sw.js `CACHE` bump.** `/fleet/` is outside SHELL (ADR 0116, pinned by `FleetPageAssetsTests`), and rule 3 covers
   `wwwroot/js/` only. The user's request to "remember the bump" does not apply; do not bump.
9. **Egress:** nothing new leaves the process (cross-cutting 13): settings are config, not conversation content.
10. **Side effects to accept and document:** lowering `LessonsMaxStored`/`LessonTtlDays` removes nothing at once; the TTL
    applies at the next dream's `Expire`, a lower cap only blocks new lessons. Raising `MaxAgentsPerTask` etc. raises the
    possible spend per task but the token cap (operator) still bounds it.
11. S3776 is a build error at 15: keep the bounds table data-driven (a list of descriptors), not a big `if` chain.

Governing docs: ADR 0097 (fleet), 0118 (clarify), 0119 (self-review), 0124 (dream), 0116 (fleet page not in SHELL);
`.agentic/gotchas.md` fleet entries (store over blob seam; `FleetCopilotAgentFactory` vs ADR 0085 client).
Exemplars to copy: per-user Blob doc + API + view = fleet **lessons** (`FleetLessonService`, `AgentApi.FleetLessons.cs`,
`fleet.js` lessons view); store seam = `FleetStore.GetPromptsAsync`.

## Steps

1. **Store + record** (`FleetUserSettings`, `IFleetStore`/`FleetStore`, in-memory twin in `FleetTestDoubles.cs`).
   Verify: `dotnet test --filter FleetStoreTests` with new round-trip + missing-blob-returns-empty tests.
2. **Options clone + settings service** (`FleetOptions.Clone`, `FleetSettingsService`: bounds table, `ValidateAndSave`,
   `Reset`, `ResolveAsync`; registration). Verify: `--filter FleetSettingsServiceTests` (bounds, clamp, merge, clear).
3. **Use resolved options in tasks** (`SubmitAsync` resolve before `Register`; `RunAsync`; `FleetCopilotAgentFactory.ModelFor`
   takes the value from the run's options). Verify: `--filter FleetTaskServiceTests|FleetCopilotAgentFactoryTests` plus new
   tests that a user's `MaxAttempts`/`MaxAgentsPerTask`/`DefaultCopilotModel` reach the budget and the run.
   Depends on 2.
4. **Use resolved options in the dream** (`FleetLessonService` call sites listed above; `AgentApi.FleetLessons` values;
   `DescribeConfig`). Verify: `--filter FleetLessonServiceTests|FleetDreamTests|FleetLessonApiTests` plus new window/threshold
   tests. Depends on 2. **Parallelizable with 3** only in worktrees (both touch `FleetRegistration`? no, but both are
   write-capable: use `isolation: "worktree"` or run sequentially).
5. **API** (`AgentApi.FleetSettings.cs`: `GET/PUT/DELETE /api/fleet/settings`; wire into `MapAgentApi`). Verify:
   `--filter FleetSettingsApiTests|AgentApiReachabilityTests|EasyAuthExclusionTests`. Depends on 2.
6. **UI** (`index.html` Advanced button, `fleet.js` settings view rendered from the GET payload: grouped fields, operator value
   as placeholder, blank = default, Save + Reset all, inline errors from the 400; `fleet.css`). Verify:
   `--filter FleetPageAssetsTests` (add asserts: button, route strings, no CACHE bump needed) and a manual DevHost check
   (`dotnet run --project tools/Agent42.DevHost`, open `/fleet/`, set, reload, confirm persisted and used). Depends on 5.
7. **Docs** (ADR 0135 via `/decide`, `docs/features/agent-fleet.md` section, CLAUDE.md feature-map row, gotcha via `/gotcha`:
   "the run path reads the resolved options, never the singleton"). Then `/sync-docs`.
8. Full `dotnet test Agent42.slnx` + `dotnet format Agent42.slnx --verify-no-changes`, coverage gate, then MR on `feature/fleet-advanced-settings`.

## Acceptance criteria -> tests

| AC | Test | File |
|----|------|------|
| Settings round-trip through the store; no blob = all null | `Settings_RoundTrip`, `Settings_Missing_IsEmpty` | `FleetStoreTests.cs` |
| Unset field resolves to the operator value; set field overrides | `Resolve_UnsetFallsBack_SetOverrides` | new `FleetSettingsServiceTests.cs` |
| Out-of-range / bad `DreamAtUtc` / unknown copilot model rejected on save with the field named | `Save_OutOfRange_Refused`, `Save_BadTime_Refused`, `Save_UnknownCopilotModel_Refused` | `FleetSettingsServiceTests.cs` |
| A hand-edited out-of-range blob is clamped, never throws | `Resolve_ClampsStoredValues` | `FleetSettingsServiceTests.cs` |
| Operator-only fields can never be overridden (not in the record/request) | `OperatorOnlyFields_NotExposed` | `FleetSettingsServiceTests.cs` |
| A task uses its user's options (budget caps, attempts, judge, copilot model) and another user's are untouched | `Task_UsesUserSettings`, `Task_OtherUserUnaffected` | `FleetTaskServiceTests.cs` |
| A running task keeps its start-time options after a save | `Running_KeepsSnapshot` | `FleetTaskServiceTests.cs` |
| Dream window, thresholds and lesson caps follow the user's settings | `Dream_UsesUserWindow`, `Dream_UsesUserThresholds`, `Lessons_UseUserCaps` | `FleetLessonServiceTests.cs` |
| Lessons view shows the effective values | `LessonsGet_ShowsEffective` | `FleetLessonApiTests.cs` |
| Analyzer's config view shows the user's effective options | `DescribeConfig_ShowsEffective` | `FleetLauncherTests.cs` |
| GET returns settings, operator defaults and bounds; PUT saves; DELETE clears; 400 names the field; 503 without fleet; another user can't read mine | `Settings_Get/Put/Delete/Invalid/Unavailable/UserScoped` | new `FleetSettingsApiTests.cs` |
| The endpoints are reachable and not Easy-Auth-excluded | existing | `AgentApiReachabilityTests`, `EasyAuthExclusionTests` |
| The page has the Advanced button, loads and saves via the routes | `FleetPage_HasAdvancedSettings` | `FleetPageAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`)

- [ ] `coverage_new_code_pct_min`: 80 (`node .agentic/scripts/check-new-code-coverage.mjs`)
- [ ] `cognitive_complexity_max`: 15 (S3776, build error)
- [ ] `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`
- [ ] `vulnerable_packages`: 0 (no new packages planned)
- [ ] Report any gate that can't be measured as SKIPPED, never estimate.

## Out of scope

- Operator-only fields above, a global/owner-set layer, an admin UI.
- Per-task overrides of these settings, or a snapshot of the options on the task record.
- Retroactive pruning of lessons/tasks when a cap is lowered.
- Changing the composer's existing per-task controls.
- Android app (the page is web).

## Review

Two critic passes (plan, then diff). The plan review's findings were folded into the implementation (see Divergences). The diff
review found no blocking issue; its low findings are covered below.

## Divergences from the plan (as built)

- **No test double in `FleetTestDoubles.cs`**: the in-memory twin is `InMemoryFleetBlobs` under the real `FleetStore`; only two hand-written
  `IFleetStore` wrappers (`FleetLessonServiceTests`, `FleetReviewFixTests`) needed the two new methods.
- **Resolved options live on `RunningFleetTask.Options`**, resolved once in `SubmitAsync` (`ResolveOptionsAsync`, public so the composer
  config endpoint reuses it). `ActiveAsync` takes the run's options as an optional parameter instead of re-resolving.
- **Bounds table** is `FleetSettingsService.Descriptors` (key, group, kind, min, max, `FollowsTeamDepth`) with reflection onto the
  same-named properties of `FleetOptions`/`FleetUserSettings`. `JudgeDepth`/`ApprovalDepth` are bounded by the effective `MaxTeamDepth`.
- **`DefaultCopilotModel`** is validated against tool-capable models PLUS Copilot-only tool models (the plan's `PickModelAsync` rule would
  have refused Copilot-plan models); the factory takes the value through a trailing constructor parameter.
- **Extra affected areas found in review**: `GET /api/fleet/config` (composer) now shows the user's effective limits;
  `IFleetLauncher.DescribeConfig` became `DescribeConfigAsync(userId)` (interface, grain tool, fake, tests); `WindowStart` takes optional options.
- **Constructor ripple**: `FleetSettingsService` is an optional trailing parameter on `FleetTaskService` and `FleetLessonService`.
- **Test names**: `Task_UsesUserSettings` + `Task_OtherUserUnaffected` merged; `DescribeConfig_ShowsEffective` is in `FleetAnalyzerTests.cs`;
  API tests are `Settings_Get_ReturnsSettingsDefaultsAndBounds`, `Settings_Put_Saves_AndDeleteClears`, `Settings_Invalid_Is400_NamingTheField`,
  `Settings_UserScoped`, plus `MissingService_Is503` for the new route; added `ProjectLayoutTests` entry for the new partial.
- **Not done / known**: the manual DevHost check (SDK 11 breaks the DevHost build locally). `DreamMinTasks` may exceed `DreamMaxTasks`
  (the user's own dream then never runs); not cross-validated.
- **ADR 0135** records the risk that users can raise the agent/depth/sub-team caps.
