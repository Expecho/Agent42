---
id: 0149
date: 2026-11-27
status: accepted
tags: [quality-gates, tooling, analyzers, refactoring]
amends: [0050]   # "exactly that one rule" becomes three
---

# Title: Gate method length (S138) and parameter count (S107) with SonarAnalyzer

## Context

ADR 0050 adopted exactly one Sonar rule (S3776) because the rest fought the repo's idioms. The clean-code programme
(plans cc-01 to cc-10) cut method length and shaped the grain, and left nothing that stops either number growing
back. `code-shape` reports them but is a report, not a gate (its loc count differs from Sonar's by a few lines).
Measured on the pinned analyzer over every project: S138 at its default of 80 lines of code had one offender
(`AgentGrain.ChatTurnAsync`, 88, fixed by extraction); S107 at its default of 7 had 25 offenders outside the grain (15 in
the Backend, 10 in tests) plus the grain constructor (18).

## Decision

Adopt S138 and S107 as build errors beside S3776, with the analyzer, not a script, as the measuring tool.
`method_lines_max: 80` is S138's own default, so no override. `method_params_max: 11` is where the code is, carried by
`SonarLint.xml` (wired by `Directory.Build.props`) and pinned by `QualityGateTests`, which now parses each rule's
number out of that file instead of only detecting one. The AgentGrain ctor (18, ADR 0105/0106: direct DI, no aggregate)
is the single exemption, a path-scoped `.editorconfig` section. The `max` ratchets down as signatures shrink.

## Alternatives considered

- Threshold 7 with a section per offending file: 13 standing exemptions, and the list hides which are real debt.
- Threshold 7 after refactoring the 15 Backend signatures: right end state, wrong size for a gate-and-docs MR.
- Ratchet through `code-shape` and a baseline: needs its own CI wiring and its count does not match the compiler's.

## Consequences

- A method over 80 lines of code, or a signature over 11 parameters, fails `dotnet build` (verified with a throwaway
  fixture: 102-line method and 12-parameter method both error).
- 11 is weak against Sonar's 7. Lowering it is a one-line change in `SonarLint.xml` + the yaml, after the offenders move.
- `OnlyTheAdoptedSonarRules_AreEnabled` now expects S107, S138, S3776.
