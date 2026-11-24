---
id: 0050
date: 2026-08-25
status: accepted
tags: [quality-gates, tooling, ci, analyzers, coverage]
amended_by: [0051]
---

# Title: Every quality gate must have a measuring tool in this repo

## Context

`.agentic/quality-gates.yaml` carried five gates: `new_issues: 0`, `new_security_issues: 0`,
`duplication_pct_max: 3`, `coverage_new_code_pct_min: 80`, `cognitive_complexity_max: 15`.

**Four of the five had nothing in the repo that could produce a number.** They are SonarQube
"Clean as You Code" metrics — the file is a Sonar gate transplanted into a repo with no Sonar. The
evidence was already in the tree and had simply never been acted on:
`.github/workflows/pr-checks.yml` said *"Coverage/duplication/complexity gates are self-reported
only (no Sonar-style server)"*, and `.agentic/optional-skills/sonar/` sat uninstalled with the
activation rule "The repo has a Sonar quality gate" — which is exactly the ambiguity. The repo had
the gate *numbers* written down, but not the thing that produces them.

This surfaced during `/execute-plan` on ADR 0049, where step 4 demands that every "Actual" value
come from real command output. Two gates had to be reported **SKIPPED — no tooling**, which is the
correct behaviour (`/execute-plan`: *"A gate with no measuring tool in this repo shouldn't exist"*)
but is not a stable resting place: a threshold nothing measures **cannot fail**, so it is not a
gate. It is a wish that reads like one, and it makes the gate table look more rigorous than it is.

A second, narrower defect: `cognitive_complexity_max` is Sonar rule **S3776**, but the installed
`dotnet-complexity` skill reads `gates.cyclomatic_complexity_max` — a key that does not exist here.
Cyclomatic and cognitive complexity are **different metrics with different numbers** (measured:
`ChatCoreAsync` is 29 cyclomatic and 39 cognitive), so this was never a naming slip that could be
fixed by renaming one to the other.

## Decision

**The admission test for `quality-gates.yaml` is now: name the tool, or delete the gate.**

### Kept, and made real

- **`coverage_new_code_pct_min: 80`** — `.agentic/scripts/check-new-code-coverage.mjs`, run by a new
  `new-code-coverage` CI job. It intersects coverlet's cobertura report with `git diff -U0` against
  the merge base, so it gates **the lines this branch added or changed**, not the repo's slow-moving
  overall number — which is the whole point of a "new code" gate and the reason the previous
  self-reported figure was not one. No npm dependencies, matching `check-doc-paths.mjs`.
  `fetch-depth: 0` in the workflow is load-bearing: a shallow checkout has no merge base, so the
  gate would silently measure nothing.

- **`cognitive_complexity_max: 15`** — `SonarAnalyzer.CSharp` (10.33.0.1635, resolved from the feed
  at add time) as a `GlobalPackageReference` — the Central Package Management analyzer form, which
  applies to every project and implies `PrivateAssets="all"`. Rule S3776 becomes a **build-time
  diagnostic**: no Sonar server, no token, no Java, no new CI job.

### Deleted

`new_issues`, `new_security_issues` and `duplication_pct_max`. Nothing measures them, and
`new_issues: 0` without a definition of "issue" is unfalsifiable. Deleting them is not a loosening —
they never constrained anything. What replaces them is the honesty that the remaining two are real.

### Exactly one Sonar rule is adopted, and that is a measurement, not a preference

The analyzer ships hundreds of rules. Enabling them all produces **~300 warnings on the tree at
`aa247ff`**, and the top ones fire on this repo's deliberate idioms rather than on defects:

| Rule | Count | What it fires on here |
|---|---|---|
| S8969 | 164 | — |
| S108 "Either remove or fill this block" | 40 | the `catch { /* best effort */ }` idiom, used throughout |
| S3220 | 34 | `params` overload resolution |
| S125 "Remove this commented out code" | 30 | the dense explanatory comments this repo is written in |
| S3267 | 12 | "simplify this loop with LINQ" |
| S2699 "Add at least one assertion" | 4 | tests asserting via `Assert.Single` / fixture effects |

