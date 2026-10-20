planned-against: main @ 74ea5e3

# Plan: Copilot chats get the runtime's built-in tools, with an Azure Files workspace

## Overview

Copilot-kind chats (ADR 0085) run the Copilot runtime in `CopilotClientMode.Empty`: no shell, no file tools, no
URL fetch. The user wants every Copilot chat to have all of the runtime's built-in tools, auto-approved, as the
agent fleet's Copilot agents already do (ADR 0097 decision 7). A shell needs a real directory, and a chat's
transcript must survive deploys, so this plan keeps the transcript in Blob (`SessionFs`, unchanged keys) and puts
each conversation's working directory on an Azure Files share mounted into the backend container. A routing
`SessionFs` provider sends the state path to Blob and everything else to the real disk.

## Affected areas

Verified against the tree at 74ea5e3.

- `src/Agent42.Backend/Copilot/CopilotAgentFactory.cs`: client mode, `SessionFs` config, session config, permission
  policy, environment, `DeleteConversationAsync`.
- `src/Agent42.Backend/Copilot/CopilotSessionFsProvider.cs`: becomes (or is wrapped by) a path-routing provider.
- `src/Agent42.Backend/Copilot/CopilotRegistration.cs`: new options (`Copilot:BuiltInToolsEnabled`,
  `Copilot:WorkspaceRoot`).
- New: `src/Agent42.Backend/Copilot/CopilotPermissionPolicy.cs` (or a static method beside `DecidePermission`), plus
  a workspace-path helper next to `CopilotSessionPaths` in `src/Agent42.Backend/Copilot/ICopilotSessionStore.cs`.
- `src/Agent42.Backend/appsettings.json`: the `Copilot` section with the kill switch (there is none today).
- `infra/main.bicep`, `infra/modules/container-app.bicep`, new `infra/modules/copilot-files.bicep` (storage account +
  share) and a module scoped to `rg-hosting` for `Microsoft.App/managedEnvironments/storages`.
- `src/Agent42.Backend/wwwroot/index.html:317`: the agent-editor note about what a Copilot agent can do.
- Tests: `tests/Agent42.Tests/CopilotRuntimeTests.cs`, new `tests/Agent42.Tests/CopilotPermissionPolicyTests.cs`, new
  `tests/Agent42.Tests/CopilotWorkspaceFsTests.cs`, `tests/Agent42.Tests/LiveCopilotTests.cs`,
  `tests/Agent42.Tests/AgentEditorAssetsTests.cs` (if the copy it pins changes).
- Docs: new ADR (next number, after 0106), `.agentic/decisions/0085-copilot-agent-kind.md` (`amended_by`),
  `.agentic/gotchas.md` (entries at "The fleet's Copilot client is NOT ADR 0085's client" and "A Copilot AF session is
  only a POINTER"), `docs/features/custom-agents.md:171-175`, the `CLAUDE.md` feature-map Copilot row.

Unchanged by design: `src/Agent42.Agents/**` (the grain's tool list, `CopilotFunctions`, exclusions), the in-process
twin `ChatClientCopilotAgentFactory` (DevHost and the test silo keep it: no runtime, so no built-ins), the fleet's
`FleetCopilotHost` and its client.

## Context & constraints

**User decisions (2026-10-19):**
- All Copilot chats get the built-ins (not per-agent opt-in), behind a server-wide kill switch.
- Auto-approve, with no prompts: shell and URL fetch run unattended (reminder and background turns included).
  Built-in file reads and writes are approved only inside the conversation's own workspace directory.
- Keep Blob for the transcript and everything else. Azure Files is for Copilot chat workspaces only.
- Accepted risk: a shell can read every conversation's workspace files on the share (not transcripts, which stay in
  Blob). Today only two principals can sign in, both the owner (Easy Auth `allowedPrincipals`), so this is
  cross-conversation more than cross-user. It becomes cross-user if a third principal is ever admitted, and the ADR
  must say so.
- The fleet's other accepted risks carry over to chat: a shell can reach the app on `localhost` and forge
  `X-MS-CLIENT-PRINCIPAL-ID` (`src/Agent42.Backend/Api/CurrentUser.cs`), it can read the container filesystem, and it
  has the container's network reach. The last is an ADR 0013 egress extension and must be recorded as one.
- **The shell can get the app's managed identity (raised by the critic on 2026-10-19; the user must confirm it
  before approval; the fleet already carries it unstated).** The runtime runs under the backend's own uid (`USER $APP_UID`,
  `src/Agent42.Backend/Dockerfile:25`). Its shell can therefore read `/proc/<backend pid>/environ`, which holds
  `IDENTITY_ENDPOINT` and `IDENTITY_HEADER`, and use them to request managed-identity tokens. That means Cosmos and
  Blob data-plane access to ALL app data: grain state, other chats' transcripts, attachments and the Data Protection
  key ring. `MinimalEnvironment()` keeps those variables out of the runtime's own environment but cannot stop this.
  Shell runs unattended, including reminder and background turns, so a prompt injection from a fetched page reaches
  the shell with no user present. The ADR states all of this, and amends ADR 0097's risk list with a cross-reference.

