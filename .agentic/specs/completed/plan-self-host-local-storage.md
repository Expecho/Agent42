# Plan: self-hosting storage option (`Storage:Provider = azure | local`)

planned-against: main @ d164725

## Overview

Agent 42 is hard-wired to Azure storage: Cosmos DB (Orleans grain state, reminders, document vectors,
chat search) and Blob Storage (attachments, skills, fleet files, Copilot session files, chat-gateway lease,
Data Protection key ring). The goal is to let someone self-host the app with `docker compose up`, with **no
Azure account**, while **prod stays exactly as it is**. Both providers stay available, selected by one config
key. The user decided (this conversation): Postgres for the database side, both options kept, prod unchanged,
purpose = self-hosting without Azure lock-in.

- `azure` (default when the key is absent, so prod's Bicep needs **no change**): today's Cosmos + Blob wiring.
- `local`: Postgres (Orleans ADO.NET grain storage + reminders, pgvector document index, full-text chat
  search, advisory-lock gateway lease) + a filesystem volume for everything that is blob today.

## Affected areas (verified against the tree at the base commit)

| Area | Files |
|---|---|
| Provider wiring (Cosmos client, grain storage, reminders, blob client, Data Protection, index selection) | `src/Agent42.Backend/Program.cs` (:65 Cosmos client, :76 credential, :142-173 blob + DP, :198 skill store, :209-210 indexes, :352-363 gateway/copilot/fleet, :429-471 `UseOrleans`) |
| Index registration helpers (the exemplar shape for a per-feature registration) | `src/Agent42.Backend/Documents/DocumentIndexRegistration.cs`, `src/Agent42.Backend/Search/ChatSearchIndexRegistration.cs`, `src/Agent42.Backend/Fleet/FleetRegistration.cs`, `src/Agent42.Backend/ChatGateway/ChatGatewayRegistration.cs`, `src/Agent42.Backend/Copilot/CopilotRegistration.cs` |
| Blob seams to re-implement on files | `IAttachmentStore` (`src/Agent42.Agents/Attachments/IAttachmentStore.cs`), `ISkillPackageStore` (`src/Agent42.Agents/UserSkills/`), `IFleetBlobs` (`src/Agent42.Agents/Fleet/IFleetBlobs.cs`), `ICopilotSessionStore` (`src/Agent42.Backend/Copilot/`), `IChatGatewayLease` (`src/Agent42.Backend/ChatGateway/ChatGatewayLease.cs`) |
| Cosmos seams to re-implement on Postgres | `IDocumentIndex` (`src/Agent42.Agents/Documents/`, Cosmos impl `src/Agent42.Backend/Documents/CosmosDocumentIndex.cs`), `IChatSearchIndex` (`src/Agent42.Agents/Search/`, Cosmos impl `src/Agent42.Backend/Search/CosmosChatSearchIndex.cs`) |
| Config health | `src/Agent42.Backend/Configuration/ConfigHealth.cs` (:77-91 branches on `IsDevelopment()`, not on provider) |
| Layout/guard tests that a new folder or file trips | `tests/Agent42.Tests/ProjectLayoutTests.cs` (`ExpectedFolders` :92-112, `Hosts_CallAddAgent42Silo` :713-737), `DeployTriggerTests.cs`, `QualityGateTests.cs`, `AgentApiReachabilityTests.cs` |
| Packages | `Directory.Packages.props`, `src/Agent42.Backend/Agent42.Backend.csproj`, `tests/Agent42.Tests/Agent42.Tests.csproj` (no Npgsql / Testcontainers exist today) |
| New | `src/Agent42.Backend/Storage/` (new feature folder), `compose.yaml` (repo root), `docs/features/self-hosting.md`, `README.md` section, ADR (next free number, see Step 1) |

## Context & constraints (what the executor cannot cheaply re-derive)

**Hard constraints from the repo (read the gotcha, not just this list):**
1. Grain state is JSON with `$type` via Orleans' default `JsonGrainStorageSerializer`; namespaces are on-disk
   format (gotchas "Cosmos grain storage is JSON with `$type`", ADR 0039). **The local provider must write the
   byte-identical JSON shape**, so every persisted-state rule (trailing `[Id(n)]`, no collection expression into
   `IReadOnlyList<T>`, no `bool = true` defaults) keeps holding for both providers. The binary serializer proves
   nothing here.
2. Registration rule (ADR 0105, CLAUDE.md rule 7): ports that vary per host are registered by the host *before*
   `AddAgent42Api()` / `AddAgent42Silo()`; `ProjectLayoutTests.Hosts_CallAddAgent42Silo` source-text-pins those two
   calls in order before `builder.UseOrleans(`. `AddAgent42Silo()` fails startup on a missing *direct* grain ctor
   dependency (`IAttachmentStore`, `IDocumentIndex`, `IChatSearchIndex` are checked), not on a transitive one
   (`ISkillPackageStore` is transitive: the file-backed one must be registered or skills silently break).
3. Flat namespace for Backend (ADR 0040); IDE0130 is off on purpose; `ProjectLayoutTests` pins the Backend
   folder list exactly and allows only `Program.cs` at the project root. A new `Storage/` folder needs the test
   updated in the same step.
4. S3776 cognitive complexity max 15 is a build error. `Program.cs` is already large: put each provider's
   registration in its own static class in `Storage/` (the `HindsightRegistration` shape), never inline branches.
5. Dependency versions are resolved, never recalled (hard rule 4): `dotnet add package`, pin in
   `Directory.Packages.props`.
6. Single replica is load-bearing (rule 12). Local mode stays one container; the Postgres advisory lock is the
   guard against an accidental second container on the same database.
7. Egress is a design property (rule 13). Local mode adds **no** new egress. The `SEARXNG` sidecar still forwards
   queries upstream (gotcha "SearXNG is self-hosted, NOT private") and the compose file must say so.
8. Hard rule 5: no Azure RBAC executed by the agent. Nothing in this plan changes Azure infra; if the executor finds
   a reason to touch `infra/`, stop and report.

**Investigation findings:**
- `JsonGrainStorageSerializer` is the default for Cosmos with no explicit wiring; there is **no ETag /
  `InconsistentStateException` handling anywhere** (`rg InconsistentState` is empty), writes are bare
  `WriteStateAsync`. So the local store need not add conditional-write semantics beyond what Orleans' provider
  does itself.
- 23 `[PersistentState]` names, all under one provider name, `AgentGrain.StorageProviderName = "agent-state"`
  (`src/Agent42.Agents/Conversations/AgentGrain.cs:32`). One local registration covers all of them.
- Reminders: `silo.UseCosmosReminderService` today (Program.cs:464-471); `UseInMemoryReminderService` in DevHost/tests.
- **Attachment read URLs are persisted in the session.** `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs:105-121` mutates
  `UriContent.Uri` in the chat history, and the session (2 MB cap, ADR 0004) is serialized to grain state, so the
  URL lives there between turns and is re-signed on activation. Consequence: a `data:` URL (megabytes of base64)
  **cannot** be returned from `GetReadUrlAsync`; it would blow the session cap. Hosted models also cannot fetch a
  `localhost` URL, which is the same limit AppHost documents today. Chosen design (below): the store returns a
  tiny non-fetchable reference URL, and a chat-client wrapper rewrites it to inline `DataContent` at request time.
- `GetReadUrlAsync` must stay deterministic/memoized per blob (gotcha "`GetReadUrlAsync` is memoized per blob",
  ADR 0104): a reference URL is constant, which is *better* for the prompt-prefix cache than a SAS.
- Browser-facing attachment paths already stream bytes through the app (`AgentApi.Attachments.cs:120-131`,
  `AgentApi.Sharing.cs`), they never see a SAS, so they work unchanged on files.
- Artifact bytes must keep being saved as `application/octet-stream` and the proxies keep nosniff
  (gotcha "Artifact blobs are stored as octet-stream"): the file store must persist the *stored media type* and
  never infer one from the file name.
- Blob features actually used (so the file store only needs these): block write with overwrite, append (4 MB
  chunks, `AzureFleetBlobs`/`BlobCopilotSessionStore`), list by prefix (404 → empty), delete, content-type +
  `filename` metadata (attachments only), one create-only upload + lease (gateway). No ETag, tags, or SAS beyond
  attachments.
- Fleet event log is an append blob with a 50,000-block cap that the Azure code works around by coalescing
  (gotcha, `FleetEventLog.FlushAsync`); the file store has no block cap, so the coalescing stays as-is, harmless.
- `Cosmos:DocChunksDimensions` default 3072; `IDocumentIndex.ExpectedDimensions` is validated *before* upsert
  (ADR 0030, gotcha "Cosmos vector search is prod-only"). The local index must keep that contract.
- **pgvector HNSW/IVFFlat cannot index a `vector` above 2000 dimensions** (a 3072-dim `halfvec` can be HNSW-indexed;
  `vector(3072)` can only be scanned exactly). Search is always scoped to one owner (`ownerKey` first parameter of
  every call), so an exact scan over one user's chunks is acceptable for a personal agent. Decision: `vector(N)`
  with N from config, **no ANN index in v1**, btree on `owner_key`. Record it in the ADR as a known limit.
- Cosmos BM25 (`FullTextScore`) has no Postgres equivalent; `ts_rank_cd` ranks differently. Accepted, documented;
  ranked search only has to be "relevant", the existing tests assert behavior on the in-memory twin.
- Auth: with no Easy Auth every request is `local-dev` (`CurrentUser.cs:36,158`). There is no startup guard against
  running public as `local-dev`. Local mode needs `Access__Owners__0=local-dev` to reach `/dashboard` and `/logs`
  (AppHost sets it; `OwnerPolicyTests` pins only the shipped `appsettings.json`, an env override is fine).
- `new ArmClient(credential)` and `DefaultAzureCredential` are constructed unconditionally in Program.cs:76,91 but
  are lazy (no network until used). Leave them; do not widen the diff.
- CI: `pr-checks.yml` jobs run on `ubuntu-latest` (all five), so Testcontainers works in CI. No test today needs
  Docker, so the new Postgres tests must **skip cleanly when Docker is unavailable** (the `Skip = ...` pattern the
  `Live*Tests` use) rather than fail a developer's `dotnet test`.

**Decisions made in the interview / by this plan (state them in the final message):**
- Postgres, not SQLite: Orleans has supported ADO.NET providers for it, pgvector gives vectors, and it is one
  container. (User approved Postgres.)
- Blob side = **filesystem volume**, not MinIO/S3: one fewer container, and the seams use a handful of blob
  features. S3-compatible stays a possible later provider behind the same ports.
- Provider key absent ⇒ `azure`. An unknown value fails startup (never silently falls back).
- **No data migration** between providers (documented).
- **Auth is out of scope** (see Out of scope); compose binds the app to `127.0.0.1` and says so.
- Rejected: an app-served HMAC-signed image URL for model fetches (needs inbound reachability from the model
  provider, an auth-bypass endpoint, and a public base URL). Rejected: returning `data:` URLs (breaks the 2 MB
  session, see above). Rejected: Testcontainers-free tests (the `$type` parity claim cannot be proven without
  a real Postgres).

**Governing ADRs / gotchas (read before the matching step):** ADR 0004 (attachments), 0007 + 0030 (document
index, dimensions), 0039/0040 (flat namespace), 0050/0051 (complexity), 0097/0102/0107 (fleet/Copilot blobs),
0104 (read-URL memo), 0105/0106 (registration, TurnContext), 0111 (gateway lease), 0121 (owner tier). Gotchas:
the entries named in this section's bullets, plus "Cosmos containers must be pre-declared in Bicep" (the local
provider is the opposite: it creates its own schema).

