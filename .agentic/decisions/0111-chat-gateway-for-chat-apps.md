---
id: 0111
date: 2026-10-21
status: accepted
tags: [chat-gateway, telegram, slack, discord, whatsapp, signal, signal-cli, azure-files, egress, pairing, hosted-service, lease, single-replica, no-migration]
supersedes: []
extends: [0013, 0016, 0022, 0028, 0075, 0096, 0107]
amends: [0028, 0075]
amended_by: [0127]
---

# A chat gateway: each user's own bots on chat apps, connected outward from the single replica

> **Amended by [ADR 0127](0127-self-hosting-local-storage-provider.md) (2026-11-14): with `Storage:Provider = local`, the lease is a Postgres advisory lock held on a dedicated connection.** The Blob lease below is the `azure` provider.

## Context
Agent42 is reachable only from its web app, its API key door (ADR 0022), MCP (ADR 0070) and inbound email (ADR 0096).
A comparison with OpenClaw and Hermes Agent (2026-10-21) found chat-app reach to be the single biggest gap: both put
the agent in Telegram, Slack, Discord, WhatsApp and Signal behind one gateway process. An earlier, untracked plan
covered Slack alone over Slack's Events API webhook. Hermes Agent's gateway (`NousResearch/hermes-agent`, read at
`09836543`) was studied for its structure: one runner owning authorization, sessions and delivery, and thin per-platform
adapters that own only transport and formatting.

Every primitive a chat turn needs already exists: `IAgentGrain.ChatAsync` (with `interactive: false`), `StopAsync`,
the conversation index, stored agents, per-user secret protectors, and the push and webhook seams.

## Decision

### 1. One in-process gateway, one adapter per platform
`ChatGatewayService` (an `IHostedService`, one per process) supervises one session per runnable connection.
`IChatPlatformAdapter` is the only per-platform seam: validate credentials, connect, parse, send, edit, typing,
upload/download, and format. **An adapter owns no authorization and no session state** (Hermes' runner/adapter
boundary). `ChatTurnPipeline` owns everything else.

### 2. Outbound connections, not webhooks, wherever the platform allows it
Telegram long polling, Slack Socket Mode (phase 1b), the Discord gateway websocket (phase 1b). Phase 1 adds **no
public endpoint and no `excludedPaths` entry**: no signature verification, no Slack URL-verification bootstrap, and
DevHost runs the whole path with no tunnel. WhatsApp (phase 2) is the exception: Meta's Cloud API offers only a
webhook. Signal (phase 3) talks to a local `signal-cli` sidecar.

### 3. Bring your own bot
Each user creates their own bot per platform and pastes its credentials into Preferences → Chat apps. They are
validated against the platform on save (a rejected token creates nothing), protected under the Data Protection purpose
`Agent42.ChatGateway.Secret.v1` before the grain sees them, and never returned by a GET. The operator holds **no
platform secret**: no GitHub secret, no Bicep parameter, no Config Health row. A shared operator bot with account
linking was rejected.

### 4. Only the paired owner talks to the agent
Saving credentials opens a pairing window: a ten-character code (no `0 O 1 I L`), valid 15 minutes, compared in
constant time, locked after 5 wrong tries. The first **DM** whose text is the code binds that platform user as owner.
After that, **only the owner's messages start anything, in DMs and groups alike**. Everyone else, bots included, is
ignored silently and creates no server state (Hermes #9337: answering strangers is noise and an information leak).
Because only the owner can trigger a turn, no bot-loop guard is needed; adding "allow other senders" or "allow bots"
must bring one back.

### 5. Routes, conversations and commands
- A route is a chat, or a thread in one. `ChatRouteGrain` stores the route's current conversation id (prefix `chat-`,
  disjoint from ADR 0016's `agent-` and ADR 0070's `mcp-`). A stored id rather than a hash of the route is what makes
  `/new` possible in a DM with no threads.
