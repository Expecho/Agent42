---
id: 0003
date: 2026-07-07
status: accepted
tags: [agents, grains, persona]
amended_by: [0082, 0123]
---

# Snapshot an agent's persona into the conversation at bind time

> **Amended by [ADR 0134](0134-incognito-agent-chats.md) (2026-11-16): an incognito bind is in-memory only.** It is never stored, so after a deactivation the next turn rebinds from the agent's *current* definition: a deliberate exception to "first persona wins", which holds only while the activation lives.

> **Amended by [ADR 0082](0082-seeded-research-agent-loop.md) (2026-09-22): the agent's `Kind` is snapshotted too.** A research-kind agent changes how
> the model runs, so `Kind` is persona and joins the name, prompt, model and tools in `AgentBinding` — editing or
> deleting the source agent never turns an existing research chat into an ordinary one, or back.

## Context
Stored agents (user-defined personas: name, description, model, system prompt) are
first-class and editable/deletable, but a chat started from an agent must stay stable
for its whole life. If a conversation referenced its agent *live*, editing an agent
would retroactively change past chats and deleting one would break — or 500 — a
resumed turn. Agents live in a per-user `IAgentRegistryGrain`; conversations are
separate per-conversation `AgentGrain`s (ADR 0001).

## Decision
When a chat is started from an agent, the persona (agent id, name, system prompt,
model) is **snapshotted into the conversation grain's own state** at bind time
(`AgentGrain.BindAgentAsync`, pinned to `AgentGrainState.AgentId/AgentName/SystemPrompt`
+ `DeploymentName`). Binding is idempotent — the first persona is immutable for the
life of the conversation. The conversation never re-reads the registry after binding.

## Alternatives considered
- **Live reference (store only the agent id, resolve the definition each turn)** — an
  edit would silently rewrite the persona of every past chat, and a delete would make a
  resumed turn throw (the definition is gone). Rejected: a chat must be self-contained.

## Consequences
- Editing or deleting an agent never disturbs an existing chat; each conversation
  carries its own persona snapshot. Verified live: after an agent is edited then
  deleted, its earlier chat still replies with the *original* pinned persona.
- The backend chat handler null-guards a missing definition (deleted agent) and skips
  re-binding, so a resumed agent chat can never 500 (regression test AC7).
- Additive to ADR 0001, not a supersession: a new registry grain + persona fields on
  the existing conversation grain. The registry rides the shared `agent-state` storage
  provider (Orleans partitions by grain type), so no new Cosmos container is introduced.
- A persona edit that a user *wants* reflected requires starting a new chat from the
  updated agent — by design.
