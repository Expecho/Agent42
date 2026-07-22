# Plan — User-configured embedding model (remove the operator embedding prerequisite)

planned-against: main @ 8404190

## Overview

Today the document-RAG embedding model is an **operator prerequisite**: a fixed
`text-embedding-3-large` deployment authenticated by `AzureOpenAI:ApiKey` +
`AzureOpenAI:EmbeddingDeploymentName`, both **fatal-if-blank at startup**
(`src/Agent42.Backend/Program.cs:58,189`). It is the last remaining "availability
floor" — the exact opposite of chat, which ADR 0019 made fully per-user.

Goal: make the embedding model a **normal user-configured model** (added like any
other model under a provider, ADR 0018) and **chosen as the user's "default
embedding model" in Preferences** — mirroring the chat "default model" selector.
Remove the operator embedding floor entirely. After this change the app boots with
**no embedding capability** until a user configures one; with no embedding model the
composer **does not allow attaching documents** (images still allowed), the server
rejects any document upload as defense-in-depth, and retrieval on an existing document
conversation **degrades to empty context** (never throws). This also removes the
`AZURE_OPENAI_API_KEY` operator secret that the security review flagged.

**Architecture (revised after critic):** follow the ADR 0020 `SupportsImageGeneration`
exemplar exactly — extend the **existing** per-user `IChatClientFactory` with a
`GetEmbeddingGenerator()` (built as a `Func` on the resolved `Provider`, like
`GetImageGenerationService`), consumed via the already-resolved `_chatClientFactory` /
`IUserChatClientFactoryProvider.GetForUserAsync(userId)`. **No new provider interface and
no new every-host registration** (the earlier draft's parallel `IUserEmbeddingGeneratorProvider`
seam is rejected — it re-created the register-in-every-host ripple that has repeatedly bitten
`IMcpSecretProtector`/`PushConfig`/`IUserMcpToolProvider`).

## Affected areas (verified against the tree @ 8404190)

**Data model / config records**
- `src/Agent42.Agents/UserProviderConfig.cs` — `StoredModel` (add `SupportsEmbeddings`, trailing `[Id(5)]`), `UserProviderConfig` (add `EmbeddingDefault`, trailing `[Id(3)]`); update `UserProviderConfig.Empty` (`:58`) trailing arg
- `src/Agent42.Backend/ModelProviderOptions.cs` — `ConfiguredModel` (add `SupportsEmbeddings`)
- `src/Agent42.Backend/ModelCatalog.cs` — `ModelInfo` (add `SupportsEmbeddings`; `CompositeModelCatalog` merge)

**Overlay grain**
- `src/Agent42.Agents/IUserProviderConfigGrain.cs`, `src/Agent42.Agents/UserProviderConfigGrain.cs` — `SetEmbeddingDefaultAsync`

**Resolver**
- `src/Agent42.Backend/UserProviderResolver.cs` — `ResolvedProviderSet` gains `EmbeddingDefaultModelId`; carry `SupportsEmbeddings` into `DeclaredModels`

**Embedding on the existing factory (mirror `GetImageGenerationService`)**
- `src/Agent42.Agents/IChatClientFactory.cs` — add `string DefaultEmbeddingModelId { get; }` + `IEmbeddingGenerator<string, Embedding<float>> GetEmbeddingGenerator()`
- `src/Agent42.Backend/ChatClientFactory.cs` — `MultiProviderChatClientFactory` builds the generator per kind (Foundry `GetEmbeddingClient(model).AsIEmbeddingGenerator()` / OpenAICompatible `EmbeddingClient`), reusing the `Provider` `Func` shape at `:162`; `NoEmbeddingGenerator` for unset/unknown
- offline `SingleChatClientFactory` (`tools/Agent42.DevHost/Program.cs:34`) — `GetEmbeddingGenerator()` returns the `DeterministicEmbeddingGenerator`; same in the test single-user factory

