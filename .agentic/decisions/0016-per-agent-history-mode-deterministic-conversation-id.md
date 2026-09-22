---
id: 0016
date: 2026-07-15
status: accepted
tags: [agents, conversations, history, routing, agent-definition, continuous, session-cap]
---

# Per-agent history mode via a deterministic conversation id

> **Amended by [ADR 0082](0082-seeded-research-agent-loop.md) (2026-09-22): a research agent is per-chat only.** The registry forces `Continuous = false`
> for it and the editor locks the box — research turns are the largest turns there are, and a continuous chat is
> one 2 MB grain.

## Context
Every stored agent (ADR 0003) starts a **fresh** conversation each time: `startAgentChat` mints
`crypto.randomUUID()` and the backend keys a grain by whatever id arrives (`AgentApi.cs` `AgentKey`),
so an `AgentGrain` **is** one conversation and its history is 1:1 with that conversation
(`AgentGrainState.SerializedSession`). There is no shared-history primitive.

The user asked for the opposite option too: an agent whose every chat continues **one** ongoing
conversation ("continuous"), so context carries across sessions. This must be a per-agent choice, and
existing agents must be unaffected.

Two facts frame the design:
- **The client already owns conversation ids.** The backend simply keys a grain by the id the client
  sends. So "continuous" needs no new grain and no backend routing authority — only a *deterministic*
  id derived from the agent, so every start collapses onto the same grain.
- **Binding is a per-conversation persona snapshot that ADR 0003 makes immutable** (`AgentGrain.BindAgentAsync`
  no-ops once `AgentId` is set). History mode is a **routing** decision, not a persona trait that changes
  model behaviour.

## Decision
Add a trailing defaulted **`[Id(7)] bool Continuous = false`** field to `AgentDefinition` (the history-mode
flag), plumbed through `AgentUpsertRequest` and the POST/PUT `/api/agents` handlers, and exposed as a
checkbox in the agent editor.

The flag lives on `AgentDefinition` **only** — never snapshotted into `AgentBinding` — and is read **live**
at chat-start in the client. When an agent is continuous, `startAgentChat` derives the conversation id as
`` `agent-${agent.id}` `` (otherwise a random UUID); `crypto.randomUUID()` never emits the `agent-` prefix,
so the two id spaces never collide. When that deterministic id already appears in the loaded conversation
list, `startAgentChat` delegates to `selectConversation` to load the ongoing history instead of showing a
blank thread over a grain that already has state.

No migration: a trailing defaulted field deserializes as `false` (per-chat) under both the Orleans binary
field-id and JSON property-name serializers, so agents persisted before this change keep today's behaviour.

## Alternatives considered
- **Snapshot the flag into `AgentBinding`.** Rejected. Binding is immutable per ADR 0003, so snapshotting
  would freeze the mode per-conversation and, worse, treat a routing choice as a persona trait. Keeping it
  off the binding lets a user flip an agent's mode for **future** chats while every existing conversation is
  untouched — exactly consistent with ADR 0003's "editing an agent never disturbs an existing chat."
- **A backend endpoint that owns continuous routing.** Rejected. The client already owns conversation ids;
  a deterministic id is the minimal change. It is not a security boundary either way — the grain key is
  `{userId}/{conversationId}`, so a hand-crafted client can only ever affect its own conversations.

## Consequences
- **Continuous mode makes the 2 MB session cap a designed-in failure mode.** A continuous agent funnels
  every chat into ONE grain forever, and that grain re-serializes its whole session into a single
  `agent-state` Cosmos item every turn, hard-capped at 2 MB (ADR 0004; see the "an agent tool's RETURN value
  is persisted" gotcha). Per-chat mode bounds each conversation; continuous grows without bound (text + tool
  results + image refs + RAG excerpts, all replayed every turn) and will eventually fail `WriteStateAsync`
  *after* the model has answered — bricking the conversation, silently, in prod. **The escape hatch is
  delete-to-reset:** `DELETE /api/conversations/agent-{agentId}` purges the grain, and the next start
  recreates it fresh at the same id. ~~No code mitigation is in scope beyond documenting this.~~
  **Amended by [ADR 0068](0068-prune-continuous-agent-history-to-a-24-hour-window.md) (2026-09-05) — that
  last sentence is the one thing 0068 reverses.** A continuous chat's history is now pruned to a fixed
  24-hour window on every persisted turn. Everything else above stays true, delete-to-reset included:
  a window bounds **age**, not **size**, so a busy agent or one oversized tool return still bricks
  inside the day. The flag stays client-side — 0068 detects continuity from the `agent-` id shape
  precisely so this decision needs no reversal.
- **Continuous mode disables ADR 0003's only escape hatch for persona edits.** Binding is immutable once set,
  and ADR 0003's remedy for a wanted persona edit is "start a new chat from the updated agent." For a
  continuous agent that is impossible — every start collapses onto the one already-bound grain — so editing a
  continuous agent's system prompt or model has **zero effect** on its ongoing conversation until it is
  deleted. Reading the flag live (not snapshotting it) does not itself conflict with ADR 0003, but this
  consequence is real and delete-to-reset is again the only remedy.
- **Routing stays client-side.** Backend enforcement of continuity is out of scope, consistent with the
  client already owning conversation ids and the per-user grain key meaning it is not a security boundary.
- **Default is unchanged.** `Continuous` defaults to `false`, so every existing and new agent stays per-chat
  until the box is ticked.
