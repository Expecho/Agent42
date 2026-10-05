planned-against: main @ c9ca74e

# Plan: a user-created "copilot" agent kind (GitHub Copilot SDK loop, Agent42 toolset, Blob-backed session state)

## Overview

Add a second agent `Kind`, `copilot`. A chat bound to it runs on the GitHub Copilot SDK's agent loop
(`Microsoft.Agents.AI.GitHub.Copilot` → `GitHubCopilotAgent`, driving the Copilot CLI runtime) instead of a
`ChatClientAgent`. It uses the user's own provider (BYOK, ADR 0018/0019), Agent42's own toolset (selectable tools +
MCP tools) as Copilot **custom tools**, and persists Copilot's session files to Blob storage through the SDK's
`SessionFs`. The user wants Copilot's agent loop (planning, context compaction), not merely "BYOK + MCP", which
Agent42 already does natively.

This is **phase 1**. Copilot's built-in shell/file tools are OFF (`CopilotClientMode.Empty` allowlist). Phase 2 (a
separate plan, not this one) moves the runtime into a real sandbox so shell/file tools can be enabled. Phase 1 must
therefore keep the runtime connection swappable (stdio child process now, `RuntimeConnection.ForUri` later).

## Affected areas (verified against the tree @ c9ca74e)

| Area | Files |
|---|---|
| Kind constants | `src/Agent42.Agents/Research/AgentKinds.cs` (move/extend; see Step 3) |
| Stored agents | `src/Agent42.Agents/StoredAgents/AgentDefinition.cs` (Kind `[Id(11)]`), `AgentBinding.cs` (Kind `[Id(5)]`), `AgentRegistryGrain.cs` (insert forces Kind null ~l.56, update preserves Kind + forces per-chat for research ~l.90-91) |
| Stored-agent API | `src/Agent42.Backend/**/AgentContracts.cs` (`AgentUpsertRequest`, no Kind today), `AgentApi.StoredAgents.cs` (POST/PUT, agent-run bind ~l.128) |
| Bind paths (3) | `AgentApi.Conversations.cs` ~l.147, `AgentMcpRunner.cs` ~l.64, `AgentApi.StoredAgents.cs` ~l.128 → `AgentGrain.BindAgentAsync` (`AgentGrain.cs` ~l.435) |
| Grain composition | `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` (builds `ChatClientAgent` ~l.280; tools ~l.144-195; context providers ~l.230-275), new partial `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` |
| Grain turn paths | `AgentGrain.cs`: `ChatCoreAsync` (~l.613), `ProjectStreamedContent` (~l.1060), stopped-turn hand-append (~l.913), `GetHistoryAsync` (~l.1269), `ReceiveReminder` (~l.1469), session serialize (~l.1178) / deserialize (~l.389) |
| Provider resolution | `src/Agent42.Backend/Models/ChatClientFactory.cs` (`MultiProviderChatClientFactory`, private `Provider` ~l.210-254 holds endpoint/kind/key/UseResponsesApi), `src/Agent42.Agents/Models/IChatClientFactory.cs` |
| Copilot runtime host (new) | `src/Agent42.Backend/Copilot/` (client singleton, agent factory, Blob `SessionFsProvider`, registration) |
| Silo hosts (rule 7) | `src/Agent42.Backend/Program.cs` (~l.55-200), `tools/Agent42.DevHost/Program.cs` (~l.35-99), `tests/Agent42.Tests/TestSiloConfigurator.cs` (~l.51-111) |
| Infra | `infra/modules/storage.bicep` (new private container `copilot-sessions`, same shape as `skill-packages`) |
| Build | `Directory.Packages.props`, `src/Agent42.Backend/Dockerfile` (Debian `aspnet:10.0`, non-root `$APP_UID`, publish without `-r`) |
| Frontend | `src/Agent42.Backend/wwwroot/js/agents.js` (editor: research badge ~l.67, continuous lock ~l.146, save ~l.189), `sw.js` (`CACHE` bump) |
| Docs | new ADR (via `/decide`), `.agentic/gotchas.md` entries (via `/gotcha`), CLAUDE.md feature map row |

## Context & constraints

### Interview decisions (user, 2026-10-05)

- **Why Copilot:** the user wants Copilot's agent loop, not just BYOK + MCP.
- **Built-ins:** the user ultimately wants a full coding agent (shell + files). Agreed to **phase it**: this plan ships
  the loop with NO built-in tools; a later plan adds a sandboxed runtime. Rejected for phase 1: running shell/file
  tools in the backend container (it holds Cosmos access, the Data Protection key ring, the managed identity, and
  every user's data) and a same-replica sidecar (shares `localhost` with the backend, so a shell can call the app
  port behind Easy Auth and forge `X-MS-CLIENT-PRINCIPAL-ID` — see `src/Agent42.Backend/Api/CurrentUser.cs:24`).
