# Plan: Config-driven remote MCP servers (per-user, no per-server code)

planned-against: main @ 31d9500

## Phase status (ADR 0024)

- **Phase 1 — DELIVERED** (None/Header, Steps 0–7), merged (#47).
- **Phase 2 — DELIVERED** (MSAL ClientCredentials, Step 8, branch `feature/remote-mcp-servers-phase2`):
  `McpAuthHeader.cs` (`IAuthHeaderProvider`/`AuthHeaderHandler`/`ClientCredentialsTokenProvider`), a
  ClientCredentials branch in `McpConnectionFactory` over a plain `HttpClient` (no resilience inheritance),
  the `IMcpConnectionFactory` seam generalized to an `McpConnectionSecrets` bundle, API DTO/validation/
  mask + UI for authority/clientId/clientSecret(write-only)/scopes. AC11 (`ClientCredentials_StampsBearer`)
  tests header-stamping against a fake token provider; the real Entra/Dataverse round-trip is deploy-smoke.
- **Phase 3 — DELIVERED** (InteractiveOAuth, Step 9, branch `feature/remote-mcp-servers-phase3`, ADR 0025):
  app-driven authorization-code + PKCE with **PRM discovery** (the SDK's OAuth blocks — Step 0), across
  `GET /api/mcp/oauth/start` + `/callback` (behind Easy Auth), pending state in `UserMcpOAuthPendingGrain`,
  tokens encrypted in `TokenCiphertext`, connect-time refresh in the provider, static Bearer injection, UI
  Connect button + `?mcp_oauth` return handling. `McpOAuthClientTests` (PKCE/discovery/exchange/refresh) +
  `McpOAuthEndpointTests` (AC12 start→callback→persist) + provider OAuth cases; the live dance is deploy-smoke.
  **This completes the plan — the spec now moves to `specs/completed/`.**

**Where the Phase-1 implementation diverged from the plan (all sound, none rejected):**
- **Truncation moved to the provider, not the connection loader.** `McpConnectionFactory` returns RAW
  tools; `UserMcpToolProvider` applies the `TruncatingMcpTool` (`DelegatingAIFunction`) wrapper. This makes
  truncation uniform across servers and testable through a fake connection seam (AC9 via the provider).
- **An `IMcpConnectionFactory` seam was introduced** (the "stub transport" option Step 0 left open) so the
  provider's orchestration — version cache, best-effort skip, reload, bounded lifetime — is unit-tested
  deterministically without a live server; the real SDK path is proven by the Step 0 spike + one real-factory
  dead-endpoint integration test (`RealFactory_DeadEndpoint_IsSkipped_NotFatal`).
- **HttpClient shape:** Phase 1 uses the transport's built-in client via `AdditionalHeaders` (no DI-resolved
  `HttpClient`), so the resilience-pipeline gotcha is avoided by construction (recorded in Step 0 results).
- **Bounded lifetime = retain-most-recent-2 versions per user**, dispose older on eviction
  (`RetainedVersionsPerUser = 2`).
- **Server identity is a server-minted GUID `Id`** (not the name) — a user may add duplicate-named servers.
- **Phase-1 validation accepts None/Header and rejects ClientCredentials/InteractiveOAuth** as "not yet
  supported"; the request DTO carries only None/Header fields, while the stored `StoredMcpServer` record
  carries all phases' fields now (trailing `[Id]` defaults → no future migration).
- **Registration sites beyond the plan's list:** DevHost also needed `IMcpSecretProtector` (the endpoints
  resolve it), and the four existing API-host test fixtures (`AgentApiTests`, `AttachmentTests`,
  `ToolSelectionApiTests`, `ProvidersApiTests`) each needed it for the route matcher — the same
  every-`MapAgentApi`-host rule as the provider protector (a 39-test regression caught and fixed).
- **Catalog count test** `Tools_Catalog_ReturnsFiveSelectableKeys` → `ReturnsSelectableKeys` (+`mcp`); the
  guard test renamed to `..._OrIsAnMcpTool` and now injects an MCP tool to exercise the carve-out.


## Overview

Let a signed-in user add **remote MCP servers** from the web UI and have their tools appear
to the agent — with **no per-server code**. Every MCP server exposes the same discovery
interface, and its tools convert to `AITool` (`McpClientTool`), which merges directly into the
`List<AITool>` that `AgentGrain.BuildAgent` already builds (`src/Agent42.Agents/AgentGrain.cs:239`).
Adding a server becomes a config entry, not a code change.

