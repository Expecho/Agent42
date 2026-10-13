---
id: 0097
date: 2026-10-12
status: accepted
tags: [fleet, agents, agent-framework, workflows, declarative, github-copilot-sdk, blob, sse, egress, trust-boundary, single-replica, usage, prerelease-package, no-migration]
supersedes: []
amends: [0085, 0013]
---

# Agent42 agent fleet: an in-process recursive team runner outside Orleans, persisted in Blob, with Copilot agents that keep their built-in tools

## Context

The user built an "Agent Teams" engine as a file-based app (outside this repo): a planner writes a team of declarative
YAML agents, which run as an Agent Framework workflow (Sequential, Concurrent, GroupChat, Handoff, Magentic or
Staged). Agents recurse into sub-teams, and a judge scores the result and triggers a fix or a new team. On
2026-10-12 they asked for it inside Agent42 as its own page, `/fleet/`, reached from the sidebar ("Agent42 agent
fleet"), with persisted tasks and follow-ups, using only the user's own models (ADR 0018/0019). Spike results are in
`.agentic/specs/completed/plan-agent-fleet.md`.

## Decision

1. **Not a grain.** `FleetTaskService` is an in-process singleton that runs each task on a background `Task`, tied
   to `ApplicationStopping`. The registry of running tasks, open questions and live subscribers is in-process, which
   relies on the single replica (CLAUDE.md rule 12). One running task per user, plus a global `Fleet:MaxConcurrentTasks`.
2. **Blob only.** One private container, `agent-fleet` (a fixed literal in `storage.bicep` and `AzureFleetBlobs`,
   like `copilot-sessions`), holds `{escaped userId}/tasks/{taskId}.json`, `/events/{taskId}.ndjson` (append blob),
   `/workspaces/{workspaceId}/…` and `/prompts.json`. All logic is in `FleetStore` over a narrow `IFleetBlobs` seam,
   with an in-memory twin for DevHost and tests. The account-scoped data role already covers the new container. The
   log coalesces streamed text (append blobs allow 50,000 blocks) and is capped by `Fleet:MaxEventLogBytes`.
3. **SSE for the page.** `GET /api/fleet/tasks/{id}/events` replays the persisted log, then streams live, with
   exactly-once ordering by a per-task sequence number and a 15 s keep-alive comment. A task stored as `running` that
   is not in the registry is rewritten to `interrupted` when it is read. There is no startup scan.
4. **The user's own models.** Chat clients come from `IUserChatClientFactoryProvider`, and the planner chooses from
   the user's tool-capable catalog ids. Each task's client is wrapped in a per-task token budget that never changes the
   shared client. The engine's Anthropic-only parts are dropped. Hosted YAML tool kinds are refused at load ("use the
   `web_search` function").
5. **Usage attribution.** The runner sets `UserTelemetryContext.RequestContextKey` in Orleans `RequestContext` for
   the task's user at the start of its background `Task`, and the application dimension to `"Agent42 fleet"`. It never
   relies on the POST's flowed ExecutionContext.
6. **Tools**: the workspace tools, `delegate_to_team`, `web_search`/`fetch_web_page` (truncated), and the user's MCP
   tools gated by their DEFAULT tool selection (ADR 0021), with the ADR 0081 reauth and ADR 0095 elicitation
   wrappers. An elicitation becomes a fleet question.
7. **Copilot fleet agents keep Copilot's built-in tools (shell, file writes, URL fetch) in the backend container.**
   **This amends ADR 0085's phase-1 trust boundary for fleet tasks only** (user decision, 2026-10-12). It uses a
   SEPARATE `CopilotClient` in default mode (`CopilotClientMode.CopilotCli`), with no `SessionFs` (fleet sessions are
   one-shot), and `SessionConfig.WorkingDirectory` = the task's local workspace directory. The mitigations from 0085
   stay: `MinimalEnvironment()` (no managed identity, Cosmos or storage variables reach the runtime), reads and writes
   outside the working directory are refused, and every write, shell command and URL fetch is asked to the user, with
   "always for this task". Agent42's own functions (`PermissionRequestCustomTool`) are approved without asking. A kill
   switch, `Fleet:CopilotAgentsEnabled`, removes Copilot agents and the planner's Copilot section.
   **Residual risks, accepted:**
   - a shell can reach the app port on `localhost` and forge `X-MS-CLIENT-PRINCIPAL-ID` (`src/Agent42.Backend/Api/CurrentUser.cs`);
   - the container filesystem is readable by a shell;
   - the network reach is the container's.
8. **Egress (an ADR 0013 extension).** Fleet content goes to the user's own providers, SearXNG, context.dev and the
   user's MCP servers, which are all existing paths. **New:** a Copilot fleet agent's built-in URL fetch and shell can
   reach any host from the container, gated by the user's per-action approval and the kill switch.
9. **Workspace downloads are an XSS sink.** They are served with `Content-Disposition: attachment`,
   `X-Content-Type-Options: nosniff`, and `text/plain` or `application/octet-stream`, never `text/html` or
   `image/svg+xml` (the ADR 0084 precedent).
10. **`Microsoft.Agents.AI.Declarative` is pinned at `1.23.0-rc1`**, a recorded exception to hard rule 4: the package
    has never had a stable release, and it ships in lockstep with the stable AF family (`Workflows` 1.23.0).

## Alternatives considered

- **A grain-hosted runner** (a grain per task). The user rejected it. A task runs for minutes with many concurrent
  sub-teams and long-lived SSE subscribers, which fits the single-threaded grain model and the 2 MB state limit
  poorly.
- **A Cosmos index of tasks.** Rejected in favour of Blob only: the store is per user and listed by prefix, and a new
  Cosmos container needs Bicep and a migration-free schema for no query benefit.
- **NDJSON polling** (the chat's stream shape). Rejected: the engine's whole UI is event replay, and SSE with a
  replay-then-live subscription gives the reload-anywhere behaviour directly.
- **Copilot in `Mode.Empty`** (ADR 0085's phase 1, with no built-in tools). The user rejected it: the fleet's Copilot
  agents exist to write files and run commands.
- **Our own YAML loader instead of the prerelease Declarative package.** Rejected by the user: it means more code to
  own, while the rc tracks the AF family.

## Consequences

- Recursive agent teams use the user's own models, survive a reload, and their history survives a restart. A task
  that is running at a restart becomes `interrupted` and can be run again, but it cannot be resumed.
- Copilot fleet agents can damage the container, read its files and forge a principal header on localhost. The
  approval prompt and the kill switch are the only gates. Sandboxing them (an ADR 0085 phase-2 style remote
  runtime) is the follow-up.
- Rule 12 (single replica) gains another load-bearing in-process registry.
- Fleet tokens appear in the usage dashboard under the user and the application `"Agent42 fleet"`.
- A prerelease package is in the dependency graph. When Declarative goes stable, move it to stable.
