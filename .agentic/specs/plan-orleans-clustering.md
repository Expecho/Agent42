# Plan — Real Orleans clustering (Cosmos / Postgres membership) and multi-replica readiness

planned-against: main @ a5d20e9

## Overview

**Goal:** replace `UseLocalhostClustering()` in the Backend with a real membership provider: **Cosmos DB** on the
Azure path and **Postgres (ADO.NET)** on the self-host `Storage:Provider=local` path. Then make the app genuinely safe
to run as more than one replica, and lift the `min = max = 1` pin in Bicep.

**Why now:** the live Container App had drifted to `minReplicas = 0` with a portal-added `http-scaler` rule (10
concurrent requests) when observed via `az containerapp list -g rg-agent42 --subscription "De Heer MSDN"` on
2026-11-19 (the Bicep says 1, `@minValue(1)`, and declares no rule; this is live state, so Step 14 re-confirms it
before declaring anything). Every
deploy overlaps two revisions. Under localhost clustering each replica is its own one-silo cluster, so the same
conversation grain can activate twice (split brain). Real membership fixes that for grains. It does **not** fix the
in-process registries that CLAUDE.md rule 12 lists, and the scope decision below (max > 1) makes those a requirement.

**Phasing (one spec, three MRs, each shippable on its own):**

| Phase | Content | Ships with |
|---|---|---|
| 0 | Five spikes (unverified platform behaviour). Throwaway files outside the repo | no MR; findings recorded in the spec |
| 1 | Real clustering (Cosmos + Postgres), `max` still 1 | rollover overlap is safe for grains; fixes the split brain |
| 2 | Multi-replica readiness: the silent-failure rows of the inventory below | still `max = 1` |
| 3 | Lift `maxReplicas`, declare the scale rule in Bicep, docs and rule 12 | the actual scale-out |

**Phase 3 is gated:** it needs every Phase 2 row green and the Phase 0 spikes S1 and S2 passed. If S1 or S2 fails,
stop after Phase 1 and say so; do not force Phase 2/3.

## Decisions taken in the planning interview (2026-11-19)

User's explicit calls. Don't relitigate.

- **D1 — Scope is "allow max > 1"** (not just rollover safety). Rule 12 is therefore being retired, row by row, in
  Phase 2. The user was told that real clustering alone does not make the in-process registries safe.
- **D2 — Postgres (ADO.NET) clustering for self-host too**, not only Cosmos for Azure.
- **D3 — Membership lives in the existing `agent42` Cosmos database** as a new pre-created container (the account has
  `disableLocalAuth`, so the app cannot create containers: same constraint as every other container).
- **Not decided, flagged for approval:** the *minimum* replica count. The interview asked about max only. This plan
  keeps `minReplicas` default **1**, but widens the allowed range to `0..N` so scale-to-zero becomes a declared option
  rather than portal drift. Whether prod should actually run at 0 is a separate call, because reminders, bot
  connections and background fleet work stop while at zero (ADR 0111, 0097). Confirm before Phase 3.
- Hosts that are not the Azure/local Backend keep their current clustering: DevHost and every `TestCluster` stay on
  localhost/test clustering; the Aspire AppHost keeps `WithDevelopmentClustering()` (the existing
  `Orleans:Clustering:ProviderType` guard in `Program.cs` stays the Aspire escape hatch).

## Affected areas

Verified against the tree at `a5d20e9`.

- Clustering wiring: `src/Agent42.Backend/Program.cs` (silo block, the `Orleans:Clustering:ProviderType` guard),
  `src/Agent42.Backend/Storage/{StorageRegistration,AzureStorageRegistration,LocalStorageRegistration,PostgresSchema}.cs`,
  `src/Agent42.Backend/Storage/Sql/*.sql` (embedded), `Directory.Packages.props`, `src/Agent42.Backend/Agent42.Backend.csproj`.
- Dev/test hosts (must keep working): `tools/Agent42.DevHost/Program.cs`, `src/Agent42.AppHost/AppHost.cs`,
  `tests/Agent42.Tests/{PostgresFixture,SharedClusterFixture,TestSiloConfigurator}.cs`.
- Infra: `infra/main.bicep` (replica params, Cosmos container params), `infra/modules/container-app.bicep` (scale, env),
  `infra/modules/cosmos.bicep`, `docs/features/azure-deployment.md`.
