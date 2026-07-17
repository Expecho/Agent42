# Plan — User-configurable model providers (web app)

planned-against: main @ ad6a23c

## Overview

Today the `Models:` provider catalog is bound once from `appsettings`/env at startup and
frozen for the process lifetime (`MultiProviderChatClientFactory` + `CompositeModelCatalog`
are singletons; changing a provider needs a redeploy). This plan lets a signed-in user
add / edit / delete model providers — **including Foundry** — from the web UI, per-user,
with API keys persisted (encrypted) and changes taking effect without an app restart.

Why now: the user wants to wire new endpoints (Ollama/vLLM/second Foundry/GitHub) without a
deploy, and to manage keys themselves rather than through operator env vars.

**Governing decision:** this contradicts the load-bearing assumptions of ADR 0013
(“endpoint always comes from config, never from a request”) and ADR 0017 (“secrets come from
operator-managed env; one source of truth; blank Foundry key is fatal”). **A new ADR that
amends both is a mandatory step (Step 1), not optional** — do it before/with the code.

## Design in one paragraph (read before touching anything)

**Baseline ⊕ per-user overlay, resolved async on grain activation.** The env/`appsettings`
provider set stays exactly as today and remains the **boot-time availability floor** — the
app still starts on it, and the “blank Foundry key is fatal” rule (ADR 0017) still holds for
that *baseline* at startup. A new per-user grain persists an **overlay** (added providers +
overrides, with encrypted keys). A single resolution component merges *baseline ⊕ overlay* into
an effective provider set for a given userId; both the chat-client factory and the model catalog
consume it. A user override that resolves invalid (e.g. blanks the Foundry key) **falls back to
baseline** — user overlays are best-effort and never fatal; only the operator baseline is on the
availability path. This is what keeps ADR 0017’s guarantee intact while making Foundry editable.

## Affected areas (verified against the tree @ ad6a23c)

New files:
- `src/Agent42.Agents/UserProviderConfigGrain.cs` + `IUserProviderConfigGrain.cs` — per-user overlay grain (mirrors `UserProfileGrain.cs`).
- `src/Agent42.Agents/UserProviderConfig.cs` — serializable overlay state (`StoredProvider` list + optional default).
- `src/Agent42.Agents/IUserChatClientFactoryProvider.cs` — async seam `Task<IChatClientFactory> GetForUserAsync(string userId)` + a `SingleUserChatClientFactoryProvider` adapter (wraps any `IChatClientFactory`, returns it for every user — for DevHost/tests).
- `src/Agent42.Backend/UserProviderResolver.cs` — the baseline ⊕ overlay merge; single source of truth for “this user’s effective providers”. Produces the `IReadOnlyDictionary<string, Provider>` + effective default.
- `src/Agent42.Backend/UserChatClientFactoryProvider.cs` — backend impl of the seam; per-user cache keyed by `(userId, version)`.
- `src/Agent42.Backend/UserModelCatalogProvider.cs` (`IUserModelCatalogProvider`) — per-user `CompositeModelCatalog` builder.
- `src/Agent42.Backend/ProviderSecretProtector.cs` (`IProviderSecretProtector`) — `IDataProtector` wrapper (encrypt on save / decrypt on build).

