# Plan: clean-code programme (COMPLETE, cc-01 to cc-11; master — read this first, then the child plan for your session)

planned-against: main @ 02ebdf0

## Overview

A review of the codebase against clean-code principles found it healthy at method level and weak at **class and
composition level**. This programme is a sequence of small, behaviour-preserving MRs, each in its own session, each with
its own child plan (`plan-cc-NN-*.md`, same folder). This file owns the order, the shared constraints, the baseline
numbers and the status table. Update the status table in the MR that finishes each child.

Everything here is **refactoring**: no user-visible behaviour change, no new features. Each child proves that with the
existing test suite staying green plus the narrow new tests the child names.

## Baseline (measured at 02ebdf0)

**Plan 01 re-measured this with the committed tool (`node .agentic/scripts/code-shape.mjs`, `.agentic/code-shape-baseline.json`).** The table below is the ORIGINAL ad-hoc estimate, kept for history; its method lengths are raw SPANS, not lines of code. Committed-tool numbers for `src/`+`tools/`: 2,941 methods/ctors/declarations (the tool also counts ctors and body-less interface/abstract members); span > 60: 36 (the ad-hoc 34 holds); **lines of code > 60: 7** (worst: `ChatTurnAsync` 99 loc, `SelectableTools` 91, `MapModelEndpoints` 91, `GenerateImageTurnAsync` 81, `RunReminderAsync` 74); >= 7 params: 28; `AgentGrain` 4,099 loc / 6,653 span over 32 files, ctor 19. Plan 11 picks thresholds from the LOC numbers.

| Metric | Value |
|---|---|
| C# methods scanned | 2,354 |
| Methods > 60 lines | 34 (worst: `AgentGrain.ChatTurnAsync` 199, `SelectableTools` 154, `ComposeAgent` 151, `MapModelEndpoints` 149, `RunReminderAsync` 131, `GenerateImageTurnAsync` 126) |
| Methods/ctors with ≥ 7 params | 24 (worst: `AgentGrain` ctor 19, `ChatTurnPipeline` ctor 13; most others are HTTP handlers / platform API wrappers) |
| `AgentGrain` | core 1,890 lines; 33 files ≈ 7,500 lines in total (an earlier scan said 7,215/31); ctor 19 params (16 required) |
| `catch (Exception` | 146 sites in 86 files; 74 filtered with `when`; **4 swallow silently** |
| `Task.Delay` in tests | 93 lines / 45 files; 8 private copies of a poll-until helper |
| Raw `Task.Delay` in `src/` with no `TimeProvider` | 6 sites |
| `Program.cs` | 550 lines, 4 inline registration blocks with no `Add*` method |

## Corrections to the first-pass review (so nobody plans against the wrong claim)

- Methods are mostly fine (34 of 2,354 over 60 lines). The problem is **classes**, not methods.
- `catch (Exception` is 146 not 164, mostly justified and commented. **No catch-and-log helper** is planned: it would hide
  per-site decisions (what to swallow, at what level). Only the 4 silent swallows are fixed (plan 03).
- **`FakeTimeProvider` is rejected** (`tests/Agent42.Tests/TestTimeProvider.cs` header: a frozen monotonic clock would
  change the Orleans runtime under every test, and a package is not worth it). Plan 02 fixes waits with a shared helper
  and event signalling instead.
- Chat adapter duplication is real but small: one copy-pasted byte-capped download loop, four status→error mappers, five
  hand-written named-HttpClient registrations. Chunking, retry, dedupe, commands are already shared. Markdown conversion
  and mention parsing are legitimately per-platform. No test reads adapter source as text (safe to refactor).
- The `AgentGrain` TurnContext + 4-service extraction is **already done** (`completed/plan-agentgrain-service-extraction.md`,
  ADR 0106). Plans 08–10 are the *next* steps and do not redo it.

## Order and dependencies

```
01 measure ──┬─ 02 test-waits ───────────────┐
             ├─ 03 silent-catches            │
             ├─ 04 adapter-helpers ─ 05 pipeline-split
             ├─ 06 composition-roots ─ 07 fleet/config/endpoints
             └─ 08 grain-persist+phases ─ 09 grain-leaf-tools ─ 10 grain-tier-b (conditional)
                                                                          │
                                  all of the above ───────────────── 11 enforce + docs
```