**Spike evidence (2026-10-19, GitHub.Copilot.SDK 1.0.16, Windows host, live Foundry gpt-5-mini; spike files were in
the session scratchpad and are gone):**
1. `CopilotCli` mode with today's `SessionFs` (`InitialWorkingDirectory="/work"`, Posix): the built-in file tools went
   through `SessionFs` into the store (`u/c/state/files/spike-hello.txt`). The shell ran on the real host with an
   invalid cwd and every command failed with `The directory name is invalid. (os error 267)`. Setting
   `SessionConfig.WorkingDirectory` to a real directory did NOT fix the shell while `InitialWorkingDirectory` was
   virtual. A create under the state path raised NO Write permission.
2. Hybrid, which is the design this plan uses: `InitialWorkingDirectory` = a REAL root directory, a per-session
   `WorkingDirectory` = a real per-conversation subdirectory, and a provider routing `SessionStatePath/**` to a store
   and every other path to the real disk. For two conversations, the file tool wrote `c1\note.txt` and `c2\note.txt`
   on disk. Each shell's cwd was its own subdirectory, and `Get-Content note.txt` printed `c1` and `c2`. The
   transcript (`events.jsonl`, `workspace.yaml`, `checkpoints/index.md`) went only to the store, and the state path
   never existed on disk. The create raised `PermissionRequestWrite` with the real path. The shell permission
   request's `ResolvedWorkingDirectory` was EMPTY, so shell commands cannot be path-checked. No SQLite was opened.
3. Not measured: Linux/Posix in the container, SMB semantics, resume after a runtime restart in the hybrid shape, and
   resume of an EXISTING chat whose `workspace.yaml` was written with cwd `/work` (see step 1).

**Constraints and governing docs:**
- ADR 0085 (`.agentic/decisions/0085-copilot-agent-kind.md`) decisions 4, 6, 7 and 9, and its alternatives list,
  which rejected "Shell and file tools in phase 1, in the backend container" and "An Azure Files mount for the
  runtime's state". This plan amends 0085 for chat the way 0097 decision 7 did for fleet. The Azure Files mount here
  holds workspaces only, NOT runtime state, so 0085's SQLite-over-SMB objection does not apply. Say so in the ADR.
- ADR 0097 decision 7 and `src/Agent42.Backend/Fleet/FleetCopilotPermissionPolicy.cs`: the policy shape to copy,
  minus the asking.
- Gotcha "A fleet Copilot `SessionConfig` must NOT set `AvailableTools`": the same now applies to chat when built-ins
  are on. `AvailableTools` restricts the session to the named tools and would hide the built-ins. Gotcha "A Copilot
  AF session is only a POINTER" says that under `Mode.Empty` every session AND resume config must carry
  `AvailableTools`, so that requirement remains on the kill-switch-off path.
- Gotcha "The fleet's Copilot client is NOT ADR 0085's client: don't merge them": still true. The chat client
  changes mode but stays a separate client, because it has `SessionFs` and the fleet's does not.
- Keep the existing Blob keys: `SessionStatePath` stays `/state` and the prefix stays
  `CopilotSessionPaths.ConversationPrefix`, so existing chats' transcripts resume. The state path is routed by prefix
  and never touches the disk.
