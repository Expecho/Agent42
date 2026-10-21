# Plan — Chat gateway: talk to Agent 42 from Telegram, Slack, Discord, WhatsApp and Signal

planned-against: main @ 41f0a41

Supersedes the untracked `plan-slack-chat.md` (planned against `8b91841`). That plan's thread model, pairing code,
event filter, drain discipline and secret handling carry over. Its transport (Events API) and its Slack-only shape
do not.

## Overview

**Goal:** each Agent42 user can connect their **own** bots on chat platforms and talk to Agent 42 there. DMs,
threads and (when the bot is mentioned) groups. Replies stream in place where the platform can edit. Images, files
and voice notes ride along. Scheduled results and notifications can be delivered back into the chat. One in-process
**chat gateway** hosts every connection behind a single adapter contract, borrowed from Hermes Agent's gateway
(`NousResearch/hermes-agent`, `gateway/` + `plugins/platforms/*`, read at commit `09836543`, 2026-10-21).

**Why now:** the comparison with OpenClaw and Hermes (2026-10-21) showed chat-app reach as Agent42's single
biggest gap. Every turn primitive already exists: `ChatAsync`, `StopAsync`, the agent-run file core, per-user
secret protectors and the push and webhook seams. What's missing is one shared gateway, rather than a separate
integration per platform.

**Phasing (one spec, several MRs):**

| Phase | MR | Content |
|---|---|---|
| 1a | core | Gateway core, connections, pairing, turn pipeline, streamer, commands, **Telegram**, DevHost fake, Preferences panel |
| 1b | adapters | **Slack** (Socket Mode) and **Discord** (gateway websocket) |
| 1c | extras | Inbound files + outbound images, voice notes (server STT), groups with mentions, proactive delivery |
| 2 | whatsapp | **WhatsApp** via the official Meta Cloud API (webhook in, Graph API out) |
| 3 | signal | **Signal** via a `signal-cli` daemon sidecar with a persistent key store |

Each MR is shippable on its own. Before starting phase 2 or 3, re-run Step 1's drift check (header commit vs
HEAD) and re-verify the phase's spike items. Those phases are planned here at a coarser grain, on purpose.
**Phase 3 was refined on 2026-10-21 and runs before phase 2** (section "Phase 3 — Signal, refined plan"); it
changes Signal to a dedicated number and a lease-gated child process.

**Shape:**

```
                     ┌──────────────────── ChatGatewayService (IHostedService, ONE per process) ────────────────────┐
 Telegram ◄─poll──── │ TelegramAdapter ┐                                                                            │
 Slack   ◄─wss────── │ SlackAdapter    ├─► InboundMessage ─► ChatTurnPipeline                                        │
 Discord ◄─wss────── │ DiscordAdapter  │      (normalized)     1 dedupe (gateway-owned, survives reconnect)          │
 WhatsApp ─webhook─► │ WhatsAppAdapter │                       2 authz: paired owner? else pairing check / silence   │
 Signal  ◄─sse────── │ SignalAdapter   ┘                       3 command? → dispatch INLINE (bypasses every queue)   │
                     │   ▲ one session per enabled connection    4 route → conversationId (ChatRouteGrain)            │
                     │   │ supervised: jittered reconnect        5 per-conversation guard (sync) → queue → semaphore  │
                     │   │ singleton lease (revision overlap)    6 files/voice → store/ingest/transcribe              │
                     │   └──── IChatPlatformSession              7 detached drain ChatAsync → buffer                  │
                     │          Send / Edit / Typing / Upload    8 ReplyStreamer → edit-in-place | final-only + chunks│
                     └──────────────────────────────────────────────────────────────────────────────────────────────┘
 AgentGrain (reminder / agent-run / push tool) ──IChatDeliverySender──► gateway ──► origin chat or home chat
```

## Decisions taken in the planning interview (2026-10-21)

These are the user's explicit calls. Don't relitigate them. If one has to change, that's a new planning round.

- **G1 — Outbound connections, not webhooks, wherever the platform allows it.** Slack uses **Socket Mode**,
  Discord the **gateway websocket**, Telegram **long polling**. They are all held by the one replica. This
  reverses the old plan's D1 (Events API). It removes the public endpoint, the HMAC check and the pre-secret
  `url_verification` problem (old C3), and it makes local exercise possible with no tunnel. WhatsApp (Cloud API)
  is the one exception, because Meta offers no outbound transport. It gets a webhook in phase 2. Signal talks to
  a local sidecar.
- **G2 — Bring your own bot (old D2 kept).** Each user creates their own bot or app per platform and pastes its
  credentials into Preferences. The operator holds **no platform secret** in phases 1–2. There's no GitHub secret
  and no Bicep param. Phase 3 adds an operator-run sidecar container, but each user still links their **own**
  Signal account to it. A shared operator bot was rejected.
- **G3 — WhatsApp and Signal are later phases.** WhatsApp goes through the **official Cloud API**. Baileys, the
  unofficial WhatsApp Web client, was rejected over the terms-of-service and ban risk. Signal goes through
  **`signal-cli`**.
- **G4 — All four extras are in v1 (phase 1c):** proactive delivery to a chat; groups and channels with mention
  gating; chat commands; voice notes.
- **Carried from the old plan:** a thread is a conversation where the platform has threads (old D3). Each
  connection targets plain chat or one stored agent (old D4). The owner is bound by a pairing code (old D5).
  Replies are progressive (old D6). Inbound images and documents use the agent-run core (old D7). **Outbound
  images are now in scope** (1c). Old D7 deferred them. Chat apps make the upload cheap, and
  `IAttachmentStore.OpenReadAsync` already returns bytes.

## Inherited rules from the old Slack plan (inlined, so this file stands alone)

The text below refers to these rules as "old D3", "old C4" and so on. They are the authoritative summary. The
old plan file isn't needed.

- **Old D3: thread = conversation.** A threaded platform keys a conversation on its thread root. One endless
  conversation was rejected over ADR 0004/0016's 2 MB session cap.
- **Old D4: a target per connection.** A connection targets plain chat (`null`) or one stored agent. Several agents
  means several bots.
- **Old D5 / C12: pairing code.** Saving credentials mints a one-time, expiring code. The first DM whose trimmed
  text matches binds the sender as owner. Before pairing, and from anyone else after it, messages are ignored
  silently. A wrong or expired code gets no reply (no oracle). The pairing DM is never a conversation.
- **Old D6: progressive replies.** Send a placeholder, edit it on a throttle, then do a final edit. Split past the
  size limit.
- **Old D7: inbound files** use the same store and ingest core as `/api/agent-run`.
- **Old C4: the event filter**, the source of the self-reply loop. Every send **and every throttled edit** can come
  back as an inbound event: Slack `bot_id`, subtype `message_changed`. Accept a message only when **all** of these
  hold:
  - it is a user message (Slack: subtype absent **or `file_share`**, because rejecting every subtype drops every
    file message);
  - it is not from a bot and not from our own bot user id;
  - the sender is the paired owner, or it is a pairing attempt before pairing;
  - its id hasn't been seen. Record the id **only after a successful enqueue**, so a failed enqueue doesn't
    swallow the platform's retry.
- **Old C5 / M3 / M4: drain discipline.**
  - Drain `ChatAsync` to completion on a **detached task into a buffer**. The reply streamer reads snapshots.
  - **No platform call is ever awaited inside the `await foreach`**: a hung platform call would stall `MoveNext`,
    and the turn would never persist (the session write sits after the loop).
  - Set the user identity in `RequestContext` inside the turn's own async scope.
  - Curated failure text, never the exception.
  - Capture the public base URL at enqueue time, where `UseForwardedHeaders` makes it correct.
  - Turns in flight at shutdown are lost; that's an accepted residual.
- **Old C7: persona safety.**
  - Touch the index on every turn.
  - Bind a target agent only on a **new** conversation, and never re-bind a continued one, which would rewrite
    the persona under its history.
  - A deleted target agent gets a curated reply, and no turn runs.
  - A chat-started conversation is deliberately **not** retention-windowed, because the ADR 0016 window applies
    only to continuous `agent-` ids. Say so where the id is minted.
  - **No tool narrowing:** `AgentMcpRunner.WithoutMemoryOrPush` exists because an API-key holder isn't the owner.
    Here the sender **is** the platform-verified owner, so memory and push stay as the user selected them. Put this
    contrast in a code comment.
- **Old C8: files.**
  - Validate every file from its **metadata** (name, declared type, size) with extension-first media-type
    resolution **before downloading any** (gotcha "A browser's upload Content-Type is unreliable").
  - Download from a **pinned host only**, with no off-host redirect and a read capped at `MaxAttachmentBytes`.
    Treat a `text/html` response as an auth failure (Slack serves a login page, not a 401).
  - Register documents **before** the turn.
- **Old C9: HTTP.**
  - No SDK where about 10 calls suffice.
  - Each named client calls `RemoveAllResilienceHandlers()` (an inherited retry double-posts a send the platform
    already accepted) and sets an explicit timeout.
  - Handle `429 Retry-After` explicitly.
- **Old C11: secrets.** Protect in the API layer under a dedicated Data Protection purpose. Write-only (the GET
  says only "set"). Validate on save, and persist the bot's own id.

**Hermes citations** (`file:line` into `NousResearch/hermes-agent` @ `09836543`) are background evidence only. The
rule each one supports is restated in this file, and the restatement is what binds. To re-read one:
`git clone --filter=blob:none https://github.com/NousResearch/hermes-agent` then
`git sparse-checkout set --no-cone '/*' '!/website/'` (the website folder has paths too long for Windows). It is
untrusted code: read it, never run or install it.

## Affected areas

Verified against the tree at `41f0a41`. Re-verify every count marked *(re-count)* at execution: other specs
also add rows.

**New — Agents (`src/Agent42.Agents/ChatGateway/`, flat `Agent42.Agents` namespace, ADR 0039):**
- `ChatConnection.cs`: the persisted per-connection record, a **class** with property initialisers (see C11).
  `UserChatConnectionsState` is its root.
- `IUserChatConnectionsGrain.cs` / `UserChatConnectionsGrain.cs`, keyed by userId.
- `IChatConnectionDirectoryGrain.cs` / `ChatConnectionDirectoryGrain.cs`. One well-known key lists
  `(userId, connectionId)` for every **enabled** connection, so the gateway can start them all at boot (C3).
- `IChatRouteGrain.cs` / `ChatRouteGrain.cs`, keyed by `{connectionId}:{routeKeyHash}`. It holds the current
  conversation id for one chat or thread, the `/new` epoch, the `/model` override and the home flag (C5).
- `ChatOrigin.cs`: a **class with property initialisers** (`ConnectionId`, `RouteKey`, each `[Id(n)]` with a
  default), stored on the conversation (C9). It's not a positional record, per the gotcha "A record parameter a
  Cosmos document lacks binds to `null`".
- `IChatDeliverySender.cs`: the outbound port the grain calls (C9), plus `NullChatDeliverySender`.
- `ChatPairingCode.cs`, `ChatGatewayLimits.cs`, `ChatPlatformKind.cs` (`Telegram`, `Slack`, `Discord`,
  `WhatsApp`, `Signal`; persisted as **strings**, never ints, like `ProviderKind` in ADR 0110).

**New — Backend (`src/Agent42.Backend/ChatGateway/`, flat `Agent42.Backend` namespace, ADR 0040):**
- `AgentApi.ChatGateway.cs`: the endpoint partial *(re-count: 26th at plan time)*, holding management CRUD under
  `/api/chat-connections`. It's **Easy-Auth-gated**, so phase 1 adds no `excludedPaths` entry.
