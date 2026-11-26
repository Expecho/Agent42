---
id: 0146
date: 2026-11-26
status: accepted
tags: [fleet, agents, lessons, skills, unattended, egress, untrusted, approval, blob, ui, no-migration, shell]
supersedes: []
extends: [0124]
amends: [0143, 0049]
---

# The fleet dream proposes edits to, and creation of, the user's uploaded skills, and only the user's Approve applies them

## Context
The nightly dream (ADR 0124) auto-applies team-design lessons and, since ADR 0143, proposes targeted prompt edits that only
Approve applies. A procedural, reusable lesson ("when doing X in this codebase, do Y") belongs in a skill (ADR 0049), not as
prose in a planner prompt. Skill text reaches a model through `load_skill` and, natively, in Copilot chats and fleet agents
(ADR 0085), where the agent has a shell.

## Decision
A third dream tool, `propose_skill_change`, proposes (1) a `replace`/`append` edit of the BODY of an installed uploaded skill, or
(2) a new skill. Both are stored pending in `lessons.json` (`SkillProposals`, trailing and defaulted, no migration) and only
Approve changes anything; Dismiss keeps a tombstone. Rules:
- Targets are uploaded skills only. A shipped or companion name is never an edit target (an upload shadowing one is refused too),
  and a created skill may not take any shipped name or an installed one.
- Frontmatter is never the model's: a create supplies name, a one-line description (plain-text whitelist, so no YAML injection)
  and a body, and code builds `SKILL.md`; an edit applies to the body only. The whole `SKILL.md` stays within `MaxSkillBodyLength`.
- Evidence from at least 3 workspaces (`FleetSkillProposals.MinSupport`, a constant, not a `Fleet:*` option).
- Caps: 2 per run, one pending per skill, 5 pending, 50 decided. Rules live in the tool description, not the `dream` prompt.
- Approve re-applies an edit to the CURRENT `SKILL.md` (outdated when the passage is gone; append idempotent) and installs through
  `UserSkillInstaller`, the one install path the upload endpoint also uses. A create auto-enables (ADR 0049 rule); an edit never
  re-ticks a skill the user switched off. The installer is an optional trailing parameter of `FleetLessonService`; without it the
  dream offers no skill tool and Approve answers 409.
- `FleetDreamer.MaxIterations` goes from 4 to 6: three tools, one refusal and a final answer need five round trips.

## Alternatives considered
- **Skill text as prose in the planner prompt (ADR 0143 only)** — a procedure is reused selectively and loaded on demand; a prompt
  paragraph is paid for on every run.
- **Gated terms for skills, like prompts** — the user decided the dream may write skills covering shell-capable tools; the Approve
  diff is the control. 0143's `GatedTerms` stay prompt-only.
- **A user-settable support threshold** — would need ADR 0135 descriptors and `FleetUserSettings`; a constant is enough.
- **Letting the model write the frontmatter** — the description is an injection point into YAML.

## Consequences
- Residual risk, accepted: an approved skill's text may advise using shell or other tools, in a Copilot agent that has a shell. A skill
  does not GRANT any tool (picker keys do, ADR 0021/0048); only the review of the diff guards the text. The card says so.
- Egress (ADR 0013 extension, same destination as 0124/0143): the user's own skill bodies now also go to their dream model.
- An approved create is on for the default selection at once; conversations and agents with a materialised override need a manual tick.
- Approve holds the per-user lessons lock across the install; a concurrent upload of the same skill can still race (as for `prompts.json`).
- No revert or effectiveness measurement; same follow-ups as 0143.
