---
id: 0107
date: 2026-10-20
status: accepted
tags: [copilot, github-copilot-sdk, sessionfs, azure-files, infra, tools, permissions, egress, trust-boundary, kill-switch, no-migration]
supersedes: []
amends: [0085, 0097]
---

# Copilot chats get the runtime's built-in tools, auto-approved, with an Azure Files workspace per conversation

> **Amended by [ADR 0121](0121-open-signin-owner-tier.md) (2026-11-09).** The consequence below that cross-user exposure "becomes real the day a third principal is
> admitted" is now **accepted**: sign-in opens to everybody and the Copilot shell stays available to all, un-isolated. It can
> forge the principal header and read managed-identity tokens, so the owner tier is advisory, not a boundary. The kill switch
> (`Copilot:BuiltInToolsEnabled`) is global and chat-only.

> **Amended by [ADR 0111](0111-chat-gateway-for-chat-apps.md), phase 3 (2026-10-21): this account now also holds the
> chat gateway's signal-cli store** (share `signal-data`, mounted at `/mnt/signal` on the backend only). That store is
> SQLite over SMB, which this ADR rejected for the Copilot runtime; it is acceptable there because one daemon writes it
> at a time, under the gateway lease. The built-in shell below can reach that daemon and store, an extension of the
> accepted risks listed here.

## Context

Copilot-kind chats (ADR 0085) run the runtime in `CopilotClientMode.Empty`: no shell, no file tools, no URL fetch.
The user wants every Copilot chat to have all of them, as the fleet's Copilot agents do (ADR 0097 decision 7). A
shell needs a real directory, and a chat's transcript must survive deploys. Spikes on 2026-10-19 (Windows) and
2026-10-20 (Linux, in `mcr.microsoft.com/dotnet/sdk:10.0`), SDK 1.0.16, live gpt-5-mini:
- `CopilotCli` with today's VIRTUAL `InitialWorkingDirectory` (`/work`): file tools went through `SessionFs`, but
  every shell command failed with `os error 267`, and a per-session `WorkingDirectory` did not fix it.
- **Hybrid** (a REAL `InitialWorkingDirectory` root, a per-session real `WorkingDirectory`, a provider routing the
  state path to the store and everything else to disk): `create /tmp/ws/c1/note.txt` and then `cat note.txt; pwd`
  printed `c1/tmp/ws/c1`. The transcript (`events.jsonl`, `workspace.yaml` and the checkpoint index) went only to
  the store, the state path never existed on disk, and no SQLite was opened.
- Resume after a runtime restart, with state only in the store: the file and the code word were both still there.
- A chat created under `Mode.Empty` (its `workspace.yaml` says `cwd: /work`), resumed in the hybrid: the runtime
  `stat`s `/work` once, gets not-found, and the shell's `pwd` is the new workspace. No rewrite is needed.
- A chat created with built-ins, resumed under `Mode.Empty` with `AvailableTools`, answers normally.
- A create under the state path raises NO Write permission. A shell request's `ResolvedWorkingDirectory` is EMPTY,
  so shell commands cannot be path-checked.

## Decision

1. **All Copilot chats get the built-ins, behind a kill switch** (user decision, 2026-10-19).
   `Copilot:BuiltInToolsEnabled` (default `true`) puts the chat client in `CopilotCli` mode. `false` is exactly
   ADR 0085's phase 1 (`Mode.Empty`, `/work`, `AvailableTools`). Flipping it needs a restart (one client per silo).
   With it on, `AvailableTools` stays unset, because it would hide the built-ins.
2. **Auto-approve, no prompts** (`CopilotPermissionPolicy`, one instance per session). Custom tools, the shell and
   URL fetch are approved. Built-in reads and writes are approved only inside the conversation's workspace (a null
   path is refused). Every other kind (MCP, memory, hooks, extensions) is refused. Reminder and background turns
   run the shell unattended too.
3. **The transcript stays in Blob; workspaces go to Azure Files.** `CopilotSessionFsProvider` routes `/state/**` to
   the `copilot-sessions` store under today's keys, so existing chats resume, and routes every other path to
   `{WorkspaceRoot}/{escaped userId}/{escaped conversationId}` (`CopilotWorkspacePaths`, the one place that path is
   built). It refuses everything outside that directory, symlinks included. An outside read is a not-found, and an
   outside write is refused. That backs up the policy, since a create can reach `SessionFs` without a permission.
   An id of `.`, `..`, empty or with a trailing dot cannot name a workspace. Deleting a chat deletes its workspace.
