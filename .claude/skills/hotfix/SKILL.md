---
name: hotfix
description: Fast-path fix — diagnose, minimally fix, regression-test, and MR in one compressed flow. Escalates to /plan when the change outgrows a hotfix.
user-invocable: true
disable-model-invocation: true
argument-hint: "<issue description>"
---

# Hotfix (compressed diagnose → fix → validate)

Transformed from the workspace `/hotfix` skill; its guardrails were the valuable
part and are kept verbatim in spirit.

## Guardrails (non-negotiable)

- **Max 5 files.** More than that, or any architectural change → stop and escalate
  to `/plan`. A hotfix that grows is a feature wearing a disguise.
- **No refactoring, no drive-by cleanups.** Minimal diff that fixes the issue.
- **A regression test is mandatory** — a test that fails without the fix and passes
  with it. No exceptions; an untested hotfix is a second incident scheduled.
- **Always a branch** (`hotfix/<slug>` off the production branch, or `fix/<slug>`
  off the integration branch — follow CLAUDE.md's branch policy).

## Phase 1 — Diagnose (timebox ~2 min of investigation before coding)

Read `.agentic/gotchas.md` for the affected area FIRST — the trap may already be
documented. Trace the actual root cause; do not fix symptoms.

## Phase 2 — Fix

Smallest change that resolves the root cause + the regression test.

## Phase 3 — Validate

Run the matching tests, then full test + lint from CLAUDE.md's Commands table.
Render the quality-gate table from `.agentic/quality-gates.yaml` using real
command output.

## Phase 4 — Close

- If the root cause was a non-obvious trap, capture it: `/gotcha` (evidence = the
  fixed file). This is how the same hotfix stops happening twice.
- Commit (conventional format), push, create the MR using the repo's platform
  command (see `/execute-plan` step 5). If the branch was cut from the production
  branch, note the mandatory backport to the integration branch in the MR body.