- 01 first for 04–11 (they report numbers against its tool); 02 and 03 do not need it.
- **02 goes before the others that touch tests.** It introduces `TestWait` and a guard against private poll helpers; a later
  plan that adds its own helper would be rejected by that guard.
- Parallelism is **not free** (critic, verified): 02 edits `ChatTurnPipelineTests`/`ChatReplyStreamerTests`/`AgentGrainTests`
  which 05 and 08 also edit; 02, 04, 06, 08 and 09 all edit `ProjectLayoutTests.cs`. Default is **sequential in the order
  01, 02, 03, 04, 05, 06, 07, 08, 09, (10), 11**. Parallel worktrees are an opt-in only for pairs that share no file:
  e.g. 03 ∥ 06, 04 ∥ 08 (after 02 merged), and then expect a trivial `ProjectLayoutTests.cs` merge.
- 11 last: it turns measurements into gates (a gate set before the cleanup would fail on day one) and trims docs.
- **ADR numbers** are taken from `origin/main` at merge time, never from the working tree (decisions README).
- **Doc-path check is a hard CI gate** (`.agentic/scripts/check-doc-paths.mjs` over CLAUDE.md, gotchas.md and every
  non-superseded ADR): any plan that deletes or renames a file must `grep -rn "<path>" CLAUDE.md .agentic` and fix the
  references **path-only** in the same MR.
- **New-code coverage is a CI hard block** (`check-new-code-coverage.mjs` counts moved lines as added; ADR 0105 notes
  `Program.cs` scores 0%). A plan that moves code the test hosts never execute must add tests that execute it, not explain
  the number away (constraint 7 forbids editing the gate).

## Status

| # | Plan | Branch | Status | MR |
|---|---|---|---|---|
| 01 | `completed/plan-cc-01-shape-measurement.md` | `feature/cc-01-shape-measurement` | done (pending merge) | |
| 02 | `completed/plan-cc-02-test-waits.md` | `feature/cc-02-test-waits` | done (pending merge) | |
| 03 | `completed/plan-cc-03-silent-catches.md` | `fix/cc-03-silent-catches` | done (pending merge) | |
| 04 | `completed/plan-cc-04-adapter-helpers.md` | `feature/cc-04-adapter-helpers` | done (pending merge) | |
| 05 | `completed/plan-cc-05-pipeline-split.md` | `feature/cc-05-pipeline-split` | done (pending merge) | |
| 06 | `completed/plan-cc-06-composition-roots.md` | `feature/cc-06-composition-roots` | done (pending merge) | |
| 07 | `completed/plan-cc-07-fleet-config-endpoints.md` | `feature/cc-07-fleet-config-endpoints` | done (pending merge) | |
| 08 | `completed/plan-cc-08-grain-persist-phases.md` | `feature/cc-08-grain-persist-phases` | done (pending merge) | |
| 09 | `completed/plan-cc-09-grain-leaf-tools.md` | `feature/cc-09-grain-leaf-tools` | done (pending merge) | |
| 10 | `completed/plan-cc-10-grain-tier-b.md` | `feature/cc-10-grain-tier-b` | done (pending merge): only the two read-only partials, ADR 0148; write-based partials stay | |
| 11 | `completed/plan-cc-11-enforce-and-docs.md` | `feature/cc-11-enforce-and-docs` | done (pending merge): S138 80 + S107 11 gates, ADR 0149 | |
| 12 | `plan-cc-12-grain-tier-b-write-partials.md` | `feature/cc-12-<n>-<slug>` | optional follow-up to 10, not started | |

## Shared constraints (every child inherits these)

1. **Behaviour-preserving.** A refactor MR changes structure only. If a step needs a behaviour change, stop and split it
   into a separate `fix/*` MR. Existing tests are the safety net; do not weaken or delete one to make a move pass. Where a
   test pins source text (listed per child), update the pin **in the same step** as the move and say so in the MR.
2. **Flat namespace.** Agents/Backend types stay in the flat root namespace; new files go in feature folders
   (ADR 0039, CLAUDE.md cross-cutting rule 1). `ProjectLayoutTests.EveryFileStaysInTheFlatNamespace` enforces it.
3. **Persisted types are untouched.** Do not rename or move any type that appears in grain state or a stored document
   (`$type` is the full CLR name). Moving *grain behaviour* into collaborators is fine; moving state classes is not.
