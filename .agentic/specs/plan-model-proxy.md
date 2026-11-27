planned-against: main @ 72815df

# Plan: model proxy for code written by Copilot-shell agents (+ an `other` non-chat model capability)

## Overview

A user has models deployed that are not chat models (first case: `Fal-MiniMax-H3-Max-Turbo`, text-to-video).
Agents that can run code (Copilot-kind chats, ADR 0107, and Copilot fleet agents, ADR 0097) should be able to
write code that calls such a model, and any other configured provider, **without ever holding the provider key**.

Agent42 gets a pass-through HTTP proxy, `/api/model-proxy/{provider}/{**path}`. It forwards to the endpoint of one of
the caller's own configured providers, injecting that provider's credential server-side. The caller authenticates with
a short-lived, per-run token that a new custom tool hands the agent. The API shape is the provider's own (the proxy
does not translate), so the user's prompt supplies the API notes (user decision: no built-in notes) and the provider's
docs apply, with `base_url` pointed at the proxy and the token as the API key.

Second part, a user decision from the interview: a new model capability **`other`** (non-chat). Without it a
video-only model shows up in the chat picker and Auto can route to it.

Typed per-modality tools (a `generate_video` tool) are NOT part of this plan. Only the proxy is.

## Affected areas (verified against the tree at 72815df)

New code:
- `src/Agent42.Agents/ModelProxy/` — `IModelProxyTokens` (+ in-memory impl), the `get_model_proxy_access` tool.
- `src/Agent42.Backend/ModelProxy/` — `ModelProxyForwarder`, `AgentApi.ModelProxy.cs` (`MapModelProxyEndpoints`),
  `ModelProxyRegistration`.
- `.agentic/decisions/0140-model-proxy.md`.

Touched (every place the `SupportsTranscription` flag flows is the template for `other`; grep it, the list below is the
verified set at plan time):
- `src/Agent42.Agents/Models/UserProviderConfig.cs` (`StoredModel`), `src/Agent42.Backend/Models/{ModelProviderContracts,
  ModelProviderOptions,ModelProviderBuilder,ChatClientFactory,ModelCatalog,UserProviderResolver,AgentApi.Models}.cs`,
  `src/Agent42.Backend/Models/Routing/UserModelRouterProvider.cs` (lines 87 and 92: `IsChat` / `IsImageGeneration` both
  enumerate the non-chat flags), `src/Agent42.Backend/Fleet/FleetTaskService.cs` (reads the flag),
  `src/Agent42.Backend/wwwroot/js/{models,providers}.js`, `src/Agent42.Backend/wwwroot/fleet/fleet.js`,
  `src/Agent42.Backend/wwwroot/sw.js` (`CACHE` bump, cross-cutting rule 3),
  `tools/Agent42.Android/app/src/main/java/nl/agent42/android/api/ApiModels.kt` and its test
  (`Agent42ApiTest.kt`; the NDJSON/API contract has a golden file, ADR 0114: check whether `/api/models` is in it).
- `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` (`CopilotFunctions`, lines ~99-139: where `read_skill_resource` is
  added to the candidate list is the shape to copy), `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs`
  (`BuildSessionConfig`, `Tools = [.. canvas.Tools, .. todos?.Tools]`), `src/Agent42.Backend/Fleet/FleetTaskService.Run.cs`
  (run start/end), `src/Agent42.Agents/Copilot/AgentGrain.Copilot.cs` (`OpenCopilotRun`/`CloseCopilotRun`, ~149-166).
- `src/Agent42.Backend/Api/AgentApi.cs` (`MapAgentApi` call list), `src/Agent42.Agents/` silo registration
  (`Agent42SiloRegistration.cs`) and `src/Agent42.Backend/Api/Agent42ApiRegistration.cs`.
- `src/Agent42.Backend/appsettings.json`, `infra/main.bicep` + `infra/modules/container-app.bicep` only if a setting
  must be an env var (see Step 6), `compose.yaml` (comment only), `CLAUDE.md` (feature map row, rule 12 list),
  `.agentic/gotchas.md`, `.agentic/decisions/README.md`.

## Context & constraints

**User decisions (interview, 2026-11-22):**
- Proxy only; no typed `generate_video` tool.
- API notes come from the user's prompts. The tool therefore returns only provider keys, kinds, declared model ids,
  the proxy base URL and the token. Never API docs.
- Add a non-chat capability bit named `other`. Models with it are hidden from the chat pickers and from Auto, and
  can never be a default.

**Findings the executor cannot cheaply re-derive:**
- **No per-session environment for the Copilot shell.** `Environment` is set once on `CopilotClientOptions`, and one
  client serves every session (`src/Agent42.Backend/Copilot/CopilotAgentFactory.cs:204`,
  `src/Agent42.Backend/Fleet/FleetCopilotAgentFactory.cs:52`). `SessionConfig` carries no env field in this repo. Whether
  the SDK has one is unverified (package absent from the local NuGet cache; check after `dotnet restore`, but the design
  does not depend on it). So the token reaches the agent as a **tool result**, not an env var.
