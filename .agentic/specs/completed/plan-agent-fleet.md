planned-against: main @ afaaba3 (worktree `.claude/worktrees/agent-fleet`, branch `feature/agent-fleet`)

# Plan: "Agent42 agent fleet": recursive agent teams as a separate page

## Overview

Port the "Agent Teams" engine (a planner designs a team of declarative YAML agents, which runs as an Agent
Framework workflow, recurses into sub-teams, and is reviewed by a judge that triggers a fix or a new team) into
Agent42. It gets its own page, `/fleet/`, styled like Agent42 and reached from a sidebar link labelled
"Agent42 agent fleet". The engine does NOT run in a grain. It is an in-process runner service. Tasks, results,
plans, verdicts, the live event log and workspace files are persisted in Blob, so the page survives a reload,
and past tasks (including follow-ups) survive a restart or deploy.

Source (outside the repo, on this machine; read it, don't copy it blindly):
- `Z:\Sources\Dotnet.FileBasedApps\ai\agent-with-subagents\agent-with-subagents.cs` (engine, about 2,400 lines)
- `Z:\Sources\Dotnet.FileBasedApps\ai\agent-with-subagents\dashboard.html` (UI, about 1,200 lines, vanilla JS)
- `Z:\Sources\Dotnet.FileBasedApps\ai\agent-with-subagents\HANDOFF.md` (architecture, events, the refactoring list in §8)

**Security: `agent-with-subagents.cs:115` holds a live Anthropic API key. Never copy it into this repo,
a test or a commit. The user was told to rotate it.**

## Affected areas (verified against the tree at afaaba3)

- New backend feature folder `src/Agent42.Backend/Fleet/`: engine, Blob store, Copilot factory, registration,
  `AgentApi.Fleet.cs`.
- New Agents-layer folder `src/Agent42.Agents/Fleet/`: persisted records, `IFleetStore`, `FleetStore`, the
  `IFleetBlobs` seam and `InMemoryFleetBlobs`. This follows the storage-abstraction exemplar
  (`Attachments/IAttachmentStore.cs` plus its in-memory twin). The Azure adapter `AzureFleetBlobs` lives in Backend.
- `src/Agent42.Backend/Api/AgentApi.cs`: register `MapFleetEndpoints` (CLAUDE.md rule 7: a partial that is
  never mapped silently deletes its endpoints).
- `src/Agent42.Backend/Program.cs`: call `AddAgentFleet(...)`, which lives in its own registration file
  (rule 9 and S3776; shaped like `Memory/HindsightRegistration.cs`).
- `tools/Agent42.DevHost/Program.cs`: in-memory fleet store. Copilot fleet agents are disabled offline.
- `Directory.Packages.props`, `src/Agent42.Backend/Agent42.Backend.csproj`: add `Microsoft.Agents.AI.Workflows`
  and `Microsoft.Agents.AI.Declarative`. Today only `Microsoft.Agents.AI`, `.OpenAI` and `.GitHub.Copilot` 1.23.0
  are pinned (`Directory.Packages.props:45-49`).
- `src/Agent42.Backend/appsettings.json`: a `Fleet` section.
- `infra/modules/storage.bicep`: a new `agent-fleet` blob container, plus its name passed to the app
  (`infra/main.bicep`, `infra/modules/container-app.bicep`).
- `src/Agent42.Backend/wwwroot/fleet/` (new): `index.html`, `fleet.css`, `fleet.js`.
- `src/Agent42.Backend/wwwroot/index.html`: the sidebar-footer link. `src/Agent42.Backend/wwwroot/sw.js`: bump
  `CACHE`.
- `tests/Agent42.Tests/`: new `Fleet*Tests.cs`.
- Docs: a new ADR, `.agentic/gotchas.md` entries, and a feature-map row in `CLAUDE.md`.

## Context & constraints

### User decisions (interview, 2026-10-12)

1. **Not grain-based.** Persist tasks and results. Orleans is not used for fleet state.
2. **Copilot agents keep their built-in tools (shell, file writes, URL fetch) in the backend container. ADR 0085
   is deliberately set aside for this feature, and this is documented.** The new ADR must record it as an amendment
   of 0085's trust boundary for the fleet only, with the residual risks named (see step 1). The cheap mitigations
   from 0085 stay: `MinimalEnvironment()` (no managed identity, Cosmos or storage variables reach the runtime),
   a per-task working directory, read/write outside it refused, and every write, shell command and URL fetch
   asked to the user (with "always for this task"). There is also a kill switch, `Fleet:CopilotAgentsEnabled`.
3. **Tools: the workspace tools, `delegate_to_team`, `web_search`, `fetch_web_page`, and the user's own MCP
   servers.**
4. **Storage: one private Blob container `agent-fleet`** behind `IFleetStore` with an in-memory twin. No Cosmos
   container and no grains.
5. **Limits: every task has a token cap**: a default in the UI, a server-side maximum from config, the agent cap,
   and one running task per user. **The six system prompts can be edited per user**, saved in Blob.

### Investigation findings

- **No operator model exists (ADR 0019).** The engine's hardcoded Anthropic client and model list must become the
  user's own: chat clients come from `IUserChatClientFactoryProvider.GetForUserAsync(userId)` → `GetClient(modelId)`
  (`src/Agent42.Agents/Models/IUserChatClientFactoryProvider.cs`, `src/Agent42.Backend/Models/ChatClientFactory.cs:74`).
  The choosable models come from `IUserModelCatalogProvider.GetForUserAsync(userId)`
  (`src/Agent42.Backend/Models/UserModelCatalogProvider.cs`). Model ids are qualified `provider:model` and split on
  the FIRST colon (gotcha "A GitHub model id contains a `/`"). A user with no tool-capable model cannot start a
  task: the page shows the same "add a provider" gate as the composer.
- Only the `AzureOpenAI` (Foundry) and `OpenAICompatible` provider kinds exist
  (`src/Agent42.Backend/Models/ModelProviderOptions.cs`). Drop the engine's Anthropic-only pieces:
  `WebSearchThinkingChatClient`, sampling-parameter nulling, and the hosted `webSearch`/`codeInterpreter` YAML
  kinds. A YAML that uses a hosted kind is rejected at load with "use the `web_search` function", so the planner
  retries. The gotcha "`HostedWebSearchTool.Name` is `web_search`" explains why the two must not coexist.
- Token usage reaches the usage dashboard's TOTALS because the per-user clients are already wrapped in
  `UseOpenTelemetry` (`ChatClientFactory.cs:219`). **Attribution is NOT free.** `user.*` comes from Orleans
  `RequestContext` under `UserTelemetryContext.RequestContextKey`, set per HTTP request by
  `src/Agent42.Backend/Telemetry/UserTelemetryMiddleware.cs:25`. Async paths without it land in an unreported
  bucket (gotcha "The usage dashboard counts tokens from `chat` spans ONLY", .agentic/gotchas.md:712). The runner
  therefore sets that key for the task's user at the start of its background `Task`, and the application dimension
  to `"Agent42 fleet"` (the `PublishUsageApplication` mechanism). It never relies on the POST's flowed
  ExecutionContext. Fleet budget counting wraps the client per task and never modifies the shared client.
- **`SelectableTools` cannot be called outside `AgentGrain`** (`src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:163`).
  The fleet builds its own small catalog:
  - `IWebSearch` and `IWebPageFetcher` (`src/Agent42.Agents/Web/`), truncated with `WebToolLimits` as the grain
    does.
  - `IUserMcpToolProvider.GetForUserAsync` (`src/Agent42.Agents/Mcp/IUserMcpToolProvider.cs`), gated with
    `ToolCatalog.IsMcpToolEnabled` against the user's DEFAULT tool selection from `UserProfileGrain`. This is the
    ADR 0021 user layer, and `null == all`.
  - The ADR 0081 `ReauthNoticeMcpTool` wrapper (static, reusable).
  - ADR 0095 `ElicitingMcpTool`, with a sink that turns an elicitation into a fleet question.
  - No URL seeding (ADR 0067), because the fleet has no `read_web_document`.
  - The engine's file-based `remember`/`recall`/`knowledge_search` are NOT ported.
- **Copilot.** ADR 0085's `CopilotAgentFactory` (`src/Agent42.Backend/Copilot/CopilotAgentFactory.cs:127-135`) uses
  `Mode.Empty`, `SessionFs` and `/work`. The fleet needs a SEPARATE `CopilotClient` in default mode (built-in
  tools), with no `SessionFs` (fleet sessions are one-shot and never resumed) and the per-task working directory.
  Reuse `CopilotAgentFactory.MinimalEnvironment()`, and get BYOK settings from `ICopilotProviderSource`
  (`src/Agent42.Backend/Copilot/CopilotProviderSettings.cs:37`), so `copilot/<model>` maps to one of the user's
  models.
- **No SSE exists in the app today.** Chat streams NDJSON (`src/Agent42.Backend/Conversations/AgentApi.Conversations.cs:223`).
  The fleet uses SSE (`text/event-stream`). On connect it replays the persisted log and then streams live, which
  fixes the engine's "events kept in memory" gap (HANDOFF §9). Send a keep-alive comment every 15 s, so the ACA
  ingress does not drop an idle stream.
- **Append-blob limit:** at most 50,000 blocks per blob. Streamed tokens must be COALESCED before they are
  persisted: flush per task at most once a second, or when a non-text event arrives, and merge consecutive
  `agent.text`/`agent.thinking` deltas per (team, attempt, agent). The live fan-out may stay per-delta. Cap the
  log size (`Fleet:MaxEventLogBytes`). Past the cap, text deltas stop being persisted, but structural events and
  results still are.
- **Single replica is load-bearing (CLAUDE.md rule 12).** The registry of running tasks, open questions and live
  subscribers is in-process. On restart a task that is persisted as `running` but absent from the registry is
  reported and rewritten as `interrupted` the first time it is read. No startup scan of all users. Interrupted
  tasks cannot be followed up, but the page offers "run again" (it resubmits the request).
- Blob RBAC: the app identity's Storage Blob Data Contributor grant is account-scoped
  (`infra/modules/storage.bicep:69,86`, `infra/modules/storage-role-assignment.bicep`), so the new container needs
  **no role assignment** (rule 5 is not triggered).
- **Workspace.** Agents and Copilot work in a local directory, `Path.GetTempPath()/agent42-fleet/{workspaceId}`
  (workspaceId is a GUID, and the user id never appears on disk). It is synced to Blob
  (`{userId}/workspaces/{workspaceId}/…`) after every attempt and at the end of a task. It is materialized from Blob
  when a follow-up starts, and the local directory is deleted when the task ends. Caps: `Fleet:MaxWorkspaceBytes`
  and `Fleet:MaxWorkspaceFiles`. A sync that would exceed them skips the excess files and logs a warning.
- **Serving workspace files is an XSS sink.** The content is model-written. Download endpoints MUST send
  `Content-Disposition: attachment`, `X-Content-Type-Options: nosniff` and a non-HTML content type
  (`text/plain; charset=utf-8` for text, `application/octet-stream` otherwise). Never `text/html` or `image/svg+xml`
  from the app origin. The ADR 0084 CSP sandbox header is the precedent.
- Egress (CLAUDE.md rule 13, ADR 0013): content goes to the user's own providers, SearXNG, context.dev and the
  user's MCP servers, all of which are existing paths. **New:** Copilot fleet agents' built-in URL fetch and shell
  network access can reach any host from the container. The ADR records this as an ADR 0013 extension, gated by
  the user's per-action approval and the kill switch.
- Frontend: a standalone page follows `wwwroot/logs/` (a classic script, with theme tokens COPIED from
  `styles.css:6-34` as `logs/logs.css` does, a no-FOUC theme script, and libraries from `/shared/lib/`
  (`marked.min.js`, `purify.min.js`)). The link goes in `.sidebar__footer-actions` next to `#openUsageBtn`
  (`wwwroot/index.html` around lines 92-124). A plain `<a href="/fleet/">` needs no JS module. Bump `CACHE` in
  `sw.js` anyway, because `index.html` is in `SHELL`. The page lives outside `wwwroot/js/`, so it is not in `SHELL`
  (gotchas 2311/2331/2366).
- Flat namespace (ADR 0039/0040, rule 1): every new type is in `Agent42.Agents` / `Agent42.Backend`, even under
  `Fleet/`. Fleet JSON in Blob is our own format (not Orleans `$type`), but keep the rule anyway.
- **ADR numbering:** `main` ends at 0095 (MCP elicitation). The in-flight branch
  `feature/inbound-email-triggers-an-agent` has an unmerged ADR also numbered 0095, which collides with
  `main`'s. Take the next free number at
  execution time, after checking `origin/main` and open branches.

### Governing ADRs and gotchas to read before starting

ADR 0013, 0018, 0019, 0021, 0026, 0039/0040, 0041, 0050/0051, 0054 (standalone `/logs/` page), 0081, 0084
(sandbox/CSP precedent), 0085, 0095. Gotchas: "Chat is fully per-user", "A GitHub model id contains a `/`",
"`HostedWebSearchTool.Name` is `web_search`", "A named HttpClient silently inherits AddServiceDefaults' retry
pipeline", "Minimal-API handlers returning a bare string serve text/plain", "Service worker must stay
network-first", the SHELL/CACHE trio (2311/2331/2348), "A scripted whole-file rewrite silently converts
CRLF to LF", and "The usage dashboard counts tokens from `chat` spans ONLY" (712).

- **Service worker navigate fallback:** `sw.js:106` serves the cached `/index.html` for any failed navigation, so
  `/fleet/` offline would paint the chat app. Add a `/fleet` guard next to the existing `/api`, `/searxng`,
  `/dashboard` and `/.auth` guards (`sw.js:85-100`), BEFORE `respondWith`. `/logs/` has the same gap, but that is
  out of scope here.
- **Coverage gate honesty:** there is NO Blob test seam in the repo (no test references `BlobAttachmentStore`
  or `BlobCopilotSessionStore`). So the store is split: `FleetStore` holds ALL the logic (layout, escaping,
  traversal refusal, index, coalescing limits) over a narrow `IFleetBlobs` seam (put, get, append, list-prefix,
  delete-prefix). `AzureFleetBlobs` is a thin adapter with no logic, and `InMemoryFleetBlobs` lets the contract
  tests run the real `FleetStore`. Likewise the real `CopilotClient` wiring in `FleetCopilotAgentFactory` is a thin
  shell around a tested policy and config builder. Measure the 80 % new-code gate honestly at the end; if the
  adapters push it under, report FAIL with the number. Never estimate it.

## Spike results (step 0, 2026-10-12)

Run as file-based apps in the session scratchpad, pinned to the repo's versions.

1. **Packages.** `Microsoft.Agents.AI.Workflows` is stable: `1.22.0`, `1.23.0`, `1.24.0`. `Microsoft.Agents.AI.Declarative`
   has **never** had a stable release (every version is `-preview`/`-rcN`, lockstep `x.y.0-rc1`). The user decided
   (2026-10-12): pin `Declarative 1.23.0-rc1` as a recorded exception to hard rule 4, and `Workflows 1.23.0` to keep
   the AF family in step (no bump to 1.24.0 in this MR).
2. **Declarative** (`ChatClientPromptAgentFactory` + `CreateFromYamlAsync`, wrapped in a probing factory):
   ```
   name=Researcher hint=openai:gpt-5 type=ChatClientAgent
   tool web_search ReflectionAIFunction isAIFunction=True
   tool nope DefaultAIFunctionDeclaration isAIFunction=False
   hosted tool web_search HostedWebSearchTool
   bad yaml -> returned ChatClientAgent
   bad2 -> returned ChatClientAgent
   ```
   `model.id` reaches `ModelNameHint` verbatim, including a `provider:model` id. An unknown function becomes a
   declaration only (the engine's check works). A hosted `webSearch` kind becomes a `HostedWebSearchTool` (detect and
   reject it). **Malformed YAML does not throw**: it loads an agent with a wrong/empty name, so the engine's
   name-must-match check is the real guard for invalid YAML.
3. **Workflows** (fake echo clients, `RunStreamingAsync` + `TurnToken(emitEvents: true)`):
   ```
   Sequential: output=List`1 last=Beta says hi
   Concurrent: output=List`1 last=Alpha says hi
   GroupChat:  output=List`1 last=Beta says hi
   Handoff:    output=List`1 last=Alpha says hi
   Magentic:   output=List`1 last=Task execution stopped due to hitting the maximum round count limit.
      events=ExecutorCompletedEvent,ExecutorInvokedEvent,MagenticPlanCreatedEvent,MagenticReplannedEvent,...,WorkflowOutputEvent,WorkflowWarningEvent
   AgentResponseUpdateEvent base: Microsoft.Agents.AI.Workflows.WorkflowOutputEvent
   ```
   All builder names exist as planned. **`AgentResponseUpdateEvent` derives from `WorkflowOutputEvent`**: the
   `AgentResponseUpdateEvent` case must stay before every `WorkflowOutputEvent` case, and those must keep their
   `Data` type filters (the engine's order already does this). Magentic with a model that never emits a ledger ends at
   max rounds with a text output, which is enough for a smoke test.
4. **Copilot SDK 1.0.16** (reflection only): the working directory is `SessionConfig.WorkingDirectory` (also
   `CopilotClientOptions.WorkingDirectory`); default mode is `CopilotClientMode.CopilotCli` (vs `Empty`).
   `PermissionRequestRead{Path}`, `Write{FileName,Intention}`, `Shell{FullCommandText,Intention}`, `Url{Url,Intention}`,
   `Mcp{ReadOnly,ServerName,ToolName}` and `CustomTool{ToolName}` exist as the engine uses them.
   **UNVERIFIED:** that a default-mode BYOK session actually raises write/shell permissions and honours `cwd`. That
   needs a live model key, which this session does not have. The permission policy is unit-tested; the live check is
   a manual post-merge check.

## Steps

Each step is gated on its tests. Steps 3, 5 and 9 are parallelizable once step 2 lands (each needs worktree
isolation if run concurrently).

### Step 0: Spike the package and API assumptions (`/spike-dotnet`, outside the repo)

Validate with the repo's AF 1.23.0 pins and an OpenAI-compatible `IChatClient` (DevHost's canned client, or any
key the user supplies):
1. `dotnet add package Microsoft.Agents.AI.Workflows` and `Microsoft.Agents.AI.Declarative` resolve to versions
   compatible with `Microsoft.Agents.AI` 1.23.0, and whether the latest of each is stable or prerelease. **If either
   exists only as a prerelease, stop and ask the user** (hard rule 4 says latest stable).
2. `ChatClientPromptAgentFactory(chatClient, functions).CreateFromYamlAsync(yaml)` loads a Prompt agent, and
   `model.id` reaches `GptComponentMetadata.Model.ModelNameHint` for `ModelRoutingPromptAgentFactory`.
3. `AgentWorkflowBuilder.BuildSequential`/`BuildConcurrent`/`CreateGroupChatBuilderWith`/`CreateHandoffBuilderWith`/
   `CreateMagenticBuilderWith(...).RequirePlanSignoff(false)` all exist in the resolved version. Also check that
   `InProcessExecution.RunStreamingAsync` plus `TurnToken(emitEvents: true)` emits `AgentResponseUpdateEvent`,
   `ExecutorInvoked/Completed/FailedEvent`, `WorkflowOutputEvent` and the `Magentic*Event`s under these names.
4. A default-mode `CopilotClient` (not `Mode.Empty`) with BYOK `Type="openai"`, `MinimalEnvironment()`, and a
   session working directory set to a temp folder: a write inside it raises `PermissionRequestWrite`, a shell command
   raises `PermissionRequestShell`, and `cwd` is honoured. Name the exact `SessionConfig`/`CopilotClientOptions`
   property for the working directory.

Record the results under "Spike results" in this spec. Any difference from the API names above changes the steps
that follow.
**Verify:** the spike output is pasted into the spec.

### Step 1: ADR (`/decide`)

Topic: "Agent42 agent fleet: an in-process recursive team runner outside Orleans, persisted in Blob, with
Copilot agents that keep built-in tools." It amends **0085**: for fleet tasks only, Copilot built-in tools run in
the backend container. The residuals must be recorded verbatim:
- a shell can reach the app port on `localhost` and forge `X-MS-CLIENT-PRINCIPAL-ID` (`Api/CurrentUser.cs`);
- the container filesystem is readable by a shell;
- the network reach is the container's.

These are mitigated by per-action user approval, `MinimalEnvironment`, the per-task working directory and
`Fleet:CopilotAgentsEnabled`. The ADR also extends **0013** (Copilot URL/shell egress) and leans on **0012**/rule 12
(in-process registry, single replica). Alternatives to record: grain-hosted runner (rejected by the user), Cosmos
index (rejected in favour of Blob only), NDJSON polling instead of SSE (rejected: the engine's whole UI is
event-replay), and Copilot in `Mode.Empty` (rejected by the user). The frontmatter is `amends: [0085, 0013]`
(0085's own frontmatter is the precedent). Record the usage attribution (the application is `"Agent42 fleet"`).
**Verify:** the ADR file exists. `grep -n "0085" ` of the new ADR shows the amendment, and 0085 gets an
"amended by" note.

### Step 2: Packages, options, Blob container

- Add the two packages with `dotnet add package` (pinned in `Directory.Packages.props` next to the AF family, with
  a comment linking the ADR) to `Agent42.Backend.csproj`.
- Add `FleetOptions` (section `Fleet`): `MaxTeamDepth`(6), `DefaultMaxGroupChatRounds`(60),
  `MaxAgentsPerTask`(40), `DefaultMaxTokensPerTask`, `MaxTokensPerTaskLimit`, `BudgetSoftLimit`(0.8),
  `MaxConcurrentSubTeamsPerLevel`(6), `ApprovalDepth`(0), `JudgeDepth`(1), `PassingScore`(7), `MaxAttempts`(3),
  `MaxFixRounds`(1), `MaxRetryResultLength`(20000), `MaxToolTextLength`(4000), `MaxConcurrentTasks`(global, 2),
  `MaxEventLogBytes`, `MaxWorkspaceBytes`, `MaxWorkspaceFiles`, `CopilotAgentsEnabled`, and `DefaultCopilotModel`
  (empty: use the planner's model).
- Add the `agent-fleet` container to `storage.bicep` as a **fixed literal**, copying the `copilot-sessions` block and
  its comment ("a param here would be a second knob that could drift from the code"). Hardcode
  `ContainerName = "agent-fleet"` in `AzureFleetBlobs`, as `BlobCopilotSessionStore.cs:25` does. No option, no env
  var, and no change to `main.bicep` / `container-app.bicep`.

**Verify:** `dotnet build Agent42.slnx`; `FleetOptionsTests` (binds from config, defaults sane, token default ≤
limit). The Bicep build passes (`az bicep build --file infra/main.bicep`, if available locally; CI otherwise).

### Step 3: Persistence (`IFleetStore`)

`src/Agent42.Agents/Fleet/`: records `FleetTaskRecord` (Id, UserId, Request, FollowUpOf, WorkspaceId, PlannerModel,
Status `running|done|stopped|failed|interrupted`, Result, Reason, StartedAt, FinishedAt, Tokens, Agents, MaxTokens)
plus `IFleetStore`:
- list tasks per user, newest first;
- get, upsert and delete a task (delete removes its event log and, when no other task uses the workspace, the
  workspace);
- append events and read events (stream);
- workspace put, get, list and delete-prefix;
- get and set per-user prompt overrides.

The implementation is `FleetStore : IFleetStore` over the `IFleetBlobs` seam (see Context, "Coverage gate
honesty"). `IFleetBlobs` and `InMemoryFleetBlobs` live in `src/Agent42.Agents/Fleet/`, and the thin adapter in
`src/Agent42.Backend/Fleet/AzureFleetBlobs.cs`. The layout is `{escaped userId}/tasks/{taskId}.json`,
`{userId}/events/{taskId}.ndjson` (append blob), `{userId}/workspaces/{workspaceId}/{path}` and
`{userId}/prompts.json`. Escape the user id the way `BlobCopilotSessionStore` does, and refuse `..` and absolute
paths in workspace paths.
**Verify:** `FleetStoreTests` runs the real `FleetStore` over `InMemoryFleetBlobs`. It covers the path-traversal
refusals, user-id escaping, per-user isolation (user B's list never shows user A's tasks), delete semantics
(a workspace shared by a follow-up chain is kept until its last task is deleted) and list ordering.

### Step 4: Engine port (`src/Agent42.Backend/Fleet/`)

Port HANDOFF §8 refactorings 1–8. The engine's local functions become `FleetTeamRunner`, with one instance per
task run, holding the per-run budget, ids, events sink and interaction. It contains `RunTeamAsync`,
`CreateTeamAsync`, `JudgeAsync`, `DelegateToTeamAsync`, `BuildWorkflow`, `RunWorkflowAsync` and `RunStagesAsync`.
Also port, as separate files: `FleetBudget` (`RunBudget`), `FleetBudgetChatClient`
(`TokenBudgetChatClient`, with the budget passed in and no global), `ModeratedGroupChatManager`,
`ModelRoutingPromptAgentFactory` (keyed by the user's tool-capable catalog ids), `FleetPrompts` (definitions and
defaults copied from the engine's `Prompts`, with a per-user override map loaded from the store), `FleetWorkspace`
(local dir and tools, `Describe`, sync to and materialize from `IFleetStore`), `TeamPlan`/`AgentDefinition`/
`Verdict`/`TeamContext`, an `IFleetEventSink` and an `IFleetInteraction` (`AskAsync`).

Adaptations:
- **Models.** The planner, judge, moderator and Magentic manager run on the task's chosen model. The planner prompt
  lists the user's tool-capable models instead of the Claude list (rewrite that prompt section; keep
  `{{defaultModel}}`).
- **Hosted tool kinds** in YAML are rejected (see Context).
- **Console output** becomes `ILogger`. Drop `MetricsStatusBar` and `OpenBrowser`. Keep the `agents.spawned`
  counter on a meter registered with the app's OTel setup (find where ServiceDefaults adds meters).
- Ids per runner (`team-N`, …). Directories go into the store, not `generated-agents/`. `plan.json` and the verdicts
  are carried in events (`plan.submitted`, `verdict`) and need no separate files.
- **S3776 ≤ 15 is a build error.** `RunTeamAsync` and `SubmitTeamPlanAsync` must be split into named helpers. Don't
  game the metric.

**Verify:** `FleetTeamRunnerTests` with a scripted fake `IChatClient` (find the repo's existing fake that emits
`FunctionCallContent`; extend it rather than invent one). Cover:
- the planner's `submit_team_plan` missing args → rejected with a message;
- invalid YAML → errors back;
- a hosted kind → rejected;
- a Sequential team runs → result;
- judge below `PassingScore` → fix round → new team → best result kept;
- a judge with no verdict passes;
- `delegate_to_team` absent at `MaxTeamDepth` and refused at the soft limit;
- the token cap cancels the task with status `stopped`;
- `BuildWorkflow` builds a runnable workflow for each of Concurrent, GroupChat (moderator `finish` ends it),
  Handoff and Magentic (`RequirePlanSignoff(false)`), each with one smoke run against the scripted client;
- plan approval: "y" runs, "n" stops the task, free text revises the plan;
- the user attribution key is set in `RequestContext` during a run (`Run_SetsUserTelemetryContext`).

Plus `ValidateStages` unit tests and `FleetPrompts.Render` tests (one pass, a value containing `{{x}}` is not
re-expanded).

### Step 5: Fleet tool catalog

`FleetToolCatalog.BuildAsync(userId, workspace, interaction, ct)` returns the `AIFunction`s:
- the workspace tools;
- `web_search` and `fetch_web_page` over `IWebSearch` / `IWebPageFetcher` (truncated);
- the user's MCP tools: `IUserMcpToolProvider`, gated by `ToolCatalog.IsMcpToolEnabled` against the user's default
  selection, with the reauth wrapper (`ReauthNoticeMcpTool`, only when `McpServerTools.InteractiveOAuth`) outermost
  and the eliciting wrapper (`ElicitingMcpTool`) innermost. Mirror `AgentGrain.BuildAgent.cs:363-383`. The router is
  the group's `McpServerTools.Elicitation` (`McpElicitationRouter`, `src/Agent42.Agents/Mcp/McpElicitationRouter.cs`).
  The sink is a `FleetElicitationSink : IMcpElicitationSink` (`src/Agent42.Agents/Mcp/McpElicitation.cs:34`) that
  turns an elicitation into a fleet question through `IFleetInteraction`.

Skip tool names outside `^[A-Za-z0-9_-]{1,128}$` with a warning (ADR 0085 finding).
**Verify:** `FleetToolCatalogTests` covers:
- with `SingleUserMcpToolProvider` holding two servers, a deselected server or tool is absent;
- `null` selection == all;
- reauth wrapping only for InteractiveOAuth;
- web tool results truncated.

### Step 6: Copilot fleet agents

`FleetCopilotAgentFactory : PromptAgentFactory` (`copilot` / `copilot/<model>`, where `<model>` is one of the user's
catalog ids, defaulting to the task's model). It has its own lazily started default-mode `CopilotClient` per silo,
`MinimalEnvironment()`, the working directory = the task's local workspace dir, and the system message plus the
`copilot-report` prompt. It also hosts `FleetCopilotPermissionPolicy`, ported from the ENGINE's
`CopilotPermissionPolicy` class in `agent-with-subagents.cs` (outside the repo; Agent42 only has the static
`CopilotAgentFactory.DecidePermission`), with one instance per task (HANDOFF §8.5) that asks through
`IFleetInteraction`. **Add a case the engine lacks:** `PermissionRequestCustomTool`, meaning Agent42's own
functions (workspace tools, `delegate_to_team`, `web_search`, `fetch_web_page`, MCP), is approved once and never
asked. Default mode still raises it, and asking per call would stall every agent. This is the same rule as
`CopilotAgentFactory.DecidePermission`. When `CopilotAgentsEnabled` is false,
or there is no `ICopilotProviderSource` for the model, it throws a load error that the planner sees ("Copilot agents
are not available, use a normal agent"). The planner prompt's Copilot section is only included when they are
enabled. DevHost and tests: disabled.
**Verify:** `FleetCopilotPermissionPolicyTests` (pure):
- a custom tool is approved without asking (`CustomTool_ApprovedWithoutAsking`);
- reads inside are allowed, reads outside refused;
- a write outside is refused;
- write, shell and url ask, and "a" (always) persists per task only;
- a free-text answer becomes the rejection reason;
- a fresh task does not inherit "always".

`FleetCopilotAgentFactoryTests`: disabled → load error; a non-copilot model returns null (fall-through).

### Step 7: Runner service

`FleetTaskService` (singleton):
- `SubmitAsync(userId, request, followUpOf?, model, maxTokens)`. It refuses: an empty request; a running task for
  this user; the global `MaxConcurrentTasks`; a `maxTokens` over the limit; a model that is not tool-capable or not
  in the user's catalog; a follow-up of a task that is not `done` or belongs to another user.
- `StopAsync`, `AnswerAsync`.
- `SubscribeAsync(userId, taskId)`: replays the store, then goes live, with no gap or duplicate between replay and
  live. Hold a per-task sequence number and subscribe before reading the persisted log. Skip ≤ the last replayed
  sequence.
- `GetAsync`/`ListAsync`, which apply the interrupted rewrite.

It runs each task on a background `Task` tied to `IHostApplicationLifetime.ApplicationStopping`: on shutdown it
cancels running tasks and persists them as `interrupted`, best effort. The event writer coalesces per the
Context. Follow-up input = the engine's `FollowUpInput` over the chain loaded from the store, with the workspace
materialized. `task.restored` is unnecessary, because the list comes from the store.
Register it via `FleetRegistration.AddAgentFleet(services, configuration)`:
- in Backend, `FleetStore` over `AzureFleetBlobs`;
- in DevHost, `FleetStore` over `InMemoryFleetBlobs`;
- in the API test fixture, if fleet endpoints are tested there.

Resolve it optionally in endpoints (rule 7, the `/api/speech` pattern): a missing service → 503.
**Verify:** `FleetTaskServiceTests` covers:
- a second submit for the same user → refused, while a different user is accepted;
- the global cap;
- a cross-user follow-up → refused;
- each other refusal: an empty request, `maxTokens` over the limit, a model not in the catalog or not
  tool-capable, and a follow-up of a non-`done` task (`Submit_Refusals` theory);
- `ApplicationStopping` → running tasks persisted as `interrupted` (`Shutdown_PersistsInterrupted`);
- `running` in the store + not in the registry → `interrupted` on read, and persisted;
- the replay+live subscriber sees every event exactly once in order (race test);
- text deltas coalesced in the persisted log but complete;
- the log cap drops text but keeps `task.finished`;
- the stop → `stopped` status with reason.

### Step 8: API (`src/Agent42.Backend/Fleet/AgentApi.Fleet.cs`)

Shape it like `src/Agent42.Backend/Users/AgentApi.Users.cs`. All routes are behind Easy Auth (NOT in
`excludedPaths`), with `userId = GetUserId()`, and a task of another user → 404:

| Route | Purpose |
|---|---|
| `GET /api/fleet/tasks` | list |
| `POST /api/fleet/tasks` `{task, followUpOf?, model, maxTokens}` | 202 `{taskId}` / 409 `{error}` |
| `GET /api/fleet/tasks/{id}` | record |
| `GET /api/fleet/tasks/{id}/events` | SSE, replay + live, 15 s keep-alive |
| `POST /api/fleet/tasks/{id}/stop`, `POST /api/fleet/tasks/{id}/answer` `{answer}` | 200 / 409 |
| `DELETE /api/fleet/tasks/{id}` | refused while running |
| `GET /api/fleet/tasks/{id}/files`, `GET /api/fleet/tasks/{id}/files/{**path}` | attachment + nosniff + non-HTML type |
| `GET /api/fleet/prompts`, `PUT/DELETE /api/fleet/prompts/{key}` | per-user prompts |
| `GET /api/fleet/config` | limits, defaults, whether Copilot agents are enabled |

Models come from the existing `GET /api/models`. Return JSON objects, never bare strings (gotcha). Add
`MapFleetEndpoints` to `AgentApi.cs`.
**Verify:** `FleetApiTests` on `SharedApiFixture` covers:
- isolation (B gets 404 on A's task, files, events and delete);
- 409s: a second submit, stop/answer with nothing running or open, delete while running;
- the SSE keep-alive comment is sent (with an injectable interval, so the test does not wait 15 s);
- download headers (`Content-Disposition: attachment`, `nosniff`, never `text/html` for an `.html` workspace file);
- path traversal in `{**path}` → 400/404;
- the SSE content type plus replayed events;
- prompts round trip and reset;
- missing service → 503.

Also add an `EasyAuthExclusionTests` assertion that `/api/fleet` is not excluded, if that test enumerates.

### Step 9: Fleet page (`wwwroot/fleet/`)

Port `dashboard.html`'s event→state reducer and views into `fleet.js` (a classic script; HANDOFF §6 lists the
events). The views are: task list (with status, follow-up chain and "run again" for interrupted tasks), team tree,
attempt detail (plan with agents/YAML/flow, stages, verdicts), agent panel (streamed text, thinking, tool calls),
the open question bar (choices as buttons + free text), budget/metrics meters, a prompts editor, workspace files
(download links) and a composer (task, model picker from `/api/models` filtered to tool-capable models, token cap).

Restyle the page with Agent42's tokens (copied from `styles.css:6-34`, as `logs/logs.css` does), its fonts,
light/dark handling with the no-FOUC script, and a header with a back link to `/`. Render markdown with
`/shared/lib/marked.min.js` + `purify.min.js`; never assign unsanitized model text to `innerHTML`. Use `EventSource`
(same origin, so the cookie rides along). On an error, retry with backoff. After repeated failures (the Easy Auth
session expired), show "Sign in again" linking to `/`. With no tool-capable model, show the provider gate.

Add `<a class="sidebar__settings" href="/fleet/">Agent42 agent fleet</a>` to `.sidebar__footer-actions` in
`wwwroot/index.html`. In `sw.js`, bump `CACHE` and add the `/fleet` navigate guard (see Context).
**Verify:** `FleetPageAssetsTests`, shaped like `LogsConsoleAssetsTests` (fetch `/fleet/` directly, NOT via
`GetAppScriptAsync`):
- the page and its assets are served;
- it uses `/shared/lib/` and loads purify before any markdown render;
- `index.html` has the `/fleet/` link with the exact label;
- `fleet.js` handles every event type the backend emits (one source-text list shared with a backend constant, or
  assert each `Publish("…")` type name in `Fleet/*.cs` appears in `fleet.js`).

Plus `ServiceWorker_FleetNavigationNotServedFromShell` (in `PwaAssetsTests`), and
`PwaAssetsTests.ServiceWorker_CacheNameIsCurrent` stays green. Manual check: `dotnet run --project
tools/Agent42.DevHost`, open http://localhost:61326/fleet/, run a task against the canned client, reload mid-run,
and confirm the replay.

### Step 10: Docs (`/sync-docs`, `/gotcha`)

- `CLAUDE.md` feature map: add a row "Agent fleet (recursive agent teams, `/fleet/`)" → the new ADR. Also amend the
  Copilot row ("phase-1 trust boundary") to mention the fleet amendment.
- `docs/features/agent-fleet.md` (new), plus a row in `docs/features/README.md` and the top-level `README.md`
  feature list. `docs/features/custom-agents.md:171` ("no built-in shell or file tools in phase 1") gets a pointer
  saying the fleet is the exception.
- ADR 0085: an "Amended by 00xx (fleet)" note under its title.
- Gotchas:
  - append-blob coalescing and the 50k-block limit;
  - workspace downloads are an XSS sink;
  - `interrupted` is computed on read;
  - the fleet Copilot client is NOT the ADR 0085 client (`Mode`, `SessionFs`, working dir differ: don't merge them);
  - replay/live ordering.
- Add exemplar rows if the store or SSE endpoint is a new canonical shape.
- Move this spec to `specs/completed/` at the end.

**Verify:** `dotnet test Agent42.slnx` (incl. doc-path integrity / `QualityGateTests`), `dotnet format Agent42.slnx
--verify-no-changes`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | Sidebar shows "Agent42 agent fleet", linking to `/fleet/` | `IndexHtml_LinksToFleetPage` | `tests/Agent42.Tests/FleetPageAssetsTests.cs` |
| 2 | The fleet page uses Agent42's theme tokens and sanitized markdown | `FleetPage_UsesThemeTokensAndSharedLibs` | `FleetPageAssetsTests.cs` |
| 3 | A submitted task is planned, run, judged and finished with a result using only the user's own models | `SequentialTeam_ProducesResult`, `Planner_UsesUserCatalogModels` | `FleetTeamRunnerTests.cs` |
| 4 | Rejected result → fix → new team → best kept | `Judge_RejectsThenFixThenNewTeam_KeepsBest` | `FleetTeamRunnerTests.cs` |
| 5 | Sub-teams honour depth, soft limit and per-level slots | `Delegate_AbsentAtMaxDepth`, `Delegate_RefusedAtSoftLimit` | `FleetTeamRunnerTests.cs` |
| 6 | The token cap stops a task with status `stopped` | `TokenCap_StopsTask` | `FleetTeamRunnerTests.cs` |
| 7 | Tasks, results and the event log persist and replay after a reload | `Subscribe_ReplaysThenLive_ExactlyOnce`, `Events_TextCoalesced_Complete` | `FleetTaskServiceTests.cs` |
| 8 | A task running at restart is shown as `interrupted` | `RunningNotInRegistry_IsInterrupted_AndPersisted` | `FleetTaskServiceTests.cs` |
| 9 | Follow-ups build on a done task's chain and workspace, own tasks only | `FollowUp_UsesChainAndWorkspace`, `FollowUp_OtherUsersTask_Refused` | `FleetTaskServiceTests.cs` |
| 10 | One running task per user, plus a global cap | `SecondSubmit_SameUser_Refused`, `GlobalCap_Refused` | `FleetTaskServiceTests.cs` |
| 11 | Users can't see each other's tasks, events or files | `OtherUser_Gets404_Everywhere` | `FleetApiTests.cs` |
| 12 | Workspace downloads can't execute in the app origin | `FileDownload_IsAttachment_NoSniff_NotHtml`, `FileDownload_PathTraversal_Refused` | `FleetApiTests.cs`, `FleetStoreTests.cs` |
| 13 | Plan approval and Copilot permission questions are answerable from the page | `Approval_Run_Stop_Change`, `Permission_Always_PerTaskOnly` | `FleetTeamRunnerTests.cs`, `FleetCopilotPermissionPolicyTests.cs` |
| 14 | MCP tools follow the user's selection; the reauth notice is preserved | `McpTools_GatedBySelection`, `McpTools_ReauthWrapped` | `FleetToolCatalogTests.cs` |
| 15 | Copilot agents can be switched off | `CopilotDisabled_LoadErrorForPlanner` | `FleetCopilotAgentFactoryTests.cs` |
| 16 | System prompts are editable per user and reset to default | `Prompts_SetResetRoundTrip_PerUser` | `FleetApiTests.cs` |
| 17 | Hosted YAML tool kinds are refused with guidance | `HostedToolKind_Rejected` | `FleetTeamRunnerTests.cs` |
| 18 | Every orchestration pattern runs (Sequential, Concurrent, GroupChat, Handoff, Magentic, Staged) | `BuildWorkflow_EachPattern_Runs`, `ValidateStages_*` | `FleetTeamRunnerTests.cs` |
| 19 | Invalid submits, stop/answer/delete misuse return 409 with a reason | `Submit_Refusals`, `Api_Conflicts` | `FleetTaskServiceTests.cs`, `FleetApiTests.cs` |
| 20 | Fleet tokens are attributed to the user in the usage dashboard | `Run_SetsUserTelemetryContext` | `FleetTeamRunnerTests.cs` |
| 21 | Copilot agents' calls to Agent42 tools are never prompted | `CustomTool_ApprovedWithoutAsking` | `FleetCopilotPermissionPolicyTests.cs` |
| 22 | A shutdown leaves no task stuck as `running` | `Shutdown_PersistsInterrupted` | `FleetTaskServiceTests.cs` |
| 23 | `/fleet/` offline does not paint the chat app | `ServiceWorker_FleetNavigationNotServedFromShell` | `PwaAssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` over `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |
| `cognitive_complexity_max` | 15 (S3776, build error) | `dotnet build Agent42.slnx` |
| `vulnerable_packages` | 0 | CI package-freshness job |
| build / tests / format / doc paths | must pass | `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes` |

Report SKIPPED for any gate that could not be measured. Never estimate.

## Out of scope

- Grain hosting, multi-replica operation, and resuming an interrupted task mid-run.
- The engine's file-based `remember`/`recall`, the `knowledge_search` SQLite store, `mcp-servers.json`, Hindsight
  memory, the user's RAG documents, and skills.
- Anthropic as a provider kind (users can add any provider Agent42 supports).
- Sandboxing Copilot fleet agents (an ADR 0085 phase-2 style follow-up; the new ADR names it).
- Exposing fleet tasks via `/api/agent-run`, MCP server, webhooks, email or push notifications.
- A separate fleet view in the usage dashboard (fleet tokens are attributed per user, under the application
  `"Agent42 fleet"`).
- An offline guard for `/logs/` (the same gap as `/fleet/`, pre-existing).

## Review

Critic pass on 2026-10-12. Every finding was accepted and fixed in this spec:
1. Coverage gate unreachable for Blob: added the `IFleetBlobs` seam; the real `FleetStore` is tested over
   `InMemoryFleetBlobs`; the gate is measured honestly.
2. Usage attribution was wrong: the runner sets `UserTelemetryContext` in `RequestContext`; AC 20.
3. The container-name knob contradicted `storage.bicep`'s fixed-literal rule: dropped and hardcoded.
4. Doc debt: added `docs/features/agent-fleet.md`, the README rows, the `custom-agents.md:171` pointer, the 0085
   cross-refs and `amends:` frontmatter.
5. Copilot custom-tool permission: auto-approved; AC 21.
6. Missing tests: refusal theory, the shutdown→interrupted test, the keep-alive test, a smoke test per pattern,
   the approval flow, the 409s; ACs 18, 19, 22.
7. `CopilotPermissionPolicy` source: pointed to the engine file.
8. SW navigate fallback: `/fleet` guard; AC 23.
9. Wording: ADR numbering clarified; the elicitation router and sink types named.

No findings rejected.

## Divergence from the plan (execution, 2026-10-12/13)

- **Packages.** `Microsoft.Agents.AI.Declarative` has never been stable, so it is pinned at `1.23.0-rc1`, a recorded
  exception to hard rule 4 (user decision, ADR 0097 §10). `Workflows` is pinned at `1.23.0`, in step with the family.
- **ADR number 0097**, not 0096. Open PR #185 still carries a second 0095 and will need 0096.
- **`fetch_web_page` keeps the chat's exfiltration allowlist.** Only a URL that a search returned or that the user
  wrote in the task can be fetched (`FleetWebTools`). The plan only said "truncated". MCP results enter agent context,
  so the control matters as much here.
- **Web tools follow the user's default tool selection too**, not only MCP (ADR 0021 applied consistently).
- **Event flush:** once a second, plus at the end. It does not also flush on every non-text event. Replay reads
  the in-memory unconfirmed snapshot, so freshness doesn't depend on flush timing. The replay merge lives in
  `FleetEventLog.ReplayAsync`, so the race test drives it directly.
- **Usage attribution** is set by the SERVICE's background run, so `Run_SetsUserTelemetryContext` is in
  `FleetTaskServiceTests`, not `FleetTeamRunnerTests`.
- **A root run with no team ends `failed`** (reason `FleetTeamRunner.NoTeamResult`), not `done` with the fallback
  text. The engine did the latter. The DevHost smoke run found it, and `NoTeam_EndsFailed_NotDone` covers it.
  A delegating agent still gets the text back.
- **`metrics` event folded into `budget`** (it carries input/output tokens and the deepest level). There is no MeterListener.
- **Copilot fleet agents get no Agent42 custom tools** (engine parity: "no tools section for a Copilot agent"). The
  policy still auto-approves `PermissionRequestCustomTool`. The runtime environment is `MinimalEnvironment()` plus
  `PATH`, because a shell needs it.
- **Workspace notes** (`note_set`) are persisted on the task record (`FleetTaskRecord.Notes`), and a follow-up
  starts from them.
- **Critic fixes (2026-10-13):** a stale read can no longer overwrite a finished task as `interrupted` (re-read
  before the rewrite). Workspace listing and sync skip symbolic links and other reparse points. Answers can name
  their question (`prompt` carries an `id`, and `POST /answer` takes `questionId`). The ADR frontmatter
  `amended_by` was added on 0013 and 0085. All are covered in `FleetReviewFixTests`.
- **AC test names that differ from the table:** AC 13 is `Approval_Change_ThenRun`, `Approval_No_StopsTask`,
  `Approval_Yes_Runs` and `WriteShellUrl_Asked_AndAlwaysPersistsForTheTaskOnly`. AC 19's `Api_Conflicts` is in
  `FleetApiTests`. AC 9's `FollowUp_UsesChainAndWorkspace` is in `FleetTaskServiceTests`.
- **Not verified:** that a live default-mode Copilot session raises write/shell permissions and honours the working
  directory (no model key in the session). The manual browser check of `/fleet/` was not done, because the Chrome
  extension wasn't connected. The DevHost API smoke run (submit, SSE replay, record) was done.
