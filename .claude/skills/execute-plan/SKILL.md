---
name: execute-plan
description: Execute a plan from .agentic/specs/ with per-step test gating, evidence-based quality gates, and spec lifecycle handling through to an MR.
user-invocable: true
disable-model-invocation: true
argument-hint: "[plan file — defaults to most recently modified plan-*.md in .agentic/specs/]"
---

# Execute a plan

Transformed from the workspace `/erupt` skill. The three disciplines that made it
valuable are kept; the multi-repo, expertise-YAML, and hardcoded-threshold parts are not.

## Step 0 — Branch

Never build on a protected branch (the `protect-main` hook blocks it anyway).
If on one, create `feature/<plan-slug>` first.

## Step 1 — Load & announce

Load the plan from `.agentic/specs/`. Post a short table of its steps and the
verification command for each, so the user sees the roadmap before work starts.

## Step 2 — Per-step loop (the core discipline)

For every step:
1. Re-read the exemplar for the shape you're about to write (`.agentic/gotchas.md`
   exemplars table). Copy the shape; don't invent.
2. Implement the step.
3. **Immediately run the narrowest matching test** — the test file for the changed
   file, or a filtered run (e.g. `dotnet test --filter <Handler>`, `npx vitest run <file>`).
   Do not batch validation to the end; a broken step found 5 steps later costs 5 steps.
4. If a shared helper/utility changed, run the full suite now, not later.
5. Write the step's tests alongside the implementation, per the plan's AC→test
   mapping table. Tests are part of the step, never a trailing phase.

**Parallel subagents:** dispatching ≥2 write-capable subagents concurrently requires
worktree isolation (`isolation: "worktree"`) or sequentialize — parallel writers in
one checkout corrupt each other's edits.

## Step 3 — Full validation

Run the full test, lint, and type-check commands from CLAUDE.md's Commands table.

## Step 4 — Quality gate (evidence, not estimates)

Read thresholds from `.agentic/quality-gates.yaml` and render a PASS/FAIL table.
Every "Actual" value must come from **real command output produced this session**
(coverage report, lint summary, duplication tool) — never estimated, never
self-reported from memory. On FAIL, follow the `enforcement.local` policy in
quality-gates.yaml (default: show the table, ask proceed/abort).

## Step 5 — Close the loop

- Move the spec to `.agentic/specs/completed/`.
- Decisions made along the way → `/decide`. Traps discovered → `/gotcha`.
  Diff invalidates an existing doc claim → fix it now (see `/sync-docs`).
- Commit (conventional format), push the branch, and create the MR/PR using the
  repo's platform command: `gh pr create --base main --fill`