4. **Infra.** A separate StorageV2 account with shared-key access (`infra/modules/copilot-files.bicep`) holds one
   share, `copilot-workspaces` (10 GiB). It is separate because Container Apps mounts Azure Files with the account
   key, and the attachments account is key-free (ADR 0004). An environment storage link `agent42-copilot` in the
   shared `cae-hosting` (`rg-hosting`) gets the key from `listKeys()`, so there is no role assignment (hard rule 5).
   The share mounts on the `backend` container only, at `/mnt/copilot`, with
   `uid=1654,gid=1654,dir_mode=0700,file_mode=0600` (1654 is `APP_UID` in the aspnet image, measured). The mount is
   pinned by `CopilotFilesMountTests`. The runtime gets `MinimalEnvironment()` plus `PATH` (`ShellEnvironment()`,
   which the fleet now shares).

## Alternatives considered

- **Keep `Mode.Empty` and add file tools of our own.** Lost: no shell, and the user's goal is a coding agent.
- **Files only, with no shell** (the built-in file tools over today's virtual `SessionFs`). Lost: the user wants a
  shell, and the shell cannot run against a virtual directory (measured, `os error 267`).
- **Put the runtime's state on the share too, and drop `SessionFs`.** Lost: SQLite over SMB (0085's objection), and
  the transcripts already in Blob would no longer resume. Here the share holds workspaces ONLY, so 0085's objection
  to "an Azure Files mount for the runtime's state" does not apply.
- **A local directory synced to Blob.** Lost: a sync is a second consistency problem, and a deploy mid-turn loses
  whatever has not synced yet.
- **Drop `SessionFs` and lose transcripts on deploy.** Lost: measured in 0085, resume then throws `Session not found`.
- **Per-agent opt-in.** Rejected by the user: every Copilot chat gets the built-ins, and the server switch covers
  turning them off.
- **Ask in chat for each action, as the fleet does.** Rejected by the user: a reminder or background turn has nobody
  to ask, and asking per command stalls the loop.
- **Mount the share keyless with a managed identity.** Lost: a preview, and support for it in Container Apps is
  unconfirmed.

## Consequences

- **Accepted risks (user decision, 2026-10-19; the critic raised the last one, and the plan's Review records it as
  confirmed with the user before approval).** The runtime runs as the backend's own user (`USER $APP_UID`) in the
  backend container, unattended. Its shell:
  - can read every conversation's workspace on the share (not the transcripts, which stay in Blob). Today only two
    principals can sign in, both the owner (Easy Auth `allowedPrincipals`), so this is cross-conversation more than
    cross-user. **It becomes cross-user the day a third principal is admitted.**
  - can reach the app on `localhost` and forge `X-MS-CLIENT-PRINCIPAL-ID` (`src/Agent42.Backend/Api/CurrentUser.cs`);
  - can read the container filesystem;
  - **can read `/proc/<backend pid>/environ`, which holds `IDENTITY_ENDPOINT` and `IDENTITY_HEADER`, and so request
    managed-identity tokens**. That gives Cosmos and Blob data-plane access to ALL app data: grain state, other
    chats' transcripts, attachments and the Data Protection key ring. `MinimalEnvironment()` keeps those variables
    out of the runtime's own environment, but cannot stop this. The fleet's Copilot agents (0097) carry the same
    exposure, behind per-action approval. A prompt injection in a fetched page reaches an approved shell with no user
    present. The kill switch is the response.
- **Egress (an ADR 0013 extension).** A Copilot chat's built-in URL fetch and shell can reach any host the container
  can, unattended, gated only by the kill switch.
- **The shared environment holds this app's storage link**, which outlives the app, and its copy of the key is
  re-read on each deploy. After a key rotation the mount fails until the next deploy. Before the first deploy, a
  what-if confirms the deploy SP can write `managedEnvironments/storages` in `rg-hosting`.
- **SMB has no symlinks and no chmod**, so `git`, `npm` and Python venvs may misbehave in a workspace. The shell can
  run what the image ships (Python 3, Node.js; `BackendImageRuntimeTests`), and nothing can be installed.
- **Not migrated:** files a pre-built-ins chat wrote under `/work` stay in Blob, unread.
- **Rule 12:** the share mounts into the single replica, so in-process write gates stay sufficient.
- **Open:** a production check: `pwd`, `ls`, write and read back, then redeploy and confirm the next turn sees the
  file and remembers turn 1 (`.agentic/specs/completed/plan-copilot-chat-builtin-tools.md`, step 13).