- Phase 2 code (rows of the inventory): `src/Agent42.Backend/Fleet/*`, `src/Agent42.Backend/ChatGateway/*`,
  `src/Agent42.Backend/Mcp/{LocalBridge*,McpOAuthTokenResolver}.cs`, `src/Agent42.Backend/Email/MailgunReplayGuard.cs`,
  `src/Agent42.Agents/Fleet/*`, `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs`.
- Tests that read Bicep text and will trip on infra edits (all in `tests/Agent42.Tests`): `CopilotFilesMountTests`
  (relies on the `scale: {` boundary in `container-app.bicep`), `InfraPortabilityTests`, `EasyAuthSessionTests`,
  `AppInsightsWiringTests`, `CompositeIndexMatchesTheSearchQueryTests` (the `chatMessagesContainer` block in
  `cosmos.bicep`), `DeployTriggerTests`. No test pins a replica count today.
- Doc/comment claims of "single replica / localhost clustering" to fix in Phase 3 (grep again at execution time, do
  not trust this list): CLAUDE.md rule 12 and the manual-prerequisites table; `docs/features/self-hosting.md`,
  `docs/setup/local.md`; `.agentic/gotchas.md` (the replica notes, e.g. around the Copilot/fleet/gateway entries);
  ADRs 0001, 0031, 0049, 0054, 0066, 0076, 0096, 0097, 0107, 0111, 0124, 0127, 0135; infra comments in `main.bicep`,
  `container-app.bicep`, `cosmos.bicep`; source comments in `LiveTelemetryBuffer`, `LocalBridgeRegistry`,
  `MailgunReplayGuard`, `ChatGateway*` (`ChatGatewayLease`, `ChatGatewayService`, `ChatTurnPipeline`, `InboundDeduper`),
  `LocalBridgeLimits`, `Fleet*Service`, `CopilotSessionFsProvider`, `CopilotModelDirectory`, `BlobAttachmentStore`,
  `LocalFileStore`, `AppHost.cs`; and the Docnet PDF-lock gotcha (`.agentic/gotchas.md`, "pins a single replica").

## Context & constraints

Judgment and findings the executor cannot cheaply re-derive. Read `.agentic/gotchas.md` for: the replica/rollover
entries (grep `rollover`, `single replica`), the chat-gateway lease entry, the Cosmos `$type` serializer entry, the
shared TestCluster entries, and the local-storage entry (the `ReloadTypesAsync` trap).

1. **`ServiceId` is a data key; do not change it.** `UseLocalhostClustering` sets development constants for
   `ServiceId` and `ClusterId` (not the `ClusterOptions` class defaults; recalled as `"dev"`, but S0 measures it), and
   the code never sets either (no `ClusterOptions` anywhere in `src`). Where `ServiceId` is a data key:
   - **Postgres: confirmed.** `src/Agent42.Backend/Storage/Sql/PostgreSQL-Persistence.sql` filters on `ServiceId`
     (lines ~72, 122, 162, 180, 197) and `PostgreSQL-Reminders.sql:13` makes it part of the reminders primary key
     `(ServiceId, GrainId, ReminderName)`.
   - **Cosmos grain state: NOT keyed by it.** The stored envelope is `{ id, PartitionKey, GrainType, State, ETag }` and
     the default partition key is the grain type (`.agentic/decisions/0061-searchable-chat-message-projection.md:45-46`).
   - **Cosmos reminders: unverified** (believed to carry it; no evidence in this repo). S3 settles it from the package
     source or the emulator.
   Switching to real clustering forces `ClusterOptions` to be explicit; if `ServiceId` differs from the localhost value,
   **every Postgres grain row and reminder, and probably every Cosmos reminder, becomes invisible** with no error. So:
   measure the localhost values (S0), pin `ServiceId` to exactly that as a constant, and prove visibility with a
   **round-trip test** (write under the old configuration, read under the new one), not only a constant check (Step 2).
   `ClusterId` only affects membership and may be chosen freely. The Cosmos reminder round trip cannot run in CI (no
   emulator); say so in the ADR and cover it with the S3 spike result.
2. **The silo must advertise a reachable address.** `UseLocalhostClustering` binds 127.0.0.1. A replica that publishes
   127.0.0.1 into a shared table is unreachable to its peers, and the membership protocol then declares peers dead.
   Real clustering needs `ConfigureEndpoints(advertisedIP, siloPort, gatewayPort, listenOnAnyHostAddress: true)`
   (Orleans docs, "Server configuration"). The co-hosted client lives in the silo, so the gateway port can be 0 unless
   S1 shows something needs it. Easy Auth `excludedPaths` and ingress `targetPort` (8080) are unaffected: silo traffic
   is raw TCP between replicas, not through the ingress.
