---
name: onboard-existing
description: Detection-driven setup of the agentic template for an existing codebase. Infers stack, commands, and conventions from source, mines git history and legacy docs for ADRs and gotchas, and asks the user only what cannot be inferred.
user-invocable: true
disable-model-invocation: true
argument-hint: "[optional: path to repo root, defaults to cwd]"
---

# Onboard an EXISTING repository

Golden rule: **every fact you write must be verified against the code in this
session.** Detection first, questions second, guesses never. If migrating from a
legacy agentic layer (old CLAUDE.md, expertise YAMLs, decisions.md), treat those
files as *candidate claims to verify*, not as truth — they are a known drift source.

## Step 1 — Detect (parallel Explore subagents for large repos)

1. **Stack & commands:** package.json scripts / Makefile / *.csproj / *.slnx /
   pyproject.toml, CI config, Dockerfile. Candidate commands for the CLAUDE.md
   table come from here.
2. **Test & lint setup:** test framework, lint/format config, coverage tooling,
   git hooks (lefthook/husky), CI quality gates → values for quality-gates.yaml.
3. **Branch policy:** CI rules per branch, server-side protections if visible,
   existing hook configs → protected-branches list.
4. **Conventions in practice:** sample 5–10 recently-touched source files. Identify
   the repeated shapes (feature structure, naming, error handling) and pick the
   best CURRENT exemplar file for each shape — prefer files touched recently by
   humans over old ones.
5. **History mining:** `git log --oneline -100` + merged MR titles. Look for
   decision-shaped events (library swaps, architecture changes, "use X not Y"
   commits) and incident-shaped events (revert/hotfix clusters) → ADR and gotcha
   candidates.
6. **Legacy docs (if present):** old CLAUDE.md / .agentic / README claims. For each
   claim: verify against code → carries over (with fresh evidence path), gets
   corrected, or gets dropped. List the drops explicitly in the final report.

## Step 2 — Verify commands

Actually run (or at minimum `--help`/dry-run) every command before it enters
CLAUDE.md. A failing command gets fixed or excluded — never written down broken.

## Step 3 — Ask only the unanswerable (AskUserQuestion)

Typically: the *why* behind detected decisions (code shows WHAT was chosen, rarely
why); which detected conventions are intentional vs. accidental; disputed or
ambiguous detections; anything for "Repo-specific notes" (external quirks,
consumers). Present decision candidates from step 1.5 as a checklist — the user
confirms which deserve ADRs and supplies rationale.

## Step 4 — Write

- Fill every `<!-- onboard:... -->` marker in CLAUDE.md AND in `.claude/skills/`
  (e.g. `execute-plan` mr-command). CLAUDE.md stays ≤120 lines; overflow becomes
  gotchas/ADRs. Update `protect-main.mjs` PROTECTED list, `lefthook.yml`, and
  `quality-gates.yaml` from detections.
- **Optional skills:** check `.agentic/optional-skills/README.md` against what was
  detected (SonarQube config → `sonar`; Jira MCP / ticket-keyed branches → `jira-implement`).
  For each match, COPY the folder into `.claude/skills/` and fill its markers.
  Skills left in the library stay inactive — do not delete them.
- If a deploy pipeline was detected in CI config, offer to generate a repo-specific
  `/deploy` skill from it (real job names, real environments — never placeholder).
- **Stack addenda — .NET:** verify Central Package Management is in place
  (`Directory.Packages.props` + `ManagePackageVersionsCentrally`); offer to migrate
  if not — the latest-stable policy needs one place where versions live. Wire
  `.agentic/ci/dotnet-package-freshness.gitlab-ci.yml` into the repo's CI includes
  and recommend a Renovate bot for ongoing updates. **Non-.NET:** delete that CI
  file and offer the stack's equivalent (e.g. `npm audit` job + lockfile policy).
- `gotchas.md`: real exemplar table (each row verified to exist) + constraint
  entries with `evidence:` path and today's `verified:` date. Delete the EXAMPLE
  entries.
- One ADR per confirmed decision, dated to when it actually happened if known
  from git history (absolute dates). Chain `supersedes:` where history shows a
  reversal.

## Step 5 — Audit and finish

- Run `node .agentic/scripts/check-doc-paths.mjs` — must pass.
- Run the `/audit-docs` procedure over everything just written (self-check: does
  each claim still hold against code you actually read this session?).
- Confirm no `onboard:` markers remain: `grep -rn "onboard:" CLAUDE.md .claude/skills/ .agentic/ | grep -v optional-skills`
  (markers inside `.agentic/optional-skills/` are filled on activation, not at onboarding).
- Report: what was detected vs. asked, dropped legacy claims (with reasons), and
  any `TODO`s left for the team.