Changed files:
- `src/Agent42.Agents/AgentGrain.cs` — constructor takes `IUserChatClientFactoryProvider` instead of `IChatClientFactory`; `_chatClientFactory` is resolved in `OnActivateAsync` from the grain’s userId (parse `this.GetPrimaryKeyString()` on the first `/`) **before** the first use at `AgentGrain.cs:93` (see m4 constraint). The ~8 existing sync call sites are unchanged.
- `src/Agent42.Backend/ModelProviderBuilder.cs` — refactor to expose a **per-provider** build (`TryBuildProvider(key, options, config)` returning success/failure) so the resolver can reuse the load-bearing Foundry inheritance + skip/fatal rules instead of duplicating them (M2). `Build` stays the startup-baseline entry point and keeps throwing on a blank *baseline* Foundry key; the new per-provider path lets the resolver **catch a failing overlay build and keep the baseline entry** (AC2) rather than throw.
- `src/Agent42.Backend/AgentApi.cs` — new `/api/providers` CRUD (mirror `/api/agents`, lines 194–224); `/api/models` (line 192) becomes user-scoped via `IUserModelCatalogProvider`.
- `src/Agent42.Backend/Program.cs` — register the new services; add Data Protection with a **persisted** key ring (see Step 3 — MI overload, gated on the prod `Storage:BlobEndpoint` branch); baseline factory/catalog become the *baseline* inputs to the per-user layer rather than the injected singletons.
- `src/Agent42.Backend/wwwroot/index.html`, `app.js`, `styles.css` — a “Providers” management UI mirroring the agents modal (`app.js` lines 268–400).
- `tools/Agent42.DevHost/Program.cs` (`IChatClientFactory` at line 28, `IModelCatalog` at line 36) and every test that registers `IChatClientFactory` (`TestSiloConfigurator.cs:47`, `CapSizedWebPageFetcher.cs:41`, `AttachmentRehydrateTests.cs:42`, `FakeGeocoder.cs:47`) — also register `IUserChatClientFactoryProvider` via the `SingleUserChatClientFactoryProvider` adapter (one line each).
- **Test/DevHost hosts that register `IModelCatalog` for the `/api/models` endpoint (M3):** `AgentApiTests.cs:38`, `AttachmentTests.cs:37` (both call `MapAgentApi()` — the registration is what lets minimal-API resolve the catalog param as a service, not a body), and `tools/Agent42.DevHost/Program.cs:36` — each must also register the new `IUserModelCatalogProvider` (via a single-user adapter).
- `infra/modules/storage.bicep` — a `dataprotection` blob container for the Data Protection key ring (app MI already holds Storage Blob Data Contributor).
- `.agentic/decisions/` — new ADR (Step 1). `.agentic/gotchas.md` + `CLAUDE.md` — doc sync (Step 9).

Unchanged on purpose: `ModelProviderOptions.cs` (`ProviderOptions`/`ConfiguredModel`/`ModelId`/`ProviderSettings` are reused as-is — `StoredProvider` maps to `ProviderOptions`), `MultiProviderChatClientFactory` internals (instantiated per-user instead of once), the embedding client (stays on operator `AzureOpenAI:ApiKey`; **out of scope**).

## Context & constraints (what the executor can’t cheaply re-derive)