3. **A rollover will now run mixed versions in ONE cluster** (old and new revision share membership). Orleans handles
   grain-interface version skew only for compatible changes; a breaking grain-method signature change during a deploy
   now fails calls instead of silently splitting. This needs a gotcha and a rule for reviewers, not new code (Step 7).
4. **First deploy of this change is a one-time split brain.** The old revision is still a localhost cluster while the
   new one joins Cosmos membership. That is exactly today's every-deploy behaviour; accept it and say so in the ADR.
5. **The inventory (Phase 2) is the heart of D1.** A scout swept `src/` for process-local state (2026-11-19, at
   `a5d20e9`). Rows below are the ones it classed NOT safe at 2 replicas. Starting points (the executor re-reads the
   code; line numbers drift): R1–R3 `src/Agent42.Backend/Fleet/FleetTaskService.cs` (`_running`, `_submitGate`,
   `NormalizeAsync`), R2 also `FleetEventLog.cs`; R4 `FleetLessonService.cs` (`_running`, `_locks`); R5/R6
   `ChatGateway/ChatGatewayService.cs` (`_holdsLease`), `ChatSessionRegistry.cs`, `ChatDeliverySender.cs`; R7/R8
   `Mcp/LocalBridgeRegistry.cs`, `LocalBridgeConnectionFactory.cs`, `AgentApi.Mcp.cs` (`EvictAsync`); R9
   `Email/MailgunReplayGuard.cs`; R10 `Mcp/McpOAuthTokenResolver.cs` (`_gates`); R11
   `ChatGateway/Signal/SignalDaemon.cs`; R12 `Documents/DocumentIndexRegistration.cs`; R13
   `Telemetry/LiveTelemetryBuffer.cs`. "Safe iff one cluster" rows (client-tool broker, `_turnCts`, SessionFs gate,
   MCP/GitHub OAuth pending grains) need no code beyond Phase 1, **but the plan does not assume it: Phase 1 adds the
   cross-silo tests that prove it** (Step 5b), and **reminders are not "safe by default"**: real membership
   redistributes reminder ranges on every join or leave (every rollover, even at `max = 1`), so a reminder can fire
   late until a dead silo is declared dead, or twice across a rebalance. `AgentGrain.Scheduling.cs` (`schedule_prompt`)
   and `FleetDreamGrain.cs` (hourly dream, ADR 0124) must tolerate both; S3 measures it and Step 5b tests it.

   **Checked and found safe (recorded so nobody re-checks):** the Data Protection ring (Azure path persists to Blob,
   `AzureStorageRegistration.cs`; local mode uses the filesystem ring, fine at one container); `UsageIngestionService`
   (flushes to one global grain; contention on that non-reentrant grain grows with replica count, watch it);
   `PostgresSchemaInitializer` (advisory lock); version-keyed per-user caches (`UserMcpToolProvider`,
   `UserChatClientFactoryProvider`); `McpAppRegistry`; Copilot runtime clients and workspaces (per replica / shared
   Azure Files); chat-gateway sessions, dedupe and queues (lease holder only). The Docnet PDF lock is per process and
   only gets better with replicas; its gotcha text ("pins a single replica") joins the Phase 3 doc sweep.

   | # | State | Failure at 2 replicas | Fix class |
   |---|---|---|---|
   | R1 | Fleet `NormalizeAsync` rewrites a task not in the local `_running` to *Interrupted* (`FleetTaskService`) | a plain GET on replica B persists *Interrupted* for a task still running on A; A's later saves race it. **Silent data corruption; worst item** | owner id + heartbeat in the task record; interrupt only when the heartbeat is stale |
   | R2 | Fleet stop / answer / SSE live tail / event-log fan-out (`FleetTaskService`, `FleetEventLog`) | stop/answer on B answers Conflict; SSE on B has no live tail | owner-routed call (mailbox, below) |
   | R3 | Fleet per-user and global caps (`_submitGate`, `_running`) | caps are per replica, so they multiply | count from the store via owner heartbeats |
   | R4 | Fleet dream guard (`FleetLessonService` `_running`/`_locks`) | reminder and manual dream on different replicas both pass; orphan normalisation can fail a live dream | one grain-owned dream per user, or a Blob ETag write |
   | R5 | Chat-gateway control (`RestartAsync`/`StopAsync`/`IsLive` are `if (_holdsLease)` no-ops off the holder) | a saved/stopped connection silently does nothing until the holder restarts | persist desired state in the connection directory grain; the lease holder reconciles on its tick |
   | R6 | Away-reply / notification delivery to a chat (`ChatDeliverySender` checks the LOCAL session registry) | a grain on a non-holder silo finds no session and skips delivery silently | route to the lease holder (mailbox) or outbox drained by the holder |
   | R7 | Local-bridge socket registry + Chrome-extension bridge (`LocalBridgeRegistry`, `LocalBridgeConnectionFactory`) | helper websocket lands on A, grain MCP call on B finds none | directory record (owner replica) + forward through the owner (mailbox) |
   | R8 | Bridge revocation (`EvictAsync`) | regenerate/disable/delete on B does not close the socket on A: **a revoked helper keeps serving. Security property** | eviction reaches the owner (mailbox) |
   | R9 | `MailgunReplayGuard` in-memory `_seen` | a captured signed request is accepted once per replica | persisted conditional insert with TTL |
   | R10 | `McpOAuthTokenResolver` `_gates` (refresh single-flight) | two replicas refreshing one rotating refresh token can invalidate each other | refresh in the user's config grain, or conditional save (low likelihood; smallest fix wins) |
   | R11 | Signal daemon (child process, loopback port) | link/status calls on a non-holder return 503 at random | stays **dormant** (`signalEnabled=false`); startup refuses `signalEnabled` with `maxReplicas > 1` |
   | R12 | Process-global in-memory stores used when a Cosmos key is absent (`DocumentIndexRegistration` fallback and friends) | a replica with a missing env var silently gets a private RAG store | startup assert in prod: fail fast, never fall back, when the Azure provider is on |
   | R13 | `/logs` ring (`LiveTelemetryBuffer`) | each replica has its own ring and cursor | **leave and document** (diagnostics only); not a blocker |

   `_clientToolBrokers`, fleet per-run objects, the Copilot runtime client and the chat-gateway runners are fine as
   they are (grain-owned, or tied to the one replica that runs the thing). Chat-gateway sessions, dedupe and queues are
   fine because only the lease holder runs them.