**Call-site rewiring**
- `src/Agent42.Backend/DocumentIngestionService.cs` (`:17` injection → `IUserChatClientFactoryProvider`; `:36` embed call) — derive `userId` from `ownerKey`
- `src/Agent42.Agents/AgentGrain.cs` — drop `IEmbeddingGenerator` ctor param (`:42,105,117`); retrieval at `:1144` uses `_chatClientFactory.GetEmbeddingGenerator()`; degrade-to-empty guard around `:368`/`:1144`
- `src/Agent42.Agents/IDocumentIndex.cs` + `src/Agent42.Backend/CosmosDocumentIndex.cs` + `src/Agent42.Agents/InMemoryDocumentIndex.cs` — `ExpectedDimensions`
- `src/Agent42.Backend/AgentApi.cs` — `IngestAsync` call sites `:148` (browser) **and `:365` (agent-run)**; new `PUT /api/default-embedding-model`; `ProvidersResponse` (`:974`); `/api/models` (`:203`); provider save/response per-model `SupportsEmbeddings`
- `src/Agent42.Backend/ConfigHealth.cs` — remove operator embedding presence checks (`:62–66`) and the singleton probe (`:257,284–289`)

**Registration**
- `src/Agent42.Backend/Program.cs` — remove fatal checks (`:44–48,58–62,189–196`) and the operator embedding-generator singleton (`:200–202`); **keep** the `DefaultAzureCredential` `TenantId` read (`:70–73`). No new registration (factory already registered).
- `tools/Agent42.DevHost/Program.cs` (`:34,92`), `tests/Agent42.Tests/TestServiceRegistration.cs` (`:20`) — the single-user factory grows `GetEmbeddingGenerator`; the standalone `DeterministicEmbeddingGenerator` singleton stays only if still needed by the factory

**Infra / config**
- `src/Agent42.Backend/appsettings.json` (AzureOpenAI section), `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml`
- `infra/modules/cosmos.bicep` (`:135` — 3072 dims, read not changed)

**Docs (same MR)** — new ADR; `.agentic/gotchas.md`; `CLAUDE.md`; `docs/features/document-qa.md`

## Context & constraints (what the executor can't cheaply re-derive)

1. **The 3072-dim wall is fixed and load-bearing.** The prod `doc-chunks` vector policy is
   baked at `dimensions: 3072` (`infra/modules/cosmos.bicep:135`, `quantizedFlat`, cosine).
   **User decision: validate-and-reject** — reject an ingest whose generated vector length ≠
   the container's expected dimension, with a clear message. Do **not** parameterize the
   container. (Gotcha "Cosmos vector search is prod-only"; ADR 0007.)

2. **Changing the embedding model invalidates existing vectors.** **User decision: out of
   scope for code** — document as delete-to-reingest. No per-chunk model tagging.

3. **User decision: fully remove the operator embedding floor.** No `AzureOpenAI:*` embedding
   default; embeddings entirely per-user overlay (symmetric with ADR 0019 for chat).

4. **The "unset embedding model" case has TWO paths and must NOT throw on retrieval** *(critic
   HIGH 2)*. Upload calls the generator (`DocumentIngestionService`), and **so does every turn
   of an existing document conversation**: `AgentGrain.cs:368` runs the `TextSearchProvider`
   whenever `DocumentNames.Count > 0`, embedding the query at `:1144` **before the model call**.
   A `NoEmbeddingGenerator` that throws would brick the whole turn — not just upload — for any
   conversation whose embedding default was later cleared/changed. **Upload** may surface a clear
   4xx; **retrieval must degrade to `[]` (empty context), never throw** — mirroring ADR 0019's
   "no-model chat yields a notice, never throws" (gotcha "Chat is fully per-user"). Guard on an
   empty `DefaultEmbeddingModelId` before embedding, and/or catch the typed unset error and return
   no context.