## Steps

Branch from `main @ d164725`: `feature/self-host-local-storage`. Each step ends green on its narrowest test, then
`dotnet build Agent42.slnx` and `dotnet format Agent42.slnx --verify-no-changes`.

**Step 0 — Spikes (gating; use the `spike-dotnet` skill, one throwaway file outside the repo, pinned to the repo's
Orleans 10.3.1).** Record the findings in this spec before Step 4, and in the ADR.
- 0a. Orleans ADO.NET provider on Postgres: (i) can `AdoNetGrainStorageOptions.GrainStorageSerializer` be set to
  `JsonGrainStorageSerializer` so the payload is the same `{"$type":...}` document the Cosmos path writes (and what
  column holds it); (ii) where the PostgreSQL schema scripts (Main/Persistence/Reminders) live in the package and
  how to apply them idempotently at startup (the provider does not create its tables); (iii) a reminder
  register/fire/survive-restart round trip with `Orleans.Reminders.AdoNet`; (iv) a read of a committed golden payload
  (`GrainStorageSerializerTests` fixtures) through the provider.
- 0b. **Decision gate:** if (i)-(iv) hold, use the ADO.NET providers. If not, write a small owned
  `IGrainStorage` + `IReminderTable` over one Npgsql table each (JSON via the same serializer; auto-created schema).
  Say which in the spec before proceeding.