- **Token in a tool result is acceptable only because it is bounded, and it is NOT "dead" while the run lives.** A tool
  result is persisted in the session (gotcha "An agent tool's RETURN value is persisted"), and a Copilot transcript lives
  in Blob via SessionFs, not the shell's reach. Mitigation: per-run revocation, an idle TTL **and an absolute maximum
  lifetime** (a sliding TTL alone would keep a leaked token alive as long as anyone keeps using it, which for a fleet task
  is unbounded), plus the per-run request cap. A token that leaks into a transcript, canvas page, workspace file or a
  public fleet share link (ADR 0120) is usable until the run ends, the absolute lifetime passes or the cap is hit, and then
  it is dead. A provider key in those places would be permanent. The ADR and gotcha must say "bounded", never "dead" or
  "safe", and ADR 0121's rule applies: never write "only X can" as a security claim.
- **Nothing in the repo is a per-run, expiring credential.** `ApiKey`, `ShareToken`, `BridgeKey` are random secrets
  hashed into a directory grain with no expiry; `AgentMcpKeyGate`
  (`src/Agent42.Backend/Mcp/AgentMcpKeyGate.cs:53-93`) is the validation shape to copy (middleware over a prefix, one
  uniform 401 body, caller handed on through `HttpContext.Items`); `BridgeKey.Generate`
  (`src/Agent42.Agents/Mcp/BridgeKey.cs`) is the generate/hash shape. The only in-process registry precedent is
  `LocalBridgeRegistry` (`src/Agent42.Backend/Mcp/LocalBridgeRegistry.cs`), single-replica bound.
- **`TurnContext` has no user id, and fleet runs have no `TurnContext` at all**
  (`src/Agent42.Agents/Conversations/TurnContext.cs:305-313`; no `TurnContext.For*` in Fleet). Chat side: the tool is
  built once per activation in `CopilotFunctions`, **not per run**, so its closure captures the user id (the grain's
  `UserIdFromGrainKey`, `AgentGrain.cs:271`; the grain key is not the user id itself) but the **run identity must come from
  `TurnContext.CurrentOrClosed`**, which `SchedulerBoundAIFunction` re-establishes on the Copilot RPC thread
  (`SchedulerBoundAIFunction.cs:54-62`: the flowed context if registered, else the single in-flight run, else `Closed`).
  Under `Closed` (zero or several runs in flight) the tool **refuses to mint** and returns an error result. The `runKey` is
  the registered `TurnContext` object itself, so `CloseCopilotRun` can revoke exactly it. Fleet side: the tool closure
  captures the task's user id and task id. Fleet tools are NOT wrapped in `SchedulerBoundAIFunction` (gotcha "The Copilot
  runtime calls custom tools OFF the grain's scheduler"), so do not copy that wrapper for the fleet tool.
- **User provider secrets:** `IUserProviderResolver.ResolveAsync(userId, ct)` returns `ResolvedProviderSet` whose
  `Providers[key].Options` has `Endpoint` and the **plaintext** `ApiKey` (`src/Agent42.Backend/Models/UserProviderResolver.cs`).
  This is the lookup the forwarder uses. `StoredProvider.Key` is the URL segment: non-empty, no `:`, not `github-copilot`
  (`AgentApi.Models.cs` `ValidateProvider`). Match it exactly, case-sensitive, after URL-decoding.
