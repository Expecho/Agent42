# Extract Google Calendar into a standalone MCP server, and remove it from Agent42

planned-against: main @ e392958

## Overview

Move Agent42's six native Google Calendar tools (ADR 0053) out of this repo and into a new
`Calendar.Mcp` server in the sibling repo `Expecho/mcpserver` (cloned at `Z:\Sources\mcpserver`),
then delete the native integration here. Agent42 reaches the calendar afterwards only through the
ADR 0024 user-configured remote MCP server path.

Why now: the calendar logic is useful outside Agent42, which is the one motivation ADR 0053 never
weighed — it rejected the MCP shape because it "would have added a protocol, a transport and a
discovery step to reach code that already lives in this process". That premise is what changes: the
code is no longer meant to live in this process.

**Two things the executor must not discover late:**

1. **This plan re-adopts an alternative ADR 0053 rejected twice and marked "neither is to be
   relitigated."** That is an explicit user decision from the planning session, not an oversight.
   Step B1 records the reversal and its new reason.
2. **The chosen OAuth design (D) serves one pre-registered redirect URI per MCP client** — see
   "The reuse motivation is only partly delivered" below. **Decided by the user at plan approval:
   ship Design D now and revisit Design B only if the one-console-edit-per-client limit actually
   chafes in use.** That is an explicit commitment to revisit, not a judgement that the limit is
   permanent — ADR 0063 must record it that way.

## Affected areas

### Repo A — `Z:\Sources\mcpserver` (new code; separate git repo, no `.agentic/` process)

- `src/Mcp.Hosting/McpHost.cs`, `ApiKey.cs` — a generic auth mode **and** a build/run seam (M3). Scraper behaviour must stay byte-identical.
- `src/Calendar.Core/` — NEW. Google Calendar REST v3 client + result model.
- `src/Calendar.Mcp/` — NEW. Six MCP tools, the OAuth endpoints, `Program.cs`, `appsettings.json`.
- `tests/Calendar.Mcp.Tests/` — NEW. The repo's first test project.
- `McpServer.slnx`, `Dockerfile` (**incl. `HEALTHCHECK` + `ENTRYPOINT`**, M4), `docker-compose.yml`, `.env.example`, `README.md`, `.github/workflows/ci.yml`, `tools/mcp_probe.py`.

### Repo B — `Z:\Sources\Agent42` (deletion)

Verified against the tree at `e392958`. **The bracketed items were missed in the first draft and
added after review — they are the ones most likely to be missed again.**

Code:
- `src/Agent42.Agents/Calendar/` — 6 files, 781 lines (Agents 21 to 20 folders).
- `src/Agent42.Backend/Calendar/` — 6 files, 953 lines (Backend 24 to 23 folders).
- `src/Agent42.Agents/Conversations/AgentGrain.cs:70,183,198`.
- `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs:104-154` — six `AIFunctionFactory.Create` registrations.
- `src/Agent42.Agents/Tools/ToolCatalog.cs:61` (`Calendar` const), its `Descriptors` row, `:172-177` (six `ToolNameToKey` rows).
- `src/Agent42.Backend/Api/AgentApi.cs:40` — `MapCalendarEndpoints(app);` (endpoint groups 17 to 16).
- `src/Agent42.Backend/Program.cs:354,361,367,369-376`; `tools/Agent42.DevHost/Program.cs:148,153,154`.
- `src/Agent42.Backend/Configuration/ConfigHealth.cs` — the GoogleCalendar static checks.
- **[`src/Agent42.AppHost/AppHost.cs:57-60`]** — Aspire forwards `GoogleCalendar__ClientId`/`ClientSecret`.
- **[`tools/Agent42.DevHost/CannedChatClient.cs:92-227`]** — canned calendar tool-call triggers. Compiles fine after deletion, then emits calls to tools that no longer exist.
- **[CS1574 `see cref` breaks]** `src/Agent42.Agents/Memory/HindsightIntegration.cs:11` (`ICalendarService`), `src/Agent42.Agents/Search/ChatSearchLimits.cs:5` (`CalendarToolLimits`).

Frontend:
- `wwwroot/js/calendar.js` (118 lines); `wwwroot/js/dom.js:91,102-105`; `wwwroot/js/main.js:18,72,111`;
  **[`wwwroot/js/profile.js:9,62`]** — `import { loadGoogleStatus } from "./calendar.js";` and its call.
  A dangling ES specifier is not a compile error; it is a runtime `SyntaxError` and a blank app (ADR 0041).
- `wwwroot/index.html:314,319-320,465-474`; `wwwroot/styles.css` — **the calendar rules MOVED**: ADR 0064
  inserted `.agent-form__note--ok` at what was `:1644`, so the calendar block is now roughly `:1651-1659`.
  **Locate it by content (`.calendar__status`), never by that line number.**
  `wwwroot/sw.js:5` (CACHE — **ADR 0064 already took it to `agent42-shell-v28`, so this plan must bump
  `-v28` to `-v29`, NOT v27 to v28**, which is now a no-op that would ship a stale shell) and `:7` (drop
  `/js/calendar.js` from SHELL).

Tests deleted (10): `CalendarToolTests.cs`, `CalendarAssetsTests.cs`, `FakeCalendarService.cs`,
`GoogleCalendarServiceTests.cs`, `GoogleOAuthClientTests.cs`, `GoogleOAuthEndpointTests.cs`,
`GoogleOAuthPendingGrainTests.cs`, `GoogleSecretProtectorTests.cs`, `UserGoogleGrainTests.cs`,
`GoogleOAuthApiFixture.cs`. **`GoogleGeocoderTests.cs` STAYS** — ADR 0012 geocoding, not calendar.

Tests edited — **eleven sites, not five**:
- `ProjectLayoutTests.cs:97,105,143,177,241-245` (+ the stale doc comments at **`:130`** "the thirteen parts" and **`:159`** "the seventeen endpoint groups").
- `ToolCatalogTests.cs:32-56,63-79`; `ConfigHealthTests.cs:40-41,60-61,69-91` **and `:92`** (`c.Key.Contains("Calendar")`); `EasyAuthExclusionTests.cs:85-92`.
- `TestServiceRegistration.cs:61`; `CapSizedWebPageFetcher.cs:90`.
- **[`GrainStorageSerializerTests.cs:48-49`]** — `"Agent42.Agents.UserGoogleOAuthPendingState"`, `"Agent42.Agents.UserGoogleState"` in the hard-coded `$type` list.
- **[`SerializationCompatibilityTests.cs:82,85,254-275`]** — two golden blobs + two round-trip tests.
- **[`GoldenStates.cs:106-118`]** — `UserGoogle()` / `UserGoogleOAuthPending()` factories.
- **[`ToolSelectionApiTests.cs:60`]** — literal `"calendar"` in an `Assert.Equal` over `/api/tools` keys. **This test fails without the edit.**
- **[`FakeChatClient.cs:323-481`]** — the canned trigger table's test-side twin.
- **[`AgentGrainTests.cs:2196`]** — stale literal in a legacy-snapshot list.

Config/infra:
- `GoogleCalendar` section in **`src/Agent42.Backend/appsettings.json` only** — DevHost has no `appsettings.json` at all.
- `infra/main.bicep:51-56,206-207`; `infra/modules/container-app.bicep` — 2 params, 2 env conditionals **and the two secret blocks at `:204-217`**; `.github/workflows/deploy.yml:99-100`.
- `infra/modules/container-app.bicep` `excludedPaths` is **unchanged** — no calendar route was ever excluded.