5. **`IngestAsync` must take `userId` from `ownerKey`, never `GetUserId`** *(critic HIGH 1)*.
   There are two ingestion sites: `AgentApi.cs:148` (browser) and **`:365` (the `/api/agent-run`
   handler on the Easy-Auth `excludedPaths` route)**. The agent-run path has **no Easy-Auth
   principal** — `AgentApi.cs:278` states `GetUserId` would return `local-dev` there, and `ownerKey`
   (`:345`) is built from `owner.UserId`. So derive `userId` inside `IngestAsync` by splitting
   `ownerKey` on the first `/` (same as `AgentGrain.UserIdFromGrainKey`) — no new `userId` param, no
   `GetUserId` at either site. (Gotcha: "an `excludedPaths` endpoint must take userId from the
   directory resolution, never `GetUserId`".)

6. **`AzureOpenAI:*` has MULTIPLE live consumers — do not naively delete** *(critic MEDIUM 4)*.
   Two survive embedding removal: (a) the **Foundry-inheritance seam**
   `ModelProviderOptions.cs:173–180` reads `Endpoint/ApiKey/SubscriptionId/ResourceGroup/AccountName`
   (dormant — baseline `Models:Providers` = `{}` since ADR 0019 — but live code, returns blanks not
   crash; ADR 0018 gotcha); (b) **`Program.cs:70–73` builds `DefaultAzureCredential` with
   `TenantId = config["AzureOpenAI:TenantId"]`**, used for blob-storage MI + ARM, which **must
   stay**. Therefore: remove the embedding-only `EmbeddingDeploymentName` and the fatal startup
   checks; **keep `TenantId`**; `ApiKey`/`Endpoint` are then read only by the dormant inheritance —
   the `AZURE_OPENAI_API_KEY` secret + `deploy.yml` wiring may be removed **only after** grep-confirming
   no baseline provider needs it. The pruning rule is per-key by consumer, not "keep what
   `ModelProviderOptions` reads".

7. **Mirror ADR 0018 machinery exactly** — grain state stays `"providers"`
   (`UserProviderConfigGrain.cs:25`), version bumped per write (`BumpAsync`); `EmbeddingDefault`
   trailing `[Id(3)]`, `SupportsEmbeddings` trailing `[Id(5)]` → **zero migration** (verified:
   `StoredModel` max `Id(4)`, `UserProviderConfig` max `Id(2)`). `PUT /api/default-embedding-model`
   mirrors `PUT /api/default-model` (`AgentApi.cs:491–503`): 200-char cap, **no catalog validation**,
   204. Offline single-user factory wraps `DeterministicEmbeddingGenerator` (128-dim), so RAG
   round-trips with no Azure; the in-memory index has **no** `ExpectedDimensions` (null → no offline
   check).

8. **`SupportsEmbeddings` mirrors ADR 0020 `SupportsImageGeneration`** — a declared, opt-in
   per-model flag (no discovery), built into the **existing** per-user factory as a `Func` on
   `Provider`, consumed through the already-resolved `_chatClientFactory`. Two client branches only
   (AzureOpenAI / OpenAICompatible), reusing `ChatClientFactory.cs:176–237`; keep client
   construction shared to stay under the 3% duplication gate.

9. **ConfigHealth loses its embedding checks; visibility moves to the providers tab** *(critic
   MEDIUM 3)*. `ConfigHealth.BuildStatic` is pinned pure `(IConfiguration, IHostEnvironment,
   PushConfig)` — no HTTP, no DI, no grain (ADR 0029 gotcha), so it **cannot** report a per-user
   embedding default, and after floor-removal there is **no operator embedding config left to
   check**. Remove the `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName` presence checks and the
   singleton `IEmbeddingGenerator` probe (`:257,284–289`) entirely (also update
   `ConfigHealthProbeTests.cs:29`, which registers that singleton). The "is an embedding model
   configured?" signal is surfaced by the new embedding-model selector in the Model providers tab,
   not by config-health. Record this shape change in the ADR.

10. **Composer blocks document attachment when no embedding model** *(user requirement)*. The
    client knows the embedding default from `GET /api/providers` (`EmbeddingDefault`, Step 7). When it
    is empty, `handleFiles` (`app.js`) must **reject document media types** (PDF/Office/text — the RAG
    ingest set) with a clear message pointing to Preferences, while **still accepting images** (images
    never touch embeddings). This is the primary UX gate; the server 4xx (constraint 4/5) stays as
    defense-in-depth and covers the non-UI `/api/agent-run` path. The **Web Share Target** path (ADR
    0023) funnels shared files through the same `handleFiles`, so it inherits the gate — a shared
    document with no embedding model is rejected there too.

11. **This needs an ADR** (`/decide`): amends 0007 (embedding no longer an operator prerequisite),
    0017 (`AzureOpenAI:ApiKey` no longer authenticates embeddings / not fatal), 0019 (removes the
    *embedding* floor 0019 kept); extends 0018 to embeddings; records the ConfigHealth shape change.

## Steps

> 1→4 are a dependency chain. 5 and 6 both depend on 4 and both touch the retrieval/ingest wiring
> — run **sequentially**. 9 depends on 7. 10 is last.

**Step 1 — Data model + flags.** `SupportsEmbeddings` on `StoredModel`/`ConfiguredModel`/`ModelInfo`;
`EmbeddingDefault` on `UserProviderConfig` (+ fix `Empty`); thread through `CompositeModelCatalog`.
*Verify:* `dotnet build`; `ModelCatalogTests`, `ModelProviderTests`.

**Step 2 — Grain `SetEmbeddingDefaultAsync`** (mirror `SetDefaultAsync`, bump version).
*Verify:* `UserProviderConfigGrainTests` (set persists + version bumps).

**Step 3 — Resolver `EmbeddingDefaultModelId`.** Overlay `EmbeddingDefault` if it names a declared
embedding-capable model, else `""`; carry `SupportsEmbeddings` into `DeclaredModels`.
*Verify:* `UserProviderResolverTests` (capable id → id; non-embedding id → ""; none → "").

**Step 4 — Embedding on the existing factory.** `IChatClientFactory` gains `DefaultEmbeddingModelId`
+ `GetEmbeddingGenerator()`; `MultiProviderChatClientFactory` builds per kind (shared client
construction; `NoEmbeddingGenerator` for unset); offline/test single-user factory returns the
`DeterministicEmbeddingGenerator`. **No new interface, no new host registration.**
*Verify:* `ModelProviderTests` (builds per kind; unset → `NoEmbeddingGenerator` throws a clear,
message-bearing error on use; version change flows via the existing version-cached factory).

**Step 5 — Rewire ingestion + dimension validation.** `IDocumentIndex.ExpectedDimensions`
(`CosmosDocumentIndex` reads new `Cosmos:DocChunksDimensions`, default 3072; `InMemoryDocumentIndex`
→ null). `DocumentIngestionService` injects `IUserChatClientFactoryProvider`, derives `userId` from
`ownerKey`, resolves `GetForUserAsync(userId).GetEmbeddingGenerator()`, and rejects a batch whose
vector length ≠ `ExpectedDimensions` with a typed, message-bearing exception. Both upload endpoints
(`:148,365`) map "unset"/"dimension mismatch" to a clear 4xx — **no `GetUserId` at the agent-run
site**. *Verify:* `DocumentIndexTests` / new ingestion test (wrong-length rejected; unset → clear
error; 3072 happy path; agent-run ownerKey resolves the right user).

**Step 6 — Rewire AgentGrain retrieval (degrade-to-empty).** Drop the `IEmbeddingGenerator` ctor
param; retrieval at `:1144` calls `_chatClientFactory.GetEmbeddingGenerator()`; when
`DefaultEmbeddingModelId` is empty (or the generator signals unset), return `[]` context — never
throw. *Verify:* offline RAG round-trip still green; a grain test that a document conversation with
**no** embedding default still answers (empty context), not errors.

**Step 7 — API + ConfigHealth.** `PUT /api/default-embedding-model` (+ `DefaultEmbeddingModelRequest`,
200-char cap, no catalog validation); `ProvidersResponse.EmbeddingDefault`; per-model
`SupportsEmbeddings` in provider save/response; `ModelInfo.SupportsEmbeddings` via `/api/models`.
**Remove** the ConfigHealth embedding presence checks + singleton probe (update
`ConfigHealthProbeTests`). *Verify:* `ProvidersApiTests`, `UserModelCatalogTests`, `ConfigHealthTests`,
`ConfigHealthApiTests`, `ConfigHealthProbeTests`.

**Step 8 — Remove the operator embedding floor.** `Program.cs`: delete fatal checks (`:44–48,58–62,
189–196`) + the embedding singleton (`:200–202`); **keep** `TenantId` (`:70–73`). `appsettings.json`:
remove `EmbeddingDeploymentName` + the `//ApiKey` embedding comment (keep `TenantId`; keep/blank
`Endpoint`/`ApiKey` per constraint 6). Bicep: remove `azureOpenAIEmbeddingDeploymentName` param + env;
remove `azureOpenAIApiKey`/`AZURE_OPENAI_API_KEY` wiring in `deploy.yml` **only after** grep-confirming
no baseline provider needs it. *Verify:* `dotnet build`; `dotnet format --verify-no-changes`; **manual
DevHost/prod boot with no `AzureOpenAI:ApiKey`** (see AC9 note).

**Step 9 — Frontend** *(depends on 7)*. `index.html`: a "Default embedding model"
`<select id="embeddingModelSelect">` in the Model providers tab; a "supports embeddings" checkbox per
model in the add/edit provider form. `app.js`: `syncEmbeddingModelSelect()` (filter to
`m.SupportsEmbeddings`), `persistEmbeddingDefault()` (`PUT /api/default-embedding-model`), read
`EmbeddingDefault` in `loadProviders()`, per-model flag in the provider form. **Attachment gate:**
`handleFiles` rejects document media types with a clear message when `EmbeddingDefault` is empty,
while still accepting images (constraint 10); the Web Share Target path inherits it. *Verify:* DevHost
manual (with no embedding model, attaching a PDF is blocked with a message but an image is accepted;
then add + pick an embedding model, upload a doc, ask a grounded question).

**Step 10 — ADR + docs** *(depends on all)*. `/decide` the ADR (amends 0007/0017/0019, extends 0018,
records 3072 validate-and-reject, delete-to-reingest, no-baseline, ConfigHealth shape change).
`/sync-docs`: update the "Document RAG embedding deployment is a manual prerequisite" and "Cosmos
vector search is prod-only" gotchas, the `CLAUDE.md` RAG bullet, `docs/features/document-qa.md`.
*Verify:* doc-path integrity CI; `/audit-docs` spot-check.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Proving test | Test file |
|---|---|---|---|
| AC1 | An embedding-capable model surfaces on `/api/models` with `SupportsEmbeddings` | catalog carries the flag | `UserModelCatalogTests.cs`, `ModelCatalogTests.cs` |
| AC2 | `SetEmbeddingDefaultAsync` persists + bumps overlay version | grain round-trip | `UserProviderConfigGrainTests.cs` |
| AC3 | Resolver returns the embedding default only for a declared embedding-capable model, else `""` | resolve cases | `UserProviderResolverTests.cs` |
| AC4 | Factory `GetEmbeddingGenerator()` builds per kind; unset → clear "not configured" error on use | factory unit | `ModelProviderTests.cs` |
| AC5 | Ingest rejects a vector whose length ≠ `ExpectedDimensions` with a clear message; 3072 happy path succeeds; agent-run `ownerKey` resolves the right user | ingestion/dimension guard | `DocumentIndexTests.cs` (or new ingestion test) |
| AC6 | Offline RAG round-trips (upload → retrieve → grounded answer) via the single-user generator | existing round-trip | `DocumentIndexTests.cs` |
| AC7 | A document conversation with **no** embedding default answers (empty context), never throws | retrieval degrade-to-empty | grain integration (`TestSiloConfigurator` harness) |
| AC8 | `PUT /api/default-embedding-model` enforces the 200-char cap + persists; `GET /api/providers` returns `EmbeddingDefault` | endpoint tests | `ProvidersApiTests.cs` |
| AC9 | App builds and boots with **no** `AzureOpenAI:ApiKey`/`EmbeddingDeploymentName` (no fatal check) | **manual/smoke — no `WebApplicationFactory` in the suite** (`UserTelemetryTests.cs:11`); verify via DevHost/prod boot | manual |
| AC10 | ConfigHealth no longer reports embedding presence/probe; other checks unaffected | config-health | `ConfigHealthTests.cs`, `ConfigHealthProbeTests.cs`, `ConfigHealthApiTests.cs` |
| AC11 | With no embedding model, the composer blocks attaching a document (clear message) but still accepts an image; the share path inherits it | attachment gate | manual/DevHost (no `WebApplicationFactory`); `app.js` `handleFiles` logic |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ plan time)