- 0c. Where to rewrite attachment URLs: confirm the narrowest seam for a request-time `UriContent`→`DataContent`
  rewrite (a `DelegatingChatClient` in the per-user factory path, `UserChatClientFactoryProvider` /
  `MultiProviderChatClientFactory`). Every producer of a persisted read URL must be covered: the live-turn site
  `src/Agent42.Agents/Conversations/AgentGrain.cs:1261`, the activation refresh and generated-image sites
  `src/Agent42.Agents/Attachments/AgentGrain.Attachments.cs:110,197`, and `AgentGrain.ImageTurn.cs:94,110`. Establish
  whether images reach the Copilot kind and fleet agents at all (they may not use `UriContent`); the wrapper must be
  present for every provider kind that can carry an image. Decide the over-ceiling behavior now: **an image over the
  per-request ceiling, or past it cumulatively, is replaced by a short text placeholder (oldest first) and the turn
  proceeds**, never failed, since every historical image in a long chat is re-inlined each turn. Confirm the rewrite
  leaves the persisted session untouched.
- Verify: spike prints the persisted row text and the rewritten request content; commit nothing from the spike.

**Step 1 — ADR (`/decide`), written AFTER the Step 0 gate resolves** (so it records the real ADO.NET-vs-owned
outcome). Record: the provider switch, Postgres + files, the reference-URL attachment design, the no-ANN-index limit,
no auth, no migration, and the egress statement (none new). It **amends** (does not supersede) ADR 0001, 0004 and
0111, where Cosmos persistence / Blob attachments / the Blob lease are stated: say so explicitly and add an
"Amended by" note to those three. Check open PRs for ADR-number collisions first (`gh pr list`, the
`adr-numbering` workflow). Amend the ADR at the end if implementation diverges. Verify: ADR lint passes
(`.github/workflows/adr-numbering.yml` rules).

