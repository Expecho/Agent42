---
id: 0034
date: 2026-07-26
status: accepted
tags: [agents, conversations, privacy, ephemeral, incognito, session, persistence, routing]
amended_by: [0044, 0085, 0106]
---

# Ephemeral ("incognito") chat

> **Amended by [ADR 0134](0134-incognito-agent-chats.md) (2026-11-16): incognito is offered on fresh per-chat agent chats too.** The bind skips its write when ephemeral and the client resends `agentId` each incognito turn; continuous and Copilot agents stay excluded (a continuous id is refused with 400).

> **Amended by [ADR 0106](0106-agentgrain-services-and-turn-context.md) (2026-10-19): the ephemeral flag is a per-run `TurnContext.Ephemeral`, not the `_ephemeral` field.** There is no clear ritual any more. A reminder or background run that interleaves with an incognito turn cannot unset it, and a tool called outside a run reads the fail-closed context (ephemeral).

> **Amended by [ADR 0085](0085-copilot-agent-kind.md) (2026-10-05): a copilot agent's chat refuses incognito turns.**
> The Copilot runtime writes its transcript to Blob during the turn, so the grain refuses an ephemeral turn there
> before anything runs, and the client locks the toggle off.

> **Amended by [ADR 0044](0044-persistent-topbar-action-cluster.md) (2026-08-21) — the toggle moved.**
> `#incognitoBtn` no longer lives in `.composer__frame`, and no longer "tints the composer frame while
> engaged": it sits in a persistent top-right action cluster in `.topbar`, and the engaged tint moved to
> the topbar with it (same `body.is-incognito` class, so `setIncognito` is unchanged). Every behavioural
> rule below — off by default, interactive only on a fresh empty regular chat, hidden under the picker
> lock — still holds exactly as written.

## Context
Every chat in Agent42 is durable by design: `AgentGrain` re-serializes its whole session into one
`agent-state` Cosmos item on every turn (`ChatCoreAsync` → `WriteStateAsync`, ADR 0004/0001), and the API
layer lists the conversation in the sidebar by touching a per-user index grain
(`ConversationIndexGrain.TouchAsync`, called from `POST /api/chat/{id}`). There was no way to have a
throw-away or sensitive conversation that leaves no trace.

The user asked for an **incognito** option: a chat whose history is never persisted and that never appears
in the sidebar. Two facts frame the design:

- **The two persistence surfaces are already independent.** Session state (the grain's `WriteStateAsync`)
  and sidebar listing (the index grain's `TouchAsync`) are separate — the grain never touches the index;
  only the API layer does. An ephemeral chat is simply one that skips **both**.
- **There is an exact prior-art pattern for a turn-scoped, non-persisted signal.** The user's location
  (ADR 0012) rides the chat request into `_turnLocation`, a grain field that lives for the *activation* and
  is cleared explicitly in a `finally` — never written to state. Ephemerality is the same shape.

## Decision
Add a trailing defaulted **`bool Ephemeral = false`** to `ChatRequest` (`AgentApi.cs`). The `POST
/api/chat/{id}` handler, when it is set, (a) **skips the `TouchAsync`** call so the conversation never
enters the sidebar index, and (b) passes the flag into the grain via a new trailing
`bool ephemeral = false` parameter on `IAgentGrain.ChatAsync`.

In the grain the flag lands in **`_ephemeral`**, a turn-scoped, non-persisted field set at the top of
`ChatAsync` and cleared in the same `finally` as `_turnLocation` (and defensively reset on the two async
entrypoints, `EnqueuePromptAsync` and `ReceiveReminder`, which are always persisting paths). It gates the
two end-of-turn `WriteStateAsync` calls (`ChatCoreAsync` and `GenerateImageTurnAsync`): an ephemeral turn
runs the model and streams a reply, then **skips the session write** entirely. Because the grain key is
still `{userId}/{conversationId}`, an ephemeral conversation is just one whose session item is never
written and whose id is never listed — nothing to clean up, no new grain, no state migration.

**Scope is session-only.** Documents are **blocked at the composer** in an ephemeral chat (client-side
gate in `handleFiles`), because RAG ingestion (`RegisterDocumentAsync`) writes durable chunks to the Cosmos
vector store — that would outlive the chat. Images stay allowed; their bytes persist in the `attachments`
blob container (an accepted, documented limitation of "session-only"). **Scheduling is refused:**
`SchedulePromptAsync` returns a refusal and registers no reminder when `_ephemeral`, because a durable
Orleans reminder would fire `ReceiveReminder`, which persists the session and sends the away-reply Web
Push (ADR 0028) — both break the guarantee.

The UI is a composer **incognito toggle** (`#incognitoBtn`), off by default, interactive only on a fresh
empty regular chat and locked once the first message is sent (ephemerality is fixed at creation, like the
conversation id). It hides under the same picker lock as the tools button (regular chats only), and tints
the composer frame while engaged. The offline read cache (ADR 0032) needs no new guard: an ephemeral chat
is never `TouchAsync`'d, so it is absent from `/api/conversations` (and its `cachePut`) and, having no
sidebar row, is never opened via `selectConversation` (so its transcript is never mirrored).

## Alternatives considered
- **Full incognito (blob teardown on end).** Rejected for the MVP. It would need a client-driven end-of-chat
  teardown path and blob deletion, and the bytes still transit Azure Storage during the chat — a large
  surface for a marginal gain over blocking documents (the durable-index concern) and leaving images.
- **A dedicated grain / conversation-id namespace for ephemeral chats** (à la ADR 0016's `agent-` prefix).
  Rejected. Non-persistence is a grain *behaviour*, not a routing choice; a plain UUID plus the
  non-persisted flag needs no new id space and no server-side detection from the id alone.
- **Gate the scheduling tool out of `BuildAgent` when ephemeral.** Rejected. `BuildAgent` runs on activation
  before the first request sets `_ephemeral` (the user-default-tools ordering hazard), so per-turn tool-list
  rebuilding is fragile. Refusing inside `SchedulePromptAsync` is a one-line guard with no ordering hazard.

## Consequences
- **The incognito guarantee is "not persisted, not listed" — not "not in memory."** While the grain stays
  activated, its in-memory `_session` still holds the conversation (so a multi-turn ephemeral chat works and
  `GET /messages` reflects it live). The trace-free property is realized on deactivation: nothing was
  written, so a reopened conversation is empty. If the grain is collected between turns (idle/memory
  pressure), an ephemeral conversation's history is lost mid-chat — inherent to not persisting, acceptable
  for a throw-away chat.
- **Egress is unchanged (ADR 0013).** Ephemeral changes persistence, not routing: a turn on a third-party
  model still sends its content outside the Entra tenant. Incognito is about *durability*, not *privacy from
  the model*.
- **The async paths are unaffected.** `EnqueuePromptAsync` (agent-run) and reminders never set the flag and
  always persist — correct, since those conversations are opened later by their owner.