- **Time never starts a new conversation** (Hermes' rule). A DM conversation rolls over after
  `ChatGatewayLimits.MaxTurnsPerConversation` (100) turns because its history is one 2 MB session item (ADR 0004)
  and ADR 0084's compaction stubs tool payloads only. The rollover always tells the chat. `chat-` ids are deliberately
  not retention-windowed (ADR 0068 keys on the other two prefixes).
- A target agent binds only when the route mints a conversation, never under an existing history (ADR 0003). A
  conversation deleted in the web UI starts fresh on the next message.
- Commands (`/new`, `/stop`, `/model`, `/agent`, `/sethome`, `/status`, `/help`) are the first token only, dispatched
  from a name → handler table, and run **inline, ahead of every queue**, so `/stop` never waits behind the turn it
  stops.

### 6. The turn pipeline
Bots and own echoes are dropped; the platform message id is claimed in a gateway-owned dedupe that survives
reconnects; the owner check runs; then each route has a FIFO whose slot is claimed synchronously before any await,
with different routes running in parallel up to 8. Each turn calls
`ChatAsync(text, model, attachments: null, ephemeral: false, interactive: false)`: an interactive turn would pause on a
client tool, an MCP elicitation or a suggestion, waiting for a browser a chat app cannot be. The owner's telemetry
identity is set on the turn's `RequestContext`, so `/usage` and `/logs` attribute chat turns. The enumeration runs
detached into a buffer, and no platform call is awaited inside it. The chat waits at most 15 minutes for one turn: then
it says where the reply will appear and frees the conversation's queue and the global slot, while the turn runs on to
its persist (the "cap the WAIT, never the enumeration" rule). A message with no text (a sticker, an uncaptioned photo)
gets a one-line "text only for now" answer rather than an empty model call.

**The owner's identity is persisted.** A chat turn has no HTTP request, and `/usage` keys a user on email, then name,
then id. So `ChatConnection.OwnerTelemetry` stores the owner's `UserTelemetryContext` (id, name, email, full name,
roles, identity provider) from their latest signed-in save of the connection. It is the owner's own data in their own
grain state, refreshed on every save; roles can go stale between saves, and they only label spans. The alternative,
storing the id alone, would make every chat turn show under an opaque object id in `/usage`.

### 7. Replies
A placeholder is sent, then edited with plain frames on the platform's pace (Telegram: about one action per second
per chat). Only the final frame is formatted, and a format the platform rejects is resent plain. Only the streamer
declares the final, by editing the placeholder, never by a fresh send. Rate-limited edits are skipped, and three in a
row stop the streaming. The final waits out a rate limit only up to 30 seconds (Hermes once slept a 97-minute flood
wait and froze every platform). Every platform call is time-boxed. Over-long replies are split at newlines, never
inside inline code, with fences closed and reopened and `(1/3)` markers. Only text, `TextReset` and image notices
reach a chat. Reasoning, todos, tool calls, suggestions and elicitations never do.

### 8. The gateway lease
Rule 12's single replica does not cover an Azure Container Apps rollover, when the old and new revision run side by
side: two Telegram pollers fight (409) and two Discord sessions both answer. The gateway connects nothing until it holds
a Blob lease on `chat-gateway/lease` (a 60-second lease renewed every 20 seconds, released on graceful shutdown), and
drops every session the moment a renewal fails. DevHost uses a no-op lease.

### 9. Telegram (phase 1a)
The Bot API is hand-rolled (about ten JSON POSTs). Telegram HTML parse mode, not MarkdownV2, since HTML needs only
`& < >` escaped. Connect deletes any webhook **without** dropping pending updates, and the poll cursor is committed
after each batch, so a restart neither replays nor loses messages. A 409 (another poller) or 401/404 (dead token)
faults the connection instead of retrying. **The token is in the URL path**, which OpenTelemetry never redacts, so
both named clients suppress their HttpClient spans and drop `IHttpClientFactory`'s request logging.

### 10. Phases
1a: core and Telegram. 1b: Slack and Discord. 1c: files, voice notes (a separate ADR adds server-side speech-to-text
to ADR 0038), groups, and proactive delivery to the origin or home chat. 2: WhatsApp Cloud API. 3: Signal via
`signal-cli`. The plan is `.agentic/specs/plan-chat-gateway.md`.

## Alternatives considered
- **Webhooks where possible (the old Slack plan).** Rejected: every platform needs its own public path, signature
  scheme and bootstrap, and none can be exercised offline. Discord's gateway is the only way to receive ordinary DMs,
  so the gateway must hold outbound connections anyway.
- **A shared operator bot per platform with account linking.** Rejected: the operator would hold every platform
  secret, and every user's traffic would share one bot and its rate limits.
- **WhatsApp through Baileys (an unofficial WhatsApp Web client).** Rejected: it breaks WhatsApp's terms of service
  and risks the number being banned.
