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
identity (captured from their last signed-in save) is set on the turn's `RequestContext`, so `/usage` and `/logs`
attribute chat turns. The enumeration runs detached into a buffer, and no platform call is awaited inside it.

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
