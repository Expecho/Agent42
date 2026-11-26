# Plan cc-01: a committed code-shape measurement tool and baseline

planned-against: main @ 02ebdf0
parent: `plan-cc-00-programme.md` (read its "Shared constraints" first)

## Overview

The programme's targets (method length, parameter count, class size) are currently measured by a throwaway regex script.
Hard rule 3 says a threshold nothing measures cannot be a gate. This plan commits a measurement tool and a baseline
report so every later child can show before/after numbers, and plan 11 can turn it into a gate. **No gate is added here.**

## Affected areas

- New: `.agentic/scripts/code-shape.mjs` (sibling of `check-doc-paths.mjs`, `check-new-code-coverage.mjs`; read one for the
  house style: Node, no dependencies).
- New: `.agentic/code-shape-baseline.json` (generated, committed) — plan 11 reads it for the ratchet.
- Docs: a line in `.agentic/gotchas.md`? No — a row in the CLAUDE.md Commands table ("Code shape").

## Context & constraints

- Do **not** use Roslyn metrics or Sonar to measure here if it needs a new package: a regex/brace scanner over `src/` is
  enough for a *report*. But the scanner must handle: expression-bodied members (`=>`), primary constructors
  (`class X(A a, B b)` count as ctor params; fixture-test this, `AgentGrain` has none), local functions (count separately or
  ignore, state which), attributes on parameters, generic commas inside `<>`, strings/comments containing braces.
  Compare its output on the files in the baseline table of the master plan. Before the tool is committed it replaces an
  ad-hoc regex script whose numbers are only ±10%.
- Exclude `obj/`, `bin/`, generated files, `tests/`. Report `tools/` separately.
- **Metric definition (must match Sonar S138, which plan 11 may enforce):** a method's length is **lines of code**, i.e. non-blank,
  non-comment lines between its first and last line. State the definition in the script header and in the test fixture. Report
  total span as a second column if useful, but the gated number is lines of code.
- Output: per-method (**key = file + fully-qualified method name**, never file:line, which goes stale; line is informational),
  lines of code, params; per-class (file set for partials, total lines, ctor params, member count); sorted descending;
  machine-readable JSON plus a markdown summary on stdout. Scan `src/`, `tools/` **and `tests/`** as separate sections (plan 11
  may enforce over every project because the Sonar analyzer is a `GlobalPackageReference`).
- If a measuring tool for these metrics already ships in the repo's analyzers (check `Directory.Packages.props`: Sonar
  S138/S107/S1200), prefer **enabling them as warnings** only if that needs no new package; otherwise keep the script.
  Plan 11 decides gating; this plan only needs numbers.

## Steps

1. Read the two existing `.agentic/scripts/*.mjs` for style. Write `code-shape.mjs`. Verify: run it, check the master
   plan's baseline table within ±10% and spot-check 5 methods by hand against the source.
2. Add a `--json` baseline mode and commit `.agentic/code-shape-baseline.json`.
3. Add `tests/Agent42.Tests/CodeShapeToolTests.cs`: a small fixture (a temp `.cs` string) with known method lengths,
   param counts (incl. primary ctor, generic commas, expression-bodied) proving the tool's numbers. Run via `node` the way
   `check-doc-paths` is tested (find that test; copy its process-launch shape).
4. CLAUDE.md Commands table: add "Code shape: `node .agentic/scripts/code-shape.mjs`". Update the master status table.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| Counts method lines/params correctly incl. primary ctors, `=>`, generics | `CodeShapeTool_MeasuresKnownFixture` | `CodeShapeToolTests.cs` |
| Baseline is keyed by file + method and every key resolves in the tree | `CodeShapeBaseline_KeysResolve` | `CodeShapeToolTests.cs` |
| Lines-of-code metric agrees with Sonar's definition on the fixture (blank/comment lines excluded) | `CodeShapeTool_CountsCodeLinesOnly` | `CodeShapeToolTests.cs` |
| Tool runs on the real tree without error | `CodeShapeTool_RunsOverSrc` | `CodeShapeToolTests.cs` |

## Quality gates

coverage_new_code_pct_min 80 (the Node script is not covered by coverlet: the xUnit fixture test is its coverage,
say so in the MR); cognitive_complexity_max 15 applies to C# only.

## Out of scope

Any threshold or gate (plan 11); fixing any measured offender.

## Review

(Critic pass on the diff: no blocking findings; its medium/low items are applied below.)

## Implementation notes — where it diverged from the plan

- **Step 1 verification (evidence).** Spans match the master estimate (`ChatTurnAsync` 199, `SelectableTools` 154, `MapModelEndpoints` 149, `GenerateImageTurnAsync` 126, `RunReminderAsync` 131); span > 60 gives 36 vs the estimated 34 (within 10%). `AgentGrain` ctor 19 and `ChatTurnPipeline` ctor 13 match. Hand spot-checks against source: `ChatMessageChunker.SafeCut` (2 loc), `ChatMessageChunker.FenceAfter` (12 loc, 13 span, one blank line excluded), the fixture's `Block` (8 loc / 12 span), `AgentGrain` ctor (19 params), and the tuple-return `FindCut` (params 2, not mistaken for the tuple). A tuple return type (`static (int, int) M(...)`) was misread as the parameter list at first and fixed.
- **Counts differ from the master table:** 2,941 vs 2,354 methods because the tool also counts constructors and body-less interface/abstract members (S107 applies to them); lines of code (the gated metric) give 7 methods > 60, not 34 (that was spans). The master baseline section now says so.
- **Baseline is floor-filtered** (see the script header) and carries `measuredAt`; the whole tree would be ~1 MB. Plan 11 note added.
- **`CodeShapeBaseline_KeysResolve` is weaker than the AC row reads, on purpose:** the committed baseline is a snapshot whose keys legitimately vanish as the programme refactors offenders, so a strict "every baseline key resolves" test would fail on plan 05. The test instead checks that a fresh run's keys are unique and name existing files and spelled method names, and that the committed baseline is well formed (sections, unique keys, key format). It cannot detect a hand-corrupted baseline; plan 11's ratchet will.
- A primary constructor feeds the class's `ctorParams` (fixture-tested: 3 with generic commas and a default), not a method entry.
- No gate added, `quality-gates.yaml` untouched. The Sonar analyzers (S138/S107/S1200) were not enabled: left to plan 11.