6. **The shared primitive is a per-replica "mailbox".** R2, R5, R6, R7, R8 all need "call the replica that owns X".
   Do not invent five mechanisms. Spike S2 decides how to address *one specific silo* (candidates: a grain keyed by
   replica id that the owning replica activates itself and keeps alive; an Orleans placement hint to a named silo).
   The replica identity is `CONTAINER_APP_REPLICA_NAME` on ACA (**unverified**, S1) with a generated fallback for
   DevHost/compose. Owner records carry `{replicaId, heartbeat}`; a record whose heartbeat is stale is orphaned, which
   is what R1 uses.
7. **No Azure RBAC change.** The Cosmos data-plane grant is account-scoped
   (`infra/modules/cosmos-role-assignment.bicep`), so a new container needs none. Hard rule 5 is not engaged.
8. **Dependency versions are resolved, not recalled** (hard rule 4): `dotnet add package
   Microsoft.Orleans.Clustering.Cosmos` and `Microsoft.Orleans.Clustering.AdoNet`, pin in `Directory.Packages.props`
   at the same version as the other Orleans packages (they must match, currently 10.3.1). The local NuGet cache holds
   Cosmos clustering only at 10.2.2 and older and **no** AdoNet clustering package, so both come from the feed.
9. **The Orleans ADO.NET packages ship no SQL.** ADR 0127 decision 3 and `PostgresSchema` already embed the v10.3.1
   scripts and apply each only when its sentinel table is missing, under an advisory lock. The membership script
   follows that exact pattern (S4 retrieves it; never write it from memory).

## Steps

Mark: **[P]** = parallelizable with other [P] steps in the same phase; otherwise sequential. Every step's tests are
written in the same step (CLAUDE.md hard rule 3). Run the narrowest test first, then `dotnet test Agent42.slnx`.

### Phase 0 — Spikes (use the `spike-dotnet` skill where .NET; Azure ones need the user)

Each spike's result is written into this spec's **Spike results** section before Phase 1 starts. S1 and S2 gate Phase 3.

