---
id: 0118
date: 2026-11-08
status: accepted
tags: [fleet, agents, clarify, questions, frontend, no-migration]
supersedes: []
extends: [0097]
amends: [0097]
---

# A fleet task can interview the user about its prompt before it plans

## Context
A fleet task (ADR 0097) plans the moment the user presses Run, so a vague prompt becomes a vague team and a spent token
budget. The user asked that the agent "grill me on my task prompt until all requirements are clear" before the task runs.

## Decision
A **clarify phase** runs first for a page-started task that asked for it (`FleetTaskRecord.Clarify`, set by the
composer's "Ask me questions first" checkbox and the `clarify` field of `POST /api/fleet/tasks[/with-files]`).

- **An interviewer agent on the task's model** (`FleetClarifier`, shaped like the planner's `PlanSubmission`) has two
  tools: `ask_user(question, choices?, freeTextHint?)` and `requirements_clear(brief)`. The result arrives through tool
  calls, so a provider that ignores structured output still works. The system prompt is the editable `clarify`
  prompt (one placeholder, `maxQuestions`); the request, the attached-files list and a follow-up's earlier request go
  in the user message, so a user's prompt edit cannot lose them.
- **It reuses the question slot** (ADR 0097): each question is a `prompt` event answered through
  `POST .../answer`, one at a time, replayed from the log after a reload, cancelled by a stop. The page labels the banner
  "Before it starts, the fleet asks" between the new `clarify.started` and `clarify.done` events.
- **It cannot grill forever.** `Fleet:MaxClarifyingQuestions` (default 8, below 1 counts as 1); every question carries a
  fixed "Enough, start the task" choice; past the cap or after that choice `ask_user` tells the model to call
  `requirements_clear`. A brief over 6,000 characters is refused, not truncated (the ADR 0102 convention).
- **It fails open.** A model that never clears, or errors, leaves the user's request as it was (`clarify.done` says
  `cleared: false`). A stop is never swallowed: the clarifier runs on `task.Budget.Token`, which `RunAsync` already
  maps to `Stopped`.
- **The brief replaces the request as the planner's input only** (`ComposeInputAsync`, first run and the follow-up
  prompt alike). `FleetTaskRecord.Request` stays the user's words; the brief lives in the `clarify.done` event, and the
  page shows it under "Requirements". Run again re-asks (it repeats the record's flag), because the brief is not stored.
- **A task started from a chat never clarifies** (ADR 0115): `SubmitAsync` stores `Clarify = false` for it, and the
  run skips the phase for `AutoApprove` as well (defence in depth; the submit is the authority).
- **Cost and egress:** the interviewer's tokens count against the task's budget (it runs on the wrapped
  `DefaultClient`); the same model, provider and content as the planner, so no new egress (ADR 0013 unchanged).
- **Default:** on in the composer (remembered in `localStorage`), off for API callers that omit the field.

## Alternatives considered
- **A chat before submit on the page:** a second conversation surface and a draft state outside the task; the question
  slot already gives replay, stop and per-task isolation.
- **The planner asks through a tool:** it would mix interviewing with designing a team, spend a planner turn per question,
  and ask again for every sub-team and retry.
- **Always on:** a clear prompt would pay for a round trip, and chat-started and API tasks have nobody to answer.
- **Giving the interviewer tools (search, documents):** it would answer its own questions from the web; out of scope.

## Consequences
A page task with the box checked sits as `running` with an open question until the user answers or stops it, like a
plan approval. The interviewer's quality is a prompt matter (the `clarify` prompt is editable per user). The DevHost
canned client never calls the tools, so there the phase fails open and the interview itself was not seen live;
`FleetClarifierTests` and `FleetTaskServiceTests` script it.