Docs:
- `docs/integrations/google-calendar/README.md`; **[`docs/integrations/README.md`]** (its index table's only row links to the folder being deleted); **[`docs/features/tool-selection.md:10,57,61`]**; **[`README.md:58-62,215-216,226-227`]**; the CLAUDE.md ADR 0053 bullet; `.agentic/gotchas.md` (see B6 — edits and one deletion).

## Context and constraints

### The OAuth design is forced by two measured facts

**Design A (PRM points straight at `accounts.google.com`) is impossible.** Both constraints verified:

1. Agent42 is a strict **public** client: `src/Agent42.Backend/Mcp/McpOAuth.cs:162-190` —
   `ExchangeCodeAsync` and `RefreshAsync` send `client_id` + `code_verifier` and **never**
   `client_secret`. Google requires `client_secret` at the token endpoint for **every** client type,
   even with PKCE. This repo already recorded that finding independently — `infra/main.bicep:51,55`:
   *"the Google Web application client type is CONFIDENTIAL, so the token exchange needs BOTH...
   PKCE alone is not accepted on the token exchange."* A direct exchange fails `invalid_client`.
2. Agent42's authorize URL (`src/Agent42.Backend/Mcp/AgentApi.Mcp.cs:207-216`) sends `response_type`,
   `client_id`, `redirect_uri`, `state`, `code_challenge`, `code_challenge_method`, `resource` and
   `scope` — and **no `access_type=offline`, no `prompt=consent`**. Both are mandatory for Google to
   return a refresh token. Without them the connection dies at the first access-token expiry —
   **silently, an hour later, invisible in testing and obvious only in production**.

**Chosen: Design D — a stateless Google-federating OAuth front.**

- `GET /.well-known/oauth-authorization-server` — RFC 8414 metadata. `issuer` = this server;
  `authorization_endpoint` = **this server's** `/oauth/authorize`; `token_endpoint` = **this
  server's** `/oauth/token`; `code_challenge_methods_supported: ["S256"]`;
  `grant_types_supported: ["authorization_code","refresh_token"]`; **and `scopes_supported` — see
  the scope trap below, this field is load-bearing.** No `registration_endpoint` (Google has no DCR).
- `GET /oauth/authorize` — 302 to Google's real authorize endpoint, passing the client's parameters
  through **and adding `access_type=offline` + `prompt=consent`**. Exists solely to repair fact (2).
- `POST /oauth/token` — proxies to Google's token endpoint, **adding `client_secret`** (repairing
  fact (1)) and **stripping `resource`** (RFC 8707, undefined at Google). Google's response is
  returned verbatim, so the client stores Google's own access + refresh tokens. Note Agent42 sends
  `resource` on the **refresh** as well as the exchange (`McpOAuth.cs:180-190`) — strip both.
- `/mcp` bearer = a Google access token, validated via Google `tokeninfo`, which **must check `aud`
  equals this server's own Google client id** — otherwise any Google token carrying calendar scope
  is accepted, including one minted for an unrelated application (token substitution).
  **Cache the validation** keyed on token hash + expiry: `tokeninfo` is debug-oriented and
  rate-limited, and an uncached check adds a Google round trip to every tool call on the latency path.

The server never persists a user token, but it does hand Google tokens to the client and it holds the
Google **client secret**.

### The reuse motivation is only partly delivered — decided, with a commitment to revisit

Design D's `/oauth/authorize` is a pass-through, so **Google receives the client's own
`redirect_uri`** and rejects any value not pre-registered on the server's single Google OAuth client.
Google supports no DCR. Therefore **every additional MCP client needs a manual Google Cloud console
edit by the server operator**. Design D's statelessness is bought precisely by that pass-through.

That directly qualifies the "usable from Claude Code and any other MCP client" premise in the
Overview: it is true only for clients whose redirect URI has been registered by hand. **Design B**
(the server as a full authorization server issuing its own tokens, with DCR, federating to Google)
delivers unrestricted reuse and needs per-subject token storage — `Z:\Sources\mcpserver` has no
database, cache or state of any kind today (verified).

**Decision (user, at plan approval): Design D now; move to Design B only if the manual-registration
limit proves annoying in practice.** ADR 0063 records both the limit and that commitment, so a later
session finds a documented upgrade path rather than re-deriving this analysis. Do not silently start
building Design B; if the limit chafes, that is a new plan.

### The scope trap — this 400s at Google if missed

`AgentApi.Mcp.cs:202` — `var scopes = server.Scopes is { Count: > 0 } ? server.Scopes :
config.ScopesSupported;` and `:219-222` append `scope` **only when non-empty**.
`config.ScopesSupported` comes from AS-metadata `scopes_supported`, falling back to PRM
`scopes_supported` (`McpOAuth.cs:101-104`). So if neither document declares scopes and the user left
the server record's Scopes blank, Agent42 sends **no `scope`** and Google 400s. Declare
`scopes_supported: ["https://www.googleapis.com/auth/calendar.events",
"https://www.googleapis.com/auth/calendar.calendarlist.readonly"]` in **both** documents (AC5a).

### SDK facts (measured, `ModelContextProtocol.AspNetCore` 2.2.0 from `Z:\Packages\.nuget\packages`)

- The package **ships the resource-server half**: `AuthenticationBuilder.AddMcp(...)`,
  `McpAuthenticationHandler`, `McpAuthenticationOptions.ResourceMetadata` / `.ResourceMetadataUri`,
  `McpAuthenticationEvents.OnResourceMetadataRequest`, `AddAuthorizationFilters()`. PRM is
  configuration; do not hand-roll it.
- **Interop trap — this silently 404s discovery.** `ResourceMetadataUri` defaults to
  `/.well-known/oauth-protected-resource/<resource-path>` (so `/mcp` yields
  `/.well-known/oauth-protected-resource/mcp`). Agent42 fetches the **root** path — `McpOAuth.cs:88`,
  `new Uri(origin, "/.well-known/oauth-protected-resource")` — with no `WWW-Authenticate`
  `resource_metadata` fallback. Set `ResourceMetadataUri` explicitly to the root, or serve both.