Why now: the user wants Dataverse/GitHub-style remote tool servers wired in without bespoke
integration per server. A reference implementation was drafted in a separate conversation
(standalone console app) — it is a **mechanism reference only**; its bootstrap does not transfer
(see Context).

This feature is the MCP analogue of user-configurable model providers (ADR 0018): the same
per-user, encrypted-secret, version-cached, no-restart shape. It is deliberately built by
**copying that feature's exemplars**, not by inventing structure.

## Affected areas (verified against the tree @ 31d9500)

New files (mirroring the ADR 0018 provider feature):
- `src/Agent42.Agents/IUserMcpConfigGrain.cs` — per-user overlay grain interface (mirror `IUserProviderConfigGrain.cs`)
- `src/Agent42.Agents/UserMcpConfigGrain.cs` — grain impl (mirror `UserProviderConfigGrain.cs`)
- `src/Agent42.Agents/UserMcpConfig.cs` — `StoredMcpServer` + `UserMcpConfig` records (mirror `UserProviderConfig.cs`)
- `src/Agent42.Agents/IUserMcpToolProvider.cs` — `Task<McpToolSet> GetForUserAsync(string userId, CancellationToken)` returning `IReadOnlyList<AITool>` + version (interface in Agents so `AgentGrain` sees only `AITool`). **Also declare `SingleUserMcpToolProvider` here** — the offline/test adapter, mirroring how `SingleUserChatClientFactoryProvider` sits in `IUserChatClientFactoryProvider.cs:23`.
- `src/Agent42.Backend/UserMcpToolProvider.cs` — impl: connect + `ListToolsAsync`, version-cached, best-effort (mirror `UserChatClientFactoryProvider.cs`)
- `src/Agent42.Backend/McpConnectionFactory.cs` — builds a transport + `McpClient` per `StoredMcpServer` by auth mode (the reference's `McpToolLoader`/`Authentication` logic, reshaped)
- `src/Agent42.Backend/McpSecretProtector.cs` — **new protector class** (`IMcpSecretProtector` + impl), mirroring `ProviderSecretProtector.cs`. Required because the DP **purpose is a private const baked into `ProviderSecretProtector` (`ProviderSecretProtector.cs:33`), not a parameter** — the plan's "distinct purpose" cannot be passed to the existing class. Own purpose `"Agent42.Mcp.Secret.v1"`, own `AddSingleton` registration, own `McpSecretProtectorTests`. (Alternative — reuse `IProviderSecretProtector` and accept the shared provider DP scope — rejected: MCP secrets are a distinct concern and a shared scope couples their key rotation.)
- `src/Agent42.Agents/McpToolLimits.cs` — return-value budget (mirror `WebToolLimits.cs`)
- Tests under `tests/Agent42.Tests/` (see AC→test map)

Modified:
- `Directory.Packages.props` — add `ModelContextProtocol` + `Microsoft.Identity.Client` (**resolved at add time**, not the versions written here)
- `src/Agent42.Backend/Agent42.Backend.csproj` — reference both packages (CPM: no version attribute)
- `src/Agent42.Agents/ToolCatalog.cs` — add the `mcp` group key + descriptor
- `src/Agent42.Agents/AgentGrain.cs` — inject `IUserMcpToolProvider`; resolve `_mcpTools` in `OnActivateAsync` **before** the first `BuildAgent`; add MCP tools to the list in `BuildAgent` gated by the `mcp` key (mirror the `skillsEnabled` gate)
- `src/Agent42.Backend/AgentApi.cs` — `GET/POST/PUT/DELETE /api/mcp-servers` (mirror `/api/providers`); Phase 3 adds the OAuth start + callback endpoints
- `src/Agent42.Backend/Program.cs` — register `IUserMcpConfigGrain` deps, `IUserMcpToolProvider` singleton
- `tools/Agent42.DevHost/Program.cs` — register the `SingleUserMcpToolProvider` adapter
- `src/Agent42.Backend/wwwroot/app.js` + `index.html` + `styles.css` — Preferences → "MCP servers" dialog (mirror the providers dialog)
- **Every silo/host that activates `AgentGrain` must register `IUserMcpToolProvider`** or activation throws (Orleans activates through DI) and *all* existing `AgentGrain` tests fail. The complete set — the exact sites that register the ADR 0018 analogue `IUserChatClientFactoryProvider`:
  - `src/Agent42.Backend/Program.cs` (prod singleton)
  - `tools/Agent42.DevHost/Program.cs` (single-user adapter)
  - `tests/Agent42.Tests/TestSiloConfigurator.cs:70`
  - `tests/Agent42.Tests/NoModelSiloConfigurator.cs:57`
  - `tests/Agent42.Tests/FakeGeocoder.cs:49`
  - `tests/Agent42.Tests/AttachmentRehydrateTests.cs:44`
  - `tests/Agent42.Tests/CapSizedWebPageFetcher.cs:43`
  - `src/Agent42.AppHost/AppHost.cs` — **no** new Cosmos container needed (the grain rides `"agent-state"`); AppHost is unaffected beyond that confirmation.

## Context & constraints (what the executor can't cheaply re-derive)

### The reference implementation and what transfers
Source: `C:\Users\PeterBons\Downloads\files` (`McpToolLoader.cs`, `Authentication.cs`,
`McpServerOptions.cs`, `LoopbackOAuth.cs`, `Program.cs`, `appsettings.json`). **Transfers:** the
per-auth-mode connection switch, the `DelegatingHandler` that stamps an auth header per request,
the MSAL `ConfidentialClientApplication` client-credentials provider. **Does NOT transfer:** the
standalone `Program.cs`/`ServiceCollection`/`DefaultAzureCredential` bootstrap (Agent42 wires
tools per-user, per-grain-activation); the `appsettings.json` `McpServers` array (config is
per-user + encrypted here, not appsettings); `LoopbackOAuth.cs` (`Process.Start` browser +
`HttpListener` is a **desktop** flow — useless on headless ACA; see Phase 3).

### The SDK API is UNVERIFIED — a spike is Step 0 (hard rule #4)
The real package is **`ModelContextProtocol`** (latest stable **1.4.1** at plan time; there is no
separate `ModelContextProtocol.Client` package — the client types live in the
`ModelContextProtocol.Client` **namespace** within it). The reference's type names
(`HttpClientTransport`, `HttpClientTransportOptions`, `McpClient.CreateAsync`, `ListToolsAsync`,
`ClientOAuthOptions`) are recalled from *its* conversation's memory of "v1.2.0" and are **not
verified against 1.4.x**. Do not write code against them from memory. Step 0 resolves the package
and confirms, via `/spike-dotnet`, the actual: transport type for remote Streamable HTTP, the
`McpClient` create signature, that `McpClientTool` implements `AITool` (or how tools surface as
`AITool`), how to pass a custom `HttpClient`/headers, and how a tool-call result is represented
(needed for the truncation wrapper). Everything downstream depends on these shapes.

### This must fit four Agent42 invariants (all with governing docs)
1. **Per-user, encrypted, no-restart (ADR 0018).** MCP config is a per-user overlay grain, not
   `appsettings.json`. Secrets (Header value, OAuth client secret, OAuth tokens) are encrypted via a
   **new `IMcpSecretProtector`** (see New files — the existing `ProviderSecretProtector`'s DP purpose
   is a private const at `ProviderSecretProtector.cs:33`, not a parameter, so it cannot be reused with
   a distinct scope) **before** the grain sees them, stored as ciphertext, masked to
   `hasSecret: bool` on GET, blank-on-update = keep. Grain rides the existing `"agent-state"` storage
   provider with a new state name (e.g. `"mcp-servers"`) — **no new Cosmos container** (Orleans
   partitions by grain type; `UserProviderConfigGrain` already proves this pattern). Version-stamped;
   the tool provider caches by `(userId, version)` exactly like `UserChatClientFactoryProvider.cs:41`.
2. **Selectable tools (ADR 0021).** Add ONE group key `mcp` to `ToolCatalog`
   (`src/Agent42.Agents/ToolCatalog.cs`). MCP tool names are dynamic/user-defined and cannot be
   static catalog entries, so gate the whole MCP group with
   `mcpEnabled = capabilities.SupportsTools && enabled.Contains(ToolCatalog.Mcp)` and only then add
   the tools; do NOT rely on `IsToolEnabled` per tool name (unmapped names are kept unconditionally —
   `ToolCatalog.cs:79`, so they'd bypass the picker). **Insertion window is precise:** add the gated
   MCP tools to the `Tools` list **after** the `IsToolEnabled` filter (`AgentGrain.cs:262`) and
   **before** the capability intersection `Tools = capabilities.SupportsTools ? tools : []`
   (`AgentGrain.cs:283`). (This is a `Tools`-list gate — it is NOT the `skillsEnabled` shape, which
   gates a **context provider** in the `AIContextProviders` switch at `AgentGrain.cs:297`; borrow the
   `SupportsTools && enabled.Contains(key)` *idea* from it, not the insertion site.)
   **Catalog-guard conflict:** `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate`
   (`AgentGrainTests.cs:~1438`) asserts every registered tool maps to a catalog key or is `get_date`.
   MCP tools deliberately violate that — they are the documented **group-gated exception**. Step 5 must
   edit that guard to add an "…or is an MCP tool" carve-out (identify MCP tools by the set returned from
   `IUserMcpToolProvider`, not by name-guessing), and the ADR (Step 7) records the exception.
   Per-server enable/disable is a separate concern
   living as an `Enabled` flag in each `StoredMcpServer` (the reference already has this). `mcp` is
   off-by-default for existing **explicit** tool-savers (ADR 0021's documented semi-breaking note)
   and on via `null == all` for never-configured users — but a user with **zero** configured MCP
   servers gets zero MCP tools regardless, so the egress surface only opens once they deliberately add
   a server.
3. **2 MB session cap (ADR 0004).** An MCP tool's **return value is persisted** into the session and
   replayed every turn (the "an agent tool's RETURN value is persisted" gotcha —
   `.agentic/gotchas.md`). Wrap each `McpClientTool` so its serialized result is truncated to a budget
   in a new `McpToolLimits` (mirror `src/Agent42.Agents/WebToolLimits.cs`). The wrapper MUST preserve
   the inner tool's name + JSON schema so the model still calls it correctly — Step 0's spike confirms
   the cleanest wrap mechanism.
4. **Egress + SSRF (ADR 0013/0018).** The MCP endpoint comes from the authenticated user's **own
   persisted config**, never a model or live request — so, exactly like a user provider endpoint
   (ADR 0018 gotcha, `src/Agent42.Backend/AgentApi.cs` `ValidateProvider`), it carries **no
   `PrivateAddressGuard`** and may be `localhost` (a self-hosted MCP server is a valid target).
   Validation is only absolute `http`/`https` + parseable auth mode. Do NOT add an address guard, and
   never accept an MCP endpoint from a tool argument/request body. **Headline consequence (document
   it):** an MCP server sees conversation content, and its tool results are a new inbound
   prompt-injection channel — the same framing as ADR 0011/0013; this is an `/decide` ADR (Step N).

### Connection lifetime / grain resolution ordering
`BuildAgent` is sync; MCP tools require an async connect + `ListToolsAsync`. Resolve
`_mcpTools = await _mcpToolProvider.GetForUserAsync(_userId, ct)` in `OnActivateAsync` **before**
the first `BuildAgent(_modelId)` — the same hard ordering hazard as `_chatClientFactory` and
`_userDefaultEnabledTools` (`.agentic/gotchas.md` "user default must be fetched BEFORE the first
BuildAgent", and AgentGrain.cs:117/133/135). The provider is a **singleton** holding live
`McpClient`s (the `McpClientTool`s reference them), keyed by `(userId, version)`. It must be
**best-effort**: a dead/slow MCP server must never break grain activation or the whole tool set —
connect with a timeout, skip failures, log (mirror the overlay best-effort rule, ADR 0018). Accept
config-change staleness: a warm grain picks up MCP changes on next activation (same model as ADR
0018's provider hot-reload).

**Connection lifetime is NOT a Step-0 detail — it is a standing resource cost the copied exemplar
doesn't carry.** `UserChatClientFactoryProvider`'s cache is never evicted
(`UserChatClientFactoryProvider.cs:22-24`) — cheap, because a chat-client factory is not a live
socket. An `McpClient` **is** a live connection, so a never-evict `(userId, version)` cache leaks one
open connection per config change. But disposal is unsafe while grains still hold the version's
`McpClientTool`s. Step 4 must pick a bounded policy — e.g. **retain-most-recent-N versions per user,
dispose older on eviction, tolerate an in-flight grain seeing a disposed client as a skipped tool** —
and state it explicitly; do not ship the unbounded copy of the exemplar's cache.

### Auth modes (user selected all four; staged by risk)
- **None / Header** — Phase 1. Header uses the reference's `DelegatingHandler` stamping a static
  (decrypted) header value per request.
- **ClientCredentials (MSAL/Entra)** — Phase 2. `Microsoft.Identity.Client`
  `ConfidentialClientApplication.AcquireTokenForClient` behind the same header handler (MSAL caches +
  refreshes). This is the **Dataverse** path. `Microsoft.Identity.Client` latest stable **4.86.1**
  (resolve at add time). NOTE the named-`HttpClient` retry-pipeline gotcha
  (`.agentic/gotchas.md` "A named HttpClient silently inherits AddServiceDefaults' retry pipeline") —
  any `HttpClient` built for MCP transport in the Backend inherits the standard resilience handler and
  should `RemoveAllResilienceHandlers()` unless retries are wanted.
- **InteractiveOAuth** — Phase 3, highest risk, **its own MR**. The reference's server-side loopback
  cannot work headless. The Agent42 flow: an authenticated `GET /api/mcp/oauth/callback` (behind Easy
  Auth — do **NOT** add to `excludedPaths`, or `GetUserId()` returns `local-dev`; confirmed
  `.agentic/gotchas.md` "excludedPaths endpoint" + scout) captures the `code`; the app drives
  auth-code + PKCE itself (store PKCE verifier + `state` as per-user pending config) and persists the
  resulting access/refresh tokens **encrypted**; the live token is then injected via the SAME header
  handler as Header/ClientCredentials, with app-owned refresh. The SDK's built-in `ClientOAuthProvider`
  assumes a **blocking** redirect delegate, which does not fit a stateless two-request web flow — Step 0
  evaluates whether to use it at all or drive PKCE directly. There is no existing OAuth/redirect/popup
  infrastructure in the app (scout confirmed) — this phase builds it. Given the size, Phase 3 may be
  deferred; Phases 1–2 already deliver the headline ask for the common cases.

### Package ownership
MCP client code (the `ModelContextProtocol` package) lives in **`src/Agent42.Backend`** (the
`UserMcpToolProvider` impl needs `IProviderSecretProtector`, which is Backend-only). `Agent42.Agents`
does **not** reference the package — `IUserMcpToolProvider` returns `IReadOnlyList<AITool>`, a
`Microsoft.Extensions.AI` type already available there. This mirrors how `IUserChatClientFactoryProvider`
(interface in Agents) has its impl in Backend.

## Steps

### Phase 1 — Foundation + None/Header (shippable MR)

**Step 0 — SDK spike (blocking, do first).** Via `/spike-dotnet`, resolve `ModelContextProtocol`
(latest stable) and confirm the real API: remote Streamable-HTTP transport type + options, the
`McpClient` create call, `ListToolsAsync`, that tools surface as `AITool`, custom-`HttpClient`/header
injection, and the tool-result representation (for truncation). Record the confirmed shapes in the
spec's Review section or as a gotcha. *Verify:* the spike program lists tools from a public/mock MCP
endpoint and prints a tool's `AITool` type + name. **No repo code until this passes.**

**Step 1 — Packages.** Add `ModelContextProtocol` (+ `Microsoft.Identity.Client`, used in Phase 2)
to `Directory.Packages.props` via `dotnet add package` (resolved, not recalled) and reference in
`Agent42.Backend.csproj`. *Verify:* `dotnet build Agent42.slnx`.

**Step 2 — Config model + grain.** Create `UserMcpConfig.cs` (`StoredMcpServer` record: name,
endpoint, auth mode enum-as-string, `Enabled`, header name, encrypted secret ciphertext, MSAL
authority/clientId/encrypted-secret/scopes, OAuth fields for Phase 3; + `UserMcpConfig` version-stamped
record with `Empty => new(...)` factory — the shared-static gotcha), `IUserMcpConfigGrain.cs`,
`UserMcpConfigGrain.cs`. Mirror the ADR 0018 trio exactly. *Verify:*
`tests/Agent42.Tests/UserMcpConfigGrainTests.cs` (round-trip, version bump, blank-secret-keeps,
per-user isolation) — mirror `UserProfileGrainTests`/provider grain tests.

**Step 3 — API endpoints.** `GET/POST/PUT/DELETE /api/mcp-servers` in `AgentApi.cs`, `GetUserId()`-scoped,
secret masked to `hasSecret`, encrypt via `IProviderSecretProtector` in the handler before `SaveAsync`.
A `ValidateMcpServer` (absolute http/https, valid auth mode, required fields per mode) returning 400.
*Verify:* `tests/Agent42.Tests/McpServersApiTests.cs` (GET never returns secret; blank-secret keeps;
validation 400s; localhost accepted) — mirror `ProvidersApiTests`.

**Step 4 — Tool provider + connection factory (None/Header only).** `IUserMcpToolProvider` +
`SingleUserMcpToolProvider` (Agents), `UserMcpToolProvider` (Backend, version-cached with the
**bounded** lifetime policy above, best-effort, timeout), `McpConnectionFactory` (None + Header via
the `DelegatingHandler`). Include the `McpToolLimits` truncation wrapper. **The transport `HttpClient`
inherits the standard resilience handler** — this applies in **Phase 1**, not just Phase 2: the
connect + `ListToolsAsync` runs awaited inside non-reentrant `OnActivateAsync`, so a retried
dead-server dial multiplies activation stall and re-dials a server the timeout meant to skip (the
"named HttpClient inherits AddServiceDefaults' retry pipeline" gotcha). Decide the client shape here:
either `AddHttpClient(...)` + `RemoveAllResilienceHandlers()`, or a plain `new HttpClient` (like
`OpenAIImageGenerator`'s static client) — and state which. Register the singleton in `Program.cs` +
the `SingleUserMcpToolProvider` in DevHost **and every test silo configurator** (see Affected areas).
*Verify:*
`tests/Agent42.Tests/UserMcpToolProviderTests.cs` against a **local mock MCP server** (stand up an
in-process MCP server, or a stub transport — decide in Step 0): tools load; a dead server is skipped,
not fatal; a large tool result is truncated to budget; version bump reloads.

**Step 5 — Catalog + BuildAgent wiring.** Add `ToolCatalog.Mcp = "mcp"` + descriptor
(`RequiresHostedTools: false`). Inject `IUserMcpToolProvider` into `AgentGrain`; resolve `_mcpTools`
in `OnActivateAsync` before the first `BuildAgent`; in `BuildAgent` add `_mcpTools` gated by
`mcpEnabled = capabilities.SupportsTools && enabled.Contains(ToolCatalog.Mcp)`. *Verify:*
`tests/Agent42.Tests/AgentGrainTests.cs` new cases: MCP tools reach the model when `mcp` enabled +
tools-capable; dropped when `mcp` disabled; dropped on a no-tools model (use `FakeChatClient`'s
`[tools:…]` echo, per the skills-gate exemplar); `_mcpTools` resolved before first `BuildAgent`
(mirror `UserDefault_AppliesWhenNoOverride`).

**Step 6 — UI.** Preferences → "MCP servers" dialog in `app.js`/`index.html`/`styles.css`: list
(`GET`), add/edit (write-only secret field, blank = keep), delete, per-server `Enabled` toggle, auth-mode
selector showing only the fields that mode needs. Mirror the providers dialog
(`app.js` `loadProviders`/`openProviderDialog`/`saveProvider`). *Verify:*
`tests/Agent42.Tests/PwaAssetsTests.cs`-style source-text asserts (there is no JS runner — the repo's
convention), plus manual `/run` smoke against DevHost.

**Step 7 — ADR + docs.** `/decide` an ADR (MCP servers: per-user, encrypted, `mcp` group key,
egress/injection framing, InteractiveOAuth deferred/staged). `/sync-docs` for CLAUDE.md's "Where
knowledge lives" bullets and any tripped gotcha. *Verify:* `/audit-docs` clean on touched claims.

### Phase 2 — ClientCredentials (MSAL) — small follow-on MR
**Step 8.** Add the MSAL `ConfidentialClientApplication` provider behind the header handler; extend
`McpConnectionFactory` + config + UI + validation for the ClientCredentials fields; `RemoveAllResilienceHandlers()`
on the transport `HttpClient`. *Verify:* `UserMcpToolProviderTests` ClientCredentials case (token acquired
+ header stamped — assert against a fake token endpoint; the real Entra/Dataverse path is deploy-smoke).

### Phase 3 — InteractiveOAuth — separate, higher-risk MR
**Step 9.** `GET /api/mcp/oauth/start` (build authz URL + PKCE/state, persist pending) and
`GET /api/mcp/oauth/callback` (behind Easy Auth; exchange code→tokens; persist encrypted). UI: a
"Connect" button that navigates to `/start` and returns to the app (query-param + reload, or the
IndexedDB hand-off pattern of ADR 0023). Token refresh owned by the provider. *Verify:*
`McpOAuthTests` for URL/PKCE construction + callback token persistence against a fake authz server;
the live interactive dance is deploy-smoke. **Re-run `critic` before this MR** — it introduces the
one new inbound auth surface.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|----------------------|------|------|
| AC1 | A user's None/Header MCP server's tools reach the model | `McpTools_ReachModel_WhenEnabledAndToolsCapable` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | Disabling the `mcp` key drops all MCP tools | `McpDisabled_DropsMcpTools` | `AgentGrainTests.cs` |
| AC3 | A no-tools model gets no MCP tools | `NoToolModel_GetsNoMcpTools` | `AgentGrainTests.cs` |
| AC4 | `_mcpTools` resolved before first `BuildAgent` (no NRE / ignored-on-activation) | `McpTools_ResolvedBeforeFirstBuildAgent` | `AgentGrainTests.cs` |
| AC4b | A user with an **explicit** `EnabledTools` list lacking `mcp` gets no MCP tools (the documented semi-breaking off-by-default, ADR 0021) | `ExplicitToolsWithoutMcp_DropsMcpTools` | `AgentGrainTests.cs` |
| AC5 | GET never returns a secret; blank secret on update keeps stored | `Get_NeverReturnsSecret`, `Put_BlankSecret_Keeps` | `McpServersApiTests.cs` |
| AC6 | Endpoint validation: localhost accepted, malformed 400 | `AcceptsLocalhost_RejectsMalformed` | `McpServersApiTests.cs` |
| AC7 | Grain state round-trips; version bumps; per-user isolation | `RoundTrips`, `VersionBumps`, `IsolatedPerUser` | `UserMcpConfigGrainTests.cs` |
| AC8 | A dead MCP server is skipped, not fatal | `DeadServer_IsSkipped_NotFatal` | `UserMcpToolProviderTests.cs` |
| AC9 | An oversized tool result is truncated to budget | `ToolResult_Truncated_ToBudget` | `UserMcpToolProviderTests.cs` |
| AC10 | Version bump reloads the tool set | `VersionBump_ReloadsTools` | `UserMcpToolProviderTests.cs` |
| AC11 (P2) | ClientCredentials acquires a token and stamps the header | `ClientCredentials_StampsBearer` | `McpAuthTests.cs` (dedicated auth-mechanism file; the real Entra round-trip is deploy-smoke) |
| AC12 (P3) | OAuth callback exchanges code and persists encrypted tokens | `OAuthCallback_PersistsEncryptedTokens` | `McpOAuthEndpointTests.cs` (client/PKCE/discovery unit tests in `McpOAuthClientTests.cs`) |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)
- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80 (coverlet: `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"`)
- cognitive_complexity_max: 15
- dependencies: vulnerable_packages 0 (hard block); new deps `latest-stable`, resolved at add time
- CI hard blocks: build, tests, `dotnet format --verify-no-changes`, vulnerable packages, doc-path integrity
- Branch: `feature/*` + MR; never commit to `main` (hard rule 1)

## Out of scope
- **App-global (admin) MCP servers.** Per-user only, mirroring ADR 0018.
- **stdio / local-process MCP servers.** Remote HTTP transport only (the app is a headless container).
- **Hosted MCP via Foundry.** This is the client-managed path; Foundry's hosted MCP tool is a
  different, Foundry-coupled mechanism (noted in the reference) and not in scope.
- **Live-invalidation of in-flight grains** on MCP config change — next-activation pickup only (same
  as ADR 0018).
- **Per-conversation / per-agent selection of individual MCP servers.** v1 gates MCP as one `mcp`
  group across the ADR 0021 layers; per-server on/off is user-global via the `Enabled` flag.
- **Human-in-the-loop tool approval** for MCP tool calls (Agent42 auto-approves, as it does for skills).

## Step 0 spike results (CONFIRMED — ModelContextProtocol 1.4.1, live against a public MCP server)

Run via `/spike-dotnet` (three throwaway file-apps, live connect to `https://mcp.deepwiki.com/mcp`).
Package: **`ModelContextProtocol` 1.4.1** (pulls `Microsoft.Extensions.AI.Abstractions >= 10.5.2`).
No separate `.Client` package — client types are in the `ModelContextProtocol.Client` **namespace**.

Confirmed shapes (all differ in detail from the reference's recalled "v1.2.0"):
- **Type chain:** `McpClientTool : AIFunction : AIFunctionDeclaration : AITool`. It is BOTH an `AITool`
  and an `AIFunction`; `serverTools.Cast<AITool>()` works, `.ToList()` merges into `List<AITool>`.
- **Transport:** `new HttpClientTransport(HttpClientTransportOptions, ILoggerFactory)` OR
  `(options, HttpClient httpClient, ILoggerFactory, bool ownsHttpClient)`. `HttpClientTransportOptions`
  props: `Endpoint`, `TransportMode`, `Name`, `ConnectionTimeout`, **`AdditionalHeaders`**,
  `KnownSessionId`, `OwnsSession`, `OAuth`, `MaxReconnectionAttempts`, `DefaultReconnectionInterval`.
  Streamable HTTP is the default `TransportMode` for a remote endpoint.
- **Static header auth (Header mode) needs NO `DelegatingHandler`:** set
  `AdditionalHeaders = new() { [headerName] = headerValue }` on the options; the transport builds its own
  `HttpClient`. The reference's `AuthHeaderHandler`/custom-`HttpClient` path is only needed for **dynamic**
  per-request tokens (Phase 2 ClientCredentials refresh).
- **Client create:** `await McpClient.CreateAsync(IClientTransport, McpClientOptions? = null,
  ILoggerFactory? = null, CancellationToken = default)`. Then `await client.ListToolsAsync(ct)` →
  `IList<McpClientTool>`.
- **Tool-call result (for truncation):** `McpClientTool.InvokeAsync(...)` returns a
  `System.Text.Json.JsonElement` (the serialized `CallToolResult`, a `{ "content": [ { "type":"text",
  "text":… } ] }` shape). `.ToString()` yields compact JSON. `tool.JsonSerializerOptions` is non-null.
- **Truncation wrapper (CONFIRMED cleanest):** subclass `DelegatingAIFunction(inner)` and override ONLY
  `InvokeCoreAsync` — `Name`/`Description`/`JsonSchema`/`ReturnJsonSchema`/`AdditionalProperties` are
  forwarded automatically. Call `base.InvokeCoreAsync(args, ct)`, stringify the `JsonElement`
  (`raw?.ToString()`), and return the raw element when under budget or a truncated string when over. This
  preserves the model-facing contract while bounding the persisted return value (ADR 0004).

**HttpClient-shape decision (resolves Step 4's required choice):** Phase 1 uses the transport's built-in
`HttpClient` via the `(options, ILoggerFactory)` ctor with static `AdditionalHeaders` — **no DI-resolved
named/typed `HttpClient`**, so `AddServiceDefaults`' resilience pipeline is never inherited (the gotcha is
avoided by construction, not by `RemoveAllResilienceHandlers()`). Best-effort timeout is enforced with a
linked `CancellationTokenSource` around connect + `ListToolsAsync` (belt-and-suspenders with
`ConnectionTimeout`). Phase 2 ClientCredentials will use a **plain `new HttpClient`** wrapping the
`AuthHeaderHandler` (like `OpenAIImageGenerator`'s static client) — again not DI-resolved, so still no
resilience inheritance.

## Review

`critic` reviewed this plan (fresh context, against ADRs 0002/0004/0013/0018/0021 + gotchas + code).
Verdict: **core fit to the ADRs is sound**; seven findings, all accepted and folded in:

- **HIGH — missing test-silo registrations + no offline adapter.** Fixed: Affected areas now lists all
  five test configurator sites + `Program.cs`/DevHost by file:line, and `SingleUserMcpToolProvider` is
  a named new file. (Regression against the ADR 0018 plan's rigor — restored.)
- **HIGH — `IProviderSecretProtector` purpose is a private const, not a parameter.** Fixed: a new
  `IMcpSecretProtector`/`McpSecretProtector` (own purpose, registration, test) replaces the infeasible
  "distinct purpose string" instruction.
- **MEDIUM — `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate` guard conflict.** Fixed: Step 5 now
  edits that guard to carve out MCP tools (identified by the provider's set, not by name), and the ADR
  records MCP as the documented group-gated exception.
- **MEDIUM — named-HttpClient retry gotcha applies to Phase 1.** Fixed: moved into Step 4 with a
  required decision on the client shape.
- **MEDIUM — AC gap for the off-by-default semi-breaking claim.** Fixed: AC4b added.
- **LOW — singleton connection-lifetime leak.** Fixed: Step 4 now requires a bounded retain-N policy
  stated explicitly, not deferred to Step 0.
- **LOW — BuildAgent "mirror skillsEnabled" points at the wrong shape.** Fixed: the precise insertion
  window (after `AgentGrain.cs:262`, before `:283`) is spelled out, with the context-provider contrast.

No findings rejected. The critic also affirmed (not a finding) that the plan's viability genuinely
hinges on the blocking Step 0 spike, and Phases 2–3 rest on SDK shapes not yet confirmed — reflected
in the staging.