4. **`AgentGrain` is non-reentrant and ADR 0106 holds:** the grain is the only owner of `_state`, `_session`, `_agent`;
   per-run state is a `TurnContext`; collaborators are constructed in `OnActivateAsync` or injected as direct ctor deps
   (ADR 0105: a new direct dep must be registered in every host and the silo startup check only sees direct deps).
5. **Gates** (copied from `.agentic/quality-gates.yaml` at plan time; re-read the file, never trust this copy):
   `coverage_new_code_pct_min: 80`, `cognitive_complexity_max: 15` (S3776, build error), `vulnerable_packages: 0`.
   Plus CI: build, tests, `dotnet format --verify-no-changes`, doc-path integrity.
6. **Per MR:** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
   then update this status table. Moved code keeps its comments (they hold the *why*). Fix any doc claim the diff
   invalidates in the same MR (`/sync-docs`).
7. **Pre-existing failures** are fixed in-branch, never by editing `quality-gates.yaml` (memory: gate-failures-fix-dont-defer).
8. **Never edit `Directory.Packages.props` for a new package without resolving the version via tooling** (hard rule 4).
   None of these plans needs a new package.
9. Run each child in a fresh session (`/clear`), branch from up-to-date `main`, one MR per child. A child that grows past
   ~600 changed lines should be split at a step boundary and the remainder recorded in the status table.

## Success criteria for the programme

- No method over the chosen length threshold and no method/ctor over the chosen param threshold, **or** a visible, reasoned,
  class-scoped exemption for each remaining offender (plan 11). Enforced by plan 11, not by good intentions.
- `AgentGrain` ctor stays direct-DI (ADR 0105/0106) and will shrink only modestly (~19 → ~15): the real gains are the single
  persist sequence, ≤ 100-line methods and named tool-set classes. **No ctor-count target:** the aggregate-object way to hit a
  number was rejected (plan 09).
- Zero silent `catch (Exception` swallows in `src/`.
- No private copy of a poll-until helper in `tests/`.
- `Program.cs` ≤ ~300 lines and only order-sensitive code.

## Decisions for the user (defaults chosen; veto any)

1. Thresholds enforced in plan 11: S138 (method length; Sonar default **80** lines of code on the pinned 10.37) and S107
   (params; default 7). Proposal: ≤ 100 lines of code if offenders remain, else 80; params ≤ 7. Adding them amends ADR 0050's
   "exactly one rule" sentence. Plan 11 step 1 picks from measured offender counts; tell me if you prefer a number now.
2. Plan 10 (grain tier B: web/document/attachment/artifact tool partials) is **conditional**: do it only if plan 09 shows
   the collaborator pattern worked without friction. Tier C (Research, ImageTurn, Copilot, ClientTools, SharedState,
   EditFork, Scheduling, RetentionSweep, BuildAgent hub) stays in the grain: they are entangled with the turn/session
   and a split there is a behaviour risk (ADR 0106 "left for a separate plan").
3. Parallelism: default is sequential (see Order and dependencies).

## Out of scope (programme-wide)

Any feature work; the frontend (`wwwroot/js`, the SW cache rule stays as is); Android/ChromeExtension/LocalBridge tools;
Orleans clustering / model-proxy plans already in `specs/`; renaming `AgentApi.*` partials beyond what plan 07 names.

## Review

Critic run over the whole set (verdict: not ready as first written). **Accepted and fixed** — HIGH: plan 09 aggregate rejected
(ADR 0105 safeguard, untestable for Backend, reverses ADR 0106 §4, ctor arithmetic ~19→~15 not ~10); plan 08 non-existent MCP
pins removed, persist sites 3→6 with include/exclude decision and webhook return value; plan 11 S138 default (80) and `max`
parameter, lines-of-code metric, all-projects scope, path-scoped exemptions, `QualityGateTests` that break or go vacuous;
plan 06 coverage hard block + slice boundary + `models` hand-back. MEDIUM: plan 05 ctor ≈ 11 and call sites; doc-path
CI gate for deleted files (05, 09); plan 02 more helpers and shape-based guard; plan 03 premise corrected and logger plumbing
specified; plan 07 name collision, real sibling, lock semantics, route-table snapshot; false parallelism claim (above); plan 04
status mapping rescoped. LOW: all accepted, see each child's Review. **Rejected: none.** Known unverified: the critic's S138/S107
default values come from reading the DLL; plan 11 step 1 re-verifies.
