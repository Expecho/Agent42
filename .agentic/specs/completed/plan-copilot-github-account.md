# Plan: Copilot agents on the user's own GitHub account (Copilot subscription)

planned-against: main @ ddb74af

## Overview

Copilot agents (copilot-kind chats, ADR 0085/0107, and fleet Copilot agents, ADR 0097) run today only on BYOK: the
runtime is handed the user's provider (`SessionConfig.Provider`) and never signs in to GitHub. The user wants them to
also run on **their own GitHub Copilot subscription**, so the Copilot plan's models (Claude, GPT, Gemini…) are usable
without a separate API key. A new provider kind, `GitHubCopilot`, is connected with **"Sign in with GitHub"** (OAuth),
its models are **discovered live** from the user's plan, and they are **usable only by Copilot agents**: hidden from
every ordinary picker and refused server-side outside the Copilot runtime. It is an **add-on**: a user still needs
one ordinary (BYOK) provider for plain chats, the default model and fleet planning (user decision 5).

## Affected areas (verified against the tree at ddb74af)

- `src/Agent42.Backend/Models/ModelProviderOptions.cs` — `ProviderKind` enum (append `GitHubCopilot` at the END).
- `src/Agent42.Backend/Models/ModelProviderBuilder.cs` — `TryBuildDeclaredProvider` (:147) falls through to
  OpenAI-compatible for every non-Anthropic kind (:170-173); needs an explicit branch BEFORE the blank-endpoint
  Invalid rule (:163).
- `src/Agent42.Backend/Models/ChatClientFactory.cs` — `Provider.*` builders (:319/:410/:442), `GetCapabilities` (:92),
  `GetCopilotProvider` (:158-167), `Resolve` (:171-190, silently falls back to the default model for a missing
  provider).
- `src/Agent42.Backend/Copilot/CopilotProviderSettings.cs` — record + `ICopilotProviderSource`.
- `src/Agent42.Backend/Copilot/CopilotAgentFactory.cs` — `CreateAgent` gate (:112-115), `BuildSessionConfig`
  `Provider =` (:209-215), `MinimalEnvironment`/`ShellEnvironment`.
- `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs` — gate (:96-103), `BuildSessionConfig` `Provider =` (:131).
- `src/Agent42.Backend/Fleet/FleetTaskService.cs` (`IsToolCapable` :313) and `FleetTaskService.Run.cs` (~:145, builds
  a client per tool-capable model and passes `catalogModels` to the Copilot factory); `FleetPrompts.cs` (:102-116).
- `src/Agent42.Backend/Models/ModelCatalog.cs` (`ModelInfo`, `CompositeModelCatalog`),
  `src/Agent42.Backend/Models/UserModelCatalogProvider.cs` (fresh catalog per `/api/models` call, :36-49),
  `src/Agent42.Backend/Models/UserProviderResolver.cs` (`Merge` :79-123, `EffectiveDefault` ~:159),
  `src/Agent42.Backend/Models/AgentApi.Models.cs` (`ValidateProvider` :144-176, `Mask`, `/api/models` :186-197).
- `src/Agent42.Agents/Models/UserProviderConfig.cs` — `StoredProvider` (Kind is a string; `ApiKeyCiphertext`).
- New: GitHub OAuth start/callback endpoints (an `AgentApi.*` partial, or inside `AgentApi.Models.cs`), a pending-state
  grain (shape of `src/Agent42.Agents/Mcp/UserMcpOAuthPendingGrain.cs`), a Copilot model directory (cache) under
  `src/Agent42.Backend/Copilot/`.
- Frontend: `src/Agent42.Backend/wwwroot/index.html` (kind `<option>`s :870-873), `wwwroot/js/providers.js`
  (`syncProviderKindUi` :213-220, save :355), `wwwroot/js/models.js` (`fillModelOptions` :63-81),
  `wwwroot/js/agents.js` (:149-190, kind lock), `wwwroot/js/dom.js`, `wwwroot/sw.js` (`CACHE` bump, rule 3).
- Infra/config: `src/Agent42.Backend/appsettings.json`, `infra/modules/container-app.bicep`, `infra/main.bicep`,
  `.github/workflows/deploy.yml` (a new secret), `CLAUDE.md` (manual-prerequisites table + feature map).
- Tests: `tests/Agent42.Tests/{CopilotRuntimeTests,FleetCopilotAgentFactoryTests,ModelProviderTests,ProvidersApiTests,
  ProvidersAssetsTests,UserProviderResolverTests,GrainStorageSerializerTests,AgentApiReachabilityTests}.cs`,
  a new `LiveCopilotGitHubTests.cs`.
- Docs: `docs/features/bring-your-own-models.md`, `docs/features/custom-agents.md`, `docs/features/agent-fleet.md`,
  `.agentic/gotchas.md`, ADRs 0013/0018/0085/0097 (amendment notes), new ADR.

## Context & constraints

