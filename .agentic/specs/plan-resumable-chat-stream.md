# Plan — Resumable chat stream (rejoin a turn in flight after backgrounding, F5 or another device)

planned-against: main @ f43a9a3

## Overview

**Goal:** a chat turn that is still running can be rejoined. The browser keeps the id of the last event it saw; after a
dead socket (PWA frozen, F5) or on a second device it reattaches, the server replays what was missed, then continues
live. Today the stream is the HTTP response of the `POST`, so a lost socket loses the live view and the client can only
poll the history for ~30 s (ADR 0046); a research turn or a second device cannot rejoin at all.

**Why now / constraint:** the user wants scale-out to more than one replica to stay possible
(`.agentic/specs/plan-orleans-clustering.md` is the unexecuted plan that lifts `min = max = 1`). So this feature must be
**correct with N replicas by construction**: the replay buffer is a shared store behind a port, never in-process state
that correctness depends on. An in-process fast path is allowed only as an optimisation that can be deleted without
changing behaviour.

## Decisions taken while planning (flag at approval — the user has not been interviewed on these)

- **D1 — The turn is pumped in the Backend, not in the grain.** A `ChatTurnPump` drains `IAgentGrain.ChatAsync` on a
  detached task and records every event with a per-turn sequence number. The `POST` and the reattach `GET` are both just
  readers. Rejected: (a) grain-owned pump — `ChatAsync` is pulled by its caller under per-pull `TurnContext.RunInAsync`
  (ADR 0106), so the grain cannot drive its own stream without a rewrite; a grain-held buffer also meets the 2 MB state
  cap (ADR 0004). (b) Orleans streams — no durable provider exists for Cosmos/Postgres, and self-host (ADR 0127) has
  neither a queue nor Event Hubs. (c) Redis/pub-sub — new infrastructure; self-host is Postgres + a volume only.