- `new_issues: 0` · `new_security_issues: 0` · `duplication_pct_max: 3` ·
  `coverage_new_code_pct_min: 80` · `cognitive_complexity_max: 15` · `vulnerable_packages: 0` (hard block)
- Tests written alongside implementation, never deferred (hard rule 3).
- Branch: `feature/*` + MR; never commit to `main` (hard rule 1).
- `dotnet format Agent42.slnx --verify-no-changes` clean before MR.

## Out of scope

- Making the Cosmos `doc-chunks` container dimension configurable (stays 3072).
- Per-chunk embedding-model tagging / retrieval filtering (model change = delete-to-reingest, documented).
- Deleting the `ModelProviderOptions` Foundry-inheritance reads of `AzureOpenAI:Endpoint/ApiKey/...` (dormant but live; separate concern).
- Rotating the leaked Azure OpenAI key and scrubbing git history (separate security thread — this plan removes the *need* for the operator secret but performs no rotation/rewrite).
- Embedding models on providers beyond the two standard AzureOpenAI / OpenAICompatible branches.

## Review

Critic pass run before approval. All findings accepted; none rejected.

- **HIGH 1** (agent-run ingestion must not use `GetUserId`) — fixed: constraint 5 + Step 5 derive
  `userId` from `ownerKey`; both call sites (`:148,365`) named; AC5 asserts the agent-run path.