- **Wire auth per kind** (`ChatClientFactory.cs`): OpenAICompatible = `Authorization: Bearer` (the SDK's
  `ApiKeyCredential`); AzureOpenAI (Foundry) = the `api-key` header (`ApiKeyHeaderPolicy`, gotcha "Azure's /openai/v1
  authenticates the resource key via the api-key header"); Anthropic = `x-api-key` (the API's documented header; SDK
  side in this repo, verify with a unit test against the real header name you send). **GitHubCopilot and DecisionModel
  are refused**: the Copilot token must never leave its session path (gotcha "The GitHub token goes on every Copilot
  session create AND resume"), and a Decision-1 router is not a general endpoint.
- **Base URL per kind:** the proxy path is relative to the provider's configured endpoint, verbatim. OpenAICompatible:
  `endpoint + path` (the user's endpoint normally already ends in `/v1`). Foundry: the stored endpoint is the resource
  root, so `/openai/v1/...` (and any other Azure path) is given by the caller. This is what makes the provider's own
  docs apply. Do not add or strip any prefix.
- **A provider endpoint is deliberately not SSRF-guarded** (gotcha "A user provider overlay is best-effort ... NOT
  SSRF-guarded"): it is the user's own config and may be `localhost`. That exemption holds ONLY while the host comes from
  config. **The `{**path}` comes from agent code and must never be able to change scheme, host or port.** Build the
  upstream URI by concatenation onto the fixed base. **`SearxngProxy.BuildUpstreamUri`
  (`src/Agent42.Backend/Web/SearxngProxy.cs:118`) is the SCAR TISSUE to learn from (collapse leading `/` and `\`, then
  compare authorities), not code to copy:** it uses `GetLeftPart(Authority) + path`, which drops the base path, while here
  the endpoint's own path (e.g. `/v1`) must be kept, and an authority check alone does not stop `../` from climbing out
  of that path. The new builder must (1) keep the base path and require the final path to still start with it, after
  normalisation, (2) reject `.`/`..` segments in raw, single-encoded and double-encoded form (`%2e`, `%252e`, `%2f`,
  `%252f`), (3) reject a **decoded** `?`, `#`, `\`, `@`, `%` or a control character inside the `{**path}` route value
  (after routing decodes it, concatenation would turn `%3F`/`%23` into a query or fragment: parameter injection onto the
  provider), (4) collapse leading `/` and `\` and reject anything that parses as having a scheme or authority, and
  (5) forward the caller's **query string** only as a raw, length-capped pass-through appended after the checked path
  (it cannot alter the host or path), never re-decoded. Do not add an address guard on the provider endpoint itself.
- **Own `HttpClient`** (gotchas "A named HttpClient silently inherits AddServiceDefaults' retry pipeline" and "A named
  HttpClient cannot be re-pointed per consumer; a proxy needs its OWN"): exemplar
  `src/Agent42.Backend/Program.cs:243-254` (`searxng-ui`: `AllowAutoRedirect=false`, `UseCookies=false`, no resilience
  handlers). A retried POST would double-submit a video job, which costs money; no retries here.
- **Easy Auth:** do NOT add the path to `excludedPaths` (default). The shell calls the backend over loopback, and the
  repo says the shell can already forge the principal header against localhost (ADR 0018 header, gotchas
  "The owner tier is advisory"), which implies loopback does not traverse Easy Auth. That is **unverified at runtime**
  (post-deploy check P1). The endpoint enforces the token itself regardless, so it is correct in both worlds; if P1
  shows loopback does go through Easy Auth, the fallback is an `/api/model-proxy/*` exclusion (cross-cutting rule 4:
  exact vs `/*` matching, `EasyAuthExclusionTests`, take the user id from the token, never `GetUserId()`, fail closed
  with one uniform 401). Record the P1 result in the ADR.
- **Single replica is load-bearing (rule 12) and the token registry joins that list.** In-memory, so a revision
  rollover kills live tokens; the run is dying anyway and the tool re-mints. Rejected alternative: stateless
  Data-Protection time-limited tokens, which survive replicas and restarts but cannot be revoked at run end and add a
  key-ring dependency. If the untracked `.agentic/specs/plan-orleans-clustering.md` is ever executed, this registry is
  one of the things it must move (say so in the ADR). Not read for this plan.
- **Egress (rule 13):** agent code can send arbitrary content to any provider the user configured, including ones not
  chosen as a chat model. That is an ADR 0013 extension, recorded as one in ADR 0140. It is opt-in per chat/agent via a
  selectable tool key (rule 11: lands OFF for anyone who ever pressed Save; `null == all`; read ADR 0021 and the
  "Selectable tools" gotcha first). **Never write "only X can" as a security claim** (ADR 0121): the Copilot shell is
  un-isolated and can read the backend process, so the token scopes the proxy, it does not isolate one user's shell from
  another's.
- **Usage accounting is out of scope.** The usage dashboard counts `chat` spans only (gotcha "The usage dashboard counts
  tokens from chat spans ONLY"), so proxied calls do not appear in it or in the estimated cost. Say so in the ADR.
- **Complexity:** S3776 is an error at 15 (`.agentic/quality-gates.yaml`). Split the forwarder into
  URI builder / header filter / token gate / streamer from the start.

## Steps

Branch: `feature/model-proxy` from `main @ 72815df`. Steps 1 and 2 are independent (parallelizable, but both write
code, so use worktree isolation or run them in sequence); 3 depends on nothing but is needed by 4 and 5; 4 and 5 are
sequential after 3; 6-7 last.

### Step 1 — Facts the design leans on (no code)
- After `dotnet restore Agent42.slnx`, check whether the Copilot SDK's `SessionConfig` has a per-session environment
  field. Record the answer in the ADR; the design does not change either way.
- Determine how the backend learns its own loopback URL: Kestrel binds the aspnet:11.0 image default (port from the
  base image; the repo sets no `ASPNETCORE_URLS`). Implement it as: `ModelProxy:BaseUrl` if set, else derive from
  `IServerAddressesFeature` (first `http` address, host rewritten to `127.0.0.1`), else the loopback URL of the
  ingress `targetPort` 8080 as a last resort. Whatever it is, the tool must return it and a unit test must pin the
  precedence. The `IServerAddressesFeature` host can be `0.0.0.0`, `[::]` or a `+`/`*` wildcard: the rewrite to
  `127.0.0.1` must be covered for each. **Verify:** a test for the resolver with those cases.

### Step 2 — The `other` capability (template: `SupportsTranscription`)
- Add `SupportsOther` as a **trailing `[Id(n)]` with a `false` default** on `StoredModel` (rule 2: no migration; prove it on the
  Cosmos JSON path in `GrainStorageSerializerTests`, not only binary) and carry it through every file the
  `SupportsTranscription` grep returns (list in Affected areas): contracts, options, builder, catalog, resolver,
  `AgentApi.Models.cs` (request/response/`ToStored`/`Mask`), the Android `ApiModels.kt` (+ its golden-file test if
  `/api/models` is covered).
- Semantics: an `other` model is **not chat**. Every site that enumerates the non-chat flags gets `other` (verified
  set at plan time; re-grep `SupportsTranscription|supportsTranscription|IsChat` over `src/`, `tests/`, `tools/`,
  ignoring `bin`/`obj`, before declaring done):
  - `UserModelRouterProvider.cs:87` (`IsChat`) and `:92` (`IsImageGeneration`); `ModelRouting.cs:24` (`IsChat = true`
    default) and `ModelRouterService.cs:68` (`c.IsChat && ...`) only consume it, so check they need no change.
  - `FleetTaskService.IsToolCapable` (`FleetTaskService.cs:352`) and its twin `toolCapable` in `wwwroot/fleet/fleet.js:478`
    (planner, judge and manager must never be handed a video model). The Android `ApiModels.kt`.
  - `models.js:90` is the only chat-picker filter. `providers.js:98/144/189` are purpose-specific positive filters
    (speech, transcription, ...), so `other` is excluded from them by default; add a checkbox ("other (non-chat)") in the
    form (`providers.js` ~368-433).
  - **The default-model rule does not exist today and must be written, not "followed"** (critic H1).
    `UserProviderResolver.EffectiveDefault` (`UserProviderResolver.cs:191-207`) only checks that the default names a
    built provider and falls back to `declared[0]`, so an embedding-first provider can already become the default, and an
    `other` model would too. New rule: `EffectiveDefault` and `NamesBuiltProvider` skip `other` models (a stored
    `overlay.Default` naming an `other` model is treated as unset); the fallback is the first declared **chat-capable**
    model (`!other`); if a user has providers but no chat-capable model the default is `""` (empty picker and gated
    composer, the same state as a user with no provider, ADR 0019). Do NOT widen this to embedding/speech/transcription in
    this MR, but note that pre-existing gap in the ADR. Locate the Auto default path (`AutoModelId.IsAuto`, the router
    provider) and confirm it never selects an `other` model.
  - A provider whose only model is `other` still validates (it declares ≥1 model, `ValidateProvider`).
  - Test files that enumerate the flags and need updating: `DecisionModelProviderTests`, `ModelRouterServiceTests`,
    `SerializationCompatibilityTests`, `GoldenStates.cs` (a golden file: regenerate deliberately, not blindly),
    `FleetTaskServiceTests`, `UserProviderResolverTests`, `ProvidersApiTests`, `GrainStorageSerializerTests`.
- Bump `CACHE` in `sw.js` (rule 3). Several tests pin the `sw.js` CACHE string (`ClientToolsAssetsTests`,
  `ModelRouterAssetsTests`, `PwaAssetsTests`, `ResearchAssetsTests`, `SearchAssetsTests` in commit 0ce147e's diff; the
  set can change): grep for the old value and update every one.
- **Verify:** `ProvidersApiTests` (round-trip + masking unchanged), `UserProviderResolverTests`,
  `GrainStorageSerializerTests`, `ModelRouterAssetsTests` (picker hides `supportsOther`), a router unit test that an
  `other` model is not a candidate, then `dotnet test Agent42.slnx`.

### Step 3 — Token registry (Agents project)
- `IModelProxyTokens` (Agents): `Mint(userId, runKey)` → opaque token (`mpt_` + 32 random bytes, base64url; hashed
  SHA-256 in the map, raw never stored), `TryValidate(token)` → `ModelProxyCaller(userId, runKey)` or null (also
  slides the idle TTL and counts the request), `RevokeRun(runKey)`. In-memory `ConcurrentDictionary`, injected
  `TimeProvider`, a periodic sweep (or lazy purge on `Mint`) so it cannot grow without bound, and a hard cap on live
  tokens per run (a runaway agent must not leak memory).
- Settings (`ModelProxy:*`, bound to options). Every default below is **the planner's pick, not a measured value**;
  change any of them with its test: `Enabled` `true`; idle TTL 30 min; **absolute token lifetime 6 h** (longer than a
  realistic fleet task, so a task re-mints through the tool rather than being cut off; the tool returns a fresh token
  when the live one has under half its lifetime left); per-run request cap 1000 (a runaway agent burning an expensive
  video model is the risk this bounds); max live tokens per run 8; `MaxRequestBytes` 50 MB (a video-submit may carry a
  reference image or clip; must not exceed Kestrel's request limit, check it and the ACA ingress); `TimeoutSeconds` 600
  (a provider may hold a request open while a job renders; polling APIs answer fast, so this is a ceiling not a norm).
- `TryValidate` takes an injected `TimeProvider` so expiry, sliding TTL and the absolute lifetime are testable with a
  fake clock.
- Disjoint from every other credential namespace by construction (gotcha "Each credential namespace gets its own
  directory grain type"): own prefix, own type, never registered in `IAgentKeyDirectoryGrain`.
- Register as a TryAdd singleton in `AddAgent42Silo()` (rule 7: identical in every host). The endpoint resolves the
  same singleton by optional `GetService` (the `/api/speech` pattern) and answers 404 when `Enabled` is false or the
  service is absent.
- **Verify:** `ModelProxyTokensTests` (mint/validate, expiry via fake time, sliding TTL, revoke, per-run cap, request cap,
  cap on live tokens, token never stored raw, unknown/empty token → null).

### Step 4 — Forwarder and endpoint (Backend)
- `AgentApi.ModelProxy.cs` with `MapModelProxyEndpoints`, **added to the list in `MapAgentApi`** (an unmapped partial
  silently deletes its endpoints; `AgentApiReachabilityTests` catches it). Route `ANY /api/model-proxy/{provider}/{**path}`
  (all methods; bare `/api/model-proxy/{provider}` and an empty path are valid).
- Token gate, in the `AgentMcpKeyGate` shape, **before anything else**: read the token from `Authorization: Bearer`,
  else `api-key`, else `x-api-key` (so the OpenAI, Azure OpenAI and Anthropic SDKs work with `base_url` = the proxy and
  `api_key` = the token, unmodified). Missing/unknown/expired/over-cap → one uniform 401 body, no hint which. The user id
  comes **only** from the token, never `GetUserId()`.
- Resolve the provider with `IUserProviderResolver.ResolveAsync(callerUserId)`; unknown key, GitHubCopilot, DecisionModel,
  or a provider with no key → 404 (uniform, no disclosure of which providers exist beyond the caller's own).
- Forwarder: build the upstream URI as specified in Context; **allowlist** request headers forwarded (`Content-Type`,
  `Accept`, `Content-Language`, `anthropic-version`, `anthropic-beta`, `OpenAI-Beta`; extend only with a test), strip
  everything else including all caller auth headers, `Host`, `Cookie`, `X-MS-CLIENT-*`, `X-Forwarded-*`; add the
  provider credential per kind; stream the request body (request body cap, `ModelProxy:MaxRequestBytes`, your pick,
  must also be ≤ Kestrel's limit: check) and stream the response with `ResponseHeadersRead`, never buffering a video;
  pass the upstream status and an allowlisted set of response headers (`Content-Type`, `Retry-After`, `x-request-id`,
  `Content-Disposition`); drop `Location` and do not follow redirects; overall timeout `ModelProxy:TimeoutSeconds`
  (your pick), cancelled on client abort. Reject a provider whose endpoint would loop back into this proxy
  (the host is a loopback alias, `localhost`, `127.0.0.0/8`, `[::1]`, `0.0.0.0` or `[::]`, on this app's own port, with a path
  starting `/api/model-proxy`). This is a **best-effort** guard against an accidental amplification loop, not a security
  boundary: the endpoint is the user's own config and a hostname alias or the public FQDN is not covered (a call to the
  FQDN goes through Easy Auth and fails). The per-run request cap bounds any loop that gets past it.
- Named client `model-proxy` registered in `ModelProxyRegistration` with its own handler, `RemoveAllResilienceHandlers()`,
  no redirects, no cookies.
- Logging: provider key, method, path **without query**, status, duration, bytes. Never a body or a header value.
  Never put the token in a log, span tag or exception message.
- **Verify:** `ModelProxyApiTests` on the shared API fixture with a fake upstream handler (see AC table).

### Step 5 — The `get_model_proxy_access` tool (chats and fleet)
- Tool (copy `ReadSkillResourceTool`'s shape: static `Create`, `AIFunctionFactory.Create`, errors returned as results,
  result truncated to a small constant, rule 5). It takes no arguments and returns: the proxy base URL, a fresh-or-live
  token, the expiry, and one line per **proxyable** provider (key, kind, declared model ids, the `other` flag). Provider
  list comes from `IUserProviderConfigGrain.GetAsync()` (ciphertext is present on that record: never read or return it).
  Its description is the rule the model reads (rule 11): it states that the token is a bearer secret for this run only,
  that the base URL is `<base>/api/model-proxy/<provider key>/`, that the path after the provider key is the provider's
  own API path, that SDKs take it as `base_url` + `api_key`, that API details come from the user, and that it must not
  be written into files or printed.
- Chat: add to the candidate list in `CopilotFunctions` only when the tool key is enabled (the new key, e.g.
  `model_proxy`, follows ADR 0021: expand any wildcard before the picker sees it; document the lands-OFF rule). The
  token's `runKey` is the `TurnContext` registered by `OpenCopilotRun` (see Context: read from `CurrentOrClosed`, refuse
  under `Closed`); **revoke in `CloseCopilotRun`** (`AgentGrain.Copilot.cs` ~149-166).
- Fleet: `FleetCopilotAgentFactory.BuildSessionConfig` (`FleetCopilotAgentFactory.cs:170-188`, `Tools = [.. canvas.Tools,
  .. todos?.Tools ?? []]` at :179) must be **threaded** with the user id, task id and the token service, then the tool
  appended there when the task's selected tool keys include the new key. How a fleet task's keys resolve is ADR 0099
  (per-task tool override) and `FleetTaskService`: read both, resolve it in Step 5, and write the answer into the ADR.
  `runKey` = the task id. **Revoke in the task run's `finally`** (`FleetTaskService.Run.cs:67-72`), **before**
  `await FinishAsync(...)` and in its own `try/finally`, so a throwing `FinishAsync` cannot leave the token live. Restart
  needs nothing: the registry is in-memory.
- Never offered to non-Copilot agent kinds (no shell, nothing to call it from), nor when
  `Copilot:BuiltInToolsEnabled` is false (chat) / `Fleet:CopilotAgentsEnabled` is false, nor when `ModelProxy:Enabled` is
  false.
- **Verify:** `ModelProxyToolTests` (lists only proxyable providers, never returns a key or ciphertext, revoked after
  close, absent from non-Copilot agents and when disabled), plus a grain test that `CloseCopilotRun` revokes.

### Step 6 — Config and hosts
- `appsettings.json` `ModelProxy` section (defaults). Env vars/Bicep only for settings an operator must flip without a
  rebuild (decide; `Enabled` is enough). DevHost: confirm it boots (it has no Copilot, so the tool never appears; the
  endpoint answers 401 without a token). `compose.yaml`: comment that chats have built-ins off there, so only fleet
  Copilot agents use the proxy. Easy Auth `excludedPaths` untouched (a test pins `/api/model-proxy/x` as NOT excluded).
- **Verify:** `AgentApiReachabilityTests`, `EasyAuthExclusionTests`, `dotnet build Agent42.slnx` (S3776).

### Step 7 — Docs (same MR; hard rule 2)
- `/decide` → **ADR 0140** (confirm the next free number at execution): decision, rejected alternatives (typed tool
  only, key in env/files, stateless DP tokens, translating proxy), egress extension of 0013, amends 0018/0107/0097
  (shell can use a configured key indirectly), the P1 result, and the `other` capability. Update
  `.agentic/decisions/README.md`.
- `/gotcha` entries: (a) the proxy path must never change the host or climb out of the endpoint's base path, the provider
  exemption from SSRF does not transfer to the path; (b) the token is a tool result and is bounded (run end, idle TTL,
  absolute lifetime, request cap), never "dead" or "safe" while the run lives; (c) the registry is in-memory and joins
  rule 12. **Amend** the existing gotcha "A user provider overlay is best-effort ... NOT SSRF-guarded"
  (`.agentic/gotchas.md:992`) with a pointer to (a), since its claim "do not let any future change accept a provider
  endpoint from ... a request" now has a neighbour that accepts a path. Add the registry/forwarder exemplar row if one is
  canonical.
- `CLAUDE.md`: feature-map row for the proxy and `other`; add the token registry to the rule 12 list. `/sync-docs`.

### Step 8 — Post-deploy checks (hand to the user; list in the MR description)
- **P1** From inside the running container (Copilot shell), `curl` the proxy over loopback with a token from the tool and
  confirm it is **not** redirected to Entra login. If it is, switch to the `/api/model-proxy/*` exclusion fallback.
- **P2** Call the real Fal-MiniMax model through the proxy (submit + poll) and confirm streaming and the timeout behave.
- **P3** Confirm the token is dead after the turn ends, and after a revision rollover.
- Azure RBAC is untouched by this plan (hard rule 5); there is nothing to hand over.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| A request with no / wrong / expired / revoked token gets one uniform 401 and nothing is forwarded | `Proxy_NoOrBadToken_Returns401AndForwardsNothing` | `tests/Agent42.Tests/ModelProxyApiTests.cs` |
| The token is accepted from `Authorization`, `api-key` and `x-api-key` | `Proxy_AcceptsTokenFromAnyOfThreeHeaders` | same |
| The user id comes from the token; a forged `X-MS-CLIENT-PRINCIPAL-ID` is ignored | `Proxy_UserIdFromToken_IgnoresPrincipalHeader` | same |
| The upstream sees the provider key in the kind's header and none of the caller's auth/cookie/host headers | `Proxy_InjectsProviderCredentialPerKind_StripsCallerHeaders` (OpenAICompatible, Foundry, Anthropic) | same |
| The path cannot change host or leave the endpoint's base path: asserts the upstream authority is unchanged AND the upstream path still starts with the endpoint's path, for `..`, `%2e%2e`, `%252e%252e`, `%252f`, `//x`, `\x`, `@`, a scheme, decoded `%3F`/`%23`/`%25` and control characters | `Proxy_PathCannotEscapeProviderBase` (theory) | same |
| The caller's query is forwarded raw and length-capped and cannot alter host or path | `Proxy_QueryStringPassThrough_IsCapped` | same |
| Absolute token lifetime cuts off a token that is still being used; the tool re-mints when under half is left | `Token_AbsoluteLifetime_EndsASlidingToken`, `Tool_ReMintsNearExpiry` | `tests/Agent42.Tests/ModelProxyTokensTests.cs`, `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| Under `TurnContext.Closed` (zero or several runs in flight) the tool refuses to mint | `Tool_ClosedContext_RefusesToMint` | `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| A throwing `FinishAsync` still revokes the fleet task's tokens | `FleetTaskEnd_RevokesTokens_EvenWhenFinishThrows` | `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| An `other` model is never the effective default (a stored default naming one is treated as unset; fallback is the first chat-capable model; none → `""`) | `EffectiveDefault_SkipsOther`, `EffectiveDefault_OnlyOtherModels_IsEmpty`, `EffectiveDefault_StoredDefaultNamingOther_IsIgnored` | `tests/Agent42.Tests/UserProviderResolverTests.cs` |
| The fleet never picks an `other` model for planner, judge or agents (server and `fleet.js`) | `IsToolCapable_ExcludesOther`, a `fleet.js` source guard | `tests/Agent42.Tests/FleetTaskServiceTests.cs` |
| Another user's provider key is a 404 | `Proxy_OtherUsersProvider_Returns404` | same |
| GitHubCopilot, DecisionModel and key-less providers are refused | `Proxy_RefusesNonForwardableKinds` | same |
| Bodies stream both ways; the upstream status and allowlisted headers pass; `Location` is dropped, redirects not followed | `Proxy_StreamsAndPassesStatus_NoRedirects` | same |
| Request-size cap, per-run request cap, timeout and client abort end the call cleanly | `Proxy_EnforcesCapsAndTimeout` | same |
| A provider that points back at the proxy is refused | `Proxy_SelfReferencingEndpoint_Refused` | same |
| Disabled or service absent → 404 | `Proxy_Disabled_Returns404` | same |
| No log line, span tag or error carries the token, a header value or a body | `Proxy_NeverLogsSecrets` | same |
| Tokens: expiry, sliding TTL, revoke, caps, never stored raw | `ModelProxyTokensTests.*` | `tests/Agent42.Tests/ModelProxyTokensTests.cs` |
| Tool lists only proxyable providers and never a key/ciphertext; absent for non-Copilot and when disabled | `ModelProxyToolTests.*` | `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| Closing a Copilot run / ending a fleet task revokes its tokens | `CopilotRunClose_RevokesTokens`, `FleetTaskEnd_RevokesTokens` | `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| Loopback base URL precedence | `BaseUrl_PrefersSettingThenServerAddress` | `tests/Agent42.Tests/ModelProxyToolTests.cs` |
| The endpoint is mapped and not Easy-Auth-excluded | `AgentApiReachabilityTests` (existing), `Exclusions_DoNotCoverModelProxy` | `tests/Agent42.Tests/AgentApiReachabilityTests.cs`, `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| `other` is a trailing default-false field and survives the Cosmos JSON path | `StoredModel_Other_RoundTripsOnJsonPath` | `tests/Agent42.Tests/GrainStorageSerializerTests.cs` |
| `other` models never appear in chat pickers, Auto candidates, or as a default | `ModelRouterAssetsTests` (picker), `Router_ExcludesOtherModels`, `EffectiveDefault_SkipsOther` | `tests/Agent42.Tests/ModelRouterAssetsTests.cs`, `tests/Agent42.Tests/UserProviderResolverTests.cs` |
| `other` round-trips through `/api/providers` and is masked correctly | `ProvidersApiTests` additions | `tests/Agent42.Tests/ProvidersApiTests.cs` |
| `sw.js` CACHE bumped and every pinning test updated | the five existing asset tests | `tests/Agent42.Tests/*AssetsTests.cs` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` at plan time)

| Gate | Threshold | Tool | Result |
|------|-----------|------|--------|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet) | _to be measured; report SKIPPED if the tool cannot run, never estimate_ |
| `cognitive_complexity_max` | 15 | SonarAnalyzer S3776 at build time | _`dotnet build Agent42.slnx`_ |
| `vulnerable_packages` | 0 | CI `package-freshness` job | no new packages planned; if one is needed, `dotnet add package`, never a remembered version |
| Lint | clean | `dotnet format Agent42.slnx --verify-no-changes` | _to be run_ |
| Android | green | `./gradlew test lint assembleDebug` in `tools/Agent42.Android` | only if `ApiModels.kt` changes; needs JDK 21 + `ANDROID_HOME` |

## Out of scope

- A typed `generate_video` (or any per-modality) tool, polling helpers, or job tracking.
- Built-in API notes or docs for any provider (the user's prompts carry them).
- Usage/cost accounting for proxied calls; per-provider rate limiting beyond the per-run request cap.
- Following upstream redirects, WebSocket/upgrade passthrough, response rewriting (for example turning provider result URLs into proxy URLs).
- Non-Copilot agents: they have no shell.
- Multi-replica correctness (the registry is single-replica by design; see Context).
- Image/speech/embedding models keep their existing dedicated capability flags. Only the new `other` is added.
- Azure RBAC and infra changes beyond a possible `ModelProxy__Enabled` env var.

## Review

Critic pass (fresh-context `critic` subagent, 2026-11-22): **not a clean pass**, 2 high, 5 medium, several low. Each
finding was checked against the tree before acting. None was rejected outright; one was narrowed.

| Finding | Verdict | Handling |
|---|---|---|
| H1 default-model rule does not exist (`EffectiveDefault` falls back to `declared[0]`, no non-chat filter) | Accepted, verified (`UserProviderResolver.cs:191-207`) | Step 2 now writes the rule explicitly (skip `other`, stored default naming `other` treated as unset, first chat-capable fallback, none → `""`) with three tests. The same gap exists for embedding/speech/transcription today; deliberately **not** widened in this MR, recorded in the ADR. |
| H2 path-escape list incomplete; `SearxngProxy.BuildUpstreamUri` drops the base path | Accepted, verified (`SearxngProxy.cs:118-130`) | Context now treats it as scar tissue, not a copy target, and lists the five required properties (base path kept, double-encoded dots/slashes, decoded `?`/`#`/`%`, scheme/authority, raw capped query). The theory asserts authority AND base path. |
| M1 sliding TTL makes the "expires" argument weaker | Accepted | Added an absolute max lifetime (6 h, a pick), re-mint near expiry, and wording rule: "bounded", never "dead"/"safe". |
| M2 self-reference guard is string-level | Narrowed | Kept as a **best-effort** guard covering loopback aliases (`localhost`, `127/8`, `[::1]`, `0.0.0.0`, `[::]`) and labelled non-boundary. Hostname and FQDN aliases are not covered: the FQDN fails at Easy Auth, and the per-run cap bounds any loop. Rejected "drop the claim": a guard against the accidental self-loop is cheap and useful. |
| M3 chat run identity unspecified; `Closed` case; grain key is not the user id | Accepted, verified (`SchedulerBoundAIFunction.cs:54-62`) | Context and Step 5: run identity from `TurnContext.CurrentOrClosed`, `runKey` = the registered `TurnContext`, refuse under `Closed`, user id via `UserIdFromGrainKey`. Test added. |
| M4 `other` missed in `FleetTaskService.IsToolCapable` (:352), `fleet.js:478`, router consumers, golden/test files | Accepted, verified | Step 2 names them and the test files incl. `GoldenStates.cs`; fleet ACs added. Positive-filter pickers (`providers.js`) need no change. |
| M5 fleet wiring needs threading and a safe revoke point | Accepted, verified (`FleetTaskService.Run.cs:67-72`) | Step 5 threads the factory, resolves ADR 0099 key resolution in-step (recorded in the ADR), and revokes before `FinishAsync` in its own `try/finally`; test added. |
| L1 unflagged numbers, no logging capture | Accepted | Every default is labelled a planner pick with a rationale. `Proxy_NeverLogsSecrets` uses a capturing logger/`ActivityListener` in the API fixture. |
| L2 `IServerAddressesFeature` wildcard hosts | Accepted | Step 1 lists the cases. |
| L3 Easy Auth loopback claim | Already hedged (P1 + fallback) | No change. |
| L4 "four tests" vs five names; L5 amend the SSRF gotcha | Accepted | Wording fixed; Step 7 amends `gotchas.md:992`. |
| L6, L7 | No defect | None. |

Not verified by the plan or the critic, both flagged where they matter: the Copilot SDK's per-session environment
(does not change the design) and whether an in-container loopback call traverses Easy Auth (post-deploy check P1, with
a fallback).