- Storage auth: the attachments account is key-free by design (`infra/modules/storage.bicep`,
  `allowSharedKeyAccess: false`, ADR 0004). Container Apps SMB mounts authenticate with the account key (keyless
  Azure Files with a managed identity is a preview, and its support for Container Apps is unconfirmed). So the share
  goes on a SEPARATE storage account with shared-key access, used for nothing else. Its key reaches the environment
  storage through `listKeys()` in Bicep, so no RBAC change is needed (hard rule 5 untouched).
- The Container Apps environment `cae-hosting` lives in `rg-hosting` (`infra/main.bicep:14-17`) and is shared with
  other apps. The deploy SP is Contributor on `rg-hosting`, so a Bicep module scoped there can create
  `managedEnvironments/storages`. Adding a storage link is additive. Name it distinctly (e.g. `agent42-copilot`).
- Rule 12 (single replica): the share is mounted into one replica, so the in-process write gates stay sufficient.
- Rule 9 (cognitive complexity 15): keep the routing provider and the policy as small switch-shaped methods.
- Hard rule 4: no new packages are expected. If one is needed, resolve it with `dotnet add package`.

## Steps

1. **Measure the open questions on Linux (spike, before code).** Use `/spike-dotnet` in the container image, or WSL
   with the linux-x64 runtime, pinned to GitHub.Copilot.SDK 1.0.16. Measure these in the hybrid shape:
   (a) Posix `InitialWorkingDirectory` = a real dir, per-session `WorkingDirectory`: shell and file tools agree;
   (b) resume after a runtime restart, with state only in the store;
   (c) resume of a session whose `workspace.yaml` was created under today's shape (`/work`). Does the new
   `WorkingDirectory` win, or does the shell inherit `/work` and fail?
   (d) the kill switch flipped both ways: a session created in one mode, resumed in the other (built-in tool calls in
   the transcript, resumed under `Mode.Empty` with `AvailableTools`).
   **If (c) fails, the default fallback is code, not a decision:** the router rewrites the `cwd` value `/work` in
   `/state/workspace.yaml` to the conversation's workspace when it is read, with a unit test (AC 9). Ask the user only
   if that rewrite proves impossible. If (d) fails, the ADR records that flipping the switch breaks resumes of chats
   that used the other mode, and the switch is documented as "restart, and expect those chats to fail".
   Verify: the spike prints expected vs actual for (a) to (d).
2. **Options and kill switch.** Add `BuiltInToolsEnabled` (default `true`) and `WorkspaceRoot` to
   `CopilotRuntimeOptions`. Bind them in `CopilotRegistration` from `Copilot:BuiltInToolsEnabled` and
   `Copilot:WorkspaceRoot`. The default root is a directory under `Path.GetTempPath()`, so local runs and live tests
   work without a mount. Add the `Copilot` section to `appsettings.json` with a comment, following the
   `Fleet:CopilotAgentsEnabled` precedent (`src/Agent42.Backend/Fleet/FleetOptions.cs`, `appsettings.json:75-79`).
   Verify: `CopilotOptions_BindFromConfig` in `CopilotRuntimeTests.cs` binds both keys.
