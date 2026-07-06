---
name: plan
description: Create an implementation plan for a nontrivial task and persist it to .agentic/specs/ for execution by /execute-plan.
user-invocable: true
disable-model-invocation: true
argument-hint: "<task>"
---

# Plan a nontrivial change

Transformed from the workspace `/forge` skill: single-repo, gates read from
`quality-gates.yaml` (never hardcoded), interview discipline from `/jira-implement`.

## Step 1 — Sync & understand

- Offer to `git pull` first (plans against stale code are wrong on arrival).
- Read `.agentic/gotchas.md` entries and the exemplars for the affected area.
- Grep `.agentic/decisions/` for active ADRs touching this area — a plan that
  contradicts an ADR needs `/decide` (supersession) as an explicit plan step.
- Investigate the code itself; use read-only subagents for breadth. Native plan
  mode is welcome — this skill governs the *artifact*, not how you think.

## Step 2 — Interview (only if ambiguous)

Max 2 rounds, 3–7 questions total via AskUserQuestion. Ask about intent, scope
boundaries, and acceptance criteria — never about things the code can answer.

## Step 3 — Write the plan

Write `.agentic/specs/plan-<slug>.md`:

- **Overview** — goal + why now (2–5 lines).
- **Affected areas** — files/modules, verified against the actual tree, never from memory.
- **Steps** — discrete, ordered; each names the files it touches and how to verify it
  (which test/command proves the step works). Mark independent steps as parallelizable.
- **Acceptance criteria → test mapping** — a table: each AC, the test that will prove
  it, and the test file it lives in.
- **Quality gate checklist** — copy the thresholds FROM `.agentic/quality-gates.yaml`
  at plan time; never write numbers from memory.
- **Out of scope** — what this plan deliberately does not do.

## Step 4 — Approval

Present the plan and get explicit user approval before any execution. On approval,
hand off to `/execute-plan`. Do not start implementing inside this skill.