- **S0 [P] — `ServiceId` default.** Start an in-process silo with `UseLocalhostClustering()`, resolve
  `IOptions<ClusterOptions>`, record `ServiceId` and `ClusterId`. This is deterministic and needs no Azure.
- **S1 [P] — ACA replica-to-replica reachability.** Deploy a throwaway 2-replica Container App in the same
  managed environment as prod (take the name from the `CAE_NAME` repo variable; never write it into a repo file) running
  two plain TCP listeners (test clustering cannot span replicas), and prove: (a) replicas reach each other directly on a non-ingress TCP port by pod IP; (b) the
  address to advertise (an env var or `Dns.GetHostAddresses`); (c) `CONTAINER_APP_REPLICA_NAME` exists and is unique
  per replica; (d) whether `additionalPortMappings` is needed (it load-balances, so it should NOT be the mechanism);
  (e) SIGTERM grace period and whether the silo can shut down gracefully inside it. **The user must run or approve
  the `az containerapp create`/delete** (outward-facing, costs money); prepare the exact commands.
- **S2 [P] — addressing one silo.** In a 2-silo `TestCluster`, prove a call can be delivered to the silo owning a
  given replica id (mailbox grain / placement hint), survives deactivation (re-activates on the right silo or fails
  cleanly), and what happens when the owner is gone. Pick one mechanism, record why.
- **S3 [P] — Cosmos membership schema and cold start.** Against the Cosmos emulator (Aspire AppHost) or a scratch
  account: the container's partition key path the provider needs, that AAD auth works with
  `IsResourceCreationEnabled = false`, and whether Cosmos grain-state documents are keyed by `ServiceId`. Then the
  scale-to-zero case: start a silo, `kill -9` it (stale `Active` row left behind), start a second silo and measure
  whether startup succeeds and how long until the stale row is declared dead (`ValidateInitialConnectivity`,
  `IAmAlive`, `DefunctSiloCleanupPeriod`). **Also:** (i) which `Cosmos reminder` documents carry `ServiceId`;
  (ii) a reminder owned by the killed silo: does it fire on the survivor, how late, and can it fire twice. Decide the
  `ClusterMembershipOptions` values to set. **S3 gates the "min 0 allowed" part of Phase 3** as well as S1/S2.
- **S4 [P] — ADO.NET membership.** Resolve `Microsoft.Orleans.Clustering.AdoNet` from the feed, locate the Postgres
  membership script for that exact version (the Orleans repo ships it; the package does not), record its sentinel
  table name and idempotence, and the registration API (`UseAdoNetClustering(...)` options and `NpgsqlDataSource`
  support, mirroring `UseLocalStorage`). **Repeat S3's kill-and-restart measurement on Postgres**: a single self-host
  silo that restarts (`compose restart`, container recreate) gets a new IP and leaves a stale `Active` row; prove it
  starts and recovers, and how long that takes. D2 puts this path in Phase 1 even though compose never exceeds one
  replica, so its restart risk is the cost to weigh.

### Phase 1 — Real clustering, `max` still 1 (one MR, branch `feature/orleans-clustering`)

1. **Packages and ADR skeleton.** Add the two clustering packages via `dotnet add package`; pin centrally. Run
   `/decide` for ADR 0136 (clustering provider, ServiceId pin, mixed-version rollover, the one-time split brain, why
   Cosmos and Postgres, what stays localhost). Verify: `dotnet build Agent42.slnx`.
