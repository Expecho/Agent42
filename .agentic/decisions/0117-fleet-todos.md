---
id: 0117
date: 2026-11-08
status: accepted
tags: [fleet, agents, tools, todos, frontend, no-migration]
supersedes: []
extends: [0097, 0102, 0082]
amends: [0097, 0116]
---

# Fleet agents share a todo list per team attempt

## Context
Fleet agents (ADR 0097) plan nothing visible: someone watching `/fleet/` sees text and tool calls only. The research
chat agent (ADR 0082) already shows a live checklist through Agent Framework's `TodoProvider`. The user asked for the
same in the fleet ("like in the harness agent").

## Decision
Four plain `AIFunction`s, `todo_add`, `todo_complete`, `todo_remove`, `todo_list` (class `FleetTodos`, shaped like
`FleetCanvas`, ADR 0102), that agents opt into by YAML tool name.

- **Scope: one list per team attempt, shared by that team's agents.** A sub-team has its own (its own `TeamId`), a
  fix or retry attempt starts empty. `FleetTeamRunner.TodosFor(context)` memoizes it per `(TeamId, Attempt)`,
  because `AgentFunctions(context)` runs twice per attempt (the planner's tool listing and the agent factory).
- **Plain functions, not `TodoProvider`/`HarnessAgent`.** The provider keeps its state in an `AgentSession` and
  offers tools only through one; fleet agents are built from YAML by name (`ChatClientPromptAgentFactory`) or by
  Copilot's `AsAIAgent` and have no hook for a provider. ADR 0082 already rejected `HarnessAgent` as a preset that
  removes control.
- **Persistence is the event log.** Each change publishes `todos.updated {teamId, attempt, todos:[{id,title,done}]}`
  carrying the WHOLE list, so a replay or reload replaces the state. Only text events are coalesced or dropped at the
  cap (ADR 0097), so these are kept. No blob, no Cosmos field, nothing persisted past the attempt.
- **Caps refuse, never truncate** (the ADR 0102 convention): at most 50 items, 200 characters a title. Ids are
  monotonic and never reused. All calls take arrays. Complete is idempotent; an unknown id refuses the whole call.
- **Concurrency:** agents of a team run concurrently, so each call mutates and publishes under one lock; the event
  order is the state order.
- **Copilot agents** get them as custom tools beside the canvas tools, with `AvailableTools` still unset (ADR 0102
  decision 7). The Copilot factory is built once per task, so `FleetCopilotAgentFactory.ForAttempt(todos)` binds one
  per attempt, and `CreateAgentFactory` uses it.
- **Judge:** the team's final list (open items first) is appended to the judge input, so an unfinished checklist
  counts against delivery.
- **UI:** a read-only checklist with a done/total counter in the attempt's Details, and `done/total` on the team box
  in the graph (ADR 0116). Titles are `textContent`. `/fleet/` is outside the service worker SHELL: no `CACHE` bump.
- The four names are reserved in `FleetToolCatalog.Usable`, so an MCP tool cannot take one.

## Alternatives considered
- **A list per agent:** needs a per-call agent identity that is not proven inside a workflow executor (ADR 0102
  decision 5), and a shared list is the more useful checklist for a team.
- **`TodoProvider` / `HarnessAgent`:** see above.
- **Persisting the list or carrying it into a follow-up:** it is a working checklist, not a deliverable (the canvas is).
- **A loop that re-runs an agent until its todos are done** (`TodoCompletionLoopEvaluator`): the judge/fix cycle owns
  "is the work complete".
- **User editing:** read-only, like the canvas.

## Consequences
A team whose planner does not hand agents the todo functions shows no checklist: the planner prompt says who should
get them, and each tool description carries its own rules, because a user's prompt override replaces the prompt text.
Each event carries the whole list (up to about 10 KB) and is not subject to the text byte cap, so a call that changes
nothing (completing an already-done item) publishes nothing. The event log is otherwise bounded only by the number of
real changes. The DevHost canned client cannot script tool calls, so the checklist and graph badge were not seen live.
