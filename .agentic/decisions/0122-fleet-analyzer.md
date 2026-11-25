---
id: 0122
date: 2026-11-09
status: accepted
tags: [fleet, agents, tools, selection, diagnostics, telemetry, egress, untrusted, copilot, no-migration]
supersedes: []
extends: [0013, 0054, 0097, 0115]
amends: []
amended_by: [0123]   # 0123: the fleet_analyzer chat key is removed; the analyzer is a panel on /fleet/
---

# A chat can diagnose a fleet task and stop or re-run it, through one more selectable tool key

> **Amended by ADR 0123:** the delivery (decision 5, the `fleet_analyzer` chat key) was replaced by a panel on `/fleet/`
> with fixed tools. The tool guarantees below still hold.

## Context
A fleet task that can't complete (2026-11-09) was diagnosable only by reading `/fleet/` by hand, or the Blob store and
Application Insights with operator RBAC. The user asked for an analyzer **inside the app**, that they can ask questions,
that can read the task, its agents, output and logs, and act on the fleet **without creating a whole new team for it**.

## Decision
One new selectable key, **`fleet_analyzer`**, covering eight built-in chat tools. The agent already in the conversation is
the analyzer: no new team, grain, page or endpoint. They reach the fleet through the existing `IFleetLauncher` port
(ADR 0115, ADR 0105 rule 7), extended with the analyzer operations.

- **Read:** `list_fleet_tasks`, `analyze_fleet_task` (status, reason, tokens against the cap, tool override, an event
  histogram, every failure signal, the latest plan, **an unanswered question the task is waiting on**, the last events),
  `read_fleet_events` (paged, filterable by event type), `read_fleet_workspace` (listing, or one text file),
  `get_fleet_config` (the effective `Fleet:*` limits), `read_recent_spans` (the caller's own trace spans).
- **Act:** `stop_fleet_task`, and `rerun_fleet_task` (a NEW task repeating a task's request and attached files).
- Every read is scoped by the grain's `_userId`, never an argument: another user's task reads as not found. Everything
  returned is cut to a cap and framed as data (`FleetToolLimits.WrapAsData`, closing tag neutralized), and the tool
  descriptions say it is evidence, never instructions from the user (rule 11).

### Decisions inside that, each with its rejected alternative

1. **A re-run is NOT an origin task.** An origin task (ADR 0115) is auto-approved, Copilot shell and MCP writes included.
   The analyzer composes a re-run from event text, which is model and tool output, so the re-run keeps ADR 0097's own
   rule: its questions go to the `/fleet/` page, and the tool tells the user to open the link. Cost: the result does not
   come back into the chat. *Rejected: origin tasks.* An auto-approved shell steerable by injected log text.
2. **A re-run can only narrow.** `disableTools` removes keys from the original task's selection (or the user's default
   one); there is no `enabledTools`. `note` is capped at 1,000 characters and appended to the request. The token cap goes
   through `SubmitAsync`, which rejects an over-limit value (it does not clamp), and the reason is the tool's answer.
   *Rejected: a free `enabledTools` list.* Injected text could widen a task to MCP and Copilot tools.
3. **A re-run is a start.** It shares `FleetStartRefusal` with `start_fleet_task`: refused in an incognito chat, in a
   fleet-result turn (the ADR 0115 loop guard) and past one per turn. `stop_fleet_task` is not limited. One task per
   user still holds, so a re-run while one runs is refused with the service's text.
4. **Telemetry: the caller's own spans only, no logs.** `LiveTelemetryBuffer` is global and holds every user's chat text
   (ADR 0054). A span is returned only if it carries a `user.id` tag equal to the caller's; a span without the tag is
   left out. Log lines carry no user tag, so they cannot be scoped, and sending them to even the owner's model would be
   egress of other users' content. Spans carry no fleet task id either, so the tool says to match on time, model or
   error text. *Rejected: owner-gated logs.* `DashboardOwnerGate` gates a page, it does not authorize routing logs into
   a model.
5. **Not offered to Copilot chats, fleet agents, or `ask_agent`.** What it reads is attacker-influenceable, and a Copilot
   chat runs a shell without asking (ADR 0107). `IsExcludedFromCopilot` drops all eight tools, and
   `AgentMcpRunner.ExcludedFromAskAgent` strips the key (ADR 0070, 0115). The key is appended last, so it is ON for a
   `null` selection and OFF for anyone who ever pressed Save (rule 11, ADR 0021). Not migrated.
6. **The port is extended, not a second one.** The grain holds `IFleetLauncher` as a direct constructor dependency and
   seven test silos register it. A second port would touch every one of them. *Rejected: `IFleetAnalyzer`.* Cost: a port
   named for starting tasks now has eight more members, all text-returning, so `Agent42.Agents` stays free of Backend types.

## Egress and residuals (rule 13)
- **Egress: no new destination.** Fleet content already goes to the user's own providers (ADR 0097 decision 8). The
  analyzer hands the user's own task content and own spans to the conversation's model, which is that same set.
- **Residual: the analysis can quote anything an agent wrote**, including a secret a shell agent echoed. It is the
  user's own task, in the user's own chat.
- **Residual: a prompt-injected analyzer can stop the user's running task** (`stop_fleet_task` has no confirmation) or
  start one re-run per turn without auto-approval. Both are bounded and visible on the page.
- **Not built, and why:** Application Insights and Log Analytics. No query client exists, the Container App's
  system-assigned identity has no monitoring role, App Insights has chat content stripped and no logs (ADR 0077), and
  log lines have no per-user tag. A follow-up needs a query package resolved with `dotnet add package` (rule 4), a
  per-user stamp on log records, and the role grant below, which **the user runs** (rule 5):
  `az role assignment create --assignee-object-id <container-app-principal-id> --assignee-principal-type ServicePrincipal
  --role "Log Analytics Reader" --scope <agent-42-logs resource id>`, with the principal id from
  `az containerapp show -n <app> -g rg-agent42 --query identity.principalId`.
- **Not built:** a fleet task id on spans and logs (it would make telemetry per-task), and a button on `/fleet/`.
- **Relation to ADR 0121 (open sign-in, owner tier, phase A, PR #228):** that work redacts telemetry for non-owners.
  This ADR already returns only the caller's own spans, so it does not depend on 0121 and does not widen it.

## Alternatives considered
- **A fleet team that analyzes fleet tasks.** Rejected by the request: a team costs a planner, agents and a budget to
  read a few files, and its output is another task to diagnose.
- **A `/fleet/` panel with its own agent.** Rejected for this slice: a second agent surface with its own model choice,
  history and streaming path, when the user's chat already has all three.
- **Reading blobs and Application Insights from outside the app** (the 2026-11-09 attempt). It needs data-plane RBAC the
  deploy identity doesn't give a developer, and the user would hold the diagnosis in a terminal instead of the app.