- **Availability floor is non-negotiable.** Startup still builds the baseline from env exactly as `Program.cs:89–112` does today, and `ModelProviderBuilder.Build` still throws on a blank *baseline* Foundry key (ADR 0017). Per-user overlays are layered *after* boot and are best-effort. Evidence: `ModelProviderBuilder.cs:47–56`, `Program.cs:57–69`.
- **The consuming call sites are sync and live inside `AgentGrain`.** `GetClient`/`GetCapabilities` are called from non-async grain code (`AgentGrain.cs:93,114,207,339,422,437,1052`). Do **not** try to make them async. Instead resolve a *user-scoped* `IChatClientFactory` once in `OnActivateAsync` (async) and store it in `_chatClientFactory`; the existing sync interface (`IChatClientFactory.cs:36–50`) is unchanged and every call site keeps working. The grain already knows its userId — the key is `{userId}/{conversationId}` (`AgentApi.cs:247`).
- **Grain-key parse:** userId is everything before the first `/` in `this.GetPrimaryKeyString()`. A conversation id can itself contain no `/` guarantee is not needed — split on the FIRST `/` (userId is an Easy-Auth principal id, opaque, no `/`). Confirm against `AgentApi.AgentKey` (`AgentApi.cs:247`).
- **Secrets never leave the server and never enter the 2 MB session.** The overlay grain rides a **distinct state name** on the shared `agent-state` provider (like `UserProfileGrain`’s `"profile"` — `UserProfileGrain.cs:29`), a separate Cosmos item from the session (ADR 0004). `GET /api/providers` returns keys **masked** (e.g. `hasKey: bool`, never the ciphertext or plaintext). On `PUT`, a **blank ApiKey field means “keep the stored key”** so the user needn’t re-enter secrets to edit other fields.
- **Encryption + key-ring persistence.** Encrypt keys with `IDataProtector` before persisting; decrypt only when building a client. The Data Protection **key ring must be persisted out-of-process** (prod) or a restart rotates the ring and every stored secret becomes undecryptable. **Gate on the PROD branch — `Storage:BlobEndpoint` is set (NOT `GetConnectionString("blobs")`, which is the Azurite/AppHost path — see `Program.cs:126–137`), and shared-key auth is disabled on the account (`gotchas.md:663–665`), so the connection-string overload cannot work in prod.** Use the **MI overload**: construct a `BlobClient(new Uri(...), credential)` (the same `credential` from `Program.cs:76`) and pass it to `PersistKeysToAzureBlobStorage(blobClient)`. DevHost/tests use the default ephemeral ring (fine — no persisted secrets there). **Precise posture (m6):** the user *secrets* are DP-encrypted; the DP *master key* itself sits in the blob protected only by Storage SSE + RBAC (no `ProtectKeysWith…`) — say exactly this in the ADR/gotcha, not a blanket “encrypted at rest.” Cosmos’s own at-rest encryption still covers the persisted ciphertext.
- **Provider endpoint is deliberately NOT SSRF-guarded.** A user-configured chat endpoint must accept `http://localhost:11434` (Ollama) and private hosts — that is a supported target. This is safe because the endpoint comes from the *authenticated user’s own persisted config*, not from a model or a live request, so it stays off the `fetch_web_page` SSRF path (ADR 0011). **Do not add a `PrivateAddressGuard` here.** Validation is limited to: absolute `http`/`https` URI, valid `Kind`. Record this as an accepted, deliberate difference in the ADR + a gotcha.
- **Deleting/renaming a provider degrades, never breaks.** A persisted conversation pinned to `github:…` whose provider the user later removes already falls back to the default via `MultiProviderChatClientFactory.Resolve` (`ChatClientFactory.cs:98–107`). The per-user factory inherits this — no migration needed (same reasoning as ADR 0013’s legacy-id handling).
- **Hot-reload — honest, achievable semantics.** Per-user factories/catalogs are cached keyed by a **version stamp** the overlay grain bumps on every save. Effect: `/api/models` and every **new or newly-activated** conversation grain pick up changes with **no restart**. An **already-activated** conversation grain picks up the change on its next activation (Orleans deactivates idle grains). Full live-invalidation of in-flight active grains is **out of scope** (see Out of scope) — state this to the user at approval; it is the pragmatic reading of “hot-reload live” for a personal app.
- **Exemplars to copy:** grain shape → `UserProfileGrain.cs` / `AgentRegistryGrain.cs`; endpoint shape → `/api/agents` block (`AgentApi.cs:194–224`); frontend CRUD → `loadAgents`/`renderAgents`/`saveAgent` (`app.js:268–400`) and `loadModels`/`fillModelOptions` (`app.js:123–167`).
- **Gotchas to respect:** the Foundry inheritance / single-source-of-truth entry (`gotchas.md` ~407–435), the three Foundry-key traps (~480–512), the first-colon split (~340–357). The ADR must reconcile the first two.

## Steps

1. **ADR — amend 0013 + 0017 for user-configurable per-user providers.** Run `/decide`.
   Record: endpoints may now come from authenticated per-user config (SSRF invariant re-scoped —
   still off the model-driven fetch path); per-user secrets persisted encrypted in Cosmos, env
   Foundry stays the availability floor and “blank baseline Foundry key = fatal” stays; overlay is
   best-effort. *Verify:* ADR file exists in `.agentic/decisions/`, supersession/amendment noted in
   0013 & 0017 headers. **Blocks all other steps.**

2. **Overlay grain + state.** `UserProviderConfig` (`StoredProvider` list — mirror of `ProviderOptions`
   with the key stored as ciphertext — + optional default + a monotonically-increasing `Version`),
   `IUserProviderConfigGrain` (`GetAsync`/`SaveAsync`/`DeleteAsync`/`GetVersionAsync`),
   `UserProviderConfigGrain` on `[PersistentState("providers", AgentGrain.StorageProviderName)]`.
   *Files:* `src/Agent42.Agents/UserProviderConfig.cs`, `IUserProviderConfigGrain.cs`, `UserProviderConfigGrain.cs`.
   *Verify:* new grain test — save→get round-trips, delete removes, blank-key-on-update keeps stored key, Version bumps on save. `dotnet test --filter UserProviderConfigGrainTests`.

