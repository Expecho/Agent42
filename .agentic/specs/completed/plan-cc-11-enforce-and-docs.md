# Plan cc-11: turn the measurements into gates, and trim the docs

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md`; **run last** (after the cleanup children have merged; a gate set earlier fails on day one).
Revised after critic review (see Review).

## Overview

Two jobs. (1) Enforce method length and parameter count with a measuring tool (hard rule 3, ADR 0050 admission test).
(2) Trim documentation that restates code and drifts (hard rule 2).

## Affected areas

- `.agentic/quality-gates.yaml`; `.editorconfig` (`dotnet_diagnostic.S138/S107`); `tests/Agent42.Tests/QualityGateTests.cs`
  (read it first: see "Tests that will fail or go vacuous"); `.agentic/code-shape-baseline.json` (plan 01);
  `.agentic/decisions/0050-*.md`, `0051-*.md`; `CLAUDE.md` (rule 9; the "Convention enforcement" sentence "…and exactly that
  one rule"); `.agentic/scripts/code-shape.mjs`; `.github/workflows/pr-checks.yml`.
- Docs to trim: CLAUDE.md "Feature map" and "Cross-cutting rules" (keep each rule's one line, drop rationale the ADR already
  holds); gotchas entries whose Evidence paths are gone after the refactors (`/audit-docs`).

## Facts established by the critic (verify again in step 1; do not recall)

- Pinned analyzer: `SonarAnalyzer.CSharp 10.37.0.15768` (`Z:\Packages\.nuget\packages\sonaranalyzer.csharp\10.37.0.15768\analyzers`).
  **S138 default max is 80 lines of code** (not 200); **S107 default max is 7**. Both parameters are named **`max`** (not
  `maximumParameterCount`). Both are configurable through a `SonarLint.xml` AdditionalFile.
- S138 counts **lines of code** (non-blank, non-comment). Plan 01 defines `code-shape`'s metric as the same, so `quality-gates.yaml`
  has one meaning for `method_lines_max`. If the script cannot match Sonar's count within a line or two, enforce through Sonar and
  keep the script as the report only.
- The analyzer is a `GlobalPackageReference`: S138/S107 as `error` hit **every** project: `tests/`, `tools/`, DevHost, not just `src/`.
  Step 2 must run the measurement over all of them, or scope the rules with path sections in `.editorconfig`.
- Sonar has no allowlist. The only exemption mechanism consistent with "no new `[SuppressMessage]`/pragmas" is **path-scoped
  `.editorconfig` sections** (e.g. `[**/AgentApi.*.cs]` with `S107` at a higher `max` or `suggestion`), each with a one-line
  reason comment. A name allowlist works only on the script-ratchet branch.
- `QualityGateTests` today: `OnlyTheAdoptedSonarRule_IsEnabled` does `Assert.Equal(["S3776"], enabled)` → **breaks** when S138/S107
  are enabled (update it to the new set, deliberately). `CognitiveComplexityGate_MatchesWhatTheAnalyzerActuallyEnforces` passes if
  `declared == 15 || hasSonarLintXml` → a `SonarLint.xml` that now also carries S138/S107 makes it **vacuous for S3776**: tighten it
  to parse the S3776 value out of the file. `NoGateExists_WithoutAnEntryInTheToolingMap` pins keys to `GatesAndTheirTooling`:
  every new key needs a map row.
- ADR 0050 admission test: a key `method_lines_max: 100` that tolerates existing offenders "lies". The yaml value must be the real
  enforced threshold; any tolerance lives in the exemption sections, each reasoned, not in the number.
- A ratchet baseline keyed `file:line` goes stale on the next edit. Key it by **file + fully-qualified method name**.

## Context & constraints

- **Thresholds:** the master plan's defaults (≤ 100 lines of code, ≤ 7 params) are proposals for the user: S138's own default is 80,
  so 80 is an equally defensible choice if the cleanup gets everything under it. Decide in step 1 from the measured offender count.
- **Programme criteria vs the gate:** after plans 04-09, `AgentGrain` (≈16-19 params) and possibly `ChatTurnPipeline` (≈11) will exceed 7.
  Do **not** hide them with a path exemption that makes the headline offenders invisible. Either (a) they are fixed by then,
  (b) they get a narrow, reasoned, *visible* exemption (class-scoped section naming the type), recorded in the MR as known debt, or
  (c) the threshold is set where the codebase actually is and ratcheted. Say which, per offender.
- Never hardcode thresholds outside `quality-gates.yaml`; the SonarLint.xml/.editorconfig value is the single allowed mirror,
  pinned by `QualityGateTests` (for **each** rule, not only S3776).
- ADR 0050/0051 amendment: "exactly that one rule" becomes three rules. `/decide` decides whether it is a note or a new ADR; next
  number from origin/main at merge.
- Doc trimming rule: delete a claim only if code or a test states it. `/audit-docs` before and after; the drift count must go down.

## Steps

1. Spike: confirm S138/S107 defaults and `SonarLint.xml` parameter names on the pinned version (re-read the analyzer metadata);
   measure offenders over **all** projects; pick thresholds and mechanism; record the evidence in the MR.
2. Fix or exempt (reasoned, visible, path/class-scoped) every offender over the thresholds; list exemptions in the MR.
3. Add the gates + the `QualityGateTests` changes listed above (`OnlyTheAdoptedSonarRule…`, tightened complexity check, per-rule
   mirror check, tooling-map rows).
4. If the script ratchet is the mechanism for any gate: wire CI (`pr-checks.yml`), key the baseline by file + method, and make the
   yaml say it is a ratchet. Confirm a deliberately bad fixture fails (locally; do not push it).
5. ADR amendments + CLAUDE.md edits.
6. Docs trim with `/audit-docs`; `/sync-docs`.
7. Mark the programme complete: move all `plan-cc-*.md` to `specs/completed/` in this MR; update the master status table first.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Only the three adopted Sonar rules are enabled | `OnlyTheAdoptedSonarRules_AreEnabled` (renamed/updated) | `QualityGateTests.cs` |
| Each gate's yaml value equals its analyzer config value (S3776, S138, S107) | `Gates_MatchWhatTheAnalyzerEnforces` (tightened: parse the XML) | `QualityGateTests.cs` |
| Every gate key maps to tooling | `NoGateExists_WithoutAnEntryInTheToolingMap` (rows added) | `QualityGateTests.cs` |
| A 150-line method fails the build | verified locally with a throwaway fixture, quoted in the MR (not committed) | — |
| Doc drift not increased | `/audit-docs` before/after in the MR | — |

## Quality gates

Read `.agentic/quality-gates.yaml` at execution time (coverage_new_code_pct_min, cognitive_complexity_max, vulnerable packages 0);
this plan adds two gates and its own MR must pass them.

## Out of scope

Class-size gates (partials make LOC a poor signal); analyzers beyond S138/S107.

## Review

Critic HIGH #4/#5 accepted: S138 default corrected to 80 and parameter name to `max`; metric unified (lines of code); all projects
in scope; exemptions via path-scoped `.editorconfig`; named `QualityGateTests` that break or go vacuous; ratchet-vs-admission
tension and baseline keying addressed; headline-offender exemption trap addressed.

## Note from plan 01

The committed baseline (`.agentic/code-shape-baseline.json`) is floor-filtered: methods with loc >= 40 or params >= 5, classes with loc >= 400 or ctor params >= 5. A ratchet threshold must stay at or above that floor, or regenerate with `--all`. Entries also carry an informational `line` that goes stale; strip it or rewrite the file only deliberately. The baseline is a snapshot: refresh it in the MR that sets the gate.