3. **Workspace paths.** Add a helper beside `CopilotSessionPaths` that maps (userId, conversationId) to
   `{WorkspaceRoot}/{segment(userId)}/{segment(conversationId)}`. **`Uri.EscapeDataString` (today's prefix escaping)
   leaves `.` and `..` unchanged**: that is harmless as a Blob key, but on disk `..` escapes the root. So the helper
   must reject (throw on) empty, `.` and `..` segments, and segments with a trailing dot or space, because Windows
   and SMB strip those. It must also canonicalize with `Path.GetFullPath` and assert the result lies strictly under
   `WorkspaceRoot`. The SAME helper produces the `WorkingDirectory`, the policy root, the router root and the delete
   target, so no caller builds the path itself.
   Verify: `CopilotWorkspaceFsTests` with ids `..`, `.`, `a/b`, `a\b`, `x.`, and a per-conversation directory.
4. **Routing SessionFs provider.** Paths under `SessionStatePath` keep today's Blob behaviour, byte-identical keys.
   Every other path goes to the real disk, and is REFUSED unless it lies inside the conversation's workspace
   directory. That second check backs up the permission policy, because a create under the state path raised no
   permission at all in the spike. Keep the per-conversation write gate for the state side. Prefer composition:
   factor the existing Blob logic so the router delegates to it, without duplicating it. Classify a path only after
   normalizing it for the host conventions (separators, drive letter, `\state` vs `/state`): a mis-classified state
   path would put the transcript on disk and break resume. The disk side implements every `SessionFsProvider`
   operation (read, write, append, exists, stat, mkdir, readdir, readdir-with-types, remove, rename) and throws the
   same exception types as the store side (`FileNotFoundException`). `CreateAgent` passes the conversation's
   workspace to the router alongside today's store, prefix and gate. Path comparison follows the OS:
   case-insensitive on Windows, ordinal on Linux. Don't copy the fleet's `OrdinalIgnoreCase` blindly. Symlinks: SMB
   has none, but a local root can, so the disk side resolves a link target and refuses one outside the workspace.
   Verify: `CopilotWorkspaceFsTests`. A state write lands in `InMemoryCopilotSessionStore` under the same key as
   today. Workspace write, stat, readdir, rename and remove work on disk. A path outside the workspace and outside
   state throws. A symlink to outside is refused. Existing `CopilotRuntimeTests` SessionFs tests still pass.
5. **Client and session config.** In `BuildClientOptions`, when `BuiltInToolsEnabled`: use `CopilotClientMode.CopilotCli`,
   `SessionFs.InitialWorkingDirectory = WorkspaceRoot` (real), `SessionStatePath = "/state"`, and conventions from
   the host OS. Use the environment `MinimalEnvironment()` + `PATH`, reusing the fleet's helper rather than copying
   it. When off: exactly today's options (`Mode.Empty`, `/work`, `MinimalEnvironment()`). In `BuildSessionConfig`,
   when on: set `WorkingDirectory` to the conversation's workspace, create the directory before the session, and
   leave `AvailableTools` UNSET. Keep everything else as today (custom tools, skills, discovery off, no large-output
   spill, `IncludedBuiltinSkills = []`). When off: today's config, `AvailableTools` included.
   Verify: `CopilotRuntimeTests` pins both modes. Extend `CopilotClientOptions_MinimalEnvAndIdleTimeout` (its
   `Environment.Count <= 3` assertion changes for the on path) and `CopilotSkills_NoBuiltinSkills_AndSkillToolOnlyWithFolders`.
6. **Permission policy.** Approve `PermissionRequestCustomTool`, `PermissionRequestShell` and `PermissionRequestUrl`.
   Approve `PermissionRequestRead` and `PermissionRequestWrite` only when the path is inside the conversation's
   workspace, and reject them otherwise with a message naming the workspace. Reject everything else (MCP, memory,
   hooks, extensions: no native MCP servers are configured; Agent42's MCP tools arrive as custom tools). Shell cannot
   be path-checked (`ResolvedWorkingDirectory` is empty, spike 2). A Read or Write with a null path is rejected. The
   policy is a per-session INSTANCE built with the conversation's workspace (like
   `FleetCopilotPermissionPolicy(workingDirectory, …)`), not a static method, and it uses the step 3 helper and the
   step 4 comparison rules. When the switch is off: today's `DecidePermission`.
   Verify: `CopilotPermissionPolicyTests`, modelled on `FleetCopilotPermissionPolicyTests.cs`, with no
   interaction/asking.
7. **Delete.** `DeleteConversationAsync` also deletes the conversation's workspace directory (best-effort, as today)
   under the gate, ONLY through the step 3 helper, which refuses `.` and `..`, so the target is never the user's or
   the share's root. Verify: `DeleteConversation_RemovesOnlyThatConversation` is extended to the workspace dir, and
   `DeleteConversation_DotDotId_DeletesNothing` is added.
8. **Live test.** Add a `[LiveModelFact]` in `LiveCopilotTests`: a file-tool create plus a shell read agree in the
   conversation's workspace, the transcript stays in the store, and a turn after `RestartRuntimeAsync` resumes.
   Verify: run locally with `live-model.local.json`. It is skipped in CI.
9. **Infra (parallelizable with 2-8).** New `infra/modules/copilot-files.bicep`: a StorageV2 account with
   `allowSharedKeyAccess: true`, TLS 1.2, no public blob access, and one file share (quota set explicitly, small).
   Add a module scoped to `resourceGroup(containerAppsEnvironmentResourceGroup)` that creates
   `Microsoft.App/managedEnvironments/storages` (`AzureFile`, `ReadWrite`) with the key passed as an `@secure()`
   param from `listKeys()`. In `container-app.bicep`: a `volumes` entry with `storageType: 'AzureFile'`, a
   `volumeMounts` entry on the `backend` container ONLY (not the SearXNG sidecar) at `/mnt/copilot`, and the env var
   `Copilot__WorkspaceRoot=/mnt/copilot`. **`mountOptions` is required, not optional:** the image runs as
   `USER $APP_UID` (`src/Agent42.Backend/Dockerfile:25`; the aspnet image's `APP_UID` is 1654, so confirm it with
   `docker run --rm <image> id`), while an ACA SMB mount is root-owned by default. Set
   `uid=<APP_UID>,gid=<APP_UID>,dir_mode=0700,file_mode=0600` with the measured value.
   The deploy SP is Contributor on `rg-hosting`. That comes from the session memory `agent42-azure-deployment`, and
   nothing in the repo shows it, so confirm it by running a what-if before the first deploy
   (`az deployment group what-if`). The `managedEnvironments/storages` entry lives in the shared environment and
   outlives the app. Each deploy re-reads `listKeys()`, so a rotated key heals on the next deploy, and until then the
   mount fails. Record both facts in the ADR. SMB has no symlinks and no chmod, so `git`, `npm` and Python venvs may
   misbehave in the workspace. Record that too.
   Verify: `az bicep build --file infra/main.bicep` is clean. No test parses `container-app.bicep` for mounts today, so
   add `CopilotFilesMountTests` (a text-level test like `EasyAuthExclusionTests`) that pins: the volume on `backend`
   only, the mount path, `mountOptions` with uid/gid, and `Copilot__WorkspaceRoot`.
10. **UI copy.** Update `src/Agent42.Backend/wwwroot/index.html:317` to say a Copilot agent can run commands and work
    with files in its own workspace. `AgentEditorAssetsTests` (line 63) pins the option and the JS, not this note, so
    add an assertion on the new note text. `/index.html` is in the service worker's `SHELL`
    (`src/Agent42.Backend/wwwroot/sw.js:6`), so bump `CACHE` (currently `agent42-shell-v65`), or the old note keeps
    being served.
11. **Decision and docs.** Run `/decide` for the new ADR, which amends 0085 (add it to 0085's `amended_by`). It must
    contain: the decisions above; the spike evidence; the rejected alternatives, each with its reason (full-share
    runtime state, a local dir synced to Blob, dropping SessionFs and losing transcripts, files-only without shell,
    per-agent opt-in, ask-in-chat approval); the egress extension; the accepted risks; and the two-principal note. Then
    run `/gotcha` to update the two Copilot gotchas and add one for the hybrid routing (a virtual
    `InitialWorkingDirectory` breaks the shell; state routed by prefix; no Write permission for state-path creates),
    and `/sync-docs` for `docs/features/custom-agents.md`, the `CLAUDE.md` feature map row and the
    `CopilotAgentFactory` class remarks (the phase-1 trust boundary text). Also update ADR 0085's title suffix
    "(phase 1: no built-in tools)" and its amendment banner (lines 14-17, "unchanged for the copilot agent kind"), and
    add a cross-reference from ADR 0097 decision 7 (it says "for fleet tasks only"). Update the gotcha "The fleet's
    Copilot client is NOT ADR 0085's client", whose claim that chat runs `Mode.Empty` becomes conditional. Add the
    share mount to the CLAUDE.md rule 12 list. Grep `src/`, `docs/`, `.agentic/` and `CLAUDE.md` for "phase 1",
    "no built-in" and "Mode.Empty" to catch the `CopilotRuntimeOptions` XML comments and anything else.
    Verify: the ADR numbering guard test passes, and the doc-path integrity check passes.
12. **Gates.** `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`,
    `dotnet format Agent42.slnx --verify-no-changes`, and new-code coverage via
    `node .agentic/scripts/check-new-code-coverage.mjs`.
13. **Post-deploy check (manual, record the result in the ADR/memory).** In production, a Copilot chat runs `pwd`
    and `ls`, writes a file and reads it back. Redeploy, then confirm the next turn still sees the file and still
    knows turn 1.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | With the switch on, the chat client runs `CopilotCli` with a real `InitialWorkingDirectory`, `/state`, and minimal env + PATH only | `CopilotClientOptions_BuiltIns_CliModeRealRoot` | `tests/Agent42.Tests/CopilotRuntimeTests.cs` |
| 2 | With the switch off, the client and session options are exactly today's (`Mode.Empty`, `/work`, `AvailableTools` set) | `CopilotClientOptions_MinimalEnvAndIdleTimeout`, `CopilotSkills_NoBuiltinSkills_AndSkillToolOnlyWithFolders` (kept for the off path) | `tests/Agent42.Tests/CopilotRuntimeTests.cs` |
| 3 | With the switch on, a session works in its own conversation directory and does not set `AvailableTools` | `SessionConfig_BuiltIns_PerConversationWorkingDirectory_NoAvailableTools` | `tests/Agent42.Tests/CopilotRuntimeTests.cs` |
| 4 | The state path goes to the Blob store under the SAME keys as today; workspace paths go to disk (all operations); anything else is refused; ids `.`/`..`/trailing-dot are rejected | `StatePath_UsesTodaysKeys`, `WorkspacePath_AllOperationsHitDisk`, `OutsideWorkspace_Refused`, `Traversal_Refused` (ids `..`, `.`, `x.`, separators), `SymlinkOutside_Refused`, `StatePath_WindowsAndPosix_Classified` | `tests/Agent42.Tests/CopilotWorkspaceFsTests.cs` |
| 5 | The policy approves custom tool, shell and URL; approves read/write inside the workspace; rejects outside it and every other kind | `Policy_ApprovesShellUrlCustom`, `Policy_ReadWrite_InsideOnly`, `Policy_RejectsOtherKinds` | `tests/Agent42.Tests/CopilotPermissionPolicyTests.cs` |
| 6 | Deleting a chat removes its transcript prefix AND its workspace directory, and nothing else; a `..` id deletes nothing | `DeleteConversation_RemovesOnlyThatConversation` (extended), `DeleteConversation_DotDotId_DeletesNothing` | `tests/Agent42.Tests/CopilotRuntimeTests.cs` |
| 7 | `Copilot:BuiltInToolsEnabled` and `Copilot:WorkspaceRoot` bind from config, defaulting to on and a temp dir | `CopilotOptions_BindFromConfig` | `tests/Agent42.Tests/CopilotRuntimeTests.cs` |
| 8 | Live: the file tool and the shell agree in the workspace, and a turn after a runtime restart resumes | `LiveCopilot_BuiltIns_ShellSeesFileToolWrite_AndResumes` | `tests/Agent42.Tests/LiveCopilotTests.cs` |
| 9 | An existing chat (cwd `/work` in its `workspace.yaml`) resumes with a working shell in its new workspace | If step 1c passes unaided: `LiveCopilot_PreexistingSession_ResumesWithWorkspaceShell`. If it fails: `WorkspaceYaml_LegacyWorkCwd_RewrittenOnRead` plus that live test | `tests/Agent42.Tests/LiveCopilotTests.cs`, `tests/Agent42.Tests/CopilotWorkspaceFsTests.cs` |
| 10 | The editor copy says what a Copilot agent can do now, and the shell cache is bumped | `AgentEditor_OffersCopilotOnCreateOnly_AndGatesItsChat` (new assertion on the note) | `tests/Agent42.Tests/AgentEditorAssetsTests.cs` |
| 11 | The share mounts on `backend` only, owned by the app uid, with the workspace root env var | `CopilotFilesMount_BackendOnly_OwnedByAppUid` | `tests/Agent42.Tests/CopilotFilesMountTests.cs` |

## Quality gate checklist

From `.agentic/quality-gates.yaml` at 74ea5e3:

- [ ] `coverage_new_code_pct_min: 80` (`node .agentic/scripts/check-new-code-coverage.mjs`)
- [ ] `cognitive_complexity_max: 15` (S3776, a build error)
- [ ] `dependencies.vulnerable_packages: 0`
- [ ] build, tests and `dotnet format --verify-no-changes` clean; doc-path integrity clean

## Out of scope

- Hosted (provider-side) web search for Copilot chats. It was discussed separately and needs its own spike.
- Sandboxing the runtime (ADR 0085 phase 2 / remote runtime). This plan accepts the in-container risks instead.
- Documents/RAG, long-term memory, images, client tools and incognito for Copilot chats: unchanged, still excluded.
- Moving the fleet's workspaces onto the share, or merging the fleet and chat clients.
- Per-agent opt-in and in-chat approval prompts (rejected by the user).
- Migrating files that pre-existing chats wrote under `/work` in Blob (they stay where they are, unread).

## Review

Critic pass on 2026-10-19. All ten findings were accepted and folded into the plan above:
- 1: `.`/`..` ids escape on disk. Fixed in steps 3 and 7 and ACs 4 and 6.
- 2: legacy `/work` cwd. Step 1c now has a default code fallback, and AC 9 is a test.
- 3: mount permissions. Step 9 requires `mountOptions` and adds `CopilotFilesMountTests` (AC 11).
- 4: deploy SP rights. Step 9 runs a what-if and records the shared environment storage and key rotation.
- 5: managed-identity token via `/proc/<pid>/environ`, and unattended shell. Added to Context, and confirmed with the
  user before approval.
- 6: the policy is a per-session instance with OS-specific comparison, null-path rejection and symlink handling.
  Step 6.
- 7: flipping the kill switch across modes. Step 1d.
- 8: full router operation set, path classification, constructor change. Step 4 and AC 4.
- 9: doc debt (0085 title and banner, 0097 cross-reference, gotcha, rule 12, grep sweep). Step 11.
- 10: AC/test-file gaps, index.html in SHELL means a `CACHE` bump. Steps 2 and 10, AC 10.

None were rejected.

## Implementation notes: where it diverged from the plan (2026-10-20)

- **Drift:** one commit landed after `74ea5e3`: #205 (Python 3 and Node.js in the runtime image). It moved
  `USER $APP_UID` to Dockerfile line 40, and its comments named fleet agents only. Those comments now say chats too.
- **Step 1, Linux spike** (`mcr.microsoft.com/dotnet/sdk:10.0`, SDK 1.0.16, gpt-5-mini): (a) `create
  /tmp/ws/c1/note.txt` + `cat note.txt; pwd` printed `c1/tmp/ws/c1`; (b) resume after a restart kept the file and
  the code word, and the store held only `state/{workspace.yaml,events.jsonl,checkpoints/index.md}`; (c) a `/work`
  session resumed with `pwd` = its new workspace, because the runtime stats `/work`, gets not-found, and uses
  `WorkingDirectory`; (d) a CLI-created session resumed under `Mode.Empty` answered normally. So **(c) passed
  unaided**: no `workspace.yaml` rewrite, and AC 9 is the live test only (`LiveCopilot_PreexistingSession_ResumesWithWorkspaceShell`).
- **Outside paths:** (c) is why a read outside the workspace is a NOT-FOUND (not access denied), while a write is refused.
- **Router shape:** one `CopilotSessionFsProvider` with an optional `workspace`, which routes each operation to the
  existing store logic or to a new disk backend, `CopilotWorkspaceFiles`. That is composition without a second
  `SessionFsProvider`. A rename across state and workspace is refused. The write gate covers the state side only.
- **Env helper:** `ShellEnvironment()` moved into `CopilotAgentFactory`, and the fleet calls it, rather than the
  chat calling a fleet helper.
- **Windows host:** live tests on Windows use `SessionFsSetProviderConventions.Windows` with a real root, and they
  pass. `IsStatePath` and `KeyFor` drop a leading drive letter, so a `C:\state\x` path keeps today's key.
- **Workspace segment:** `*` is escaped to `%2A` too (EscapeDataString keeps it; Windows and SMB can't hold it).
- **Extra tests** beyond the AC table: `Separators_StayInOneSegment`, `CopilotFilesAccount_IsSeparateFromTheKeyFreeAccount`.
  The existing `LiveCopilot_ToolTurn_ResumesFromSessionFs` and `LiveCopilot_LoadsSelectedSkill` now run with
  built-ins on (the default), which also settled the gotchas' "unverified live" notes on custom tools beside built-ins.
- **Not done here (manual):** the `az deployment group what-if` before the first deploy (it needs the deploy secrets),
  and step 13's production check.