- **A Slack/Telegram SDK.** Rejected for Telegram and Slack: about ten calls each don't justify a dependency. Discord's
  gateway protocol (heartbeat, resume, intents) is the exception, and phase 1b picks a library.
- **Interrupt as the busy policy (Hermes' default).** Rejected: a chat user typing "and also…" mid-turn expects both
  messages answered. `/stop` is the explicit interrupt.
- **A deterministic conversation id per route.** Rejected: `/new` has nowhere to go in a DM without threads.

## Consequences
- **Egress (an ADR 0013 extension, opt-in per user):** replies, which can carry document excerpts, tool output and
  memory recall, are stored by the chat platform under the user's own bot. In a group (phase 1c) every member sees the
  reply. It is opt-in per connection, so this is not ADR 0008's rejected always-on shape. The panel says so in plain
  words.
- **Inbound:** the sender is pinned to the paired owner by the platform's own identity. The residuals are a compromised
  platform account and a token at rest (encrypted, replaced by pasting a new one). Files the owner forwards (phase 1c)
  are still untrusted content.
- **Pairing can be blocked, never stolen:** wrong codes from ANY sender count toward the five-try lockout, so someone
  who finds the bot's username while a window is open can lock it. That is an accepted denial-of-service residual: the
  owner presses "New pairing code" and tries again. Counting per sender would let a stranger try five codes per account
  they control.
- **In-process state** (rule 12): sessions, the dedupe, the route queues and their guards, and the lease holder. Lost on
  restart: queued turns, and Discord messages sent while disconnected (Telegram keeps updates for 24 hours; Slack
  redelivers). Turns in flight at shutdown are not cancelled into a consistent state.
- One new Blob container (`chat-gateway`), on the existing account-scoped role assignment.
- A Cosmos JSON trap surfaced and is pinned: the serializer omits a member holding its CLR default, so a bool with a
  `true` initialiser flips on reload. `ChatConnection` keeps every bool defaulting to false.

## Verification
- Tests: `ChatTurnPipelineTests`, `ChatReplyStreamerTests`, `ChatGatewayServiceTests`, `ChatGatewayApiTests`,
  `TelegramUpdatesTests`, `TelegramTextTests`, `TelegramAdapterTests` (including `TelegramBotApi_NeverLeaksTheToken`,
  mutation-checked), the grain tests, and the serializer goldens.
- DevHost, 2026-10-21: the real Telegram adapter against a local stand-in for the Bot API. A rejected token was
  refused. A stranger and a wrong code were ignored. The code paired the bot. A DM was answered by editing the
  placeholder, `/new` and `/status` answered, the conversation appeared in the sidebar, and the panel showed
  "connected".
- Not yet verified live: a real Telegram bot, and the lease across a real revision rollover (plan Step 19).

## Amendment (2026-10-21): Slack and Discord, phase 1b

Phase 1b adds the two other outbound-connection platforms behind the same adapter contract. Nothing in the pipeline,
the supervisor, the lease, the state or the endpoints changed: the adapter seam held.

### Slack over Socket Mode
- Each user's own Slack app, with two credentials: the bot token (`xoxb-`, for the Web API) and an app-level token
  (`xapp-`, scope `connections:write`, for the socket). Both are validated on save (`auth.test`,
  `apps.connections.open`), and a pasted user token (`xoxp-`) is refused by name. The panel gains a write-only
  app-token field, shown only for Slack. On an edit, a token field left blank keeps the stored token, so rotating one
  of the two doesn't mean re-pasting the other; the merged pair is validated and stored as one credential.
- **Every envelope is acked, before it is read, ignored ones included.** Slack redelivers what is not acked and
  disables the subscription after sustained failures. An envelope past the 8 MB memory guard is not read, but it is
  still acked, from the `envelope_id` in its first or last bytes. Back-to-back refreshes are spaced out (from 4 s up
  to 30 s), so a flapping workspace can't make the session hammer `apps.connections.open`.
- A `disconnect` envelope (`warning`, `refresh_requested`: Slack recycles a socket about hourly) reconnects **inside**
  the session rather than through the supervisor. Replies go over the Web API, never the socket, so a reply that is
  streaming survives the refresh. `link_disabled` (Socket Mode turned off) is a permanent fault. A socket that fails
  ends the session, and the supervisor reconnects it.
- The event filter is old C4: `message` / `app_mention` with no subtype or `file_share`; the bot's own user dropped,
  other `bot_id`s flagged. The dedupe id is `channel:ts`, since a `ts` is unique per channel only; it also collapses
  the `message` + `app_mention` pair one mention produces. In a channel, a message is always a thread (its
  `thread_ts`, or its own `ts` at the top level), so a reply threads under the mention. A follow-up in a thread the
  bot posted in counts as a reply to it.
- **Formatting is mrkdwn, sent as `text`**, converted by `SlackText`. Slack also offers `markdown_text`, but documents
  how mrkdwn escapes `& < >` and not how `markdown_text` does, and that escaping is what keeps a reply from forming
  `<!channel>`. `chat.update` caps `text` at 4,000 characters, so that is the message length.
- **Commands start with `!`**, because Slack's composer takes a leading `/` as a slash command.
- Slack offers bots no typing indicator outside its AI-apps surface, so typing is a no-op. The app manifest is in
  code (`SlackAdapter.AppManifest`), and the setup guide quotes it verbatim (a test pins that).

### Discord over the gateway, with Discord.Net
- **Library: `Discord.Net.WebSocket`** (3.20.2, added with `dotnet add package`), for the gateway socket only. NetCord
  was the alternative (active, no dependencies) but has no stable release, which the repo's `add_policy:
  latest-stable` rules out.
- **Intents `Guilds | GuildMessages | DirectMessages`, never the privileged MESSAGE_CONTENT.** Discord still delivers
  the content of DMs and of messages that mention the bot, which is everything the owner gate lets through. The
  consequence for phase 1c: in a guild thread the owner must mention the bot, since an unmentioned message arrives
  empty.
- **A non-reconnectable close code is a permanent fault.** Discord documents 4004 and 4010–4014 as "Reconnect: false",
  but Discord.Net reconnects forever on them. The adapter watches `Disconnected` for those codes, ends the session
  with `ChatAuthException`, and the supervisor records the fault. A 401 from the library's own `/gateway/bot` call is
  treated the same way. That it arrives through `Disconnected` is read from the Discord.Net source
  (`ConnectionManager` raises it from its reconnect loop); no test can reach that call, so Step 19 confirms it live.
- **Replies use a hand-rolled REST client, not the library's.** Discord.Net's REST retries and waits out rate-limit
  buckets inside the library, which is the inline sleep section 7 forbids, so a 429 must come back to the streamer as
  `RateLimited`. Every create and edit carries **`allowed_mentions: {"parse": []}`**, so no reply can ping
  `@everyone`, `@here`, a role or a user.
- In a guild, a top-level mention is routed to the thread a reply starts on it (Discord gives that thread the
  message's own id), so the mention and its follow-ups are one conversation. A bot not allowed to start threads (403)
  answers in the channel instead, as a reply to the mention. Tables become bullet lists; other
  Markdown is native. 2000 characters a message, at most 8 per reply.
- `/` is the command prefix. The bot registers no application commands, so Discord sends `/new` through as text. Not
  documented by Discord; the Step 19 live check confirms it.

### Consequences (phase 1b)
- One new dependency tree: `Discord.Net.WebSocket` → Core, Rest, Dave (libdave bindings), Newtonsoft.Json.
- Two more connections held by the single replica, both covered by the existing lease: Slack's socket (which Slack
  would otherwise load-balance across both revisions during a rollover) and Discord's gateway session.
- Lost on restart, as before: Discord messages sent while disconnected. Slack redelivers what was not acked.
- Egress is unchanged in kind: replies are stored by Slack (and visible to workspace admins with export rights) and
  by Discord, under the user's own app or bot.

### Verification (phase 1b)
- Tests: `SlackEventsTests`, `SlackTextTests`, `SlackAdapterTests` (including `Slack_AcksEveryEnvelope`,
  `Slack_AcksAnOversizedEnvelope` and `Slack_Disconnect_Reconnects` against a real local websocket), `DiscordMessagesTests`, `DiscordTextTests`, and
  `DiscordAdapterTests`. `Discord_AllowedMentionsNone` and `Discord_4004_Faults` run the real Discord.Net client
  against a local gateway. The ack test, the mentions test and the 4004 test were each mutation-checked.
- DevHost, 2026-10-21: the real Slack and Discord adapters against local stand-ins. For both: a rejected token was
  refused; a stranger and a wrong code were ignored; the code paired; a DM was answered by editing the placeholder;
  `!new` / `/new`, `!status` and `/help` answered; the bot's own echo was ignored. Slack acked every envelope.
  Discord sent every message with `allowed_mentions` none.
- Not yet verified live: a real Slack app and Discord bot, `/text` pass-through in a Discord DM, and the lease across a
  rollover with Slack and Discord connected (plan Step 19).

## Amendment (2026-10-21): files, voice notes, groups and delivery, phase 1c

### Inbound files: skip and note, not all-or-nothing
- Files ride the agent-run core (`AgentRunFileError` + `StoreAgentRunFilesAsync`), which gains a metadata overload and a
  byte-level overload so the gateway can reuse it. Every file is judged from its **metadata** (name, declared type,
  size) **before** anything is downloaded, then downloaded through the adapter, which pins the platform's own host and
  caps the read at the 10 MB attachment limit:
  - Telegram: `getFile`'s `file_size` is checked first, and the token only ever goes to the fixed Bot API base;
  - Slack: `files.slack.com` with the bot token, no redirects, `text/html` is an auth failure;
  - Discord: `cdn.discordapp.com` / `media.discordapp.net`, no token.
- **A file that can't ride is skipped, and both the owner and the model are told why.** The agent-run door rejects a
  whole batch because automation retries cleanly; a chat user cannot easily resend a mixed batch. This is inbound
  email's policy (ADR 0096). Images become attachments on the turn. Documents are ingested and **registered before the
  turn**. A copilot agent refuses documents, as on the other doors. At most 10 files a message.
- A message with no text and only files that fail the metadata check gets the reason and mints no conversation. One
  whose files pass that check but then fail to download or ingest has already minted its conversation, which stays
  listed and empty (accepted: the ingest needs the conversation's owner key). A captionless photo is a
  turn of its own, titled by the file name.

### Outbound images
- An image the model generates is uploaded **once, after the text final**, as its own message in the same chat,
  thread and reply chain:
  - Telegram `sendPhoto`;
  - Slack's external upload (`files.getUploadURLExternal`, the bytes to an upload URL that must be on the pinned file
    host, then `files.completeUploadExternal`; this adds the **`files:write`** scope to the manifest);
  - Discord a multipart message with `allowed_mentions` still none.
- An image that can't be sent leaves one note pointing to Agent42, never a retry loop. A failed reply uploads nothing.

### Voice notes
- See [ADR 0113](0113-server-side-speech-to-text-for-voice-notes.md). They are off per connection by default, use the
  owner's own transcription model, and are transcribed before the route is touched. Every failure gets one curated
  reply and no conversation. The transcript is echoed as one quoted line.

### Groups
- The per-connection toggle is now in the panel, off by default. The toggle's note says that everyone in the group sees
  the replies, including what the agent quotes from documents, tools or memory. The gate is unchanged from phase 1a:
  only the owner, and only when the bot is mentioned or replied to. `/new`, `/stop` and `/help` pass without the mention.
- **Telegram privacy mode:** a bot in a group sees commands, replies to it and messages sent via it, but not a plain
  `@mention`. The panel and the guide tell the owner to turn privacy off (`/setprivacy`).

### Proactive delivery (section 9 implemented)
- **The port** `IChatDeliverySender` is per host (ADR 0105). Backend and DevHost get the gateway's implementation from
  `AddChatGateway()`, which both call before `AddAgent42Silo()` (pinned by `Hosts_RegisterChatGateway`). Test silos
  register a recording sender or `NullChatDeliverySender`. The grain takes it as a direct constructor dependency, so a
  host that forgets it fails at startup (`ThrowIfPortsMissing`).
- **The origin** (`ChatOrigin { ConnectionId, RouteKey }`) is written once, by `SetChatOriginAsync`, when the route
  mints a conversation and before its first turn. It is a trailing `[Id(18)]` on `AgentGrainState`, defaulting to `null`.
- **Away replies.** `EnqueuePromptAsync` and the reminder path accumulate the run's text (`TextDelta` appends,
  `TextReset` clears, bounded by the webhook limit) and hand it over **next to `SendAwayReplyPushAsync`, at its two sites
  only**:
  - to the origin chat when its connection is paired and live, and, for a group origin (`ChatOrigin.IsGroup`), still
    has groups on. **This needs no notifications toggle:** a conversation started in a chat answers in that chat, as
    its live turns do;
  - else to the first connection with notifications on and a home route (`/sethome`).

  A web conversation whose model already used `send_push_notification` this turn skips the home fallback, since that
  notification went home already. Accepted residual: in a chat-started conversation whose origin IS the home chat, a
  turn that also used `send_push_notification` posts twice there (the notification, then the reply). A chat-driven turn runs `ChatAsync`, so it never reaches those sites and is never
  posted twice.
- **`send_push_notification`** also goes to the home chat, on the sent path only. A user with no Web Push device still
  gets the tool's refusal and no chat message. The tool's model-facing contract is unchanged.
- **The hand-off never holds the grain.** The conversation grain is non-reentrant, and a platform send can take the
  streamer's time-box several times over. `ChatDeliverySender.DeliverAsync` returns at once, and the send runs detached.
  Incognito never delivers.

### Consequences (phase 1c)
- **Egress, extended:**
  - generated images are stored by the chat platform, like the replies;
  - voice audio goes to the owner's transcription endpoint (ADR 0113);
  - scheduled results and queued runs of a chat-started conversation are written back into that chat (a group one only
    while groups are on); the home chat receives the rest, and the agent's notifications, only with "deliver
    notifications here" on.
- More in-process state on rule 12's single replica: the delivery hand-off reaches only sessions live on this replica.
- Slack apps created before phase 1c need `files:write` added and the app reinstalled before image upload works.
  Without it, the upload fails and the note points to Agent42.

### Verification (phase 1c)
- `ChatTurnPipelineTests`:
  - files: image and document reach the turn; an oversized file is skipped and never fetched; unusable-only files mint
    nothing; a captionless photo is a turn; a failed download is skipped;
  - voice: a voice note becomes a turn; one reply per failure kind.
- `ChatReplyStreamerTests`: images are uploaded once after the final; a failed reply uploads nothing.
- Adapter rows: Telegram `getFile` and `sendPhoto`, with the token-leak test now covering both; Slack's external upload
  and its untrusted-URL refusal; Discord's multipart message without mentions.
- `ChatDeliveryTests`: the write path, origin, home fallback, the push tool, never-twice, incognito, and the hand-off
  that doesn't wait on a hung platform. The serializer suites cover a state without `Origin`.
- `ChatGatewayPanelAssetsTests` covers the three toggles and the group warning.
- Not yet verified live (plan Step 19): uploads on the real platforms, a real transcription model (and Ogg), a
  scheduled prompt delivered to its origin chat, and Telegram group mentions with privacy mode off.

## Amendment (2026-10-21): Signal through a lease-gated signal-cli, phase 3

Phase 3 ran before phase 2 (WhatsApp), the user's call. It amends section 3 ("each user links their own Signal
account") and the plan's sidecar. Plan: `.agentic/specs/plan-chat-gateway.md`, section "Phase 3 — Signal, refined plan".

### A dedicated number, not a linked device
- Each user registers a **spare number of their own** in signal-cli (`register`, then the code Signal sends by text or
  call, then `verify`). That number is the bot's own Signal account, so pairing, the owner check, DMs and groups work as
  on the other platforms.
- **Rejected: a linked device of the user's own account.** Such a device has three problems:
  - it could reach its owner only through Note-to-Self;
  - its replies would be sent *as the user*, and phone notifications for them are unreliable (signal-cli
    `--notify-self`);
  - the daemon would receive and store **all** of the user's inbound Signal traffic and attachments.
- **The number is the credential, and it is never pasted.** `POST /api/chat-connections/{id}/signal/register` and
  `/verify` are the only way one is stored. The generic create and edit endpoints refuse a Signal credential.
  Otherwise a user could paste another user's registered number, pass the "does the daemon hold it" check, and drive
  that bot.
- **One number, one connection.** `ISignalNumberDirectoryGrain` is keyed by the E.164 number. The number is claimed
  **before** `register` is called, because a `register` over someone else's number would reset their registration.
  - A pending claim expires after 30 minutes, and a failed `register` releases it, so a number can't be squatted.
  - A claim another connection holds is answered with a generic 409.
  - The number is the grain key in clear: a hash of a phone number is brute-forceable.
- **Delete** happens in this order. Each later step is best effort:
  1. read the connection first;
  2. deregister it;
  3. stop the session;
  4. have the daemon `unregister` the number and `deleteLocalAccountData`, through the optional `IChatPlatformCleanup`,
     **only if this connection still holds the number's claim**: an abandoned registration's claim can have expired and
     been taken by another connection, whose number must not be unregistered;
  5. release the claim.

  A delete that lands on an instance without the lease (a rollover) can't reach the daemon. The claim is still released,
  and the number stays registered with its keys on the share until it is registered again. Switching a connection to
  another number releases the earlier pending claim, and `verify` checks that the claim is still this connection's
  before it asks the daemon.

### signal-cli is a child process of the backend, started only under the gateway lease
- The backend image bundles signal-cli and a JRE 25. It is the JVM build, pinned by version and SHA-256; the GraalVM
  build is marked experimental upstream.
- **Rejected: a sidecar container.** The daemon has no HTTP authentication, and its store is SQLite in WAL mode behind
  Java file locks. A sidecar runs in both revisions during a rollover, so two daemons would share one store with only an
  unverified SMB lock between them.
- **The lease notifies participants.** `ChatGatewayService` sends `IChatGatewayLeaseParticipant`s two signals:
  - "renewed" after acquiring the lease and after every renewal;
  - "lost" **before** it stops the sessions, so a hung dispose can't delay the kill. It is sent on renewal failure, in
    the failure path and on shutdown, always before the lease is released.
- **`SignalDaemon`** starts the process lazily, only while the lease is held, and stops it with SIGTERM, then SIGKILL
  after 5 s.
- **A watchdog** stops the daemon when no renewal arrived within `lease duration − renew interval − margin` (35 s for
  the Blob lease). That closes the window where a hung renewal outlives the lease while a new revision acquires it.
  `IChatGatewayLease` gained `Duration` for this.
- **Arguments:** `--config /mnt/signal daemon --http=127.0.0.1:<port> --receive-mode=on-connection --ignore-stories
  --no-receive-stdout`.
  - **The loopback bind is load-bearing.** signal-cli validates the Host header only on a loopback bind. Measured:
    `/api/v1/rpc` with a foreign Host answers 421, while `/api/v1/check` answers 200 regardless.
  - `on-connection` leaves messages on Signal's server while no event stream is open. The default, `on-start`, would ack
    them with nobody listening.
  - There is one event stream per number (`/api/v1/events?account=`). A stream without `account` captures the account
    list at connect time.

### The store is on Azure Files
- The store is a second share, `signal-data`, on ADR 0107's key-access account. It is linked into the environment as
  `agent42-signal` and mounted at `/mnt/signal` on the backend container only.
- **For this store only, this reverses the "SQLite over SMB" objection of ADR 0085 and ADR 0107.** It holds here because
  exactly one daemon writes the store at a time, under the lease. The Copilot runtime had no such single-writer
  guarantee.
- **Signal ships dormant.** `ChatGateway:Signal:Enabled` (Bicep `signalEnabled`) defaults to false.
  - Off: `AddSignalChat` registers nothing, no daemon can start, the panel doesn't offer Signal, and the backend keeps
    its current CPU and memory.
  - On: the backend gets +0.25 CPU / +0.5Gi for the JVM, which measured 186 MiB idle.
  - **Turning it on waits for two things:** the plan's Step 24 measurement (WAL on the mount, with and without `nobrl`),
    and the operator's confirmation of the Copilot-shell risk under Consequences.

### Replies, files and formatting
- **Final-only.** Signal can edit a message, but its limits are undocumented and every edit notifies again.
- **Typing** is refreshed every 10 s; Signal clears it after 15 s.
- **Length:** 2000 characters per message. Signal moves text past 2000 UTF-8 bytes into a long-text attachment.
- **Formatting:** Markdown becomes plain text plus `textStyles` ranges (BOLD, ITALIC, STRIKETHROUGH, MONOSPACE), in
  UTF-16 units. Commands start with `/`.
- **Mentions** come from `mentions[].uuid`, never the text. The U+FFFC placeholder is stripped, so "@bot /new" is a
  command. A reply in a group quotes the triggering message.
- **Dedupe:** Signal has no message id, so dedupe is on `sender ACI:timestamp`.
- **Ignored input:** sync, story, receipt, typing, call and edit envelopes never become turns, and neither do
  reaction-only or delete-only messages.
- **Files:** inbound files come through `getAttachment`. The declared size is checked first, and the decoded bytes are
  capped again. Generated images go out as RFC 2397 data URIs.
- **Voice notes** are AAC, which the transcription endpoints don't list, so the existing curated format reply is
  expected (ADR 0113). This is checked live.
- **Faults:** a send that Signal answers with a proof-required token (a rate-limit captcha) or "unknown account" faults
  the connection, with a reason the panel shows. Solving the captcha from the panel is out of scope.

### Consequences (phase 3)
- **Egress (an ADR 0013 extension, opt-in per user).** Replies are end-to-end encrypted to the owner's devices. But the
  **bot number's** identity keys, and the messages waiting for it, live on the operator's Azure Files share, so the
  operator can read what the bot receives. The user's own Signal account is never involved. The panel says so.
- **The Copilot shell can reach the daemon and its store.** ADR 0107's built-in shell runs as the backend's uid, in the
  backend container.
  - It can call the unauthenticated daemon on loopback: send as any connected number, and read any number's events.
  - It can read `/mnt/signal`.
  - The loopback bind protects against the network, never against that shell, nor against the SearXNG sidecar, which
    shares the replica's network namespace (it runs an upstream image, not a shell anyone controls).
  - The daemon is started with a scrubbed environment (`SignalProcessEnvironment`: `PATH`, `JAVA_HOME`, `HOME`, and a
    few locale variables), so the JVM never inherits the backend's connection strings, keys or managed-identity
    endpoint.

  This is the same class as ADR 0107's accepted risks: the shell can already forge the principal header and mint
  managed-identity tokens for all app data. It stays cross-conversation rather than cross-user while only the owner's
  principals can sign in, and the Copilot kill switch is the response. **It is an extension of those accepted risks,
  pending the operator's explicit confirmation before `signalEnabled` is turned on.** If it is declined, the
  alternatives are a separate uid for the JVM (a root entrypoint that drops privileges), or refusing to start Signal
  while built-in tools are on.
- **Accepted residual:** any signed-in user can make Signal send a code to an arbitrary number, and sign out that
  number's phone, by calling `register` on it. Signal's own rate limits bound this, and only the owner's principals can
  sign in.
- **Expiry:** signal-cli releases older than three months may stop working, and nothing upgrades it automatically.
  Config health reads the baked `SIGNAL_CLI_VERSION` and `SIGNAL_CLI_RELEASED`, warns at 75 days and errors at 90.
- **Licence:** signal-cli is GPL-3.0-only. It runs as a separate program in a private image, which is aggregation, not
  linking.
- **Rule 12:** the daemon process is more in-process state on the single replica. The lease now protects a **store**,
  not only a connection.

### Verification (phase 3)
- **Tests:**
  - `SignalEventsTests`, `SignalTextTests`, `SignalRpcTests`, `SignalAdapterTests`, `SignalDaemonTests`;
  - `SignalRegistrationApiTests`, `SignalNumberDirectoryGrainTests`;
  - `ChatGatewayServiceTests` (the participant hook);
  - `SignalImageTests`, `CopilotFilesMountTests`, `ConfigHealthTests`;
  - the serializer goldens.

  Mutation-checked: the watchdog reset, the number redaction, lost-before-stop, and the pasted-credential refusal.
- **Measured locally** (signal-cli 0.14.9 in Docker):
  - every daemon flag is accepted;
  - `verify` is a JSON-RPC method in multi-account mode;
  - `listAccounts` returns the ACI;
  - an unknown account's stream answers 400;
  - the backend image runs `java` 25 and signal-cli as the app uid;
  - the daemon shuts down cleanly on SIGTERM.
- **DevHost, 2026-10-21**, against a stand-in for the daemon:
  - a pasted number was refused;
  - a captcha demand was answered with the link;
  - register, verify and pairing worked;
  - a stranger and a wrong code were ignored;
  - a DM was answered with typing, then a styled final;
  - a duplicate ran once;
  - `/new` started a new conversation;
  - a group mention was answered in the group as a quote, and an unmentioned group message was ignored.

  It surfaced one bug, now fixed: `JsonContent`'s chunked body arrived empty, so RPC calls are sent buffered.
- **Not yet verified:**
  - the Azure Files measurement (plan Step 24);
  - everything live (plan Step 31): a real number, `on-connection` holding messages across a daemon restart, AAC voice
    notes, the long-text attachment, and a revision rollover with the daemon running.
