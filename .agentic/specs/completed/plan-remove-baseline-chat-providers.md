# Plan — Remove the fixed/baseline chat providers (chat becomes fully per-user)

planned-against: main @ 876a290

## Overview

Today the app ships a **baseline chat-provider catalog** in `appsettings.json` (`Models:Providers`:
`foundry` + `github`) and derives a default model from it. ADR 0018 made that baseline the **boot-time
availability floor** — the app always starts on it. This plan **removes the baseline chat providers
entirely**: chat providers become **only** the per-user overlay (ADR 0018), the app boots with **zero**
chat providers, and a user with no configured provider sees an **empty picker** and a gated composer until
they add one in Preferences. The **only** fixed model that remains is the **embedding model + endpoint**
(`AzureOpenAI:Endpoint`/`ApiKey`/`EmbeddingDeploymentName`) for document RAG.

Why now: the user wants chat entirely self-managed per user, with no operator-shipped default model.

**Governing decision:** this reverses ADR 0018's availability-floor invariant (and retires ADR 0017's
"blank baseline Foundry key is fatal" rule for chat — there is no baseline Foundry provider to guard).
**A new ADR amending 0018 is a mandatory first step (Step 1).**

## Design in one paragraph (read before touching anything)

**The `Models:` mechanism stays; the baseline ships empty; the grain keeps a valid agent even with no
model via a no-op client.** `ModelProviderBuilder.Build` already returns an empty setup for an empty
`Providers` map (no code change). The load-bearing move: **`MultiProviderChatClientFactory`, when it holds
zero providers, returns a shared no-op `NoModelChatClient` from `GetClient` and `(false,false)` from
`GetCapabilities`, and its `DefaultModelId` is `""`.** Because `BuildAgent` only *wraps* the client
(`ChatClientAgent` construction and all session serialize/deserialize/create ops never call inference —
verified at `AgentGrain.cs:228-269`), the grain **always** builds a valid `_agent`/`_session` — even with
no model — so `GetHistoryAsync`, `BindAgentAsync`, `RegisterDocumentAsync`, `ReceiveReminder`,
rehydrate/align all keep working unchanged (this is what avoids scattering null-guards). Only the two sites
that actually **run** the model must act: `ChatCoreAsync` and `ReceiveReminder`. The "no model" signal is
`string.IsNullOrEmpty(_chatClientFactory.DefaultModelId)` (⟺ the factory has zero providers). A provider is
required to declare **≥1 model** (API validation), so "factory has providers ⟺ a usable default exists" —
no "provider present but modelless" gap. The factory is resolved **once per activation**, so when a user
**adds their first provider and then chats in that same still-activated grain**, `ChatCoreAsync` must
**re-resolve** `GetForUserAsync(_userId)` (hence `_userId` is now a retained field) and **rebind** the
conversation onto the new default (snapshotting the existing session so history is preserved). If after
re-resolving there is still no provider, it yields a one-line notice instead of running the no-op client.
The frontend gates the composer on a non-empty picker. `AzureOpenAI:*` stays **fatal-if-blank for
embeddings only**.

## Affected areas (verified against the tree @ 876a290)

New file:
- `src/Agent42.Backend/NoModelChatClient.cs` — a minimal no-op `IChatClient`: `GetResponseAsync` /
  `GetStreamingResponseAsync` throw a clear `InvalidOperationException("No model provider is configured")`
  (never actually invoked — the grain guards before running), `GetService` returns null, `Dispose` no-op.
  A single shared instance the empty factory hands out.

Changed — code:
- `src/Agent42.Backend/ChatClientFactory.cs` (`MultiProviderChatClientFactory`) — tolerate an empty
  provider dict: ctor no longer throws on `providers.Count == 0`; `_defaultProvider` guards the
  `providers.Keys.First()` call; when empty, `GetClient` returns the shared `NoModelChatClient` and
  `GetCapabilities` returns `new(false, false)` (before reaching `Resolve`, which assumes ≥1 provider).
  `DefaultModelId` is whatever it's constructed with (`""` for the empty per-user case).