- **HIGH 2** (retrieval throws on unset) — fixed: constraint 4 + Step 6 degrade retrieval to `[]`;
  new AC7 proves it.
- **MEDIUM 3** (ConfigHealth purity, ADR 0029) — fixed: constraint 9 + Step 7 **remove** the embedding
  checks/probe instead of adding a per-user static check; visibility moves to the providers tab; AC10
  updated; `ConfigHealthProbeTests` flagged.
- **MEDIUM 4** (second `AzureOpenAI:*` consumer) — fixed: constraint 6 keeps `TenantId` for
  `DefaultAzureCredential`; per-key pruning rule.
- **MEDIUM 5** (deviates from ADR 0020 exemplar) — fixed: architecture reworked to extend the existing
  `IChatClientFactory` (`GetEmbeddingGenerator`), dropping the parallel provider seam and the every-host
  registration ripple.
- **LOW 6** (AC9 unprovable) — fixed: AC9 relabelled manual/smoke with the `UserTelemetryTests.cs:11`
  evidence.
- **LOW 7** (line-ref nits + `UserProviderConfig.Empty`) — fixed: line refs corrected to `:148/:365`;
  `Empty` trailing-arg update called out in affected areas.

**Post-approval scope addition (user):** when no embedding model is configured the composer must
**not allow attaching documents** (images still allowed). Added as constraint 10, folded into Step 9,
and pinned by AC11. Server-side rejection (constraints 4/5) remains as defense-in-depth for the
non-UI `/api/agent-run` path.