3. **Secret protector.** `IProviderSecretProtector` over `IDataProtector` (protect/unprotect). Program.cs:
   `AddDataProtection()` with the key ring **gated on the prod `Storage:BlobEndpoint` branch** using the **MI
   `PersistKeysToAzureBlobStorage(BlobClient)` overload** — NOT the connection-string overload, which cannot
   work with shared-key auth disabled (B1). Default ephemeral ring when `Storage:BlobEndpoint` is absent (DevHost/tests).
   *Files:* `src/Agent42.Backend/ProviderSecretProtector.cs`, `Program.cs`, `infra/modules/storage.bicep` (add `dataprotection` container); may need `Azure.Extensions.AspNetCore.DataProtection.Blobs` — resolve latest-stable via `dotnet add package`, pin in `Directory.Packages.props`.
   *Verify:* unit test — protect→unprotect round-trips; a masked DTO never carries plaintext. `dotnet test --filter ProviderSecret`.

4. **Resolver (baseline ⊕ overlay).** `UserProviderResolver`: given userId, fetch the overlay grain, decrypt keys,
   and merge **at the options level**, then build. Algorithm (explicit — M2):
   (a) start from the baseline `Dictionary<string, ProviderOptions>` (the bound `Models:` options);
   (b) for each overlay `StoredProvider`, decrypt its key and **upsert its `ProviderOptions` by key** onto that map;
   (c) for each entry, call `ModelProviderBuilder.TryBuildProvider` (Step 2 refactor) — on success use the built
   `Provider`; on failure (e.g. a Foundry override with a blank key throwing per `ModelProviderBuilder.cs:51–56`)
   **keep the baseline-built entry if one exists, else skip** (never throw — AC2/availability floor). This reuses the
   Foundry inheritance/skip/fatal rules from one place (no duplication → respects `gotchas.md:407–435` + the 3%
   duplication gate). Produce `IReadOnlyDictionary<string, Provider>` + effective default. Single source of truth reused by Steps 5 & 6.
   *Files:* `src/Agent42.Backend/UserProviderResolver.cs` (+ `ModelProviderBuilder.cs` from Step 2’s refactor).
   *Verify:* unit tests — added OpenAI-compatible provider appears; Foundry override with blank key falls back to
   baseline (availability floor); removed provider → qualified id degrades to default. `dotnet test --filter UserProviderResolver`.
   **Parallelizable with Step 5’s seam once Steps 2–3 land.**

5. **Per-user chat-client factory seam.** `IUserChatClientFactoryProvider.GetForUserAsync(userId)` (in `Agent42.Agents`)
   + `SingleUserChatClientFactoryProvider` adapter; backend `UserChatClientFactoryProvider` builds a per-user
   `MultiProviderChatClientFactory` from the resolver, cached by `(userId, version)`. `AgentGrain` ctor swaps
   `IChatClientFactory` → `IUserChatClientFactoryProvider`; resolve `_chatClientFactory` in `OnActivateAsync`.
   **Hard ordering constraint (m4):** the `await …GetForUserAsync(userId)` assignment to `_chatClientFactory` MUST run
   **before** `AgentGrain.cs:93` (`_state.State.DeploymentName ?? _chatClientFactory.DefaultModelId`) and the
   `BuildAgent` call on line 94 — both dereference `_chatClientFactory`, so resolving it later NREs.
   *Files:* `IUserChatClientFactoryProvider.cs`, `UserChatClientFactoryProvider.cs`, `AgentGrain.cs`, `Program.cs`,
   and the DevHost/test registrations (`TestSiloConfigurator.cs`, `CapSizedWebPageFetcher.cs`,
   `AttachmentRehydrateTests.cs`, `FakeGeocoder.cs`, `tools/Agent42.DevHost/Program.cs`).
   *Verify:* factory-provider test — distinct users get distinct factories; version bump returns a fresh factory
   (cache invalidation); existing `ModelProviderTests` still green. `dotnet test --filter "ModelProvider|UserChatClientFactory"`.

