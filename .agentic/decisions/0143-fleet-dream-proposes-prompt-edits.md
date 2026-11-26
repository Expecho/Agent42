---
id: 0143
date: 2026-11-24
status: accepted
tags: [fleet, agents, lessons, prompts, unattended, egress, untrusted, approval, blob, ui, no-migration]
supersedes: []
extends: [0124, 0097]
amends: [0124]
amended_by: [0146]
---

# The fleet dream proposes targeted edits to the user's fleet prompts, and only the user's Approve applies them

## Context
The nightly dream (ADR 0124) learns team-design lessons and adds them to the planner's USER prompt, auto-applied. It never
read or changed the user's editable fleet prompts (`FleetPrompts.Definitions`, overrides in Blob `{userId}/prompts.json`);
0124 listed "prompt rewriting" as out-of-scope Slice 2. On 2026-11-24 the user asked the dream to also reflect on those
prompts and propose edits grounded in what recent tasks showed. A prompt is a system prompt, a higher-authority surface
than the planner's user prompt (0124's reason to keep lessons out of it), and the dream's input is untrusted.

## Decision
The dream gets a second tool, `propose_prompt_change`, and a second framed data block with the current text of the prompts
it may change. A proposal is stored as **pending** in `lessons.json` and changes nothing until the user clicks **Approve**
in the `/fleet/` Lessons view; **Dismiss** keeps it as a tombstone so it is not proposed again. Rules
(`FleetPromptProposals`, pure, tests in `FleetPromptProposalsTests`):
- **Allowlist of keys:** `planner`, `delegation`, `clarify`, `judge`, `retry`, `followup`, `moderator`. Never `dream` (it would
  rewrite its own instructions), `copilot` or `copilot-report` (guidance for the one agent kind with a shell). An allowlist,
  so a prompt added later stays out by default.
- **Targeted edits only:** `replace` (one exact passage, found exactly once) or `append`. The UI shows a before/after diff.
  On Approve the edit is **re-applied to the CURRENT prompt**, so the user's own later edits elsewhere survive; a passage
  that is gone marks it `outdated` (409). An append whose text already ends the prompt counts as applied (idempotent).
- **The approval is the control.** The checks keep that review honest: no invisible character (control except `\n`/`\t`,
  format/zero-width/bidi, private-use, surrogates, line/paragraph separators, unassigned, every space but U+0020); no non-Latin
  letter or combining mark that is not already in the prompt (lookalikes); every `{{placeholder}}` kept and none unknown added; size caps; no **gated term**
  (`FleetLessons.DeniedTerms` + `delegate_to_team`, `model:`, `approv`, `tools:`) that the replaced text does not already
  contain, and none at all in an append. The rationale follows the lesson text rules; the evidence follows the lesson
  min-support rule. At most 2 per run, one pending per prompt, 5 pending in total; pending ones expire after
  `LessonTtlDays`, decided ones are capped at 50.
- **The rules live in the tool description and the code-built input**, not only in the `dream` prompt: a user who edited
  that prompt keeps their override and never sees a newer default.
- The digest's plan lines gain a capped (150 chars) one-line excerpt of each agent's instructions, so the dream can see what
  the planner asked. `MaxTaskChars` and `DreamMaxDigestChars` are unchanged, so 0124's digest bounds hold.
- **No new switch:** part of the existing dream toggle (the user's choice), because nothing changes without Approve.
  `Fleet:DreamEnabled` stays the operator kill switch. No new `Fleet:*` option; the caps are constants.
- **Amends 0124** only on its Slice 2 line: prompt changes are now in scope, behind approval. Lessons are unchanged: still
  auto-applied, still the user prompt.

## Alternatives considered
- **A full-prompt rewrite** — hard to review, and it overwrites the user's own edits; the user chose targeted edits.
- **Auto-applying prompt edits like lessons** — a system prompt outranks the planner's user prompt (0124); approval is the
  control.
- **Including `dream`, `copilot` and `copilot-report`** — self-modification, and steering the shell agent kind.
- **A separate toggle** — nothing changes without approval, so the dream toggle is consent enough (the user's choice).
- **A content denylist on the whole replacement** — the shipped planner prompt itself names `copilot`; the relative
  gated-term rule is used instead.
- **Excluding only the three prompts (a denylist of keys)** — a prompt added later would be proposable by default.

## Consequences
- **Egress (ADR 0013 extension, same destination as 0124):** the user's own fleet prompts and capped agent-instruction
  excerpts now also go to the user's chosen dream model, the same per-user provider set as ADR 0124. No new destination.
- **Residual:** a human can approve a subtle steer that passes every check. The gated-term rule narrows what a diff can
  add; it does not judge meaning.
- **Approving forks the shipped default:** `FleetPrompts.WithOverride` stores the WHOLE prompt, so a user who never edited
  it stops receiving later changes to its default. The card says so; Reset in the prompt editor restores the default.
- **Locking:** approve holds the per-user lessons lock across reading and writing both `lessons.json` and `prompts.json`
  (the one exception to "short read-modify-writes only"), so a double click gets a 409 and the prompt changes once.
  `prompts.json` itself has no lock (`FleetTaskService.SetPromptAsync` is an unlocked read-modify-write): a prompt-editor
  save in another tab at the same moment can still race. Accepted, as 0124 accepted the no-ETag rollover loss.
- The badge counts pending proposals created after `SeenAt`; the waiting count is in the block's heading.
- Blob JSON with trailing defaulted fields (`PromptProposals`, `FleetDreamRun.Proposed`): an older document reads, no
  migration. `/fleet/` is outside `SHELL`: no `sw.js` bump.
- Follow-ups (out of scope): a one-click revert that stores the before-text; measuring whether an approved change improved
  scores; a lock or ETag on `prompts.json`; user-settable caps through ADR 0135's descriptors.
