---
id: 0140
date: 2026-11-23
status: accepted
tags: [conversations, retention, agents, orleans, reminders, search, no-migration]
supersedes: []
extends: [0068, 0003]
amends: [0068]
---

# A stored agent carries its own message-retention setting, enforced by an idle sweep

> **Amends [ADR 0068](0068-prune-continuous-agent-history-to-a-24-hour-window.md)** — it fixed the window at 24 h,
> "not per-agent, not relitigable", and scoped it to continuous chats. Both calls are reversed here by a deliberate user
> decision. Everything else in 0068 stands: whole-turn prefix pruning, the stamping clamp, the search reconcile, the
> hard-delete exceptions and the 2 MB residual.

## Context

The user wants control over how long an agent's conversations are kept, per agent, without having to use the continuous
mode. ADR 0068 deleted messages older than a fixed 24 h, only for continuous chats, and only on the next persisted turn,
so an idle chat kept its text indefinitely.

## Decision

1. **A per-agent preset**: `AgentDefinition.RetentionMinutes` (`int?`; never / 1 h / 24 h / 7 d / 30 d, stored as minutes,
   `null` = unset).
2. **Snapshot at bind time, new chats only** (ADR 0003): it travels `AgentDefinition → AgentBinding → AgentGrainState`,
   and a fork carries it. Editing the agent leaves existing chats alone; an ongoing continuous chat keeps the value it was
   first bound with until it is deleted.
3. **One field for both modes.** Unset keeps today's behaviour exactly: a continuous chat prunes to 24 h on the next
   persisted turn, with no sweep and no deletion of the chat; a per-chat agent chat keeps everything. A continuous agent can
   pick another duration but **cannot pick "never"** (brick protection, ADR 0016/0068). Any explicit value gets the sweep.
4. **Effective window**: `state.RetentionMinutes` if set, else a conversation id starting `agent-` or the MCP prefix →
   24 h, else none. MCP conversations never carry a value (`AgentMcpRunner` does not pass one). Stamping `createdAt` is
   gated on "this chat has an effective window", so a per-chat chat without the setting stays unstamped.
5. **An idle sweep**: a per-conversation Orleans reminder (reserved name `__retention`, period `Clamp(W/4, 15 min, 6 h)`)
   prunes expired leading turns while nobody is in the chat. When the newest message is itself expired, or the session is
   empty, the sweep deletes the **whole chat** with the effect of the Delete button (session, search index, documents,
   reminders, conversation-index entry, share link) and, for a chat-app conversation, resets the chat route.
6. **Never over a turn in flight**: a tick with `_runsInFlight > 0` skips before anything else (ADR 0106).
7. **Per-chat-only kinds** (research, copilot) are forced to `null` by the registry: a copilot transcript lives outside
   the session, so a session prune would not remove it, and the research fold interaction is unverified.

## Alternatives considered

- **A live lookup of the agent per turn.** The grain does not know its definition (ADR 0003 snapshots persona by design),
  and a lookup would change existing chats when an agent is edited.
- **A free-form duration.** A preset is explainable in one line of UI and bounds the reminder period.
- **Prune on the next turn only.** An idle chat would keep expired text, which is the case the user cares about.
- **Keep the newest turn on a sweep.** The ordinary prune keeps it so a live turn is never emptied; an idle chat whose
  newest message has expired has nothing left worth keeping, and keeping it would defeat the setting.
- **"Never" for continuous agents.** Reopens ADR 0016's brick.

## Consequences

- **A chat's lifetime ends at most (window + one sweep period) after its last activity**, for every bound chat. An empty
  bound chat is deleted at its first tick (≥ 15 min after bind, far beyond a normal first turn).
- **Reminder rows**: one per retention chat until it is deleted. Single replica stays load-bearing (rule 12).
- **Reserved reminder name**: `cancel_all_scheduled_prompts` skips it and `schedule_prompt` refuses it.
- **Dangling ids**: a browser tab left open on a swept chat sends its next turn into the purged id, which re-lists it
  persona-less under the new message's title. This equals deleting the chat from another tab today; the client protocol
  is not changed. A continuous chat reuses `agent-<id>` and hits the same path.
- **External runs are swept too**: chats started through the agent's API key (`/api/agent-run`) or its inbound email
  address bind the agent's value like a browser chat, so an expired run is deleted whole. Automation that reads results
  later must read them within the window.
- **Editor edge**: a continuous agent has no explicit "24 hours" option (it is the Default), so ticking Continuous on an
  agent saved with 24 hours, or reopening a continuous agent stored with 1440, shows Default and saves null: the same
  window, but no sweep for chats started afterwards.
- **A PUT that omits `retentionMinutes` clears it**, like `Continuous`; scripted PUT clients must send it.
- **A fork re-stamps** every copied message with "now" when it carries a retention value (the window starts at the fork).
- **Prune now runs on edit/regenerate** for a windowed per-chat chat, because it is a persist site.
- **Still survives a prune or sweep** (0068, restated): text already POSTed to Hindsight (no delete path, ADR 0056), the
  `/logs` ring until it rolls (ADR 0054), orphaned attachment blob bytes, and the client IndexedDB mirror (ADR 0032).
- **Residual from 0068, now more exposed**: a failed search-index delete that is never retried leaves searchable orphans
  permanently. The sweep makes pruning routine for per-chat agents, so the residual is restated here, not closed.
- Out of scope: per-conversation or user-level retention, applying a changed value to existing chats, retention for
  research/copilot/plain/MCP/fleet chats, deleting blob/memory/log content, a UI showing what was pruned.