**Step 2 — Provider switch + behavior-preserving refactor.** (blocks 3-7)
- Add `StorageProvider` options (`Storage:Provider`, default `azure`, unknown ⇒ `InvalidOperationException`).
- Introduce `AddStorageProvider(...)` in `Storage/` that dispatches to `AzureStorageRegistration` (the *existing*
  wiring moved mechanically: same registrations, same order) or `LocalStorageRegistration`. **Everything that today
  assumes Azure must sit inside the Azure branch only**, or `docker compose up` fails at startup with no Azure
  config: `builder.AddAzureCosmosClient("cosmos")` (Program.cs:65), the `blobs` / `Storage:BlobEndpoint` client
  and its unconditional throw (:142-154), `PersistKeysToAzureBlobStorage` (:166-173), `AddCosmosGrainStorage` and
  `UseCosmosReminderService` (:449-471), and the Cosmos selection of the two indexes (:209-210; `local` selects
  Postgres in Step 6). Leave the lazy `DefaultAzureCredential`/`ArmClient` (:76,91) untouched.
- **Deliberate change to a pinned test (`ProjectLayoutTests.Hosts_CallAddAgent42Silo`, :727-731).** That test
  source-text-pins `AddSingleton<IAttachmentStore` in each host's `Program.cs` before `AddAgent42Silo();`. Moving
  the registration behind `AddStorageProvider(` makes it fail for Backend. Fix it on purpose, not by accident: make the
  port token per host (Backend: `AddStorageProvider(`; DevHost keeps `AddSingleton<IAttachmentStore`), still asserted
  *before* `AddAgent42Silo()`. `IPushNotificationSender` stays as is. Note this in the MR.
- Update `ProjectLayoutTests.ExpectedFolders` for `Storage/`.
- Verify: `SiloRegistrationTests`, `AgentApiReachabilityTests`, `ChatSearchIndexRegistrationTests` unchanged and
  green; `ProjectLayoutTests` green with the two edits above; new `StorageProviderOptionsTests`, including
  "`local` registers every port with no `CosmosClient` / `BlobServiceClient` and no `cosmos`/`blobs` connection
  string" (resolve `IAttachmentStore`, `ISkillPackageStore`, `IDocumentIndex`, `IChatSearchIndex`, `IFleetBlobs`,
  `ICopilotSessionStore`, `IChatGatewayLease` from a ServiceCollection built by `AddStorageProvider(local)`; nothing
  in the suite executes `Program.cs`, so the helper must be testable on its own).

**Step 3 — Filesystem blob side** (3a first; 3b and 3c parallelizable after 3a **for the implementation files only**:
every step in 3-6 adds lines to `LocalStorageRegistration`, so the main session applies those registration edits
serially; parallel `step-implementer`s need worktree isolation per the working agreement).
- 3a. `LocalFileStore` primitive in `Storage/`: root from `Storage:Local:Root`; one `Resolve(key)` that rejects
  rooted paths, `..`, and reserved names, and is the **only** place a key becomes a path; atomic write (temp +
  rename); append; prefix list (missing dir ⇒ empty); delete. Verify: `LocalFileStoreTests` incl. traversal cases.
- 3b. `FileSkillPackageStore`, `FileFleetBlobs`, `FileCopilotSessionStore` over the primitive, plus Data Protection
  `PersistKeysToFileSystem(<root>/dataprotection)`. Register in the local branch (skills store must be registered
  explicitly: it is a transitive dependency, `AddAgent42Silo()` will not catch its absence). Verify: contract tests
  that run the same cases against the file store and the existing `InMemory*` twin (put/get/append/list/delete,
  404 ⇒ null/empty), a restart test (new store instance over the same dir sees the data), and
  `FileDataProtectionTests`: two `IDataProtectionProvider`s built over the same directory unprotect each other's
  payload (a `ProviderSecretProtector` round trip), proving the key ring persists.
- 3c. `FileAttachmentStore`: stores bytes + a sidecar record (stored media type, file name); `CopyAsync` copies;
  `GetReadUrlAsync` returns a constant reference URL (`agent42-attachment://{ownerKey}/{id}`-shaped, exact scheme per
  Step 0c) so it stays memoizable and tiny; the chat-client wrapper from 0c resolves it to `DataContent` at request
  time via `IAttachmentStore.OpenReadAsync`, with the ceiling/placeholder behavior decided in 0c.
  Register the wrapper **only** when the provider is `local` (azure behavior byte-for-byte unchanged).
  Verify: `FileAttachmentStoreTests`, `AttachmentUrlRewriteTests` (the persisted session still holds the reference
  URL; the request sent to a fake provider holds bytes; an over-ceiling image becomes a placeholder and the turn
  proceeds; the wrapper is applied for **each provider kind** the factory builds, asserted by enumerating the kinds),
  and the existing `BlobAttachmentStoreReadUrlTests` untouched.

**Step 4 — Postgres grain storage + reminders.** (depends on 0b, 2; parallel with 3, 5)
- Packages via `dotnet add package` (Npgsql, and per 0b the Orleans ADO.NET persistence/reminders packages).
  `NpgsqlDataSource` singleton from `ConnectionStrings:postgres`.
- Schema bootstrap (idempotent, runs before the silo starts, advisory-locked so two starts cannot race).
- `AddAdoNetGrainStorage(AgentGrain.StorageProviderName, ...)` + reminder service, JSON serializer per 0a, in the local
  branch only; azure branch unchanged.
