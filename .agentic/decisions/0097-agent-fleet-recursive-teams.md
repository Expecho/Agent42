---
id: 0097
date: 2026-10-12
status: accepted
tags: [fleet, agents, agent-framework, workflows, declarative, github-copilot-sdk, blob, sse, egress, trust-boundary, single-replica, usage, prerelease-package, no-migration]
supersedes: []
amends: [0085, 0013]
amended_by: [0099, 0101, 0107, 0108, 0115, 0116, 0117, 0118, 0119, 0120]   # 0099: a task can override the default tool selection (decision 6); 0101: attached files + workspace document tools (decision 7); 0107: copilot chats get built-in tools too, and the managed-identity residual (decision 7); 0108: Copilot agents may run on the user's GitHub Copilot models (copilot/<id>), never the task model; 0115: a chat-started task approves every question automatically and declines elicitations (decision 7); 0116: a Details | Graph view of the task; 0117: a team-scoped todo list (todo_add/todo_complete/todo_remove/todo_list, decision 6 tools); 0118: an optional clarify phase interviews the user about the prompt before the planner runs; 0119: an optional per-task self-review loop on the agent(s) that write a team's result
---

# Agent42 agent fleet: an in-process recursive team runner outside Orleans, persisted in Blob, with Copilot agents that keep their built-in tools

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** Fleet Copilot agents have **no kill switch**: `Copilot:BuiltInToolsEnabled` governs
> chats only, and `FleetCopilotHost` is hard-coded to `CopilotCli` mode. With sign-in open, every signed-in user can start one
> (user decision, accepted risk).

> **Amended by [ADR 0108](0108-github-copilot-provider.md) (2026-10-20):** a Copilot agent may run on the user's GitHub
> Copilot models as `copilot/github-copilot:<model>`, signed in with the user's GitHub token per session. Those models
> are never the task's model and never get a chat client (planner, judge, manager and ordinary agents stay on BYOK).

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
6. **Tools**: the workspace tools, the team's todo list (ADR 0117), `delegate_to_team`, `web_search`/`fetch_web_page` (truncated), and the user's MCP
   tools gated by their DEFAULT tool selection (ADR 0021; since ADR 0099 the task's own override, else the default), with the ADR 0081 reauth and ADR 0095 elicitation
   wrappers. An elicitation becomes a fleet question.
7. **Copilot fleet agents keep Copilot's built-in tools (shell, file writes, URL fetch) in the backend container.**
   **This amends ADR 0085's phase-1 trust boundary for fleet tasks** (user decision, 2026-10-12; since
   [ADR 0107](0107-copilot-chat-builtin-tools.md) copilot-kind chats have the built-ins too, auto-approved). It uses a
   SEPARATE `CopilotClient` in default mode (`CopilotClientMode.CopilotCli`), with no `SessionFs` (fleet sessions are
   one-shot), and `SessionConfig.WorkingDirectory` = the task's local workspace directory. The mitigations from 0085
   stay: `MinimalEnvironment()` (no managed identity, Cosmos or storage variables reach the runtime), reads and writes
   outside the working directory are refused, and every write, shell command and URL fetch is asked to the user, with
   "always for this task". Agent42's own functions (`PermissionRequestCustomTool`) are approved without asking. A kill
   switch, `Fleet:CopilotAgentsEnabled`, removes Copilot agents and the planner's Copilot section.
   **Residual risks, accepted:**
   - a shell can reach the app port on `localhost` and forge `X-MS-CLIENT-PRINCIPAL-ID` (`src/Agent42.Backend/Api/CurrentUser.cs`);
   - the container filesystem is readable by a shell;
   - the network reach is the container's;
   - **added by ADR 0107**: the runtime runs as the backend's own user, so an approved shell can read
     `/proc/<backend pid>/environ` (`IDENTITY_ENDPOINT`, `IDENTITY_HEADER`) and request managed-identity tokens,
     which reach all app data in Cosmos and Blob. `MinimalEnvironment()` cannot stop that.
   **Since ADR 0101** a task can carry user-uploaded files under `inputs/` (with `.txt` copies of PDF and Office
   documents) in the workspace, so a Copilot fleet agent's built-in shell and file tools now read user-uploaded,
   possibly third-party, content too.
   **Since 2026-10-19** the runtime image ships Python 3 (Ubuntu apt `python3`) and Node.js with npm (copied from the
   official `node:24` LTS image, because Ubuntu 24.04's apt `nodejs` is the end-of-life 18.x), so the shell can run
   code in more than bash and Perl. Nothing can be installed at runtime, because the app runs as `$APP_UID`: what the
   shell can run is what `src/Agent42.Backend/Dockerfile` bakes in (`BackendImageRuntimeTests`). This widens the
   residuals above (a script can do more than a shell one-liner) but adds no new gate or reach. The image grew from
   982 MB to 1.23 GB (measured locally).
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