## Implementation divergences from the plan

- **ConfigHealth went further than "remove the checks":** the `ConfigHealthProbeSet.Embeddings` field and
  `ProbeEmbeddingsAsync` were removed entirely (not just the static checks), so the probe-set record shrank
  from 4 to 3 delegates. Updated `ConfigHealthTests`, `ConfigHealthProbeTests`, `ConfigHealthApiTests`
  accordingly (AC10). The static `AzureOpenAI:Endpoint` Core check was also dropped (no longer fatal); the
  malformed-URI test retargeted to `WebSearch:Endpoint`.
- **`loadProviders()` is now called at startup** (init), not only on Preferences-open — the composer's
  document-attach gate needs `userDefaultEmbeddingModel` on first paint. One extra `/api/providers` fetch at
  boot; no server change (avoided widening `/api/models` / `ModelCatalogResult`).
- **The `AZURE_OPENAI_API_KEY` operator secret was fully removed** (Bicep param + ACA secret + `deploy.yml`
  wiring), and the leaked key in `src/Agent42.AppHost/appsettings.Development.json` was deleted — the grep-gate
  in Step 8 confirmed nothing baseline reads it (baseline `Models:Providers` is empty). `AzureOpenAI:Endpoint`
  (+ its ACA env) was kept as the dormant Foundry-inheritance source, `TenantId` kept for `DefaultAzureCredential`.
- **The embedding capability threads through the provider CRUD DTOs** (`ProviderModelRequest`/`ProviderModelResponse`
  + the request→`StoredModel` and `StoredProvider`→response mappings) and the provider add/edit form
  (`collectModels` + a per-model "embeddings" checkbox) — needed so a user can declare an embedding model, which
  the plan implied but did not enumerate as edits.
- **Test-host wiring:** `AddDocumentRagFakes` `TryAdd`s an embedding-capable `IUserChatClientFactoryProvider`
  (a no-op chat client + the deterministic generator) for endpoint-only containers; the silo/DevHost
  `SingleChatClientFactory` carry the deterministic generator + a stub embedding id so offline RAG round-trips.
- **AC9/AC11 are manual/DevHost** (no `WebApplicationFactory`/browser harness in the suite), as the plan labelled.
  Everything else is covered by automated tests (474 pass).
