---
id: 0111
date: 2026-10-21
status: accepted
tags: [chat-gateway, telegram, slack, discord, whatsapp, signal, egress, pairing, hosted-service, lease, single-replica, no-migration]
supersedes: []
extends: [0013, 0016, 0022, 0028, 0075, 0096]
amends: []
---

# A chat gateway: each user's own bots on chat apps, connected outward from the single replica

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
