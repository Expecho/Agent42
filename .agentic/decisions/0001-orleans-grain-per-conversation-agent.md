---
id: 0001
date: 2026-07-05
status: accepted
tags: [architecture, orleans, agent-framework]
amended_by: [0074, 0127]
---

# Host each conversation as an Orleans grain wrapping an Agent Framework agent

> **Amended by [ADR 0127](0127-self-hosting-local-storage-provider.md) (2026-11-14): grain state and reminders can live in Postgres (`Storage:Provider = local`).** Cosmos persistence below is the `azure` provider.

> **Amended by [ADR 0074](0074-identity-alias-for-a-second-entra-account.md) (2026-09-13).**
> The Consequences below say grain keys are `{userId}/{conversationId}`, "so one user can never
> address another's conversation". That still holds of **userIds** — but two Entra principals can
> now map to one userId through the `Identity:Aliases` map, so the isolation boundary is the userId
> **after** aliasing, and an alias entry is an authorization grant.

## Context
Agent42 needs stateful, addressable per-conversation chat agents on Azure: durable
conversation history, streaming replies, agent tools that can schedule durable
reminders, and single-writer semantics per conversation (founding commit 4acb6dd,
2026-07-05).

## Decision
One `AgentGrain` per (user, conversation) key wraps a Microsoft Agent Framework
`ChatClientAgent`. The serialized `AgentSession` is grain state (Cosmos in prod),
reminders use Orleans' durable reminder service, and tools bind to the grain
instance so they can reach Orleans directly.

## Alternatives considered
- **Stateless API + history table** — loses actor single-writer semantics; durable
  per-conversation scheduling would need a separate job system.
- **Azure AI Foundry hosted agents/threads** — session state lives service-side;
  local tools (Orleans reminders, grain state) can't bind to it, and offline dev
  (DevHost with a canned client) wouldn't work.

## Consequences
- Streaming falls out naturally (`IAsyncEnumerable<AgentStreamEvent>` from grain to HTTP NDJSON).
- The backend co-hosts an Orleans silo; without Aspire-provided clustering config it
  falls back to localhost clustering, so the Container App must stay single-replica
  until real clustering is configured.
- Grain keys are `{userId}/{conversationId}`, so one user can never address
  another's conversation.