**Spike, 2026-10-20 (GitHub.Copilot.SDK 1.0.16 = the repo pin, `Mode.Empty`, `UseLoggedInUser = false`, Windows
minimal env, no `Provider`).** Script was ephemeral (scratchpad); the result is recorded here:
- Per-session `SessionConfig.GitHubToken` = the `gh auth token` OAuth token (`gho_…`):
  `client.Rpc.Models.ListAsync(null, token)` → 21 models (`auto, claude-sonnet-5, claude-fable-5.1, claude-opus-5.5,
  claude-opus-5, claude-opus-4.8, …, gpt-6.1-sol, gpt-6-sol, …`); a custom-tool turn on `gpt-5-mini` returned the
  tool's random code word → PASS. `CopilotClient.StartAsync` needs no client-level auth.
- Negative controls: a bogus token → `models.list … 401 Unauthorized: {"message":"Bad credentials"}`; no token →
  `Session error: Execution failed: InvalidArg, No GitHub OAuth token or Copilot HMAC key provided`. **Fails closed,
  no ambient fallback** with `UseLoggedInUser = false`.
- `Mode.Empty` throws unless `BaseDirectory` or `SessionFs` is set (Agent42 always sets `SessionFs`).
- SDK surface (XML docs): `SessionConfigBase.GitHubToken` ("resolves this token into a full GitHub identity … for
  content exclusion, model routing, and quota checks"), `SessionConfigBase.GitHubTokenProvider` (on-demand callback;
  failures reject create/resume, no ambient fallback; cannot be combined with `GitHubToken`),
  `SessionConfigBase.EnableSessionTelemetry` ("null (the default) or true → telemetry is enabled for
  GitHub-authenticated sessions"; BYOK always disables it), `ServerModelsApi.ListAsync(selectionId, gitHubToken)`.
- **Unverified (Step 0 settles them):** a token from Agent42's OWN OAuth App (not the gh CLI's) and its minimal
  scope; `EnableSessionTelemetry = false` really sends no telemetry; the measured egress hosts; whether the token is
  written into the SessionFs transcript (`workspace.yaml`/`events.jsonl`); resume of a SessionFs session on a token;
  AF `GitHubCopilotAgent` passing `GitHubToken` through on create AND resume; the ADR 0107 hybrid workspace with a
  token; resuming a BYOK-created session on a token (model switched in the agent's settings).

**User decisions (interview, 2026-10-20):**
1. Scope: copilot-kind chats/agents **and** fleet Copilot agents.
2. Credential: **GitHub OAuth sign-in** (not a pasted token).
3. Models: **discovered live** from the user's plan (not declared).
4. Ordinary chats: **hidden** — only Copilot pickers list these models, and the server refuses them elsewhere.
5. (After critic F1) A GitHub-only user is **not** supported: GitHub Copilot is an add-on and a BYOK provider is
   still required for plain chats, the default model and the fleet planner/judge/manager. Rejected: Copilot models
   as default/planner (breaks ADR 0019's "providers ⟺ a usable default", `AgentApi.Models.cs:165-167`, and would
   need the planner on the Copilot runtime). The provider dialog and docs say so; a GitHub-only user sees the
   existing "No model provider is configured" gate (`AgentGrain.cs:546-557`), reworded to mention that GitHub
   Copilot needs an ordinary provider alongside.

**Design (judgment; the executor may refine the shape, not the decisions):**
- **A provider kind, not a parallel "connection" store.** `ProviderKind.GitHubCopilot` reuses the per-user provider
  overlay (ADR 0018): the OAuth access token is DP-encrypted with `ProviderSecretProtector` into the existing
  `StoredProvider.ApiKeyCiphertext` — **no new persisted fields**, no migration, never echoed (`hasKey` only; gotcha
  "A user provider's API key is encrypted, never echoed"). The UI shows it in the provider list like any provider;
  delete = disconnect.
- **GitHub OAuth App, operator-registered** (not a GitHub App). OAuth App user tokens are `gho_…` — the type the
  spike proved — and don't expire, so there is **no refresh path** (contrast ADR 0025/0081). A GitHub App's
  expiring `ghu_` tokens + refresh are the rejected alternative unless Step 0 shows OAuth App tokens are refused.
  The registration is an operator *app identity*, not an operator *model* (ADR 0019 holds: each user's own plan is
  used). Config: `GitHub:OAuth:ClientId` (committed, public) + secret `GitHub:OAuth:ClientSecret` (GitHub Actions
  secret → ACA secret). Missing → the kind's Connect button is hidden and the start endpoint answers 503; the app
  boots.
- **Flow = ADR 0025's app-driven shape**: `GET …/github/start` (state, PKCE if GitHub accepts it — Step 0) persists a
  pending entry in a per-user pending grain and 302s to `https://github.com/login/oauth/authorize`;
  `GET …/github/callback` one-time-`Take`s the state, exchanges the code at
  `https://github.com/login/oauth/access_token`, and upserts the user's `GitHubCopilot` provider. **Both endpoints
  stay behind Easy Auth** (never `excludedPaths`, rule 4) so `GetUserId()` binds the token to the right user. The
  exchange uses a named `HttpClient` that **opts out** of the `AddServiceDefaults` retry pipeline (rule 8). The
  callback never reflects an upstream error body (generic reason, detail logged) — as ADR 0025.
- **Copilot settings gain an auth mode.** `CopilotProviderSettings` today is BYOK-only (`Type, BaseUrl, ApiKey,
  WireApi`). Add a GitHub-token variant (e.g. a `GitHubToken` member or a sibling factory); `Provider.GitHubCopilot`
  sets `copilot:` to it. Both `BuildSessionConfig`s (chat + fleet) branch: GitHub mode → `GitHubToken = token`,
  `Provider = null`, **`EnableSessionTelemetry = false`**; BYOK mode unchanged. The token never goes into
  `CopilotClientOptions` (client-wide identity is wrong for a multi-user runtime) and never into the runtime's
  `Environment` (`MinimalEnvironment`/`ShellEnvironment` must not gain `GH_TOKEN`/`GITHUB_TOKEN`/`COPILOT_GITHUB_TOKEN`).
- **Refusing chat client.** `Provider.GitHubCopilot`'s `Client` returns a client that throws/answers a clear
  "This model runs only in Copilot agents" (shape of `NoModelChatClient`). This is the ONE server-side refusal for
  every non-Copilot path (plain chat, stored non-copilot agent, `/api/agent-run`, research, follow-ups — which
  already swallow failures — and fleet non-copilot agents). Because the provider EXISTS in `_providers`, `Resolve`
  does not hit its silent default-model fallback.
- **Never a default, never the default provider.** A pinned overlay default naming a `github-copilot:…` id must be
  ignored: `NamesBuiltProvider` (`UserProviderResolver.cs:171-174`) only checks the provider key is built — that is
  the real gap. (Discovered models are never in `declared`, so the `declared[0]` fallback at `:181` is already safe;
  pin it with a test, don't change it.) The factory's `_defaultProvider` (used by `ModelId.Parse` for legacy
  UNQUALIFIED ids, gotcha "A GitHub model id contains a `/`…") must never be the GitHubCopilot provider, or a legacy
  id would resolve to the refusing client. `PUT /api/default-model` stays unvalidated by design.
- **No silent fallback when the provider is gone (critic F3).** `Resolve` (`ChatClientFactory.cs:170-189`) falls back
  to `DefaultModelId` when a provider key is missing — after Disconnect, or a Skipped build. A Copilot agent pinned to
  `github-copilot:<id>` would then silently run BYOK. Guard: `GetCopilotProvider` (chat and fleet both use it) returns
  a distinct "GitHub account not connected" refusal for an id whose provider key is the GitHubCopilot key and is
  missing — not the fallback. Plain-chat `Resolve` fallback for other kinds is unchanged. A runtime 401 (revoked token)
  surfaces as a user-facing "Reconnect GitHub in Settings → Providers" turn message, not a raw exception.
- **Capabilities are unconditional (critic F2).** `IsCopilotComposition` (`AgentGrain.Copilot.cs:52-53`) needs
  `SupportsTools`, and `GetCapabilities` is synchronous and called on many grain paths, so for this kind it returns
  `SupportsTools = true`, everything else false, **with no cache read** (a TTL miss must never flip a chat to a plain
  `ChatClientAgent`). Vision false is correct already: `CanSeeImages` is false for copilot chats
  (`AgentGrain.Copilot.cs:169-170`). `SupportsImageGeneration` must stay false so `GetImageGenerationService` stays shut.
- **Discovery (critic F5).** A Copilot model directory in `src/Agent42.Backend/Copilot/` lists a user's models with
  `Rpc.Models.ListAsync(null, token)` on the **chat** runtime's client (ADR 0085's, exposed by `CopilotAgentFactory`
  through a narrow method that makes sure the client is started — check in Step 0 that a concurrent `StartAsync`
  from discovery and an AF run is safe; serialize it if not). The fleet client is NOT used or merged (gotcha "The
  fleet's Copilot client is NOT ADR 0085's client", `.agentic/gotchas.md:4357`). Availability: chat runtime missing →
  no GitHub models anywhere (chat and fleet); `Fleet:CopilotAgentsEnabled` off → fleet ignores them as today. The
  token comes from the resolved `Provider.Copilot` GitHub-mode settings (its only carrier). Cached per user
  in-process (single replica, rule 12), short TTL, **bounded timeout** (`CompositeModelCatalog` swallows exceptions but
  has none), failure → empty (logged), never a thrown `/api/models`; a GET may start the runtime process — accepted,
  bounded by the cache. A connected user with no Copilot plan gets an empty list and a one-line hint in the dialog.
  It feeds `UserModelCatalogProvider` as a new `IModelCatalog` contribution (`Discovering` is Foundry-typed — add a
  path, don't overload the tuple); the new catalog builds the `ModelInfo`s (`DeclaredModels` maps only declared
  ones). `ModelInfo` gains a `CopilotOnly` flag (wire DTO only, not grain state).
- **Direct writes of the kind are refused (critic F4).** `POST/PUT /api/providers` share `ValidateProvider`/`ToStored`,
  and `UserProviderConfigGrain.SaveAsync` upserts the whole record by key, keeping a stored ciphertext on a blank key.
  So: the API refuses any write whose `Kind` is GitHubCopilot **or** whose key names an existing GitHubCopilot
  provider (blocks converting it to OpenAI-compatible and sending the token to a chosen endpoint), and the fixed key
  is reserved (an ordinary POST cannot squat it). The OAuth callback is the only writer, through a grain method that
  enforces the same rule (not only the HTTP layer). No rename (the key is fixed; DisplayName is fixed text).
  Delete stays the generic DELETE. `ValidateProvider`'s exemption is kind-specific, and `Mask`/`ProviderResponse`
  must handle a provider with no endpoint and no models.
- **DevHost / offline (critic F6).** DevHost has no OAuth config, so the kind is hidden there and Connect answers
  503 — by design; no fake OAuth. The in-process twin `ChatClientCopilotAgentFactory`
  (`src/Agent42.Agents/Copilot/ChatClientCopilotAgentFactory.cs:23-25`) calls `GetClient`, which would hit the
  refusing client: it must refuse a GitHub-mode model with the same "needs the Copilot runtime" message instead (a
  test). UI checks run against the full stack (`dotnet run --project src/Agent42.AppHost`) with a dev OAuth App. The
  redirect URI is built from the request host, as `AgentApi.Mcp.cs:295` does; a GitHub OAuth App has ONE callback
  URL, so dev and prod are two app registrations.
- **Non-Copilot paths covered by the one guard (critic F7, verified):** `AgentGrain.BuildAgent.cs:146`,
  `AgentGrain.Research.cs:100`, `AgentGrain.FollowUps.cs:52` (best effort, swallowed), fleet `Clients`
  (`FleetTaskService.Run.cs:145`, closed by `IsToolCapable`). `/api/agent-run` and `/api/agent-mcp` go through the
  grain. Embeddings/speech resolve only declared ids. Hindsight and reminders call no `GetClient` for a model id.
  Check `RebindModelAsync` (`AgentGrain.cs:274-276`) once in Step 5.
- **Pickers.** `/api/models` carries `copilotOnly`; `models.js` omits them by default; the agent editor includes them
  only while kind = copilot (`agents.js`); the conversation model switcher never lists them for a plain chat.
- **Fleet.** `IsToolCapable` excludes copilot-only models (so no non-copilot client/agent/planner/judge is built on
  them, and `SubmitAsync` with `request.Model` = a copilot-only id is refused); the Copilot factory's `catalogModels`
  = tool-capable ∪ copilot-only; `FleetPrompts`' copilot section lists them as `copilot/<id>` options.
  `Fleet:DefaultCopilotModel` (operator config, used by `ModelFor` for a bare `copilot` hint) stays an ordinary
  catalog id — document it; don't make it accept a per-user GitHub id.
- **ADR 0019 (critic F11).** The OAuth App client secret is an operator-held *app identity*, not an operator model:
  it grants no model access by itself, and every turn runs on the signed-in user's own plan. The ADR must say this
  explicitly — it is where a reviewer checks against CLAUDE.md's "No operator model of any kind".
- **Egress (rule 13, ADR 0013 extension).** Conversation content, tool results and images go to GitHub (and the
  model vendors behind Copilot). Per-user, opt-in (the user signs in). Record the Step-0-measured hosts and the
  telemetry posture in the new ADR. Never write "following ADR 0008".
- **Trust boundary.** The token lives in the Backend's memory and in the session config sent over the runtime's
  stdio. With built-in tools on (ADR 0107) the shell can read the runtime's `/proc/<pid>/environ` — the token is NOT
  in the environment, but Step 0 must check whether the runtime process holds it anywhere the shell can read
  (e.g. a file under `HOME`/`BaseDirectory`). If it does, the accepted-risk list in the ADR must say so, or built-in
  tools must be off for GitHub-mode sessions (decide in Step 0, record in the ADR).

**Governing ADRs / gotchas:** ADR 0013, 0018, 0019, 0025/0081 (OAuth shape), 0085, 0097, 0103 (precedent for a kind),
0105 (registration: endpoint-side deps resolve optionally; a new `AgentApi.*` partial must be called from
`MapAgentApi`), 0107. Gotchas: provider key encryption (~:1064), overlay empty `IConfiguration` (~:1011), Copilot
session pointer/SessionFs/AvailableTools (~:4067-4078), SDK version pin (~:4096), fleet Copilot client separation
(~:4357-4375), workspace/shell (~:4377-4393), the `excludedPaths` gotcha.

## Steps

**Step 0 — Gate spike (user present; BLOCKS everything).** Manual prerequisite first: the user registers a GitHub
OAuth App for local dev (callback `http://localhost:61326/api/providers/github/callback` — confirm the final route
name in Step 3 — **Device Flow enabled**) and hands the client id (secret into user-secrets / a git-ignored file,
never the chat). A `/spike-dotnet` file-based app pinned to `GitHub.Copilot.SDK@1.0.16` (+ the repo's AF
`Microsoft.Agents.AI.GitHub.Copilot@1.23.0` for 0.6) gets a token via device flow and checks, printing
expected-vs-actual:
0.1 the OAuth-App token lists models and completes a custom-tool turn; the **minimal scope** that works (try none,
then `read:user`); 0.2 whether GitHub accepts PKCE (`code_challenge`) on the web flow token exchange — or note
"untested, state only"; 0.3 with `SessionFs` (a local store like the repo's) + `EnableSessionTelemetry = false`:
grep every stored transcript file for the token string — **must be absent**; 0.4 resume the session from the
store on the same token; 0.5 resume a session created BYOK on a token (and vice versa); 0.6 the same turn through
AF `GitHubCopilotAgent` (create + resume) — token passes through; 0.7 default mode + real working directory
(ADR 0107 hybrid) — a shell `env` and a search of `BaseDirectory`/`HOME` for the token; 0.8 remote endpoints
during a turn (e.g. `Get-NetTCPConnection` on the runtime pid, resolved hosts) — record them; 0.9 concurrent
`StartAsync` on one client (discovery + a run) is safe, or note that it must be serialized; 0.10 if available, a
token of an account WITHOUT a Copilot plan: what `models.list` and a turn return (drives the empty-list hint and
the error message). **Verify:** paste the
actual output into this spec's "Step 0 results". **Stop and re-plan** if 0.1 fails (then the GitHub App
alternative), or 0.3 shows the token in the transcript.

**Step 1 — ADR (`/decide`).** New ADR (next free number; `ls .agentic/decisions`) extending 0013/0018 and amending
0085/0097: the kind, OAuth App + no refresh, copilot-only models, refusing client, telemetry off, egress hosts from
0.8, trust-boundary findings from 0.7, alternatives (pasted PAT, GitHub App, client-level `GitHubToken`,
`UseLoggedInUser`, an `IChatClient` over a Copilot session, `api.githubcopilot.com` directly). Add "Amended by"
notes to 0085/0097, "Extended by" to 0013/0018, and the row in `.agentic/decisions/README.md`. Include the ADR 0019
reasoning (operator app identity ≠ operator model) and decision 5 (add-on, BYOK still required). **Verify:**
`dotnet test Agent42.slnx --filter FullyQualifiedName~AdrNumberingTests`.

**Step 2 — The kind and its Copilot settings.** `ProviderKind.GitHubCopilot` (last member); `ModelProviderBuilder`
branch before the endpoint rule (blank key → Skipped; no endpoint and no declared models required; `ValidateProvider`
exempts this kind — but the OAuth callback is its only writer, so `POST/PUT /api/providers` must REFUSE creating or
editing a `GitHubCopilot` provider directly, except rename/delete); `Provider.GitHubCopilot` with the refusing
client and the GitHub-mode `CopilotProviderSettings`; `GetCapabilities` for the kind. **Verify (tests first):**
`ModelProviderTests` (kind builds, blank key skipped, no endpoint OK), `ProvidersApiTests` (direct create refused;
GET masks the token; unknown-kind message now lists the new kind), refusing client throws the clear message.
`dotnet test --filter "FullyQualifiedName~ModelProviderTests|FullyQualifiedName~ProvidersApiTests"`.

**Step 3 — OAuth connect flow.** Options (`GitHub:OAuth:ClientId`, `ClientSecret`; scope from 0.1), a pending grain
(`UserMcpOAuthPendingGrain` shape: bounded, one-time `Take`, its own state name on the shared storage provider, no
new container), start/callback endpoints behind Easy Auth, named opt-out `HttpClient`, upsert of the provider (a
fixed key such as `github-copilot`, one per user; a reconnect replaces the token). Disconnect = existing provider
DELETE (+ best-effort grant revoke via GitHub's `DELETE /applications/{client_id}/grant` — only if trivial; else
out of scope and noted in the ADR). Registration per ADR 0105 (endpoint-side; optional resolution; the partial is
called from `MapAgentApi`). **Verify:** endpoint tests with a stub token endpoint (state mismatch → refused and
nothing stored; replay → refused; success → provider stored encrypted; missing client config → 503;
upstream error not reflected); `EasyAuthExclusionTests` unchanged (the new paths are NOT excluded);
`AgentApiReachabilityTests`; `GrainStorageSerializerTests` round-trips the new pending state on the Cosmos JSON path
(rule 2); the three write-guard cases (POST of the kind, PUT converting the GitHubCopilot key to another kind with a
blank key, POST of an ordinary provider under the reserved key) are refused. **Parallelizable with Step 4 after
Step 2** (worktree isolation if dispatched concurrently). Steps 4 → 5 are **sequential** (both touch the fleet
Copilot factory).

**Step 4 — Copilot sessions on the token.** Both `BuildSessionConfig`s branch on the settings' mode (GitHub:
`GitHubToken`, no `Provider`, `EnableSessionTelemetry = false`; plus Step 0.7's built-in-tools decision); both gates
accept GitHub-mode settings; the "not connected" refusal for a missing GitHubCopilot provider key (no fallback); the
401 → "Reconnect GitHub" message; the twin `ChatClientCopilotAgentFactory` refuses GitHub-mode models.
**Verify:** `CopilotRuntimeTests` (disconnected provider → refusal, never the BYOK default; GitHub mode sets token +
telemetry off + no
Provider; BYOK unchanged; `KeyNeverInToString` covers the token; the runtime environment contains no
`GH_TOKEN`/`GITHUB_TOKEN`/`COPILOT_GITHUB_TOKEN` in both env builders), `FleetCopilotAgentFactoryTests` (GitHub-mode
session config; copilot-only model accepted as `copilot/<id>`).

**Step 5 — Discovery, defaults and the fleet catalog.** The Copilot model directory (per-user cache + timeout +
failure → empty), its `IModelCatalog` contribution, `ModelInfo.CopilotOnly`, capabilities, resolver
`EffectiveDefault` skips the kind, `IsToolCapable` excludes copilot-only, fleet `catalogModels` union, `FleetPrompts`
copilot section; `_defaultProvider` never the GitHubCopilot one; the reworded no-provider gate message.
**Verify:** `UserProviderResolverTests` (a pinned `github-copilot:…` default is ignored; a GitHub-only user resolves
`DefaultModelId == ""`), a factory test (legacy unqualified id never resolves to the GitHubCopilot provider;
capabilities for the kind are tools-only and independent of the directory), a directory test with a fake lister
(cache hit, timeout → empty, failure → empty, chat runtime missing → empty), `/api/models` test (`copilotOnly`
set), fleet tests (non-copilot agent on a copilot-only model never gets a client; `SubmitAsync` with a
copilot-only `Model` refused; planner prompt lists it under copilot; fleet disabled → ignored).

**Step 6 — Frontend.** Kind option "GitHub Copilot (your subscription)"; for that kind the dialog hides endpoint, key
and models, and shows "Sign in with GitHub" (→ start endpoint) or "Connected"; hidden when the server reports the
OAuth client unconfigured (expose a flag on an existing GET, don't add a probe endpoint if one fits). `models.js`
filters `copilotOnly`; the agent editor shows them only for kind copilot. Bump `CACHE` in `sw.js` (rule 3) and
update any `SHELL`/asset guard tests. The dialog states that GitHub Copilot is an add-on (an ordinary provider is
still needed) and shows the no-plan hint for an empty list. **Verify:** `ProvidersAssetsTests`,
`AgentEditorAssetsTests`, `PwaAssetsTests` (and the other `*AssetsTests` that pin the cache name — grep `sw.js`
cache name in `tests/`); manual check on the full stack (AppHost) with the dev OAuth App: connect, the option
renders, the filters hold.

**Step 7 — Infra + config.** `GitHub:OAuth:ClientId` in `appsettings.json` (blank until the user registers the prod
app); `GH_OAUTH_CLIENT_SECRET` → `deploy.yml` → `main.bicep` param → `container-app.bicep` secret + env
`GitHub__OAuth__ClientSecret`. No RBAC change (hard rule 5 not triggered). **Verify:** `az bicep build` on
`infra/main.bicep`; `ConfigHealthTests.cs` is the existing test that references `SEARXNG_SECRET` — follow its
pattern if the config health tab (ADR 0029) should report the OAuth client as configured/missing.

**Step 8 — Live test.** `LiveCopilotGitHubTests` following `LiveAnthropicChatTests`' pattern: token from env
`AGENT42_LIVE_GITHUB_TOKEN` (deliberately not `GH_TOKEN`) or git-ignored `live-github.local.json` (add to
`.gitignore`); skip when absent. One tool turn that resumes from SessionFs on the token, through the real
`CopilotAgentFactory`. **Verify:** runs green locally with a token, skips without.

**Step 9 — Docs (`/sync-docs`).** `CLAUDE.md`: manual-prerequisites rows (`GH_OAUTH_CLIENT_SECRET`, and the
OAuth App callback URL `https://chat.agent42.nl/<route>/callback`), feature-map rows (Model providers + Copilot agent
kind gain the new ADR). `docs/features/bring-your-own-models.md`, `custom-agents.md`, `agent-fleet.md`. Gotchas
(`/gotcha`): "a GitHubCopilot model must never reach an IChatClient — the refusing client is the guard, not the
picker" (cross-link the existing BYOK Copilot and provider-key entries); "the GitHub token goes per session, never in
CopilotClientOptions or the runtime environment"; "a disconnected GitHubCopilot id must refuse, not fall back"; any
Step 0 trap. Move this spec to `specs/completed/`.

## Acceptance criteria → tests

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | An OAuth-App token runs a Copilot session (models + tool turn + resume) | Step 0 output; live `LiveCopilotGitHub_ToolTurn_ResumesFromSessionFs` | `LiveCopilotGitHubTests.cs` |
| 2 | The token is stored encrypted, never returned by the API, never in a transcript | `Get_NeverReturnsKey` (extended to the kind), callback-stores-ciphertext test; Step 0.3 | `ProvidersApiTests.cs`, new OAuth endpoint tests |
| 3 | OAuth callback is CSRF-safe and one-time | state-mismatch / replay tests | new OAuth endpoint tests |
| 4 | Missing OAuth client config → 503 + UI hides Connect; app boots | missing-config test; assets test | new OAuth endpoint tests, `ProvidersAssetsTests.cs` |
| 5 | GitHub-mode sessions carry the token, no Provider, telemetry off (chat + fleet) | `SessionConfig_GitHubMode_*` | `CopilotRuntimeTests.cs`, `FleetCopilotAgentFactoryTests.cs` |
| 6 | The runtime environment never carries a GitHub token | env-builder tests | `CopilotRuntimeTests.cs`, `FleetCopilotAgentFactoryTests.cs` |
| 7 | Copilot-only models are refused outside Copilot agents (no silent fallback) | refusing-client test; fleet non-copilot + `SubmitAsync` tests; twin refuses | `ModelProviderTests.cs`, fleet tests, `CopilotRuntimeTests.cs` |
| 7a | A disconnected GitHub provider refuses Copilot agents pinned to it; never runs them on the BYOK default | disconnected-provider tests (chat + fleet) | `CopilotRuntimeTests.cs`, `FleetCopilotAgentFactoryTests.cs` |
| 7b | Capabilities for the kind are tools-only and never depend on discovery | factory test | `ModelProviderTests.cs` |
| 8 | Copilot-only models are never the default model or default provider; a GitHub-only user gets the no-provider gate | resolver + factory tests | `UserProviderResolverTests.cs`, `ModelProviderTests.cs` |
| 9 | Models are discovered per user, cached, and fail soft | directory tests; `/api/models` `copilotOnly` | new directory tests, models API test |
| 10 | Pickers hide them except the copilot agent editor | asset guard tests | `ProvidersAssetsTests.cs`, `AgentEditorAssetsTests.cs` |
| 11 | Direct create/edit/convert of the kind via `/api/providers`, and squatting its reserved key, are refused | three API tests + grain test | `ProvidersApiTests.cs` |
| 11a | A revoked token yields a "Reconnect GitHub" message | 401-mapping test | `CopilotRuntimeTests.cs` |
| 12 | New pending state survives the Cosmos JSON path | round-trip test | `GrainStorageSerializerTests.cs` |
| 13 | New endpoints are mapped and stay behind Easy Auth | reachability + exclusion tests | `AgentApiReachabilityTests.cs`, `EasyAuthExclusionTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

- [ ] Protected branches: `[main]` — work on `feature/copilot-github-account`, MR only.
- [ ] New-code coverage ≥ **80 %** — `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet).
- [ ] Cognitive complexity ≤ **15** (SonarAnalyzer S3776, build error).
- [ ] Vulnerable packages: **0** (hard); outdated packages: report; new packages: latest stable (none expected).
- [ ] Enforcement: local soft warning, CI hard block.
- [ ] `dotnet build Agent42.slnx`, `dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- Copilot models in ordinary chats (rejected by the user: hidden). An `IChatClient` over a Copilot session.
- Pasted tokens/PATs (a later fallback if OAuth is ever unavailable), GitHub App + token refresh (unless Step 0
  forces it — then re-plan), GitHub Enterprise hosts.
- Copilot quota/usage display, premium-request accounting, the usage dashboard (copilot turns already produce no
  `chat` spans — ADR 0085 residual).
- Copilot's native GitHub MCP tools (`GitHubMcpToolConfig`), remote sessions, Copilot's own skills/agents.
- An Anthropic BYOK mapping for Copilot (ADR 0103 follow-up).

## Review (critic, 2026-10-20)

Verdict was "do not approve as written". Every finding was accepted and folded in above:
F1 GitHub-only user unreachable → user decision 5 (add-on, BYOK required). F2 capabilities vs cache → unconditional
tools-only. F3 missing-provider fallback + 401 → "not connected" refusal + reconnect message. F4 write bypasses →
kind/key write guard in API and grain. F5 discovery client → chat client, start safety (0.9), availability combos,
timeout. F6 DevHost/twin → twin refuses, UI checks on AppHost, two OAuth Apps. F7 path list → recorded.
F8 `NamesBuiltProvider` and `_defaultProvider` gaps → Step 5. F9 `SubmitAsync` + `DefaultCopilotModel` → fleet bullet.
F10 non-subscriber → 0.10. F11 ADR 0019 wording → Step 1. F12 ADR README + cross-links → Steps 1/9.
F13 verification commands → fixed (`AdrNumberingTests`, `ConfigHealthTests`, test classes confirmed to exist,
Steps 4 → 5 sequential). Nothing rejected.

## Divergence from the plan (implementation, 2026-10-20)

- **Order.** Step 0 ran items 0.3–0.9 on the `gh` CLI's OAuth token. Items 0.1 (Agent42's own OAuth App token and its
  scope), 0.2 (PKCE honoured) and 0.10 (an account without a plan) need the user's OAuth App and are **still open**. The
  steps that don't depend on the token type (2, 4, 5, 6, 7, 8) went ahead, because both a GitHub App and an OAuth App
  hand the runtime a plain token string. Step 3 sends PKCE and ships `GitHub:OAuth:Scopes` empty; the ADR records both
  as unverified.
- **UI.** The plan had "GitHub Copilot" as a kind option in the provider dialog. What shipped is a **"Sign in with
  GitHub" button** on the Model providers tab, plus a connected row with Disconnect and no Edit. The dialog's endpoint,
  key and model fields would all have had to hide, and its Save would be refused by the write guard anyway. Recorded in
  ADR 0108's alternatives.
- **Copilot agents in plain pickers.** The composer's model picker leaves these models out, but a copilot chat already
  pinned to one shows it under its provider label, not "Unavailable" (`selectModel`).
- **Revoked token.** It became a wrapper agent (`GitHubAuthFailureAgent`), applied only to GitHub-mode copilot chats.
  A failed stream otherwise reaches the browser only as "Transmission failed". Fleet Copilot agents are not wrapped: a
  rejected token there is a failed agent in the task log.
- **Tests.** The Step 4 tests went into a new `CopilotGitHubAccountTests.cs`, not into `CopilotRuntimeTests.cs`. The
  discovery and fleet tests are in `CopilotModelDiscoveryTests.cs`, the sign-in tests in `GitHubSignInTests.cs`, and the
  live test in `LiveCopilotGitHubTests.cs`. The live test passed on 2026-10-20 with a `gh` token. `SubmitAsync` with a
  copilot-only model is covered through `IsToolCapable`, which is its only gate, not through a `FleetTaskService` test.
- **Not done.** No config-health (ADR 0029) row for the OAuth App. No grant revoke on disconnect (out of scope, in the
  ADR). There is no per-ADR index in `.agentic/decisions/README.md` to add a row to.

## Step 0 results

**Run 2026-10-20, Windows, on the REPO's own builders** (a file-based app with `#:project` on `Agent42.Backend`:
`CopilotAgentFactory.BuildClientOptions`/`BuildSessionConfig` with `Provider = null`, `GitHubToken`,
`EnableSessionTelemetry = false`; the real `CopilotSessionFsProvider` over an in-memory `ICopilotSessionStore`; AF
`GitHubCopilotAgent`; model `gpt-5-mini`; token = `gh auth token` (`gho_…`, the gh CLI's OAuth App)). Output:

```
PASS 0.6 AF create on token zebra-2190
PASS 0.4 resume from store after runtime restart, on token zebra-2190
PASS 0.4b resume WITHOUT token fails closed EXCEPTION InvalidOperationException: Session error: Execution failed: InvalidArg, No GitHub OAuth token or Copilot HMAC key provided
PASS 0.3 token absent from every transcript file files=3 leaked=[] sample=[u1/c-empty/state/workspace.yaml, u1/c-empty/state/events.jsonl, u1/c-empty/state/checkpoints/index.md]
PASS 0.3b no 'gho_' substring in transcript
PASS 0.5a BYOK session resumed on token byok='Running get_code_word … zebra-2190' token='zebra-2190'
PASS 0.5b token session resumed on BYOK zebra-2190
PASS 0.9 concurrent StartAsync models=21
PASS 0.7 shell ran in workspace Done.
PASS 0.7 token NOT in the shell's environment vars=[COMSPEC,COPILOT_AGENT_SESSION_ID,COPILOT_CLI,COPILOT_RUNTIME_PROCESS_FILE_LOGGING,GCM_INTERACTIVE,GIT_ASKPASS,GIT_CONFIG_COUNT,GIT_CONFIG_KEY_0..2,GIT_CONFIG_VALUE_0..2,GIT_OPTIONAL_LOCKS,GIT_TERMINAL_PROMPT,PATH,PATHEXT,POWERSHELL_UPDATECHECK,PROMPT,PSModulePath,SSH_ASKPASS,SYSTEMROOT,TEMP,TMP]
PASS 0.7 token in no file written during the run (workspace, TEMP, ~/.copilot)
0.8 runtime pids=47748 remotes during turn:
   140.82.121.5 -> lb-140-82-121-5-fra.github.com
   140.82.112.21 -> lb-140-82-112-21-iad.github.com
   fwd api.githubcopilot.com = 140.82.113.21
   fwd api.individual.githubcopilot.com = 140.82.114.21
   fwd api.github.com = 140.82.121.5
   fwd telemetry.individual.githubcopilot.com = 140.82.114.21
   fwd copilot-telemetry.githubusercontent.com = 140.82.114.21
   fwd collector.github.com = 140.82.114.21
```

Findings:
- 0.3/0.4/0.5/0.6/0.9 settle the plan's assumptions: the token is never written to the SessionFs transcript; a
  session resumes on the token after a runtime restart; resume WITHOUT the token fails closed (so the token must be
  supplied on every create AND resume — the AF agent's `SessionConfig` carries it on both); a session moves between
  BYOK and token auth in both directions (switching the agent's model needs no reset); concurrent `StartAsync` is
  safe (no serialization needed for discovery).
- 0.7: with built-in tools on, the shell's environment holds no token and no runtime-written file holds it. The
  runtime HARDENS git for the shell (`GIT_ASKPASS=` empty, `credential.interactive=never`, `GIT_TERMINAL_PROMPT=0`),
  so the shell cannot borrow the user's GitHub identity through git. **Decision: built-in tools stay on for
  GitHub-mode sessions**; the residual (the token is in the runtime process's memory; a shell could in principle read
  `/proc/<pid>/mem` of the runtime on Linux if ptrace is permitted) goes in the ADR's accepted risks next to ADR
  0107's `/proc/<pid>/environ` residual.
- 0.8: egress during a token turn went only to GitHub (`140.82.121.5` = api.github.com; `140.82.112.21`, a GitHub
  `iad` load balancer). **IP-level capture cannot tell telemetry from inference**: the telemetry hosts resolve into
  the same GitHub ranges. So `EnableSessionTelemetry = false` is set and documented, but its effect is UNVERIFIED by
  measurement — the ADR says exactly that. No non-GitHub host was contacted.
- **Still open (need the user's OAuth App):** 0.1 (a token from Agent42's own OAuth App + minimal scope), 0.2 (PKCE
  on GitHub's token exchange), 0.10 (an account without a Copilot plan).