- **Consequence for the deferred hosting decision:** PRM at the origin root, plus RFC 8414
  path-insertion (`McpOAuth.cs:136`), means Calendar.Mcp **cannot be hosted at a subpath** (e.g.
  behind Agent42's ingress at `/calendar/`). Hosting is out of scope here, but this design has
  already narrowed it to an origin root — record that in the ADR so the hosting MR is not surprised.

### Repo A conventions (follow these; do not import Agent42's)

File-scoped namespaces; dense XML doc comments in a direct technical voice; `static` classes for pure
logic; `sealed record` for data; primary constructors; **exceptions**, not result types, for failure;
limit constants as `private const` at the top of the tools class; `Text.Truncate(value, max, note:
true)` for capping; every tool method `static`, `[McpServerToolType]` on the class,
`[McpServerTool(Name = "...")]` + `[Description]` on the method and **every** parameter,
`CancellationToken cancellationToken = default` last. `.editorconfig` sets
`insert_final_newline = false`. `McpHost.RunAsync`'s `configureServices` parameter is unused today —
Calendar.Mcp is its first consumer if tools need instance dependencies.

### What must be carried over, or is silently lost

`ICalendarService` (`src/Agent42.Agents/Calendar/ICalendarService.cs`) is the behavioural spec:

- **Curated constant messages.** A Google error body echoes the request URL, the calendar id (an
  email address) and event detail. Repo A's house style is exceptions — so the tools layer must catch
  and map, or the discipline is lost in translation.
- `singleEvents=true` + `orderBy=startTime`, so ids are **instance** ids and an edit touches one
  occurrence, never the series. Say so in the tool descriptions, as ADR 0053 does.
- `sendUpdates=none` pinned on **every** write — a pre-existing event may already carry attendees.
- The narrow scope pair, never the broad `.../auth/calendar` (the blast-radius bound that matters
  given there is no confirmation gate).
- No attendee **list**; a count only.
- `CalendarToolLimits` caps. The reason travels: Agent42 wraps MCP tool results in truncation (ADR
  0024), but any other MCP client has its own context budget.
- **`CalendarSpanSuppressingHandler` is deleted, and the rule it served must not be.** The gotcha
  "OTel redacts an HttpClient span's query string by default" notes OTel **never redacts the PATH**,
  which is why the calendar client suppressed its span — Calendar REST v3 puts the calendar id (an
  email address, somebody *else's* for a shared calendar) in the path. Edit that entry to record the
  client is gone while keeping the general rule and the ADR 0056 Hindsight exception intact. Repo A
  emits no OpenTelemetry today, so the concern does not transfer — say that rather than implying it
  was solved.

### Agent42 removal constraints

- **Grain state orphans.** `google-credentials` (`IUserGoogleGrain.cs:74`) and `google-oauth-pending`
  (`UserGoogleOAuthPendingGrain.cs:62`) ride the shared `agent-state` Cosmos container — no container
  or provider change needed. Deleting the code leaves those documents in place, unread, **still
  holding DP-encrypted Google refresh tokens**; the DP key ring persists in blob storage, so the
  ciphertext stays decryptable in principle. Cleanup is a manual operator step (B7), not code. This
  is also why removing the two entries from `GrainStorageSerializerTests`' `$type` list must be a
  deliberate edit, not an incidental one — those documents still exist in prod.
- **The `calendar` selection key.** Removing it from `AllSelectableToolKeys`/`Descriptors` means
  `Sanitize` drops a stored `"calendar"` from existing selections. Harmless, no migration, one-way.
- **Do NOT try to re-point `ToolCatalogTests.LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes`
  at `memory`.** `memory` has no `ToolNameToKey` entry by design, and `IsToolEnabled` returns **true**
  for any unmapped name (`ToolCatalog.cs:279-280` — an unmapped tool "must not be silently
  stripped"), so the assertion would invert. The rule is already covered at the level it can be, by
  `AgentGrainTests.LegacyAgentSnapshot_DoesNotEnableMemory` (`AgentGrainTests.cs:2188`), whose comment
  says it lives at grain level for exactly this reason. Delete the calendar test.
- Tab ordering is safe: Calendar is the **last** tab (index 6; `index.html:314`). Removing the last
  one renumbers nothing — ADR 0025's `profileTabs.select(3)` and the ADR 0029/0049 locks at 4 and 5
  are untouched (`calendar.js:102` is the only `select(6)`).
- `sw.js` **must** bump `CACHE` because `wwwroot/js/` changes; a stale SHELL entry for a deleted
  module kills the whole service worker (`ProjectLayoutTests:311,336`).
- ADR 0058 cold start: after adding the server in Preferences, **per-tool checkboxes do not appear
  until that user's next chat activation**. The master checkbox works immediately.
- Calendar tools were live on `ReceiveReminder` and `/api/agent-run`; an existing reminder whose
  prompt assumes calendar access degrades to a model with no such tool.
- **`.claude/hooks/protect-main.mjs:10,29` pins `ROOT` to `CLAUDE_PROJECT_DIR`** and runs
  `git branch --show-current` there — in `Z:\Sources\mcpserver` it reports **Agent42's** branch. Repo
  A therefore has **no enforced branch protection**; branch discipline there is manual.

### Landing order — READ THIS BEFORE MERGING ANYTHING

Repo A ships first and must be **deployed somewhere Agent42's backend can dial** before Repo B
merges. Hosting was deliberately deferred, so this plan cannot name that host. Until it is chosen and
running, merging Repo B removes calendar from chat.agent42.nl with nothing to replace it. Local
verification (stdio + `http://127.0.0.1:5099`) proves Repo A — Google permits `http://localhost`
redirect URIs — but does not license retiring Repo B.

## Steps

### Repo A — `Z:\Sources\mcpserver` (branch `feature/calendar-mcp`)

**A1. `src/Calendar.Core/` — the Google Calendar client.** Port `GoogleCalendarService.cs` (407
lines) and the data records from `ICalendarService.cs`. Drop the `userId`-first port shape: the
credential arrives per call as a Google access token. Keep the result model, the six operations,
`singleEvents=true`, `sendUpdates=none`, the attendee **count**, and the caps.
*Verify:* `dotnet build McpServer.slnx`.

**A2. `src/Mcp.Hosting/` — a generic auth mode and a test seam.** Add an `McpAuth` abstraction:
`ApiKey` (today's behaviour, the default) and `Bearer` (a validation delegate + a
`ProtectedResourceMetadata`). In Bearer mode wire `AddAuthentication().AddMcp(...)` with
`ResourceMetadataUri` at the **root** well-known path, plus `AddAuthorizationFilters()`, and exempt
`/health`, `/.well-known/*` and `/oauth/*` from the key middleware. **Also extract a `BuildAsync`
returning the un-run `WebApplication`** — `RunHttpAsync` currently builds *and* `await
app.RunAsync()`s internally (`McpHost.cs:96-155`), leaving no seam, and AC4/AC5 cannot be written
without one.
*Verify:* `dotnet build`; `python tools/mcp_probe.py` and the ci.yml 401/200 curl pair still pass
against **Scraper.Mcp** — its behaviour must be byte-identical.

**A3. `src/Calendar.Mcp/` — OAuth endpoints.** `/.well-known/oauth-authorization-server` (**including
`scopes_supported`**), `/oauth/authorize` (302 to Google adding `access_type=offline` +
`prompt=consent`), `/oauth/token` (proxy adding `client_secret`, stripping `resource` on **both**
exchange and refresh). PRM must also declare `scopes_supported`. Config: `GoogleCalendar:ClientId` /
`ClientSecret` from env, refusing to start in HTTP mode without them — the `MCP_API_KEY` shape, since
here they *are* the availability path.
*Verify:* curl each endpoint; assert the AS metadata's three URLs and its scopes, and that
`/oauth/authorize` 302s to `accounts.google.com` with both extra params present.

**A4. `src/Calendar.Mcp/CalendarTools.cs` + `Program.cs` — the six tools.** Names identical to
today's. Bearer validation via `tokeninfo` asserting `aud` == our client id and calendar scope
present, **with a cache keyed on token hash + expiry**. Map provider failures to curated constants.
*Verify:* `mcp_probe.py` lists six tools; a call with no token 401s carrying the PRM hint; a token
whose `aud` is another client is rejected.

**A5. `tests/Calendar.Mcp.Tests/` — the repo's first test project.** `dotnet test` does nothing
today. Cover the pure logic that is cheap to get wrong and expensive to debug in production: the
token proxy's `client_secret`-add and `resource`-strip (both grants), the authorize hop's two added
params, the AS metadata + PRM shape including scopes, the `aud` check, the curated-message mapping,
and the truncation caps — all against a fake Google, never the live one. Extend `ci.yml` with a
`dotnet test` step and a Calendar.Mcp 401/200 pair mirroring the scraper's.
*Verify:* `dotnet test McpServer.slnx` is green **and actually runs tests** (it currently runs none).

**A6. Docs and packaging.** `McpServer.slnx` gains the projects; `Dockerfile` `COPY` lines **plus
parameterised `HEALTHCHECK` and `ENTRYPOINT`** — both are hardcoded to `Scraper.Mcp` today, so
`--build-arg SERVER=src/Calendar.Mcp` currently produces an image that starts the scraper; a
`calendar-mcp` service in `docker-compose.yml`; `.env.example` gains the two Google variables;
`README.md` gains a Calendar section in the existing shape — tool table, stdio + HTTP usage, and the
manual Google Cloud prerequisites, **which must list every redirect URI to register, including
`https://chat.agent42.nl/api/mcp/oauth/callback`** (`AgentApi.Mcp.cs:179` builds it from
scheme + host).
*Verify:* `docker build --build-arg SERVER=src/Calendar.Mcp .` produces an image that answers
`/health` **and serves the Calendar tools, not the scraper's**.

**A7. Real-Google verification (manual, cannot be automated).** With a real Google client and both
redirect URIs registered, run the full flow and confirm a **refresh token is returned** and that a
call still works **after the access token expires** (the fact-2 trap only shows up an hour in).
Record the observed values. Note A7 with `http://localhost` alone cannot detect a missing
production redirect URI — that failure appears only at chat.agent42.nl.

### Repo B — `Z:\Sources\Agent42` (branch `fix/remove-native-calendar`, AFTER Repo A is deployed)

**B1. ADR 0063**, `supersedes: [0053]`, following the 0062 front-matter shape. **And edit 0053 in the
same commit: `superseded_by: 0063` and `status: superseded`** — `.agentic/decisions/README.md:10-11`
mandates both, and `grep -rL "^superseded_by:"` is the active-ADR scan, so omitting it leaves a cold
session reading "Agent 42 has six native Google Calendar tools" as current. **This is the repo's
first total supersession — `status: superseded` has never been used here** (ADR 0007 carries only
`superseded_by`, annotated `# partial`; ADR 0019 deliberately omitted the tag *because* its
supersessions were partial — that precedent does not apply to a full deletion).
The ADR must: quote 0053's rejection of both MCP shapes and its "neither is to be relitigated" line;
state the new reason that changes the premise; and record the consequences — Google tokens and the
client secret now outside Agent42; the calendar reached through ADR 0024's inbound prompt-injection
channel; `CalendarSpanSuppressingHandler` gone (and why that is fine — Repo A emits no OTel); the
curated-message discipline re-established in Repo A; the Design D single-redirect-URI limit; the
origin-root hosting constraint; ADR 0058's cold start; the orphaned Cosmos state.
*Verify:* `node .agentic/scripts/check-doc-paths.mjs`.

**B2. Delete the two `Calendar/` folders and their wiring** — the code list above, **including
`AppHost.cs:57-60`, `CannedChatClient.cs:92-227` and the two CS1574 `see cref` breaks**.
*Verify:* `dotnet build Agent42.slnx` with no new warnings.

**B3. Frontend.** Delete `calendar.js`; the `dom.js:91,102-105` exports; `main.js:18,72,111`;
**`profile.js:9,62`**; `index.html:314,319-320,465-474`; `styles.css` — the calendar rules moved to roughly
`:1651-1659` when ADR 0064 inserted `.agent-form__note--ok` above them, so **find them by `.calendar__status`,
not by line number**. Drop `/js/calendar.js` from `sw.js` SHELL and bump `CACHE` **v28 to `-v29`** (ADR 0064
already spent v28 — bumping v27 to v28 is now a no-op that ships a stale shell).
*Verify:* `dotnet test --filter ProjectLayoutTests` (SHELL + import-graph reachability at :311,:336).

**B4. Tests.** Delete the 10 files (**not** `GoogleGeocoderTests.cs`); edit the eleven sites listed
above, including the three serialization files and `ToolSelectionApiTests.cs:60`. Delete the calendar
`ToolCatalogTests` case rather than re-pointing it (see constraints).
*Verify:* `dotnet test Agent42.slnx`.

**B5. Config, infra, docs.** The `GoogleCalendar` section from `src/Agent42.Backend/appsettings.json`
(the only one); `infra/main.bicep:51-56,206-207`; `container-app.bicep` params, env conditionals
**and the secret blocks at :204-217**; `.github/workflows/deploy.yml:99-100`; delete
`docs/integrations/google-calendar/` **and fix `docs/integrations/README.md`'s index table**; update
`README.md:58-62,215-216,226-227` and `docs/features/tool-selection.md:10,57,61`.
*Verify:* `dotnet test --filter EasyAuthExclusion`; Bicep lint/what-if; `check-doc-paths.mjs`.

**B6. Doc debt in the same MR** (hard rule 2).
(a) CLAUDE.md's ADR 0053 bullet becomes a short bullet pointing at 0063 and the external server.
(b) **CLAUDE.md count drift**: ADR 0039's "the 21 directories", ADR 0040's "the 24 directories" and
"17 per-feature partials", ADR 0056's "19th Agents feature folder", ADR 0062's "endpoint groups stay
17".
(c) `.agentic/gotchas.md`: **edit** the OTel path-redaction entry (`:486-487`), keeping the general
rule and the ADR 0056 exception; and **delete** the entry at `:2042` *"A Google Calendar connection
dies SILENTLY in three ways"* together with its paths at `:2056,2068-2071` — its subject ceases to
exist.
(d) **An unresolved rule collision the executor must settle, not paper over.**
`.agentic/scripts/check-doc-paths.mjs:11` scans **every** `.agentic/decisions/*.md`, and after this
change two ADRs cite deleted paths: `0053:48,123` (itself, now superseded) and **`0039:99`**
(`src/Agent42.Agents/Calendar/AgentGrain.CalendarTools.cs`, in an ADR that stays **active**). But
`.agentic/decisions/README.md:5` says *"Never edit an accepted ADR's decision content; supersede
it."* The honest options are (i) teach `check-doc-paths.mjs` to skip ADRs carrying `superseded_by:`
— which fixes 0053 but **not** 0039 — or (ii) accept a narrow factual edit to 0039's partial list and
record why in ADR 0063. Pick one explicitly; do not let CI failure drive the choice.
*Verify:* `check-doc-paths.mjs` green; grep `0053` and `Calendar` across `.agentic/`, `docs/`, CLAUDE.md, README.md.

**B7. Manual operator steps (hand to the user; the agent does not run these).**
(a) **Register `https://chat.agent42.nl/api/mcp/oauth/callback` as an authorized redirect URI on the
server's Google OAuth client** — without it production fails `redirect_uri_mismatch` after the native
path is already deleted.
(b) Add the server in Preferences → MCP servers, InteractiveOAuth, with the server's Google client id
pre-set **and its Scopes filled in** (belt and braces against the scope trap), then open a chat once
so ADR 0058 discovers per-tool names.
(c) Revoke the old Agent42 grant at <https://myaccount.google.com/permissions> — deleting code does
not revoke it.
(d) Delete the orphaned `google-credentials` / `google-oauth-pending` documents from `agent-state`.
(e) Remove the `GOOGLE_OAUTH_CLIENT_ID` / `GOOGLE_OAUTH_CLIENT_SECRET` GitHub Actions secrets.

## Acceptance criteria to test mapping

| # | Acceptance criterion | Test / proof | Lives in |
|---|---|---|---|
| AC1 | Six calendar tools exposed over MCP with the same names | `mcp_probe.py` tools/list | A: `tools/mcp_probe.py` |
| AC2 | `/oauth/token` adds `client_secret`, strips `resource` on exchange **and** refresh | unit test vs fake Google | A: `tests/Calendar.Mcp.Tests/TokenProxyTests.cs` |
| AC3 | `/oauth/authorize` adds `access_type=offline` **and** `prompt=consent` | assert both params on the 302 | A: `.../AuthorizeHopTests.cs` |
| AC4 | PRM served at the **root** well-known path | integration GET returns 200 (needs A2's seam) | A: `.../DiscoveryTests.cs` |
| AC5 | AS metadata names this server's authorize + token endpoints | assert the three URLs | A: `.../DiscoveryTests.cs` |
| AC5a | **Both** documents declare the two calendar scopes | assert `scopes_supported` | A: `.../DiscoveryTests.cs` |
| AC6 | A bearer minted for another `aud` is rejected | unit test on the validator | A: `.../BearerValidationTests.cs` |
| AC7 | Provider error bodies never reach the tool result | fake Google 4xx with a body yields a curated constant | A: `.../CuratedMessageTests.cs` |
| AC8 | Writes pin `sendUpdates=none`; listing uses `singleEvents=true` | request-recording test | A: `.../CalendarRequestTests.cs` |
| AC9 | `/mcp` still 401s without and 200s with a key for **Scraper** | existing curl pair, unchanged | A: `.github/workflows/ci.yml` |
| AC10 | The image built with `SERVER=src/Calendar.Mcp` runs Calendar, not Scraper | ci docker job asserts a calendar tool in tools/list | A: `.github/workflows/ci.yml` |
| AC11 | A refresh token is issued and survives access-token expiry | **manual, A7** — cannot be automated | A: recorded in the PR |
| AC12 | Agents/Backend folder sets no longer contain `Calendar` | `ExpectedFolders` theory | B: `ProjectLayoutTests.cs:97,105` |
| AC13 | No orphaned `AgentApi` partial / grain part | `AgentApiParts`, `AgentGrainParts` | B: `ProjectLayoutTests.cs:143,177` |
| AC14 | `sw.js` SHELL matches the module set and every import resolves | SHELL + reachability | B: `ProjectLayoutTests.cs:311,336` |
| AC15 | **The tools are actually gone, not just the folders** — `KeyFor("list_calendars")` is null | new assertion | B: `ToolCatalogTests.cs` |
| AC16 | `GET /api/tools` exposes no `calendar` key | edited assertion | B: `ToolSelectionApiTests.cs:60` |
| AC17 | The "stored selection is an explicit list" rule still covered | existing grain-level test, unchanged | B: `AgentGrainTests.cs:2188` |
| AC18 | Persisted state types no longer referenced by the `$type` guard | edited list | B: `GrainStorageSerializerTests.cs:48-49` |
| AC19 | `excludedPaths` still matches the Bicep, minus calendar rows | exclusion theory | B: `EasyAuthExclusionTests.cs` |
| AC20 | ConfigHealth reports no GoogleCalendar keys | edited assertions | B: `ConfigHealthTests.cs` |
| AC21 | No doc cites a deleted path | `check-doc-paths.mjs` (CI hard block) | B: `.agentic/scripts/` |
| AC22 | Whole suite green after deletion | `dotnet test Agent42.slnx` | B |

AC15/AC16 exist because a branch that deletes the folders but leaves the six `ToolNameToKey` rows or
the `Calendar` descriptor would otherwise pass every structural criterion.

## Quality gate checklist

Copied verbatim from `.agentic/quality-gates.yaml` at plan time (never from memory):

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura intersected with `git diff`) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` S3776 at build time, severity `error` — a method over it fails `dotnet build` |
| `vulnerable_packages` | **0** | CI package-freshness job (hard block) |
| `outdated_packages` | report | CI (soft) |
| `add_policy` | latest-stable | resolved at add time, never from memory |
| enforcement | local `soft_warning`, ci `hard_block` | `.github/workflows/pr-checks.yml` |
| coverage tooling | coverlet | `dotnet test Agent42.slnx --collect:"XPlat Code Coverage"` |

Notes: these gates are **Agent42's**; Repo A has none today and must not silently inherit them — A5
establishes what Repo A measures. On Repo B, new-code coverage is near-vacuous (a deletion MR adds
few lines), which is expected, not something to engineer around. `branches.protected: [main]` — both
MRs go on a branch; note the hook cannot see Repo A, so its branch discipline is manual.

## Out of scope

- **Hosting.** Sidecar vs its own Container App is a separate MR — but note this design requires an
  **origin root**, not a subpath.
- **Design B** (full authorization server, DCR, per-subject token storage) — the upgrade path for
  unrestricted multi-client reuse. Explicitly deferred rather than dismissed: revisit if the
  one-console-edit-per-client limit chafes.
- A confirmation gate on calendar writes — ADR 0053 rejected it explicitly; unchanged here.
- Widening the OAuth scope beyond the `calendar.events` + `calendar.calendarlist.readonly` pair.
- Attendee read/write beyond the existing count.
- Migrating existing `google-credentials` state — manual revoke + re-consent (B7).
- Bumping Repo A's pinned upstream package versions.

## Review

A `critic` subagent reviewed the first draft: **six blockers, seven majors, several minors.**
Every finding I spot-checked was accurate, including three where the draft was wrong. **All findings
were accepted; none were rejected.** Notable fixes:

- **`profile.js:9,62` imports `calendar.js`** — a frontend importer the draft missed entirely.
  Deleting the module without it is a runtime `SyntaxError` and a blank app.
- **Three serialization test files** (`GrainStorageSerializerTests`, `SerializationCompatibilityTests`,
  `GoldenStates`) reference the deleted grain-state types; the test project would not compile.
- **Supersession mechanics were wrong.** The draft cited ADR 0019's leave-it-`accepted` pattern; that
  was for *partial* supersession. `decisions/README.md:10-11` mandates `superseded_by:` **and**
  `status: superseded`, which this repo has never yet used.
- **"Re-point the ToolCatalog test at `memory`" was unimplementable** — `IsToolEnabled` returns true
  for unmapped names, so the assertion inverts. The rule is already covered at grain level.
- **Design D serves one pre-registered redirect URI per client**, qualifying the reuse motivation
  that justifies the whole change. Now stated in the Overview as a decision to confirm.
- **The scope trap** (`scopes_supported` absent → Agent42 sends no `scope` → Google 400s) and the
  **missing production redirect URI** (fails only at chat.agent42.nl, after deletion) were both
  unhandled paths to a production-only failure.
- **A rule collision** between `check-doc-paths.mjs` scanning all ADRs and the "never edit an accepted
  ADR" rule — surfaced as an explicit choice in B6(d) rather than left to CI.
- Eight further Repo B files (AppHost, CannedChatClient, ToolSelectionApiTests, FakeChatClient,
  AgentGrainTests, README.md, docs/features/tool-selection.md, docs/integrations/README.md).

Sections the critic found genuinely clean: the quality-gate table (copied, not recalled), the tab-index
claim, `excludedPaths` unchanged, 0063 as the next free number, the `null == all` / wildcard machinery
surviving untouched, the `sw.js` handling, and keeping `GoogleGeocoderTests.cs`.

## Execution — what was built, and where it diverged from the plan

Executed 2026-09-03 against `main @ e392958` (no drift; HEAD matched `planned-against` exactly).

**Scope actually delivered: Repo A only (A1–A6).** A7 and all of Repo B (B1–B7) are NOT done, and
that is the plan's own instruction, not a shortfall — see "Landing order": Repo A must be deployed
somewhere Agent42's backend can dial before Repo B merges, and hosting is explicitly out of scope
here. Merging Repo B now would remove calendar from chat.agent42.nl with nothing to replace it.

Repo A branch: `feature/calendar-mcp` in `Z:\Sources\mcpserver`.

### Divergences from the plan

1. **`client_id` is FORCED, not passed through**, on both `/oauth/authorize` and `/oauth/token`
   (`GoogleOAuthFront.cs`). The plan said pass the client's parameters through. Exactly one Google
   client sits behind this front, so any other `client_id` can only produce a confusing
   `invalid_client` or a Google error page that does not say which of the two values was wrong.
   Forcing ours makes B7(b)'s "with the server's Google client id pre-set" belt-and-braces rather
   than load-bearing. `TokenProxy_ForwardsThisServersOwnClientId` and
   `Authorize_SendsThisServersOwnClientId` pin it.

2. **`--health-probe` was implemented for real** (`src/Mcp.Hosting/HealthProbe.cs`), which the plan
   did not ask for. It had to be: the Dockerfile's `HEALTHCHECK` ran `/app/Scraper.Mcp
   --health-probe`, a flag `McpHost` never handled, so it fell through to the command-line
   configuration provider and started a **stdio server that never exits** — the check timed out and
   the container was reported unhealthy forever. A6 required parameterising that HEALTHCHECK, which
   would have propagated the bug to Calendar.Mcp; and an orchestrator that gates a revision on
   container health then refuses to start the app (the `SEARXNG_SECRET` failure shape). Measured:
   exit 0 against a live server, exit 1 against a dead port.

3. **The Dockerfile entry point is an `/app/server` symlink** made at build time, not a
   per-project name. Docker cannot shell-expand a build `ARG` into an exec-form `ENTRYPOINT`, so
   this is how A6's "parameterised HEALTHCHECK and ENTRYPOINT" was actually achieved.

4. **stdio reads `GOOGLE_ACCESS_TOKEN`** (`CalendarCaller.cs`). There is no OAuth flow over a pipe,
   and A6 requires the README to document stdio usage; without a credential path that documentation
   would describe something that cannot work.

5. **`CalendarException` carries a numeric `StatusCode` beside the curated message.** Agent42's
   adapter logged the status via `ILogger`; Repo A's `*.Core` projects have no logging, so without
   this the status was simply lost. It is deliberately NOT part of `Message`, so the curated-message
   discipline is unchanged.

6. **Caps live in `Calendar.Core.CalendarLimits`, not as `private const` on the tools class.** The
   repo convention (from `ScraperTools`) fits the scraper, where limits are applied in the tools
   class; here truncation happens while parsing the provider response, which is in Core.

7. **`Calendar.Core` does not reuse `Scraper.Core.Text.Truncate`.** Different marker (`…` vs `...`,
   a distinction ADR 0053 made deliberately for one-line event titles), and coupling two unrelated
   domains for one method is worse than a three-line helper.

8. **Tests are xunit v2, not v3.** `xunit.v3` pulls in Microsoft.Testing.Platform, which the .NET 10
   SDK refuses to drive through the VSTest bridge (`dotnet test` hard-errors); neither the
   `TestingPlatformDotnetTestSupport` property nor a `dotnet.config` runner entry fixed it. Settled
   on Agent42's proven set: xunit 2.9.3 + xunit.runner.visualstudio 4.0.0 + Microsoft.NET.Test.Sdk
   18.9.0.

9. **`tools/mcp_probe.py` was generalised** — its scraper sample calls are now skipped for a server
   that does not advertise the tool, so it is a `tools/list` probe for everything else. A4's
   verification ("probe lists six tools") needed this, and the scraper's probe output is unchanged.

10. **`BearerAuth` has no exempt-path list.** The plan said "exempt `/health`, `/.well-known/*` and
    `/oauth/*` from the key middleware". Bearer mode installs no blanket middleware at all — only
    `/mcp` carries an authorization policy — so those paths are anonymous by construction. An
    initial implementation did carry such a list; it was **dead code**, since the key middleware
    short-circuits on mode before consulting it, and dead code shaped like a security control is
    worse than none. `ApiKeyAuth`'s list is unchanged and still exempts `/health` alone, which is
    what keeps the scraper byte-identical.

### Critic review of the Repo A diff — findings and what was done

A `critic` subagent reviewed the diff before the MR. **Four majors and six minors; all accepted, none
rejected.** The first is the one that mattered:

1. **`Request.Scheme`/`Request.Host` with no forwarded-header handling — ADR 0042's failure,
   reproduced in the new repo.** Behind a TLS-terminating ingress (which is how this will be hosted)
   the connection to the process is plain HTTP, so `issuer`, `authorization_endpoint`,
   `token_endpoint` and the PRM `resource`/`authorization_servers` were all minted as `http://`. A
   client POSTing to an `http` token endpoint gets a 301, and **a redirected POST becomes a GET**.
   The existing discovery tests could not catch it, because the test listener is plain HTTP too.
   Fixed with `UseForwardedHeaders(XForwardedProto | XForwardedHost)` as the first middleware, plus a
   new `MCP_PUBLIC_ORIGIN` pin that wins outright — which also closes the second half the critic
   raised, that `Host` is a caller-settable header and this is a document telling clients where to
   send credentials. New `PublicOriginTests` covers both, via `X-Forwarded-Proto` and the pin.
2. **AC10's CI assertion was vacuous.** `! grep -q 'scrape_page' prm.json` is true of any PRM
   document, so it would pass against a blank page. Replaced with positive assertions
   (`Google Calendar MCP`, `/oauth/authorize`) plus a check that the *scraper* image does not serve
   them, which is what actually proves the `SERVER` build-arg selected the right server.
3. **`HttpResponseMessage` leak.** `response` was assigned inside the `try` but only disposed after
   it, so a throw while reading the body leaked the connection — a regression against the Agent42
   original, on exactly the failure most likely to repeat. Split into `SendRequestAsync` with the
   response held by a `using` in the caller.
4. **`docker-compose.yml` broke the scraper.** Compose interpolates the whole file, so
   `${GOOGLE_OAUTH_CLIENT_ID:?...}` made `docker compose up scraper-mcp` fail for anyone with no
   Google client — a change to the scraper's surface, which A2 forbids. Now plain `${VAR}`; the
   server still fails closed in-process.

Minors, all fixed: an `HttpClient` timeout surfaced as `TaskCanceledException` and escaped as a
JSON-RPC protocol error rather than a curated notice (now mapped, with a test that provokes a real
timeout); the truncation caps had no test at all (`CalendarLimitsTests`); the `GOOGLE_ACCESS_TOKEN`
fallback was reachable in HTTP mode and is now gated on there being no request at all;
`CalendarStartup` duplicated `CreateHttpClient`'s base address and timeout; "only those six tools"
was asserted by presence rather than by set equality; and the README's local example collided with
the scraper's default port.

**One fix was itself caught by its own test, and the trap is worth recording.** Making
`ResourceMetadataUri` absolute — so the `WWW-Authenticate` challenge would carry the pinned origin —
also changes **where the document is served**, and the handler then 404s a request whose host is not
that origin. `APinnedPublicOrigin_DoesNotMoveTheDocument` caught it; the URI stays relative and the
residual (the challenge carries the request's scheme, so a client that follows the challenge rather
than guessing the well-known path needs the proxy to send `X-Forwarded-Proto`) is recorded in the
code.

### Verified this session (measured, not assumed)

- `dotnet build McpServer.slnx` (Debug and Release): succeeded, **0 warnings**.
- `dotnet test McpServer.slnx` (Debug and Release): **53 passed, 0 failed** (38 before the critic
  round, 53 after). `dotnet test` ran **zero** tests before this branch.
- Scraper `/mcp`: 401 with no key (**byte-identical body**), 401 with a wrong key, 200 with
  `Authorization: Bearer`, 200 with `X-API-Key`. `mcp_probe.py` against Scraper.Mcp unchanged,
  including its live sample calls.
- Calendar `mcp_probe.py`: 6 tools, correct names, DI parameters absent from the schemas (**AC1**).
- Live HTTP server: PRM served 200 at the **root** well-known path (**AC4**); both discovery
  documents declare the two calendar scopes (**AC5a**); AS metadata names this server's own
  authorize and token endpoints (**AC5**); `/oauth/authorize` 302s to `accounts.google.com` with
  `access_type=offline` + `prompt=consent`, `resource` stripped, PKCE and state preserved (**AC3**);
  401 `WWW-Authenticate` carries `resource_metadata` pointing at the root path.
- HTTP mode with no Google client: refuses to start, **exit 2**.
- With `MCP_PUBLIC_ORIGIN=https://calendar.example.com/` set, both documents advertise
  `https://calendar.example.com` with the trailing slash stripped — checked on a live server, not
  only in tests.
- `dotnet list package --vulnerable --include-transitive`: **0 across all 7 projects**.

### Unverified residuals (honest, not deferred quietly)

- **AC11 / step A7 — the real-Google leg was NOT run.** It needs a real Google OAuth client with
  both redirect URIs registered, and it is the only way to confirm a refresh token is actually
  issued and that a call still works after the access token expires. Everything about that path is
  exercised against a fake Google only.
- ~~**AC10 / the `docker build` leg was NOT run**~~ — **resolved later the same session**, once a
  Docker daemon became available. Both images build; both containers report **`(healthy)`**, which is
  the `--health-probe` fix demonstrated in a real container rather than argued; every CI docker
  assertion passes; `MCP_PUBLIC_ORIGIN` is honoured inside the container; and the entry-point
  mechanism was checked directly — `scraper /app/server -> /app/Scraper.Mcp`,
  `calendar /app/server -> /app/Calendar.Mcp`, each image carrying only its own server's assemblies,
  both running as non-root `app`.
  **Running it found one more bug** (fixed in `6cbacfb`): the scraper's `MCP_API_KEY:?` made
  `docker compose config` fail outright, so `docker compose up calendar-mcp` was broken — the mirror
  image of the defect the critic found on the Google variables, and one that only became harmful
  because this change adds a second service. Reading a compose file is not running one.
- Repo A has **no branch protection**: `.claude/hooks/protect-main.mjs` pins `ROOT` to
  `CLAUDE_PROJECT_DIR` and reports Agent42's branch, so discipline there is manual (as the plan says).

### Quality gates

`.agentic/quality-gates.yaml` measures **Agent42** — its two gates name `check-new-code-coverage.mjs`
(coverlet + `git diff` against a merge base) and `SonarAnalyzer.CSharp` S3776 via
`Directory.Packages.props` + `.editorconfig`. None of that tooling exists in Repo A, and the plan
says so explicitly: "these gates are Agent42's; Repo A has none today and must not silently inherit
them". Per ADR 0050's own rule, a threshold with no measuring tool is not a gate — so those two are
reported **SKIPPED (no tooling in Repo A)** rather than estimated. The dependency gates *are*
measurable there and were measured. When Repo B is executed it runs under Agent42's real gates.

## Handoff — Repo B, for a fresh `/execute-plan` session

**Status: the landing-order gate is satisfied. Repo B may be executed.** The plan's own rule was
"Repo A must be deployed somewhere Agent42's backend can dial" before Repo B merges — that is now
true, verified against a real deployment, not a staging/local one.

### What is actually running, right now

- **`Calendar.Mcp`** is live at `https://google.agent42.nl/mcp` — a real, trusted, production
  Let's Encrypt certificate (verified with a client doing full hostname validation, no `-k`).
- **`Scraper.Mcp`** is live at `https://scraper.agent42.nl/mcp`, same certificate story.
- Both are fronted by **`Mcp.Gateway`** (`Z:\Sources\mcpserver`, `src/Mcp.Gateway/`) — one process,
  one container, host-based YARP routing, TLS-ALPN-01 via LettuceEncrypt — running on the user's own
  **bare-metal server, directly on the internet, no reverse proxy in front**. Getting here needed a
  real debugging pass through the actual deployment: the router's own WAN admin service was
  intercepting port 443, then a missing port-forward rule, then a missing Windows Firewall rule
  (Docker Desktop + WSL2 on the host), then a real, reproducible Certes bug in Let's Encrypt's
  *staging* chain specifically (documented in `GatewayCertificates.cs` and the mcpserver README —
  switching off staging and retrying against production resolved it cleanly). None of that was a
  code defect; all of it is now recorded so it isn't rediscovered.
- Google Cloud OAuth client for `Calendar.Mcp` is provisioned and working (`GOOGLE_OAUTH_CLIENT_ID`/
  `SECRET` set, container issuing real tokens).

This satisfies plan step B7(a)'s substance — the redirect URI
`https://chat.agent42.nl/api/mcp/oauth/callback` is the one to register on that Google OAuth
client (confirm it actually was, if picking this up cold).

### What is NOT yet confirmed — check before assuming Repo B is risk-free to start

1. **Has Agent42 actually been pointed at `https://google.agent42.nl/mcp`?** (Preferences → MCP
   servers → InteractiveOAuth, per B7(b), including opening one chat afterward so ADR 0058
   discovers the six per-tool checkboxes.) As of this handoff being written, that wiring step and
   real end-to-end usage (does the OAuth consent flow complete through Agent42, does
   `list_calendars`/`create_calendar_event` actually work, does a token survive an hour to prove
   refresh works) had **not yet been done or observed**.
2. **My own recommendation, not a plan requirement:** validate real usage through Agent42 for a
   while before executing Repo B's deletion. The hard gate (Repo A deployed and dialable) is met;
   this second one is prudence, not a rule the plan states — deleting the native fallback before
   anyone has confirmed the replacement actually works in practice is avoidable risk for zero
   benefit. If real usage has since been confirmed working, this concern is moot — proceed.

### Before executing B1–B7, two things a fresh session must still do itself

- **Drift check (per `/execute-plan` Step 1):** `planned-against: main @ e392958`. As of this
  handoff, `main` is one commit ahead, at `e3bc4cd` — and that one commit is PR #110, the doc-only
  commit that added this very spec file. It touches nothing in the plan's affected areas. A fresh
  session should still run the check itself rather than trust this note blindly, but should expect
  it to be a non-event.
- **B6(d)'s rule collision is still unresolved, deliberately.** `check-doc-paths.mjs` scanning every
  ADR versus "never edit an accepted ADR's decision content" for ADR 0039's Calendar path reference —
  the plan says explicitly *"pick one explicitly; do not let CI failure drive the choice."* Nothing
  since has decided this. It is the executor's call to make at execution time, not something this
  handoff should pre-empt.

### One correction for ADR 0063 (step B1) to make while writing it

The plan's Repo A steps left hosting **deliberately unspecified** ("Hosting was deliberately
deferred… this plan cannot name that host"). It can now be named accurately: Design D's OAuth front,
running behind `Mcp.Gateway` (host-based YARP routing + LettuceEncrypt/TLS-ALPN-01), on the
operator's own bare-metal server with no reverse proxy in front. ADR 0063 should record the *real*
shape, not the deferred placeholder — including that a second OAuth-backed MCP server sharing this
same gateway would need its own subdomain (host-based routing, not path-based — forced by the RFC
8414 path-insertion collision `BackendRoute`'s remarks describe), should Agent42 or another agent
ever front more than the two servers already running there.

## Execution — Repo B, and where it diverged from the plan

Executed 2026-09-04 on branch `fix/remove-native-calendar`. **Scope: Repo B (B1–B6). B7 is the manual
operator list and was handed to the user, not run.**

**Drift check.** `planned-against: main @ e392958`; HEAD was `4e764e4`, four commits ahead — PRs #110–113,
all ADR 0064 (MCP connection test) plus the two spec-doc commits. One plan instruction was already stale
and is the kind that ships a silent bug: the plan said bump `sw.js` CACHE **v28 → v29**, but #113 had
already spent v29, so the real bump was **v29 → v30**. The plan's own warning about a no-op bump applied
to the plan itself.

### Landing-order gate

Satisfied and confirmed with the user before any deletion: the MCP server is added in Preferences → MCP
servers as an InteractiveOAuth server, the Google consent flow completes through Agent42, and the calendar
tools work end to end. The handoff's second, discretionary gate ("validate real usage for a while first")
was therefore also met. What is **not** evidenced is the post-expiry refresh specifically — recorded as an
Unverified residual in ADR 0063 rather than glossed.

### Divergences from the plan

1. **The plan's Repo B file list was incomplete in five places**, one of which breaks the build:
   - `tests/Agent42.Tests/RecordingHttpHandler.cs:7-8` — a **third** CS1574 site (`see cref` to two
     deleted test files). The plan listed only the two in `src/`.
   - `tests/Agent42.Tests/PwaAssetsTests.cs:255` and `SearchAssetsTests.cs:120-121` — both pin the
     `sw.js` CACHE literal and fail on any bump. Not in the plan; found by running the suite.
   - `tests/Agent42.Tests/SharedApiFixture.cs:27,95` and `SharedClusterFixture.cs:46` — doc comments
     whose stated rationale was the calendar double.
   - `src/Agent42.Agents/Tools/ToolCatalog.cs` — the **Memory** const's docstring carried two
     `see cref` references to the calendar key (a fourth CS1574 site), using it as its no-migration
     precedent.
   - `src/Agent42.Backend/Web/SearxngProxy.cs:186` — ADR 0059's open-redirector guard justified itself
     by naming the deleted Google callback route. Found by the critic, not by any tool:
     `check-doc-paths.mjs` scans only `.md`.

2. **`AgentGrainTests.cs:2196`'s `"calendar"` literal was KEPT, against the plan.** The plan called it a
   stale literal to edit out. It is a *legacy snapshot* — the comment says "every key that existed then",
   and `calendar` genuinely did. `Sanitize` drops unknown keys, so it enables nothing. Removing it would
   have made the comment false and thrown away a case that now proves a since-deleted key in stored state
   is harmless. Annotated in place instead.

3. **B6(d) (the `check-doc-paths.mjs` rule collision) was decided by reading ADR 0039, which had already
   decided it.** The plan framed it as an open choice between teaching the script to skip superseded ADRs
   and editing ADR 0039. In fact 0039 (and 0040, identically) state: *"A folder list is a factual
   inventory, not a decision, so it is corrected in place."* So option (ii) was never a rule breach. Both
   halves shipped, because neither fixes the other — the skip is keyed on **`status: superseded`** and
   deliberately not on `superseded_by:`, which ADR 0007 carries while remaining authoritative.

4. **ADR 0040 was amended too, which the plan never mentioned.** It carries the same folder inventory and
   the same in-place-correction licence; leaving it would have had two sibling ADRs disagreeing about
   whether a deleted folder exists. Its pre-existing count drift (missing `Search/` and `Orleans/`, a
   stale endpoint-group count, an unmeasured "25 DTO records") was repaired in the same pass.

5. **A guard test for `check-doc-paths.mjs` was added** (`tests/Agent42.Tests/DocPathCheckerTests.cs`, 4
   tests). Not in the plan. This MR gives that script its first ability to switch itself **off** for a
   file, and it fails open, so the "partial supersessions stay fully checked" property needed something
   stronger than a comment. It paid for itself immediately — see below.

### Two self-inflicted faults worth recording

- **45 files were silently converted CRLF → LF** by the editing helper used here. `git diff` hid it
  (`core.autocrlf=true` normalises on compare), but `dotnet format` reads the file directly and failed
  with whitespace errors on lines the diff never touched. Confirmed against clean `main` in a throwaway
  worktree that the errors were introduced here, then restored CRLF. **If `dotnet format` flags lines you
  did not edit, check line endings before reading the diff.**
- **The shell wrapper collapsed doubled backslashes in a heredoc**, turning the new front-matter regex's
  escapes into a literal CR/LF and leaving `check-doc-paths.mjs` a **syntax error** — which still printed
  nothing alarming, because the script died before reporting anything. The four new `DocPathCheckerTests`
  caught it; the earlier manual mutation check could not, having run before that edit. Content with regex
  escapes goes through the file-writing tools, not a shell heredoc.

### Critic review of the Repo B diff

A `critic` subagent reviewed the staged diff: **two majors, three minors, one nit — all accepted, none
rejected.** The majors were ADR 0040 (item 4 above) and the `SearxngProxy` comment (item 1). The minors:
the new front-matter regex was scoped to the whole file rather than the front-matter block (a fail-open
hole, since `decisions/README.md` documents that very key); ADR 0063's Unverified section claimed the
refresh path "is exercised in production use" when the handoff said it had not been observed; and two live
rules still pointed at the now-superseded ADR 0053 (`appsettings.json`, `gotchas.md`).
It independently re-derived every count against the tree and confirmed no dangling ES specifier in either
direction, correct `sw.js` handling, safe tab indices, intact `$type` discipline, and that removing the
six `ToolNameToKey` rows causes no MCP regression (MCP tools are added after the `IsToolEnabled` filter
and gated by `IsMcpToolEnabled`, so the replacement server's identically-named tools are unaffected).

### Verified this session (measured, not assumed)

- `dotnet build Agent42.slnx`: **succeeded**, no new warnings (the `NU1507`/`NU1510`/`CS8602` ones are
  pre-existing on `main`).
- `dotnet test Agent42.slnx`: **1099 passed, 0 failed** (1095 after the deletion, plus 4 new guards).
- `dotnet format Agent42.slnx --verify-no-changes`: **exit 0**.
- `node .agentic/scripts/check-doc-paths.mjs`: **green**.
- `az bicep build --file infra/main.bicep`: **exit 0**.
- S3776 (`cognitive_complexity_max`, severity `error`): **0 violations**.
- `dotnet list package --vulnerable --include-transitive`, all 6 projects: **0**.
- `check-new-code-coverage.mjs`: *"no production code changed — nothing to gate"* — vacuous on a deletion
  MR, exactly as the plan predicted.
- Folder/partial/endpoint counts re-derived from the tree: Agents **20**, Backend **23**, `AgentGrain`
  partials **12**, endpoint groups **16** — agreeing across `ProjectLayoutTests`, CLAUDE.md, ADR 0039 and
  ADR 0040.

### Still outstanding

**B7, the manual operator steps** — none were run, and the agent must not run them:
(a) register the production redirect URI on the server's Google OAuth client (confirm it, if it was not
done during Repo A); (b) already done — the server is added and working; (c) revoke the old Agent42 grant
at <https://myaccount.google.com/permissions> (deleting code does not revoke it); (d) delete the orphaned
`google-credentials` / `google-oauth-pending` documents from `agent-state`; (e) remove the
`GOOGLE_OAUTH_CLIENT_ID` / `GOOGLE_OAUTH_CLIENT_SECRET` GitHub Actions secrets.
