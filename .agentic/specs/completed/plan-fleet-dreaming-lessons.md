# Plan: fleet "dreaming", slice 1 — nightly, unattended lessons

planned-against: main @ 69f52fa

## Overview
The judge writes concrete feedback on every failed fleet result, but each task starts from zero: nothing learned in one
task reaches the next planner (only the in-task `PreviousAttempt` note does). Slice 1 adds a **nightly "dream"**, with
no human step: for each user who switched it on, once a day at night, a model reads that user's recent finished fleet
tasks and distills a few **evidence-backed lessons about team design**, which are **applied automatically to every
planner run, sub-teams included**. The user can always **see what was learned, when, from which tasks, and where it was
applied**, and can disable or delete any lesson. No prompt rewriting and no judge calibration (later slices).

Changes from the first draft (user, 2026-11-10): scheduled nightly with no human intervention (so no approval gate),
visibility of lessons learned AND applied, and lessons apply to sub-teams as well as the top-level team.

## Affected areas (verified against the tree on 69f52fa)
- `src/Agent42.Agents/Fleet/` — new `FleetLesson` and `FleetLessonsDocument` types; `FleetTaskRecord` gains a trailing
  `AppliedLessons`; `IFleetStore`/`FleetStore` gain `GetLessonsAsync`/`SetLessonsAsync` (one blob `{userId}/lessons.json`,
  the `prompts.json` shape, `FleetStore.cs` `PromptsKey`); new `FleetDreamGrain` (+ `IFleetDreamGrain`) for the schedule;
  `IFleetLauncher` gains one member, implemented by `DisabledFleetLauncher` (same file) and the Backend `FleetLauncher`.
- `src/Agent42.Backend/Fleet/` — new `FleetDigest` (pure), `FleetLessons` (pure rules), `FleetDreamer` (one model run),
  `FleetLessonService` (orchestration, per-user lock, in-process run registry), `AgentApi.FleetLessons.cs` (+ a call in
  `Api/AgentApi.cs:59-61`); changed `FleetOptions`, `FleetPrompts` (new `dream` key), `FleetRun`,
  `FleetTaskService.Run.cs` (`BuildRunAsync`), `FleetTeamRunner.Planning.cs` (`CreateTeamAsync`), `FleetRegistration.cs`,
  `FleetLauncher.cs`.
- `src/Agent42.Backend/wwwroot/fleet/` — `index.html`, `fleet.js`, `fleet.css`: Lessons dialog, badge, per-task line.
- Tests: `FleetStoreTests`; `FleetReviewFixTests` (its `StaleFirstReadStore : IFleetStore` must implement the new members or
  the test project stops compiling); `FakeFleetLauncher.cs` (implements `IFleetLauncher`); `ProjectLayoutTests.cs:193-195`
  (endpoint-partial map); `FleetPageAssetsTests`; `FakeChatClient.Fleet.cs` and `FleetTestDoubles.cs` (`FleetRunHarness`).
  **Test seams to add:** a `PlannerInputs` capture beside `JudgeInputs`/`ClarifyInputs` (lines 41-44), or "the planner got
  the lessons" cannot be observed; a `submit_lessons` branch checked BEFORE `FindScript` (the digest copies task requests in
  and each fleet request ends with a `[[fleet]]<token>` marker, `FakeChatClient.Fleet.cs:73-77,106`, so the dream call would
  otherwise consume that task's agent script), building evidence ids at runtime by parsing task ids out of the digest;
  `FleetRunHarness` must accept `Lessons`. The concurrent-dream and bad-model refusals are tested in
  `FleetLessonServiceTests` with a gated fake client and their own catalog, NOT in the shared `FleetApiFixture` (one
  `FakeChatClient` and one catalog shared with `FleetApiTests`/`FleetAnalyzerPanelApiTests`).
- Docs: new ADR 0124 (`/decide`), CLAUDE.md feature-map row "Agent fleet" (add 0124), `docs/features/agent-fleet.md`.
- DevHost needs no registration: `AddAgentFleetInMemory` (`tools/Agent42.DevHost/Program.cs:183`) goes through
  `FleetRegistration`, and DevHost already has `UseInMemoryReminderService` (line 260).

## Context & constraints
Findings the executor cannot re-derive cheaply:
- **The data is already persisted.** Each task's event log holds `plan.submitted`, `verdict` (score, passed, feedback, `next`),
  `attempt.failed`, `planner.failed`, `task.finished`; `FleetAnalysis.Parse` (internal, same assembly) parses a log;
  `FleetTaskRecord` has status, reason, tokens, agents, request. Evidence: `FleetTeamRunner.cs:117-130`, `FleetAnalysis.cs`.
- **A tool call carries the structured result** here (`submit_team_plan`, `submit_verdict`, `requirements_clear`), so a provider
  that ignores structured output still works; a refused item returns to the model as the tool result (the `PlanSubmission`
  shape in `FleetTeamRunner.Planning.cs`). Exemplars: `FleetClarifier.cs` (one agent, tools, fail open), `JudgeAsync`.