- **D2 — Dual path, one interface.** The originating request reads the pump's in-process **feed** (zero added latency,
  and live streaming does not depend on the store being up); every other reader (second device, another replica, after
  the owner's feed is evicted) reads the **store**. Both implement `ITurnEventReader`. The store is always written, so
  deleting the feed changes latency and resilience, never correctness. A store-only design was rejected: a Blob outage
  would stop every chat from streaming.
- **D3 — Raw events are stored; coalescing happens at replay time only.** The client's resume point is the last `seq` it
  *saw*. If the store coalesced text deltas on write (as the fleet log does, keeping the merged event's last seq), a
  client that had seen seq 10 of a merged 8–12 event would be replayed text it already shows. Coalescing the replay
  response (events with `seq > after`) is safe and keeps the replay small.
- **D4 — Liveness is a heartbeat in the turn's meta blob, evaluated at read time and never written by a reader.**
  `{ownerReplicaId, lastHeartbeatUtc}`; a `running` turn whose heartbeat is older than `StaleAfter` is *reported* as
  `interrupted`. **One writer per turn:** the heartbeat, the flushes and the terminal status are all written by the
  pump's single writer loop, and completion stops and awaits the heartbeat before writing the terminal status, so a late
  heartbeat can never overwrite `completed` (review finding 2). Terminal states: `completed` (a stopped turn is a
  completed turn whose `Done` has `stopped`), `failed` (the grain's `ChatAsync` threw mid-stream), `interrupted`
  (host stopping, or stale), plus the flags `degraded` and `truncated`. This is the owner-record shape the clustering plan already specifies (its R1: a reader rewriting state
  to Interrupted is silent corruption at 2 replicas). `IReplicaIdentity` is introduced here minimally; if the
  clustering plan's step 8 has landed first, reuse its type instead.
- **D5 — Wire format stays NDJSON.** `seq` is a new trailing nullable field on `AgentStreamEvent` (omitted when null by
  `WhenWritingNull`, tolerated by Android's `ignoreUnknownKeys`); the turn id travels in an `X-Turn-Id` response
  header. No new event type, so the Android golden file and `AndroidStreamContractTests` do not change. Reattach also
  answers NDJSON, so the browser reuses one reader/dispatcher.
- **D6 — Incognito (ephemeral) turns use the in-memory store regardless of `Storage:Provider`** (ADR 0034: no trace). A
  reattach for an incognito turn is therefore same-replica only; elsewhere it answers 404 and the client falls back to
  today's history recovery. Recorded as a known limitation, not hidden. The in-memory store is swept by the same sweeper
  as the durable one (D10) so incognito text never sits in memory past `Retention`. The `ephemeral` query flag is
  client-supplied and only chooses *which* store to look in: a mismatch yields 404 and can never leak, because both
  lookups are keyed by the authenticated user. The client never calls `GET /turn` on open for an incognito
  conversation (it is not listed anywhere).
- **D7 — A kill switch `ChatTurns:Enabled` (default true).** Off = today's behaviour (the endpoint drains the grain
  stream straight into the response). Kept because this sits in front of every chat turn.
- **D8 — Out of scope clients:** Android (it tolerates the new field/header, but does not reattach), the fleet analyzer
  chat, the chat-app gateway and the MCP agent surface (they drain `ChatAsync` directly and keep doing so).
- **D9 — ADR 0046's client recovery stays** as the fallback when reattach is impossible (log truncated/expired,
  `interrupted`, 404, incognito on another replica, kill switch). It is demoted, not deleted.

- **D10 — Retention is one provider-neutral mechanism: a `ChatTurnSweeper` hosted service** (hourly; deletes turn blobs
  older than `ChatTurnOptions.Retention`, default 24 h, from the durable store and the in-memory twin). It replaces the
  Bicep lifecycle rule the first draft proposed: one mechanism that also covers self-host (a lifecycle rule is Azure-only
  and `infra/` has none today), the grain-side retention sweep that cannot reach the Backend store, and incognito
  memory. Concurrent sweeps on several replicas are idempotent deletes. Next-turn cleanup deletes only *terminal* turns
  of that conversation, never a running one.
- **D11 — Delete fences the pump through the store, not through process state.** The conversation delete purges the
  conversation's turn blobs. `AppendAsync` only creates the log at offset 0 (in `StartAsync`); an append at a later
  expected offset to a **missing** blob is a fence: the pump stops persisting, marks `degraded`, and never recreates
  it. The originating stream (feed) is unaffected. This works across replicas with no extra fence blob.
- **D12 — Appends are conditional and idempotent.** Azure append blobs are not idempotent on retry (a timed-out
  `AppendBlock` may have landed). `IChatTurnBlobs.AppendAsync(key, bytes, expectedOffset)` uses `AppendPosition`
  (file and memory twins compare the current length). On a failed or ambiguous append the pump re-reads the length: if it
  equals `expectedOffset + size` the batch landed, otherwise it retries (bounded by `MaxAppendAttempts`), after which
  the turn is marked `degraded` and persistence stops (the feed keeps going). `StoreReader` also dedupes by `seq`.
- **D13 — A passive attach never runs client tools.** Opening a conversation on another device (step 10) is view-only:
  tool and elicitation events are rendered but never executed. Only the page that sent the turn (step 9) reattaches
  with execution, gated by `X-Turn-Replay-Through`. A confirm that is pending while only a passive device is attached
  stays unanswered until the originating page returns; accepted for now, listed as a follow-up. This removes the
  double prompt and the double side effect (location, browser context) on a second device.
- **D14 — The feed is a bounded ring.** `FeedMaxBytes` (default 1 MB) of the newest events. A `FeedReader` whose `after`
  is older than the ring's start falls back to the `StoreReader`; if the store is `truncated` as well, the reader ends
  with status `truncated` and the client uses history recovery. Memory per running turn is therefore bounded.
- **D15 — Reporting honesty over a sick store.** If the store fails, the heartbeat fails with it, so *other* devices
  will report `interrupted` after `StaleAfter` while the owner's feed is fine; the owner's own page is unaffected and
  the other device falls back to history polling, which still works. `StaleAfter` defaults to 45 s (3x the heartbeat plus
  a blob-call allowance). After a **graceful** rollover the pump writes `interrupted` at once on host stopping; only a
  crash waits out `StaleAfter`.

## Affected areas (verified against the tree at f43a9a3)

Backend / agents:
- `src/Agent42.Backend/Conversations/AgentApi.Conversations.cs` — `ChatAsync` (the `await foreach` at ~220–230 moves
  behind the pump; the pre-stream persona/bind/replace logic stays). New `AgentApi.ChatTurns.cs` beside it (must be
  called from `MapAgentApi`; `AgentApiReachabilityTests` + `tests/Agent42.Tests/AgentApiRouteTable.golden.txt`).
- `src/Agent42.Agents/Conversations/AgentStreamEvent.cs` — trailing `[Id(24)] long? Seq`. `ChatStreamJson` unchanged.
- New port `IChatTurnBlobs` + in-memory twin in `src/Agent42.Agents/` (exemplar: `Fleet/IFleetBlobs.cs`,
  `Fleet/InMemoryFleetBlobs.cs`); logic class `ChatTurnStore` over it (exemplar: `Fleet/FleetStore.cs`, "adapters hold no
  logic").
- Adapters: `src/Agent42.Backend/Conversations/AzureChatTurnBlobs.cs` (exemplar `Fleet/AzureFleetBlobs.cs`),
  `src/Agent42.Backend/Storage/FileChatTurnBlobs.cs` (exemplar `Storage/FileFleetBlobs.cs` over `LocalFileStore`).
  Registration: new `ChatTurnRegistration` called from `Program.cs` and `tools/Agent42.DevHost/Program.cs`; the local
  adapter registered in `Storage/LocalStorageRegistration.cs` `AddFileStores`, the Azure one by `TryAddSingleton`
  (the `AddAgentFleet` precedent: `TryAdd` so the local registration wins; ADR 0105 port-per-host rule).
- Pump/feed/tail/registry: new files under `src/Agent42.Backend/Conversations/` (flat namespace, ADR 0039).
- Infra: `infra/modules/storage.bicep` (new container `chat-turns` — a fixed literal, same as `agent-fleet`),
  `infra/main.bicep` if params are needed. No lifecycle policy (D10: the sweeper owns retention). No RBAC change: the
  data role is account-scoped (rule 5 not engaged).
- Conversation delete: `DELETE /api/conversations/{id}` in `AgentApi.Conversations.cs` must also purge the conversation's
  turn blobs (D11). The grain-side retention sweep (`AgentGrain.RetentionSweep.cs`, `DeleteExpiredChatAsync` calling
  `PurgeAsync`) cannot reach the Backend store; the sweeper (D10) covers what it leaves behind.
- Identity propagation precedent for a detached run: `Fleet/AgentApi.Fleet.cs`, `Fleet/FleetTaskService.Run.cs`,
  `ChatGateway/ChatTurnPipeline.Turn.cs` (they read `UserTelemetryContext` before leaving the request and
  `RequestContext.Set` it inside the background task). Gotcha: "User identity reaches downstream/grain spans via Orleans
  RequestContext, never Activity.Baggage"; guard test `RequestContextPropagationTests`.
- Config: new `ChatTurnOptions` (exemplar `Fleet/FleetOptions.cs`, bound the same way).

Frontend (`src/Agent42.Backend/wwwroot/`):
- `js/send.js` — `send()` is ~269 lines (166–434); the reader loop + event dispatch (260–350) is extracted into a new
  `js/chatstream.js` used by both `send()` and reattach. `recoverPersistedReply` (89–134) is kept as the fallback.
- `js/conversations.js` `selectConversation` (30–94): add a parallel `GET /turn`; today nothing knows a turn is running.
- `js/clienttools.js` — `handled` dedupe only covers in-flight calls (51–58), so replayed events must never run tools.
- `sw.js` — `CACHE` is `agent42-shell-v94` (line 8) and the new module must join `SHELL` (ADR 0041). Pinned in
  `tests/Agent42.Tests/{AgentRetentionAssetsTests,ClientToolsAssetsTests,PwaAssetsTests,ResearchAssetsTests,SearchAssetsTests}.cs`
  and parsed by `tests/e2e/devhost.spec.mjs`.

## Context & constraints (what the executor cannot cheaply re-derive)

- **The turn already survives the client leaving**, implicitly: Kestrel discards writes to an aborted socket, so the
  endpoint's `await foreach` keeps draining (ADR 0046; `ChatStreamDisconnectTests`). The pump makes this explicit and
  must keep it: the pump's lifetime is the host's `ApplicationStopping`, **never `HttpContext.RequestAborted`**. Honouring
  `RequestAborted` is the "obvious improvement" ADR 0046 warns about.
- **Mid-turn history is unverified.** Scout finding: the grain writes the durable session only after the last model event
  and before `Done` (`ChatTurnAsync`, then `PersistAndNotifyChatTurnAsync`, then `WriteSessionAndIndexAsync`);
  `GetHistoryAsync` projects the in-memory `_session` (`ProjectHistory`) and whether that already holds the user message
  mid-run is **not established** (re-read those methods; line numbers drift). Step 0 (S1) settles it;
  the client's attach rendering depends on the answer. Research-kind turns hide the whole draft from history until the
  end (ADR 0082, `AgentGrain.Research.cs:181`).
- **No stream position exists today** — events carry no id/seq (`AgentStreamEvent`, `ChatStreamJson.Options` =
  `JsonSerializerOptions.Web` + `WhenWritingNull` + camelCase enums).
- **The grain accepts a second concurrent `ChatAsync`** (scout finding: `ChatCoreAsync` only increments and decrements
  `_runsInFlight`; only edit/regenerate/fork read it and answer `HistoryEditOutcome.Busy`; the chat-app gateway queues per
  conversation itself in `ChatTurnPipeline`). So `active.json` is a **hint, last writer wins**, with no ETag and no claim
  of atomicity: two concurrent turns each keep their own readable log (cleanup never deletes a running turn, D10); the
  pointer simply names the more recent start. A test pins that starting B leaves a running A intact.
- **Client tools (ADR 0089).** The broker is grain-side and cluster-wide; `pending-tool-calls` is the authority for what
  is still waiting. Replayed `toolCallStart{clientTool}` / `mcpElicitation` events must not execute: the attach response
  carries `X-Turn-Replay-Through: <last seq at attach time>` and the client runs tool events only above it, then calls
  `resumePendingClientTools` once. A second device attached to the same turn may show a confirm too; the first answer
  wins and the late post gets a swallowed 404 (`clienttools.js:71`).
- **Stop (ADR 0078)** is a grain call (`POST /api/chat/{id}/stop`), already cluster-wide; it works from an attached
  second device unchanged. The `Done` event with `stopped` flows through the log like any other.
- **Single replica stays load-bearing today** (CLAUDE.md rule 12) and this plan does not lift it. The in-process
  pieces (`ChatTurnRegistry`, feeds, signals) are the optimisation layer; the store is the truth. Do not add a gotcha
  claiming multi-replica is *enabled* — only that this feature adds no new blocker.
- **Log failure must never fail the turn.** The pump keeps draining the grain if an append throws (log + mark the turn's
  meta `degraded`); readers on the store then get an end-of-stream and the client falls back to history recovery.
- **Egress (rule 13):** no third party; turn logs live in the app's own storage account and hold conversation content,
  so they are deleted with the conversation (purge hook, D11), swept after `Retention` (D10, every provider, incognito
  memory included) and cleaned when the next turn of that conversation starts. Never log secrets.
- **Detached pump identity (review finding 1).** The pump runs after the request handler returns. It must (a) read
  `UserTelemetryContext` and the application dimension *before* detaching and `RequestContext.Set` them inside the pump
  task (the fleet pattern named above), and (b) never touch `HttpContext` after the handler returns. Losing this silently
  unattributes usage (ADR 0010/0031) and breaks non-owner span redaction (ADR 0121).
- **Gate shapes:** S3776 15 / S138 80 lines / S107 11 params are build errors — the pump, tail and store are split into
  small single-purpose types for that reason (see Steps). Don't game the metric.
- **Reader state at completion.** A page that reattaches after the turn already finished replays through `Done` and
  renders that one bubble; it must not additionally trigger a history re-render of the same turn (e2e asserts exactly one
  bubble). `GET /turn` reporting `completed` means "ignore, history has it".
- **Prior art to read before touching code:** ADR 0046 (+ amendments 0080/0082/0089), 0097 (fleet replay-then-live,
  `FleetEventLog.ReplayAsync`, `FleetTaskService.SubscribeAsync` subscribes **before** reading the store), 0089, 0082,
  0106, 0127, 0114; gotchas entries for TurnContext and the replica rules; plan-orleans-clustering.md (R1, owner record).
- The fleet log's byte-cap counter is **not** seeded from the stored blob after a restart (scout finding,
  `FleetEventLog.cs:56,122-125`). Do not copy that; the chat log has one writer per turn and a fresh blob per turn, so
  seed it from `0` deliberately and say so in a comment.

## Design (the shape the steps build)

```
POST /api/chat/{id} ──pre-checks──► ChatTurnRunner.StartAsync ─► ChatTurnPump (detached task)
                                         │ sets X-Turn-Id          │ drain agent.ChatAsync
                                         ▼                         │ assign seq, push to Feed (in-proc)
                    ChatTurnTail.Open(key, turnId, after=0)        │ every FlushInterval: ChatTurnStore.AppendAsync
                        ├─ local feed present?  ─► FeedReader      │ every HeartbeatInterval: store meta heartbeat
                        └─ otherwise            ─► StoreReader ◄───┘ on end: final flush + meta status
GET /api/chat/{id}/turns/{turnId}/stream?after=N  ──► same ChatTurnTail (replay-through header, coalesced replay)
GET /api/chat/{id}/turn                           ──► active turn meta (status evaluated for staleness) or 204
```

Store layout in container `chat-turns` (append blob + small JSON blobs, `{ownerKey}` = the existing
`AgentKey(userId, conversationId)` escaped like the fleet's keys): `{ownerKey}/active.json` (pointer),
`{ownerKey}/{turnId}.meta.json`, `{ownerKey}/{turnId}.ndjson`. The authenticated user id always comes from
`GetUserId()`, so a request can only reach its own keys.

Types (each small, each unit-testable without Azure):
- `IChatTurnBlobs` — `GetAsync(key)`, `GetRangeAsync(key, offset)` (returns bytes + new length; empty when no growth),
  `PutAsync`, `AppendAsync(key, bytes, expectedOffset)` (conditional, D12; creates only at offset 0),
  `DeleteAsync`, `ListAsync(prefix)` returning key, size **and last-modified** (the sweeper needs the age).
  Adapters hold no logic.
- `ChatTurnStore` — all log logic: start, append batch (cap `MaxLogBytes`, then `truncated`), heartbeat, complete,
  read-from-offset, active pointer, `StatusOf(meta, now)` (stale → `interrupted`), purge. Needs `TimeProvider`.
- `ChatTurnPump` — one turn: drain, seq, feed push, hand events to the writer, completion. Takes narrow collaborators
  (`ChatTurnStore`, `TurnFeed`, options, `TimeProvider`, logger) — **not** the grain factory (the runner passes the
  `IAsyncEnumerable<AgentStreamEvent>` in), keeping the parameter count well under S107. Decomposed up front so no
  method nears S3776/S138: `ChatTurnWriter` (the single writer loop: batch buffer, interval/size flush, conditional
  append with the D12 retry policy, heartbeat, terminal status), `EventBatch` (pure buffer), and the pump itself reduced
  to drain, seq, feed push and error classification (`completed`, `failed`, `interrupted`).
- `TurnFeed` / `ChatTurnRegistry` — in-proc append-only list + wake signal per running turn, evicted `FeedGrace` after
  completion; registry is a `ConcurrentDictionary` keyed by turn id. **Optimisation only** (D2).
- `ITurnEventReader` with `FeedReader` and `StoreReader` (`IAsyncEnumerable<LoggedEvent> ReadAsync(after, ct)`);
  `StoreReader` follows the store by byte offset (replay all once, then range-read from the offset), woken by the local
  signal and otherwise polling every `PollInterval`. Subscribes before reading, like `FleetTaskService.SubscribeAsync`.
- `ChatTurnTail` — picks the reader and yields lines; `ReplayCoalescer` (pure, static: merges consecutive `TextDelta`
  events above `after`, keeping the last seq) and `NdjsonStreamWriter` (the keep-alive race loop, exemplar
  `WriteEventStreamAsync` in `Fleet/AgentApi.Fleet.cs`) are separate types. `ChatTurnRunner` — the endpoint-facing
  facade (start + open). `ChatTurnSweeper` — the hosted service of D10.
- `IReplicaIdentity` — `ReplicaId` from `CONTAINER_APP_REPLICA_NAME` (unverified, clustering-plan spike S1) with a
  generated fallback.

Defaults (all in `ChatTurnOptions`, never literals in code): `FlushInterval` 250 ms, `PollInterval` 500 ms,
`HeartbeatInterval` 10 s, `StaleAfter` 45 s, `MaxLogBytes` 4 MB, `FeedMaxBytes` 1 MB, `FeedGrace` 60 s,
`MaxAppendAttempts` 3, `Retention` 24 h, `SweepInterval` 1 h, keep-alive 15 s (the fleet value).

## Steps

Order matters except where marked **[P]**. Every step writes its own tests (hard rule 3), runs the narrowest test first,
then `dotnet test Agent42.slnx` before the step is reported done.

0. **Spikes (use `spike-dotnet`; throwaway files outside the repo; record results in a "Spike results" section here).**
   - **S1:** with a test silo and `FakeChatClient.SlowStreamMarker`, does `GET /api/conversations/{id}/messages` mid-turn
     contain the user message? Does the grain's in-memory `_session` hold it before the first model event? Also a research
     turn. *Decides the client's attach rendering in step 10.*
   - **S2:** append-blob `Download` with a `Range` past the end / on an empty blob (Azurite if available): what does
     the SDK throw (416?) and can one call return "no growth" cheaply? *Decides `GetRangeAsync`'s shape.* If Azurite is
     unavailable, say so and move the measurement to the post-deploy checks.
1. **Wire: `Seq` on events.** Add `[property: Id(24)] long? Seq = null` (trailing, default null, rule 2) to
   `AgentStreamEvent`. Tests: JSON omission when null and presence when set via `ChatStreamJson.Options`; the existing
   `AndroidStreamContractTests` stays green **unchanged** (proves D5). No Cosmos-JSON-path test: `AgentStreamEvent` is a
   streamed return type, not persisted grain state (review finding 9); confirm that with a grep at execution time.
2. **Ports, options, store, identity.** `IChatTurnBlobs` + `InMemoryChatTurnBlobs`, `ChatTurnOptions`, `IReplicaIdentity`,
   `ChatTurnMeta`/status, `ChatTurnStore`. Tests (`ChatTurnStoreTests`): append/read-from-offset, conditional append
   (wrong offset rejected; a landed-but-timed-out batch is recognised, not duplicated), an append to a missing blob at
   offset > 0 is a fence, cap → `truncated`, stale heartbeat reads as `interrupted` and **is not written back**, purge,
   `active.json` is last-writer-wins and starting B leaves a running A intact.
3. **[P with 4–5] Adapters + registration + infra.** `AzureChatTurnBlobs`, `FileChatTurnBlobs`, `ChatTurnRegistration`
   (host-independent registrations inside it; blob port per host, ADR 0105), `chat-turns` container in `storage.bicep`,
   DevHost registration, `ChatTurnSweeper`. Tests: one adapter contract test run against the in-memory and file twins;
   `ChatTurnContainerNameTests` pins the container literal in code and Bicep (the `agent-fleet` precedent);
   `ChatTurnSweeperTests` (fake clock: deletes aged turns in both stores, keeps young and running ones); `az bicep build`.
4. **Pump, writer, feed, registry.** Carry `UserTelemetryContext` and the application dimension into the pump task
   (fleet pattern). Tests (`ChatTurnPumpTests`, `ChatTurnWriterTests`, fake clock): seq is gapless and monotonic;
   batches flush on interval/size; a throwing store does **not** stop the drain, retries per D12, then marks `degraded`;
   the heartbeat ticks without events and **cannot overwrite a terminal status** (the writer stops it first); a grain
   that throws mid-stream ends the turn `failed` and every reader sees the end; stopping the *host* marks `interrupted`
   at once; a request's cancellation token is never observed by the pump; **spans of a turn driven through the pump
   carry `user.id`** (extend or copy `RequestContextPropagationTests`); the feed ring drops oldest events past
   `FeedMaxBytes` and a reader behind the ring falls back to the store.
5. **Tail + readers.** `ITurnEventReader`, `FeedReader`, `StoreReader`, `ChatTurnTail` (replay-time coalescing,
   replay-through header value, keep-alive). Tests (`ChatTurnTailTests`): replay-then-live is exactly-once across the
   boundary; reconnect with `after=N` after coalesced replay shows no duplicated text; a store-only reader (no feed)
   yields the same sequence as the feed reader.
6. **Endpoints.** Re-point `ChatAsync` (pre-checks stay; `X-Turn-Id`; `ChatTurns:Enabled=false` → the old direct drain),
   add `GET /api/chat/{id}/turn` and `GET /api/chat/{id}/turns/{turnId}/stream?after=&ephemeral=` in
   `AgentApi.ChatTurns.cs`; update the route golden file and confirm `AgentApiReachabilityTests`. Easy Auth
   `excludedPaths` and the Entra public list are **not** touched (these routes are authenticated and use `GetUserId()`).
   Tests (`ChatTurnApiTests`, real-Kestrel host copied from `ChatStreamDisconnectTests`): reset mid-reply then reattach
   completes with every event once; the original disconnect guarantee still holds; another user gets 404 for the turn;
   incognito turn is not in the durable store; kill switch restores the old path.
7. **Purge on delete.** Call `ChatTurnStore.PurgeAsync(ownerKey)` from `DELETE /api/conversations/{id}`. Tests: delete →
   no turn blobs; **delete while a turn is running** → the pump does not recreate the blob (D11) and the originating
   stream still completes. Grep for any other path that deletes conversation content (delete-all, export-all-then-delete)
   and wire the same call; the grain-side retention sweep is covered by the sweeper (D10).
8. **[P, can go first] Frontend refactor, no behaviour change.** Extract the reader loop + dispatch from `send()` into
   `js/chatstream.js`; `send()` calls it. Add it to `SHELL`, bump `CACHE`, update the five pinned tests. Verify with the
   DevHost e2e "streams a reply" test plus a manual run; **no JS unit tests exist**, so add the e2e cases in step 11
   *before* relying on this refactor.
9. **Frontend reattach on a failed stream.** Track `turnId` (header) and `lastSeq` (per event); on a non-abort failure
   call reattach with backoff; dispatch into the same bubble; gate client-tool execution on `X-Turn-Replay-Through`;
   call `resumePendingClientTools` after each attach; fall back to `recoverPersistedReply` on 404, `interrupted`,
   `failed` or truncated. A reattach to an already-finished turn replays through `Done` and renders one bubble.
   Bump `CACHE`.
10. **Frontend attach on open.** `selectConversation` fetches `/turn` in the existing parallel batch; when `running`,
    enter the streaming state (stop button, composer lock), render per S1's answer, attach from `after=0` **passively
    (D13: no client tool or elicitation execution)**. `completed`, `failed` or `interrupted` → nothing special (history
    owns it). Never called for incognito. Bump `CACHE`.
11. **E2E (`tests/e2e/devhost.spec.mjs`).** First give DevHost what it lacks (verify in `tools/Agent42.DevHost`; the
    scout did not): a deterministic slow canned reply (a marker prompt, like `FakeChatClient.SlowStreamMarker`) and a canned
    turn that calls a client tool (`confirm_action`). Cases: (1) reload mid-reply shows the full reply, exactly one
    bubble, and keeps streaming; (2) a second page opening the conversation mid-reply attaches and ends with the same
    text, and **does not execute** a confirm that the first page also shows; (3) stop from the second page ends both;
    (4) a reattach after a forced socket failure does not re-run an already answered confirm; (5) reattach to a turn that
    finished while away renders one bubble.
12. **Docs and decisions.** `/decide` ADR **0153** (amends 0046 — add the header note and the "client recovery is now the
    fallback" line; extends 0097's replay shape; cross-links 0089/0082/0106/0127 and the clustering plan; records D1–D9
    and the rejected alternatives); `/gotcha` entries (store is truth, feed is an optimisation; replay-time coalescing
    only; `RequestAborted` must not reach the pump; replayed client tools must not run); CLAUDE.md feature-map row
    "Conversations" gets 0153; fix the `ChatStreamDisconnectTests` doc comment; `/sync-docs`.
13. **Post-deploy checks (the user runs/confirms; list them in the ADR as open):** reload mid-reply on the real Container
    App; Azure append-blob latency feel at `FlushInterval`; a revision rollover mid-turn lands on `interrupted` and the
    client falls back cleanly; confirm `chat-turns` container + lifecycle rule exist after the deploy (Bicep deploy runs in
    CI, not by the agent).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | An event carries `seq` on the wire only when set; Android contract unchanged | `Seq_IsOmittedWhenNull_AndAndroidGoldenUnchanged` | `tests/Agent42.Tests/ChatTurnWireTests.cs` (+ existing `AndroidStreamContractTests.cs`) |
| AC2 | A client that resets mid-reply can reattach and receives every event exactly once, in order | `Reattach_AfterReset_ReplaysThenContinuesExactlyOnce` | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC3 | The turn still completes and persists when the client never returns | existing `Chat_PersistsTheTurn_WhenTheConnectionIsResetMidReply` stays green | `tests/Agent42.Tests/ChatStreamDisconnectTests.cs` |
| AC4 | Resuming with `after=N` after a coalesced replay never duplicates text | `Replay_CoalescesOnlyAboveAfter_NoDuplicateText` | `tests/Agent42.Tests/ChatTurnTailTests.cs` |
| AC5 | A reader with no local feed (another replica: two stores/runners over one shared in-memory blob store) sees the same sequence as the feed reader | `StoreReader_EqualsFeedReader_ForTheSameTurn` | `tests/Agent42.Tests/ChatTurnMultiReplicaTests.cs` |
| AC6 | A running turn is never reported or written as `interrupted` while its owner heartbeats; a dead owner's is reported `interrupted` and nothing is written by the reader | `StatusOf_FreshHeartbeat_Running`, `StatusOf_StaleHeartbeat_Interrupted_NotWritten` | `tests/Agent42.Tests/ChatTurnStoreTests.cs`, `ChatTurnMultiReplicaTests.cs` |
| AC7 | A failing store never stops the grain drain or the originating stream; the turn is marked `degraded` | `StoreFailure_DoesNotStopDrain_OrLiveStream` | `tests/Agent42.Tests/ChatTurnPumpTests.cs` |
| AC8 | Host stopping marks the turn `interrupted`; request cancellation is not observed by the pump | `HostStopping_MarksInterrupted`, `RequestAbort_DoesNotCancelPump` | `tests/Agent42.Tests/ChatTurnPumpTests.cs` |
| AC9 | The log is capped; past the cap the turn is `truncated`, the live feed is unaffected | `LogCap_Truncates_FeedUnaffected` | `tests/Agent42.Tests/ChatTurnStoreTests.cs` |
| AC10 | Incognito turns never reach the durable store and are swept from memory; another user gets 404 | `Incognito_UsesMemoryStoreOnly`, `OtherUser_Gets404` | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC11 | Deleting a conversation purges its turn blobs | `DeleteConversation_PurgesTurnBlobs` | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC12 | `ChatTurns:Enabled=false` restores the old direct stream | `KillSwitch_StreamsDirectly` | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC13 | New routes are reachable and in the route table; container literal pinned in code and Bicep | `AgentApiReachabilityTests` + golden file; `ChatTurnContainerNameTests` | `tests/Agent42.Tests/` |
| AC14 | Replayed client-tool events do not execute; a passive second device never executes any | e2e cases (2) and (4) of step 11 | `tests/e2e/devhost.spec.mjs` |
| AC15 | F5 mid-reply shows the full reply once; a second page attaches; stop works from either; reattach after completion renders one bubble | e2e cases (1), (3), (5) of step 11 | `tests/e2e/devhost.spec.mjs` |
| AC17 | A turn driven through the detached pump attributes spans/usage to the user | `Pump_PropagatesUserIdentity_ToGrainSpans` | `tests/Agent42.Tests/ChatTurnPumpTests.cs` |
| AC18 | A late heartbeat never overwrites a terminal status; a throwing grain ends the turn `failed` for every reader | `Heartbeat_CannotOverwriteTerminal`, `GrainThrows_EndsFailed_ForAllReaders` | `tests/Agent42.Tests/ChatTurnWriterTests.cs`, `ChatTurnPumpTests.cs` |
| AC19 | A timed-out append that landed is not duplicated; readers dedupe by seq | `AmbiguousAppend_NoDuplicate`, `StoreReader_DedupesBySeq` | `tests/Agent42.Tests/ChatTurnStoreTests.cs`, `ChatTurnTailTests.cs` |
| AC20 | Deleting a conversation under a running turn does not resurrect its blobs | `DeleteWhileRunning_DoesNotRecreateBlobs` | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC21 | The sweeper deletes aged turn blobs (durable and in-memory), never a running or young one | `Sweeper_DeletesAged_KeepsRunningAndYoung` | `tests/Agent42.Tests/ChatTurnSweeperTests.cs` |
| AC22 | The feed is a bounded ring: a reader behind it falls back to the store; both truncated ends `truncated` | `FeedRing_FallsBackToStore`, `BothTruncated_EndsTruncated` | `tests/Agent42.Tests/ChatTurnTailTests.cs` |
| AC23 | `GET /turn` and the stream never expose an incognito turn without the flag, or to another user | `Turn_Incognito_NotVisibleWithoutFlag` (extends AC10) | `tests/Agent42.Tests/ChatTurnApiTests.cs` |
| AC24 | Concurrent turns in one conversation keep independent logs; starting B does not delete a running A | `ConcurrentTurns_KeepIndependentLogs` | `tests/Agent42.Tests/ChatTurnStoreTests.cs` |
| AC16 | `sw.js` `CACHE` bumped, `chatstream.js` is in `SHELL` and reachable from `main.js` | updated pins + existing shell test | the five `*AssetsTests.cs` files, `tests/e2e/devhost.spec.mjs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measuring tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776, at build |
| `method_lines_max` | 80 | SonarAnalyzer S138, at build |
| `method_params_max` | 11 | SonarAnalyzer S107, at build |
| `e2e_failing_tests_max` | 0 | Playwright exit status (`e2e-devhost`, `e2e-selfhost.yml`) |
| `vulnerable_packages` | 0 | CI `vulnerable-packages` job |

Also: `dotnet build`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`, `az bicep build` for
the infra change. No new NuGet packages are planned; if one turns out to be needed, resolve it with `dotnet add package`
(rule 4), never from memory. Report any gate not actually measured as SKIPPED.

## Out of scope

- Lifting `maxReplicas` or any other clustering work (`plan-orleans-clustering.md`); this plan only avoids adding a blocker.
- Android reattach, the fleet analyzer chat, the chat-app gateway and the MCP agent surface.
- Resuming a turn whose **owning replica died** — it ends `interrupted` and the client uses history recovery. Re-driving
  an interrupted turn would mean executing tool calls twice.
- Cross-replica push of live tokens (Postgres `LISTEN/NOTIFY`, Redis): polling the store is the baseline; revisit only if
  measured latency on a second replica is a problem.
- Answering a pending client tool from a passive second device (D13 follow-up).
- Changing session persistence, the 2 MB cap, or what the grain stores (the log is a separate, short-lived copy).
- A web-push on completion (ADR 0028 covers away replies already).

## Review

Critic verdict on the first draft: **not ready** (3 high, 7 medium, 3 low). Handling, finding by finding:

| # | Finding | Handling |
|---|---|---|
| 1 high | Detached pump loses `RequestContext` / user identity | **Accepted.** Context bullet "Detached pump identity", step 4, AC17 |
| 2 high | Heartbeat vs terminal-status write race; no `failed` state; append retry not idempotent | **Accepted.** D4 (single writer, heartbeat stopped first, terminal states), D12 (conditional append, seq dedupe), step 4, AC18/AC19 |
| 3 high | Delete under a running turn resurrects content; retention sweep and self-host not covered | **Accepted.** D10 (one provider-neutral sweeper, Bicep lifecycle rule dropped), D11 (store-side fence), step 7, AC20/AC21 |
| 4 med | `active.json` read-modify-write race; next-turn cleanup contradicts "older turns stay readable" | **Accepted.** Pointer is a hint, last writer wins; cleanup deletes terminal turns only; AC24 |
| 5 med | Second device would double-run client tools | **Accepted.** D13: a passive attach never executes tools; the pending-confirm gap is listed as a follow-up; AC14 extended |
| 6 med | Incognito retention/eviction and flag semantics unspecified | **Accepted.** D6 extended, sweeper covers the memory twin, AC23 |
| 7 med | `TurnFeed` unbounded | **Accepted.** D14 bounded ring with store fallback, AC22 |
| 8 med | Sick store makes other replicas misreport; slow rollover detection | **Accepted as documented behaviour, not a code change.** D15: `StaleAfter` 45 s, graceful rollover writes `interrupted` at once; the other device falls back to history polling |
| 9 med | AC gaps, file-name inconsistencies, dubious Cosmos serializer test | **Accepted.** Step 11 now lists five cases and the DevHost canned-turn work; AC5/AC13 names aligned; the Cosmos test is dropped (streamed type, not persisted state) |
| 10 med | Complexity risk in pump/tail; bare line-number claims | **Accepted.** Pump/writer/batch and tail/coalescer/stream-writer split up front; line ranges replaced by method names and marked "re-read, numbers drift" |
| low | ADR number | **Accepted:** 0153 is the next free number at plan time; `/decide` re-checks at execution |
| low | Polling cost depends on spike S2 | **Accepted as is:** already flagged in S2 and the post-deploy checks |
| low | Reattach after completion could render twice | **Accepted.** "Reader state at completion" bullet, step 9, e2e case (5) |

No finding was rejected. The critic did not open the individual ADR files or `plan-orleans-clustering.md`, and did not
verify DevHost's canned-turn capability or the `sw.js` version; the executor re-checks DevHost in step 11 (done) and the
ADR set when writing 0153. A second critic pass on the *revised* plan was not run.

## Execution notes (live; becomes "Divergences from the plan" at close-out)

Branch `feature/resumable-chat-stream`, from `main @ f43a9a3` (no drift). Steps 1-5 done with their tests; 6+ pending.

Divergences so far:
- **Types live in two projects.** Port, in-memory twin, `ChatTurnStore`, `ChatTurnOptions`, `ChatTurnMeta`, `IReplicaIdentity` are in `src/Agent42.Agents/ChatTurns/`; pump/writer/feed/registry/readers/tail/sweeper/registration in `src/Agent42.Backend/Conversations/`.
- **`ITurnEventReader.ReadAsync` yields batches** (`IReadOnlyList<LoggedEvent>`), not single events: the first batch is the replay, later ones are live growth. Both readers expose `End` (`TurnEnd`: status, truncated, degraded).
- **`StoreReader` polls only** (no local wake signal): the plan called the signal an optimisation; deferred until a second replica's latency is measured.
- **A batch over `MaxLogBytes` lands as far as it fits, in whole lines**, then the turn is `truncated` (the first draft dropped the whole batch, so a turn that emitted everything inside one flush lost its start). Found by `BothTruncated_EndsTruncated`.
- **Meta updates are fenced like appends** (`ChatTurnStore.UpdateMetaAsync` refuses when the log is gone). Found by `APurgedLog_StopsTheWriter_AndNothingIsRecreated`: a heartbeat landing between a purge and the next append recreated the meta blob.
- `LocalFileStore` gained `AppendAtAsync` (conditional append under the file gate) and `LocalFileItem.Modified`.
- Added `ChatTurnOptions.FlushBytes` (64 KB): a batch that large flushes at once.
- AC5/AC6 live in `ChatTurnMultiReplicaTests` (two replicas = two stores/registries/tails over one blob store).
- **Spike S2 (append-blob range on an empty blob / past the end) NOT measured here** (no Azurite). `AzureChatTurnBlobs.GetRangeAsync` handles 416 as "no growth" defensively; moved to the post-deploy checks. Spike S1 pending (decides step 10 rendering).