- Tests (Testcontainers Postgres, skip when no Docker): a `PostgresClusterFixture` running a real silo against it;
  (1) `GrainState_IsPersistedAsJson_WithAssemblyQualifiedTypeNames` equivalent reading the raw row; (2) the committed
  pre-move/golden payloads from `GrainStorageSerializerTests` inserted raw and read back through a grain
  (`DocumentWrittenByAnEarlierDeployment_StillDeserializes` shape); (3) `PersistedTypeName_StillResolves` over all 23
  state roots; (4) the collection-expression hazard and `bool = true` flip cases run through the Postgres path;
  (5) a reminder survives a silo restart. Reuse the fixture literals; do not copy them (extract to a shared
  `GoldenStates` accessor if needed, `GoldenStates.cs` already exists).
- Verify: `PostgresGrainStorageTests` green with Docker. **Skip gate check:** run the new Postgres test classes once
  with Docker stopped and record that they report *Skipped* (not failed); this is a verification step, not a mapped AC.

**Step 5 — Gateway lease on Postgres.** `PostgresChatGatewayLease : IChatGatewayLease` using `pg_try_advisory_lock`
on a dedicated held connection (renew = connection still alive and lock still held; release = unlock + dispose).
The contract to honor (`src/Agent42.Backend/ChatGateway/ChatGatewayLease.cs:14-30`): `RenewInterval` and `Duration`
properties, `AcquireAsync` completes only once held (the Blob one retries on a held lease), `RenewAsync` returns
false when the lease is lost (the service then drops every session, `ChatGatewayServiceTests.LostRenewal_DisconnectsEverything`),
`ReleaseAsync` is graceful. Read it before writing the class. Register with
the same `TryAdd` precedent (`ChatGatewayRegistration` TryAdds, a host's earlier registration wins). Verify:
`PostgresChatGatewayLeaseTests` (second contender blocked, handover after release, lost connection ⇒ renew false).

**Step 6 — Postgres document index + chat search index.** (depends on 4's schema bootstrap)
- `PostgresDocumentIndex : IDocumentIndex` (pgvector `vector(N)`, N = `Storage:Local:DocChunksDimensions` default
  3072, cosine `<=>`, `ExpectedDimensions` = N so the pre-upsert dimension check keeps working, owner-scoped on every
  query, all values parameterized). `PostgresChatSearchIndex : IChatSearchIndex` (substring via case-insensitive
  match; ranked via `tsvector`/`ts_rank_cd` with caller terms **parameterized, never concatenated into the
  tsquery**; same `DocumentId` sanitation as the Cosmos index; per-owner delete; ordinal window).
- Selection mirrors `DocumentIndexRegistration` / `ChatSearchIndexRegistration` (provider `local` ⇒ Postgres).
- Verify: the existing `DocumentIndexTests` / `ChatSearchIndexTests` cases re-run against the Postgres impls
  (parametrized, Testcontainers pgvector image), plus a tsquery-injection test (terms like `a' | b`, `!`, `:*`).

**Step 7 — Config health + safety rails.** `ConfigHealth`: when provider is `local`, `Storage:BlobEndpoint` and
`AzureOpenAI:TenantId` are NotApplicable regardless of environment, and add a `Storage:Provider` row; log one clear
startup warning in `local` mode that there is no authentication and every caller is `local-dev` (a small
`LocalModeWarning` helper called from `AddStorageProvider`/host start, so it is testable). Verify:
`ConfigHealthTests` for both providers in Production env, and `LocalModeWarningTests` (the warning is logged in
`local`, absent in `azure`).

**Step 8 — Compose + docs.** `compose.yaml`: `app` (builds `src/Agent42.Backend/Dockerfile`), `postgres` (pgvector
image, healthcheck, volume), `searxng` (build `infra/searxng`, same env as `AppHost.cs`), named volume for
`Storage:Local:Root` and for `Copilot:WorkspaceRoot` (decide per Step-0-style check of ADR 0107: workspace root
unset behavior; otherwise set `Copilot:BuiltInToolsEnabled=false` for local and say why), app port bound to
`127.0.0.1`, `Access__Owners__0=local-dev`, `Access__Owners__1=`, `Storage__Provider=local`. A `.env.example` with
the secrets (`POSTGRES_PASSWORD`, `SEARXNG_SECRET`). **Required env the app throws without** (verified):
`WebSearch__Endpoint` (`Program.cs:215-221`, eager) with SearXNG's `SEARXNG_PORT=8888`, `SEARXNG_SECRET` and
`SEARXNG_BASE_URL=<its own endpoint>/searxng/` exactly as `AppHost.cs:34-46` does (ADR 0059 subpath);
`ConnectionStrings__postgres`; `Storage__Provider=local`; `Storage__Local__Root`.
**Volume ownership:** the image ends with `USER $APP_UID` (Dockerfile:71), so a fresh named volume (root-owned) is
unwritable. Fix in compose (a one-shot init service that `chown`s the volumes to the image's `APP_UID`, or a
`user:` the volume allows) and prove it in the smoke by writing to both volumes.
`docs/features/self-hosting.md` (how to run, what is and is not different from Azure, limits: no auth, no
migration, no ANN index, reminders/signal notes, SearXNG egress) and a README "Self-host with Docker" section;
every path mentioned must exist (CI doc-path check, `pr-checks.yml:92`).
`DeployTriggerTests` and `QualityGateTests` need no change: `compose.yaml` and `.env.example` are not Dockerfile
COPY sources, and new files under `src/Agent42.Backend` are already inside the deploy path filter (confirm by running
both tests, not by assumption).
Verify: `ComposeFileTests` parses `compose.yaml` with YamlDotNet (resolve with `dotnet add package`, test project
only) and asserts: the app port is bound to `127.0.0.1`, a healthcheck exists on postgres, and a fixed list of
required env keys is present (`Storage__Provider`, `ConnectionStrings__postgres`, `Storage__Local__Root`,
`WebSearch__Endpoint`, `Access__Owners__0`). It does **not** try to prove "every key is read by code" (a source
grep would be brittle). The real boot proof is a **scripted smoke recorded in the MR**:
`docker compose up -d --build`; wait for healthy; `curl -fsS` the UI root and the ServiceDefaults health endpoint
(confirm the mapped path in `src/Agent42.ServiceDefaults`); add a provider, send a message, `docker compose restart app`,
confirm history persists; upload an image and a document; check both volumes received files.

**Step 9 — Doc debt (`/sync-docs`).** Add gotchas (local JSON `$type` parity; pgvector 2000-dim index limit;
persisted read URL ⇒ no `data:` URLs; file-store key traversal guard is the only key→path point); a CLAUDE.md
feature-map row for self-hosting; the CLAUDE.md **Stack** line ("Orleans ... Cosmos persistence + reminders") and
the rule-12 single-replica list (add the advisory-lock lease and the local file stores); `docs/features` index; the
"Amended by" notes on ADR 0001/0004/0111; fix any doc claim the diff invalidated (e.g. "Cosmos" as the only grain
store). Save a project memory note. Run `dotnet test Agent42.slnx`, `dotnet format ... --verify-no-changes`,
and the new-code coverage script, then open the MR.

## Acceptance criteria → test mapping

| AC | Test | File |
|---|---|---|
| AC1 `Storage:Provider` absent ⇒ azure; `azure`/`local` accepted; unknown fails startup | `StorageProviderOptionsTests` | `tests/Agent42.Tests/StorageProviderOptionsTests.cs` (new) |
| AC2 azure wiring unchanged (same ports registered, `Hosts_CallAddAgent42Silo` still passes, Cosmos/Blob selection tests green) | existing tests, unmodified, plus `AzureProvider_RegistersTheSamePorts` | `ProjectLayoutTests.cs`, `ChatSearchIndexRegistrationTests.cs`, new `StorageProviderOptionsTests.cs` |
| AC3 local grain state is the same `$type` JSON; golden pre-move payloads and all 23 state roots read through Postgres | `PostgresGrainStorageTests` | `tests/Agent42.Tests/PostgresGrainStorageTests.cs` (new) |
| AC4 JSON-path hazards hold on Postgres (collection expression, `bool = true`) | same class, reusing `GrainStorageSerializerTests` literals | `PostgresGrainStorageTests.cs` |
| AC5 reminders persist across a silo restart on Postgres | `PostgresReminderTests` | `tests/Agent42.Tests/PostgresReminderTests.cs` (new) |
| AC6 file stores satisfy the same contract as the in-memory twins and survive restart | `FileBlobStoreContractTests` | `tests/Agent42.Tests/FileBlobStoreContractTests.cs` (new) |
| AC7 no key can escape the storage root | `LocalFileStoreTests` (traversal, rooted, reserved names) | `tests/Agent42.Tests/LocalFileStoreTests.cs` (new) |
| AC8 attachments: persisted session holds a tiny reference URL, the model request holds the bytes, URL is memoizable/constant | `AttachmentUrlRewriteTests`, `FileAttachmentStoreTests` | `tests/Agent42.Tests/AttachmentUrlRewriteTests.cs`, `FileAttachmentStoreTests.cs` (new) |
| AC9 gateway lease: exclusive, handover after release, lost connection ⇒ renew false | `PostgresChatGatewayLeaseTests` | `tests/Agent42.Tests/PostgresChatGatewayLeaseTests.cs` (new) |
| AC10 document index on Postgres: owner-scoped, dimension mismatch rejected before upsert, most-similar-first | `PostgresDocumentIndexTests` (parametrized from `DocumentIndexTests` cases) | `tests/Agent42.Tests/PostgresDocumentIndexTests.cs` (new) |
| AC11 chat search on Postgres: substring, ranked, window, delete; tsquery injection-safe | `PostgresChatSearchIndexTests` | `tests/Agent42.Tests/PostgresChatSearchIndexTests.cs` (new) |
| AC12 config health NotApplicable for Azure keys under `local` in Production | `ConfigHealthTests` additions | `tests/Agent42.Tests/ConfigHealthTests.cs` |
| AC13 compose: loopback bind, env keys exist in code, healthchecks, doc paths exist | `ComposeFileTests` | `tests/Agent42.Tests/ComposeFileTests.cs` (new) |
| AC14 Data Protection key ring persists on the filesystem | `FileDataProtectionTests` | `tests/Agent42.Tests/FileDataProtectionTests.cs` (new) |
| AC15 local mode logs the no-auth warning; azure does not | `LocalModeWarningTests` | `tests/Agent42.Tests/LocalModeWarningTests.cs` (new) |
| AC16 `local` registers every port with no Azure client or connection string; unknown provider fails | `StorageProviderOptionsTests` (`Local_ResolvesEveryPort_WithoutAzure`, `UnknownProvider_Throws`) | `StorageProviderOptionsTests.cs` |
| AC17 a deliberate, narrow pin change: Backend host still registers the storage ports before `AddAgent42Silo()` | `Hosts_CallAddAgent42Silo` (per-host port token) | `tests/Agent42.Tests/ProjectLayoutTests.cs` |

(Postgres tests skip, not fail, without Docker: shared `PostgresFixture.cs`, checked once by hand in Step 4.)

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet × git diff); Postgres tests only run where Docker exists, so this number is authoritative on CI (`ubuntu-latest`), never estimate it locally |
| `cognitive_complexity_max` | 15 | `SonarAnalyzer.CSharp` S3776 at build (error) |
| `dependencies.vulnerable_packages` | 0 | CI `package-freshness` job |
| `dependencies.add_policy` | latest-stable | `dotnet add package`, never a recalled version |
| `branches.protected` | `[main]` | `.claude/hooks/protect-main.mjs`; work on `feature/self-host-local-storage` |

## Out of scope

- **Authentication for self-hosters** (reverse-proxy header injection, OIDC): its own ADR + plan. Local mode is
  loopback-only by default and documents that anything beyond localhost needs an authenticating proxy; note that
  `CurrentUser` already reads `X-MS-CLIENT-PRINCIPAL-ID`, which a proxy could set, and rule 14 says that header is
  forgeable by the Copilot shell, so it is advisory.
- Any change to `infra/`, the deploy workflow, prod config, or Azure RBAC.
- Data migration between Azure and local; moving prod off Cosmos/Blob.
- S3/MinIO provider, an ANN index for 3072-dim vectors, multi-replica local mode.
- Making the Aspire AppHost use Postgres; DevHost stays on in-memory.
- Enabling Signal in local mode (the signal-cli store is a volume + the `signalEnabled` gate, untouched).
- Replacing Cosmos BM25 ranking parity; a model-fetchable public attachment URL.

## Review

Critic verdict on the first draft: **not clean** (1 blocker, 2 high, 6 medium/low). I verified the blocker and the
third persisted-URL site against the tree before acting.

| # | Finding | Handling |
|---|---|---|
| 1 | Blocker: moving `AddSingleton<IAttachmentStore` out of `Program.cs` fails `Hosts_CallAddAgent42Silo` | **Accepted** (verified at `ProjectLayoutTests.cs:727-731`). Step 2 now changes the pin deliberately, per host, still before `AddAgent42Silo()`; AC17 |
| 2 | Cosmos client / blob client / DP / Orleans Cosmos blocks are not gated | **Accepted.** Step 2 lists each as Azure-branch-only; AC16 resolves every port with no Azure config |
| 3 | `AgentGrain.cs:1261` persisted-URL site missing; no all-provider-kinds test; over-ceiling behavior unspecified | **Accepted** (verified). Step 0c lists all sites, Step 3c tests each provider kind, over-ceiling = oldest-first placeholder, turn proceeds |
| 4 | `WebSearch__Endpoint` eager check, SearXNG base URL, `USER $APP_UID` volume ownership | **Accepted.** Step 8 |
| 5 | AC14 mis-mapped; DP, warning, lease contract, hosted unknown-provider untested | **Accepted.** New ACs 14-16, Step 5 states the lease contract, skip gate moved to a manual Step 4 check |
| 6 | `ComposeFileTests` needs a YAML parser; "keys read by code" is brittle | **Accepted** (YamlDotNet, fixed key list; the grep claim dropped) |
| 7 | Manual smoke not a gate; Copilot workspace root deferred | **Partly accepted.** The smoke is now a scripted command list recorded in the MR; the Copilot workspace-root decision stays a Step 8 check (ADR 0107) because it needs a runtime read of unset behavior |
| 8 | `DeployTriggerTests`/`QualityGateTests` dangling | **Accepted.** Step 8 says no change expected and to confirm by running both |
| 9 | ADR before spike result; parallelism hazard in `LocalStorageRegistration` | **Accepted.** ADR is Step 1 after the gate; registration edits serial |
| 10 | Wrong path for `AgentGrain.Attachments.cs` | **Accepted** |
| 11 | ADR amends not supersedes; Stack line and rule 12 doc debt | **Accepted.** Steps 1 and 9 |
| 12 | ADO.NET serializer/script behavior unverified | **Not changed:** acceptable because Step 0 gates it, as the critic noted |

## Execution notes: Step 0 spike results (2026-11-14)

Spike run against `pgvector/pgvector:pg17`, Orleans 10.3.1, Npgsql 10.0.3 (a file-based app outside the repo).

- **0a(i) serializer:** with no explicit `GrainStorageSerializer` the ADO.NET provider already writes the same JSON as the
  Cosmos path: `{"$id":"1","$type":"SpikeState, spike0a","Value":"hello","Poly":{"$id":"2","$type":"Derived, spike0a",...},"List":{"$type":"System.Collections.Generic.List`1[[System.String, ...]]","$values":["a"]}}`,
  stored as UTF-8 text bytes in the `payloadbinary bytea` column (there is no JSON column). Row key: `graintypestring` = the state
  name, `grainidextensionstring` = the grain key, `version` = 1 after the first write.
- **0a(ii) schema:** the NuGet packages ship NO SQL scripts. They live in `dotnet/orleans` at tag `v10.3.1`
  (`src/AdoNet/Shared/PostgreSQL-Main.sql`, `Orleans.Persistence.AdoNet/PostgreSQL-Persistence.sql`,
  `Orleans.Reminders.AdoNet/PostgreSQL-Reminders.sql`) and are NOT idempotent (second run: `42P07 relation ... already exists`),
  so the app embeds them and runs them once under an advisory lock, guarded by an `OrleansQuery` existence check.
- **0a(iii) reminders:** register, restart the silo, the reminder row is still there and fires (`REMINDER FIRED after restart: 1`).
- **0a(iv) golden payloads:** not spiked; proved in Step 4's `PostgresGrainStorageTests` (inserting the committed fixtures raw).
- **0b decision: use the ADO.NET providers** (`Microsoft.Orleans.Persistence.AdoNet`, `Microsoft.Orleans.Reminders.AdoNet`), no owned `IGrainStorage`.
- **File-based spike gotcha:** .NET 10 file-based apps default `PublishAot=true`, which breaks Orleans' serializer at runtime
  (`Dynamic code generation is not supported`); add `#:property PublishAot=false`.
- **0c:** the rewrite seam is `MultiProviderChatClientFactory.Build`, beside `ToolImageChatClient` (below OTel), which every provider
  kind and every `IChatClient` consumer (chat, fleet agents) goes through. Neither the Copilot kind nor the fleet touches
  `IAttachmentStore`, so images never reach them. Built as `AttachmentInliner` / `AttachmentInlineChatClient`; limits default to
  4 MB per image and 16 MB per request, configurable.

## Where the implementation diverged from the plan

- **Step 0b:** ADO.NET providers, as the spike showed (see Execution notes). The embedded Orleans scripts are applied per table, under an advisory lock.
- **Step 2:** `AddStorageProvider` takes an `IHostApplicationBuilder` (the Azure branch needs `AddAzureCosmosClient`), and a second method `UseStorageProvider` covers the Orleans half. `AddCopilotAgents` and `AddAgentFleet` switched `AddSingleton` to `TryAddSingleton` so the local stores registered earlier win. `AddStorageProvider` sits where the blob block was, and the skill store and index registrations moved into it.
- **Step 3c:** the rewrite wrapper sits just ABOVE `ToolImageChatClient`, not beside it as the last `.Use`: `McpToolImageTests.ToolImageChatClient_SitsBelowOpenTelemetry` pins the tool-image wrapper as the last one. Limits default to 4 MB per image and 16 MB per request. Attachment metadata lives in a separate `attachment-meta/` folder.
- **Step 4:** the new tests drive the registered `agent-state` storage provider and `IReminderTable` of a real silo directly, not through the app's grains. Real bugs found by them: Npgsql needs `ReloadTypesAsync` after `CREATE EXTENSION vector` (a fresh database failed until restart), and the provider's row version conflicts on a second write through a fresh `GrainState`.
- **Step 6:** the `DocumentIndexTests` and `ChatSearchIndexTests` cases were PORTED into Postgres test classes (xunit 2 has no per-subclass runtime skip, so an inherited contract class could not skip without Docker). The pure-function cases (`CapText`, `SnippetAround`) were not duplicated. The `DocumentId` sanitation is not needed: the primary key is a composite of parameters.
- **Step 7/8 additions:** compose sets `Copilot__BuiltInToolsEnabled=false` (an auto-approved shell could read the DB password and key ring for an unauthenticated caller), and `GSS Encryption Mode=Disable` on the connection string. The doc-path check found one path already broken on `main` (ADR 0126), fixed here.
- **Smoke (Step 8), run locally 2026-11-14:** `docker compose up -d --build` OK; UI root 200; `/health` is 404 in the Production environment (it is Development-only, so the plan's health check is the UI root); profile PUT, then app restart, GET returns the same row; image upload stored under `/data/attachments`, served 200 `image/png` before and after the restart; both volumes writable by uid 1654. NOT run: a chat turn, a document upload (both need a model endpoint), a reminder firing, a Copilot chat.
- **Skip gate (Step 4):** with `DOCKER_HOST=tcp://127.0.0.1:1`, the Postgres classes reported 9 Skipped, 0 Failed.
- **Local tooling:** this machine has SDK 11.0.100, where `dotnet build Agent42.slnx` fails in `tools/Agent42.DevHost` (a StaticWebAssets error that also occurs on a clean `origin/main`); the test project was run directly.