- **Where lessons enter:** the planner's *user prompt* in `CreateTeamAsync` next to `previousAttempt`, NOT the system prompt:
  derived text gets user-role authority (ADR 0104 amended 0056 on exactly this line), and the editable `planner` prompt is
  untouched (the same reason `CopilotModels()` is appended in code).
- **Schedule = an Orleans reminder on a per-user grain**, not a hosted-service timer. A revision rollover overlaps two
  replicas briefly (CLAUDE.md rule 12; the chat gateway needed a Blob lease for it), and a timer in each would both dream.
  A reminder fires once cluster-wide and is persisted (`UseCosmosReminderService`, `Program.cs:479`); the repo already runs
  unattended LLM turns from reminders (`AgentGrain.ReceiveReminder`, ADR 0022/0028), so unattended spend of the user's own
  tokens has precedent. **Do not rely on a reminder catching up:** Orleans realigns to the next period boundary and skips
  ticks missed while the app was down, and every push to `main` deploys, so a restart near the nightly time is plausible. The
  design therefore ticks HOURLY and lets the Backend decide whether it is time (decision 6), so a missed tick costs at most an
  hour, a changed `DreamAtUtc` needs no re-registration, and `ScheduleAsync` is also re-run on enable and on every
  `GET /api/fleet/lessons` while the toggle is on, which heals a reset reminder table. The grain reaches the Backend through
  the existing `IFleetLauncher` port (ADR 0122 decision 6: extend it, do not add a second port). Every silo that lacks the
  fleet already registers `DisabledFleetLauncher` (the test silos, listed as a required port in `SiloRegistrationTests.cs:24`;
  `FleetRegistration.cs:41-42` registers the Backend one), so registration impact is none beyond the three implementers
  (`DisabledFleetLauncher`, `FleetLauncher`, `FakeFleetLauncher`). The grain interface follows `IChatRouteGrain`:
  `IGrainWithStringKey`, flat namespace, an `[Alias]`.
- **Single replica, in-process state** (rule 12): the per-user lock and the "one dream at a time" registry are in-process. A
  rollover overlaps two replicas for a few minutes, the store has no ETag and the dream merge writes the whole document, so a
  disable/delete made on the OTHER replica in that window can be lost. Accept it and say so in the ADR (the user re-clicks);
  do not build a conditional write for slice 1.
