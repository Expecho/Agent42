# Plan — API-key auth for the Foundry provider (replace managed identity)

planned-against: main @ cda0345

## Overview

Make the Azure AI Foundry provider authenticate with an **Azure OpenAI API key** instead of the
container app's managed identity, on **both** the Chat Completions path and the Responses `/openai/v1`
path (ADR 0014/0015). This is a **full replacement** for the Foundry data plane — the managed-identity
inference path and its bearer-token bridge are removed, not kept as a fallback. The **embedding client**
(document RAG) switches to the same key. ARM-based model **discovery** is dropped (an Azure OpenAI
data-plane key cannot authenticate ARM); the picker degrades to the default plus any models the Foundry
provider now **declares** in config.

Why now: the user wants Foundry reachable without a managed identity (key-only operation). This
contradicts the credential decisions in ADR 0013 (`AzureOpenAI` = managed identity) and ADR 0014
(Responses via a managed-identity bearer policy), so it ships with a new superseding ADR.

## Affected areas (verified against the tree at cda0345)

| File | Change |
|------|--------|
| `src/Agent42.Backend/ModelProviderOptions.cs` | Add `ApiKey` to `FoundrySettings`; resolve it in `ProviderSettings.ResolveFoundry` (inherit from `AzureOpenAI:ApiKey`). Doc-comment updates on `ProviderKind.AzureOpenAI` / `ProviderOptions`. |
| `src/Agent42.Backend/ChatClientFactory.cs` | `Provider.Foundry` takes an **API key**, not a `TokenCredential`. Chat path → `AzureOpenAIClient(endpoint, AzureKeyCredential)`. Responses path → `OpenAIClient(ApiKeyCredential, …)`. **Delete** `AzureIdentityTokenProvider` and the `BearerTokenPolicy` branch. |
| `src/Agent42.Backend/ModelProviderBuilder.cs` | Build the Foundry provider from the resolved key; **throw at build** when it is blank (fatal — availability path). Contribute a non-discovering `AzureOpenAI` provider's declared `Models` to the catalog. |
| `src/Agent42.Backend/Program.cs` | Stop passing `credential` into the Foundry provider build. Switch the embedding `AzureOpenAIClient` to `AzureKeyCredential`. Keep `credential` (blob storage, Cosmos, unused-by-Foundry `ArmClient`). |
| `src/Agent42.Backend/appsettings.json` | `foundry` provider: `Discover: false`, declare its `Models`, document the `ApiKey` secret. Add `AzureOpenAI:ApiKey` placeholder/comment. |
| `tests/Agent42.Tests/ModelProviderTests.cs` | Rework for key-based construction; add fatal-on-blank-key; keep Chat/Responses wiring assertions; **remove** `BearerBridge_MapsAzureTokenToBearer`. |
| `tests/Agent42.Tests/ModelCatalogTests.cs` | Add a test that a non-discovering Foundry provider contributes its declared models. (ARM tests stay — the machinery is not deleted.) |
| `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml` | Thread the Foundry key as a Container App secret, mirroring the `github` provider wiring. |
| `.agentic/decisions/`, `.agentic/gotchas.md`, `CLAUDE.md` | New ADR (supersede credential aspects of 0013/0014); gotcha for the `/openai/v1` key-auth finding + the fatal-key reversal; refresh the Foundry bullets in CLAUDE.md. |

## Context & constraints (what the executor can't cheaply re-derive)

- **User decisions (this planning interview):**
  1. **Full replacement**, not an optional override — remove the MI inference path entirely.
  2. **Accept discovery degradation** — no ARM query for Foundry; picker shows default + declared models.
  3. **Switch embeddings too** — the embedding client uses the same key.
- **The Responses path is ACTIVE in this repo.** `appsettings.json` ships `foundry.UseResponsesApi: true`
  (operator-confirmed 2026-07-15, per its `//UseResponsesApi` comment). So the key MUST work on the
  `/openai/v1` Responses surface — this is not a dormant path. See Step 1 (spike).