6. **Per-user catalog.** `IUserModelCatalogProvider.GetForUserAsync(userId)` → per-user `CompositeModelCatalog`
   from the resolver; `/api/models` resolves userId from `HttpContext`.
   *Files:* `UserModelCatalogProvider.cs`, `AgentApi.cs`, `Program.cs`, and the `IModelCatalog`-registering hosts
   the endpoint switch breaks (M3): `AgentApiTests.cs:38`, `AttachmentTests.cs:37`, `tools/Agent42.DevHost/Program.cs:36`.
   *Verify:* catalog test — a user’s added provider’s declared models appear; existing `ModelCatalogTests` green;
   `AgentApiTests`/`AttachmentTests` still map `/api/models`.

7. **`/api/providers` CRUD.** GET (masked) / POST / PUT (blank key = keep) / DELETE, mirroring `/api/agents`.
   The API layer **encrypts the key via `IProviderSecretProtector` before calling the grain** (the grain stores
   ciphertext, Step 2), so the endpoint host — including the `ProvidersApiTests` host (m7) — must register
   `IProviderSecretProtector`. Validation: absolute `http`/`https` endpoint, valid `Kind`; **no PrivateAddressGuard**.
   Each write bumps the grain Version.
   *Files:* `AgentApi.cs` (+ request/response records), `ProvidersApiTests.cs` host wiring. *Verify:* API tests (mirror `AgentApiTests`) — CRUD, key never returned, `localhost` endpoint accepted, malformed endpoint 400. `dotnet test --filter Provider`.

8. **Frontend providers UI.** List + add/edit/delete modal (Kind, DisplayName, Endpoint, write-only ApiKey with
   “leave blank to keep”, Models sub-editor with tools/vision, UseResponsesApi for Foundry). On save, call
   `loadModels()` so the picker reflects changes immediately.
   *Files:* `wwwroot/index.html`, `app.js`, `styles.css`. *Verify:* manual — run DevHost (`dotnet run --project tools/Agent42.DevHost`), add a provider, confirm it appears in the model picker without restart; `/verify` for the flow.

9. **Doc sync.** New gotchas (availability floor / baseline-wins; secrets encrypted-never-echoed + key-ring must persist;
   per-user factory resolved on activation not injected; provider endpoint deliberately not SSRF-guarded). Update the
   `CLAUDE.md` “Model providers” bullet. Run `/gotcha` + `/sync-docs`.
   *Verify:* `/audit-docs` clean; `dotnet build` (doc-path integrity in CI).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A user can add an OpenAI-compatible provider; its declared models appear in that user’s catalog | `UserProviderResolver_AddsOpenAICompatibleProvider`; `UserModelCatalog_ListsUserProviderModels` | `UserProviderResolverTests.cs`, `UserModelCatalogTests.cs` |
| AC2 | A user can override/edit Foundry; a blank/invalid key override falls back to the baseline (app never bricks) | `UserProviderResolver_FoundryOverrideWithBlankKey_FallsBackToBaseline` | `UserProviderResolverTests.cs` |
| AC3 | Deleting a provider degrades a conversation pinned to it to the default, not an error | `UserProviderResolver_RemovedProvider_DegradesToDefault` | `UserProviderResolverTests.cs` |
| AC4 | API keys are persisted encrypted and never returned to the client | `ProviderSecretProtector_RoundTrips`; `ProvidersApi_Get_NeverReturnsKey` | `ProviderSecretProtectorTests.cs`, `ProvidersApiTests.cs` |
| AC5 | Editing without re-entering the key keeps the stored key | `UserProviderConfigGrain_BlankKeyOnUpdate_KeepsStoredKey` | `UserProviderConfigGrainTests.cs` |
| AC6 | On save, the provider layer returns a fresh factory/catalog for the user without an app restart (the seam that makes `/api/models` and next-activation grains reflect the change; live-invalidation of active grains is out of scope) | `UserChatClientFactoryProvider_VersionBump_ReturnsFreshFactory` | `UserChatClientFactoryProviderTests.cs` |
| AC7 | `localhost` endpoints are accepted; malformed endpoints are rejected | `ProvidersApi_AcceptsLocalhost_RejectsMalformed` | `ProvidersApiTests.cs` |
| AC8 | CRUD over `/api/providers` works per-user (one user can’t see another’s) | `ProvidersApi_Crud_IsPerUser` | `ProvidersApiTests.cs` |
| AC9 | Baseline behavior is unchanged when a user has no overlay | existing `ModelProviderTests`, `ModelCatalogTests` stay green | (existing) |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ ad6a23c)

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `dependencies.vulnerable_packages: 0` (hard block)
- Branch: `feature/*` + MR (never commit to `main`). Tests written alongside each step, never deferred.
- Any new package (e.g. `Azure.Extensions.AspNetCore.DataProtection.Blobs`) resolved latest-stable via `dotnet add package`, pinned in `Directory.Packages.props` — never a version from memory.