2. **`ClusteringRegistration` (new file in `src/Agent42.Backend/Storage/`, flat namespace).** One method the silo block
   calls in place of the bare `UseLocalhostClustering()`; keeps the `ProviderType` guard (Aspire) and picks by
   `StorageProviderSetting`: Azure → Cosmos membership, local → ADO.NET membership. Sets the pinned `ServiceId` (S0),
   a `ClusterId` constant, advertised endpoints (S1), and the `ClusterMembershipOptions` S3 decided. Keep it split into
   small methods (S3776 is a build error at 15; follow `HindsightRegistration`'s shape). The Cosmos client is the
   existing `CosmosClient` singleton (same `ConfigureCosmosClient(sp => …)` shape as `AzureStorageRegistration`).
   Verify: `ClusteringRegistrationTests` (below).
3. **[P] Cosmos container.** `infra/modules/cosmos.bicep` + `infra/main.bicep` param (`membership`, partition key per
   S3) mirroring `remindersContainer`; seed the same container in `src/Agent42.AppHost/AppHost.cs`; add a test that the
   container name in code equals the Bicep default (precedent: `CompositeIndexMatchesTheSearchQueryTests`). Verify:
   `bicep build infra/main.bicep` (the CI gate), the Bicep-reading tests listed under Affected areas.
4. **[P] Postgres membership.** Embed the membership script (S4) in `Storage/Sql/`, add its `OrleansScripts` entry in
   `PostgresSchema` keyed by its sentinel table, register ADO.NET clustering in `LocalStorageRegistration`. Update
   `PostgresFixture` to use ADO.NET clustering against the same per-test database. Verify: `PostgresSchemaTests`-style
   test that the table appears and a second `EnsureAsync` is a no-op; `PostgresTwoSilos_FormOneCluster`.
5. **Keep the other hosts green.** DevHost, Aspire and all `TestCluster` fixtures stay as they are. Verify:
   `SharedClusterSeamTests`, the full suite.
5b. **Cross-silo behaviour tests** (the "safe once one cluster" rows are proven, not assumed). On a 2-silo
   `TestCluster`: a streaming `ChatAsync` and a client-tool result submit that originate on the silo that does NOT host
   the grain; a stop request from the non-hosting silo cancels the running turn; a reminder owned by a silo that is shut
   down fires on the survivor (tolerating late/double fire as `schedule_prompt` and the dream must). Verify: the tests
   named in the mapping table below.
6. **Mixed-version rollover gotcha.** `/gotcha`: during a rollover old and new revisions share one cluster, so a
   breaking grain-interface change must be staged. Verify: gotcha cites `ClusteringRegistration`.
7. **Docs for Phase 1.** `/sync-docs`: the `docs/features/azure-deployment.md` container list, self-hosting doc,
   `Program.cs` comments, `AppHost.cs` comment. CLAUDE.md rule 12 stays true at this phase (still `max = 1`); only
   add the pointer to ADR 0136. The ADR (Step 1) also records: the `IReplicaIdentity` fallback id is regenerated on a
   local-mode restart, so orphan owner records rely on the heartbeat-staleness TTL, which must be derived from the S3
   `IAmAlive` values; and R12 changes prod startup (fail fast), with DevHost and test silos explicitly exempt.

### Phase 2 — Multi-replica readiness (one MR per cluster of rows; branch off the merged Phase 1)

8. **Replica identity and mailbox primitive.** `IReplicaIdentity` (`CONTAINER_APP_REPLICA_NAME`, fallback generated
   id) and the mailbox mechanism S2 picked, with an owner record type (`{replicaId, heartbeat}`) and a staleness rule.
   Register it grain-side per rule 7 of CLAUDE.md (`AddAgent42Silo()` if identical in every host, a port otherwise),
   including DevHost and test silos. Verify: unit tests plus a 2-silo `TestCluster` routing test.
9. **Fleet ownership (R1–R4).** Owner/heartbeat in the task record; `NormalizeAsync` interrupts only a stale owner;
   stop/answer/subscribe go to the owner; caps count from the store; dream guard per user. **R1 first and alone**, it is
   the corruption bug. Verify: tests with TWO `FleetTaskService` instances over one in-memory store (start on one,
   read/list/stop/answer on the other; assert the task is never rewritten to *Interrupted* while the owner heartbeats;
   assert a dead owner IS interrupted).
10. **[P] Chat gateway (R5, R6, R11).** Persist desired connection state, holder reconciles on its lease tick;
    delivery routes to the holder; `signalEnabled` with `maxReplicas > 1` refuses at startup. Verify: gateway tests
    with a fake lease: non-holder `Save` is applied by the holder; delivery from a non-holder reaches the holder's
    session.
11. **[P] Local bridge (R7, R8).** Directory record + forward through the owner; eviction reaches the owner. Riskiest
    step: bridge tool calls are request/response and latency-sensitive. **If S2's mechanism cannot carry them within a
    reasonable budget, stop and report: the fallback is to leave `max = 1` (Phase 3 does not start).** Verify: a test
    where the socket is registered on silo A and the MCP call and the revocation originate on B.
12. **[P] Small ones (R9, R10, R12).** Persisted replay-guard claims (Cosmos conditional insert / Postgres on conflict,
    TTL); refresh gate in one place; prod-store startup assert (the Azure provider must not silently fall back to an
    in-memory index). Verify: replay test where the second "replica" shares the store; startup-assert test.
13. **`/logs` (R13).** Document as per-replica in the ADR and the `/logs` page text. No code.

### Phase 3 — Scale-out (one MR)

14. **Lift the pin.** First re-run the `az containerapp` observation from Overview and record what the live scale rule
    is; declare THAT, not a guess. `main.bicep`: `maxReplicas` becomes a real parameter (decorators removed or widened);
    `minReplicas` allowed `0..max`, default 1. `container-app.bicep`: declare the HTTP scale rule that now exists only
    in the portal, as a parameter with the live value as default. Keep the `scale: {` boundary the
    `CopilotFilesMountTests` read (edit the test if its boundary moves; do not delete it). Verify: `bicep build`, the
    Bicep-reading tests, `az deployment group what-if` (hand the command to the user).
15. **Rule 12 rewrite and doc sweep.** Replace rule 12 in CLAUDE.md with what is now true (cluster-wide state in
    grains/stores; the lease-gated singletons and the dormant Signal still single-holder; `/logs` per replica). Fix
    every doc/comment in the Affected-areas list; amend the ADRs that state "single replica" with a pointer to 0136
    (a supersession note, not a rewrite). Verify: `/audit-docs` finds no stale claim.
16. **Prod rollout notes for the user, not executed by the agent:** the deploy is the user's; first rollout is the
    one-time split brain (Context 4); watch `membership` rows and the `/dashboard` silo count after deploy.

## Acceptance criteria → test mapping

| AC | Test (new unless noted) | File |
|---|---|---|
| Effective `ServiceId` equals the localhost default (data stays visible) | `ClusterIdentity_ServiceId_IsPinnedToLocalhostDefault` | `tests/Agent42.Tests/ClusteringRegistrationTests.cs` |
| Provider is chosen by config: Aspire `ProviderType` untouched; azure → Cosmos; local → ADO.NET | `Registration_SelectsProviderByConfig` | `ClusteringRegistrationTests.cs` |
| Silo advertises a non-loopback address and the configured ports | `Registration_AdvertisesConfiguredEndpoints` | `ClusteringRegistrationTests.cs` |
| Two Postgres silos on one database form one cluster | `PostgresTwoSilos_FormOneCluster` | `tests/Agent42.Tests/PostgresClusteringTests.cs` |
| Membership script is applied once and is idempotent | `PostgresSchema_Membership_AppliedOnce` | `tests/Agent42.Tests/PostgresSchemaTests.cs` (new or existing class for schema) |
| Existing Postgres reminders and grain rows stay visible after switching clustering | `Postgres_RoundTrip_VisibleAcrossClusteringSwitch` (write under localhost config, read under ADO.NET config) | `PostgresClusteringTests.cs` |
| A single Postgres silo restarts over its own stale `Active` row and recovers | `PostgresSilo_RestartOverStaleRow_Recovers` | `PostgresClusteringTests.cs` |
| Code container name equals the Bicep default | `MembershipContainer_MatchesBicep` | `tests/Agent42.Tests/ClusteringInfraTests.cs` |
| A grain is single-activation across two replicas under one cluster | `Grain_SingleActivation_AcrossTwoSilos` | `PostgresClusteringTests.cs` |
| A stream and a client-tool submit from the non-hosting silo work; stop from it cancels the turn | `CrossSilo_StreamClientToolAndStop` | `tests/Agent42.Tests/CrossSiloTests.cs` |
| A reminder owned by a shut-down silo fires on the survivor | `CrossSilo_Reminder_FiresOnSurvivor` | `CrossSiloTests.cs` |
| Fleet task read/list on another replica never writes *Interrupted* while the owner heartbeats | `Fleet_ReadOnOtherReplica_DoesNotInterruptLiveTask` | `tests/Agent42.Tests/FleetOwnershipTests.cs` |
| A dead owner's task IS interrupted | `Fleet_StaleOwner_IsInterrupted` | `FleetOwnershipTests.cs` |
| Fleet stop/answer from a non-owner reach the owner | `Fleet_StopAndAnswer_RoutedToOwner` | `FleetOwnershipTests.cs` |
| Fleet caps hold across replicas | `Fleet_Caps_CountAcrossReplicas` | `FleetOwnershipTests.cs` |
| A chat connection saved on a non-holder is applied by the holder | `Gateway_SaveOnNonHolder_AppliedByHolder` | `tests/Agent42.Tests/ChatGatewayServiceTests.cs` |
| Chat delivery from a non-holder reaches the holder's session | `Delivery_FromNonHolder_ReachesHolder` | `ChatGatewayServiceTests.cs` |
| `signalEnabled` with `maxReplicas > 1` refuses to start | `Signal_RefusesMultiReplica` | `ChatGatewayServiceTests.cs` |
| A bridge call and a revocation from another replica reach the socket's owner | `Bridge_CallAndEvict_RoutedToOwner` | `tests/Agent42.Tests/LocalBridgeOwnershipTests.cs` |
| A replayed signed email is rejected by every replica | `MailgunReplay_RejectedAcrossReplicas` | `tests/Agent42.Tests/MailgunReplayGuardTests.cs` |
| The Azure provider never silently falls back to an in-memory index (DevHost/test silos exempt) | `AzureProvider_MissingIndexConfig_FailsStartup` | `tests/Agent42.Tests/StartupAssertTests.cs` |
| Bicep builds and still satisfies the existing Bicep-reading tests with the pin lifted | existing suite + CI `bicep build` | `CopilotFilesMountTests`, `InfraPortabilityTests`, … |

## Quality gate checklist

Thresholds copied from `.agentic/quality-gates.yaml` at plan time (re-read at execution, never from here):

- `coverage_new_code_pct_min`: **80**, measured by `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff).
- `cognitive_complexity_max`: **15**, S3776 at build time (a build error). `ClusteringRegistration` and the
  fleet-ownership code are the likely offenders: split early.
- `vulnerable_packages`: **0** (hard block); new clustering packages are resolved from the feed.
- `dotnet format Agent42.slnx --verify-no-changes`, `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, and the CI
  `bicep build`.

Coverage honesty: the Cosmos membership path has no CI emulator, so its new lines are covered only by the
registration test that builds the options without the network. If the coverage gate fails on it, report that and the
number; do not estimate it or edit the gate (memory: fix failing gates in the branch, never edit the gate file).

## Out of scope

- Changing which hosts use which clustering beyond the three in Context/Decisions (DevHost, `TestCluster` and Aspire
  stay as they are).
- Scale-out of the Signal daemon (stays dormant), of the local file stores in `local` mode (self-host stays one app
  container; compose is not made multi-replica), and of `/logs` (per replica by design).
- Changing the Copilot SessionFs write gate or adding Blob `If-Match` (safe once there is one cluster; optional
  hardening, not required).
- Actually setting prod to `minReplicas = 0`, running the prod rollout, or any Azure change: the agent prepares
  commands, the user runs them (spikes S1 and the what-if included). No RBAC is involved.
- Any new model/provider/egress behaviour (ADR 0013 not engaged).

## Spike results

*(Filled in by the executor during Phase 0. Empty at plan time.)*

## Review

Critic verdict (2026-11-19): no hard ADR contradiction (ADR 0001 line 40 already anticipates real clustering); 2
medium and several low findings. All were accepted; none rejected outright.

| # | Finding | Handling |
|---|---|---|
| 1 | Cosmos half of the ServiceId claim had no evidence; Cosmos grain state is not ServiceId-keyed (ADR 0061:45-46); the constant check does not prove visibility | Context 1 rewritten with per-store evidence; round-trip test added; Cosmos reminder round trip left to S3 and said to be un-CI-able |
| 2 | Reminders/directory change under real membership; "safe iff one cluster" rows had no cross-silo test | Context 5 reworked; Step 5b and two cross-silo tests added; S3 measures the reminder case |
| 3 | Cold start / stale row only spiked for Cosmos | S4 repeats it for Postgres; restart-over-stale-row test added |
| 4 | Live-state claims had no evidence | Overview states the `az` observation and date; Step 14 re-confirms first |
| 5 | Inventory evidence lived only in the scout's uncommitted report | per-row file starting points added to Context 5 |
| 6 | Checked-and-safe items were not recorded | recorded in Context 5 |
| 7 | Doc-sweep list incomplete | list extended; "grep again at execution time" stays the rule |
| 8 | S1 text garbled, hard-coded environment name | rewritten; environment comes from `CAE_NAME`, never into a repo file |
| 9 | S3 should gate "min 0 allowed" | added to S3 |
| 10 | Unrecorded decisions; startup-assert test in the wrong file | folded into Step 1/ADR note; test moved to `StartupAssertTests.cs` |
| 11 | Gate figures not diffed against the file | they were copied from `.agentic/quality-gates.yaml` at plan time (80 / 15 / 0); the plan still says re-read at execution |