Adopting them would mean either ~300 standing warnings or ~300 suppressions. Neither is a quality
gate. So `.editorconfig` sets `dotnet_analyzer_diagnostic.severity = none` and turns **S3776 alone**
back on. `QualityGateTests.OnlyTheAdoptedSonarRule_IsEnabled` pins that to exactly one rule, so
widening the adoption is a deliberate, visible act.

### The threshold lives in one place

Measured on 10.33.0.1635: S3776's **built-in default threshold is 15**, already exactly what the
gate file says — so no configuration is needed and there is no second number to drift. Also
measured: the threshold is **not** settable via `.editorconfig` (both
`dotnet_diagnostic.S3776.threshold` and `sonar.cognitive-complexity.threshold` are silently
ignored); it requires a `SonarLint.xml` `AdditionalFiles` entry, which *does* work (dropping it to 3
changed the message to "to the 3 allowed").
`CognitiveComplexityGate_MatchesWhatTheAnalyzerActuallyEnforces` fails if the yaml is ever moved off
15 without adding that file — otherwise the build would keep enforcing 15 while the yaml claimed
otherwise, silently, because nothing at runtime reads both.

## The baseline, and why the gate does not block yet

**It does not pass today.** Eleven methods exceed 15, all pre-existing:

| Location | Cognitive complexity |
|---|---|
| `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs` | 41 |
| `src/Agent42.Agents/Conversations/AgentGrain.cs` (`ChatCoreAsync`) | 39 |
| `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` | 29 |
| `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs` | 27 |
| `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` | 25 |
| `src/Agent42.Agents/Conversations/AgentGrain.cs` (`GetHistoryAsync`) | 23 |
| `src/Agent42.ServiceDefaults/Extensions.cs` | 22 |
| `src/Agent42.Backend/UserSkills/AgentApi.UserSkills.cs` | 22 |
| `tests/Agent42.Tests/FakeChatClient.cs` | 21 |
| `src/Agent42.Backend/Attachments/AgentApi.Attachments.cs` | 18 |
| `src/Agent42.Backend/Program.cs` (top-level) | 16 |

So S3776 is enabled at **`warning`** — visible in every build, not `-warnaserror`.

**That collides with the format gate, and the collision is only visible in CI.** `dotnet format` runs
an `analyzers` pass and exits **2 on any analyzer diagnostic at warning severity**, so
`--verify-no-changes` fails on the eleven — in a job that has nothing to say about complexity. The
fix is `--exclude-diagnostics S3776` on that command only (in the workflow *and* in CLAUDE.md's
Commands table, or every local lint run disagrees with CI); it comes off when the backlog clears.
The tempting alternative — dropping S3776 back to `info` — silences it everywhere and returns the
gate to measuring nothing, which is the failure this ADR exists to correct.
`FormatGate_ExcludesTheRecordedComplexityBacklog` pins the two-file agreement. The gate is **measurable now and blocking once the backlog is cleared**; the
`enforcement` block in `quality-gates.yaml` says so rather than implying it already binds.

Refactoring eleven methods — including the app's central `ChatCoreAsync` — was explicitly **not**
folded into this change. Wiring up a measuring tool and rewriting the hottest code path in the app
are different pieces of work with different risk, and doing the second as a side effect of the first
is how a tooling MR turns into an outage. It is recorded as debt with the numbers attached, which is
the first time those numbers have existed.

**Five of the eleven are `Map*Endpoints` partials** (ADR 0040), where the metric sums several
independent registration lambdas in one method. That is an artifact of the minimal-API idiom rather
than tangled logic, and a fix there means fighting ADR 0040's structure — worth deciding
deliberately, not silently.

## Alternatives considered

- **Install SonarQube** (the gates were written for it; the skill is ready to copy from
  `.agentic/optional-skills/sonar/`). Rejected: SonarCloud is free only for public repos and this
  one is private; self-hosted Community means a container plus Java 17+ locally for the scanner.
  That is standing infrastructure for five numbers on a single-user project. The analyzer package
  gets the one metric that mattered with none of it.