## Out of scope

- **Live invalidation of already-activated conversation grains** — they pick up config on next activation (an optional stretch: a grain method + `IUserProviderConfigGrain` broadcast, deferred).
- **The embedding client** stays on the operator `AzureOpenAI:ApiKey` — RAG is not per-user-provider.
- **App-global (admin) provider config** — this plan is per-user (user’s explicit choice).
- **ARM discovery for user-added Foundry providers** — stays off (ADR 0017); user Foundry providers declare their deployments, like OpenAI-compatible ones.
- **Migrating operator env providers into the per-user store** — env baseline remains; overlay is additive.
- **Secret rotation UI / audit log / per-provider health-check ping on save** — future work.

## Review (critic pass, pre-approval)

Verdict: **sound architecture**; one BLOCKER + two MAJOR + four MINOR — all accepted and folded in.

- **B1 (fixed)** — Data Protection persistence was gated on “blob connection present,” which is the *Azurite/local* path; prod uses the endpoint+MI branch and has shared-key auth disabled, so prod would have fallen back to an ephemeral ring and made stored secrets undecryptable after any restart. Step 3 + the Encryption constraint now gate on the prod `Storage:BlobEndpoint` branch and mandate the MI `PersistKeysToAzureBlobStorage(BlobClient)` overload.
- **M2 (fixed)** — the resolver merge was under-specified and would have duplicated the Foundry inheritance/skip/fatal rules that live only in `ModelProviderBuilder.Build` (tripping the single-source gotcha + 3% duplication gate), and couldn’t satisfy AC2 because `Build` throws on a blank Foundry key. Added a `ModelProviderBuilder` refactor (per-provider `TryBuildProvider`) and an explicit options-level merge-then-try-build-else-keep-baseline algorithm in Step 4.
- **M3 (fixed)** — switching `/api/models` to `IUserModelCatalogProvider` breaks `AgentApiTests.cs:38`, `AttachmentTests.cs:37`, and `DevHost:36`, which register `IModelCatalog` so the minimal-API param resolves as a service. Named all three in Affected areas + Step 6.
- **m4 (fixed)** — pinned the hard ordering constraint: resolve `_chatClientFactory` before its first use at `AgentGrain.cs:93`.
- **m5 (fixed)** — reworded AC6 to what its test actually proves (the provider seam), not full active-grain live-reload.
- **m6 (fixed)** — “encrypted at rest” made precise: user secrets are DP-encrypted; the DP master key is protected only by Storage SSE + RBAC (no `ProtectKeysWith…`).
- **m7 (fixed)** — the API layer encrypts before the grain call, so the `ProvidersApiTests` host must register `IProviderSecretProtector`; added to Step 7.

Cleared by the critic (no change needed): userId parse-from-grain-key is safe (`GetUserId` has no `/`); the grain-ctor ripple list is complete (four silo configurators + DevHost); the SSRF re-scoping is consistent with the config-not-request invariant; the 2 MB session is untouched (distinct `"providers"` state name). One non-blocking latent note: `(userId, version)` cache entries are never evicted — negligible here, revisit if the app grows multi-user.

## Divergences from the plan (recorded at execution, 2026-07-16)