- **The Responses-with-key construction resembles `OpenAICompatible`, but the auth header is the catch.**
  `Provider.OpenAICompatible` (`ChatClientFactory.cs:185`) is `new OpenAIClient(new ApiKeyCredential(key),
  new OpenAIClientOptions { Endpoint = … })` — and `ApiKeyCredential` makes the OpenAI SDK send
  `Authorization: Bearer <key>`. That is how **GitHub Models** authenticates, but an Azure OpenAI **resource
  key** is authenticated through the **`api-key` request header**; the `Authorization: Bearer` slot on
  `/openai/v1` expects an Entra JWT. So the plain construction will **likely 401**. The primary Responses
  design is therefore a custom **`api-key`-header pipeline policy** on the `OpenAIClient` (adapt the shape of
  the `BearerTokenPolicy` branch being removed — a `PipelinePolicy` that sets
  `message.Request.Headers.Set("api-key", key)` in `BeforeTransport`). Step 1 confirms which header the
  endpoint actually accepts.
- **The Chat path is the low-risk one.** `AzureOpenAIClient(endpoint, AzureKeyCredential)` is the Azure SDK
  client and sends the `api-key` header itself — no custom policy needed. Only the Responses `/openai/v1`
  path needs the header workaround.
- **Do NOT reach for `AzureOpenAIClient.GetResponsesClient` as a contingency.** Per ADR 0014
  (`0014-responses-api-stateless-mode.md:41-45`) it first appears in `Azure.AI.OpenAI 2.2.0-beta`; there is
  no stable release above `2.1.0` and taking a prerelease was explicitly rejected (hard rule 4 + ADR 0014
  Alternatives). The `api-key`-header policy on the stable `OpenAIClient` is the only viable Responses path.
- **`AzureKeyCredential` (namespace `Azure`, from `Azure.Core`) is the Chat-path credential.**
  `AzureOpenAIClient(Uri, AzureKeyCredential)` exists in `Azure.AI.OpenAI 2.1.0`
  (`Directory.Packages.props:13`). `ApiKeyCredential` (`System.ClientModel`) is the Responses-path one,
  already imported in `ChatClientFactory.cs`.
- **ARM cannot use the data-plane key** — that is *why* discovery is dropped, not a limitation to fix.
  `AzureModelCatalog`/`ArmClient` (`ModelCatalog.cs`, `Program.cs:87`) stay in the tree (a future
  discovering provider could still use them); the shipped `foundry` provider simply sets `Discover: false`
  and is not added to `models.Discovering`. `CompositeModelCatalog` already degrades to the default when no
  provider lists anything (`ModelCatalogTests.NoProviderListsAnything_TheDefaultIsStillOffered`).
- **Missing Foundry key is FATAL — a deliberate reversal.** ADR 0013's "a missing key degrades, not fails"
  rule (`ModelProviderBuilder.cs:66`) is about **third-party** providers on top of Foundry. Foundry is now
  the *availability path itself*, so a blank key must fail at startup like the eager endpoint check
  (`Program.cs:48`) and like `SEARXNG_SECRET`'s `@minLength` guard (`infra/main.bicep:36`). Put the throw in
  `ModelProviderBuilder.Build` (testable; mirrors the existing "AzureOpenAI provider with no endpoint" throw
  at `ModelProviderBuilder.cs:42`), NOT in Program.cs.
- **Key source & inheritance.** The Foundry key resolves like the other Foundry settings: provider-level
  `Models:Providers:foundry:ApiKey` if set, else the top-level `AzureOpenAI:ApiKey`. Keep the single
  source-of-truth pattern the "endpoint is INHERITED" gotcha (`.agentic/gotchas.md:407`) protects — do not
  introduce a second copy of the key.
- **Out-of-scope services still need the credential.** Blob storage (`Program.cs:122`) and Cosmos
  (`Program.cs:42`, via Aspire) are **not** Azure OpenAI and cannot use the key — `DefaultAzureCredential`
  stays constructed for them. "No MI needed" (interview) is scoped to the Foundry/OpenAI concerns; blob and
  Cosmos keep their own auth (MI or emulator connection strings). State this in Out of scope.
- **DevHost is unaffected.** `tools/Agent42.DevHost/Program.cs` uses a canned chat client + `StaticModelCatalog`
  and never runs `ModelProviderBuilder`, so the fatal-key check does not touch offline dev. The executor
  should confirm DevHost still boots.
