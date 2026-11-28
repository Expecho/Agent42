---
id: 0153
date: 2026-11-28
status: accepted
tags: [conversations, streaming, resilience, pwa, replay, multi-replica, storage]
supersedes: []
extends: [0097]
amends: [0046]
amended_by: []
---

# Resumable chat turns: a detached pump, a shared replay log, rejoin from a dead socket, an F5 or another device

## Context
A chat turn was the HTTP response of its own `POST`. A lost socket (a frozen PWA, an F5) lost the live view, and the client
could only poll the history for about 30 seconds (ADR 0046). A research turn, or a second device, could not rejoin at all.
The user also wants scale-out past one replica to stay possible (`.agentic/specs/plan-orleans-clustering.md`), so the feature
must be correct with N replicas by construction, not by the accident of one process.

## Decision
A **`ChatTurnPump`** drains `IAgentGrain.ChatAsync` on a detached task, assigns each event a gapless `seq` from 1, and records it.
The `POST` and the reattach `GET` are only **readers**.

- **Dual path, one interface (D2).** The originating request reads the pump's in-process `TurnFeed` (zero added latency, and live
  streaming never depends on the store). Everyone else (a second device, another replica, a feed past its grace) reads the
  **store** through `StoreReader`. The store is always written, so deleting the feed changes latency, never correctness.
- **Store (D1).** Container `chat-turns` (Blob), the local volume when self-hosting, memory in DevHost: per conversation an
  append-blob log of raw NDJSON events, a small meta blob and an `active.json` hint. The port is `IChatTurnBlobs`; all logic is
  in `ChatTurnStore` (the `FleetStore` shape). Incognito turns use an in-memory twin whatever `Storage:Provider` is (D6).
- **Raw events stored, coalescing at replay only (D3).** A client's resume point is the last `seq` it saw; coalescing on write
  would replay text it already shows.
- **One writer per turn, liveness at read time (D4, D15).** The pump's writer loop alone writes batches, the heartbeat, the flags
  and the terminal status, in sequence, so a late heartbeat cannot overwrite `completed`. A reader only EVALUATES a stale
  heartbeat as `interrupted` and never writes it back (it would corrupt a live turn on another replica). Host stopping marks
  `interrupted` at once; only a crash waits out `StaleAfter` (45 s).
- **Appends are conditional and idempotent (D12);** a delete fences the pump through the store (D11): an append to a **missing**
  log is refused at ANY offset and never creates it (creation is a separate port call used only by `StartAsync`), and a meta
  update is refused the same way. The originating stream is unaffected.
- **The feed is a bounded ring (D14);** a reader behind it continues from the store.
- **Wire (D5).** NDJSON stays. `seq` is a trailing nullable field on `AgentStreamEvent` (omitted when null, so Android's golden
  file is unchanged); the turn id travels in `X-Turn-Id`. Routes: `GET /api/chat/{id}/turn`,
  `GET /api/chat/{id}/turns/{turnId}/stream?after=&ephemeral=`, with `X-Turn-Replay-Through`.
- **A passive attach never runs client tools (D13).** Opening a conversation on another device is view-only. Only the page that
  sent the turn reattaches with execution, and only for events above `X-Turn-Replay-Through`.
- **Retention is one provider-neutral sweeper (D10)** (`ChatTurnSweeper`, hourly, 24 h), also over the in-memory incognito twin.
- **Kill switch `ChatTurns:Enabled` (D7).** Off, the endpoint drains the grain straight into the response as before.
- **ADR 0046's client recovery stays as the fallback (D9):** a 404, an `interrupted` or `failed` turn, a truncated log, an
  incognito turn on another replica, or the kill switch all end in the old history poll. Out of scope: Android, the fleet analyzer
  chat, the chat-app gateway and the MCP agent surface, which still drain `ChatAsync` directly.

## Alternatives considered
- **A grain-owned pump.** `ChatAsync` is pulled by its caller under a per-pull `TurnContext.RunInAsync` (ADR 0106), so the grain
  cannot drive its own stream without a rewrite, and a grain-held buffer meets the 2 MB state cap (ADR 0004).
- **Orleans streams.** No durable provider exists for Cosmos or Postgres, and self-host has no queue.
- **Redis or pub/sub.** New infrastructure; self-host is Postgres and a volume only.
- **A store-only design.** A Blob outage would stop every chat from streaming.
- **A Bicep lifecycle rule for retention.** Azure-only, and it cannot reach incognito memory or the local volume.

## Consequences
- A reload mid-reply, a frozen PWA and a second device all rejoin the turn; stop works from either page.
- The turn log holds conversation text for up to 24 h (deleted with the conversation, swept after retention, never to a third
  party: ADR 0013 is not extended).
- The grain's history does not hold a turn in flight, not even its user message (spike S1), so a passive attach shows only the
  assistant bubble and repaints from history when the turn ends.
- Known limits: an incognito turn is same-replica only; a confirm pending while only a passive device is attached stays
  unanswered until the originating page returns; a turn whose owning replica died ends `interrupted` and is not re-driven (that
  would run tool calls twice); a store that fails makes OTHER devices see the turn go stale while the owner's page is fine.
- This adds no new single-replica blocker, and does not lift rule 12.

## Open (post-deploy, the user runs or confirms)
- Reload mid-reply on the real Container App; the feel of Azure append-blob latency at the 250 ms flush.
- Spike S2 was not measured (no Azurite): `AzureChatTurnBlobs.GetRangeAsync` treats a 416 as "no growth"; confirm on the real
  account, as well as the conditional-append behaviour.
- A revision rollover mid-turn lands on `interrupted` and the client falls back cleanly.
- The `chat-turns` container exists after the deploy (the Bicep deploy runs in CI).
- e2e does not cover a forced socket failure mid-confirm (the replay-through gate is unit and source-text pinned only).