Implemented on `feature/user-configurable-model-providers`. New ADR is **0018** (not a number the plan
fixed). All 9 steps landed; full suite **286 green**, `dotnet format` clean, coverage on new/changed code
**92.4%** (gate ≥80), **0 vulnerable packages**. Where the implementation departed from the written plan:

1. **`StoredProvider.Kind` is a `string`, not `ProviderKind`.** `ProviderKind`/`ProviderOptions` live in
   `Agent42.Backend`, and `Agent42.Agents` (where the overlay grain lives) cannot reference up the graph.
   So the Agents-layer `StoredProvider`/`StoredModel`/`UserProviderConfig` are self-contained records
   (with an `Empty` factory, per the shared-static-default gotcha) and the **resolver** parses the kind
   string into `ProviderKind`. `ModelProviderOptions.cs` stayed unchanged as planned.

2. **User overlay providers do NOT inherit the operator `AzureOpenAI:*` secrets.** The resolver builds
   overlay providers via `TryBuildProvider` with an **empty `IConfiguration` (`NoInheritance`)`. This was a
   deliberate design decision made during Step 4: it is the correct security posture (the operator key must
   never be sent to a user-chosen endpoint) **and** it is what makes AC2 real — with inheritance a blanked
   user Foundry key would silently re-inherit the operator key and the "fall back to baseline" path could
   never fire. Recorded in ADR 0018 + a gotcha. (The plan implied reuse of the inheritance rule; it *is*
   reused — same code path — just fed a no-inheritance config for overlays.)

3. **`IUserProviderResolver` interface extracted** (plan listed only the concrete `UserProviderResolver`).
   Needed so the per-user factory (Step 5) and catalog (Step 6) providers are unit-testable against a fake
   resolver with a controllable version — no grain. The concrete resolver's pure `Merge(UserProviderConfig)`
   is also public and tested directly (no grain I/O).

4. **`SetDefaultAsync` + overlay `Default` implemented and tested** (the plan's "optional default"). The
   grain method, the resolver's guarded `EffectiveDefault` (honour the overlay default only when it names a
   built provider), and their tests exist; it is **not** yet exposed via the API/frontend (no AC required
   it) — future wiring is trivial.

5. **Ripple wider than the plan's M3.** The plan named `AgentApiTests`/`AttachmentTests`/DevHost as needing
   `IUserModelCatalogProvider` (the `/api/models` switch). The **new `/api/providers` endpoints** (Step 7)
   add `IProviderSecretProtector` as a required service for **any** host that calls `MapAgentApi` — the
   route matcher infers *all* endpoints' parameters up front, so an unregistered protector breaks the whole
   host. `AgentApiTests`, `AttachmentTests`, `ProvidersApiTests`, and **DevHost** all had to register it
   (DevHost via `AddDataProtection()` + `ProviderSecretProtector`, ephemeral ring). Same class of issue as
   M3, one service wider.

6. **Frontend placement.** The providers UI is a **section inside the Preferences dialog** (list + Add
   button) plus a **separate stacked `providerDialog`** for add/edit (with a dynamic Models sub-editor),
   rather than a sidebar nav. Cleaner than crowding the sidebar and avoids nested-dialog quirks. Validation
   requires an absolute `http`/`https` endpoint for **both** kinds (a user Foundry provider must declare its
   own endpoint, since it does not inherit — see divergence 2).

7. **Offline picker-reflection is a known gap.** DevHost/tests use single-user adapters
   (`SingleUserChatClientFactoryProvider`/`SingleUserModelCatalogProvider`) that ignore the overlay, so the
   providers **CRUD + UI** are exercisable offline but overlay→picker reflection only runs in prod (the real
   resolver-backed providers). It is unit-tested by `UserModelCatalogTests.ListsUserProviderModels` (AC1).
   Step 8's manual verify therefore drove the **HTTP CRUD flow** end to end in DevHost (create/get-masked/
   put-blank-key-keeps/400-on-malformed/delete) rather than the picker.

8. **New package:** `Azure.Extensions.AspNetCore.DataProtection.Blobs` **1.5.3** (latest-stable via
   `dotnet add package`, pinned in `Directory.Packages.props`) — for the prod key-ring persistence.
