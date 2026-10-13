---
id: 0085
date: 2026-10-05
status: accepted
tags: [copilot, stored-agents, agent-framework, github-copilot-sdk, sessionfs, blob, byok, tools, skills, egress, trust-boundary, experimental-api, no-migration]
supersedes: []
amends: [0082, 0016, 0034, 0049]
amended_by: [0097]   # 0097 agent fleet: fleet Copilot agents keep built-in tools
---

# A user-created "copilot" agent kind that runs the GitHub Copilot SDK's agent loop (phase 1: no built-in tools)

> **Amended by [ADR 0097](0097-agent-fleet-recursive-teams.md) (2026-10-12): for agent fleet tasks only, Copilot
> agents keep their built-in tools (shell, file writes, URL fetch) in the backend container.** They use a separate
> default-mode `CopilotClient` with no `SessionFs`, per-action user approval and a kill switch. This ADR's
> `Mode.Empty` client and phase-1 boundary are unchanged for the copilot agent kind.

## Context

The user wants an agent that runs GitHub Copilot's own agent loop (its planning and context compaction), not just
"BYOK + MCP", which Agent42 already does natively. Their end goal is a full coding agent with shell and files. The
SDK (`Microsoft.Agents.AI.GitHub.Copilot` → `GitHub.Copilot.SDK`) drives a native runtime process and exposes it as an
AF `AIAgent` (`GitHubCopilotAgent`). A spike on 2026-10-05 (`.agentic/specs/completed/plan-copilot-agent-kind.md`,
"Spike results") measured BYOK without GitHub sign-in, `CopilotClientMode.Empty`, custom tools, `SessionFs`, the
tool-name charset, cancellation, native skills, egress, and the runtime inside the image. Five of those results
shaped the design. AF 1.22 cannot resume a SessionFs session. The AF session is only a pointer. Tools are called
off the grain. One bad tool name fails every turn. Skill resource files are unreachable.

## Decision

