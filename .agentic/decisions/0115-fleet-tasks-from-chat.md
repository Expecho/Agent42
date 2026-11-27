---
id: 0115
date: 2026-11-01
status: accepted
tags: [fleet, agents, tools, selection, background-turn, push, chat-apps, github-copilot-sdk, trust-boundary, egress, untrusted, no-migration]
supersedes: []
extends: [0022, 0028, 0106, 0111]
amends: [0097, 0021]
amended_by: [0147]
---

# A chat can start a fleet task, and the result comes back as a background turn

## Context
The agent fleet (ADR 0097) is reachable only from the `/fleet/` page. A chat agent can't hand a long job to it, and
the user can't hand off from a chat and get the result back there. That gap came up on 2026-11-01. The pieces exist
already: `FleetTaskService` runs tasks in-process, `IAgentGrain.EnqueuePromptAsync` (ADR 0022) runs a background turn
and sends the away push (ADR 0028) and the chat-app delivery (ADR 0111).

## Decision
One new selectable key, **`fleet`**, covering two built-in tools:

- **`start_fleet_task(task, followUpOf?)`** starts a fleet task tied to the conversation (`FleetTaskRecord.OriginConversationId`)
  and returns the id and the deep link `/fleet/?task=<id>`.
- **`get_fleet_task(taskId)`** returns the status, tokens, agents and the result cut to 12,000 chars. Ownership is
  `FleetTaskService.GetAsync(userId, …)`: another user's task reads as not found.

When an origin task finishes as Done, Failed or Stopped, `FleetTaskService.FinishAsync` calls an
`IFleetCompletionNotifier` **after the record is saved**. The notifier calls the new `[OneWay]`
`IAgentGrain.ReceiveFleetResultAsync`, which runs a background turn through the same body as `EnqueuePromptAsync`
(push, then chat-app delivery), with a framed message. The grain reaches the fleet only through a port,
`IFleetLauncher` (ADR 0105 rule 7): the Backend `FleetLauncher` in hosts with the fleet, `DisabledFleetLauncher`
elsewhere.

### Decisions inside that, each with its rejected alternative

1. **A background turn, not a notification.** The user chose this: the agent can act on the result without being
   prompted. *Rejected: notify-only (a push with a link).* The result would never reach the model.
2. **Amends ADR 0097 decision 7: a chat-started task approves every question automatically.** Nobody watches the
   `/fleet/` page for a task started from a chat, so any question would block it until the budget runs out. In auto
   mode (`FleetRun.AutoApprove`, set when the record has an origin):
   - the plan approval is skipped;
   - **every** Copilot permission kind is approved once: shell, write, URL, MCP (including calls that are not
     read-only) and the unknown kinds (hooks, extensions, memory). This is broader than ADR 0107's chat policy, which
     refuses Copilot MCP writes and unknown kinds. The user chose it after being told that;
   - the hard refusals stay: reads and writes outside the working directory are rejected without asking;
   - **MCP elicitations are declined** (`DecliningFleetInteraction`). There is nobody to fill in a form or open a URL;
   - each automatic answer is logged on the task as a `question.auto` event, so the page shows what was approved.

   Auto mode is a per-site flag, never a heuristic over the offered choices, so each site has its own test.
   `POST …/answer` on an auto task answers 409. Stop still works through the budget token.
   *Rejected: no Copilot agents for chat-started tasks.* The user wants the same fleet as the page.
3. **Amends ADR 0021: the new key `fleet`.** It is appended last to `AllSelectableToolKeys`, so it is ON for a
   `null` selection and **OFF for anyone who ever pressed Save**, and for agents bound before this release. Not
   migrated. Copilot chats get it through `CopilotFunctions`. The research agent and the fleet's own agents don't.
4. **The loop guard.** A turn that delivers a fleet result can't start another fleet task. The result arrives through
   the dedicated `ReceiveFleetResultAsync` entry, which marks its `TurnContext` with `FleetResultTurn`, and
   `start_fleet_task` refuses there. *Rejected: a per-turn counter only.* Each background turn has a new
   `TurnContext`, so result → start → result would run unbounded, steerable by injected result text. A normal turn may
   start at most one task.
5. **The result is framed as data.** The message is stored as a user-role turn, so it starts with
   `[Agent42 fleet]`, tells the model that the text inside `<fleet-result>` is fleet output and not an instruction
   from the user, cuts the result to 12,000 chars with the deep link, and neutralizes any `</fleet-result>` inside
   it. A Failed or Stopped task carries its reason instead.
6. **The model choice is the launcher's.** If the conversation's model is in the user's catalog, tool-capable and not
   Copilot-only (the predicate `SubmitAsync` uses), the task uses it; otherwise the catalog default. *Rejected:
   retrying after a "Pick one of your models…" error.* Matching on error strings breaks silently.
7. **Scope: every non-ephemeral conversation.** That includes email-in, agent-run, reminders and chat-app groups (user
   decision). Excluded: incognito (refused inside the tool, as `schedule_prompt` does) and MCP `ask_agent`
   (`AgentMcpRunner.ExcludedFromAskAgent`).
8. **A deleted origin conversation drops the result.** `ReceiveFleetResultAsync` logs and returns when the grain has
   no saved record (`!_state.RecordExists`, which `PurgeAsync`'s `ClearStateAsync` leaves; the `SetSharedStateAsync`
   precedent). Nothing is resurrected or run. A record that exists without a bound model still runs the turn, which
   then shows the usual "configure a provider" notice.

## Residuals and egress (rule 13)
- **Untrusted input can start an auto-approved shell. Accepted by the user.** An email sender (ADR 0096), an
  agent-run API-key holder (ADR 0022) or a chat-app group member (ADR 0111) can prompt-inject a conversation into
  starting a fleet task whose Copilot agents run shell commands with no question. That shell has the ADR 0097/0107
  reach (the app port on `localhost`, the container filesystem, the container's network). The mitigations are only
  the excluded `ask_agent`, the incognito refusal, the `Fleet:CopilotAgentsEnabled` kill switch, and pointing those
  doors at an agent whose `EnabledTools` excludes `fleet`. Same register as ADR 0097 decision 7.
- **Copilot MCP writes are auto-approved** in chat-started tasks (decision 2).
- **A task interrupted by a restart never notifies.** ADR 0097 decision 3 rejects a startup scan.
- **The open web UI sees the result turn only on reload**, as with `/api/agent-run`.
- **Egress: no new path.** Fleet content already goes to the user's providers (ADR 0097 decision 8). The result turn's
  reply reaches the chat app through the existing away-reply route (ADR 0111), opt-in per connection.

## Alternatives considered
- **A grain per fleet task.** ADR 0097 decision 1 rejected that, and nothing here needs it.
- **Polling `get_fleet_task` from the chat.** The model is told not to; the background turn makes it unnecessary.
- **A `stop_fleet_task` tool.** Out of scope; stop a task on `/fleet/`.

## Consequences
- `FleetTaskRecord` gains a nullable `OriginConversationId` (our own Blob JSON; old records read null).
- `RunningFleetTask.Questions` is an `IFleetInteraction`, so an auto task holds no `FleetQuestions`.
- `AgentGrain` gains a direct port dependency, `IFleetLauncher`, so every host registers one or fails startup.