- **Governing docs to read first:** `.agentic/decisions/0013-multi-provider-model-catalog.md`,
  `.agentic/decisions/0014-responses-api-stateless-mode.md`, and gotchas at lines 407 (endpoint inheritance),
  452 (Responses stateful-by-default traps), 340 (qualified-id split), 359 (skills provider is a tool).
- **Infra exemplar:** the `github` provider key is a conditional Container App secret
  (`infra/modules/container-app.bicep:230`), declared as a Bicep param (`infra/main.bicep:44`) and passed
  from the `GH_MODELS_TOKEN` GitHub secret (`.github/workflows/deploy.yml:97`). Copy that shape.

## Steps

### Step 1 — Verify the key works on both surfaces (spike; HARD gate for the Responses path) — NOT parallelizable
Confirm, against the real `agent42.services.ai.azure.com` endpoint, how the Azure OpenAI **resource API
key** authenticates on each surface:
- **Chat path:** `new AzureOpenAIClient(endpoint, new AzureKeyCredential(key)).GetChatClient(dep)` streams.
  (Low risk — the Azure SDK sends the `api-key` header itself.)
- **Responses path (the risky one):** build `OpenAIClient` at `{endpoint}/openai/v1` and determine which
  header the endpoint accepts — the **`api-key` header** (expected, via a custom `PipelinePolicy`) vs.
  `Authorization: Bearer <key>` (the SDK's `ApiKeyCredential` default, likely a 401). Confirm hosted
  `web_search`/`code_interpreter` still fire with `store=false`.

Use the `spike-dotnet` skill (throwaway file outside the tree, pinned to `OpenAI 2.12.0` / `Azure.AI.OpenAI
2.1.0`), following ADR 0014's Step-1 manual-gate precedent. **Requires the user's Azure OpenAI key.**

**This gates the Responses portion of Step 3 — it is not deferrable, because prod ships
`UseResponsesApi: true` (`appsettings.json:29`) and Step 4 makes a blank key fatal, so an unverified
Responses auth path would 401 *every* prod chat turn.** If the key cannot be supplied in-session, the
executor MUST either (a) complete this spike before wiring Responses, or (b) ship `appsettings.json` with
`foundry.UseResponsesApi: false` (reverting to Chat Completions + SearXNG, ADR 0011) until the key path is
verified — never ship Responses ON + fatal key + unverified auth.
- **Verify:** spike output shows a streamed Chat completion and a Responses call with a hosted-tool call,
  and records the working header (`api-key` vs `Bearer`).

### Step 2 — Config surface (`ModelProviderOptions.cs`, `appsettings.json`)
- Add `string? ApiKey` to `FoundrySettings`; resolve it in `ProviderSettings.ResolveFoundry`
  (`provider.ApiKey` ?? `AzureOpenAI:ApiKey`).
- `appsettings.json`: set `foundry.Discover: false`; add a `Models` list for the deployments the picker
  should offer (at least the default `gpt-4o`); update the `foundry` `//` comment to document the
  `Models__Providers__foundry__ApiKey` secret and that discovery is off. Add an `AzureOpenAI:ApiKey` comment
  (the key is a secret, never committed).
- **Verify:** `dotnet build Agent42.slnx`; config binds (covered by Step 6 tests).

### Step 3 — Factory (`ChatClientFactory.cs`)
- Change `Provider.Foundry(options, endpoint, credential)` → `Provider.Foundry(options, endpoint, apiKey)`.
- Chat path: `new AzureOpenAIClient(endpoint, new AzureKeyCredential(apiKey))`.
- Responses path: build `OpenAIClient` at `{endpoint}/openai/v1` authenticating with the header Step 1
  confirmed — primary is a custom `api-key`-header `PipelinePolicy` added via `OpenAIClientOptions`
  (adapt the removed `BearerTokenPolicy` branch's shape), then the existing
  `.GetResponsesClient().AsIChatClientWithStoredOutputDisabled(model,
  includeReasoningEncryptedContent: false)`. Keep the `OPENAI001`/`MAAI001` suppressions.
- **Delete** `AzureIdentityTokenProvider` and the `BearerTokenPolicy` usage.
  Preserve the "endpoint comes only from config / SSRF-invariant" and "no IHttpClientFactory transport"
  comments. **Rewrite** the class-level XML doc (`ChatClientFactory.cs:18-21`, "Two credential shapes…") —
  both provider kinds are now key-authenticated, so that rationale is stale.
- **Verify:** `dotnet build`; Step 6 wiring tests.

### Step 4 — Builder (`ModelProviderBuilder.cs`)
- In the `AzureOpenAI` branch, resolve the key via `ResolveFoundry`; if blank, `throw
  InvalidOperationException` ("Foundry provider '{key}' has no API key: set
  Models:Providers:{key}:ApiKey or AzureOpenAI:ApiKey"). Build with `Provider.Foundry(provider, endpoint,
  key)`.
- Only add to `discovering` when `provider.Discover` is true (unchanged); when a non-discovering
  `AzureOpenAI` provider declares `Models`, add them to `declared` (mirror the OpenAICompatible
  `declared.AddRange(...)` at `ModelProviderBuilder.cs:83`, with `SupportsTools`/`SupportsVision` from config).
- **Verify:** `dotnet test --filter ModelProviderTests` (Step 6).

### Step 5 — Composition root (`Program.cs`)
- Stop passing `credential` to `ModelProviderBuilder.Build` for the Foundry provider — the builder now needs
  the key, not the token credential. (Keep passing `credential` where `ArmClient`/blob still need it.)
- Embedding client: `new AzureOpenAIClient(new Uri(azureOpenAIEndpoint), new AzureKeyCredential(key))`,
  reading the key from **`AzureOpenAI:ApiKey`** — the SAME key the `foundry` provider inherits (Step 2), so
  there is exactly one source of truth (the endpoint-inheritance invariant, gotcha `:407`). Validate it
  eagerly here like the existing endpoint/embedding-deployment checks (`Program.cs:48,132`).
- Leave `DefaultAzureCredential`, `ArmClient`, and `BlobServiceClient` MI wiring intact.
- **Verify:** `dotnet build`; app boots locally with `AzureOpenAI:ApiKey` in user secrets, fails fast without it.

### Step 6 — Tests (`ModelProviderTests.cs`, `ModelCatalogTests.cs`) — parallelizable with Step 7
- **Fix the shared helpers first** (or existing tests break under the new fatal-key rule): add
  `["AzureOpenAI:ApiKey"] = "key"` to the `Configuration()` helper (`ModelProviderTests.cs:262-270`) so both
  keyless Foundry providers — `foundry` in `Options()` (`:240`) and `foundry-swe` in
  `TwoFoundryProviders_ResolveOwnCoordinates_AndInheritWhenAbsent` (`:110`) — inherit a key via
  `provider.ApiKey ?? AzureOpenAI:ApiKey`. A provider-level-only edit would miss `foundry-swe` and the
  `Factory()`-based tests. Confirm `BlankApiKeyProvider_IsSkipped_DefaultStillResolves` (`:40`) still exercises
  the **github** skip (its `foundry` must still inherit the key and NOT throw).
- `ModelProviderTests`: assert a key-built Foundry client is non-null and that
  `ResponsesProvider_WiresResponsesEndpoint` / the Chat-vs-Responses `ProviderUri` assertions still hold.
  Add `BlankFoundryKey_IsFatal` (both `provider.ApiKey` and `AzureOpenAI:ApiKey` blank → `Build` throws).
  **Remove** `BearerBridge_MapsAzureTokenToBearer` (`:198`) — the bridge is gone. Keep `Seam_*` tests.
- `ModelCatalogTests` (or `ModelProviderTests`): a non-discovering `AzureOpenAI` provider with declared
  `Models` contributes them to `setup.DeclaredModels` / the composite catalog.
- **Verify:** `dotnet test Agent42.slnx` green; `dotnet format --verify-no-changes`.

### Step 7 — Infra & deploy (`infra/main.bicep`, `container-app.bicep`, `deploy.yml`) — parallelizable with Step 6
- Add an `azureOpenAIApiKey` Bicep param **with `@minLength`** (fatal) in `main.bicep`, and mirror the
  **`searxngSecret`** shape — NOT the `github` one: an unconditional secret + an **unconditional** env
  `secretRef` (`main.bicep:34-37`, `container-app.bicep:165-167`, unconditional env). The `github` block's
  `empty(x) ? [] : [...]` guard (`container-app.bicep:228-230`) is the *optional* shape and would be dead
  code under `@minLength`.
- Thread a **single** env var — **`AzureOpenAI__ApiKey`** — as the `secretRef`. Both the `foundry` provider
  (which inherits `AzureOpenAI:ApiKey`, Step 2) and the embedding client (Step 5) read it; do not add a
  second `Models__Providers__foundry__ApiKey` var (that would reintroduce the two-sources trap gotcha `:407`
  guards against, and the embedding client never reads the `Models:` section).
- `deploy.yml`: pass `azureOpenAIApiKey="${{ secrets.AZURE_OPENAI_API_KEY }}"`.
- **Manual prerequisites (user-owned — call out explicitly, do not execute):** create the
  `AZURE_OPENAI_API_KEY` GitHub Actions secret; the container app's MI `Cognitive Services OpenAI User` grant
  is no longer needed for inference/embeddings (leaving it is harmless).
- **Verify:** `az bicep build` / `bicep lint` clean (or the repo's Bicep validation step).

### Step 8 — ADR via `/decide`
Record the shift to API-key auth for the Foundry provider. This is a **partial** override of the credential
decisions in ADR 0013 (AzureOpenAI = managed identity) and ADR 0014 (Responses via managed-identity bearer)
— most of both ADRs stays authoritative (qualified-id split, capability flags, cross-tenant egress position;
stateless-mandatory).
- **Supersession mechanism (important):** do **NOT** set `superseded_by`/`status: superseded` on 0013 or
  0014. Per `.agentic/decisions/README.md:20`, a cold-handoff session finds active ADRs with
  `grep -rL "superseded_by"`, and `0007`'s `superseded_by: … # partial` still gets **excluded** — so a
  `superseded_by` tag would silently drop these two heavily-referenced ADRs from the active set. Instead: the
  new ADR carries `supersedes: 0013, 0014 (credential mechanism only)` in its body, and add a short
  **in-body amendment note** at the top of 0013 and 0014 pointing forward to the new ADR, keeping their
  `status: accepted` and no `superseded_by`.
- Capture: full replacement, discovery dropped, embeddings switched, fatal-on-missing-key reversal (Foundry
  is now the availability path), and the Step-1 spike result incl. the working `/openai/v1` auth header.
- depends on Steps 1–5.

### Step 9 — Gotcha via `/gotcha`
Capture the non-obvious traps: (a) Azure's `/openai/v1` accepts the resource key as a Bearer token
(Step-1 finding); (b) the Foundry key is on the **availability path** — blank is fatal, unlike third-party
keys; (c) ARM discovery is intentionally off for a key-based Foundry provider. Evidence paths + verified date.

### Step 10 — Docs sync via `/sync-docs`
Update the Foundry bullets in `CLAUDE.md` (ADR 0013/0014 lines) to say Foundry authenticates by API key,
discovery is off, and the key is a fatal prerequisite. Enumerate and fix the now-false claims the diff
invalidates (hard rule 2):
- `.agentic/gotchas.md:633-644` — "the embedding client reuses the same endpoint + `DefaultAzureCredential`"
  → now `AzureKeyCredential` from `AzureOpenAI:ApiKey`.
- `.agentic/gotchas.md:452-473` — "a stable `OpenAIClient` … with a **managed-identity bearer policy**"
  → now an API key via the `api-key` header (or the Step-1-confirmed header).
- `CLAUDE.md:146` — pre-existing drift: it says ADR 0014 "ships **off** in prod" while `appsettings.json:29`
  has `UseResponsesApi: true`. Reconcile while here (either the doc or, per Step 1(b), the setting).

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A Foundry provider builds a working Chat client from an API key (no I/O) | `Factory_*` / key-built client non-null | `ModelProviderTests.cs` |
| AC2 | The Responses-flagged Foundry provider still wires `/openai/v1` under key auth | `ResponsesProvider_WiresResponsesEndpoint` | `ModelProviderTests.cs` |
| AC3 | A blank Foundry API key fails at build (fatal) | `BlankFoundryKey_IsFatal` (new) | `ModelProviderTests.cs` |
| AC4 | A non-discovering Foundry provider contributes its declared models to the picker | new catalog/declared test | `ModelCatalogTests.cs` |
| AC5 | Chat vs Responses paths remain distinct (regression) | `ResponsesProvider_ReportsHostedTools_*`, Chat `ProviderUri` | `ModelProviderTests.cs` |
| AC6 | The managed-identity bearer bridge is gone | `AzureIdentityTokenProvider` removed; `BearerBridge_*` deleted; suite compiles/green | `ModelProviderTests.cs` |
| AC7 | Live: key authenticates Chat + Responses (hosted tools, store=false), via the confirmed `/openai/v1` header | Step-1 spike (HARD gate for Responses) | (spike harness, gitignored) |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ cda0345)

- new_issues: **0**
- new_security_issues: **0**
- duplication_pct_max: **3**
- coverage_new_code_pct_min: **80**
- cognitive_complexity_max: **15**
- dependencies.vulnerable_packages: **0** (hard block)
- add_policy: **latest-stable** (no version from memory — none added here anyway)
- Branch: `feature/*` (never `main`); MR required.

## Out of scope

- **Blob storage and Cosmos credentials.** Separate services; they keep managed identity / emulator
  connection strings. This plan is Foundry/OpenAI-only.
- **Deleting the ARM discovery machinery.** `AzureModelCatalog`/`ArmClient` stay for any future
  discovering provider; only the shipped `foundry` provider stops discovering.
- **Making the API-key path optional / MI fallback.** The user chose full replacement.
- **Rotating/creating the actual secret and RBAC changes.** Manual, user-owned (hard rule 5).
- **A per-provider egress policy** (unchanged from ADR 0013).

## Review

Critic pass (fresh-context adversarial, against ADRs/gotchas/code). Six findings, **all accepted and
fixed**; none rejected.

- **HIGH-1 — resource-key-as-Bearer likely 401s on `/openai/v1`, and it's on the prod availability path.**
  Fixed: Step 1 is now a HARD gate for the Responses portion (not deferrable), the primary Responses design
  is a custom `api-key`-header policy (Bearer is the SDK default and the likely-failing path), the
  `AzureOpenAIClient.GetResponsesClient` contingency is explicitly ruled out (2.2.0-beta, rejected by hard
  rule 4), and Step 1(b) offers shipping `UseResponsesApi: false` until verified. (Context + Steps 1, 3.)
- **MEDIUM-2 — fatal-key throw breaks two existing tests.** Fixed: Step 6 now fixes the shared
  `Configuration()` helper with `AzureOpenAI:ApiKey` so `foundry`/`foundry-swe` inherit it, and enumerates
  the two at-risk tests (`TwoFoundryProviders_…`, `BlankApiKeyProvider_IsSkipped_…`).
- **MEDIUM-3 — embedding key source incoherent.** Fixed: single source `AzureOpenAI:ApiKey`, threaded as one
  `AzureOpenAI__ApiKey` env var; no second `Models__Providers__foundry__ApiKey`. (Steps 5, 7.)
- **MEDIUM-4 — infra step mixed the optional (`github`) and fatal (`searxng`) exemplars.** Fixed: Step 7 now
  names `searxngSecret` as the shape (unconditional secret + env, `@minLength`) and calls out the `github`
  conditional guard as the wrong one.
- **MEDIUM-5 — partial supersession would drop 0013/0014 from the active-ADR set.** Fixed: Step 8 forbids
  `superseded_by` on 0013/0014, using a forward-pointing in-body amendment instead (the `grep -rL` active-set
  mechanism confirmed in `README.md:20`).
- **LOW-6 — unpaid doc debt.** Fixed: Step 10 enumerates the stale claims at `gotchas.md:633-644`, `:452-473`,
  the class-level XML doc (Step 3), and the pre-existing `CLAUDE.md:146` "ships off in prod" drift.

Critic's "clean" confirmations retained: base commit resolves on `main`; DevHost isolation holds; `credential`
usage coverage is complete (blob/Cosmos correctly kept on MI); infra exemplar line refs accurate; gotchas at
`:340` and `:452` (#1/#2) preserved, not tripped.

## Implementation divergences (executed 2026-07-16)

Executed on `feature/foundry-provider-api-key-auth`. Where the implementation diverged from the plan:

- **`gpt-4o` is not deployed → default deployment is now `gpt-5.4`.** The Step-1 spike found the account
  (`agent42.services.ai.azure.com`) has **no `gpt-4o` deployment** — its chat deployments are `gpt-5.4`,
  `gpt-4.1`, `gpt-5.1`, `o3`, `model-router`, `claude-*`. Since discovery is dropped, an unreachable default
  would 404 every turn, so the default deployment moved to **`gpt-5.4`** (user-confirmed mid-execution) in
  `appsettings.json` (`AzureOpenAI:DeploymentName` + the declared `Models` list) and `infra/main.bicep`
  (`azureOpenAIDeploymentName`). The plan had assumed `gpt-4o` throughout (Steps 2, 6). Test fixtures keep
  the arbitrary `foundry:gpt-4o` id (no I/O — it only names a client).
- **Step-1 result: the resource key authenticates on BOTH header positions.** The plan predicted plain
  Bearer would *likely 401* on `/openai/v1` and that the `api-key` header was the fix. The spike found the
  correct key gets **past auth on both** the `api-key` header AND `Authorization: Bearer` (a wrong key 401s
  on both). Shipped the **`api-key`-header** `AuthenticationPolicy` anyway — it is Azure's documented
  mechanism (Bearer there is undocumented and could regress). Hosted `web_search` fired with `store=false`.
- **`UseResponsesApi` stays `true`.** Because Step 1 verified the Responses key path live, the plan's
  fallback (ship `UseResponsesApi: false`, Step 1(b)) was **not** taken; the flag stays on, and the
  pre-existing `CLAUDE.md` "ships off in prod" drift was reconciled to "ships on".
- **`ApiKeyHeaderPolicy` is `public` + has a no-I/O unit test.** Not specified by the plan's Step 6, but the
  policy's `Process`/`ProcessAsync` run only on live I/O, so leaving them private dropped new-code coverage
  below the 80% gate (same situation ADR 0014 hit with the bearer bridge). Made public and added
  `ApiKeyHeaderPolicy_SetsApiKeyHeader_NotBearer` (both sync + async), mirroring the `Provider`/old-bridge
  precedent. Now 100% covered.
- **`ModelProviderBuilder.Build` dropped its `credential` parameter entirely.** The plan (Step 5) said stop
  passing `credential` to the Foundry build; in practice `credential` was the builder's *only* use of that
  parameter, so the whole parameter was removed (callers in `Program.cs` and the tests updated). `Program.cs`
  still constructs `DefaultAzureCredential` for blob/`ArmClient`.
- **Extracted a `DeclaredModels` helper** in `ModelProviderBuilder` — the non-discovering-Foundry and
  OpenAI-compatible branches build `ModelInfo`s identically; deduped to satisfy the duplication gate.
- **New invariant documented (critic MEDIUM-1):** with discovery off, `CompositeModelCatalog` only
  synthesizes the default when the declared list is *empty* — so the **default deployment must be listed in
  `Models`**, and `AzureOpenAI:DeploymentName` / the Bicep `azureOpenAIDeploymentName` must stay in sync with
  that list. Captured in the ADR, the new gotcha, and the appsettings comment.
- **Doc debt beyond Step 10 (critic + hard rule 2):** also fixed `README.md` (standalone-run needs the key,
  not `az login`), `CLAUDE.md` grant/RAG lines, `infra/main.bicep` embedding-param description, the
  second-Foundry-account grant note in the endpoint-inheritance gotcha, and a forward-pointing amendment on
  ADR 0007 (embedding credential). The deploy.yml `Cognitive Services OpenAI User` grant step was **left in
  place** (harmless per plan); docs now note it is no longer required.

**Verification:** full suite **264 tests green**, `dotnet format` clean, `dotnet build` 0 errors,
`az bicep build` clean, DevHost boots offline (fatal-key check does not touch it). Critic pass: 5 findings
(3 MEDIUM doc/invariant, 2 LOW), all fixed; no ADR contradictions or tripped gotchas.