- **Fleet content is untrusted, and now nothing is approved.** The digest holds more than judge feedback: task requests (a
  chat-started task's request is model-authored via `start_fleet_task`), the planner's rationale and agent names. All of it
  is data (gotchas 410-411 record the same hazard for the reminder path). The in-code checks are **shape hygiene, not an
  injection defence**, and the ADR must say exactly that. In order: (1) a lesson must be supported by >= `DreamMinSupport`
  evidence ids that are in the digest and belong to distinct WORKSPACES (a follow-up chain shares one workspace, notes and
  files, so it counts once); the model picks the ids and nothing proves they support the text, which is a stated limit;
  (2) text rules by ALLOWLIST: letters and digits (Unicode L and N), space, and `. , ; : ' " ( ) - / % +` only, so every
  control, format (Cf: zero-width, bidi), private-use, surrogate, line-separator and markup character is refused, plus a refusal
  of any domain-like token (`word.word` with a 2+ letter tail), `://`, `www`, `hxxp`; (3) a DENYLIST of execution-shaped terms,
  tested as a constant: `copilot`, `shell`, `bash`, `powershell`, `terminal`, `command`, `execute`, `run `, `install`,
  `download`, `curl`, `wget`, `script`, `credential`, `password`, `secret`, `api key`, `token`, so a lesson cannot name the
  one agent kind that has a shell (a team-design lesson can say "a staged team" without them); (4) a fixed `Topic` set, one
  line, <= `LessonMaxChars`; (5) at most `DreamMaxNewPerRun` new lessons per night; (6) the planner is told the lessons describe
  past outcomes and never reason to add tools, change approvals or use a particular agent kind, and `PlanSubmission` enforces the
  tool set, budget and approval in code regardless; (7) everything is visible and one click turns a lesson off; (8) the nightly
  run is opt-in per user; (9) lessons expire. Residuals for the ADR: a poisoned result can still plant a lesson through judge
  feedback in an unattended run (including one derived from a task whose results contained fetched web text; slice 1 does not
  exclude those tasks, because research tasks are the ones that benefit); and an allowed-vocabulary lesson can still bias team
  design toward the agents the task already permits. Bounded and visible, not prevented. ADR 0104 put untrusted text at user
  role to LOWER its authority, so the placement is the least-bad slot, not an instruction channel.

Decisions (the user fixed the first three; the rest are mine, listed so they can redirect):
1. **Nightly, unattended, opt-in per user** (Lessons dialog toggle, default OFF, so nothing spends a user's tokens until they
   say so; the toggle is the consent). Operator kill switch `Fleet:DreamEnabled` (default true). The user also gets a
   "Dream now" button (same code path, trigger `manual`).
2. **Auto-applied** (user's call): a lesson that passes the code checks is `active` at once. The lifecycle is
   `active` -> `disabled` (user; kept as a tombstone so it is not re-proposed) or deleted; no `proposed` state.
3. **Applied at every depth** (user's call), re-planned teams included. Cost: <= `LessonsInPrompt` x `LessonMaxChars`
   (~600 tokens) per planner call, which is small next to a planner run.
4. **Blob store, not Hindsight.** Hindsight has no delete path (ADR 0056), an unattended write-only memory is the wrong shape,
   it is an operator-gated third-party egress (`HINDSIGHT_API_KEY`, ADR 0056/0057) and lessons from fleet content would be a
   new egress path (rule 13). *Rejected: Hindsight; a Cosmos grain state (the fleet is Blob by design, ADR 0097).*
5. **Digest in code, one model run** with a `submit_lessons` tool. *Rejected: an analyzer-style agent browsing tasks with tools
   (ADR 0123):* many round trips, unbounded cost, nothing to unit test.
6. **Time:** an HOURLY reminder per opted-in user (see the schedule bullet), and the Backend runs a night's dream only inside a
   window: `Fleet:DreamAtUtc` (default `01:00`, 02:00-03:00 in the Netherlands) for `DreamWindowHours` (4), and only if
   `LastRunAt` is before that window's start. There is no per-user timezone in the codebase, so this is operator config in UTC,
   DST drift accepted. Outside the window an hourly tick does nothing and writes nothing (not even a "skipped" run). Inside it,
   a tick is recorded as `skipped` unless the toggle is on and >= `DreamMinNewTasks` (2) tasks finished since `LastRunAt`, so an
   idle night costs no tokens. A manual run ignores the window and the new-task check but still needs `DreamMinTasks`; it sets
   `LastRunAt`, so it also uses up that night's run (the dialog says so).
7. **Caps (all `Fleet:*`, defaults in parentheses):** `DreamMaxTasks` (20) newest finished tasks read; `DreamMinTasks` (3);
   `DreamMaxDigestChars` (30000, oldest dropped first); `DreamMinSupport` (2); `DreamMaxNewPerRun` (3); `LessonMaxChars`
   (300); `LessonsInPrompt` (8, newest first; the block is at most 8 x 300 plus framing, so no separate char cap);
   `LessonTtlDays` (90, from `LastSupportedAt`); `LessonsMaxStored` (200); `DreamTimeout` (3 min, the clock starts BEFORE the digest
   reads, so it covers the whole run); `DreamWindowHours` (4); the model client's `MaximumIterationsPerRequest` (4). Disabled lessons are kept as tombstones (cap 50, oldest dropped).
8. **Re-deriving an existing lesson reinforces it** (same text after trim/case/whitespace normalisation): `LastSupportedAt` is
   renewed and new evidence ids merged (cap 10) instead of a duplicate. That is the only upkeep slice 1 does.
9. **"Applied" is recorded when it is true:** the first time a planner of a run actually receives lessons, the run (a) saves
   them on `FleetTaskRecord.AppliedLessons` (id + a text snapshot, so the task view stays right after a lesson is deleted) and
   (b) appends `{taskId, at}` to each lesson's `Applications` (last 20) and bumps its `AppliedCount`, once per task, not per
   planner call. Every planner call also publishes `lessons.applied {teamId, attempt, depth, ids, chars}` (`chars` = the block's
   length, so the cost of sub-team application can be measured later; it is unmeasured today). It is a structural event, so it
   survives the event log's text coalescing and cap (gotcha on event-log coalescing).

Governing ADRs/gotchas: `.agentic/decisions/0097-agent-fleet-recursive-teams.md` (fleet in-process, Blob layout, decision 8
egress), `0122`/`0123` (analyzer, scoped reads, data framing, panel shape), `0056`/`0057` (Hindsight, rejected store), `0104`
(user-role for untrusted text), `0119` (opt-in toggles ship off), `.agentic/gotchas.md` fleet entries (event-log coalescing
keeps structural events; the workspace sync deletes unmatched blobs, so lessons live OUTSIDE any workspace prefix; the
"interrupted is computed on read" entry is the pattern for a dream run that died with the process).

## Steps
Branch `feature/fleet-dreaming-lessons` from `main` @ 69f52fa. Read the fleet gotchas and the exemplars named above first. Run
the narrowest matching test after each step.

1. **Types + store seam.** `FleetLesson { Id, Text, Topic, Confidence, Evidence (task ids, cap 10), Status ("active"|"disabled"),
   CreatedAt, LastSupportedAt, AppliedCount, Applications (last 20 {TaskId, At}), }`; `FleetLessonSettings { Enabled, Model,
   LastRunAt?, SeenAt? }`; `FleetDreamRun { Id, Trigger ("nightly"|"manual"), StartedAt, FinishedAt?, Status ("running"|"ran"|
   "skipped"|"failed"|"interrupted"), Reason, TasksRead, NewTasks, Learned, Reinforced, Refused, Model, InputTokens?,
   OutputTokens? }`; `FleetLessonsDocument { Lessons, Settings, Runs (last 30) }`. All in the flat `Agent42.Agents` namespace,
   trailing defaults only. `FleetTaskRecord.AppliedLessons` (`IReadOnlyList<FleetAppliedLesson>`, id + text, default `[]`).
   `IFleetStore`/`FleetStore`: `GetLessonsAsync(userId)` (an empty document when the blob is missing), `SetLessonsAsync`;
   add the layout line. Implement both on `StaleFirstReadStore`. Verify: `dotnet test --filter FleetStoreTests`.
2. **Options + pure rules.** `FleetOptions` fields from decision 7 and `DreamEnabled`, `DreamAtUtc` (below-1 clamps like
   `MaxClarifyingQuestions`). `FleetLessons` (pure): `Active(doc, now, options)` (active, unexpired, newest first, capped),
   `PromptBlock(active)` (frames the lessons as past outcomes, never a reason to add tools, change approvals or pick an agent kind),
   `Validate(proposal, digest, doc, options)` (the allowlist, domain-token and denylist rules from the untrusted bullet (2)-(3), the
   evidence ids a subset of the digest's, support = distinct WORKSPACES of those tasks >= `DreamMinSupport`, topic and confidence in
   their sets) returning an error
   string for the model, `Merge` (new / reinforce / refused-duplicate-of-disabled, the per-run and total caps, the tombstone cap),
   `Expire`. Verify: `FleetLessonsTests`.
3. **Digest** (parallelizable with step 5 only in a worktree). `FleetDigest` (pure, over `FleetTaskRecord` +
   `FleetAnalysis.Parse`d events): one block per finished task: id, status, reason, tokens, agents, request
   (`FleetAnalyzerLimits.Short`), each attempt's pattern + agent names + rationale (short), each verdict (score, passed, next,
   feedback short), fix-round and new-team counts, failure reasons, the lesson ids the task had applied. Per-task and total caps,
   oldest dropped first. Returns the text and a map task id -> workspace id for the tasks it contains (support is counted over
   distinct workspaces).
   - **Bounded reads.** A log can be `Fleet:MaxEventLogBytes` (20 MB) and `FleetStore.ReadEventsAsync` loads a whole blob, so 20 logs
     is ~400 MB on the replica that also serves chat. Read ONE task's log at a time, keep only the structural events
     (`plan.submitted`, `verdict`, `attempt.failed`, `planner.failed`, `task.finished`), drop the log before the next, and stop
     reading once `DreamMaxDigestChars` is reached (newest first).
   - **Framing without cutting.** `FleetToolLimits.WrapAsData(content, taskId)` calls `Cut`, which truncates at `MaxResultChars` =
     12,000 (`FleetToolLimits.cs:26`), below the 30,000 digest cap, so reusing it would drop the tail. Add a frame-only helper
     (make the private `Neutralize` internal); test a digest over 12,000 chars arrives whole.
   Verify: `FleetDigestTests`.
4. **Dreamer + service.** Add a `dream` entry to `FleetPrompts` (editable like the others; variables `minSupport`,
   `maxNewPerRun`, `topics`; no test lists prompt keys, checked 2026-11-09). `FleetDreamer`: one agent on the chosen model's client
   with one tool `submit_lessons(lessons[])`, `MaximumIterationsPerRequest` capped, run under `DreamTimeout` linked to the caller
   and to `IHostApplicationLifetime.ApplicationStopping`; validates through `FleetLessons.Validate`, returns refusals to the model,
   fails open (no tool call, a model error or a timeout means zero lessons, logged, run marked `failed`/`ran`). `FleetLessonService`
   (register next to `FleetTaskService`): `GetAsync`, `RunAsync(userId, trigger, model?)`, `SetSettingsAsync`, `DisableAsync`,
   `EnableAsync`, `DeleteAsync`, `MarkSeenAsync`, `RecordApplicationAsync(userId, taskId, ids)`; everything scoped by `userId`.
   - **Lock scope.** A per-user `SemaphoreSlim` guards short read-modify-writes only. `RunAsync` reads the document WITHOUT the
     lock (for the digest and the duplicate check), runs the model unlocked, then takes the lock, RE-READS, re-validates against the
     fresh document and merges, so a disable/delete made during the run survives and nothing blocks for minutes.
   - **One at a time per user** (in-process registry). A tick or click while one runs is refused/skipped. **Interrupted** follows the
     fleet's pattern, not an age rule (`gotchas.md:4411`, `FleetTaskService.NormalizeAsync` at `FleetTaskService.cs:311-334`): a
     `running` run entry with no entry in the in-process registry was running when the app stopped; it is normalised and persisted
     when the document is read, under the per-user lock with a RE-READ before the write, so a live run's own final write never
     races an "interrupted". The `running` entry and `LastRunAt` are first written under the lock at run start.
   - **Model:** resolved like `FleetTaskService.SubmitAsync` (`FleetTaskService.cs:70-77`): the setting's model, else the catalog
     default, must be `IsToolCapable`, never Copilot-only; a model that vanished makes the nightly run `failed` with that reason.
   - **Gating** per decision 6; a tick outside the window does nothing; a tick inside it that is skipped, and every real run, appends
     a `FleetDreamRun`; `LastRunAt` is set when a real run starts, so a second tick in the same window skips.
   - **Usage attribution.** `RunAsync` in `FleetTaskService.Run.cs:18-21` sets BOTH `UsageSpanProjector.ApplicationContextKey` and
     `UserTelemetryContext.RequestContextKey` (ADR 0010/0031). A reminder-started background task has no user context of its own, so
     set both for the dream: the user context (or its tokens reach no user) and a distinct application ("Agent42 fleet dream") so
     they show on the usage dashboard and cost estimate (ADR 0031/0110) under a visible name. ADR 0121's unmerged branch
     (`feature/open-signin-owner-tier`) also changes the usage projector; whichever lands second rebases this (say so in the ADR).
   Verify: `FleetDreamTests`, `FleetLessonServiceTests`.
5. **Apply to every planner.** `FleetRun.Lessons` (default empty) and `FleetRun.OnLessonsApplied` (`Func<IReadOnlyList<FleetLesson>,
   Task>?`). `BuildRunAsync` loads the document and passes `FleetLessons.Active(...)` (a failing read logs a warning and runs without,
   fail open) and the callback. **The callback assigns the in-memory `task.Record` FIRST** (`task.Record = task.Record with
   { AppliedLessons = ... }`, `RunningFleetTask.Record` is a mutable property, `FleetTaskService.cs:348`) and then saves it, because
   `FinishAsync` rebuilds the record from `task.Record` (`FleetTaskService.Run.cs:236,251`) and `RunAsync` holds an earlier local
   `record` (`Run.cs:20`); saving only a copy would be overwritten by the final save at `Run.cs:282` and the task view would show
   nothing applied. Sub-team planners run concurrently, so the assignment sits inside the once-only guard. It then calls
   `RecordApplicationAsync` once per run. The callback is awaited with every exception caught and logged: a slow or failing blob or
   lock must never fail or delay planning. `CreateTeamAsync`: at ANY depth, when there are active lessons, append `PromptBlock` after
   the workspace line and before `previousAttempt`, publish `lessons.applied {teamId, attempt, depth, ids, chars}`, and invoke
   `OnLessonsApplied` the first time only (`Interlocked`). Confirm `fleet.js` ignores an unknown event type (`fleet.js:351` does)
   and `FleetAnalysis` is unaffected.
   Verify: `FleetTeamRunnerTests`, `FleetTaskServiceTests`.
6. **Schedule.** `FleetDreamGrain : Grain, IFleetDreamGrain, IRemindable` keyed by `userId`, no persisted state (the reminder is the
   state). `ScheduleAsync` = `RegisterOrUpdateReminder("fleet-dream", due = a minute, period = 1 h)`, idempotent;
   `UnscheduleAsync`; `ReceiveReminder` calls `IFleetLauncher.RunNightlyDreamAsync(userId, ct)` (new member). **It returns a TYPED
   result, not prose:** `FleetDreamTick` = `Started | NotYet | Skipped | Disabled | Unavailable`. The grain unregisters its own
   reminder on `Disabled` (the user's toggle is off) and on `Unavailable` (the operator set `Fleet:DreamEnabled=false`, or the host
   has `DisabledFleetLauncher`), so it never ticks forever against a launcher that cannot run. The Backend `FleetLauncher` forwards
   to `FleetLessonService.TickAsync(userId)`, which does the window/gate check and STARTS the run on a background task, returning
   at once (a long reminder callback would pass Orleans' call timeout). `FakeFleetLauncher` returns a configurable tick and counts
   calls. The grain must never see Backend types. Verify with a test silo (all have `UseInMemoryReminderService`):
   `FleetDreamGrainTests`.
7. **Endpoints.** `AgentApi.FleetLessons.cs` with `MapFleetLessonEndpoints`, called beside `MapFleetAnalyzerEndpoints`, plus a row in
   `ProjectLayoutTests` (`AgentApiReachabilityTests` fails if it is not called). All behind Easy Auth, scoped to `http.GetUserId()`,
   never excluded (rule 4): `GET /api/fleet/lessons` (lessons, settings, runs, `newCount`, the schedule time; while the toggle is on
   it also re-runs the grain's idempotent `ScheduleAsync`, the heal for a reset reminder table);
   `PUT /api/fleet/lessons/settings {enabled, model}` (validates the model; enabling schedules the grain, disabling unschedules it);
   `POST /api/fleet/lessons/dream {model?}` (manual run; 200 with the run summary, 400 bad model, 409 already running);
   `POST /api/fleet/lessons/{id}/disable|enable`; `DELETE /api/fleet/lessons/{id}` (404 for an id that is not the caller's);
   `POST /api/fleet/lessons/seen`; the existing `FleetUnavailable()` when the fleet is off. Verify: `FleetLessonApiTests`.
8. **UI (the visibility requirement).** `/fleet/` is outside `SHELL`, so no `sw.js` bump (ADR 0123). A "Lessons" button next to "System
   prompts" with a badge (lessons created since `SeenAt`; opening the dialog calls `seen`). The dialog: (a) settings: nightly toggle,
   model picker, the schedule line ("about 03:00 Amsterdam time", from the server's value), "Dream now"; (b) **Learned**: each active
   lesson with text, topic, confidence, when learned, last reinforced and expiry, its evidence as links that select that task, and
   **Applied**: count, last time, and links to the tasks it was applied in; Disable / Delete; disabled ones collapsed with Enable /
   Delete; (c) **Dream log**: the last runs (when, nightly/manual, status and reason, tasks read, learned, reinforced, refused,
   model). In the task detail, a "Lessons applied (n)" line listing the snapshot texts from `AppliedLessons`. All lesson text via
   `textContent` only (model-written; stricter than the page's `setMarkdown` rule). Verify: `FleetPageAssetsTests`; then drive it in
   DevHost (`dotnet run --project tools/Agent42.DevHost`, http://localhost:61326/fleet/): the canned model makes no `submit_lessons`
   call, so only the empty/skipped paths are visible there — say so; the learning path is test-proven.
9. **Docs.** `/decide` ADR 0124 (extends 0097 incl. its Blob layout, 0122, 0123; 0056 as the rejected store; the egress paragraph and
   the screening-is-hygiene statement and both residuals from the untrusted bullet; the in-process lock/registry and the
   lost-write-on-rollover limit; the hourly-tick-plus-window schedule and why; the ADR 0121 rebase note). CLAUDE.md "Agent fleet" row +
   0124; `docs/features/agent-fleet.md` (Lessons dialog, nightly schedule, the Blob key); `/sync-docs`; `/gotcha` only for a trap
   actually hit.
10. **Gates.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
    `node .agentic/scripts/check-new-code-coverage.mjs`.

Egress (for the ADR, rule 13): no new destination. The digest (short requests, judge feedback, plan names) goes to the user's own
chosen provider, the same set as ADR 0097 decision 8 and ADR 0122; nothing goes to Hindsight. New: it happens unattended, which
is why the nightly run is opt-in per user.

## Acceptance criteria → tests
| # | Acceptance criterion | Test (file) |
|---|----------------------|-------------|
| 1 | The digest carries each finished task's plan, verdict score/feedback, fix and new-team counts, outcome and applied lesson ids; caps drop oldest first; framed as data with the closing tag neutralised and NOT cut (a >12,000-char digest arrives whole); unjudged tasks appear | `FleetDigestTests` |
| 2 | It reads one log at a time, keeps only structural events and stops at the char cap | `FleetDigestTests` (a fake store counts reads) |
| 3 | `submit_lessons` refuses: evidence ids not in the digest, support from fewer than `DreamMinSupport` distinct WORKSPACES (a follow-up chain counts once), text over the cap, a character outside the allowlist (newline, backtick, `<`, zero-width and bidi format characters), a domain-like token (`example.com`), `://`, `hxxp`, any denylisted execution term, an unknown topic, more than `DreamMaxNewPerRun`; accepted ones are saved `active` at once | `FleetLessonsTests`, `FleetDreamTests` |
| 4 | A duplicate of an active lesson reinforces it (renewed `LastSupportedAt`, merged evidence) instead of adding one; a duplicate of a disabled one is refused; the stored and tombstone caps hold | `FleetLessonsTests` |
| 5 | Only active, unexpired lessons are injected, newest first, within `LessonsInPrompt` | `FleetLessonsTests` |
| 6 | The planner prompt contains the active lessons at depth 0, at depth > 0 (a sub-team) and on a re-plan after a failed judge; `lessons.applied` is published with ids, depth and chars for each; disabled/expired ones are absent | `FleetTeamRunnerTests` |
| 7 | The first application records `AppliedLessons` on the task, appends one application per lesson and bumps `AppliedCount` once per task (not per planner call), even when several sub-team planners start at once | `FleetTeamRunnerTests`, `FleetLessonServiceTests` |
| 8 | A failing lessons read does not fail the task; a throwing or slow `RecordApplicationAsync` neither fails nor delays planning | `FleetTaskServiceTests` |
| 8b | **A whole task run through `FleetTaskService` ends with the stored record still holding `AppliedLessons`** (the final save must not overwrite it) | `FleetTaskServiceTests` |
| 9 | Fewer than `DreamMinTasks` tasks: no model call. A tick outside the window does nothing and writes nothing; inside it, a tick is skipped (recorded) when the toggle is off or with fewer than `DreamMinNewTasks` new tasks, and a second tick in the same window skips; a manual run ignores the window and the new-task check; the typed tick result is `NotYet`/`Skipped`/`Started`/`Disabled`/`Unavailable` accordingly | `FleetLessonServiceTests` (counting fake client, fake `TimeProvider`) |
| 10 | Fail open: no `submit_lessons` call, a model error, `DreamTimeout`, or the iteration cap yields what was accepted, no exception; a vanished model fails the run with that reason | `FleetDreamTests` |
| 11 | A second concurrent dream for a user is refused; a bad or non-tool-capable model is refused; the routes map them to 409 / 400 | `FleetLessonServiceTests` (gated fake), `FleetLessonApiTests` (mapping) |
| 12 | A disable/delete made while a dream runs survives the dream's merge | `FleetLessonServiceTests` |
| 13 | A `running` run entry with no entry in the in-process registry reads as `interrupted` (persisted under the lock with a re-read); a live run's own final write is not overwritten by it; a still-registered run is not touched | `FleetLessonServiceTests` |
| 14 | The dream's usage is stamped with BOTH its own application name and the user's telemetry context | `FleetDreamTests` |
| 15 | `ScheduleAsync` registers exactly one hourly reminder (idempotent), `UnscheduleAsync` removes it, a tick calls the launcher once, a `Disabled` or `Unavailable` tick result unregisters the reminder and `Started`/`NotYet`/`Skipped` keep it; `GET /api/fleet/lessons` with the toggle on re-registers a missing reminder | `FleetDreamGrainTests`, `FleetLessonApiTests` |
| 16 | Enabling the toggle schedules the grain, disabling unschedules it; the toggle defaults off | `FleetLessonApiTests` |
| 17 | Every route is scoped to the caller: another user's lesson id is 404 and their lessons are never listed; the dream reads only the caller's tasks; the routes answer like the other fleet routes when the fleet is off | `FleetLessonApiTests`, `FleetApiUnavailableTests` pattern (`FleetApiTests.cs:510`) |
| 18 | The lessons blob round-trips per user, a missing blob reads as an empty document, and a record or document missing trailing fields reads with defaults; `FleetTaskRecord` without `AppliedLessons` reads `[]` | `FleetStoreTests` |
| 19 | The fleet share view never exposes lessons (it is an allowlist, `AgentApi.FleetSharing.cs:117-118`; pin it) | `FleetShareApiTests` |
| 20 | The new endpoint partial is mapped (not silently deleted) | `ProjectLayoutTests` / `AgentApiReachabilityTests` |
| 21 | The page has the Lessons button, badge, dialog ids and the per-task line, and `fleet.js` wires them | `FleetPageAssetsTests` |

## Quality gate checklist (from `.agentic/quality-gates.yaml`, 2026-11-10)
- [ ] `coverage_new_code_pct_min`: 80, measured by `node .agentic/scripts/check-new-code-coverage.mjs`
- [ ] `cognitive_complexity_max`: 15, S3776 at build (split `RunAsync`, `Validate`, `Merge` and the digest sections into small methods)
- [ ] `vulnerable_packages`: 0; no new dependency is planned (rule 4)
- [ ] `dotnet format Agent42.slnx --verify-no-changes`, `dotnet test Agent42.slnx`, doc-path integrity (the new ADR path)

## Out of scope
- Rewriting the judge, planner or any other prompt; judge calibration or re-scoring; retiring a lesson because tasks that used it scored
  worse (slice 2, using `AppliedLessons` and `lessons.applied`).
- A per-user timezone or time of day (operator config, UTC); dreaming across users; a Hindsight store.
- Reading results, workspace files or canvas pages in the digest (judge feedback is the signal in slice 1).
- Editing a lesson's text; a Web Push when a night learns something (the badge and dialog are the visibility; a push is a follow-up).
- Chat-side or analyzer access to lessons; showing lessons in share links (never).

## Review
Round 1 (critic on the first, approval-gated draft, 2026-11-09): no ADR contradiction; seven findings, all accepted and carried
into this rewrite: bounded log reads (step 3), the 12,000-char `WrapAsData` cut (step 3), lock scope (step 4), iteration cap and usage
attribution (step 4), missing test seams (Affected areas), missing ACs (9, 10, 13, 14, 17), `docs/features/agent-fleet.md` and the ADR 0097
layout (step 9), the ADR 0121 rebase note (step 4). Rejected: none.
Round 2 (critic on this nightly, auto-applied draft, 2026-11-10): no ADR contradiction; the reminder/port registration claims held
(`SiloRegistrationTests.cs:24`, `TestSiloConfigurator.cs:119`, in-memory reminders in every test silo, Cosmos reminders in the Backend).
Nine findings, all accepted:
1. High, `AppliedLessons` lost to the final task save: fixed in step 5 (assign `task.Record` first, inside the once-only guard), AC 8b.
2. High, a prose result cannot unschedule the grain, and `DreamEnabled=false` never unschedules: fixed in step 6 (typed `FleetDreamTick`,
   `Disabled`/`Unavailable` unregister), AC 15.
3. High, the screening does not do what it claimed: rewritten in Context (hygiene, not defence; an allowlist instead of a URL
   blocklist, Unicode categories named, a domain-token rule, an execution-term denylist so a lesson cannot name a shell-capable agent
   kind, support counted over distinct workspaces, the wider untrusted input named, both residuals stated), steps 2-3, AC 3. NOT done,
   deliberately: excluding tasks whose results contained fetched web text (research tasks are the ones that benefit); stated as a
   residual instead.
4. Medium, reminders do not catch up: reworded; the design now ticks hourly inside a window and re-registers on enable and on GET.
5. Medium, usage attribution incomplete: both context keys (step 4), AC 14.
6. Medium, the age-based "interrupted": replaced by the registry check with a locked re-read (step 4), AC 13.
7. Medium, the lock does not span replicas: stated as an accepted limit in Context and for the ADR; first writes specified under the lock.
8. Medium, sub-team cost/noise: `lessons.applied` carries `chars`, is structural, and the callback is awaited with errors swallowed (step 5), AC 8.
9. Low: where `DisabledFleetLauncher` is registered, the manual-run-uses-the-night note, the grain interface exemplar (`IChatRouteGrain`),
   and the three launcher implementers are now in Context; the record's new trailing field breaks no test (checked by the critic).
Rejected: none. Round 3: not planned unless the user wants one before execution.

## Implementation notes: where it diverged from the plan
Executed 2026-11-10 on `feature/fleet-dreaming-lessons` from main @ 69f52fa. Recorded in ADR 0124 unless noted.
- **UI is a view, not a modal dialog.** The Lessons button opens a view on `/fleet/` the way the prompt editor does (a dialog would block the
  page). The ids the plan names (`lessonsBtn`, badge) exist; the settings controls are `lessonsEnabled`, `lessonsModel`, `dreamNowBtn`.
- **Lesson counters are recorded in the background.** AC 8 says a slow `RecordApplicationAsync` must not delay planning, so
  `ApplyLessonsAsync` assigns `task.Record`, starts the counter write unawaited and saves the record; `FinishAsync` waits at most 5 seconds
  for it. The record save stays on the planning path (critic finding, now stated in the ADR).
- **A run skipped for too few finished tasks does not set `LastRunAt`**, and `TickAsync` also checks `DreamMinTasks`. Skipped nightly entries are
  recorded once per window, not once per hourly tick. A tick registers the run in the registry itself before starting it, so two ticks cannot interleave.
- **`FleetToolLimits.FrameAsData`** is a new public frame-only helper (the repo has no `InternalsVisibleTo`, so the plan's "make `Neutralize`
  internal" could not cross assemblies). `FleetDigest` is public for the same reason.
- **Denylist** uses the prefix `execut` (also catches "execution") and matches at word starts, so "description" and "runtime" are allowed.
- **`Active()` orders by `LastSupportedAt`** (then `CreatedAt`), so a reinforced lesson stays in the top `LessonsInPrompt`.
- **The dreamer does not list existing lessons to the model**: re-deriving one is how it is reinforced. Its per-run cap on accepted proposals is
  `3 x DreamMaxNewPerRun`; reinforcements do not count toward `DreamMaxNewPerRun` in the merge.
- **No `submit_lessons` branch in the shared `FakeChatClient`**: the dream tests use their own `ScriptedDreamClient`, so no shared script can be consumed.
- Critic round 3 (post-implementation): no high findings. Fixed: the reminder heal on GET/PUT is best effort (logged), the nightly background run
  logs a failure that escapes, the page reads `minNewTasks` from the server. Accepted and written down: re-enabling can exceed `LessonsMaxStored`.
- Verification: `dotnet test Agent42.slnx` 3344 passed, 7 skipped on the coverage run. An earlier plain run had 3 failures (two `LiveCopilotTests`
  that drive a real Copilot shell and passed on rerun, one `ChatReplyStreamerTests` timing test that passed alone); none touch this diff.
  DevHost smoke: settings, schedule and the skipped path respond; the learning path is test-proven only (the canned model makes no `submit_lessons` call).