- `ChatGatewayContracts.cs` (DTOs) and `ChatGatewaySecretProtector.cs` (purpose `"Agent42.ChatGateway.Secret.v1"`).
- `ChatGatewayService.cs`: the supervisor (the `IHostedService` in `FleetTaskService`'s registration shape). It
  starts, stops and restarts one session per connection, with jittered backoff and the singleton lease (C3).
- `IChatGatewayLease.cs` + `BlobChatGatewayLease.cs` (production; or the Orleans alternative, if Step 1e picks it),
  `NoOpChatGatewayLease` (DevHost) and a fake in tests (C3).
- `IChatPlatformAdapter.cs`, `IChatPlatformSession.cs`, `InboundMessage.cs`, `ChatSendResult.cs`, and
  `ChatPlatformCapabilities.cs` (C2).
- `ChatTurnPipeline.cs`: authz, commands, routing, guard, queue and drain (C4–C7). `ChatCommands.cs` (C8).
- `ChatReplyStreamer.cs`, `ChatMessageChunker.cs`, `ChatTypingLoop.cs` (C6).
- `InboundDeduper.cs`: gateway-owned, keyed per connection (C4).
- `ChatDeliverySender.cs`: the Backend implementation of `IChatDeliverySender` (C9).
- `Telegram/` *(subfolder of ChatGateway; flat namespace still applies)*: `TelegramAdapter.cs`,
  `TelegramBotApi.cs` (hand-rolled), `TelegramText.cs` (Markdown → Telegram HTML).
- `Slack/`: `SlackAdapter.cs` (Socket Mode over `ClientWebSocket`), `SlackWebApi.cs` (hand-rolled), `SlackText.cs`.
- `Discord/`: `DiscordAdapter.cs` over a library (Step 1 picks it, C10) and `DiscordText.cs`.
- `ChatGatewayRegistration.cs`: `AddChatGateway(...)` in `HindsightRegistration`'s shape (rule 9).
- Phase 2: `WhatsApp/` plus `AgentApi.ChatGatewayWebhooks.cs` (the public webhook). Phase 3: `Signal/`.

**New — speech-to-text (phase 1c, extends ADR 0038):** `src/Agent42.Agents/Speech/ISpeechTranscriptionService.cs`
and `src/Agent42.Backend/Speech/OpenAITranscriptionService.cs`, resolved per user next to `GetSpeechService`
(`src/Agent42.Backend/Models/ChatClientFactory.cs:163-174`). A provider-overlay field names the transcription
model, so `UserProviderResolver.cs` and the provider dialog change too (C12).

**Modified — source:**
- `src/Agent42.Backend/Api/AgentApi.cs`: call `MapChatGatewayEndpoints`.
- `src/Agent42.Backend/Program.cs` and `tools/Agent42.DevHost/Program.cs`: `AddChatGateway(...)`. DevHost also
  registers the loopback adapter (Step 16).
- `src/Agent42.Agents/Conversations/AgentGrain.cs` and `src/Agent42.Agents/Push/AgentGrain.Push.cs`, at the two
  away-reply sites (`AgentGrain.cs:~482` and `~1614`), plus `AgentGrain.PushTool.cs`: the delivery hook (C9). Also
  a trailing `[Id(n)] ChatOrigin? Origin` on the grain's persisted state (C9).
- `src/Agent42.Agents/Conversations/IAgentGrain.cs`: a new `[Alias("SetChatOriginAsync")] Task
  SetChatOriginAsync(ChatOrigin origin)`. It's idempotent, and it's written only when the route mints a
  conversation. Any test double implementing `IAgentGrain` gains it.
- **Port registration (ADR 0105, per host):** `IChatDeliverySender` is a port, registered by **every** silo host
  before `AddAgent42Silo()`, exactly like `IAgentWebhookSender`:
  - `src/Agent42.Backend/Program.cs` registers the gateway implementation;
  - `tools/Agent42.DevHost/Program.cs` registers the gateway implementation, since DevHost runs the loopback
    gateway;
  - `tests/Agent42.Tests/TestSiloConfigurator.cs` registers a recording sender;
  - every other silo configurator under `tests/` registers the Null sender (grep `AddAgent42Silo(` and
    `IAgentWebhookSender` to list them).

  Add it to `ThrowIfPortsMissing` in `src/Agent42.Backend/Orleans/Agent42SiloRegistration.cs`, plus the matching
  registration test, if the grain takes it as a constructor dependency (`RequiredGrainServices` then makes a
  missing registration a startup failure).
- `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs`: no change expected. `AgentRunFileError` +
  `StoreAgentRunFilesAsync` are already the neutral core that email reuses (`AgentApi.Email.cs:111`). Reuse them as-is.
- `src/Agent42.Backend/wwwroot/`: `index.html` (rail button `tabChat` + `panelChat` + connection dialog), new
  `js/chatgateway.js` imported from `main.js`, `js/dom.js` (`profileTabs`, **appended last**), and `sw.js`
  (`CACHE` bump from `agent42-shell-v69` at plan time; re-read it; `SHELL` gains the module).
- Phase 2: `infra/modules/container-app.bicep` gets `'/api/chat-in/whatsapp/*'` in `excludedPaths`. Phase 3:
  `infra/main.bicep` and `infra/modules/container-app.bicep` get a `signal-cli` sidecar and an Azure Files volume.

**Modified — tests holding exact lists *(re-count at execution)*:** `ProjectLayoutTests` (`ExpectedFolders()`
for both projects, `AgentApiParts()`, now 25 rows, and a new `Hosts_RegisterChatGateway` theory),
`AgentApiReachabilityTests`, `GrainStorageSerializerTests.PersistedTypeNames()`, `GoldenStates.cs` +
`SerializationCompatibilityTests`, `PwaAssetsTests` (+ the four other tests pinning the cache name, which ADR
0110's MR also had to bump), `PreferencesLayoutAssetsTests` (6 → 7 rail sections; `tabMcp` stays at index 3),
`ConfigHealthAssetsTests` / `UserSkillPickerAssetsTests` (index pins), and phase 2 `EasyAuthExclusionTests`.

**Modified — docs (hard rule 2, same MR as the code they describe):** new ADRs *(re-check numbers; 0111 is the gateway; 0112 was taken by past-chats on main, so the STT ADR takes the next free number
at plan time)*: the chat gateway, and server-side speech-to-text (amends 0038). Amendment banners on 0038, 0028/0075
(delivery beside push) and 0065 (seven sections). Also: 0039/0040 (folder lists; 0040's partial count), `CLAUDE.md`
(feature-map row; **rule 12** gains the gateway sockets, the deduper, the turn queue, the per-conversation guards and
the lease; **rule 13** gains the egress), `.agentic/gotchas.md` (Step 17), `README.md`, and new
`docs/integrations/chat-apps.md` with one section per platform (create bot → paste → pair) plus a row in
`docs/integrations/README.md`. Old `plan-slack-chat.md` gets a "superseded by" line, and nothing more.

**Deliberately NOT touched in phase 1:** `infra/*` (no excluded path, no secret), `.github/workflows/deploy.yml`,
`ConfigHealth.cs` (no operator secret), `AgentDefinition`.

## Context & constraints

This is judgment the executor can't cheaply re-derive. Code facts are left out on purpose, so read the files.

### C1 — Exemplars to copy, by role

| Role | Copy the shape of |
|---|---|
| Drain a headless turn into text, `TextReset`, curated failure, never abandon the enumerator | `src/Agent42.Backend/Mcp/AgentMcpRunner.cs` (`DrainWithinBudgetAsync`, `:123`) |
| Start a background conversation: Touch, bind, register docs | `StartAgentRunAsync`, `src/Agent42.Backend/StoredAgents/AgentApi.StoredAgents.cs:141-162` |
| Inbound files: validate + store/ingest core | `AgentRunFileError` / `StoreAgentRunFilesAsync` (`AgentApi.StoredAgents.cs:~215-236`); email's skip-not-reject `PartitionEmailFiles` is the alternative policy |
| Per-credential directory grain | `src/Agent42.Agents/Email/EmailAddressDirectoryGrain.cs`; gotcha "Each credential namespace gets its own directory grain type" |
| Encrypted, never-echoed secret with its own purpose | `src/Agent42.Backend/Webhooks/WebhookSecretProtector.cs` (newest of three siblings) |
| Long-lived hosted service | `src/Agent42.Backend/Fleet/FleetTaskService.cs` + `FleetRegistration.cs:39` |
| Outbound websocket with reconnect and backoff | `tools/Agent42.LocalBridge/BridgeClient.cs:101-123` |
| Named HttpClient opting out of retries + explicit timeout | `Program.cs:249-254` (`ContextWebPageFetcher`) |
| User identity on a non-request caller's spans | `UserTelemetryContext.RequestContextKey` + `RequestContextPropagationTests` |
| Registration guard (no test runs `Program.cs`) | `ProjectLayoutTests` `Hosts_RegisterLocalBridge` |
| Outbound port called from the grain, recorded in tests | `IAgentWebhookSender` + `RecordingAgentWebhookSender` in `TestSiloConfigurator.cs:108-111` |
| Prefs panel + dialog + list | `wwwroot/js/mcp.js`, `#tabMcp` / `#panelMcp` |

### C2 — The adapter contract (from Hermes `BasePlatformAdapter`, trimmed)

Hermes's rule is that **the runner owns auth and profile state, and an adapter never does**
(`gateway/platforms/base.py:1910`). Copy that boundary. An adapter is transport plus formatting, nothing else.

- `IChatPlatformAdapter` (one singleton per platform kind) has `Kind`, `Capabilities`, `ValidateAsync(credentials)`
  (Telegram `getMe`, Slack `auth.test`, Discord `GET /users/@me`), which returns the bot's own id/name to persist,
  and `ConnectAsync(ChatConnectionRuntime, IInboundSink, ct) → IChatPlatformSession`.
- `IChatPlatformSession` has `SendAsync(target, text, replyTo?)`, `EditAsync(target, messageId, text)`,
  `SendTypingAsync(target)`, `UploadImageAsync(target, bytes, name, caption?)`, `DownloadAsync(fileRef, maxBytes)`
  and `DisposeAsync`. Every send returns a `ChatSendResult(Success, MessageId, ErrorKind, RetryAfter)`. `ErrorKind`
  is one of `TooLong`, `BadFormat`, `Forbidden`, `NotFound`, `RateLimited`, `Transient` or `Unknown`, as in Hermes
  `base.py:1642-1673`. The pipeline reacts to the kind, never to platform error strings.
- `ChatPlatformCapabilities`: `MaxMessageLength` (Telegram 4096 counted in **UTF-16 units**; Slack 39000; Discord
  2000 with a **cap of 8 chunks**; WhatsApp 4096; Signal 8000. These are Hermes's values. Step 1 confirms each
  from the platform docs, and `ChatGatewayLimits` cites the source), `SupportsEdit` (WhatsApp Cloud and Signal:
  **false**), `SupportsThreads`, `MinEditInterval`, `TypingRefresh`.
- `InboundMessage` (a subset of Hermes `MessageEvent` + `SessionSource`) has `ConnectionId`, `PlatformMessageId`,
  `ChatId`, `ChatType` (`Direct`, `Group`, `Thread`), `ThreadId?`, `SenderId`, `SenderIsBot`, `Text`,
  `MentionsBot`, `RepliesToBot`, `Files[]` (name, declared type, size, opaque ref), `IsVoiceNote`, `ReceivedAt`.
  The adapter fills `MentionsBot` from **server-side entities**, never a substring match (Hermes
  `telegram/adapter.py:6058-6080`; Slack `<@U…>` can sit only in blocks, `slack/adapter.py:4630-4636`).
- **The adapter drops its own echoes** (bot user id, `bot_id`, Slack `subtype`, Discord `author == self`). The
  pipeline drops them again as a second line, because old C4's self-reply loop is the worst bug in this feature.
- Adapters are registered by kind in DI. No plugin loading.

### C3 — Process model: one supervisor, supervised sessions, a singleton lease

- `ChatGatewayService.StartAsync` reads `ChatConnectionDirectoryGrain` and starts a session per enabled connection.
  Management endpoints call `IChatGatewayControl.RestartAsync(connectionId)` / `StopAsync(connectionId)` after
  every save or delete, so that credential, target or enabled changes apply without a process restart.
- **Supervision:** a session that throws or drops reconnects with jittered exponential backoff, capped (Hermes
  `run_adapters.py:710`). An auth failure (401 from the platform, Discord close code 4004) does **not** retry
  forever. It marks the connection `Faulted` with a curated reason, which the panel shows, and it stops.
- **Singleton lease (correctness, not tidiness).** ACA's revision rollover runs the old and new revision side by
  side for a while. Two Telegram pollers fight (`409 Conflict`, Hermes `telegram/adapter.py:2373-2437`), and two
  Discord gateway sessions **both answer** every message. Rule 12's single replica doesn't cover the overlap. The
  gateway therefore takes an exclusive **lease** through `IChatGatewayLease` before it connects anything, and holds
  and renews it while running. The new revision waits until it can take the lease, graceful shutdown releases it,
  and a lost renewal disconnects every session at once. Step 1e decides the production mechanism: a Blob lease on
  the existing attachments storage account (RBAC already exists, see the gotcha "Attachments Storage RBAC is in
  Bicep"), or an Orleans-native alternative. **The abstraction is fixed regardless:** DevHost gets a no-op lease,
  and tests get a fake whose handover is scripted, so AC13b runs offline. **Measure a real rollover in Step 19**,
  and record it in the ADR.
- **Inbound dedupe is gateway-owned**, keyed `(connectionId, platformMessageId)`, with a TTL and a bound, and it
  **survives a session reconnect**. Hermes learned that a hand-rolled per-adapter cache starts empty on reconnect
  (`helpers.py:69`). It is in-process, so it goes on rule 12's list.
- **Telegram offset is persisted.** `getUpdates` takes an `offset`. Store the last processed `update_id` on the
  connection, so a restart neither replays nor drops updates. Hermes drops pending updates on cold boot by default
  (`telegram/adapter.py:701-704`); here, **keep them**, since the user expects their message to be answered.
  Discord messages sent while disconnected are lost (no replay). Slack redelivers unacked envelopes. Record both.
- Don't wire `stoppingToken` into turns. In-flight turns at shutdown are lost, as in old C5. Record it.

### C4 — Authorization: paired owner only, silence to strangers

- **Only the paired owner's messages ever start a turn, in DMs and in groups alike.** The pipeline compares
  `SenderId` (and, for Slack, the team id) with the connection's `OwnerPlatformUserId`. Everyone else is
  **silently ignored** and creates **no server state** (Hermes `run_inbound_unauthorized.py:1-11`, issue #9337:
  replying to strangers with pairing prompts is noisy and an info leak). A non-owner message in a group the
  owner is in is ignored too. Other people's text never reaches the owner's agent. That rules out injection
  through group members and it's the egress story's main lever (C13). Allow-listing other senders is out of
  scope.
- **Pairing** follows old C12, hardened with Hermes's limits (`gateway/pairing.py:1-37`). Saving credentials
  mints a code (base32 without ambiguous characters, ~8–10 characters), valid for 15 minutes, compared
  case-insensitively with `FixedTimeEquals`. The **first DM** whose trimmed text matches binds
  `OwnerPlatformUserId` (+ team). A wrong code is ignored silently, with no oracle. **Lock out after 5 wrong codes**
  per connection until re-pair. Codes are never logged. The pairing DM never becomes a conversation. Pairing is
  **DM only**, never from a group.
- **Bots are ignored outright** (`SenderIsBot`). Because only the owner can trigger, Hermes's bot-loop guard
  (`bot_loop_guard.py`) isn't needed. Say so in the ADR, so nobody adds "allow bots" without bringing it back.

### C5 — Routing: chat → conversation

- **Route key.** In a DM, the key is `chat`. In a threaded reply (Slack `thread_ts`, Discord thread channel), it's
  `chat + thread root`. In a group without a thread, it's `chat`, and Slack and Discord answer in a thread they
  create from the mention, so the follow-ups land in the thread. The sender is always the owner, so Hermes's
  per-participant keys (`session.py:686-727`) are moot.
- **`ChatRouteGrain` holds the current conversation id** for a route. A deterministic id (old C7) can't support
  `/new` in a DM that has no threads, which is the Telegram and WhatsApp case. The grain mints a GUID-shaped id
  with the prefix `chat-` (disjoint from `agent-`, `mcp-` and the old `slack-`) on first use and on `/new`. Its
  `IsValid` follows the shape of `AgentMcpLimits.IsValidConversationId`.
- **Never reset by time.** That's Hermes's rule (`session_lifecycle.py:119-121`: "time never does"). A continuous
  DM conversation grows, though, and the session cap is 2 MB (ADR 0004). **Step 1 measures** whether session
  compaction (ADR 0084) bounds a long plain conversation. If it doesn't, roll over to a fresh conversation at a
  turn count in `ChatGatewayLimits`, with a one-line notice in the chat ("Started a new conversation; the old one
  is in Agent42"). Never roll over silently.
- **New vs continued:** Touch on every turn (`IConversationIndexGrain.TouchAsync`, as `StartAgentRunAsync`
  does). Bind the target agent **only when the route minted the id this turn**. Never re-bind a continued
  conversation (old C7's persona rule). A deleted target agent gets a curated reply, and no turn runs.
- A conversation deleted in the web UI and then written to again in the chat starts fresh: the route mints a new
  id on the next turn, because the index no longer has it. That's accepted, so document it.
- `/model` stores a model override **on the route** and passes it as `ChatAsync`'s `model` on later turns. `null`
  means the user's default (`UserProviderResolver.cs:166-182`). `/agent <name>` sets the route's target for its
  next conversation and implies `/new`.
- Plain chat keeps the user's tool selection, memory and push (old C7's "no tool narrowing"). The sender is the
  platform-verified, paired owner, not an external key holder.

### C6 — Reply delivery: the streamer (Hermes `stream_consumer.py` + `AGENTS.md:30-57`)

- **Drain on a detached task into a buffer; never await platform I/O inside the `await foreach`** (old C5; gotcha
  "Bounding a drained agent turn: cap the WAIT, never the enumeration"). The streamer reads buffer snapshots.
- **Call `ChatAsync(text, model: route.ModelOverride, attachments, ephemeral: false, interactive: false)`.** The
  default is `interactive = true` (`IAgentGrain.cs:57`). On an interactive turn, client tools (confirm, artifact
  pane, location; ADR 0089/0093), MCP elicitation (ADR 0095) and suggestions **pause the stream waiting for a
  browser**. A chat app can't answer, so the turn would hang. `AgentMcpRunner` passes `interactive: false` for
  exactly this reason (`AgentMcpRunner.cs:131-132`). Dropping those events in the allow-list below doesn't help:
  the pause happens in the grain. Pass `ephemeral: false` always, since a chat conversation can't be incognito
  (it's listed, and it delivers). Pin both with a test.
- **Edit-capable platforms:** send a placeholder at once (or Telegram's native draft if Step 1 shows
  `sendMessageDraft` fits), then edit it with the accumulated text no faster than `MinEditInterval`. Hermes
  defaults to 0.8 s. Telegram also has a **shared 1 action per second per chat budget across sends and edits**
  (`telegram/adapter.py:200-207`). Skip an edit whose text hasn't changed. Frames must be **prefix-stable**, and
  **only the streamer declares the final**. Reconcile by edit, never by a fresh send (the four invariants in Hermes
  `gateway/AGENTS.md:30-57`, each learned from a duplicate-final incident).
- **No-edit platforms** (WhatsApp Cloud, Signal): a typing indicator while the turn runs, then the final text only.
- **Over the limit:** the streaming preview is truncated with a marker. The final edit writes chunk 1, and the
  rest go out as replies. `ChatMessageChunker` splits at a newline, then a space, never inside inline code, and
  **closes and reopens code fences** across chunks, with `(1/3)` markers (Hermes `base.py:4842-4902`). It's a pure
  function, so test it hard.
- **Rate limits:** an intermediate edit that gets `RateLimited` is skipped. The final send retries a small bounded
  number of times, honouring `RetryAfter`, but **never sleeps inline longer than a cap** (e.g. 30 s). Hermes's
  "97-minute FloodWait slept verbatim froze inbound on every platform" (`base.py:1662-1665`). After 3 consecutive
  flood failures the stream stops editing and sends the final once. Past the cap, log and drop the update. Never
  re-send a whole payload after partial delivery.
- **Typing:** refresh on `TypingRefresh` (Telegram/Signal ~4 s, Discord ~9 s, Hermes `base.py:3350-3388`), and
  **stop typing before the final send**. A stalled ack otherwise leaves "typing…" on forever (Hermes #117300).
- **Event allow-list (old C10):** only `TextDelta`, `TextReset` (clears the buffer, ADR 0082), `Image` (C7) and
  `Done` reach the chat. `Reasoning`, `Todos`, `Suggestions`, `McpReauthRequired`, `McpElicitation`,
  `StateSnapshot` and tool-call events **never** do. Writing them would be egress the ADR doesn't cover. Tool
  progress (Hermes `display_config.py`) is out of scope.
- **Formatting:** model output is Markdown.
  - **Telegram:** convert to Telegram **HTML** parse mode (`<b> <i> <code> <pre> <a>`; escape `& < >`). On a
    `BadFormat` result, resend as plain text. HTML is chosen over MarkdownV2 because its escaping set is three
    characters instead of eighteen, and Hermes needed a plain-text fallback for MarkdownV2 anyway
    (`telegram/adapter.py:3481-3490`). Apply formatting **on the final edit only**. Intermediate frames are
    plain, so a half-open `**` never fails the edit (Hermes `:544-547`).
  - **Slack:** Step 1 decides between `markdown_text` / the markdown block and a `SlackText` mrkdwn converter.
    Escape `@channel` / `@here` / `<!…>` either way.
  - **Discord:** native Markdown. Tables become bullet lists. Send with `allowed_mentions: { parse: [] }`, so a
    reply can never ping `@everyone` or a role (Hermes `discord/adapter.py:659-688`).
- **Curated failure text**, never the exception (the `AgentMcpRunner` catch discipline). A user with no chat model
  is told to add a provider in Agent42.

### C7 — Concurrency and the busy policy

- **Install the per-conversation guard synchronously, before any await** (Hermes `base.py:3989-4031`). If the
  guard is installed after an await, a second message races in. The guard is a keyed slot, and a message that
  finds the slot busy goes to that conversation's **FIFO queue**. Two messages in one thread run in order, and
  different conversations run in parallel, up to a global `SemaphoreSlim` (`MaxConcurrentTurns` in limits).
- **Busy policy = queue.** Hermes defaults to interrupt; this plan chooses queue because a chat user
  typing "and also…" mid-turn expects both to be answered. `/stop` is the explicit interrupt (C8). A queued item
  that's waiting is acknowledged with a reaction or short notice only if Step 1 finds a cheap one per platform.
  Otherwise the user sees nothing until it starts.
- **Commands bypass every queue** and run inline. Otherwise `/stop` waits behind the very turn it should stop
  (Hermes `gateway/AGENTS.md:20-28`, issue #4926).
- Two runs on one grain interleave at `yield` points (`AgentGrain.cs:201-208`), so the per-conversation guard is
  what keeps two chat turns from interleaving on one session. A browser turn on the same conversation can still
  interleave, as it can today with a reminder. That's accepted.
- Set `RequestContext` user identity inside each turn's own async scope (old C5 / M3), or `/usage` and `/logs`
  lose attribution.

### C8 — Chat commands

`/new`, `/stop`, `/model [id | list]`, `/agent [name | none | list]`, `/sethome`, `/status`, `/help`. They're parsed
from the **start** of an owner message only, and dispatched through a name → handler table, never an if-chain
(Hermes `run_busy.py:998-1022`; S3776 would punish the chain anyway).
- Slack swallows a leading `/` as a slash command unless an app registers it, so on Slack the prefix is `!`
  (Hermes `slack/adapter.py:1023`, `typed_command_prefix`). Step 1 confirms. Discord DMs pass `/text` through when
  no app command matches; Step 1 confirms.
- Telegram registers the menu with `setMyCommands` on connect (Hermes `telegram/adapter.py:2707`).
- `/stop` calls `IAgentGrain.StopAsync` (`IAgentGrain.cs:122-124`, `[AlwaysInterleave]`) for the route's current
  conversation, and clears that conversation's queue.
- `/model list` and `/agent list` answer from `GET /api/models`'s service (`IUserModelCatalogProvider`) and
  `IAgentRegistryGrain.GetAllAsync()`. Lists are capped and chunked.
- In mention-gated groups, a command still needs the mention, except `/new`, `/stop` and `/help` (Hermes
  `telegram/adapter.py:5994`). Keep the rule identical on every platform.

### C9 — Proactive delivery (origin first, then home)

Hermes delivers cron output to a `DeliveryTarget`: "origin", a home channel, or an explicit chat
(`gateway/delivery.py:110-140`). Agent42 has three outputs nobody watches: the reminder path, the agent-run /
email path (`EnqueuePromptAsync`), and the model's `send_push_notification`.

- **Origin:** when the route mints a conversation, the pipeline calls `IAgentGrain.SetChatOriginAsync(origin)`
  **before** the first `ChatAsync`. The origin is stored as a trailing `[Id(n)]` on grain state, default `null`
  (rule 2). A reminder or queued run in that conversation delivers its **reply text** back to that chat.
- **Home:** a connection may be marked "deliver notifications here" with a home route set by `/sethome`. Away
  replies and `send_push_notification` from **any** conversation go to the home chat, **in addition to** Web Push.
  Push behaviour is unchanged (ADR 0028/0075).
- **The port:** `IChatDeliverySender.DeliverAsync(userId, ChatDelivery(conversationId, origin?, title, text))` in
  Agents, implemented by the Backend gateway (in-process; single replica). It's a **per-host port** (ADR 0105, rule
  7). `AddAgent42Silo()` only `TryAdd`s host-invariant services (`Agent42SiloRegistration.cs:30-60`), while ports
  such as `IAgentWebhookSender` are registered by each host and enforced by `ThrowIfPortsMissing`. So Backend and
  DevHost register the gateway sender, `TestSiloConfigurator` registers a recording one, and every other test silo
  configurator registers `NullChatDeliverySender` (see Affected areas).
- **Reply text isn't kept today:** `EnqueuePromptAsync` discards drained events (`AgentGrain.cs:466-468`).
  Accumulate `TextDelta` / `TextReset` there into a bounded buffer (the webhook payload already has a truncation
  rule, `AgentGrain.Webhooks.cs:34`; reuse it). Call delivery **next to** `SendAwayReplyPushAsync`, at its two
  sites only (gotcha "`SendAwayReplyPushAsync` belongs in the two async callers, never in `ChatCoreAsync`").
- **Never deliver a turn that came from the chat back to the chat a second time.** A chat-driven turn is streamed
  by the pipeline, which runs `ChatAsync`, not `EnqueuePromptAsync`, so it never reaches those two sites. Pin this
  with a test.
- Incognito conversations never deliver (as push refuses them, `AgentGrain.PushTool.cs:62`).

### C10 — Libraries: hand-roll Telegram and Slack, use a library for Discord

- **Telegram:** about 10 Bot API calls (`getMe`, `getUpdates`, `sendMessage`, `editMessageText`,
  `sendChatAction`, `getFile`, file download, `sendPhoto`, `setMyCommands`, `deleteWebhook`). Hand-roll them on a
  named HttpClient, with no package. `getUpdates` is a long poll, so give it **its own client** with a timeout
  above the poll timeout (rule 8).
- **Slack Socket Mode:** `apps.connections.open` (app-level `xapp-` token, scope `connections:write`) returns a
  `wss://` URL. Every envelope must be **acked by `envelope_id`**. Slack disables Event Subscriptions after
  sustained un-acked envelopes, so ack **every** envelope, including the ones that are ignored (Hermes
  `slack/adapter.py:1648-1664`). Handle the `disconnect` envelope by reconnecting. Web API calls are hand-rolled
  as in old C9.
- **Discord:** the gateway needs heartbeat, identify, resume, zlib, intents and close codes. **Don't hand-roll
  it.** Step 1 picks between `Discord.Net.WebSocket` and `NetCord` (both on NuGet at plan time). Criteria:
  .NET 10 support, active maintenance, DM + message-content behaviour, a small dependency tree. **Resolve the
  version with `dotnet add package`, never from memory** (hard rule 4), and pin it in `Directory.Packages.props`.
- **Every named client:** `RemoveAllResilienceHandlers()` (rule 8: an inherited retry **double-posts** a send that
  timed out after the platform accepted it) and an explicit `Timeout`.

### C11 — Secrets, persistence, and a token-in-URL trap

- Credentials are protected in the API layer under `"Agent42.ChatGateway.Secret.v1"`, with
  `WebhookSecretProtector`'s shape. They're write-only: the GET says only "set" or "not set". On save, call
  `ValidateAsync`. An invalid token gets a clear 400. Persist the bot's id and name.
  - Telegram: bot token.
  - Slack: bot token `xoxb-` **and** app token `xapp-`. Warn if a user token `xoxp-` is pasted (Hermes
    `slack/adapter.py:1586-1613`).
  - Discord: bot token.
- **The Telegram token is in the URL path** (`https://api.telegram.org/bot<token>/sendMessage`, and file
  downloads at `/file/bot<token>/…`). OTel redacts query strings by default but **not paths** (gotcha "OTel redacts
  an HttpClient span's query string by default", `.agentic/gotchas.md:646-690`). It leaks on **two** channels:
  - **Spans.** Use the technique that gotcha prescribes: a `DelegatingHandler` on the Telegram clients that wraps
    the send in `SuppressInstrumentationScope`, so no HttpClient span is emitted for these calls. Then emit a span
    of our own if one is wanted, named by Bot API method (`telegram.sendMessage`) with no URL. Prefer it over an
    enrich hook: a hook has to know every tag the instrumentation sets now **and in future versions**, while
    suppression doesn't.
  - **Logs.** `IHttpClientFactory` logs "Start processing HTTP request … {uri}" for every named client. Those logs
    flow through `builder.Logging.AddOpenTelemetry` (`src/Agent42.ServiceDefaults/Extensions.cs:54, :141`) into
    OTLP, App Insights and the owner-visible `/logs` ring buffer. Nothing in `src` filters them today. Call
    **`RemoveAllLoggers()`** on both Telegram named clients, and never log a request URL in `TelegramBotApi`.

  **AC12b proves both**, with captured activities **and** captured log records.
- `ChatConnection` is a **class with property initialisers, not a positional record** (gotcha "A record parameter
  a Cosmos document lacks binds to `null`"). There must be no collection expression into an `IReadOnlyList<T>`
  state field. The state names ride the shared `agent-state` container, so confirm that no Bicep container is
  needed (gotcha "Cosmos containers must be pre-declared in Bicep").
- Delete and disable **remove the connection from the directory FIRST**, then stop the session, then delete. The
  share-link order applies, since there's no backstop that fails closed on an orphan.

### C12 — Voice notes need server-side STT, which ADR 0038 deferred

ADR 0038 is TTS only: "STT would be a separate ADR" (`0038-server-side-speech-model.md:51-53`). Voice in the
browser is `SpeechRecognition` (ADR 0006/0037). Phase 1c therefore adds **the second new ADR**:

- `ISpeechTranscriptionService.TranscribeAsync(bytes, fileName, mediaType, ct) → string`. It's per user, resolved
  next to `GetSpeechService`, over the user's speech provider with a **transcription model** named in the overlay.
  Step 1 confirms the Azure/OpenAI deployment name shape and the `AudioClient` transcription call. **There's no
  operator model** (CLAUDE.md, ADR 0019/0030): a user without one gets a curated "add a transcription model to use
  voice notes" reply.
- **Formats:** Telegram voice is Ogg/Opus, Discord voice messages are Ogg, and Slack clips are mp4/m4a labelled
  oddly (Hermes `slack/adapter.py:930-935`). WhatsApp is Ogg/Opus, and Signal Android sends raw ADTS AAC (Hermes
  `signal/adapter.py:590-594`). Step 1 lists which ones the transcription endpoint accepts **without ffmpeg**.
  Anything unsupported gets a curated reply. **Don't add ffmpeg** in this plan (image size, CVE surface).
- The transcript becomes the turn text, prefixed so the model knows it's a voice note. It's echoed back as one
  quoted line, so the owner sees what was heard (Hermes `stt_echo_transcripts`). On failure send **one** curated
  reply, never two (Hermes `run_inbound.py:1481-1483`).
- Egress: audio goes to the user's own configured provider, the same trust path as their chat model. Say so in
  the ADR.

### C13 — Egress and injection (the substance of the gateway ADR)

- **Egress (ADR 0013 extension, user-gated):** replies, which can carry RAG excerpts, tool output and memory recall,
  are written into a third-party platform under the user's own bot: Telegram, Slack (workspace admins may be able to
  export them), Discord, Meta, and Signal (end-to-end encrypted to the user's own devices). **In groups, every
  member sees the reply.** The panel's group toggle says so in plain words. It is opt-in per connection, so this is
  **not** ADR 0008's rejected always-on shape. Don't write "following ADR 0008".
- **Inbound:** the sender is pinned to the paired owner by the platform's own identity (C4). The residuals are a
  compromised platform account and a token at rest (encrypted and rotatable). Files and voice notes the owner
  forwards are still untrusted content that composes with memory (no delete path) and the fetch tools.
- **Download host pinning (old C8):**
  - Slack: `files.slack.com` with a Bearer token, no off-host redirect, and `text/html` treated as an auth failure.
  - Telegram: only `api.telegram.org/file/bot…`.
  - Discord: attachment URLs are on Discord's CDN hosts, which Step 1 lists; no token is sent.
  - Cap reads at `MaxAttachmentBytes`. A platform token must never go to any other host.

### C14 — Complexity, registrations, tests

- The pipeline is decomposed from the start into dedupe → authz → command → route → guard → files → drain. The
  events handler of old C14 would have gone past S3776's 15. Each adapter's update parser is a pure projection,
  tested with **captured payload fixtures** from Step 1.
- `AddChatGateway` is called in Backend and DevHost. Endpoints resolve services with `IServiceProvider.GetService`
  and return 503 when absent (the `/api/speech` pattern, rule 7). `Hosts_RegisterChatGateway` guards both hosts.
  The new grains take no injected dependencies.
- **Test seams:** an `IChatPlatformAdapter` fake (in-memory sessions recording sends, edits and typing, plus an
  inbound injector) plus a scripted `IChatGatewayLease` fake drive the pipeline end-to-end on the test cluster with the canned chat client. Adapter tests
  use a stub `HttpMessageHandler`. Socket adapters use a local `WebSocket` server (`LocalBridgeKestrelTests.cs:57`
  shows the Kestrel shape). Fake `TimeProvider` everywhere for throttles, pairing expiry, the lease and backoff.

## Steps

### Phase 1a — core + Telegram (MR 1)

**Step 1: Spike the contracts.** No repo change; findings go under a **Spike findings** heading in this file.
Use the `spike-dotnet` skill for code questions and current platform docs for protocol questions (never memory).
Answer these:
- (a) Telegram: `getUpdates` long-poll semantics, `offset`, and `allowed_updates`; the HTML parse-mode tag set;
  the `sendMessageDraft` availability for bots; `editMessageText` limits; whether a 429 carries
  `parameters.retry_after`. Capture DM, group-mention, reply-to-bot, photo, document and voice update fixtures.
- (b) Slack Socket Mode: `apps.connections.open`, the envelope and ack format, the `disconnect` envelope, and the
  minimal manifest (scopes, `message.im` + `app_mention` + `message.channels`, `socket_mode_enabled`). Find out
  whether `markdown_text` works on `chat.postMessage` and `chat.update`, and whether `!` or `/` should prefix
  commands. Capture fixtures, including `file_share` and our own echoes.
- (c) Discord: compare `Discord.Net` and `NetCord` on the C10 criteria; whether DMs need the privileged
  `MESSAGE_CONTENT` intent; the attachment CDN hosts; DM `/text` pass-through.
- (d) The OTel token-in-path redaction mechanism (C11).
- (e) The lease mechanism (C3) and how ACA revision overlap behaves for this app's revision mode.
- (f) Whether ADR 0084 compaction bounds a long plain conversation (C5).
- (g) For phase 1c: the transcription call, deployment shape and accepted formats (C12).
- (h) Re-confirm every number in `ChatGatewayLimits` from the platform's docs.

**Verify:** findings are written here, and later steps cite them.

**Step 2: Agents-side state.** Add `ChatPlatformKind`, `ChatConnection` + `UserChatConnectionsGrain`
(create, update, set credentials → new pairing code, re-pair, `TryPairAsync` with lockout, enable or disable,
delete with directory-first ordering, set the Telegram offset, record a fault), `ChatConnectionDirectoryGrain`,
`ChatRouteGrain` (current id, `/new` epoch, model override, target, home flag), `ChatPairingCode` and
`ChatGatewayLimits`. *Parallelizable with Step 3.*

**Verify:** `UserChatConnectionsGrainTests` (pairing once, expiry, wrong code, lockout after 5, re-pair clears the
owner, delete deregisters first), `ChatConnectionDirectoryGrainTests`, `ChatRouteGrainTests` (mint on first use,
`/new` mints a fresh id, `IsValid` rejects other prefixes and traversal) and `ChatPairingCodeTests`.

**Step 3: Adapter contract + pure helpers.** Add `IChatPlatformAdapter`, `IChatPlatformSession`,
`InboundMessage`, `ChatSendResult`, `ChatPlatformCapabilities`, `ChatMessageChunker`, `InboundDeduper` and
`ChatCommands` (parse + table). *Parallelizable with Step 2.*

**Verify:** `ChatMessageChunkerTests` covers the newline and then space split, never inside inline code, fence
close and reopen, `(n/m)` markers, the UTF-16 counting mode and the Discord 8-chunk cap. Also
`InboundDeduperTests` (TTL, bound, survives a session swap) and `ChatCommandsTests` (prefix per platform,
unknown command → help, a command only at the start).

**Step 4: Secret protector, management endpoints, registration.** Add `ChatGatewaySecretProtector`,
`AgentApi.ChatGateway.cs`, `ChatGatewayRegistration` and the `MapAgentApi` call. Management covers
`GET/POST /api/chat-connections`, `PUT …/{id}` (name, target, enabled, groups, voice, notifications;
credentials write-only + `ValidateAsync`), `POST …/{id}/repair` and `DELETE …/{id}`. Update the
`ProjectLayoutTests` lists, `AgentApiParts()` and its count comment, and add the `Hosts_RegisterChatGateway`
theory.

**Verify:** `ChatGatewayApiTests` on a `SharedApiFixture` subclass with the fake adapter. Cover: a GET never echoes
secrets; invalid credentials → 400; delete stops the session; a purpose-isolation test for the protector.
`ProjectLayoutTests` and `AgentApiReachabilityTests` are green.

**Step 5: `ChatGatewayService` (supervisor + lease).** Start everything from the directory, restart on change,
reconnect with jittered backoff, stop on an auth fault with a curated reason, take and renew the lease (Step 1e),
and release it on stop.

**Verify:** `ChatGatewayServiceTests` with the fake adapter, the fake lease and fake time:
- boot starts every enabled connection;
- a session that keeps failing backs off;
- an auth fault stops and records;
- a second service instance without the lease connects nothing, then **connects once the first releases**, with
  no window where both hold sessions (AC13b);
- a lost renewal disconnects everything;
- restart applies a credential change;
- delete deregisters from the directory **before** the session stops, so a restart racing the delete can't
  revive the session.

**Step 6: `ChatTurnPipeline` + streamer + typing.** Implement C4–C8 exactly: dedupe → own-echo/bot drop → owner
check / pairing → command (inline) → route → synchronous guard → queue → semaphore → `RequestContext` → drain →
`ChatReplyStreamer`.

**Verify:** `ChatTurnPipelineTests` (test cluster + canned client + fake adapter):
- a stranger is ignored and creates no state;
- pairing binds the owner and never creates a conversation;
- an owner DM lists a conversation and replies;
- a second message in one route queues and runs in order;
- two routes run concurrently;
- `/stop` mid-turn stops it and doesn't wait in the queue;
- `/new` starts a fresh conversation;
- `/model` overrides later turns;
- `/agent` binds the next conversation only;
- a deleted target replies and runs nothing;
- the user identity is on the turn's `RequestContext`;
- **the turn runs `interactive: false` and `ephemeral: false`**: a fake grain recording the arguments, or a
  client tool in the canned model that would otherwise pause, completes without a browser;
- C5's turn-count rollover mints a fresh conversation **with** the notice line, and it never happens silently
  (only if Step 1f says it's needed);
- a **hanging fake session doesn't stop the turn persisting** (mutation-check it, as
  `AskAgent_TimedOut_StillPersistsTheTurn` was);
- the session's own echo is dropped.

`ChatReplyStreamerTests` (fake time): edits are throttled and skipped when unchanged; the final always lands;
formatting is applied on the final only; `BadFormat` → plain resend; intermediate `RateLimited` is skipped; the
final retry is capped and never sleeps past the cap; 3 floods → final-only; over-limit → ordered chunks; the
no-edit capability → final only; `TextReset` clears the buffer; `Reasoning`, `Todos` and tool events never reach
the session; typing stops before the final.

**Step 7: Telegram adapter.** Add `TelegramBotApi` (hand-rolled, own long-poll client, C10), `TelegramAdapter`
(long poll; offset persisted after each processed batch; `deleteWebhook` on connect; `setMyCommands`; 409 →
fault "another poller is using this bot") and `TelegramText` (Markdown → HTML), plus the span redaction (C11).

**Verify:** `TelegramAdapterTests` with a stub handler and Step 1 fixtures (DM, group mention by entity,
reply-to-bot, own echo, a 429 with `retry_after` → `RateLimited`, 409 → fault, offset advances). Also
`TelegramTextTests` (bold, italic, code, fences, links, escaping of `& < >`, unbalanced input never throws), and
**`TelegramBotApi_NeverLeaksTheToken`**. That test registers the client the way `AddChatGateway` does, with the
real HttpClient instrumentation and a capturing logger provider, and asserts that **no span tag and no log
record** contains the token.

**Step 8: Frontend panel.** Append the `#tabChat` rail button and `#panelChat` **last** (so the index pins hold)
and keep `dom.js`'s `profileTabs` in the same order. The panel lists each connection with its platform, name,
target and status (*needs credentials* / *awaiting pairing: code, expiry* / *connected as @bot* /
*faulted: reason*). The dialog has the platform picker; per-platform credential fields with "set" indicators;
setup steps linking `docs/integrations/chat-apps.md`; target (plain chat + stored agents); toggles for groups
(with the C13 warning), voice notes and notifications; re-pair; delete. The copy says that a target change
affects new conversations only. Import from `main.js`, add to `SHELL`, **bump `CACHE`** (rule 3), and update the
`PreferencesLayoutAssetsTests` count to 7.

**Verify:** `ChatGatewayPanelAssetsTests` (ids, the import, endpoint paths), `PreferencesLayoutAssetsTests`,
`ConfigHealthAssetsTests`, `UserSkillPickerAssetsTests`, and `PwaAssetsTests` + the other cache-name pins.

**Step 9: Serialization goldens.** Add every new state root to `PersistedTypeNames()` with committed golden
documents, plus the binary goldens. **Never regenerate a golden at test time.**

**Verify:** both suites are green.

**Step 10: DevHost loopback + docs for 1a.** DevHost registers a `LoopbackChatAdapter` (kind `Telegram` stand-in,
DevHost only), with a dev-only endpoint to inject an inbound message and read the recorded outbound. Exercise this
against `http://localhost:61326`: create → pair → DM → stream → `/new` → `/stop` → a stranger ignored. Write ADR
**0111** (gateway; `/decide`) covering G1–G4, C2–C5, C7, C9's design (even though 1c implements it), C10, C11 and
C13. Update `CLAUDE.md` rules 12 and 13 and the feature map, ADR 0039/0040/0065, and
`docs/integrations/chat-apps.md` (Telegram section). Add the 1a gotchas to `.agentic/gotchas.md`:
- the Telegram token sits in the URL **path**, and leaks through spans **and** HttpClient logs (C11);
- the lease versus ACA revision overlap (C3);
- commands bypass every queue, and the guard is installed before any await (C7);
- `interactive: false` for every chat-driven turn (C6);
- formatting on the final frame only (C6).

Then `/sync-docs`.

**Verify:** the transcript pasted into the MR; `node .agentic/scripts/check-doc-paths.mjs` green; the full suite +
`dotnet format --verify-no-changes`.

### Phase 1b — Slack + Discord (MR 2)

**Step 11: Slack adapter.** Socket Mode over `ClientWebSocket` (ack every envelope; handle `disconnect`; reconnect
via the supervisor). The update parser follows old C4's filter, with `im` + `app_mention` + tracked threads.
`SlackWebApi` covers `auth.test`, `chat.postMessage`, `chat.update`, file download (host-pinned) and upload. Add
`SlackText` per Step 1b, with the command prefix from Step 1b.

**Verify:** `SlackAdapterTests` against a local websocket server: envelopes are acked including ignored ones;
`disconnect` → reconnect; fixture rows for `file_share` accepted, `message_changed` / `bot_id` / own bot user
rejected, and `app_mention` in a channel → threaded reply. Plus `SlackWebApiTests` (old AC11 rows).

**Step 12: Discord adapter.** Use the Step 1c library, added with `dotnet add package` and pinned in CPM. Requests
only the intents Step 1c says DMs and mentions need. Send with `allowed_mentions` none. Close code 4004 → auth
fault. A reply in a channel creates a thread from the mention. `DiscordText` converts tables to bullets.

**Verify:** `DiscordAdapterTests` over the library's abstractions or a projection seam (whichever Step 1c shows is
testable). The parser covers own echo, other bot, mention, DM and thread. Also `DiscordTextTests`, and a 2000/8
chunk row.

**Step 13: Docs for 1b.** Add the Slack and Discord sections of `chat-apps.md`, including Slack's generated
manifest, and amend ADR 0111 with the Step 1b/1c findings. Add the 1b gotchas: **every Slack envelope must be
acked**, ignored ones included, or Slack disables the subscription; Slack's command prefix; Discord's
`allowed_mentions`.

**Verify:** doc-path check; full suite.

### Phase 1c — files, voice, groups, delivery (MR 3)

**Step 14: Files in and images out.** Inbound files are validated from metadata **before** any download
(`AgentRunFileError`), then downloaded (host-pinned, capped) and passed to `StoreAgentRunFilesAsync`. Images
become `AttachmentRef`s, and documents are registered **before** the turn (gotcha "A mid-run ingest cannot rebuild
the agent"). Decide all-or-nothing (agent-run) vs skip-and-note (email). Recommendation: **skip-and-note**, since a
chat user can't easily resend a mixed batch; record it in the ADR. Outbound: an `Image` event → `OpenReadAsync` →
`UploadImageAsync` after the text final.

**Verify:** `ChatTurnPipelineTests` rows: an image reaches the turn; a document is registered first; an oversized
file is skipped with a note; an off-host URL is never fetched; a generated image is uploaded once.

**Step 15: Voice notes (the STT ADR, next free number).** Add `ISpeechTranscriptionService` + `OpenAITranscriptionService`, per-user
resolution, the overlay field and the provider dialog field (CACHE bump again if `wwwroot/js` changes). The
pipeline transcribes before the turn, echoes the transcript and sends one curated reply on failure. Formats are
per Step 1g.

**Verify:** `OpenAITranscriptionServiceTests` (stub handler, request shape), plus pipeline rows: a voice note
becomes a turn with the transcript; no transcription model → a curated reply and no turn; an unsupported format →
a curated reply.

**Step 16: Groups with mentions.** The per-connection toggle (default **off**). Only the owner triggers (C4). A
mention or reply-to-bot is required. Commands follow C8's mention rule. Slack and Discord answer in a thread. In a
Telegram group, replies are `reply_to` the triggering message.

**Verify:** pipeline rows: in a group, the owner without a mention is ignored; the owner with a mention is
answered; a non-owner with a mention is ignored; groups off → everything in groups is ignored; `/stop` without a
mention works.

**Step 17: Proactive delivery.** Add the `IChatDeliverySender` port + Null + recording senders; the trailing
`ChatOrigin` on grain state (golden + Cosmos JSON test, rule 2); text accumulation in `EnqueuePromptAsync` and the
reminder path; delivery at the two away-reply sites and in `send_push_notification`; and the gateway
implementation (origin route, else home route if notifications are on).

**Verify:** `ChatDeliveryTests` (test cluster):
- **the write path:** a pipeline-minted conversation has `Origin` set before its first turn; a continued one
  isn't re-written;
- a reminder in a chat-origin conversation delivers its reply text to that route;
- an agent-run in a web conversation delivers to the home chat only when notifications are on;
- the push tool delivers to the home chat as well as Web Push;
- a chat-driven turn is **never** delivered back (no double post);
- incognito never delivers;
- `GrainStorageSerializerTests` covers a grain state document without `Origin`.

**Step 18: Docs for 1c.** The STT ADR (+ an amendment banner on 0038), amendment banners on 0028/0075, the ADR 0111
amendment (files policy, groups), the `chat-apps.md` sections, and the 1c gotchas: the delivery port is per host;
reply text is accumulated only at the two away-reply sites; a chat-driven turn is never delivered back.

**Verify:** doc-path check; full suite; coverage script.

**Step 19: Post-deploy live check (manual, after MR 1/2/3 merge).** Use a real Telegram bot, Slack app and
Discord bot. For each: pair, DM, stream, `/new`, `/stop`, an image + a PDF + a voice note, a group mention, a
stranger ignored, a scheduled prompt delivered to origin. Trigger one ACA revision rollover with a connection live,
and confirm there's no double reply and no 409 storm (C3). Record the results in ADR 0111's verification note
**through a follow-up `docs/*` branch and MR**, never a commit to `main` (hard rule 1). The same applies to the
live checks in Steps 22 and 31.

**Status (2026-10-21): skipped on purpose, still open.** Phase 3 does not depend on it. Its open questions (Ogg
accepted by STT, Discord `/text`, the Blob lease on real storage) stay open.

### Phase 2 — WhatsApp Cloud API (MR 4; re-verify before starting)

**Step 20: Spike + design check.** Cover Meta app setup for a single user (test number vs a real WABA), the
webhook GET handshake (`hub.verify_token`, constant-time compare, echo `hub.challenge`) and the POST
`X-Hub-Signature-256` over the **raw body** (Hermes `whatsapp_cloud/adapter.py:705-758`). Always 200 once
accepted (Meta retries non-200 for 7 days). Dedupe by `wamid`. Media is fetched in two steps (`GET /<media_id>` →
signed URL → GET with auth). The **24-hour customer-service window** (error 131047): proactive delivery (C9)
outside the window needs a template message. Decide: deliver only inside the window, else fall back to push and
note it. There's no edit, so the capability is final-only. The WhatsApp formatting subset.

**Step 21: Public webhook + adapter.** `POST|GET /api/chat-in/whatsapp/{routeToken}`. The route token resolves
through **its own** directory grain (gotcha "Each credential namespace gets its own directory grain type"). That
means a raw stored token, rotate and delete deregister first, a uniform 401, and the userId comes from the
directory, **never `GetUserId()`** (old C2, gotcha "An Easy-Auth `excludedPaths` endpoint is publicly reachable").
Credentials are the phone number id, a System User access token, the app secret and the verify token. Add the
`excludedPaths` entry `'/api/chat-in/whatsapp/*'`.

**Verify:** `WhatsAppWebhookTests` (handshake, bad signature → 401, the replay or duplicate `wamid` runs once,
unknown token → 401); `EasyAuthExclusionTests` rows (the path is excluded; `/api/chat-connections` and name stems
are not).

**Step 22: Docs + live check.** ADR 0111 amendment, the `chat-apps.md` WhatsApp section, the ADR 0002 carve-out
list, `README.md`, and a live check with a real number.

### Phase 3 — Signal via signal-cli (MR 5)

**Refined on 2026-10-21 and now runs BEFORE phase 2** (the user's call). The coarse Steps 23–25 that stood here
assumed a linked device and a sidecar. The spike refuted both, so they are replaced by the section
**"Phase 3 — Signal, refined plan"** below (Steps 23–31). Phase 2 (WhatsApp) stays coarse and unstarted.

## Phase 3 — Signal, refined plan

planned-against: main @ 2875f96 (the #214 merge). Branch `feature/chat-gateway-phase-3-signal` from there.

**Drift check (41f0a41 → 2875f96):** only phases 1a/1b/1c (#212–#214) and past chats (#211, ADR 0112). Nothing
outside the gateway changed under this plan. At execution, re-run `git log --oneline 2875f96..HEAD` and re-read any
touched file named below.

**Goal:** each user connects a **dedicated Signal number** of their own, registered in a `signal-cli` daemon that the
backend runs as a child process, and talks to Agent 42 there like on the other three platforms. No operator secret,
and no inbound HTTP surface: the daemon listens on `127.0.0.1` only.

### Decisions taken in the phase 3 interview (2026-10-21)

Don't relitigate these. Changing one is a new planning round.

- **S1: a dedicated number, not a linked device.** This amends G2's "each user links their own Signal account". A
  linked device of the user's own account could reach them only through Note-to-Self. Its replies would be sent *as the
  user*, phone notifications for them are unreliable (`--notify-self`, issue #373), and the daemon would receive and
  store **all** of the user's inbound Signal traffic and attachments. With a dedicated number, the bot is its own
  Signal account, like a Telegram bot. The existing pairing code, owner check, DMs and groups apply unchanged. The
  cost is that the user needs a spare number (a SIM, a VoIP number, or a landline with a voice code).
- **S2: `signal-cli` is a child process of the backend, started only while the gateway holds its lease.** This
  replaces the plan's sidecar. The daemon has **no HTTP auth**, and its store is SQLite in WAL mode behind Java
  `FileLock`s. A sidecar would run in both revisions during a rollover, two daemons on one store, and only an
  unverified SMB lock would stand between them. Under the lease (C3) only one daemon runs at a time, by construction.
  The backend image grows by a JRE 25 plus signal-cli (about 200 MB).
- **S3: the store lives on Azure Files, verified first.** A new share on the existing copilot Files account, mounted on
  the backend container only (the ADR 0107 shape). **Step 24 measures single-writer SQLite WAL on that mount in the
  real Container Apps environment before any adapter code.** If it fails, the fallback is local disk plus a Blob
  snapshot (Step 24's fallback paragraph). That fallback changes S3, so it is a short new planning round, not an
  improvisation.
- **Phase order:** phase 3 ships before phase 2. Step 19 stays open and is not a prerequisite.

### Spike findings (Step 23, 2026-10-21; signal-cli master @ `46b40b6`, latest stable **v0.14.9**, 2026-10-07)

Sources: the AsamK/signal-cli README, `man/signal-cli.1.adoc` (M), `man/signal-cli-jsonrpc.5.adoc` (J), the CHANGELOG
(C), and the source files named. Re-verify anything marked *unverified* during Step 24 or the DevHost exercise.

- **Runtime:** JRE **25** plus the bundled native libsignal (x86_64 glibc). A GraalVM native build exists, but the
  README calls it "EXPERIMENTAL", so **use the JVM build**. The official images are `ghcr.io/asamk/signal-cli`
  (`<ver>` and `<ver>-native`), and the JVM one is on Zulu JRE 25. Licence **GPL-3.0-only**: we run it as a separate
  program inside a private image, which is aggregation, not linking. Record that in the ADR.
- **Expiry:** "signal-cli releases older than three months may not work correctly" (README). Releases come every 3–5
  weeks, so the bundled version needs a visible age check (Step 30).
- **Daemon:** `daemon --http=127.0.0.1:<port>`. In multi-account mode (no `-a`), each JSON-RPC call names `account`.
  - Endpoints: `POST /api/v1/rpc` (needs `Content-Type: application/json`), `GET /api/v1/events?account=<n>` (SSE,
    event `receive`), and `GET /api/v1/check` (200).
  - **No auth.** The Host header is validated only when bound to a loopback name, and that validation is OFF on
    `0.0.0.0`, so the bind is load-bearing.
  - Flags: `--receive-mode on-start|on-connection|manual`, where the default is `on-start`; `--ignore-stories`;
    `--ignore-attachments`, which must NOT be set because `getAttachment` reads the downloaded file;
    `--send-read-receipts`.
- **Message loss traps:**
  - In `on-start` mode the daemon receives and **acks** messages from boot. The SSE buffer only starts recording an
    account when a client first connects for it, so anything that arrives before that is gone. **Use
    `--receive-mode=on-connection`** (inferred from the code; DevHost must confirm), so messages wait on Signal's
    server until our stream is up.
  - A stream opened without `?account=` captures the account list at connect time, so **open one stream per
    account**.
  - The in-memory replay (1000 events, `Last-Event-ID`) does not survive a daemon restart.
- **Registration (S1):** multi-account mode exposes `register`, with `captcha` and `voice` params. A captcha comes from
  `https://signalcaptchas.org/registration/generate.html` as a `signalcaptcha://…` token. **Whether `verify` is exposed
  over JSON-RPC is *unverified*.** J lists "except register, verify and link". Step 24 checks the source of `VerifyCommand`. If it isn't exposed, the fallback is a one-shot CLI
  invocation `signal-cli --config <dir> -a <n> verify <code> [--pin]`, run while the daemon holds no lock on that
  account, which means right after `register`, before the account is loaded. Measure that the daemon picks the account
  up afterwards; in multi-account mode it may need `addAccount`/a restart.
- **Phone-side or server-side loss:** an `AUTHENTICATION_FAILED` closes and removes the account **silently**. Nothing
  is emitted on SSE. Later RPCs fail with "Specified account does not exist". The adapter maps that error, and an SSE
  400 for an unknown account, to `ChatAuthException`.
- **Envelope:** `sourceUuid` (the ACI), `sourceNumber`, `sourceDevice` and `timestamp`, plus exactly one of
  `dataMessage | editMessage | storyMessage | syncMessage | receiptMessage | typingMessage | callMessage`.
  - `dataMessage` carries `message`, `groupInfo{groupId}`, `mentions[{uuid,start,length}]`, `quote{authorUuid,…}`,
    `attachments[{id, contentType, filename, size, isVoiceNote}]`, `reaction` and `remoteDelete`.
  - There is **no message id**: Signal identifies a message by sender + sent `timestamp`. Dedupe key
    `"{sourceUuid}:{timestamp}"`.
- **Sending:** `send` takes `{recipient | groupId, message, textStyle[], attachments[], quoteTimestamp, quoteAuthor}`.
  - `textStyle` is `"start:length:STYLE"` in **UTF-16 units**, with styles BOLD, ITALIC, STRIKETHROUGH, MONOSPACE and
    SPOILER.
  - `attachments` accepts `data:<mime>;filename=<name>;base64,<data>`.
  - The result `timestamp` is the message id. `results[].type` is one of SUCCESS, UNREGISTERED_FAILURE,
    NETWORK_FAILURE, and so on. A `token` in a result means proof-required (a captcha).
  - JSON-RPC errors: **-5 rate limit**, -6 captcha rejected, -4 untrusted key, -1 user error.
  - Text over **2000 UTF-8 bytes** is cut in the body and sent in full as an automatic long-text attachment.
- **Edits exist** (`editTimestamp`, since 0.11.10), but their limits are *unverified*. `sendTyping` lasts 15 s unless
  stopped.
- **Attachments in:** `getAttachment {id, recipient|groupId}` → `{data: base64}`. The daemon's cap is 150 MiB; our cap
  is `MaxAttachmentBytes`, checked from `size` first.
- **Storage:** `<config>/data/accounts.json`, `<config>/data/<n>.d/account.db` (SQLite, `journal_mode=wal`) and
  `<config>/attachments/`. A second daemon on the same dir **blocks** waiting for the `FileLock`. Over SMB, WAL and
  those locks are *unverified*, which is S3's measurement.
- **Not found:** the maximum devices per account, idle-unlink rules, and resource guidance. None of them matter with a
  dedicated primary number.

### Context & constraints (phase 3)

- **SC1: where phase 1's code is reused unchanged, and where it can't be.** It is reused unchanged:
  - `ChatTurnPipeline` (authz, pairing, commands, routing, queue, drain, files, voice, groups);
  - `ChatReplyStreamer` final-only path, `ChatMessageChunker`, `InboundDeduper` and `ChatDeliverySender`;
  - the file core (`AgentRunFileError` / `StoreAgentRunFilesAsync` overloads) and `ISpeechTranscriptionService`;
  - `ChatConnection` and its grains, and the protector. The credential is the E.164 number in
    `ChatCredentials.BotToken`, protected like any token.

  It **can't** be reused unchanged in these places:
  1. **The credential flow.** `ValidateAsync(ChatCredentials)` assumes the user pastes a working secret. Signal has a
     two-call registration (`register` → SMS/voice code → `verify`), so it gets two dedicated endpoints (Step 28).
     **The generic create and update endpoints REFUSE `Credentials` for Signal** with a 400 ("Signal numbers are
     registered, not pasted"). Otherwise user B could paste user A's registered number, pass a `listAccounts` check,
     and drive A's bot. Because a pasted Signal credential never gets past the API, the only way a credential is
     stored is `verify`, which runs under the caller's own claim (SC2). `ValidateAsync` for Signal is then only the
     supervisor's "does the daemon still hold it" check.
  2. **The supervisor's lease.** The daemon must die the moment the lease is lost, even while a registration holds it.
     `ChatGatewayService` gains a narrow hook, `IChatGatewayLeaseParticipant` (`OnLeaseRenewedAsync`,
     `OnLeaseLostAsync`):
     - it is notified **first**, before `StopAllAsync`, not after it, because a hung session `DisposeAsync` must not
       delay the kill;
     - it is notified on renewal failure, in the `ExecuteAsync` catch path, and in the `StopAsync` override, always
       before `lease.ReleaseAsync`;
     - it is idempotent, because `StopAllAsync` already runs from up to three places.

     `IChatGatewayControl` exposes `HoldsLease`, which today is only on the class.
  3. **Delete.** It must also tell the daemon to `unregister` + `deleteLocalAccountData` and release the number
     claim. The endpoint **reads the connection first**: its number is in the credentials, or in the pending-number
     field (SC2) when the registration was never finished. Then `DeleteAsync` (deregister first, as today), then
     `Control.StopAsync`, then the optional adapter interface `IChatPlatformCleanup` with that number, then the claim
     release. Cleanup is best effort: a failure is logged without the number and never undoes the delete. The claim is
     released even when cleanup fails, because a pending claim also expires (SC2).
  4. **Formatting.** There's no markup language: `SignalText` turns Markdown into `(text, textStyle[])`.
  5. **The panel.** A registration flow replaces the token field for Signal (Step 29).
  6. **Voice.** Signal voice notes are AAC, likely raw ADTS (C12). That is not on the STT list, so expect the existing
     curated format reply. Step 31 records it live; no transcoding (C12).
- **SC2: a number belongs to exactly one connection.** The daemon's account namespace is global across users. If user
  B ran `register` on user A's number, B couldn't verify (the code goes to A), but the call would reset A's local
  registration, which is a denial of service. **`ISignalNumberDirectoryGrain`**, keyed by the normalised E.164 number,
  is a claim `(userId, connectionId, state, claimedAt)` taken **before** `register` is called. It is its own grain
  type (gotcha "Each credential namespace gets its own directory grain type"), and a class with property
  initialisers.
  - **States:** `Pending` from claim until `verify` succeeds, then `Active`. A `Pending` claim **expires after 30
    minutes**: another connection may then take it, so an abandoned or failed registration can't squat a number
    forever. A failed `register` (captcha demand, rate limit, invalid number) releases the claim at once.
  - A number claimed `Active`, or `Pending` and unexpired, by another connection is refused with a generic 409 that
    doesn't say whose. Re-registering by the same connection is allowed.
  - **The pending number is persisted on the connection** as a trailing `[Id(23)] string? PendingSignalNumber` on
    `ChatConnection`, with a null default (rule 2). Setting it goes through a new grain method,
    `SetPendingSignalNumberAsync`, and `SetCredentialsAsync` clears it. `verify` reads it, so the verify request
    carries only `{code, pin?}`. Prove the field on the Cosmos JSON path (`GrainStorageSerializerTests`).
  - The **"needs registration"** status is derived, not stored: a Signal connection with no `CredentialsCiphertext`.
    It reads "awaiting code" when `PendingSignalNumber` is set.
  - The number is a grain key in clear (Cosmos is the operator's store, with the same trust as the encrypted
    credential's other neighbours). A hash of a phone number would be brute-forceable, so it would add nothing. Record
    this in the ADR.
  - **Accepted residual (record it in the ADR):** any signed-in user can make Signal send a code to, and sign out, the
    phone of an arbitrary number by calling `register` on it. Signal's own rate limits bound it, and today only the
    owner's principals can sign in (ADR 0002).
- **SC3: the daemon host (`SignalDaemon`).**
  - It starts lazily on first use, only when `HoldsLease`, as `java … signal-cli --config <dir> daemon
    --http=127.0.0.1:<port> --receive-mode=on-connection --ignore-stories --no-receive-stdout`. The process is started
    through an `ISignalProcessLauncher` seam.
  - Readiness is `/api/v1/check`. A crash restarts it with capped backoff.
  - On lease loss or shutdown, the participant hook **stops** it: SIGTERM, so SQLite closes cleanly, then SIGKILL
    after 5 s. It waits for exit before the lease is released.
  - **A watchdog closes the renewal window.** A renewal is detected as failed only at the next 20 s tick, `RenewAsync`
    has no local deadline, and SDK retries can hold a hung renewal past the lease's 60 s, while a new revision
    acquires it. So the daemon stops itself when no `OnLeaseRenewedAsync` arrived within **40 s** (less than the
    lease duration minus one renew interval). Re-check the lease's duration and interval in `ChatGatewayLease.cs` at
    execution, and derive the 40 s from them in code, never as a literal.
  - stdout and stderr go to the logger at Debug, **minus phone numbers**: signal-cli logs them, and `/logs` is
    owner-visible. Redact E.164 numbers with a regex before logging.
  - `SignalOptions.DaemonUrl` set means "external daemon, never spawn". DevHost and the tests use that.
  - The JVM heap is capped through `JAVA_OPTS` (`-Xmx`). Step 24 measures the RSS to size the container.
- **SC4: the session.** One SSE stream per account (`?account=`), read on the session's task. A dropped stream
  reconnects inside the session with a short backoff, as Slack's `disconnect` does (phase 1b). A 400 unknown-account
  or "does not exist" → `ChatAuthException` ("Signal no longer has this number registered; register it again").
  - **Inbound filter:** accept `dataMessage` with text, attachments or both. Drop `syncMessage` (no other device
    exists, so any sync is foreign), `storyMessage`, `receiptMessage`, `typingMessage`, `callMessage` and
    `editMessage`; editing never re-runs a turn (Out of scope). Also drop a `dataMessage` that is only a reaction or a
    `remoteDelete`, and anything whose `sourceUuid` is the bot's own ACI.
  - `MentionsBot` comes from `mentions[].uuid == bot ACI`, never the text. `RepliesToBot` from
    `quote.authorUuid == bot ACI`. The chat id is `groupInfo.groupId` or the sender's ACI.
- **SC5: capabilities.** Final-only (`SupportsEdit = false`). Edits exist, but their caps are unverified, and each
  edit re-notifies, so streaming by edit is a later refinement. `MaxMessageLength` is **2000** (conservative against
  the 2000-UTF-8-byte cut; the long-text attachment covers multibyte overflow). `MaxChunks` is 8, `TypingRefresh`
  10 s (Signal clears it after 15 s), and the command prefix is `/` (Signal has no slash commands).
- **SC6: errors → `ChatSendErrorKind`.**
  - JSON-RPC -5 → `RateLimited` with `retryAfterSeconds`.
  - A result `token` (proof-required) → `RateLimited`, plus a fault reason the panel shows ("Signal asks for a
    captcha; open Agent42 to continue"). Solving it via `submitRateLimitChallenge` is out of scope.
  - UNREGISTERED_FAILURE → `NotFound`. NETWORK_FAILURE / IO -3 → `Transient`.
  - Every named client calls `RemoveAllResilienceHandlers()` and sets an explicit timeout (rule 8, C10). The SSE
    stream gets **its own client** with an infinite timeout.
- **SC7: egress (ADR 0013 extension) and the key material.** Replies are end-to-end encrypted to the owner's devices.
  But the **bot account's** identity keys and its message cache live on the operator's Azure Files share, so the
  operator can read what the bot account receives. The user's own Signal account is never involved (S1). The share is
  encrypted at rest and mounted with the account key (ADR 0107's account, key-only by design). Say all this in the ADR
  0111 amendment and in the panel's Signal setup note.
- **SC7b: the Copilot shell can reach the daemon and its store (critic M1).** ADR 0107's built-in shell runs as
  the backend's uid, in the backend container. It can therefore call the unauthenticated daemon on loopback (send as
  any connected number, read any account's events) and read `/mnt/signal` (every bot's identity keys and message
  cache). The `127.0.0.1` bind protects against the network, never against that shell. This is the **same class** as
  ADR 0107's accepted risks: the shell can already forge `X-MS-CLIENT-PRINCIPAL-ID` and mint managed-identity tokens
  for all app data. It stays cross-conversation rather than cross-user while only the owner's principals can sign in,
  and the kill switch (`Copilot:BuiltInToolsEnabled`) is the response. **It is recorded as an explicit extension of
  ADR 0107's accepted risks in the ADR 0111 amendment, conditional on the user confirming it at plan approval.** The
  alternatives, if the user declines: a separate uid for the JVM (needs a root entrypoint that drops privileges), or
  refuse to start Signal while built-in tools are on.
- **SC8: rule 12.** The daemon process is in-process state for this purpose. Rule 12 gains it, and the lease (C3) now
  protects a **store**, not only a connection.
- **SC9: image and version (hard rule 4 applies by analogy).** Never write the signal-cli or JRE version from memory.
  At execution, resolve the latest stable signal-cli release from `https://api.github.com/repos/AsamK/signal-cli/releases/latest`
  and its tarball SHA-256. Pin both as `ARG`s in `src/Agent42.Backend/Dockerfile`, and verify the checksum in the
  `RUN`. Take the JRE from the current `eclipse-temurin:25-jre` tag, resolved the same day and pinned by digest. Bake
  `SIGNAL_CLI_VERSION` and `SIGNAL_CLI_RELEASED` into the image as `ENV` for Step 30's age check.

### Affected areas (phase 3; verify against the tree at execution)

**New — Agents:** `src/Agent42.Agents/ChatGateway/ISignalNumberDirectoryGrain.cs` + `SignalNumberDirectoryGrain.cs`
(claim, release, resolve; state type in `PersistedTypeNames()` + golden; shared `agent-state` container).

**New — Backend (`src/Agent42.Backend/ChatGateway/Signal/`, flat namespace):**
- `SignalOptions.cs` (DataDir, DaemonUrl, port, JavaOpts, executable path);
- `SignalDaemon.cs` + `ISignalProcessLauncher.cs` (SC3);
- `SignalRpcClient.cs` (JSON-RPC + SSE clients, SC6);
- `SignalEvents.cs` (pure envelope → `ChatInboundMessage` projection, SC4);
- `SignalText.cs` (Markdown → text + `textStyle[]`);
- `SignalAdapter.cs` (adapter + session);
- `src/Agent42.Backend/ChatGateway/AgentApi.ChatGatewaySignal.cs` (register/verify). It goes in the feature folder,
  NOT `Signal/`, because `AgentApiPart_ExistsInItsFeatureFolder` expects the folder. It holds
  `private static void MapChatGatewaySignalEndpoints(IEndpointRouteBuilder app)`, called from `MapAgentApi` like
  every other partial, plus a new row in `ProjectLayoutTests.AgentApiParts()` (count comment +1).
  `AgentApiParts_MatchesReflectedMapMethods` reflects every `Map*Endpoints` method and would fail without the row.

**Modified — source:**
- `src/Agent42.Agents/ChatGateway/ChatConnection.cs` (trailing `PendingSignalNumber`),
  `IUserChatConnectionsGrain.cs` + `UserChatConnectionsGrain.cs` (`SetPendingSignalNumberAsync`; `SetCredentialsAsync`
  clears it).
- `src/Agent42.Backend/Api/AgentApi.cs`: call `MapChatGatewaySignalEndpoints`.
- `ChatGatewayService.cs` and `IChatGatewayControl.cs`: the lease participant hook (SC1.2), and `HoldsLease` on the
  interface. A new `IChatGatewayLeaseParticipant.cs`. The factory lambda in `ChatGatewayRegistration.cs` passes
  `sp.GetServices<IChatGatewayLeaseParticipant>()`.
- `IChatPlatformAdapter.cs`: the optional `IChatPlatformCleanup`.
- `AgentApi.ChatGateway.cs`: delete calls cleanup and releases the claim.
- `ChatGatewayRegistration.cs`: `AddSignalChat()`.
- `ChatGatewayContracts.cs`: register/verify DTOs, and a "needs registration" status in `ChatConnectionStatus`.
- `tools/Agent42.DevHost/Program.cs`: config only (`ChatGateway:Signal:DaemonUrl` → a stand-in).
- `src/Agent42.Backend/Dockerfile` (SC9).
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` (+ contracts): the age row (Step 30).
- `wwwroot/index.html`, `js/chatgateway.js`, and `sw.js` (`CACHE` from `agent42-shell-v73` at plan time; re-read it).

**Modified — infra:**
- `infra/modules/copilot-files.bicep`: a second share `signal-data`, or a param-driven share list; keep the module's
  "workspaces only" comment truthful.
- `infra/modules/copilot-environment-storage.bicep`: reused for a second link, `agent42-signal`.
- `infra/main.bicep`: the module call, `ChatGateway__Signal__DataDir`, and the CPU/memory bump (Step 27).
- `infra/modules/container-app.bicep`: a volume `signal-data` mounted at `/mnt/signal` on the **backend container
  only**, with the mount options from Step 24.

**Modified — tests holding lists, and fakes:**
- `GrainStorageSerializerTests.PersistedTypeNames()` + goldens (the directory state; a Cosmos JSON row for
  `PendingSignalNumber`) and `SerializationCompatibilityTests`;
- `ProjectLayoutTests` (the `AgentApiParts()` row; extend `Hosts_RegisterChatGateway`, or add a row, to assert that
  `AddChatGateway()` registers the Signal adapter, so a dropped `AddSignalChat()` is caught);
- `AgentApiReachabilityTests`;
- `PwaAssetsTests` and the other cache-name pins, and `ChatGatewayPanelAssetsTests`;
- `CopilotFilesMountTests` (a case for the `signal-data` mount: backend container only, with Step 24's options);
- `ChatGatewayFakes.cs` (`RecordingChatGatewayControl` gains `HoldsLease`), `ChatGatewayServiceTests` (the
  construction plus the hook) and `ChatGatewayApiTests` (Signal credentials refused);
- `SiloRegistrationTests`, only if a grain gains a dependency; none should.

**Docs:** an ADR 0111 amendment (S1–S3, SC2, SC7, the GPL note, the expiry), ADR 0107 (the share's account now also
holds `signal-data`), `CLAUDE.md` (rule 12 gains the daemon; the feature map says "Signal now"),
`.agentic/gotchas.md` (Step 30), and the Signal section of `docs/integrations/chat-apps/README.md` +
`docs/features/chat-apps.md`.

**Deliberately NOT touched:** `excludedPaths` (no inbound surface), GitHub secrets, and CLAUDE.md's
manual-prerequisites table (no operator input: the share key comes from `listKeys()`, as ADR 0107's does).

### Steps (phase 3)

**Step 23: Spike.** Done (above). **Verify:** findings written here.

**Step 24b results (2026-10-21, `ghcr.io/asamk/signal-cli:0.14.9` in Docker, SC3's exact arguments):**
- Every flag is accepted. The log reads "Starting daemon in multi-account mode", then "Started HTTP server on
  /127.0.0.1:8085", then "HTTP server has no authentication; Host header is pinned to [localhost, ::1, 127.0.0.1]".
- `/api/v1/check` answers 200 **even with a foreign Host**. `/api/v1/rpc` with `Host: evil.example` answers **421**.
  So the Host pin guards RPC, but not the health probe.
- `/api/v1/events?account=+31600000000` (unknown) answers **400**. `send` for an unknown account returns JSON-RPC
  **-32602 "Specified account does not exist"**. Both mean `ChatAuthException`.
- **`verify` IS a JSON-RPC command in multi-account mode** (`VerifyCommand implements JsonRpcRegistrationCommand`;
  without `account` it gives "-32602 Method requires account parameter"). The params are
  `{account, verificationCode, pin?}`, and it returns nothing on success. With no pending session it gives
  **-3 "Verify error: No registration verification session active"**. The CLI fallback is dropped.
- `register` params: `{account, voice?, captcha?, reregister?}`. A captcha demand is **-1 (user error)** whose message
  comes from `CommandUtil.getCaptchaRequiredMessage`, so detect it by the word "captcha" in a -1 message. A rate
  limit is -5. "voice before SMS" is -1 "Before requesting voice verification you need to request SMS verification
  and wait a minute."
- **`listAccounts` returns `[{number, aci}]`**, so the ACI for `ChatBotIdentity.BotUserId` comes from there after
  `verify`.
- The idle daemon with no accounts measured **186 MiB** RSS, so the JVM with one or two accounts fits in +0.25 CPU /
  +0.5Gi. Cap the heap with `JAVA_OPTS=-Xmx256m`.

**Step 24: Azure Files measurement (MANUAL GATE, before Signal is ENABLED).** This creates resources in the shared
environment `cae-hosting`, which is outward-facing, so **prepare the commands and hand them to the user to run** (the
rule 5 posture).
- Create the `signal-data` share on the copilot Files account, and an environment storage link to it.
- Create a throwaway Container Apps **job** that mounts the share and runs a script from the pinned signal-cli JVM
  image:
  1. `sqlite3` WAL write/read loops, then `kill -9` mid-write, then `PRAGMA integrity_check` after reopen;
  2. `signal-cli --config /mnt/signal listAccounts`, which proves the `FileLock` on `accounts.json` works;
  3. both under the current mount options (`uid=1654,gid=1654,dir_mode=0700,file_mode=0600`) and with `nobrl` added.
- Record the RSS of an idle daemon. Then delete the job.

**Status (2026-10-21): the share `signal-data` (on `stagent42cpxdhwuybwqdgai`, quota 1 GiB) and the environment
link `agent42-signal` in `cae-hosting` were created with the Bicep's names. Creating the job was refused by the
session's permission policy (shared environment), so the measurement itself is the user's.** To run it, save the YAML
below as `sig24-job.yaml` and run
`az containerapp job create -g rg-agent42 -n agent42-sig24 --yaml sig24-job.yaml`, then
`az containerapp job start -g rg-agent42 -n agent42-sig24`, then read the logs (`az containerapp job logs show …`
or Log Analytics). Pass means `integrity_check` = `ok` and `journal_mode` = `wal` on `/a` (the current options);
if only `/b` passes, add `nobrl` to the Signal mount. Then `az containerapp job delete -g rg-agent42 -n agent42-sig24 --yes`.

```yaml
location: westeurope   # check: az containerapp env show -g rg-hosting -n cae-hosting --query location
properties:
  environmentId: /subscriptions/<sub>/resourceGroups/rg-hosting/providers/Microsoft.App/managedEnvironments/cae-hosting
  configuration: { triggerType: Manual, replicaTimeout: 900, replicaRetryLimit: 0,
                   manualTriggerConfig: { parallelism: 1, replicaCompletionCount: 1 } }
  template:
    containers:
    - name: probe
      image: docker.io/library/alpine:3.20
      resources: { cpu: 0.5, memory: 1Gi }
      command: ["/bin/sh", "-c"]
      args:
      - |
        apk add --no-cache sqlite >/dev/null 2>&1
        for d in /a /b; do
          echo "=== $d"; mkdir -p $d/probe; cd $d/probe; rm -f t.db t.db-wal t.db-shm
          sqlite3 t.db "PRAGMA journal_mode=wal; create table t(i integer, s text);" 2>&1
          (for i in $(seq 1 3000); do echo "insert into t values($i, hex(randomblob(200)));"; done) | sqlite3 t.db 2>&1 | head -3 &
          for k in 1 2 3 4 5 6; do sqlite3 t.db "select count(*) from t;" 2>&1; sleep 0.5; done
          pkill -9 sqlite3; sleep 1
          sqlite3 t.db "PRAGMA integrity_check; PRAGMA journal_mode; select count(*) from t;" 2>&1
        done
      volumeMounts: [ { volumeName: plain, mountPath: /a }, { volumeName: nobrl, mountPath: /b } ]
    volumes:
    - { name: plain, storageType: AzureFile, storageName: agent42-signal, mountOptions: "uid=1654,gid=1654,dir_mode=0700,file_mode=0600" }
    - { name: nobrl, storageType: AzureFile, storageName: agent42-signal, mountOptions: "uid=1654,gid=1654,dir_mode=0700,file_mode=0600,nobrl" }
```

**Until it passes, Signal ships dormant** (divergence D1 under Progress): `ChatGateway:Signal:Enabled` is false by
default. With it off, the adapter isn't registered, no daemon ever starts, and the panel doesn't offer Signal.
Turning it on (a Bicep param) is the user's step, after this measurement and their confirmation of SC7b.

**Step 24b (local, no Azure, before Step 25): run the REAL pinned daemon with SC3's exact arguments** in Docker
(`ghcr.io/asamk/signal-cli:<pinned>`, a scratch volume):
- confirm that every flag is accepted, including `--receive-mode=on-connection` and `--no-receive-stdout`;
- confirm `/api/v1/check` answers 200, and that a Host header other than localhost gets 421;
- confirm that `/api/v1/events?account=+31600000000` (unknown) answers 400;
- call `{"method":"verify"}` and `{"method":"register"}` with no params. A params error means exposed, "method not
  found" means not. That settles the `VerifyCommand` question with the CLI fallback as the other branch.
- read `ListAccountsCommand` in the pinned source: does its JSON carry the ACI? `ChatBotIdentity.BotUserId` needs the
  ACI to drop own echoes and detect mentions. If it doesn't, take it from `verify`'s result or `getUserStatus` on
  self.

Message-loss behaviour under `on-connection` needs a registered number, so it stays a Step 31 check. If it fails
there, the session's mitigation is to reopen the stream immediately after the daemon restarts. Record the result.

**Verify:** Step 24's and 24b's results, the chosen mount options, the RSS and the verify/ACI answers are written
**into SC3/SC4 and Step 28 here** (not only under Progress), so the executor of Step 25 onward reads facts, not
open questions. The Azure job is gone.

**Fallback, only if WAL is unsafe on the share:** stop and re-plan S3 with the user: local disk plus a Blob snapshot
of the config dir taken on stop and every N minutes, restored at daemon start under the lease. Don't build it
unasked.

**Step 25: The lease hook + `SignalDaemon`.** Add `IChatGatewayLeaseParticipant` and wire it per SC1.2: renewed
after each successful renew; lost **before** `StopAllAsync`; before `lease.ReleaseAsync`; idempotent. Add `HoldsLease`
to `IChatGatewayControl`. Add `SignalOptions`, `ISignalProcessLauncher`, and `SignalDaemon` (SC3, including the
watchdog). A host on `NoOpChatGatewayLease` (DevHost) still drives the hook, and an external `DaemonUrl` makes it a
no-op.

**Verify:**
- `ChatGatewayServiceTests`:
  - `LeaseLost_NotifiesParticipants_BeforeSessionsStop`: a hanging fake session doesn't delay the notification;
  - `Stop_NotifiesBeforeRelease`: the fake lease records the order;
  - `Renew_NotifiesParticipants`;
  - a double stop notifies harmlessly.
- `SignalDaemonTests` with a fake launcher and fake time:
  - no start without the lease;
  - the args contain `127.0.0.1` and `on-connection`, and never `--ignore-attachments`;
  - readiness polling;
  - a crash restarts with backoff;
  - lease loss terminates the process (TERM, then KILL after 5 s) and awaits exit;
  - **the watchdog stops the daemon** when no renewal arrived within the derived window (fake time), and a renewal
    resets it (mutation-check it);
  - an external `DaemonUrl` never spawns;
  - phone numbers are redacted from forwarded output (mutation-check the redaction).

**Step 26: The RPC client, events and text (pure, parallelizable with Step 25).** Add `SignalRpcClient`
(SC6 mapping), `SignalEvents` (SC4 filter), and `SignalText`.

**Verify:**
- `SignalEventsTests` over **captured fixtures**: DM text, group with a mention, a quote of the bot, an image, a voice
  note (`isVoiceNote`), a sync message, a story, a receipt, typing, an edit, a reaction-only message, a remote delete,
  and own-ACI. The dedupe id is `uuid:timestamp`. The fixtures come from the DevHost stand-in modelled on J's examples,
  replaced by real captures in Step 31.
- `SignalRpcTests` with a stub handler:
  - the send body;
  - `textStyle` offsets around an emoji (UTF-16);
  - a data-URI upload;
  - `getAttachment` decode and cap;
  - -5 → `RateLimited` with retry-after, a proof token → `RateLimited` + fault, UNREGISTERED → `NotFound`;
  - "Specified account does not exist" → auth.
- `SignalTextTests`: bold, italic, code, fences, links (as text + URL), nested and unbalanced input never throwing,
  and offsets in UTF-16.

**Step 27: Image + infra.** Dockerfile per SC9. Bicep per the affected areas. Rewrite `copilot-files.bicep`'s "Workspaces ONLY /
holds nothing else" comment, since the account now also holds the Signal store; keep it key-only, as ADR 0107
explains. Size the backend from Step 24's RSS:
the expectation is backend `0.75 / 1.5Gi` + SearXNG `0.5 / 1Gi` = `1.25 / 2.5Gi`, an allowed combination (gotcha
"Container Apps bills the per-replica total"). Run `az bicep build`, then hand the user `az deployment group what-if`
**and** `az deployment group validate` (a bad CPU/memory sum is caught only there). No role assignment is involved.
If one appears, hand it over (rule 5).

**Verify:**
- a `docker build` of the backend image locally, with `java -version` and `signal-cli --version` run inside it;
- a `SignalImageTests` source-text test: the Dockerfile pins the version + SHA-256 and checks it, and the `ENV` names
  match what ConfigHealth reads;
- `az bicep build` green;
- the what-if output pasted into the MR.

**Step 28: Adapter, registration endpoints, number directory.**
- `SignalAdapter` + session (SC4/SC5). `ValidateAsync` checks `listAccounts`.
- `SignalNumberDirectoryGrain` (SC2).
- `POST /api/chat-connections/{id}/signal/register {number, captcha?, voice?}`:
  - 503 unless the gateway runs here and `HoldsLease`;
  - normalise and validate E.164, then claim the number (`Pending`), then `SetPendingSignalNumberAsync`, then call
    `register`;
  - a captcha demand → 400 with the captcha link, and any `register` failure releases the claim.
- `POST …/{id}/signal/verify {code, pin?}`: the number comes from `PendingSignalNumber`. Run `verify` (or Step 24b's
  CLI fallback), then mark the claim `Active`, then `SetCredentialsAsync` with the number and the ACI identity (which
  clears the pending field and opens pairing), then `RestartAsync`.
- Create and update refuse `Credentials` for Signal (SC1.1).
- Delete follows SC1.3's order.
- `AddSignalChat()` in `AddChatGateway()`.
- No log line in the adapter, the endpoints or the daemon host carries a number: log the connection id instead.

**Verify:**
- `SignalAdapterTests` against a local Kestrel stub serving SSE + RPC (the `LocalBridgeKestrelTests` shape):
  - an owner DM end to end through the real pipeline is answered **final-only, with typing first**;
  - a dropped stream reconnects;
  - an unknown account faults;
  - a group mention is answered in the group;
  - an upload and a download run over RPC.
  The adapter tests also capture logs and assert that no record contains the test number.
- `SignalNumberDirectoryGrainTests`:
  - claim, and refuse another connection's `Active` or unexpired `Pending` number;
  - an expired `Pending` claim is taken over;
  - same-connection re-claim, activate, release.
- `SignalRegistrationApiTests` on the shared API fixture:
  - no lease → 503;
  - a number claimed elsewhere → 409, and **`register` is never called** (the stub records calls);
  - a captcha demand → 400 **and the claim is released**;
  - verify reads the pending number, stores credentials, opens pairing and never echoes the number;
  - **create and update with Signal `Credentials` → 400**, and nothing is stored (B1, mutation-check it);
  - delete of a verified connection and of an abandoned pending one both call `unregister` +
    `deleteLocalAccountData` with the right number and release the claim, in SC1.3's order;
  - a cleanup failure still deletes and releases.

**Step 29: Panel.** For Signal, the dialog replaces the token field with a registration flow:
1. the number;
2. a captcha step (link + token paste), shown when the server asks for it, plus a "call me instead" option;
3. the code + optional PIN;
4. then the usual pairing card.

The setup note carries SC7's plain-words egress line and "use a number you don't use on a phone with Signal: registering
it here signs that phone out". Add a "needs registration" status. Bump `CACHE` (rule 3).

**Verify:** `ChatGatewayPanelAssetsTests` (the ids, the register/verify paths, the status string) and the cache-name
pins.

**Step 30: Health, gotchas, docs, DevHost exercise.**
- **ConfigHealth:** a row reading `SIGNAL_CLI_VERSION`/`SIGNAL_CLI_RELEASED` that warns from 75 days and errors from
  90, with the README's expiry sentence as its rationale. It is absent when the env isn't set (DevHost).
- **The ADR 0111 amendment:**
  - S1–S3, SC2 (with its residuals: a number in clear as a grain key, and `register` used as harassment), SC7 and
    SC7b (an explicit extension of ADR 0107's accepted risks, if the user confirmed it), the GPL note and the
    expiry;
  - it **names ADR 0085 and ADR 0107's "SQLite over SMB" rejection** and says what Step 24 measured, and why it holds
    here (one writer, under the lease) when it didn't for the Copilot runtime;
  - a banner on ADR 0107 pointing at it.
- **CLAUDE.md:** rule 12 and the feature map.
- **gotchas:**
  - the signal-cli daemon has no auth, so bind `127.0.0.1`, because Host validation is off on `0.0.0.0`;
  - `on-connection` or lose messages, plus one SSE stream per account;
  - the daemon lives and dies with the gateway lease, because two daemons on one store is corruption;
  - a number is claimed before `register`, and a Signal credential is never accepted from the generic endpoints;
  - Signal has no message id: dedupe on `uuid:timestamp`;
  - signal-cli expires after 3 months.
- **The `chat-apps` Signal section:** a spare number, the captcha, the code, pairing, groups.
- **DevHost exercise:** DevHost with `ChatGateway__Signal__DaemonUrl` at a single-file stand-in in the scratchpad (not
  committed; `#:property PublishAot=false`, per the gotcha) serving `/api/v1/rpc` + `/api/v1/events`. Run:
  register → verify → pair → DM → final-only reply → `/new` → a stranger ignored → a group mention.

Then `/sync-docs`.

**Verify:** the transcript in the MR; `node .agentic/scripts/check-doc-paths.mjs`; the full suite;
`dotnet format --verify-no-changes`; `node .agentic/scripts/check-new-code-coverage.mjs`.

**Step 31: Post-deploy live check (manual).** Use a real spare number:
- register with a captcha, verify, pair;
- a DM and a long reply (does the long-text attachment render?);
- an image in and out, and a PDF;
- a voice note (record whether AAC is refused);
- a group mention;
- a stranger ignored.

Then trigger one revision rollover with the connection live: one daemon at a time (check the logs), no lost message
sent during the rollover (`on-connection`), no double reply. Then delete the connection and confirm the number is
unregistered. Record the results through a `docs/*` MR (hard rule 1), and replace the Step 26 fixtures with real
captures there.

### Acceptance criteria → test mapping (phase 3)

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC25 | Inbound: only owner-relevant `dataMessage`s become messages; sync/story/receipt/typing/call/edit/reaction-only/remote-delete/own-ACI are dropped; mention and quote come from uuids; dedupe id is `uuid:timestamp`; voice flag carried | `SignalEventsTests` | `SignalEventsTests.cs` |
| AC26 | Outbound: send/typing/upload/download over JSON-RPC; `textStyle` offsets are UTF-16-correct; error codes map to `ChatSendErrorKind`; a missing account is an auth fault | `SignalRpcTests`, `SignalTextTests` | `SignalRpcTests.cs`, `SignalTextTests.cs` |
| AC27 | The daemon runs only while the lease is held, binds `127.0.0.1`, uses `on-connection`, restarts after a crash; on lease loss it is told FIRST (before sessions stop) and stopped (TERM→KILL, exit awaited) before the lease is released; the watchdog stops it when renewals stall; the hook is idempotent; output is number-redacted | `SignalDaemonTests`, `LeaseLost_NotifiesParticipants_BeforeSessionsStop`, `Stop_NotifiesBeforeRelease`, `Renew_NotifiesParticipants` | `SignalDaemonTests.cs`, `ChatGatewayServiceTests.cs` |
| AC28 | Registration: 503 without the lease; a number claimed (active or unexpired pending) by another connection is refused and `register` never runs; an expired pending claim can be taken; a failed register releases the claim; verify takes the number from the pending field, stores the credential and opens pairing | `SignalRegistrationApiTests`, `SignalNumberDirectoryGrainTests` | `SignalRegistrationApiTests.cs`, `SignalNumberDirectoryGrainTests.cs` |
| AC28b | A Signal credential can never be pasted: create and update with Signal `Credentials` answer 400 and store nothing | `SignalRegistrationApiTests` (B1 row) | `SignalRegistrationApiTests.cs` |
| AC29 | An owner DM is answered final-only through the unchanged pipeline, typing first; a group mention answers in the group; a dropped stream reconnects; no log record carries a number | `SignalAdapterTests` | `SignalAdapterTests.cs` |
| AC30 | Delete: read the connection (number from credentials or pending field) → `DeleteAsync` (deregisters first) → `StopAsync` → daemon `unregister` + `deleteLocalAccountData` → claim released; a cleanup failure never undoes the delete or keeps the claim | `SignalRegistrationApiTests` delete rows | `SignalRegistrationApiTests.cs` |
| AC31 | The Signal flow is in the panel, reachable from `main.js`, with the egress note; CACHE bumped | `ChatGatewayPanelAssetsTests`, cache pins | `ChatGatewayPanelAssetsTests.cs`, `PwaAssetsTests.cs` |
| AC32 | The image pins signal-cli by version + SHA-256 and checks it; ConfigHealth warns at 75 days and errors at 90 | `SignalImageTests`, a `ConfigHealth` row test | `SignalImageTests.cs`, `ConfigHealthTests.cs` |
| AC33 | The number directory state and `ChatConnection.PendingSignalNumber` round-trip on both serializer paths, and a document without the field reads as null | `PersistedTypeNames` + golden, a Cosmos JSON row | `GrainStorageSerializerTests.cs`, `SerializationCompatibilityTests.cs` |
| AC34 | Azure Files carries WAL safely with the chosen mount options (or the fallback is re-planned); the real daemon accepts SC3's arguments | Step 24 manual gate, Step 24b local run, Step 31 live | written into SC3/SC4/Step 28 |
| AC35 | `AddChatGateway()` registers the Signal adapter, the new partial is mapped, and the `signal-data` mount is on the backend container only | `ProjectLayoutTests` (`AgentApiParts` row + Signal registration), `AgentApiReachabilityTests`, `CopilotFilesMountTests` | `ProjectLayoutTests.cs`, `AgentApiReachabilityTests.cs`, `CopilotFilesMountTests.cs` |

### Quality gates (phase 3; copied from `.agentic/quality-gates.yaml` on 2026-10-21)

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 (build error) |
| `vulnerable_packages` | **0** | CI package-freshness job. It doesn't see the signal-cli tarball or the JRE: Step 30's age row is the substitute, and it's **not** a gate |
| `add_policy` | latest-stable | GitHub releases API + image tag resolved at execution (SC9); no NuGet package expected |
| Branch policy | never commit to `main` | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

`SignalDaemon`'s real-process path (`ISignalProcessLauncher`'s production implementation) is expected to be the
coverage gap, as `BlobChatGatewayLease` was in 1a. Keep it to argument passing and `Process` plumbing, and report the
number; never estimate it.

### Review (phase 3)

A `critic` pass on the first draft (2026-10-21) found **1 blocker, 6 majors and 5 minors. All 12 were accepted** and
folded in; none were rejected. One of them (M1) is accepted as a risk, pending the user's confirmation at approval.
- **B1:** a Signal credential could be pasted through the generic create/update endpoints, so user B could drive
  user A's registered number. Signal credentials are now refused there (SC1.1, AC28b).
- **M1:** the Copilot shell (ADR 0107) shares the uid and the loopback with the daemon. This is recorded as SC7b, an
  extension of ADR 0107's accepted risks, **conditional on the user's confirmation**, with two named alternatives if
  they decline.
- **M2/M3:** delete couldn't find the number, an abandoned claim squatted forever, and `verify` had no number. The
  fixes are the pending/active claim with a 30-minute TTL, release on a failed `register`, a persisted
  `PendingSignalNumber`, read-before-delete, and AC28/AC30/AC33.
- **M4:** the renewal window and a hung dispose could delay the kill. The participant is now notified BEFORE the
  sessions stop, there is a 40 s watchdog derived from the lease timings, TERM→KILL, and an idempotent hook (AC27).
- **M5:** the real daemon arguments were first run in production. Step 24b now runs the pinned daemon locally with
  SC3's exact arguments and answers the verify/ACI questions.
- **M6:** ADR 0085/0107's SQLite-over-SMB rejection is now named in the amendment, and the `copilot-files.bicep`
  comment and `CopilotFilesMountTests` are in scope.
- **Minors:**
  - m1: the partial's file, method and `AgentApiParts` row are decided;
  - m2: the fakes and the factory lambda are listed;
  - m3: a no-number-in-logs assertion, and the clear-number grain key is recorded;
  - m4: the `register` harassment residual is recorded;
  - m5: AC35 pins the Signal adapter registration.

### Out of scope (phase 3)

- A linked-device mode (S1), solving Signal's rate-limit challenge from the panel (`submitRateLimitChallenge`), and
  streaming by edit.
- Transcoding AAC voice notes (ffmpeg, C12), reactions and read receipts, and stories.
- Moving an existing registered number between connections. Delete it and register it again.
- Automatic signal-cli upgrades. The age row only tells the operator.

## Spike findings (Step 1, 2026-10-21)

- **(a) Telegram:** all answers are from core.telegram.org, read 2026-10-21; the current version is Bot API 10.3,
  dated 2026-08-24.
  - `getUpdates`: an update is confirmed by any later call with a higher `offset`. Unconfirmed updates are kept for
    24 hours. The call refuses while a webhook is set. No maximum `timeout` is documented, so we use 30 s.
  - The 409 text for two pollers is **not documented**. The adapter keys on the status code only.
  - Errors: `parameters.retry_after` is documented. "message is not modified" is **not documented**; it is mapped
    from the description text and treated as success.
  - `sendMessageDraft` exists, but only for **private** chats, and it is an ephemeral 30-second preview that still
    needs a final `sendMessage`. So edit-in-place stays the streaming path; Draft is a possible later refinement.
  - `reply_to_message_id` was replaced by `reply_parameters` in Bot API 7.0.
  - HTML parse mode supports `b strong i em u ins s strike del span.tg-spoiler tg-spoiler a tg-emoji tg-time code pre
    blockquote`. Only `& < >` need escaping (plus `&quot;` inside attributes).
  - `sendChatAction` lasts 5 s or until the bot's next message. `getFile` downloads up to 20 MB, and its link lives at
    least 1 hour.
  - Limits: about 1 message per second per chat, and 20 per minute in a group.
  - **Privacy mode (matters for 1c groups):** a bot in a group sees commands addressed to it, replies to it and
    messages sent via it, but NOT a plain `@mention`. The setup guide says to turn privacy off or make the bot an
    admin.
  - The token is accepted in the URL path only.
- **(b) Slack, re-run for phase 1b (2026-10-21, docs.slack.dev):**
  - **Socket Mode:** `apps.connections.open` takes the app-level `xapp-` token in the `Authorization` header (scope
    `connections:write`) and returns a `wss://` `url`. Slack sends `hello` first; its `debug_info.approximate_connection_time`
    is about 3600 s. Each envelope is `{envelope_id, type, payload, accepts_response_payload}`, acked by sending
    `{"envelope_id": …}` back. A `disconnect` envelope carries `reason` `warning` (about 10 s ahead), `refresh_requested`
    (which can come with no warning) or `link_disabled` (Socket Mode turned off, so it is permanent). Up to 10
    connections are allowed per app, and any one may receive any payload. `&debug_reconnects=true` shortens a
    connection to 360 s.
  - **Messages:** `message.im` (`im:history`), `message.channels` (`channels:history`, public channels only),
    `message.groups` (`groups:history`), `message.mpim` and `app_mention` (`app_mentions:read`). The relevant subtypes
    are `bot_message`, `message_changed`, `message_deleted`, `file_share` and `thread_broadcast`. `chat.postMessage`
    returns `channel` and `ts`, and `thread_ts` must be the PARENT's `ts`. It allows about 1 message per second per
    channel; `chat.update` is Tier 3. A 429 `ratelimited` carries `Retry-After`. `auth.test` returns `user_id`,
    `team_id`, `bot_id` and `user`.
  - **Formatting:** `markdown_text` exists on both `chat.postMessage` and `chat.update` (12,000 characters, mutually
    exclusive with `text`). But how it treats `<!channel>` and `&lt;` is not documented. The documented mrkdwn
    escaping (`& < >` as entities) is the property that keeps a reply from pinging `@channel`. **Decision:**
    `SlackText` converts Markdown to mrkdwn, sent as `text`. `chat.update`'s `msg_too_long` caps `text` at 4,000
    characters, so `MaxMessageLength` is 4000 (also Slack's "best results" figure), not Hermes' 39000.
  - **Command prefix:** the Slack composer takes a leading `/` as a slash command (Hermes `typed_command_prefix`;
    Slack's docs don't say it outright). It stays `!`.
- **(c) Discord, re-run for phase 1b (2026-10-21, docs.discord.com + nuget.org):**
  - **Library (C10):** **`Discord.Net.WebSocket` 3.20.2.** It is stable, was published 2026-10-08, targets `net10.0`,
    and brings Core, Rest, Dave (libdave bindings, 150 KB) and Newtonsoft.Json. `NetCord` is active and has no
    dependencies, but it has **no stable release** (the latest is `1.0.0-beta.28`), so it fails `add_policy:
    latest-stable`. Added with `dotnet add package`.
  - **Intents:** `MESSAGE_CONTENT` is privileged, but "content in DMs with the app" and "content in which the app is
    mentioned" are exempt. So the adapter asks for `Guilds | GuildMessages | DirectMessages` only. `Guilds` is
    unprivileged; Discord.Net needs it to resolve thread channels. A consequence for 1c: a guild message that doesn't
    mention the bot arrives with EMPTY content, so in a thread the owner has to mention the bot (a reply that pings
    it counts, since it lands in `mentions`).
  - **Close codes:** 4004 (authentication failed), 4010–4014 (shard, version, invalid or disallowed intents) are
    "Reconnect: false". **Discord.Net reconnects forever on them anyway** (`ConnectionManager` gives special handling
    to 4006/4014 only). So the adapter watches `Disconnected` for a `WebSocketClosedException` with a non-reconnectable
    code and ends the session with `ChatAuthException`.
  - **Messages:** `content` is at most 2000 characters. `allowed_mentions: {"parse": []}` suppresses every mention
    (`parse` is mutually exclusive with `users`/`roles`). `message_reference.message_id` makes a reply, and
    `fail_if_not_exists` defaults to true. A thread started from a message has **the message's id** (flag
    `HAS_THREAD`). A voice message is flag `IS_VOICE_MESSAGE` (1 << 13). JSON error codes include 10003 unknown
    channel, 10008 unknown message and 50013 missing permissions.
  - **Attachment hosts:** `url` is on `cdn.discordapp.com` and `proxy_url` on `media.discordapp.net` (the CDN base
    in Discord's image-formatting reference). No token is sent there.
  - **Transport:** compression is optional. Discord.Net asks for `zlib-stream` but also reads uncompressed text
    frames. With `DiscordSocketConfig.GatewayHost` set, connecting makes **no REST call**: `LoginAsync` only stores
    the token, and `/gateway/bot` is skipped. That is what lets DevHost and the tests point the real library at a
    local gateway stand-in.
  - **REST is hand-rolled** (send, edit, typing, start thread, `GET /users/@me`), as for Telegram and Slack. Discord.Net's
    REST client defaults to `RetryMode.AlwaysRetry` and waits out rate-limit buckets inside the library. That is
    exactly the sleep C6 forbids, and it would hide `allowed_mentions` behind the library's defaults.
  - **`/text` in a DM:** Discord doesn't document what happens to an unmatched `/` message. The app registers no
    commands, and none of ours collide with the client's built-ins (`/shrug`, `/me`, `/tableflip`…), so the prefix
    stays `/`. **Unverified until Step 19's live check**; if Discord swallows it, switch to `!` as on Slack.
- **(g) STT, run for phase 1c (2026-10-21; OpenAI's speech-to-text guide and Microsoft Learn's Foundry quickstart):**
  - Both providers document **mp3, mp4, mpeg, mpga, m4a, wav and webm**, at most **25 MB**. **Ogg is on neither
    list**, and OpenAI's guide says so outright. Telegram, Discord and WhatsApp voice notes are Ogg/Opus. Slack clips
    are m4a/mp4.
  - **Decision:** without ffmpeg, Ogg is still sent as itself, and a refusal gets the curated reply
    (`SpeechTranscriptionFormats`). Step 19 checks live whether it is accepted.
  - Azure's REST shape is `…/openai/deployments/{name}/audio/transcriptions`. The SDK call is
    `AudioClient.TranscribeAudioAsync(Stream, fileName, AudioTranscriptionOptions)`, read from the pinned OpenAI
    2.14.0 XML docs.
  - OpenAI marks `whisper-1`, `gpt-4o-transcribe` and `gpt-4o-mini-transcribe` deprecated (shutdown 2027-02-26) and
    recommends `gpt-transcribe`. The user picks the model, so no name is in code.
- **(d) Token in the path:** the gotcha's `SuppressInstrumentationScope` handler suppresses the HttpClient span. The
  factory's request logging is a separate channel that needs `RemoveAllLoggers()`. Both are proven by
  `TelegramBotApi_NeverLeaksTheToken`, with a control client and both mutations checked. Also found: a token's `:`
  makes `new Uri(base, "bot…")` parse as a URI scheme, so the path is built as `"./bot…"`.
- **(e) Lease:** blob containers are declared in Bicep here (`infra/modules/storage.bicep`), so the lease needs one
  new container, `chat-gateway`, on the existing account-scoped role. ACA's default single-revision mode still
  overlaps the old and new revision during a rollover; Step 19 measures it live.
- **(f) Compaction:** ADR 0084 stubs only tool payloads (artifact sources, `read_document` results), never message
  text, so the turn-count rollover is needed: `MaxTurnsPerConversation = 100`.
- **(h) Limits:** Telegram is 4096 (documented as "characters"; we count UTF-16 units, which is conservative) and
  10 chunks (our choice). Edits are paced at 1.2 s for the 1/s per-chat budget, and typing is refreshed every 4 s.

## Progress and divergences

**Phase 1a (Steps 1–10) shipped in PR #212, phase 1b (Steps 11–13) in PR #213, phase 1c (Steps 14–18) in PR #214, and phase 3 (Steps 23–30, Signal dormant) is below.** Steps 19, 24 and 31 and phase 2 remain, so this
spec stays in `.agentic/specs/` (not `completed/`) until phase 3 ships. Phase 1a's differences from the plan:

- **No `LoopbackChatAdapter`.** DevHost points the REAL Telegram adapter at a local stand-in for the Bot API
  (`ChatGateway:TelegramApiBaseUrl`), which exercises the actual adapter rather than a fake one. The Step 10
  exercise used a small Node stand-in in the session scratchpad; it is not committed.
- **The lease touches infra in 1a:** one blob container in `infra/modules/storage.bicep`. "No infra in phase 1" was
  wrong, since containers are pre-declared here.
- **The setup guide is a folder,** `docs/integrations/chat-apps/README.md`, per that directory's
  one-folder-per-integration rule. There is also a `docs/features/chat-apps.md` page.
- **Group gating (Step 16) is already in the pipeline,** with its tests (`Pipeline_Group_*`), because it is a few
  lines of the owner/mention gate. The panel does not expose the toggle yet; 1c adds it with the privacy-mode setup
  note.
- **`ChatConnectionDirectoryGrainTests`** were folded into `UserChatConnectionsGrainTests`, which assert the run list
  after every lifecycle step.
- **The chunker has no UTF-16 mode flag:** `string.Length` is UTF-16 already, and it is never shorter than the
  character count other platforms use.
- **Owner telemetry:** a chat turn has no request, so `ChatConnection.OwnerTelemetry` (`[Id(22)]`) captures the
  owner's identity on each signed-in save. The plan only said "set the RequestContext".
- **Found and fixed while executing:**
  - **Cosmos JSON flips a `true`-initialised bool.** `ChatConnection.Enabled` had no-`= true` added, with a
    gotcha and a mutation-checked test.
  - **A create with a rejected token left an orphan bot.** Credentials are now validated before the create (found in
    the DevHost exercise).
  - **`\r` leaked into Telegram HTML.** Line endings are normalised.
  - **One-line replies sent a needless "typing…".**
  - **An existing flake, `McpToolImageGrainTests`:** it read one silo's counters while the grain could run on the
    other. Both are now read.
- **Diff review (critic, phase 1a): no blockers, 5 majors and 6 minors, all fixed. None were rejected.**
  - **M1:** a message with no text no longer becomes an empty turn.
  - **M2:** a queue worker can no longer die and leave its key behind (catch, plus `finally` cleanup), and a turn's
    wait is capped at 15 minutes while the turn runs on.
  - **M3:** a grain error no longer ends a connection's supervision silently, and a stop never rethrows.
  - **M4:** boot racing a restart can no longer orphan a duplicate session.
  - **M5:** the persisted owner identity is now in ADR 0111, and its equality compares roles by value.
  - **m6:** a session polls only after it is registered (`IChatPlatformSession.Start`).
  - **m7:** a poison update is skipped, and the cursor still moves past it.
  - **m8:** CLAUDE.md rule 13 names the chat gateway.
  - **m9:** the queue-cap off-by-one is fixed.
  - **m10:** the pairing-lockout DoS residual is recorded in the ADR.
  - **m11:** the dialog reopens only after a fresh token.
- **Not covered by automated tests:** `BlobChatGatewayLease` (0% new-code coverage; it needs Azurite or real
  storage). The supervisor's lease behaviour is covered against a fake; the Blob implementation is checked live in
  Step 19.

### Phase 1b (Steps 11–13), on `feature/chat-gateway-phase-1b` from `main` @ `4cec70b` (the #212 merge)

**Implemented:** Slack over Socket Mode and Discord over the gateway, behind the unchanged adapter contract. The
drift check was clean, since `main` was exactly the #212 merge. Spike items (b) and (c) were re-run against current
docs; see Spike findings. Differences from the plan:

- **Discord REST is hand-rolled; Discord.Net holds only the gateway.** The plan said "use a library for Discord", meaning
  the gateway. The library's REST client waits out rate limits inside itself (the C6 inline sleep) and would hide
  `allowed_mentions` behind its defaults, so the five REST calls follow the Telegram/Slack shape.
- **Discord.Net doesn't stop on 4004.** It reconnects forever on non-reconnectable close codes, so the adapter
  detects them in `Disconnected` (4004, 4010–4014, and a 401 from `/gateway/bot`).
- **Slack's `disconnect` reconnects inside the session,** not "via the supervisor" as Step 11 put it. Replies go over the
  Web API, so a streaming reply survives Slack's routine refresh. A failed socket still goes to the supervisor.
- **Slack formatting is mrkdwn via `SlackText`,** not `markdown_text` (Spike (b)), and Slack's `MaxMessageLength` is
  4000, because `chat.update` caps `text` there. The plan's 39000 was Hermes' figure.
- **Slack has no typing indicator** for bots outside the AI-apps surface, so typing is a no-op.
- **Discord intents** are `Guilds | GuildMessages | DirectMessages`. `Guilds` is needed for Discord.Net to resolve
  thread channels. "A reply to the bot" counts on Discord only through the mention array: without MESSAGE_CONTENT, an
  unmentioned guild message arrives empty. So `RepliesToBot` is always false there.
- **Outbound image upload stays phase 1c** for both platforms, as it is for Telegram. Slack's and Discord's
  host-pinned, capped downloads are implemented and tested now, since Step 11 listed them.
- **The frontend changed** (the plan's 1b steps named none): Slack's second credential needs a write-only `xapp-`
  field, shown only for Slack, plus setup notes for both platforms. `CACHE` went v71 → v72.
- **The Slack manifest is in code** (`SlackAdapter.AppManifest`), and the guide quotes it verbatim, pinned by
  `ChatAppsGuide_QuotesTheSlackManifest`. That is what "generated manifest" means here.
- **Found and fixed:** two command descriptions hard-coded `/`, so Slack's `!help` would have advertised
  `/model list`. Descriptions now take the platform's prefix (`ChatCommands.Describe`).
- **Observed, not fixed (pre-existing from 1a):** at DevHost boot, the supervisor's first read of
  `ChatConnectionDirectoryGrain` can run before the silo is active. Orleans' placement retry logs two Polly warnings
  and succeeds.
- **DevHost exercise (2026-10-21):** DevHost was run with `ChatGateway__SlackApiBaseUrl`, `ChatGateway__DiscordApiBaseUrl`
  and `ChatGateway__DiscordGatewayUrl` pointed at a single-file .NET stand-in in the session scratchpad (not
  committed). The stand-in serves the Slack Web API + Socket Mode and Discord REST + gateway, with control endpoints
  to inject messages. For both platforms:
  - a rejected token was refused;
  - a stranger and a wrong code were ignored;
  - the code paired;
  - a DM was answered by a placeholder edited into the reply;
  - `!new`/`!status` (Slack) and `/new`/`/help` (Discord) answered;
  - the bot's own echo and Slack's `message_changed` were ignored.

  Slack acked all 8 envelopes. Every Discord create and edit carried `allowed_mentions: {"parse": []}`, including a
  reply that echoed `@everyone`. Both connections showed "connected" and both conversations were listed.

- **Diff review (critic, phase 1b): no blockers, 2 majors and 5 minors, all fixed. None were rejected.**
  - **M1:** an envelope past the 8 MB guard was never acked. It is now acked from the `envelope_id` in its first or
    last bytes (`Slack_AcksAnOversizedEnvelope`, mutation-checked).
  - **M2:** rotating one Slack token forced re-pasting both, while the placeholder said "leave blank to keep". On an
    edit the API now keeps the stored token for a blank field (`Update_WithOneToken_KeepsTheOtherStoredToken`,
    mutation-checked).
  - **m3:** the Discord entry in `Directory.Packages.props` was splitting the security-pin comment from its package.
    It now has its own comment.
  - **m4:** close codes 4010–4012 got the "token rejected" text; they now get their own.
  - **m5:** the `/gateway/bot` 401 path is now described as verified from the library source, not by a test (Step 19
    checks it live).
  - **m6:** back-to-back Slack refreshes are spaced out, so a flapping workspace can't hammer `apps.connections.open`.
  - **m7:** a Discord bot without permission to start threads now answers in the channel instead of failing every
    reply (`Send_WhereThreadsAreForbidden_AnswersInTheChannel`).

### Phase 1c (Steps 14–18), on `feature/chat-gateway-phase-1c` from `main` @ `2d2fc48` (the #213 merge)

**Implemented:** inbound files and outbound images, voice notes over a new per-user transcription model (ADR
**0113**), the three per-connection toggles in the panel, and proactive delivery to the origin or home chat. The drift
check since `41f0a41` showed only phases 1a/1b and the past-chats merge (ADR 0112). Steps 15 (model plumbing) and 17
(delivery) ran as `step-implementer` subagents in worktrees, merged back without conflicts. The contracts they shared
(`ISpeechTranscriptionService`, `IChatDeliverySender`, `ChatOrigin`) were committed first. Differences from the plan:

- **The agent-run file core gained two overloads instead of being reused as-is.** The plan said "no change expected",
  but both helpers took `IFormFile`. `AgentRunFileError` now has a metadata overload (name, declared type, length), so
  the gateway validates before downloading, and `StoreAgentRunFilesAsync` has a byte-level overload. The form overloads
  delegate to them, and both doors' behaviour is unchanged.
- **Files: skip-and-note**, as recommended. The owner gets a notice and the model a `[Attachment skipped: …]` line.
  A no-text message whose files all fail the metadata check mints nothing; one whose files fail later (download,
  ingest) leaves an empty listed conversation (accepted, critic m2). At most 10 files per message. A copilot agent refuses documents.
- **Images out after the text final**, through `ChatReplyDestination.UploadImage`. The streamer's old "open Agent42"
  note is now only the fallback for a failed upload. **Slack's manifest gains `files:write`**, so older apps must be
  reinstalled.
- **Voice notes are transcribed before the route**, so a failure mints nothing. Exactly one curated reply per failure
  kind: off, no model, format or size, provider refusal. Ogg is sent despite being undocumented (spike (g)).
- **The transcription model** mirrors ADR 0038's speech model layer for layer:
  - `SupportsTranscription` `[Id(8)]` and `TranscriptionDefault` `[Id(5)]`;
  - `PUT /api/default-transcription-model`;
  - a 7th positional capability checkbox in `providers.js`, plus a default picker;
  - the API's model list, plus filtering in `FleetTaskService` / `fleet.js`.

  `IChatClientFactory` gained `GetTranscriptionService()` and `DefaultTranscriptionModelId` as default members, so the
  other factories need no code.
- **Delivery registration:** the gateway's `ChatDeliverySender` is registered in `AddChatGateway()`, which both hosts
  already call before `AddAgent42Silo()`, rather than by a separate line in each `Program.cs`. Test silos register a
  recording or the Null sender, and `SiloRegistrationTests` lists the port.
- **Delivery hands off and returns at once** (found in review of the subagent's work, not in the plan). Awaiting a
  platform send inside the non-reentrant conversation grain could hold it for the streamer's time-box several times
  over. Pinned by `Sender_HandsOff_WithoutWaitingOnAHungPlatform`.
- **Reminder placement:** the origin delivery runs before the reminder path's `PushesSent > 0` early return. The home
  fallback alone is skipped when the model already pushed, so a notification never lands twice.
- **`send_push_notification` reaches chat only on the sent path.** A user with no Web Push device still gets the
  tool's refusal and no chat message, which keeps the tool's model-facing contract unchanged. Delivering to chat for a
  push-less user would need that contract changed; it is a follow-up candidate.
- **Goldens:** no new golden blobs. The committed `AgentGrainState` / `UserProviderConfig` goldens predate the new
  fields, so they already are the "document without the field" case. New tests round-trip the populated fields on both
  serializer paths.
- **Diff review (critic, phase 1c): no blockers, 2 majors and 4 minors.** Four fixed, two accepted with a record:
  - **M1:** away replies reach the origin chat without the notifications toggle. That is the C9 design, so the docs were
    fixed: the ADR egress bullet and the toggle's note now say so.
  - **M2:** a group origin is now delivered to only while groups are on. `ChatOrigin.IsGroup` is a trailing `[Id(2)]`,
    defaulting to false and proven on Cosmos JSON (`Sender_GroupOrigin_OnlyWhileGroupsAreOn`).
  - **m1** (two messages in one chat when the origin is the home chat and the model also pushed) is accepted and
    recorded in the ADR.
  - **m2** (an empty conversation after a late file failure) is accepted and recorded.
  - **m3:** Telegram files have their own `telegram-files` client with a 2-minute timeout, token-safe like the others.
  - **m4:** the transcriber is resolved once per voice note.
- **Not done in this MR:** the DevHost exercise of files and voice against platform stand-ins. These paths are
  covered by the pipeline tests with a fake session and by the adapter tests against local servers. Step 19 checks the
  real platforms.

### Phase 3 (Steps 23–30), on `feature/chat-gateway-phase-3-signal` from `main` @ `2875f96` (the #214 merge)

**Implemented:** Signal as a dedicated number in a lease-gated signal-cli child process, with its store on Azure Files.
**Signal ships dormant** (D1). The drift check was clean: HEAD was exactly the planned-against commit. Steps 25 and 26
ran as `step-implementer` subagents in worktrees and merged back without conflicts, on the shared contracts
(`SignalOptions`, `ISignalDaemon`) committed first. Steps 27–30 ran in the main session. Step 31 (live) and Step 24 (the
Azure Files measurement) remain open, and so do Step 19 and phase 2.

Divergences from the plan:
- **D1: Signal ships dormant, and Step 24 is the user's.** The user asked for the PR to be created and merged without
  waiting. Creating the throwaway measurement job in the shared `cae-hosting` environment was refused by the session's
  permission policy. The `signal-data` share and the `agent42-signal` environment link were already created, with the
  Bicep's names, so the deploy adopts them.
  - So `ChatGateway:Signal:Enabled` / Bicep `signalEnabled` default to **false**. Off, `AddSignalChat` registers nothing,
    no daemon can start, the panel doesn't offer Signal, and the backend keeps its CPU and memory.
  - Turning it on is the user's step, after two things: the Step 24 measurement (commands and YAML under Step 24) and
    confirming SC7b. The ADR records SC7b as pending that confirmation, not as accepted.
- **D2: `AddSignalChat(builder.Configuration)` is its own call in each host,** right after `AddChatGateway()`.
  `Hosts_RegisterChatGateway` pins it. `AddChatGateway()` keeps its signature, which the test also pins.
- **D3: the lease hook goes slightly past the plan:**
  - `IChatGatewayLease` gained `Duration`, so the watchdog window is derived, not a literal: 35 s for the Blob lease
    (60 − 20 − 5), not the plan's 40;
  - "renewed" is also sent right after acquiring;
  - `ChatGatewayTimings` gained `ParticipantTimeout`.
- **D4: Step 24b answered the open questions, and the CLI verify fallback was dropped.** `verify` is a JSON-RPC method in
  multi-account mode, and `listAccounts` returns the ACI.
- **D5: JSON-RPC parameter names follow the man page's rule:** a list goes under the plural name. The plan wrote
  `textStyle`; it is `textStyles`, and `attachments` stays.
- **D6: `FileRef` is `chatId|attachmentId`,** because `getAttachment` needs the sender or the group as well as the id.
- **D7: the adapter tests use an `HttpMessageHandler` stub (`SignalDaemonStub`), not a local Kestrel server.** The real
  `SignalRpcClient` runs over it, with SSE fed through a pipe. "Final-only through the unchanged pipeline" is covered
  by the existing streamer tests with `SupportsEdit = false` and by the DevHost exercise, not by a new
  pipeline-level row.
- **D8: Config health's signal-cli row** is `NotApplicable` while Signal is off. `BuildStatic` gained an optional
  `TimeProvider` and is otherwise unchanged.
- **D9: delete calls `deleteLocalAccountData` with `ignoreRegistered`,** so the keys go even when `unregister` failed.
- **D10: a register on a connection that already has a different number is refused** ("delete it and add a new
  connection"). A re-register of the same number keeps its active claim.
- **Found and fixed:**
  - **The chunked body.** In the DevHost exercise, every RPC body arrived empty: `JsonContent` sends it chunked. Calls
    are now sent buffered, with a regression test.
  - **The flaky test.** `ChatGatewayServiceTests.ASessionThatEndsWithAnAuthFailure_FaultsTheConnection` (pre-existing)
    failed intermittently: the fault was recorded while `Live` was still true. `Live` is now cleared first.
- **DevHost exercise (2026-10-21):** DevHost ran with `ChatGateway__Signal__Enabled=true` and
  `ChatGateway__Signal__DaemonUrl` pointed at a Python stand-in for the daemon, in the session scratchpad (not
  committed). Results:
  - a pasted Signal number was refused with a 400;
  - register without a captcha gave a 400 with the link, and with one gave "awaiting-code";
  - a malformed code gave a 400;
  - verify opened pairing;
  - a stranger's message and a wrong code were ignored, and the code paired;
  - "hello agent" was answered with `sendTyping`, then one `send` with `textStyles`;
  - the same `sender:timestamp` again ran once;
  - `/new` answered "Started a new conversation.";
  - with groups on, an unmentioned group message was ignored, and a mention was answered in the group as a quote;
  - "@bot /help" (with the real U+FFFC placeholder) ran the command.
- **Diff review (critic, phase 3): no blockers, 1 major, 2 minors and 3 nits. All fixed or recorded; none were
  rejected.**
  - **M1: deleting a stale registration could unregister another user's number.** An abandoned pending claim expires,
    and another connection can take the number. The daemon cleanup now runs only while this connection still holds the
    claim (`Delete_AStaleRegistration_WhoseNumberWasTakenOver_LeavesTheNewHolderAlone`, mutation-checked).
  - **m1: a base64 group id starting with `+` was treated as a person.** `IsDirect` is now "+ and digits only".
  - **m2: switching to another number leaked the earlier pending claim.** It is now released.
  - **Nits:**
    - `verify` checks the claim before calling the daemon;
    - the JVM runs with a scrubbed environment;
    - the ADR now records the SearXNG sidecar's loopback reach, and the delete-without-lease residual.
  - **Found by the full suite under load:** `SignalDaemonTests.ACrash_RestartsOnTheNextCall_WithGrowingBackoff`
    exposed a real race. `EnsureRunningAsync` could restart a crashed process before its exit watcher had recorded the
    crash, so the backoff was skipped. The exit is now recorded once, by whichever sees it first.
- **Measured:** signal-cli 0.14.9 in Docker (Step 24b), and the built backend image (`java` 25.0.4.1, signal-cli 0.14.9,
  uid 1654; the daemon starts and stops cleanly on SIGTERM).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | An owner DM produces a listed conversation and a streamed reply; a stranger's message creates no state | `Pipeline_OwnerDm_Replies`, `Pipeline_Stranger_IgnoredWithoutState` | `ChatTurnPipelineTests.cs` |
| AC2 | Pairing: the right code in a DM before expiry binds the owner; wrong or expired codes are silent; lockout after 5; never from a group; never a conversation | `Pairing_*` | `UserChatConnectionsGrainTests.cs`, `ChatTurnPipelineTests.cs` |
| AC3 | Own echoes and other bots never start a turn | adapter fixture rows + `Pipeline_OwnEcho_Dropped` | `*AdapterTests.cs`, `ChatTurnPipelineTests.cs` |
| AC4 | A duplicate inbound id runs once, also across a session reconnect | `InboundDeduperTests`, `Pipeline_DuplicateAcrossReconnect_RunsOnce` | `InboundDeduperTests.cs`, `ChatTurnPipelineTests.cs` |
| AC5 | Same-route messages run in order; different routes in parallel; `/stop` bypasses the queue | `Pipeline_SameRoute_InOrder`, `Pipeline_TwoRoutes_Parallel`, `Pipeline_Stop_BypassesQueue` | `ChatTurnPipelineTests.cs` |
| AC6 | `/new`, `/model`, `/agent`, `/sethome`, `/help`, `/status` behave per C8; Slack uses its prefix | `ChatCommandsTests`, `Pipeline_Command_*` | `ChatCommandsTests.cs`, `ChatTurnPipelineTests.cs` |
| AC7 | A thread or route continues one conversation and never re-binds; a deleted target replies and runs nothing | `Pipeline_Continue_NoRebind`, `Pipeline_DeletedTarget_RepliesOnly` | `ChatTurnPipelineTests.cs` |
| AC8 | Streaming: throttled, unchanged edits skipped, final always lands, formatted final only, `BadFormat` → plain, capped flood waits, final-only for no-edit platforms, `TextReset`, allow-listed events only, typing stops before the final | `ChatReplyStreamerTests` | `ChatReplyStreamerTests.cs` |
| AC9 | Chunking keeps code fences, counts UTF-16 for Telegram, caps Discord at 8 | `ChatMessageChunkerTests` | `ChatMessageChunkerTests.cs` |
| AC10 | The turn persists even when the platform session hangs | `Pipeline_HangingSession_StillPersists` | `ChatTurnPipelineTests.cs` |
| AC11 | A chat turn's spans carry the owner's identity | `Pipeline_SetsUserTelemetryContext` | `ChatTurnPipelineTests.cs` |
| AC12 | Credentials are write-only, validated on save (400), and the protector purpose is isolated | `Connections_*`, protector test | `ChatGatewayApiTests.cs` |
| AC12b | The Telegram bot token never appears in any span tag **or log record** | `TelegramBotApi_NeverLeaksTheToken` | `TelegramAdapterTests.cs` |
| AC13 | The supervisor starts all enabled connections, backs off, stops on an auth fault, and connects nothing without the lease | `ChatGatewayServiceTests` | `ChatGatewayServiceTests.cs` |
| AC13b | Lease handover: the waiting instance connects only after release, with no overlap; a lost renewal disconnects everything | `Service_LeaseHandover_NoOverlap`, `Service_LostRenewal_Disconnects` | `ChatGatewayServiceTests.cs` |
| AC13c | Delete deregisters before stopping the session, and a racing restart can't revive it | `Service_Delete_DeregistersFirst` | `ChatGatewayServiceTests.cs` |
| AC10b | Every chat turn runs `interactive: false`, `ephemeral: false`, so nothing waits on a browser | `Pipeline_TurnIsNonInteractive` | `ChatTurnPipelineTests.cs` |
| AC7b | If the rollover exists (Step 1f), it mints a fresh conversation and always posts the notice | `Pipeline_Rollover_PostsNotice` | `ChatTurnPipelineTests.cs` |
| AC8b | Telegram HTML conversion escapes `& < >`, keeps code, and never throws on unbalanced input | `TelegramTextTests` | `TelegramTextTests.cs` |
| AC14 | The Telegram offset persists across restart; a 409 faults the connection | `Telegram_Offset_*`, `Telegram_409_Faults` | `TelegramAdapterTests.cs` |
| AC15 | Slack acks every envelope (ignored ones too) and reconnects on `disconnect` | `Slack_AcksEveryEnvelope`, `Slack_Disconnect_Reconnects` | `SlackAdapterTests.cs` |
| AC16 | Discord replies can't mention everyone or roles; an auth close code faults | `Discord_AllowedMentionsNone`, `Discord_4004_Faults` | `DiscordAdapterTests.cs` |
| AC17 | Inbound files are validated before download, host-pinned and capped; images reach the turn; documents are registered first; a generated image is uploaded | `Pipeline_Files_*`, `*WebApiTests` host rows | `ChatTurnPipelineTests.cs`, `ChatReplyStreamerTests.cs`, `SlackTests.cs`, `TelegramTests.cs`, `DiscordTests.cs` |
| AC18 | A voice note is transcribed by the user's model and becomes the turn; missing model or format → one curated reply | `Pipeline_Voice_*`, `OpenAITranscriptionServiceTests` | `ChatTurnPipelineTests.cs`, `OpenAITranscriptionServiceTests.cs` |
| AC19 | Groups: owner + mention only; a non-owner is ignored; toggle off ignores all groups | `Pipeline_Group_*` | `ChatTurnPipelineTests.cs` |
| AC20 | Proactive delivery: `Origin` written on mint only; origin first, home when enabled, never a chat-driven turn twice, never incognito | `ChatDeliveryTests` | `ChatDeliveryTests.cs` |
| AC21 | New state roots and the trailing `ChatOrigin` round-trip on both serializer paths | `PersistedTypeNames` + goldens | `GrainStorageSerializerTests.cs`, `SerializationCompatibilityTests.cs` |
| AC22 | The folders, the partial and both host registrations are wired | `ProjectLayoutTests`, `Hosts_RegisterChatGateway`, `AgentApiReachabilityTests` | `ProjectLayoutTests.cs`, `AgentApiReachabilityTests.cs` |
| AC23 | The prefs panel is appended last, reachable from `main.js`, renders all four statuses (needs credentials / awaiting pairing / connected / faulted with reason), and CACHE is bumped | panel assets + layout + PWA tests (status strings pinned as source text; the live look is Step 19) | `ChatGatewayPanelAssetsTests.cs`, `PreferencesLayoutAssetsTests.cs`, `PwaAssetsTests.cs` |
| AC24 (ph 2) | The WhatsApp handshake and signature check over the raw body; uniform 401; `wamid` dedupe; the path is excluded and nothing else | `WhatsAppWebhookTests`, `EasyAuthExclusionTests` rows | `WhatsAppWebhookTests.cs`, `EasyAuthExclusionTests.cs` |
| AC25–AC34 (ph 3) | Signal: see "Phase 3 — Signal, refined plan" → its own AC table | `Signal*Tests` | see that table |

## Review

A `critic` pass on the first draft (2026-10-21) found **no blockers, 4 majors and 6 minors. All 10 were accepted
and folded in**, and none were rejected:

- **M1: the Telegram token leaks through HttpClient logs, not only spans.** Fixed in C11:
  `SuppressInstrumentationScope` handler (the gotcha's technique) plus `RemoveAllLoggers()`. AC12b now captures
  logs too.
- **M2: the chat turn has to be `interactive: false`**, or client tools and elicitation hang waiting for a browser.
  Fixed in C6, with a pipeline row and AC10b.
- **M3: the `ChatOrigin` write path needed an interface method.** Added `IAgentGrain.SetChatOriginAsync` to the
  affected areas, C9 and the Step 17 / AC20 write-path row.
- **M4: the delivery port is per host (ADR 0105), not a `TryAdd` default.** Fixed in C9 and the affected areas,
  which now name every host, the test configurators and `ThrowIfPortsMissing`.
- **Minors:**
  - m1: the old plan's rules are inlined, and the Hermes citations are marked background with clone instructions.
  - m2: gotchas are now listed per MR (Steps 10, 13 and 18), and Step 10's wrong cross-reference is fixed.
  - m3: added AC7b (rollover), AC8b (`TelegramText`), AC13b (lease handover), AC13c (delete ordering) and the
    faulted status in AC23.
  - m4: `ChatOrigin` is a class.
  - m5: the post-merge verification note goes through a `docs/*` MR.
  - m6: `IChatGatewayLease` is in the affected areas, with no-op and fake implementations.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time (`error`) |
| `vulnerable_packages` | **0** | CI package-freshness job |
| `add_policy` | latest-stable | `dotnet add package` at add time (phase 1b's Discord library) |
| Branch policy | never commit to `main` | `.claude/hooks/protect-main.mjs`, `lefthook.yml` |

## Out of scope

- Allow-listing **other senders** (anyone but the paired owner), and multi-user bots.
- **Tool progress** in chat, reasoning in chat, MCP reauth or elicitation prompts in chat, follow-up suggestions.
- Interactive buttons and approvals (Slack Block Kit, Telegram inline keyboards, Discord components) and native
  slash-command registration on Discord and Slack.
- Slack's native `chat.*Stream` API. Hermes notes it's Tier 2, about 20/min, and needs the Agents & AI Apps feature.
  Record it as a follow-up.
- **ffmpeg** (voice transcoding and outbound voice bubbles), TTS replies as voice notes.
- Editing or deleting a chat message re-running or retracting a turn.
- **Baileys** / WhatsApp Web, and WhatsApp template messages beyond the Step 20 decision.
- Matrix, Teams, Mattermost, email-as-a-channel, SMS, and the other Hermes adapters. The contract leaves room for them.
- Surviving a restart with a queued turn; replaying Discord messages missed while disconnected.
- A deep link to the conversation (the web UI still has no conversation routing; the note links to the app root).