- **Delete all four unmeasured gates and add no tooling** — the zero-effort honest option, and what
  `/execute-plan` literally prescribes. Rejected for `cognitive_complexity_max` only, because the
  tool turned out to cost one package reference and three lines of `.editorconfig`. At that price,
  keeping the gate beats deleting it. The other three are deleted exactly as prescribed.
- **`jscpd` for `duplication_pct_max`.** Available (5.0.16) and would fit the existing Node job.
  Rejected: a 3% duplication threshold on a repo this size is noise, and nobody had ever acted on
  the number. Adding a tool to keep an unwanted gate is the wrong direction.
- **Rename the gate to `cyclomatic_complexity_max`** so the installed `dotnet-complexity` skill
  scores it. Rejected: it would silently change which metric is gated, and cyclomatic is the weaker
  of the two here — it flags long flat methods, which are readable, and misses deep nesting, which
  is not. The skill's own docs are corrected instead to say it is informational in this repo.
- **Adopt more Sonar rules** than S3776. Rejected on the measurement above; revisitable per-rule.

## Consequences

- **The gate table in every future `/execute-plan` run is now fully evidence-backed** — no SKIPPED
  rows, no self-reported numbers.
- **New code must be 80% covered, enforced in CI.** This is the first gate in the repo that can fail
  a PR for a reason other than a broken build, a failing test, a format diff, a vulnerable package
  or a stale doc path.
- **`dotnet build` gains one analyzer package.** It is `PrivateAssets="all"`, so it never flows to
  consumers.
- **`dotnet_analyzer_diagnostic.severity = none` is repo-wide, so it silences the built-in .NET
  analyzers (`CA*`, `IDE*`) too, not just Sonar's.** Nothing is lost today — measured on `main`, the
  build emitted no `CA`/`IDE` diagnostics at all, only the five pre-existing `CS8765` (a *compiler*
  warning, unaffected by analyzer severity). But it does mean a future `CA` rule must be turned on
  explicitly rather than arriving by default. That is the cost of the blanket default-off, and it was
  preferred to enumerating ~23 Sonar rule ids, which would go stale on the next analyzer version bump
  and read like "we suppressed everything to make it pass".
- **Eleven standing S3776 warnings** until the backlog is cleared — deliberate visibility, but it
  does mean `dotnet build` is no longer warning-free. (The five pre-existing `CS8765` in
  `CannedArmTransport.cs` already were not.)
- **`QualityGateTests` makes the gate file self-policing**: a gate added without tooling fails the
  suite, and so does a threshold that disagrees with what the analyzer actually enforces.
- **The `dotnet-complexity` skill is now explicitly informational here.** It still reports cyclomatic
  numbers on demand; it is no longer mistaken for the thing that scores the gate.

## Verification

- `QualityGateTests` — the gate↔tool mapping in both directions, the threshold agreement, S3776
  explicitly enabled, exactly one Sonar rule adopted, and CI actually invoking the coverage gate.
- The coverage script was run against a real cobertura report and a real diff, and both no-op paths
  (no production code changed; no coverable lines) were exercised.
- The S3776 mechanism was measured end to end with a throwaway probe: enabled, it fires at the
  expected complexity; probe removed, the eleven real violations remain; a `SonarLint.xml` override
  changes the threshold; an `.editorconfig` override is silently ignored.
- **A correction worth recording, because it is why this ADR quotes counts rather than impressions.**
  The first baseline taken for this work reported *zero* S3776 violations, and the conclusion drawn
  was "the gate passes today with no fixes needed". That was wrong twice over: the grep counted only
  `warning`, and S3776 ships at **`info`** severity by default — so all eleven were invisible. A gate
  whose rule is left at its default severity looks wired up and measures nothing, which is the same
  failure this ADR exists to correct, reproduced in miniature while correcting it.
  `S3776_IsExplicitlyEnabled_AndTheAnalyzerIsReferenced` is the guard against repeating it.