- `src/Agent42.Backend/UserProviderResolver.cs` — `EffectiveDefault` returns, in order: a valid overlay
  default → a valid baseline default → the **first resolved declared model** (`DeclaredModels[0].Id`) →
  `""`. With the ≥1-model API rule, a non-empty `Providers` always yields a non-empty `DeclaredModels`, so
  this is `""` only for genuinely zero providers.
- `src/Agent42.Backend/ModelCatalog.cs` (`CompositeModelCatalog`) — guard the `models.Count == 0` synthesize
  branch (`:157-162`) with `!string.IsNullOrEmpty(defaultModelId)`: an empty default ⇒ an empty catalog (no
  bogus `""` model).
- `src/Agent42.Agents/AgentGrain.cs`:
  - Add `private string _userId = null!;`, set in `OnActivateAsync` from `UserIdFromGrainKey(...)` (already
    computed there). `_modelId` becomes `""` when the factory is empty:
    `_modelId = string.IsNullOrEmpty(_chatClientFactory.DefaultModelId) ? "" : (_state.State.DeploymentName ?? _chatClientFactory.DefaultModelId)`.
    `BuildAgent`/session creation run **unchanged** (against the no-op client when empty) — history is
    restored normally, so no null-`_session` and no M3 history-loss risk.
  - `ChatCoreAsync` — a new block at the top (before the existing model-switch branch): if
    `string.IsNullOrEmpty(_modelId)`, **re-resolve** `_chatClientFactory = await _chatClientFactoryProvider.GetForUserAsync(_userId)`;
    if it now has a model, **rebind** — snapshot the session, set `_modelId`/`_state.State.DeploymentName`
    to `model ?? DefaultModelId`, `BuildAgent`, deserialize the snapshot (history preserved), and null out
    the local `model` so the switch branch doesn't re-run. If still no model → `yield` a `TextDelta` notice
    + `Done` and return (never runs the no-op client, never writes state).
  - `ReceiveReminder` — same guard: if `_modelId` is empty, re-resolve + rebind to `DefaultModelId`; if
    still none, log and return (a reminder fired with no model configured — skip, don't run the no-op).
- `src/Agent42.Backend/AgentApi.cs` — `ValidateProvider` (or the POST/PUT handlers) rejects a provider that
  declares **zero models** ("A provider must declare at least one model."), closing M2. `/api/providers`
  request already carries `Models`.
- `src/Agent42.Backend/Program.cs` — remove the `FoundryProviderKey` const + the
  `modelOptions.Default = ModelId.Qualify(...)` derivation block (`~72,~90-100`); remove the eager
  `app.Services.GetRequiredService<IChatClientFactory>().GetClient(null)` startup call (`~356`). **Keep**
  the `AzureOpenAI:ApiKey` and `AzureOpenAI:EmbeddingDeploymentName` fatal-if-blank checks (embeddings).
- `src/Agent42.Backend/appsettings.json` — `Models:Providers` → `{}`; drop the `foundry`/`github` blocks and
  the `//Default` derivation comment; keep `Models:Default: ""`. Remove the now-unused chat-only `AzureOpenAI`
  keys `DeploymentName`, `SubscriptionId`, `ResourceGroup`, `AccountName`. **Keep** `Endpoint`, `ApiKey`
  (rewrite its `//ApiKey` comment: it authenticates **embeddings** now, not a Foundry chat provider),
  `EmbeddingDeploymentName`, `TenantId`.
- `src/Agent42.Backend/wwwroot/app.js` — `loadModels()`: when `modelsData.models` is empty, disable the
  composer send + show a hint ("Add a model provider in Preferences to start chatting"); re-enable after the
  `loadModels()` in `saveProvider`. `index.html`/`styles.css` — the hint element + style.

Changed — infra (remove the 4 chat-only params/env vars; keep embeddings + credential + the guard):
- `infra/main.bicep` — remove params `azureOpenAIDeploymentName` (88), `azureOpenAISubscriptionId` (97),
  `azureOpenAIResourceGroup` (100), `azureOpenAIAccountName` (103). Keep `azureOpenAIEndpoint`,
  `azureOpenAIApiKey` (+ `@minLength(32)` — embeddings availability path), `azureOpenAIEmbeddingDeploymentName`,
  `azureOpenAITenantId`.
- `infra/modules/container-app.bicep` — remove env vars `AzureOpenAI__DeploymentName` (212),
  `AzureOpenAI__SubscriptionId` (216), `AzureOpenAI__ResourceGroup` (217), `AzureOpenAI__AccountName` (218).
- `.github/workflows/deploy.yml` — does **not** pass the 4 removed params (they used main.bicep defaults);
  confirm during execution, change only if a reference is found.

Changed — tests:
- `tests/Agent42.Tests/ModelProviderTests.cs` — ADD empty-provider cases (AC1/AC2). Existing tests stay
  green (they build their own non-empty in-memory `Options()`).
- `tests/Agent42.Tests/ModelCatalogTests.cs` — ADD the empty-default ⇒ empty-catalog case (AC4).
- `tests/Agent42.Tests/UserProviderResolverTests.cs` — ADD no-explicit-default⇒first-model and
  zero-providers⇒`""` cases (AC3/AC4).
- `tests/Agent42.Tests/AgentGrainTests.cs` — ADD the no-model notice + re-resolve/rebind + history-preservation
  cases (AC5/AC6/AC7). Needs a **factory-provider double** that returns an empty factory first, then a
  non-empty one after a "provider added" (NOT a single fixed `IChatClientFactory`) — see AC notes.
- `tests/Agent42.Tests/ProvidersApiTests.cs` — ADD the zero-models ⇒ 400 case (AC9).

Changed — docs (Step 8): new ADR; `.agentic/gotchas.md` (update the two named entries) + `CLAUDE.md`.

Unchanged on purpose:
- **`ModelProviderBuilder.cs`** — already handles an empty `Providers` map and still throws on a
  misconfigured **non-empty** baseline provider; that guard is now dead-in-prod but the mechanism +
  `BlankFoundryKey_IsFatal` test stay intact. No change.
- **DevHost / AppHost** — DevHost keeps its canned `StaticModelCatalog` + `SingleChatClientFactory`
  (`foundry:gpt-4o`) so offline chat still works; the empty-provider path is unit-tested, not exercised
  offline. Out of scope.
- **`AgentGrain` methods `BindAgentAsync`/`RegisterDocumentAsync`/`GetHistoryAsync`/rehydrate/align** — they
  keep working against the no-op client with **no change** (this is the whole point of the no-op approach).
- **Per-user overlay grain / secret protector / `/api/providers` CRUD** (ADR 0018) — untouched except the
  ≥1-model validation.
- **The no-inheritance rule** (ADR 0018): a user Foundry provider still declares its own endpoint + key.
- **`ArmClient`/`AzureModelCatalog`** — ARM discovery is now fully dead, but ripping it out is orthogonal;
  leave it (harmless). Future cleanup.

## Context & constraints (what the executor can't cheaply re-derive)

- **`BuildAgent` never invokes inference** — `ChatClientAgent(GetClient(modelId), options)` (`AgentGrain.cs:228-269`)
  only *wraps* the client; `CreateSessionAsync`/`Serialize`/`Deserialize` operate on in-memory history. So a
  no-op client is safe for build + all session ops; only `RunStreamingAsync`(`:546`)/`RunAsync`(`:1079`)
  hit the client. This is what makes the no-op-client design collapse the null-deref problem to two sites.
- **Every `_agent`/`_session` deref site was traced.** Build/history/bind/register/reminder/rehydrate/align
  (`AgentGrain.cs:350,388,433,546,575,593,1073,1079,1096,1128,1220`) all stay non-null under the no-op
  design. Do NOT add scattered null-guards — that was the rejected alternative (see Review B-alt).
- **The factory is resolved once per activation** (`OnActivateAsync:103`), and `userId` was a local. A user
  who removed all providers, reopened an existing conversation (grain activates empty), then re-added a
  provider and sends **in that same activation** would keep the empty factory. Hence `_userId` is retained
  and `ChatCoreAsync` re-resolves on the no-model path. Reachable via `selectConversation` →
  `GET /messages|/model|/agent` (`app.js:740-744`). This is the B1 fix.
- **A provider with zero declared models breaks "providers ⟺ usable default."** An overlay provider always
  has `Discover=false` (`UserProviderResolver.cs:139`), so it contributes to the picker only via its
  declared `Models`. `ValidateProvider` (`AgentApi.cs:305-325`) must require ≥1 model, else a keyed-but-
  modelless provider yields `providers.Count==1` but `DefaultModelId==""` and the grain wrongly treats the
  user as no-model (M2).
- **The no-model notice is a `TextDelta` + `Done`**, ephemeral (never persisted) — `AgentStreamEvent` has no
  `Error` type (`AgentStreamEvent.cs`), and adding one is unnecessary. Mirror the vision-withheld notice.
- **`AzureOpenAI:ApiKey` stays the availability path for EMBEDDINGS** — its `@minLength(32)` Bicep guard
  (`main.bicep:83-84`) and the `Program.cs` fatal check (`:57-69`) stay. The 0017 "Foundry key is the
  availability path" framing narrows to "the embedding key is the availability path."
- **Existing provider/catalog/resolver tests build their own in-memory non-empty fixtures** (`Options()`,
  `Baseline()`, `Configuration()`), not appsettings — verified. Emptying appsettings + tolerating empty is
  strictly more permissive; they stay green. ADD cases; don't rewrite.
- **Exemplars to copy:** notice shape → the `yield return` sites (`AgentGrain.cs:536,553,582`); frontend
  disable/hint → the `modelSelect.disabled` logic in `loadModels` (`app.js:123-134`).
- **Two gotchas the diff invalidates (fix in this MR, hard rule 2):** *"The per-user chat-client factory is
  resolved on grain activation"* (~gotchas 491-499) — the `_modelId = … ?? DefaultModelId` ordering is now
  conditional and gains a re-resolve; and *"A user provider overlay is best-effort (baseline wins)"*
  (~gotchas 437-446) — the baseline is now **empty**, not a floor, so "baseline wins" no longer applies.

## Steps

1. **ADR — amend 0018 (+ narrow 0017): remove the baseline availability floor.** Run `/decide`. Record:
   chat providers are now **only** per-user; the app boots with zero chat providers; a user with none gets
   an empty picker + gated composer + (defense) a grain notice, and the grain keeps a valid session via a
   no-op client; the **embedding** endpoint/key is the one remaining fixed model and stays fatal-if-blank;
   0017's "blank baseline Foundry key is fatal" is retired for chat, survives for embeddings. *Verify:* ADR
   exists; amendment note in 0018; 0017's availability-path note narrowed to embeddings. **Blocks all steps.**

2. **Factory tolerates empty (no-op client).** New `NoModelChatClient`; `MultiProviderChatClientFactory` no
   ctor throw on empty; empty → `GetClient` returns the no-op, `GetCapabilities` returns `(false,false)`,
   `DefaultModelId==""`. *Files:* `NoModelChatClient.cs`, `ChatClientFactory.cs`. *Verify:* new tests — empty
   factory constructs, `DefaultModelId==""`, `GetClient(null)` returns a client (the no-op) whose
   `GetStreamingResponseAsync` throws the clear message; existing `ModelProviderTests` green.
   `dotnet test --filter ModelProvider`.

3. **Resolver effective-default + empty catalog.** `EffectiveDefault` → overlay → baseline → first declared
   model → `""`. `CompositeModelCatalog` no synthetic model on empty default. *Files:* `UserProviderResolver.cs`,
   `ModelCatalog.cs`. *Verify:* new tests — one provider + no explicit default ⇒ that provider's first model;
   zero providers ⇒ `DefaultModelId==""` and empty catalog; existing tests green.
   `dotnet test --filter "ModelCatalog|UserProviderResolver"`.

4. **Grain no-model path (no-op build + re-resolve + notice).** Retain `_userId`; `_modelId==""` when factory
   empty; `ChatCoreAsync` re-resolve+rebind+notice block; `ReceiveReminder` guard. *Files:* `AgentGrain.cs`.
   *Verify:* new grain tests (AC5/6/7) using a factory-provider double (empty → non-empty). `dotnet test --filter AgentGrain`.

5. **Provider must declare ≥1 model.** `ValidateProvider` rejects zero models. *Files:* `AgentApi.cs`,
   `ProvidersApiTests.cs`. *Verify:* new test — POST a provider with no models ⇒ 400; existing `Provider`
   tests green. `dotnet test --filter Provider`.

6. **Program.cs + appsettings.** Remove default-derivation + eager `GetClient(null)`; empty `Models:Providers`;
   drop chat-only `AzureOpenAI` keys; keep embeddings fatal. *Files:* `Program.cs`, `appsettings.json`.
   *Verify:* `dotnet build`; `dotnet run --project tools/Agent42.DevHost` still starts (proves no coupling).

7. **Infra.** Remove the 4 chat-only params + env vars; keep embeddings + credential + `@minLength(32)`.
   *Files:* `infra/main.bicep`, `infra/modules/container-app.bicep`, (`deploy.yml` only if referenced).
   *Verify:* `az bicep build --file infra/main.bicep` succeeds (or a careful read + grep per removed name).

8. **Frontend empty-picker gate.** `loadModels()` disables send + shows the hint on empty; re-enables on
   provider save. *Files:* `app.js`, `index.html`, `styles.css`. *Verify:* manual DevHost (has models ⇒ no
   regression); reason/verify the empty branch. `/verify` the flow.

9. **Doc sync.** New gotcha (chat is fully per-user, empty until configured; the embedding key is the sole
   availability path; the grain stays valid via a no-op client). Update the **two named** gotchas
   (activation-ordering; baseline-wins) and the `CLAUDE.md` model-providers bullets. Run `/gotcha` +
   `/sync-docs`; `/audit-docs` clean. *Verify:* `node .agentic/scripts/check-doc-paths.mjs`; `dotnet build`.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | An empty provider set constructs a factory (no throw); `DefaultModelId==""` | `MultiProviderChatClientFactory_EmptyProviders_ConstructsWithNoDefault` | `ModelProviderTests.cs` |
| AC2 | An empty factory's `GetClient` returns a client whose run throws a clear message (not `KeyNotFoundException`) | `EmptyProviders_GetClient_ReturnsNoModelClient_ThrowsOnRun` | `ModelProviderTests.cs` |
| AC3 | A user with a provider but no explicit default resolves to that provider's first model | `UserProviderResolver_NoOverlayDefault_UsesFirstResolvedModel` | `UserProviderResolverTests.cs` |
| AC4 | Zero providers ⇒ resolver default `""` AND the catalog lists no models | `UserProviderResolver_NoProviders_EmptyDefault`; `CompositeModelCatalog_EmptyDefault_ReturnsNoModels` | `UserProviderResolverTests.cs`, `ModelCatalogTests.cs` |
| AC5 | A grain with no model yields a notice + Done and does not crash; `GetHistoryAsync` on it returns (no NRE) | `AgentGrain_NoModel_YieldsNotice_AndHistoryDoesNotThrow` | `AgentGrainTests.cs` |
| AC6 | Adding a provider then chatting in the same activation re-resolves, binds, and replies | `AgentGrain_NoModelThenProviderAdded_ReResolvesAndReplies` (factory-provider double: empty→non-empty) | `AgentGrainTests.cs` |
| AC7 | Re-binding onto a regained model preserves existing history | `AgentGrain_RebindAfterRegainingModel_PreservesHistory` | `AgentGrainTests.cs` |
| AC8 | A provider that declares zero models is rejected (400) | `ProvidersApi_RejectsProviderWithNoModels` | `ProvidersApiTests.cs` |
| AC9 | Baseline-shaped tests stay green (non-empty in-memory fixtures unaffected) | existing `ModelProviderTests`, `ModelCatalogTests`, `UserProviderResolverTests` | (existing) |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` @ 876a290)

- `new_issues: 0` · `new_security_issues: 0` · `duplication_pct_max: 3` · `coverage_new_code_pct_min: 80`
  · `cognitive_complexity_max: 15` · `dependencies.vulnerable_packages: 0` (hard block)
- Branch: `feature/*` + MR (never `main`). Tests alongside each step.

## Out of scope

- **Removing `ArmClient`/`AzureModelCatalog`** — dead now, but orthogonal; leave it (harmless). Future cleanup.
- **DevHost showing the empty state** — keeps canned models so offline chat works; empty path is unit-tested.
- **Seeding a Foundry provider from `AzureOpenAI:*` on first run** — rejected by the user (empty picker until configured).
- **Letting a user Foundry provider inherit `AzureOpenAI:*`** — rejected (keep ADR 0018's no-inheritance rule).
- **A dedicated `Error` stream-event type** — the text notice suffices.

## Review (critic pass, pre-approval)

Verdict on the first draft: **do not approve as written** — 1 BLOCKER + 3 MAJOR + 2 MINOR, all accepted and
folded in. The redesign (no-op `NoModelChatClient` so `_agent`/`_session` stay valid) is the structural
response to B1/M1/M3.

- **B1 (fixed)** — the empty factory is captured for the whole activation, so "build fresh when a model
  arrives" was impossible (nothing to build from) and the draft's AC6 double contradicted itself (`GetClient`
  throws yet "builds and replies"). Fix: retain `_userId`; `ChatCoreAsync` **re-resolves** `GetForUserAsync`
  on the no-model path and rebinds; AC6 now uses a **factory-provider double** (empty→non-empty), not a fixed
  `IChatClientFactory`.
- **M1 (dissolved)** — the draft guarded only 2 of 6 `_agent`/`_session` deref sites (GetHistory, Bind,
  Register, Reminder also NRE). The no-op-client design keeps all of them non-null, so no scattered guards;
  only the two *run* sites act.
- **M2 (fixed)** — a keyed provider with zero declared models broke "providers ⟺ usable default." Added an
  API rule: a provider must declare ≥1 model (Step 5, AC8).
- **M3 (dissolved)** — history-loss risk (skip session create, then overwrite) is gone: with the no-op
  client, `OnActivateAsync` restores the persisted session normally; the re-resolve path snapshots→rebuilds→
  restores (AC7 pins history preservation).
- **m1 (fixed)** — the draft's AC8 ("chat no longer fatal") had no real test; replaced by AC1 (empty factory
  constructs) + the unchanged embedding fatal check; the DevHost start in Step 6 is the honest smoke.
- **m2 (fixed)** — Step 9 now names the two specific gotchas the diff invalidates.
- **B-alt (rejected)** — the "leave `_agent`/`_session` null + guard all 6 sites" approach: rejected as
  fragile (each site a miss-risk) in favour of the centralized no-op client, which the code supports because
  `BuildAgent` never invokes inference.

Cleared by the critic (no change needed): the test-impact assessment (existing fixtures stay green); the
`CompositeModelCatalog` empty-default guard; `ResolvedProviderSet.DeclaredModels` carries what
`EffectiveDefault` needs; the infra param removal; ADR-first is correctly mandatory.

## Implementation notes — where execution diverged from the plan (2026-07-17)

Executed on `feature/remove-baseline-chat-providers` (base `main` @ 876a290). All 9 steps landed; the full
suite is green (295 passed). Divergences and additions beyond the plan-as-written:

1. **Removed the `githubModelsToken` / `GH_MODELS_TOKEN` infra wiring (beyond the plan's infra scope).**
   The plan's infra section listed only the 4 chat-only `AzureOpenAI:*` params/env vars. But dropping the
   `github` baseline provider from `appsettings.json` (Step 6) made the `githubModelsToken` →
   `Models__Providers__github__ApiKey` wiring **actively harmful**, not merely dead: with no `github` block
   declaring `Kind`/`Endpoint`, that env var would bind a `Models:Providers:github` node whose `Kind`
   defaults to `AzureOpenAI` (enum 0) → `TryBuildProvider` builds it as a **Foundry** provider on the
   inherited `AzureOpenAI:Endpoint` with the GitHub PAT as key → a non-empty, broken baseline provider,
   re-breaking ADR 0019's empty-baseline invariant. So the whole `githubModelsToken` chain was removed from
   `infra/main.bicep`, `infra/modules/container-app.bicep`, and `.github/workflows/deploy.yml`. GitHub Models
   is now a per-user provider (ADR 0018). Captured in the new "Chat is fully per-user" gotcha.

2. **Extracted a shared `RebindModelAsync(modelId)` grain helper** rather than inlining the re-resolve
   rebind twice. Used by both the `ChatCoreAsync` no-model path and the `ReceiveReminder` guard; it does
   snapshot → rebuild → restore **and** the no-tools-downgrade `StripToolHistory` (mirroring the existing
   picker-switch and bind paths — the plan didn't call the strip out, but it's required for correctness if a
   conversation regains a no-tools model with tool history). Keeps `ChatCoreAsync` cognitive complexity down.

3. **`ValidateProvider` gained a `models` parameter** (both POST and PUT handlers pass `request.Models`),
   implementing the ≥1-model rule (Step 5, closing M2/AC8).

4. **Test-infra additions for AC5/6/7:** a `SwitchableChatClientFactoryProvider` double (empty→non-empty)
   and a `NoModelSiloConfigurator` (new file `tests/Agent42.Tests/NoModelSiloConfigurator.cs`). The 3
   no-model grain tests run on a **single-silo** cluster — the double is a per-silo singleton, so the default
   2-silo `TestCluster` could activate the grain on a different silo than the one the test flips. Added a
   `NewClusterAsync<T>(short initialSilosCount = 2)` overload; the 3 tests pass `(1)`.

5. **Updated `ProvidersApiTests.Put_BlankKey_KeepsStoredKey`** to include a `models` array — the new ≥1-model
   rule legitimately 400s a PUT with no models. This is a real provider shape, not a workaround.

6. **Amended more gotchas than the two named.** The plan named the activation-ordering and baseline-wins
   gotchas. The diff also invalidated specific claims in the "endpoint is INHERITED" gotcha (the
   `Models:Default` derivation is gone) and the "Azure `/openai/v1` … three Foundry-key traps" gotcha (the
   availability path narrowed to embeddings; the shipped `foundry` provider + `azureOpenAIDeploymentName`
   are gone). Both got ADR-0019 amendment notes (hard rule 2). New gotcha added:
   "Chat is fully per-user — the app ships NO baseline chat provider…". CLAUDE.md's two provider bullets got
   leading ADR-0019 override notes.

7. **`ArmClient`/`AzureModelCatalog` left in place** (plan's "Out of scope") — now fully dead in prod, a
   future cleanup.