- **Tools:** the Agent42 toolset (selectable tools + the user's MCP servers) handed to Copilot as custom tools, so
  MCP OAuth per-request refresh (ADR 0025/0081), the local bridge and per-tool selection keep working unchanged.
  Rejected: native Copilot `McpServers` configs (static headers; OAuth servers and the local bridge break).
- **Skills:** Copilot's native `SkillDirectories`, fed the same per-skill selection (decision 17). Rejected: reusing AF `AgentSkillsProvider` output as custom tools.
- **Persistence:** Copilot's session files go to Blob via `SessionFs` (survives deploys). Rejected: Azure Files mount
  (new infra, SQLite on SMB), accepting loss.
- **Surfaces in v1:** per-chat web UI (create in the editor, stream, stop), API key `/api/agent-run` + MCP server
  `/api/agent-mcp` (ADR 0022/0070), scheduled prompts (reminder path). **Not** continuous mode (forced per-chat, like
  research).

### Measured facts (spike 2026-10-05, `Microsoft.Agents.AI.GitHub.Copilot` 1.22.0 → `GitHub.Copilot.SDK` 1.0.14, CLI runtime 1.0.85, Foundry gpt-5-mini via `<resource>/openai/v1/`, `Type="openai"`, `WireApi="responses"`)

- **BYOK needs no GitHub auth.** `GetAuthStatusAsync()` → `{"isAuthenticated":false,...,"statusMessage":"Not authenticated"}`
  and the turn completed with usage. Options used: `CopilotClientOptions { Mode = CopilotClientMode.Empty, BaseDirectory = <dir>, UseLoggedInUser = false }`.
- **`Mode.Empty` exposes no tool not in `AvailableTools`.** Asked to run `whoami`, the model replied it cannot run
  shell commands; no permission request was raised for a shell tool.
- **Custom tools + `Provider` compose**, but the permission handler is asked **for our own custom tool**
  (`PermissionRequestCustomTool`). Denying it means the tool never runs. Handler signature returns
  `Task<GitHub.Copilot.Rpc.PermissionDecision>`: `PermissionDecision.ApproveOnce()` for custom tools,
  `PermissionDecision.UserNotAvailable()` otherwise. (The docs' `PermissionRequestResult` type does not exist in 1.0.14.)
- **AF streaming shape:** `TextContent`, `FunctionCallContent` + `FunctionResultContent` (one each per call),
  `UsageContent` (one per model call), plus many bare `AIContent` (ignore). This maps onto the existing
  `ProjectStreamedContent` arms unchanged.
- **The AF session is only a pointer:** `SerializeSessionAsync` → `{"sessionId":"<guid>","stateBag":{}}`. The real
  transcript is `<BaseDirectory>/session-state/<id>/` (`events.jsonl` ≈22 KB after one tool turn, `session.db`
  SQLite 12 KB, `workspace.yaml`, `checkpoints/index.md`). Resume after a full runtime restart with the same
  directory works (the model recalled a value from turn 1). **With that directory deleted, resume throws**
  `IOException: ... session.resume failed ...: Session not found: <id>`.
- BYOK always disables Copilot session telemetry (SDK doc on `SessionConfigBase.EnableSessionTelemetry`).
- The SDK's MSBuild targets download the runtime from `github.com/github/copilot-cli/releases` at build time for
  the build RID (or `$(NETCoreSdkPortableRuntimeIdentifier)` when no RID is set). Overrides: `CopilotCliBinaryPath`,
  `CopilotSkipCliDownload=true`. **Unverified:** that the framework-dependent `dotnet publish` in the Docker build
  stage (linux-x64 host) copies the runtime + `runtime.node` into `/app`.

### SDK surface the design relies on (from the 1.0.14 XML docs; unverified at runtime unless Step 1 says so)

- `CopilotClientOptions.SessionFs` (`SessionFsConfig`: `SessionStatePath`, `Conventions`, `Capabilities.Sqlite`) +
  `SessionConfigBase.CreateSessionFsProvider` → a **per-session** `SessionFsProvider` subclass (virtual
  read/write/append/exists/stat/mkdir/readdir/remove/rename). SQLite is routed through the provider only when
  `Capabilities.Sqlite` is true and the provider implements `ISessionFsSqliteProvider` (+ optional
  `ISessionFsSqliteTransactionProvider`). "Custom SessionFs providers must serialize writers that access the same
  session from multiple processes" — single replica (rule 12), single runtime, so in-process locking suffices.
- `CopilotSession.AbortAsync` aborts the in-flight message; the session stays usable. `CopilotSession.GetEventsAsync`
  returns the full event list. `CopilotClient.DeleteSessionAsync(id)`.
- `SystemMessage` (`SystemMessageConfig { Mode = Append|Replace|Customize, Content }`) for persona/instructions.
- `ProviderConfig { Type = openai|azure|anthropic, BaseUrl, ApiKey, WireApi = completions|responses, Azure.ApiVersion, MaxPromptTokens }`.
- One `CopilotClient` hosts many concurrent sessions.

### Design decisions (planner; the ADR records them)

1. **Kind is user-selectable on create only, for `copilot` only.** `AgentUpsertRequest` gains an optional `Kind`.
   Insert accepts `copilot` and nothing else (`research` stays seed-only; unknown → 400). Update preserves
   `previous.Kind` exactly as today and ignores the request's Kind. Registry forces `Continuous = false` for copilot
   (same line as research). This **amends ADR 0082** ("Kind is set only by seeding") and **ADR 0016** (per-chat).
2. **The grain never references the Copilot package.** New Agents-layer interface (e.g. `ICopilotAgentFactory` in
   `src/Agent42.Agents/Copilot/`) that, given the user id, conversation key, qualified model id, name, instructions
   and the `AIFunction` list, returns an `AIAgent`. Backend implements it with the SDK; tests and DevHost register a
   fake built on the existing fake/canned chat client. The grain resolves it **optionally**
   (`IServiceProvider.GetService`, the `/api/speech` pattern) so a host without it fails one copilot turn with a
   clear message instead of failing every activation (rule 7). Register it in Backend, DevHost and tests anyway.
3. **Provider settings stay in the Backend.** The factory request carries the `IChatClientFactory` instance the
   grain already holds (resolved at activation, gotcha "The per-user chat-client factory is resolved on grain
   activation", so the overlay version is the one the chat runs on — no re-resolution). The Backend factory
   type-tests it for a new Backend-internal interface (e.g. `ICopilotProviderSource`, implemented by
   `MultiProviderChatClientFactory`) that returns endpoint/kind/decrypted key/UseResponsesApi for a qualified model
   id from its private `Provider` map. No `InternalsVisibleTo`. A factory that does not implement it (fakes) → the
   copilot turn fails with a clear message. The decrypted key is never put in grain state or `SessionConfig` logging.
   Mapping: `AzureOpenAI` → `Type="openai"`, `BaseUrl = <endpoint>/openai/v1/`, `api-key` (verify which `Type`
   authenticates with the Foundry resource key — gotcha "Azure's `/openai/v1` authenticates the resource key via the
   `api-key` header"); `OpenAICompatible` → `Type="openai"`, `BaseUrl = endpoint`; `UseResponsesApi` → `WireApi`.
4. **One `CopilotClient` per silo**, started lazily, `Mode.Empty`, `UseLoggedInUser = false`, no `GitHubToken`,
   `Connection` from config (default stdio child process). Phase 2 swaps it to `ForUri` without touching the grain.
5. **History keeps flowing through the existing `InMemoryChatHistoryProvider` shape.** A delegating `AIAgent`
   wrapper (Agents layer) mirrors each run's user message + response messages (text, tool calls, tool results) into
   the session's in-memory chat history, so `TryGetInMemoryChatHistory` (used by `GetHistoryAsync`, attachments,
   artifacts compaction, retention, the stopped-turn hand-append) works unchanged; chat search, share links,
   follow-ups and tool-call folding (ADR 0080) need no copilot branch. The same storage filters apply. Copilot's own
   transcript lives in Blob and is authoritative for **the model's** context; the mirror is authoritative for **the
   UI**. Step 1 proves `GitHubCopilotAgentSession`'s `stateBag` round-trips the mirror.
6. **Tool callbacks are marshalled onto the grain.** Copilot invokes custom tools from the SDK's RPC thread, not the
   activation's scheduler. Every `AIFunction` handed to Copilot is wrapped so its invocation runs on the captured
   activation `TaskScheduler` (the grain is mid-turn, awaiting the stream, so this interleaves like a normal
   `FunctionInvokingChatClient` callback). The `SessionFsProvider` must be grain-independent (no grain state).
7. **Which tools:** only `AIFunction` tools (drop hosted tools — a copilot chat always uses the SearXNG shims, ADR
   0015), after the existing selection (ADR 0021) and MCP gating. Each name goes into `AvailableTools`. A name the
   runtime rejects is skipped with a warning, never fails the turn (Step 1 measures the charset).
8. **Permission policy:** approve `PermissionRequestCustomTool` only; everything else `UserNotAvailable()`.
   Belt-and-braces with `Mode.Empty`.
9. **Context providers (RAG, memory) and images are EXCLUDED for copilot chats in phase 1** (skills are NOT — see
   decision 17). This is the design, not a fallback. Server side: registering a document (upload or
   `read_web_document` ingest) into a copilot chat is refused with a clear error, and `read_web_document` / `memory`
   are not offered to Copilot. Client side: the composer hides document upload for a copilot chat. Images: a copilot chat is treated as
   no-vision (existing withheld-image notice). The ADR records the exclusion. Step 1 risk 3 may show AF 1.22 can
   attach `AIContextProvider`s to a non-`ChatClientAgent`; if so, that is a **follow-up**, not this plan.
10. **No-tools model:** a copilot agent whose model lacks tool support runs as a plain chat (same rule as research).
11. **Incognito: a copilot chat refuses ephemeral turns.** Ephemerality is turn-scoped (`_ephemeral`, set at the top
    of `ChatAsync`, ADR 0034) while the agent and the Copilot session are built at activation, and Copilot writes its
    transcript to `SessionFs` *during* the turn — so skipping the grain write cannot keep an incognito turn out of
    Blob (same trap as gotcha "The memory provider is built at ACTIVATION but the incognito gate is per-TURN").
    `ChatCoreAsync` returns a clear refusal for an ephemeral turn in a copilot chat **before** any Copilot call. No
    in-memory SessionFs variant.
12. **Deletion:** deleting a copilot chat deletes its Blob prefix and calls `DeleteSessionAsync`. Blob layout
    `copilot-sessions/{userId}/{conversationKey}/...` (key-shape hardening per gotcha "A caller-supplied conversation
    id needs THREE defences").
13. **Egress (rule 13):** no new third party in principle (the runtime calls the user's own provider). Step 1 risk 6
    observes the runtime's outbound connections during a BYOK turn. If anything other than the provider host is
    contacted, it is disabled by config/env if possible; if not, **stop and ask the user** before Step 3 — never ship
    an unrecorded egress.
14. **Telemetry: accepted residual.** The usage dashboard counts tokens from `chat` spans only (gotcha "The usage
    dashboard counts tokens from `chat` spans ONLY"); a copilot turn produces none, so copilot usage does **not**
    appear on the dashboard in phase 1. Tokens still reach the `Done` event from `UsageContent`.
15. **Phase-1 trust boundary (recorded in the ADR).** The runtime is a third-party native binary running inside the
    backend container, with the container's network reach and the reach of any credential the process can see.
    Mitigations: `Mode.Empty` (no built-in tools, measured), deny-all permission policy except our custom tools,
    the SDK's build-time SHA256 check against the release's `SHA256SUMS.txt` (in `GitHub.Copilot.SDK.targets`), and
    the runtime version pinned by the package version (bumped deliberately, like an `OpenAI` bump). Pass the runtime
    an explicit minimal `Environment` (no inherited `AZURE_*`, Cosmos, `IDENTITY_*` variables) — Step 1 verifies the
    child still runs with it. Residual: a runtime compromise is a backend compromise. Phase 2's sandbox removes it.
16. **Copilot session lifecycle.** The live `CopilotSession` behind the AF agent is disposed (agent `DisposeAsync`) on
    grain deactivation and on every path that swaps `_agent` (model switch, tool/MCP rebuild, re-bind). The
    replacement agent resumes the **same** session id from the serialized pointer. `SessionIdleTimeoutSeconds` is set
    as a backstop so an orphaned session cannot live forever in the silo-wide runtime.

17. **Skills use Copilot's NATIVE skill support (user decision, 2026-10-05), not `AgentSkillsProvider`.** The
    copilot path never builds the AF skills provider. It hands Copilot `SessionConfig.SkillDirectories` = the
    **same selected skill directory paths** `BuildAgent` computes today (built-in `SkillCatalog` skills merged with
    the user's materialized uploads, filtered per skill by `ToolCatalog.IsSkillEnabled`, ADR 0048). Empty selection
    → no `SkillDirectories` and skills disabled (`EnableSkills = false`). `IncludedBuiltinSkills` is empty (none of
    Copilot's own skills, which may assume shell/files). Why this is acceptable: what reaches those directories has
    already passed the ADR 0049 upload controls (extension classification, script files dropped, name validation —
    gotcha "Uploaded skills: the framework's own limits are NOT what they look like"), and Copilot has no
    shell/script tool in phase 1, so the AF-specific `run_skill_script` guard has no Copilot equivalent to defend.
    Differences the ADR records: (a) `TruncatingSkillsProvider`'s size bound does not apply — Copilot reads the
    files itself; the bound existed for the 2 MB grain session (ADR 0004), and Copilot's transcript lives in Blob,
    while the mirror records only what is streamed back (skill loads appear as tool calls; their results are
    truncated by the mirror like any tool result — Step 4 must ensure the mirror bounds tool results); (b) Copilot's
    frontmatter/name rules may differ from AF's (`name == directory`) — a skill visible in the picker could be
    invisible to Copilot or vice versa (Step 1 risk 9); (c) **phase 2 consequence**: a sandboxed remote runtime
    cannot see backend-container paths, so phase 2 must materialize the selected skills into the sandbox. (which side each history feature touches)

The UI mirror (decision 5) and Copilot's own transcript (in Blob) are **two records of one chat**. They are allowed
to diverge in these specific ways, and the ADR states each one:

| Feature | Acts on | Copilot transcript | Accepted divergence |
|---|---|---|---|
| Retention pruning (`AgentGrain.Retention.cs`) | mirror only | keeps everything; Copilot compacts its own context (`InfiniteSessions`) | the model may still recall a pruned turn |
| Artifact-argument compaction (gotcha "Artifact tool ARGUMENTS are persisted too") | mirror only | keeps full arguments (in Blob, not in the 2 MB grain) | none visible |
| Stopped-turn hand-append (ADR 0078, `AppendStoppedTurnToSession`) | mirror only | holds whatever the runtime recorded before the abort | the model's view of a stopped turn may differ from the UI's partial reply |
| Attachment entry rewrites (`AgentGrain.Attachments.cs`) | mirror only | n/a (images withheld, decision 9) | none |
| Chat search, share links, follow-ups, tool-call folding | read the mirror | — | none |

Nothing writes back into Copilot's transcript. "Clear chat"/delete removes both.

### Open risks for Step 1 (spike before any production code)

1. Can `SessionFs` carry a session across a runtime restart with the local base directory wiped (the spike's
   failing case)? Is `Capabilities.Sqlite` needed for resume, or is `session.db` reconstructible? Which files does
   the runtime still write locally (config, logs) — point `BaseDirectory` at a temp dir for those.
2. Does `GitHubCopilotAgentSession.stateBag` round-trip an `InMemoryChatHistoryProvider` state written by a wrapper?
3. Can AF 1.22 attach `AIContextProvider`s to a non-`ChatClientAgent`?
4. Tool-name charset the runtime accepts (try `-`, `.`, `:`, long names).
5. Cancellation: does cancelling `RunStreamingAsync` abort the runtime turn (or must the wrapper call `AbortAsync`)?
   Is the session resumable afterwards?
6. Outbound connections of the runtime during a BYOK turn (egress), and whether it still runs with a minimal
   explicit `Environment` (decision 15).
7. Docker: does `docker build` of the Backend produce an image whose `/app` contains a working linux-x64 runtime
   runnable as `$APP_UID`? RSS of the runtime process with **one** and with **ten** concurrent idle sessions
   (gotcha "Container Apps bills the per-replica total").
8. Does a custom tool callback arrive off the caller's synchronization context / task scheduler (decision 6)?
   Measure with the real runtime.
9. **Native skills under `Mode.Empty`** (decision 17): with `SkillDirectories` pointing at a copy of
   `src/Agent42.Agents/Skills/scheduledprompt` plus one uploaded-style skill, (a) which tool name(s) the runtime
   uses to list/load a skill and whether they must be in `AvailableTools`; (b) whether loading a skill or one of its
   resources raises a permission request (e.g. `PermissionRequestRead`/path access) that the deny-all handler would
   refuse — if so, design an approval scoped to paths **inside the selected skill directories only** and record it;
   (c) whether a skill whose frontmatter `name` differs from its folder is loaded; (d) whether a script file in a
   skill folder is executable by any tool available under `Mode.Empty` (expected: no); (e) the size of a skill
   load's tool result as streamed back through AF. If (a)/(b) cannot be made to work without enabling general
   file-read tools, **stop and ask the user**.

**Step 1 is a hard gate.** Its results are written into this spec ("Spike results") and any decision they
contradict is rewritten **before Step 3**. Stop and ask the user if: risk 1 fails (no Blob-backed resume), risk 6
finds egress that cannot be disabled, or risk 7 shows the runtime cannot run in the image. Risk 3 never changes
this plan's scope (decision 9).

### Governing ADRs and gotchas to read first

ADRs: 0082 (Kind, research composition — the closest prior art), 0016, 0068 (retention), 0022, 0070, 0003 (persona
snapshot), 0004 (2 MB session), 0013 (egress), 0018/0019 (per-user providers), 0021 (tool selection), 0025/0081 (MCP
OAuth, reconnect), 0078 (stop), 0080 (tool-call folding), 0012/0075 (reminders, push), 0034 (incognito), 0007/0057
(RAG, memory — excluded here), 0083/0084 (artifacts and their compaction), 0041 (ES modules, `sw.js`).
Line numbers in this spec are approximate (`~l.`); the **symbol names** are the anchors.
Gotchas (`.agentic/gotchas.md`): "A grain's turn-scoped field lives for the ACTIVATION", "An agent tool's RETURN value
is persisted", "A cancelled Agent Framework run persists NOTHING", "The stop handle is NOT a turn-scoped field",
"A storage request filter must EXTEND `IsInjectedOrReplayed`", "The memory provider is built at ACTIVATION but the
incognito gate is per-TURN", "Skipping an incognito turn's index write is NOT enough", "Artifact tool ARGUMENTS are
persisted too", "After a retention prune, REMAP `_ephemeralOrdinals`", "A record parameter a Cosmos document lacks binds to
`null`", "Spiking Agent Framework in a file-based app needs reflection JSON switched back on", "The per-user
chat-client factory is resolved on grain activation", "A user provider's API key is encrypted, never echoed",
"Azure's `/openai/v1` authenticates the resource key via the `api-key` header", "Selectable tools: `null == all`",
"Cosmos grain storage is JSON with `$type`", "The service worker's SHELL is all-or-nothing",
"A scripted whole-file rewrite silently converts the checkout from CRLF to LF".
Exemplars: grain partial `src/Agent42.Agents/Web/AgentGrain.WebTools.cs`; storage abstraction
`src/Agent42.Agents/Attachments/IAttachmentStore.cs` + `InMemoryAttachmentStore.cs` + `src/Agent42.Backend/Attachments/BlobAttachmentStore.cs`;
feature registration `HindsightRegistration` (rule 9); grain test `tests/Agent42.Tests/SharedClusterTests.cs`;
API test `tests/Agent42.Tests/SharedApiFixture.cs`; research tests `tests/Agent42.Tests/ResearchAgentTests.cs`,
`AgentRegistryGrainTests.cs`.

## Spike results (Step 1, 2026-10-05; real output, Foundry gpt-5-mini via `/openai/v1/`, Windows host)

Versions: `Microsoft.Agents.AI(.GitHub.Copilot)` **1.23.0** + `GitHub.Copilot.SDK` **1.0.16** (CLI runtime 1.0.90).
Spike files lived in the session scratchpad (`copilot/a/spike-a.cs`, `copilot/b/spike-b.cs`).

**Version findings (change Step 2):**
- AF **1.22.0 cannot resume a SessionFs-backed session**: its private `CopyResumeSessionConfig` drops
  `CreateSessionFsProvider`, so turn 2 throws
  `InvalidOperationException: CreateSessionFsProvider is required in the session config when CopilotClientOptions.SessionFs is configured.`
  AF **1.23.0**'s `ToResumeSessionConfig` copies every `SessionConfigBase` field (decompiled; then run, see R1). →
  the AF family moves to 1.23.0 (latest stable; `Microsoft.Agents.AI.GitHub.Copilot` 1.23.0 depends on
  `Microsoft.Agents.AI.Abstractions` 1.23.0 anyway).
- The SDK's **bundled-runtime auto-launch fails on Windows** for SDK 1.0.14, 1.0.15 and 1.0.16:
  `Copilot runtime wrapper not found at '...\runtimes\win-x64\native\copilot-runtime.exe'` (the 1.0.90 win32-x64
  tarball contains only `package/copilot.exe`). An explicit `Connection = RuntimeConnection.ForStdio(<output>/runtimes/<rid>/native/copilot[.exe], null)`
  works: `SDK 1.0.15 START OK {"version":"1.0.89","protocolVersion":3}`. → the connection is always explicit
  (decision 4 already wanted it configurable); Step 2 verifies the linux-x64 layout in the image.
- `Mode.Empty` requires `BaseDirectory` or `SessionFs` on the client (`ArgumentException ... neither BaseDirectory nor SessionFs was set`)
  and `AvailableTools` on **every** session/resume config (`... the session config did not specify AvailableTools`).

**R1 SessionFs resume — PASS.** Client with `SessionFs = { SessionStatePath="/state", Conventions=Posix }` +
in-memory provider, `Environment` set:
```
R1 SessionFs ops: append=5, exists=1, mkdir=6, read=5, readdirT=1, rename=1, stat=2, write=2
R1 SessionFs files: /state/events.jsonl(26136), /state/checkpoints/index.md(172), /state/workspace.yaml(192)
R1 local base dir exists=False
R1 WIPED local base dir
R1 resume answer: PINEAPPLE-42 (expect PINEAPPLE-42)
R1 final SessionFs size: 42755 chars over 3 files
```
No `session.db` is created (no SQLite capability needed). ~26 KB `events.jsonl` per tool turn. With `Environment`
set, the client's `BaseDirectory` is never created (it is passed as `COPILOT_HOME` env, which `Environment` replaces).

**R2 stateBag mirror — PASS, with a filter requirement.** `SetInMemoryChatHistory` on a `GitHubCopilotAgentSession`
serializes into `{"sessionId":...,"stateBag":{"InMemoryChatHistoryProvider":{"messages":[...]}}}` and
`R2 mirror after round-trip: 4 msgs`. But the run's response messages carry dozens of bare `AIContent` items that
serialize as `{}` (`"contents":[{},{},{},...`) → **the mirror keeps only `TextContent`/`FunctionCallContent`/`FunctionResultContent`**
(and drops empty text).

**R3 context providers on a non-`ChatClientAgent`:** AF 1.22/1.23 has `AIAgentBuilder.UseAIContextProviders(MessageAIContextProvider[])`
→ `MessageAIContextProviderAgent` (wraps any `AIAgent`; message-based providers only, e.g. `TextSearchProvider`).
Follow-up material only (decision 9 unchanged).

**R4 tool-name charset:**
```
R4 'get_word': ... OK-8            R4 'get-word': ok            R4 'w x64': ok    R4 'w x65': ok
R4 'get.word' THREW IOException: ... session.create failed ...: tool name "get.word" contains invalid characters
R4 'srv:get_word' THREW IOException: ... tool name "srv:get_word" contains invalid charac...
R4 'w x129' THREW InvalidOperationException: Session error: 400 Invalid 'tools[0].name': string too long. ... maximum length 128
```
One bad name fails **session creation** (every turn). → only names matching `^[A-Za-z0-9_-]{1,128}$` are handed
over; others are skipped with a warning (decision 7).

**R5 cancellation — PASS without `AbortAsync`.** `R5 cancel->OCE 7ms, chunks=6`; `R5 next turn: 'OK' in 3267ms`;
events tail of the cancelled turn: `UserMessage,AssistantTurnStart,SessionShutdown,SessionResume,...` (no
`AssistantMessage`). **AF opens (create/resume) a `CopilotSession` at the start of every run and disposes it at the
end** (decompiled `RunCoreStreamingAsync`; events show `SessionShutdown,SessionResume` between every turn).

**R6 egress — PASS.** `R6 runtime remote endpoints: 20.62.58.5:443` = `agent42.services.ai.azure.com` (the
provider). Runtime ran with `Environment = { SystemRoot, TEMP, TMP }` only (Windows); Linux equivalent set in Step 6.

**R7 Docker — PASS.** `docker build -f src/Agent42.Backend/Dockerfile .` (unchanged Dockerfile, framework-dependent
publish) logged `Downloading Copilot CLI 1.0.90 for linux-x64...` and `/app/runtimes/linux-x64/native/` holds
`copilot`, `copilot-runtime`, `libcopilot_runtime.so` and `runtime.node` (both 122 MB; image now 936 MB). A
linux-x64 spike published into a mount and run in that image (`--entrypoint dotnet`, default user) with
`ForStdio("/app/runtimes/linux-x64/native/copilot")`, `Environment = { HOME=/tmp/copilot-home, TMPDIR=/tmp }`:
```
uid=1654 user=app
status: {"version":"1.0.90","protocolVersion":3}
RSS after start: 33 MB
10 concurrent turns in 13998ms: [... KIWI-9 0 | ... KIWI-9 9]
RSS peak during 10 concurrent: 159 MB; after: 159 MB
RSS idle after 3s: 159 MB
```
Sessions are not kept alive between runs (R5), so "ten idle sessions" is not a state the AF integration produces;
the measured cost is ≈160 MB steady once warm. The backend container is `0.5 CPU / 1Gi` (`infra/main.bicep`
`containerMemory`) — recorded in the ADR as a sizing watch item, not changed here.

**Step 2 build facts:** `dotnet add package` does not bump existing CPM pins (AF pins bumped by hand to the
resolved 1.23.0). `Microsoft.Agents.AI.GitHub.Copilot` 1.23.0's `buildTransitive` imports the CLI targets of the
SDK it was **packed** against (`_MicrosoftAgentsAICopilotSdkPackagedVersion` = 1.0.14 → CLI 1.0.85) into every
project that only references the SDK transitively; tests and DevHost downloaded 1.0.85 until they got a direct
`GitHub.Copilot.SDK` reference (then all three: `1.0.90`). Full suite on AF 1.23.0 before any feature code:
`Passed! - Failed: 0, Passed: 1700, Skipped: 1, Total: 1701`.

**R8 tool callback scheduler — marshalling REQUIRED.** Turn started on a `ConcurrentExclusiveSchedulerPair.ExclusiveScheduler`:
`R8 tool ran: thread=12 onExclusive=False syncCtx=null`.

**R9 native skills under `Mode.Empty`:**
- (a) skills need `"skill"` in `AvailableTools`. Without it the model answers from nothing (no skill calls). With it:
  `Available skills: - other-name - fruitfacts - scheduledprompt`.
- (b) no permission request for listing/loading (`asks=[]`).
- (c) a skill whose frontmatter `name` ≠ folder **is** loaded, under the frontmatter name: `skill({"skill":"other-name"})` → `TEAL-3`.
- (d) scripts: not runnable (no tool); the model only narrated.
- (e) the `skill` tool result is a constant `Skill "fruitfacts" loaded successfully. Follow the instructions in the skill context.` (85 chars); the body is injected as context.
- **Resource files are NOT reachable**: asked for a fact in `references/fruit.md`, the model called `skill` 6× and
  ended: `...may I access that local file to read it now, or would you paste its contents here?`

**User decisions on the spike (2026-10-05):** (1) bump the AF family to 1.23.0 in this branch; (2) add an Agent42
custom tool `read_skill_resource` that reads a file only from inside the selected skill directories (path-scoped,
size-capped), offered to Copilot together with `skill` when any skill is selected.

**Gate: PASS** (risks 1, 6, 7 pass; no stop condition hit). Decisions affected: 4 (connection always explicit), 7 (charset
filter), 5 (mirror content filter), 16 (see R5 — no live session between runs), 17 (resources gap).

## Steps

1. **Spike (gate; no production code).** Using `/spike-dotnet` in the scratchpad, pinned to the repo's
   `Microsoft.Agents.AI` version and the resolved Copilot package version, answer Open risks 1–9 (risk 7 runs
   after Step 2's package add, in a throwaway branch build). Record results in a "Spike results" section of THIS
   spec (real output only) and apply the hard-gate rule above. Verify: the spec section exists with pasted output.
2. **Package + build.** `dotnet add src/Agent42.Backend package Microsoft.Agents.AI.GitHub.Copilot` (resolve, never
   recall; CPM pins it in `Directory.Packages.props`). Confirm `dotnet build`, `dotnet test` on Windows and the
   ubuntu CI job still pass (the targets download a runtime per build RID). Adjust the Dockerfile only if Step 1
   showed it necessary. Verify: `dotnet build Agent42.slnx`; `docker build -f src/Agent42.Backend/Dockerfile .`
   and run the runtime binary inside the image as `$APP_UID`.
3. **Kind + registry + API.** Move `AgentKinds` from `src/Agent42.Agents/Research/` to
   `src/Agent42.Agents/StoredAgents/AgentKinds.cs` (not a persisted type; flat namespace unchanged, ADR 0039 — update
   any `ProjectLayoutTests` pin) and add `AgentKinds.Copilot` + `IsCopilot`. `AgentUpsertRequest.Kind` (optional);
   insert accepts only `copilot`; update preserves; Continuous forced false for copilot. Verify:
   `AgentRegistryGrainTests` (new + updated `Insert_CannotClaimAKind` → can't claim research, can claim copilot,
   unknown 400), `AgentApiTests` POST/PUT cases.
4. **Agents-layer seam.** `ICopilotAgentFactory` + request record in `src/Agent42.Agents/Copilot/`; the
   history-mirroring delegating agent (decision 5); the scheduler-marshalling `AIFunction` wrapper (decision 6).
   Verify: unit tests for the mirror (streaming + non-streaming, stopped turn) and the marshalling wrapper (invoked
   from a foreign thread, touches grain state safely) — in a new `tests/Agent42.Tests/CopilotAgentTests.cs`.
5. **Grain composition.** New partial `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs`; `BuildAgent` branches to it
   for copilot-kind chats on tools-capable models (decisions 7, 9, 10). Instructions = the same composed persona +
   rendering hints (no document awareness — decision 9). The selected skill directory paths (decision 17) are
   computed by the same code `BuildAgent` uses today — extract it, don't duplicate it — and passed in the factory
   request; the copilot path never constructs `AgentSkillsProvider`. Ephemeral-turn refusal (decision 11), document-registration
   refusal and no-vision treatment (decision 9), session disposal on deactivation and on every `_agent` swap
   (decision 16). Reminder path (`ReceiveReminder`, non-streaming `RunAsync`) and agent-run path need no branch (they
   go through `_agent`); assert turn-scoped resets in both places (rule 6). Verify: the grain ACs below with the fake
   factory.
6. **Backend runtime host** (`src/Agent42.Backend/Copilot/`, own registration class like `HindsightRegistration`):
   the `CopilotClient` singleton (lazy start, dispose on shutdown, connection from config), the factory (provider
   mapping, decision 3; permission policy, decision 8; `SystemMessage`; `AvailableTools`; minimal runtime
   `Environment` and `SessionIdleTimeoutSeconds`, decisions 15–16; `SkillDirectories`/`EnableSkills`/empty
   `IncludedBuiltinSkills` and any skill-path-scoped permission from Step 1 risk 9, decision 17), and the Blob `SessionFsProvider` behind a storage
   abstraction with an in-memory twin (exemplar: attachments store). Deletion (decision 12). Register in Backend;
   register fakes in DevHost and `TestSiloConfigurator`. Verify: provider-mapping unit tests (no key in logs/state),
   in-memory SessionFs tests, and an **opt-in live test** modelled on `LiveModelChatTests` (skipped without
   `live-model.local.json`) that runs a real copilot turn **with a custom tool call through the grain**, restarts the
   client, wipes the local base dir, and resumes from the SessionFs store.
7. **Infra.** `copilot-sessions` private container in `infra/modules/storage.bicep` (same posture as
   `skill-packages`; the MI's account-scoped grant already covers it — verify, and if an RBAC change is needed, hand
   the command to the user, hard rule 5). Verify: `az bicep build`, and `EasyAuthExclusionTests`/infra tests still pass.
8. **Frontend.** `agents.js`: an agent-type choice on create only (Standard / Copilot), a "copilot" badge, Continuous
   locked for copilot (same as research); hide document upload in a copilot chat's composer (decision 9). Bump
   `CACHE` in `sw.js` (rule 3) and satisfy any existing CACHE-bump guard test. Verify: `AgentEditorAssetsTests` case; manual check in DevHost
   (`dotnet run --project tools/Agent42.DevHost`).
9. **Docs.** `/decide` a new ADR ("copilot agent kind") recording decisions 1–14, the rejected alternatives above,
   the phase-2 sandbox requirement and why sidecar/in-container were rejected, the phase-1 trust boundary (decision
   15), the mirror-vs-transcript divergence table, the RAG/memory/image exclusion, and native skills (decision 17,
   including its phase-2 consequence); it amends 0082 and
   0016, and records egress per rule 13. `/gotcha` for: tool callbacks off the activation scheduler; `Mode.Empty` asks permission for
   custom tools; the AF Copilot session is only a pointer (state lives in `SessionFs`). Add the feature-map row in
   CLAUDE.md. `/sync-docs`.
10. **Gates.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`,
    new-code coverage script.

Parallelizable: Steps 3 and 7 are independent of each other and of 4; Step 8 can follow 3. Steps 4 → 5 → 6 are sequential.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A user can create a copilot agent; research cannot be claimed; unknown kind is rejected; update never changes Kind | `Insert_CanClaimCopilot_NotResearch`, `Insert_UnknownKind_Rejected`, `Update_PreservesKind_AndForcesPerChat` (extended) | `AgentRegistryGrainTests.cs`, `AgentApiTests.cs` |
| 2 | A copilot agent is always per-chat | `Update_PreservesKind_AndForcesPerChat` (copilot case) | `AgentRegistryGrainTests.cs` |
| 3 | Kind `copilot` is snapshotted from all three bind paths | `Bind_CopilotKind_Persists` + agent-run/MCP asserts | `AgentGrainTests.cs`, `AgentApiTests.cs`, `AgentMcpServerTests.cs` |
| 4 | A copilot chat streams text and tool calls through the existing event types and ends with a `Done` carrying tokens | `CopilotTurn_StreamsTextToolCallsAndUsage` | `CopilotAgentTests.cs` |
| 5 | History shows exactly one assistant turn per user turn, with its tool calls, and survives reactivation | `CopilotTurn_HistoryMirrorsTurn_AcrossReactivation` | `CopilotAgentTests.cs` |
| 6 | Stopping a copilot turn persists the partial reply like ADR 0078 | `Stop_CopilotTurn_PersistsPartialReply` | `StopGenerationTests.cs` |
| 7 | A scheduled prompt runs a copilot turn through non-streaming `RunAsync`, mirrors it into history, and resets turn-scoped fields (rule 6) | `ReminderCopilotTurn_RunsMirrorsAndResets` | `CopilotAgentTests.cs` |
| 7b | An agent-run (background) copilot turn resets turn-scoped fields | `AgentRunCopilotTurn_ResetsTurnFields` | `CopilotAgentTests.cs` |
| 8 | Tools handed to Copilot run on the activation scheduler | `CopilotTool_InvokedFromForeignThread_RunsOnGrainScheduler` | `CopilotAgentTests.cs` |
| 9 | Only `AIFunction` tools that passed selection are offered; hosted tools never are | `CopilotTools_RespectSelection_AndDropHostedTools` | `CopilotAgentTests.cs` |
| 10 | A no-tools model runs a copilot agent as a plain chat | `CopilotKind_NoToolsModel_IsPlainAgent` | `CopilotAgentTests.cs` |
| 11 | Provider mapping produces the right `ProviderConfig` per provider kind, and the key never lands in state or logs | `CopilotProviderMapping_*` | `CopilotRuntimeTests.cs` |
| 12 | Permission policy approves custom tools only | `CopilotPermission_ApprovesCustomToolOnly` | `CopilotRuntimeTests.cs` |
| 13 | Session files round-trip through the SessionFs store (in-memory twin) and a deleted chat leaves nothing behind | `CopilotSessionStore_RoundTrips`, `DeleteChat_RemovesCopilotSession` | `CopilotRuntimeTests.cs` |
| 14 | (Opt-in live) a real copilot turn that calls a grain tool resumes after a runtime restart with the local base dir wiped | `LiveCopilot_ToolTurn_ResumesFromSessionFs` | `LiveCopilotTests.cs` |
| 15 | The mirror survives the Cosmos JSON path inside the serialized session (no new persisted grain fields are added) | `CopilotSession_MirrorRoundTripsThroughStorage` | `GrainStorageSerializerTests.cs` |
| 16 | The editor offers the copilot type on create only, locks Continuous, hides document upload in a copilot chat; `CACHE` bumped | `AgentEditorAssetsTests` case | `AgentEditorAssetsTests.cs` |
| 17 | An ephemeral turn in a copilot chat is refused before any Copilot call | `EphemeralCopilotTurn_IsRefused` | `CopilotAgentTests.cs` |
| 18 | Registering a document into a copilot chat is refused; `read_web_document`/`memory` are not offered | `CopilotChat_RefusesDocuments_AndExcludesContextTools` | `CopilotAgentTests.cs` |
| 23 | A copilot chat passes exactly the selected skill directories (built-in + uploaded, per-skill selection) as `SkillDirectories`, never builds `AgentSkillsProvider`, and an empty selection disables skills | `CopilotSkills_PassSelectedDirectories_Only`, `CopilotSkills_EmptySelection_DisablesSkills` | `CopilotAgentTests.cs` |
| 24 | Copilot's own built-in skills are never included | `CopilotSkills_NoBuiltinSkills` | `CopilotRuntimeTests.cs` |
| 25 | (Opt-in live) a real copilot turn loads a selected Agent42 skill natively | `LiveCopilot_LoadsSelectedSkill` | `LiveCopilotTests.cs` |
| 19 | An image in a copilot turn is withheld with the existing notice | `CopilotTurn_WithholdsImages` | `CopilotAgentTests.cs` |
| 20 | A model switch / MCP rebuild mid-chat disposes the old agent and resumes the SAME Copilot session id | `CopilotChat_Rebuild_ResumesSameSession` | `CopilotAgentTests.cs` |
| 21 | Deactivation disposes the live Copilot session | `CopilotChat_Deactivation_DisposesSession` | `CopilotAgentTests.cs` |
| 22 | The runtime is started with the minimal environment and an idle timeout | `CopilotClientOptions_MinimalEnvAndIdleTimeout` | `CopilotRuntimeTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ c9ca74e)

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer.CSharp S3776 at `dotnet build` (error) |
| `dependencies.vulnerable_packages` | 0 | CI package-freshness job |
| build / tests / `dotnet format` / doc-path integrity | must pass | `.github/workflows/pr-checks.yml` |

## Out of scope

- Copilot built-in shell/file/git tools and any sandbox (phase 2 plan: spike ACA dynamic sessions vs a separate
  VNet-integrated Container App; the runtime connection seam from decision 4 is the hand-off point).
- Continuous copilot agents.
- Native Copilot `McpServers` configs, Copilot's own built-in skills (`IncludedBuiltinSkills`), Copilot custom agents, Copilot memory,
  `anthropic` provider type (Agent42 has no Anthropic provider kind).
- Usage-dashboard parity for copilot turns (accepted residual, decision 14).
- RAG documents, long-term memory and images in copilot chats (decision 9; a follow-up if Step 1 risk 3
  shows context providers can attach to a non-`ChatClientAgent`).
- Incognito copilot chats (decision 11).
- Reading history back from Copilot's `events.jsonl` (the mirror covers the UI).

## Review (critic, 2026-10-05)

All findings were accepted and folded in. None were rejected.

| # | Severity | Finding | Resolution |
|---|---|---|---|
| 1 | High | Incognito via an in-memory SessionFs can't work: `_ephemeral` is per-turn, and the Copilot session is built at activation and writes during the turn | Decision 11 now refuses ephemeral turns in a copilot chat (AC17) |
| 2 | High | The dual transcript had no sync rules for retention, artifact compaction, stop hand-append and attachment rewrites | New "Mirror vs Copilot transcript" table; it goes into the ADR. ADRs 0068/0083/0084 and their gotchas added to the reading list |
| 3 | Medium | Copilot session lifecycle undefined (disposal, rebuild paths, runtime leak) | Decision 16; AC20–22; RSS measured with 10 sessions (risk 7) |
| 4 | Medium | Document refusal and image fallback had no tests | Decision 9 now fixes the design; AC18–19 |
| 5 | Medium | Reminder non-streaming path, rule-6 resets and real-runtime tool marshalling were untested | AC7/7b; risk 8; the live test now includes a tool call (AC14) |
| 6 | Medium | Steps 3–8 depended on unresolved Step 1 outcomes; AC15 was "if any" | Step 1 is a hard gate with explicit stop rules; decisions 9/11/13/14 are now fixed; AC15 restated (no new persisted fields) |
| 7 | Medium | Supply chain and in-container trust boundary not recorded for phase 1 | Decision 15 (SHA256 check, version pin, minimal env, residual recorded in the ADR) |
| 8 | Low | `~l.` line anchors | Symbol names declared authoritative |
| 9 | Low | Provider accessor was vague | Decision 3 now specifies the `ICopilotProviderSource` type-test on the grain's own factory instance |
| 10 | Low | `AgentKinds` move was undecided; CACHE guard; dashboard residual not surfaced | Step 3 decides the move; Step 8 covers the guard; decision 14 / Out of scope surface the residual |

## Implementation divergences (2026-10-05, branch `feature/copilot-agent-kind`)

Where the shipped code differs from the plan above, and why. ADR 0085 is the authoritative record of the design.

| # | Plan said | Shipped | Why |
|---|---|---|---|
| 1 | Pin the repo's AF version (1.22.0) | AF family bumped to **1.23.0**; `GitHub.Copilot.SDK` **1.0.16** referenced directly by Backend, tests and DevHost | Spike: AF 1.22 drops `CreateSessionFsProvider` on resume. A transitive SDK reference downloads the CLI of the AF package's packed SDK (1.0.85). User-approved |
| 2 | Decision 4: connection "from config (default stdio)" | Always an explicit `RuntimeConnection.ForStdio(runtimes/<rid>/native/copilot)`; `Copilot:RuntimePath` overrides | The SDK's bundled launch failed in a file-based build; explicit stdio is measured in the image |
| 3 | Decision 16 / AC21: dispose the live Copilot session on deactivation and every `_agent` swap | **Dropped**; only the idle-timeout backstop remains | Spike R5: AF creates/resumes the `CopilotSession` at the start of every run and disposes it at the end, so nothing is live between runs |
| 4 | Decision 17: skills natively, no Agent42 tool | Native skills **plus** an Agent42 `read_skill_resource` scoped to the selected skill folders | Spike R9: resource files are unreachable under `Mode.Empty`. User-approved |
| 5 | Decision 5: mirror = user message + response messages | Mirror stores the user message, tool calls/results, and **one** assistant message holding the run's whole streamed text; only Text/FunctionCall/FunctionResult are kept; each mirrored tool result is capped at 16 KB | Copilot narrates around tool calls (several assistant messages per turn), and dozens of bare `AIContent` serialize as `{}` |
| 6 | (not planned) | `CopilotChatAgent` seeds an empty mirror on create/deserialize | A stopped first turn was otherwise dropped (no in-memory history yet). Found by `Stop_CopilotTurn_PersistsPartialReply`; recorded as a gotcha |
| 7 | (not planned) | `CopilotUnavailableAgent` stand-in that round-trips `{sessionId, stateBag}` | A missing factory or unsupported provider must not orphan the transcript pointer or empty the chat on the next serialize |
| 8 | (not planned) | Extra hardening in the session config: config/instruction discovery, file hooks, git context, cross-session store and large-output spill all OFF | Found in the SDK surface; each reads the host filesystem or a shared store |
| 9 | Step 8: hide document upload in a copilot chat | The composer refuses a document with a notice (the existing incognito/embedding gate shape); the server refuses it at both endpoints before ingestion; the incognito toggle is locked off for a copilot chat | Consistent with the existing gates; images still upload and are withheld server-side |
| 10 | Step 4 tests in `CopilotAgentTests.cs` | Seam unit tests in `CopilotSeamTests.cs`; grain ACs in `CopilotAgentTests.cs` (AC3's grain half there too) | Unit tests need no cluster |
| 11 | AC20 "resumes the SAME Copilot session id" | Proven live (`LiveCopilot_ToolTurn_ResumesFromSessionFs`, runtime restarted); the offline test `CopilotChat_Rebuild_KeepsTheSession` proves the session survives a rebuild on the twin | The twin has no session id to compare |
| 12 | (not planned) | ADR 0039/0040 folder inventories corrected (`Artifacts/` was already missing; `Copilot/` added), and ADR 0039's partials list brought in line with `ProjectLayoutTests` | In-place correction rule of those ADRs |
| 13 | Manual DevHost check of the editor UI | HTTP flow checked in DevHost (create, research 400, stream, incognito refusal, mirrored history). The visual browser check was **not done** (browser extension not connected) | — |

Not verified live: `WireApi = "completions"` (only `"responses"` was measured against Foundry), and an OpenAI-compatible provider.

### AC → shipped test names (where the plan's names changed)

| AC | Plan's test | Shipped test |
|---|---|---|
| 1 | `Insert_CanClaimCopilot_NotResearch`, `Insert_UnknownKind_Rejected`, `Update_PreservesKind_AndForcesPerChat` | `AgentRegistryGrainTests.Insert_CanClaimCopilot_NotResearch` (theory: research, unknown, copilot), `Update_PreservesCopilotKind_AndForcesPerChat`; `AgentApiTests.CreateAgent_Kind_CopilotAcceptedOthersRejected`, `UpdateAgent_IgnoresKind` |
| 3 | `Bind_CopilotKind_Persists` + agent-run/MCP asserts | `CopilotAgentTests.Bind_CopilotKind_PersistsAndBuildsTheCopilotAgent`, `AgentApiTests.AgentRun_CopilotAgent_BindsItsKind_AndRefusesDocuments`, `AgentMcpServerTests.AskAgent_CopilotAgent_BindsItsKindAndReplies` |
| 4 | `CopilotTurn_StreamsTextToolCallsAndUsage` | `CopilotAgentTests.CopilotTurn_StreamsToolCallsAndText_ToolRunsOnTheGrain`; token totals on `Done` asserted live in `LiveCopilot_ToolTurn_ResumesFromSessionFs` |
| 5 | `CopilotTurn_HistoryMirrorsTurn_AcrossReactivation` | same name, plus `CopilotSeamTests.Mirror_*` |
| 7b | `AgentRunCopilotTurn_ResetsTurnFields` | `AgentRunCopilotTurn_PersistsAndResetsTurnFields` |
| 8 | `CopilotTool_InvokedFromForeignThread_RunsOnGrainScheduler` | same name (grain), plus `CopilotSeamTests.SchedulerBound_InvokedFromForeignThread_RunsOnTheCapturedScheduler`; mutation-checked |
| 13 | `CopilotSessionStore_RoundTrips`, `DeleteChat_RemovesCopilotSession` | same names, plus `DeleteConversation_RemovesOnlyThatConversation`, `CopilotSessionPaths_RefuseTraversal` |
| 20 | `CopilotChat_Rebuild_ResumesSameSession` | `CopilotChat_Rebuild_KeepsTheSession` (twin) + the live restart test (see divergence 11) |
| 21 | `CopilotChat_Deactivation_DisposesSession` | **dropped** (divergence 3; decision 16 superseded by spike R5) |
| 23 | `CopilotSkills_PassSelectedDirectories_Only`, `CopilotSkills_EmptySelection_DisablesSkills` | `CopilotAgentTests.CopilotSkills_PassSelectedDirectories_Only` (both halves); `CopilotRuntimeTests.CopilotSkills_NoBuiltinSkills_AndSkillToolOnlyWithFolders` |
| 24 | `CopilotSkills_NoBuiltinSkills` | `CopilotRuntimeTests.CopilotSkills_NoBuiltinSkills_AndSkillToolOnlyWithFolders` |
| 16 | `AgentEditorAssetsTests` case | `AgentEditor_OffersCopilotOnCreateOnly_AndGatesItsChat` |