1. **A second agent `Kind`, `copilot`, chosen on create only** (amends ADR 0082's "Kind is set only by seeding").
   `AgentUpsertRequest.Kind` accepts null or `copilot`. Research and unknown kinds get a 400. Update preserves the
   stored kind in both directions. The registry forces `Continuous = false` (amends ADR 0016), as it does for
   research.
2. **The grain never references the SDK.** `ICopilotAgentFactory` (Agents layer) builds an `AIAgent` from the user
   id, conversation, the grain's OWN per-user chat-client factory, model, persona, tools and skill folders. The
   Backend implements it on the SDK. Tests and DevHost use an in-process twin (`ChatClientCopilotAgentFactory`).
   The grain resolves it optionally, so a host without one refuses copilot turns instead of failing activations.
3. **Provider settings stay in the Backend.** `MultiProviderChatClientFactory` implements `ICopilotProviderSource`,
   and the copilot factory type-tests the grain's own factory instance for it. Foundry maps to `Type="openai"` at
   `<endpoint>/openai/v1/`, with `WireApi` `responses`/`completions` following ADR 0014. OpenAI-compatible maps to
   `Type="openai"` at its endpoint. The decrypted key lives in memory only, and `CopilotProviderSettings.ToString`
   omits it.
4. **One `CopilotClient` per silo**: `Mode.Empty`, `UseLoggedInUser = false`, an explicit stdio connection to the
   `runtimes/<rid>/native/copilot` binary, a minimal `Environment`, `SessionIdleTimeoutSeconds` as a backstop, and
   `SessionFs`. Phase 2 swaps the connection for a remote sandbox.
5. **Two records of one chat.** Copilot's transcript lives in `SessionFs`, a private Blob container
   `copilot-sessions`, at `{escaped userId}/{escaped conversationId}/`. All files are append blobs, and paths from
   the runtime are normalised with `..` refused. The model sees that transcript. The UI sees a **mirror**:
   `CopilotChatAgent` (a `DelegatingAIAgent`) writes each finished run into the session's in-memory history, as
   the user message, the tool calls and results, and ONE assistant message holding the run's whole streamed text.
   Every existing history feature reads the mirror unchanged.
6. **Tools are Agent42's**: the same `SelectableTools` an ordinary chat gets (selection and MCP gating), functions
   only, with the SearXNG shims instead of hosted tools. Each is bound to the activation scheduler
   (`SchedulerBoundAIFunction`). Names outside `^[A-Za-z0-9_-]{1,128}$` are skipped with a warning.
7. **Permission policy**: approve `PermissionRequestCustomTool`, refuse everything else (`UserNotAvailable`).
8. **Skills are Copilot-native**: the selected folders (built-in and uploaded, ADR 0048/0049) go to
   `SkillDirectories`, with `skill` in `AvailableTools` and `IncludedBuiltinSkills = []`. Agent42 adds
   `read_skill_resource`, which reads only inside a selected skill's folder and is capped at
   `SkillToolLimits.MaxResourceTextLength` (user decision, 2026-10-05).
9. **Excluded in phase 1**: RAG documents (refused at both upload paths before ingestion, and ignored by the
   grain), long-term memory, `read_web_document`, images (withheld with the ordinary notice), and incognito turns
   (refused before anything runs, amending ADR 0034 for this kind). A copilot agent on a no-tools model is a plain
   chat.
10. **Deleting a chat deletes its transcript prefix.**
11. **The AF family moves to 1.23.0**, pinned together. `GitHub.Copilot.SDK` 1.0.16 (CLI runtime 1.0.90) is
    referenced directly by Backend, tests and DevHost.

## Alternatives considered

- **Native Copilot `McpServers` instead of Agent42's tools as custom tools.** Lost: static headers, so MCP OAuth
  per-request refresh (ADR 0025/0081), the local bridge (ADR 0076) and per-tool selection (ADR 0058) would all break.
- **Shell and file tools in phase 1, in the backend container.** Lost: the container holds Cosmos access, the Data
  Protection key ring, the managed identity and every user's data.
- **Shell and file tools in a same-replica sidecar.** Lost: the sidecar shares `localhost` with the backend, so a
  shell could call the app port behind Easy Auth and forge `X-MS-CLIENT-PRINCIPAL-ID` (`src/Agent42.Backend/Api/CurrentUser.cs`).
- **Accept losing the transcript on deploy** (the runtime's local `session-state/`). Lost: measured, resume after
  wiping it throws `Session not found`, and every deploy wipes it.
- **An Azure Files mount for the runtime's state.** Lost: new infra, and SQLite over SMB. The spike also showed no
  `session.db` is created under `SessionFs`, so Blob through `SessionFs` is enough.
- **Read history back from Copilot's `events.jsonl`** instead of a mirror. Lost: every history feature (search, share
  links, follow-ups, tool folding, retention, attachments, stop) would need a copilot branch. With a mirror none do.
- **Reuse AF `AgentSkillsProvider` output as custom tools.** Lost: the user chose Copilot-native skills. The one
  gap, resource files, is covered by `read_skill_resource`.
- **Enable Copilot's general file tools for skill resources.** Lost: it would hand the runtime the container's
  filesystem.
- **Stay on AF 1.22.0.** Not possible. Its private `CopyResumeSessionConfig` drops `CreateSessionFsProvider`, so turn
  2 throws `CreateSessionFsProvider is required in the session config when CopilotClientOptions.SessionFs is configured`.
- **Let the SDK auto-launch its bundled runtime.** Lost: in a file-based build it failed with
  `Copilot runtime wrapper not found`. An explicit stdio connection is measured in the image, and phase 2 needs the
  seam anyway.
- **An in-memory `SessionFs` for incognito turns.** Lost: incognito is per TURN, while the Copilot session (and its
  `SessionFs`) is per activation, and Copilot writes during the turn.

## Consequences

- **Phase-1 trust boundary (recorded, not removed).** The runtime is a third-party native binary inside the backend
  container, with that container's network reach. Mitigations: `Mode.Empty` (no built-in tool, measured), the
  deny-all-but-custom-tools permission policy, a minimal environment so the child never inherits the identity,
  Cosmos or storage variables (measured working: Windows `SystemRoot/TEMP/TMP`, Linux `HOME/TMPDIR`), the SDK's
  build-time SHA256 check, and the runtime version pinned by the package version (bump it deliberately). Host
  config, instruction discovery, file hooks, git context, the cross-session store and the large-output temp-file
  spill are all turned off. Residual: a runtime compromise is a backend compromise. **Phase 2** (a separate plan)
  moves the runtime into a sandbox (ACA dynamic sessions or a separate VNet-integrated app), and must then
  materialize the selected skills into it, because a remote runtime cannot see backend paths.
- **Egress (ADR 0013): no new third party.** Measured during a BYOK turn, the runtime's only remote endpoint was
  the user's provider (`20.62.58.5:443` = the Foundry resource). BYOK disables Copilot session telemetry (SDK doc).
- **Mirror vs transcript divergence (accepted).** Retention pruning (ADR 0068), artifact-argument compaction
  (ADR 0084), the stopped-turn hand-append (ADR 0078) and attachment rewrites act on the mirror only. Copilot keeps
  its full transcript and compacts its own context, so the model may recall a pruned turn, or see a stopped turn
  differently from the UI's partial reply. Nothing writes back into the transcript. A switch between a tools model
  and a no-tools model swaps the session shape, and the Copilot transcript pointer does not survive it. The mirror
  does.
- **Two runs on one conversation** (a reminder firing mid-chat) resume the same Copilot session concurrently. Writes
  are serialized per conversation, so there is no torn file, but the transcript order across the two runs is
  undefined. Accepted for phase 1.
- **Usage dashboard**: copilot turns produce no `chat` spans, so they don't appear there (accepted residual). Tokens
  still reach `Done` from `UsageContent` (measured in the live test).
- **Sizing watch item**: the runtime adds about 160 MB steady RSS once warm (33 MB at start, 159 MB with 10
  concurrent turns, measured in the image). The backend container is `0.5 CPU / 1Gi`. The image grew to 936 MB
  (`runtime.node` and `libcopilot_runtime.so` are the same 122 MB binary, copied twice).
- **No grain-level session lifecycle.** AF creates or resumes the `CopilotSession` at the start of every run and
  disposes it at the end (measured, events show `SessionShutdown,SessionResume` between turns), and a cancelled run
  disposes it too. So nothing needs disposing on deactivation or agent rebuild.
- `[Experimental]` exposure: `GHCP001` (SDK SessionFs and permission RPC types) is suppressed per file in the Backend
  copilot folder.
- Follow-ups: RAG for copilot chats (AF has `UseAIContextProviders(MessageAIContextProvider[])` for any agent, so
  message-based providers like `TextSearchProvider` could attach), images, usage-dashboard parity, phase 2.
