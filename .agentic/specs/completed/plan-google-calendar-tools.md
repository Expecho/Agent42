# Plan — Google Calendar tools (list / get / create / update / delete, per-user OAuth)

planned-against: main @ 8dcaf84

Baseline verified at plan time: `dotnet build Agent42.slnx` → **0 errors**, 21 pre-existing warnings
(NU1507/NU1510, two CS8602 in `AgentApi.StoredAgents.cs`, five CS8765 in `CannedArmTransport.cs`) and
**no S3776 violations**. Any S3776 error you see is yours — that gate blocks the build (ADR 0051).

## Overview

Agent 42 can schedule a durable reminder for itself (ADR 0021 `scheduling`), but it cannot see or touch
the user's actual calendar. Add six model-invoked tools — `list_calendars`, `list_calendar_events`,
`get_calendar_event`, `create_calendar_event`, `update_calendar_event`, `delete_calendar_event` — over
the Google Calendar REST API v3, authorized by a **per-user** Google OAuth connection the user makes
once from Preferences.

Two shapes were considered at the interview and **rejected by the user after discussion — do not
relitigate**: a co-hosted first-party MCP server exposing the same operations, and a third-party remote
MCP server added through ADR 0024. The decision is **native tools**, which is also the cheapest shape:
it reuses `IGeocoder`'s port/adapter pattern exactly and adds no new transport.

## Affected areas

Verified against the tree at `8dcaf84`.

### New — `src/Agent42.Agents/Calendar/` (18th feature folder in Agents)

| File | Purpose |
|---|---|
| `ICalendarService.cs` | **new** — the port + its DTOs. Every method takes `string userId` as its first parameter (see "the port is user-scoped" below). |
| `CalendarToolLimits.cs` | **new** — return-value budgets, the `WebToolLimits`/`GeoToolLimits` shape |
| `AgentGrain.CalendarTools.cs` | **new** — the six tools. This is the **11th** `AgentGrain` part (ADR 0039). |
| `InMemoryCalendarService.cs` | **new** — a working canned calendar for DevHost + tests, the `InMemoryAttachmentStore` shape |
| `IUserGoogleGrain.cs` | **new** — per-user encrypted token bundle, state `"google-credentials"` on `agent-state` |
| `UserGoogleOAuthPendingGrain.cs` | **new** — one-time `TakeAsync` state grain, state `"google-oauth-pending"` |

### New — `src/Agent42.Backend/Calendar/` (19th feature folder in Backend)

| File | Purpose |
|---|---|
| `GoogleCalendarService.cs` | **new** — `ICalendarService` over Calendar REST v3; resolves + refreshes the user's token |
| `GoogleOAuth.cs` | **new** — `GoogleOAuthClient` (authorization URL, code exchange, refresh) + `GoogleOAuthTokens` + `GooglePkce` |
| `GoogleSecretProtector.cs` | **new** — `IGoogleSecretProtector`, DP purpose `Agent42.GoogleCalendar.Secret.v1` |
| `AgentApi.Calendar.cs` | **new** — `MapCalendarEndpoints`; the **14th** endpoint group (ADR 0040) |
| `CalendarContracts.cs` | **new** — the endpoint request/response records |

### Changed

| File | Change |
|---|---|
| `src/Agent42.Agents/Conversations/AgentGrain.cs` | inject `ICalendarService` — the **12th** constructor dependency |
| `src/Agent42.Agents/Tools/ToolCatalog.cs` | new `Calendar = "calendar"` key: `AllSelectableToolKeys`, `Descriptors`, and **six** `ToolNameToKey` entries |
| `src/Agent42.Agents/Tools/AgentGrain.BuildAgent.cs` | create the six `AIFunctionFactory` tools and add them to the base list |
| `src/Agent42.Backend/Api/AgentApi.cs` | call `MapCalendarEndpoints(app)` — a group that exists but is never called silently deletes every endpoint in it |
| `src/Agent42.Backend/Program.cs` | named `HttpClient`s (`RemoveAllResilienceHandlers()`), `ICalendarService`, `IGoogleSecretProtector`, `GoogleOAuthClient` |
| `src/Agent42.Backend/appsettings.json` | a `GoogleCalendar:` section — structure only, never values |
| `src/Agent42.Backend/Configuration/ConfigHealth.cs` | two `PresenceCheck(GroupOptional, …)` entries. **No live probe** — see below. |
| `tools/Agent42.DevHost/Program.cs` | register `InMemoryCalendarService` + the protector + the OAuth client |
| `src/Agent42.AppHost/AppHost.cs` | forward `GoogleCalendar__ClientId` / `GoogleCalendar__ClientSecret`, the `ContextScrape__ApiKey:49` shape |
| `src/Agent42.Backend/wwwroot/index.html` | a **Calendar** tab + panel, **appended last** (index 6) |
| `src/Agent42.Backend/wwwroot/js/calendar.js` | **new** — `wireCalendar()`, `loadGoogleStatus()`, `initGoogleOAuthReturn()` |
| `src/Agent42.Backend/wwwroot/js/main.js` | import + call the three entry points |
| `src/Agent42.Backend/wwwroot/js/dom.js` | element refs + the 7th `profileTabs` pair |
| `src/Agent42.Backend/wwwroot/js/profile.js` | `loadGoogleStatus()` beside the other tab loaders |
| `src/Agent42.Backend/wwwroot/sw.js` | `CACHE` `agent42-shell-v22` → `-v23`; add `/js/calendar.js` to `SHELL` |
| `src/Agent42.Backend/wwwroot/styles.css` | the connect/disconnect control. Reuse `.providers__*`; ADR 0049's Skills tab needed ~7 new lines, so expect a similar order — **not zero** |
| `README.md` | `:207` lists the API endpoints and `:215-217` the optional feature secrets — four routes and two secrets invalidate both (hard rule 2, same MR). Also add a link to the new integrations index beside the `docs/features/` link at `:18` |
| `docs/integrations/README.md` | **new** — index of integration setup guides, the `docs/features/README.md` shape |
| `docs/integrations/google-calendar/README.md` | **new** — the manual Google Cloud setup guide (Step 11) |
| `tests/Agent42.Tests/GrainStorageSerializerTests.cs`, `GoldenStates.cs`, `SerializationCompatibilityTests.cs` | the two new persisted state types (ADR 0049 touched all three for the same reason) |
| `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml` | two **optional** secrets |
| `tests/…` | see the AC table |
| `.agentic/decisions/0053-*.md`, `.agentic/gotchas.md`, `CLAUDE.md` | doc debt, paid in the same MR |

### Guard lists that will fail unless updated (all in `tests/Agent42.Tests/ProjectLayoutTests.cs`)

These are hard-coded and asserted by name, not by count. Miss one and the build is red:

- `ExpectedFolders()` — add `"Calendar"` to **both** project lists (`ProjectLayoutTests.cs:92-109`).
  **Do this in Step 1**, the moment the folder is created.
- `AgentGrainParts()` — add `"Calendar/AgentGrain.CalendarTools.cs"`; update the `<summary>` prose
  "The ten parts" → eleven (`:129-142`). **Step 6.**
- `AgentApiParts()` — add `{ "Calendar/AgentApi.Calendar.cs", "MapCalendarEndpoints" }`; update
  "The thirteen endpoint groups" → fourteen (`:156-172`). **Step 5.**
- `FrontEndWiredModules()` — add the three `calendar.js` entry points (`:229-256`). **Step 8.**
- `ServiceWorkerShell_ListsExactlyTheModuleDirectory` needs no edit but **will fail** until
  `/js/calendar.js` is in `SHELL` (`:286-299`). **Step 8.**
- `PwaAssetsTests.cs:215` pins the literal `"agent42-shell-v22"` — it fails until the `CACHE` bump.
  **Step 8.**
- `GrainStorageSerializerTests.cs:55-68` holds a **curated, hard-coded list of every persisted state
  type name** (deliberately literals, so a namespace change cannot silently move with it). Add the two
  new state types there, and mirror ADR 0049's precedent by also touching `GoldenStates.cs` and
  `SerializationCompatibilityTests.cs`. **Step 2.** Omitting this does **not** break the build — it
  silently leaves the new types unguarded against the ADR 0039 `$type` hazard, which is the whole point
  of the list.

**Each guard-list edit belongs in the step that creates the thing it guards, not batched at the end.**
`FeatureFolderSet_IsExactlyWhatTheAdrDescribes` (`:117`) goes red the instant `Calendar/` exists, so
deferring it would leave every intermediate step's "run the tests" gate uninterpretable.

`CLAUDE.md` mirrors those counts in prose (17 Agents feature folders → 18; 18 Backend → 19; "10 `partial`
files" → 11; "13 per-feature partials" → 14). Fix in the same MR (hard rule 2).

## Context & constraints

### User decisions taken at plan time — do not relitigate

1. **Native tools, not an MCP server.** The user first asked for a co-hosted first-party MCP server in
   the same process, then reversed it in the same round ("forget about it, make it a tool instead of mcp
   server" / "replace the mcp with tools"). Both MCP shapes are rejected. Record both in the ADR's
   *Alternatives considered* so the reversal is legible later.
2. **Per-user Google OAuth in Preferences**, not an operator refresh token and not a service account.
   Tokens are per user, DP-encrypted, refreshed at call time.
3. **Full read+write, no confirmation gate.** `create` / `update` / `delete` are model-invoked directly,
   like every other tool here. The accepted residual — a prompt injection arriving through ADR 0011/0027
   (web) or ADR 0049 (uploaded skills) can rewrite or delete calendar entries — is recorded honestly in
   the ADR, not mitigated.
4. **Multiple calendars.** A `list_calendars` tool, and an optional `calendar_id` on every other tool
   defaulting to `primary`.
5. **Attendees/invitations and recurring-series editing are OUT** (not selected). See *Out of scope*.

### Derived decisions the executor must implement — flagged, not silent inventions

**(a) The port is user-scoped, and the credential never enters the grain.**
`IGeocoder` is user-agnostic; a calendar is not. So `ICalendarService` methods take `userId` first, and
the **Backend** implementation resolves, decrypts and refreshes the token itself via `IGrainFactory` +
`IGoogleSecretProtector`. This is the `UserMcpToolProvider.ResolveOAuthAccessTokenAsync` shape
(`src/Agent42.Backend/Mcp/UserMcpToolProvider.cs:158-189`) — reuse its 2-minute refresh skew and its
persist-the-refreshed-bundle step. `AgentGrain` gets `_userId` from its own grain key
(`src/Agent42.Agents/Conversations/AgentGrain.cs:198,250-254`) and never sees a token. Do not put a
token on the port, in grain state, or in a tool return value.

**(b) The endpoints must NOT become a mandatory route-matcher dependency.**
`IMcpSecretProtector` did exactly that and broke routing for all six `MapAgentApi` hosts at once
(the MCP gotcha, `.agentic/gotchas.md:1270`). There are now **thirteen** files calling `MapAgentApi`
(`src/Agent42.Backend/Program.cs:452`, `tools/Agent42.DevHost/Program.cs:142`, and eleven test fixtures —
`AgentApiTests` calls it twice:
`AgentApiTests`, `AttachmentTests`, `ChatStreamDisconnectTests`, `ConfigHealthApiTests`,
`McpOAuthEndpointTests`, `McpServersApiTests`, `ProvidersApiTests`, `SpeechApiTests`,
`ToolSelectionApiTests`, `UsageApiTests`, `UserSkillsApiTests`). So every `/api/google/*` handler
resolves `IGoogleSecretProtector` and `GoogleOAuthClient` **optionally through `IServiceProvider`**,
exactly like `/api/speech` (`src/Agent42.Backend/Speech/AgentApi.Speech.cs:22-38`), and returns **409**
when they are absent. Note also gotcha `.agentic/gotchas.md:1011` — a minimal-API handler returning a
bare string serves `text/plain`, not JSON; all four new endpoints must return `Results.Json`/`Results.Text`
explicitly rather than a naked string. Zero new mandatory registrations for the API half.

**(c) `ICalendarService` IS a mandatory grain dependency — and one silo configurator needs a hand edit.**
Every silo that activates `AgentGrain` must register it or every activation there fails. Four of the six
test configurators go through `TestServiceRegistration.AddWebToolFakes()`
(`tests/Agent42.Tests/TestServiceRegistration.cs:52` is where `IGeocoder` sits), so adding it there
covers `TestSiloConfigurator`, `NoModelSiloConfigurator`, `CoordinatesOnlyGeocoderSiloConfigurator` and
`CountingStoreSiloConfigurator`. **`CapSizedFetcherSiloConfigurator` deliberately does not call
`AddWebToolFakes()`** (`tests/Agent42.Tests/CapSizedWebPageFetcher.cs:81-86`) and must be edited by hand.
Plus the two real hosts. That is the complete edit set: **four edit sites** —
`src/Agent42.Backend/Program.cs`, `tools/Agent42.DevHost/Program.cs`,
`tests/Agent42.Tests/TestServiceRegistration.cs`, `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` —
covering all **six** silo configurators and both real hosts. The sixth,
`ActivityPropagationSiloConfigurator` (`tests/Agent42.Tests/ActivityPropagationTests.cs:127`), needs
**no edit at all** — it delegates via `new TestSiloConfigurator().Configure(siloBuilder)`. That is stated
so the enumeration reads as complete rather than as a miscount.

**Prod registers exactly one adapter, and it self-reports.** There is deliberately **no**
`NotConfiguredCalendarService` twin of `CoordinatesOnlyGeocoder`: `GoogleCalendarService` is registered
unconditionally in the Backend and returns the curated "not connected / not configured" result itself
when `GoogleCalendar:ClientId`/`ClientSecret` are blank *or* the user has not connected. That is the
`ContextWebPageFetcher.ConfigureClient` shape (ADR 0027 — a blank key degrades per call), not the
`IGeocoder` two-type shape, because the two failure modes here are the *same* result and splitting them
into two types would only duplicate the message. `InMemoryCalendarService` is DevHost/tests **only** and
must never be registered by `src/Agent42.Backend/Program.cs` — a fake calendar answering in prod is far
worse than an honest "not connected".

**(d) Google is a confidential client: PKCE alone is not enough.**
Unlike the MCP flow (a public client, PKCE-only), a Google **Web application** client requires
`client_secret` in the token exchange, so this needs **two** config values, not one. Confirmed against
Google's web-server OAuth guide, 2026-08-26. Two more wire facts pinned there and load-bearing:
- Authorization endpoint `https://accounts.google.com/o/oauth2/v2/auth`, token endpoint
  `https://oauth2.googleapis.com/token`. **Static — so PRM/RFC-8414 discovery is skipped entirely**,
  which is the one part of ADR 0025's client that does not carry over.
- The authorization URL **must** carry `access_type=offline` **and** `prompt=consent`. Without both,
  Google returns no refresh token on re-consent and the connection dies at the first access-token
  expiry — silently, an hour later, not at connect time.

PKCE is still added as defence in depth. `McpPkce` is not reused: copy the three methods into
`GooglePkce` rather than couple `Calendar/` to an `Mcp`-named type. This repo already prefers the copy
(ADR 0035's `ShareToken` is a copy of `ApiKey`) — say so in the ADR so it does not read as an oversight.

**(e) The consent screen must be In production, or this breaks weekly.**
With the OAuth consent screen left in **Testing** status and an External user type, Google **revokes
refresh tokens after 7 days** — the user would reconnect every week and blame the app. `.../auth/calendar`
is a *sensitive* scope, so an unverified production app still shows the "Google hasn't verified this app"
interstitial, which is acceptable for a personal app but must be documented. This is a **manual
prerequisite the executor cannot perform** (Step 0) and belongs in the ADR's consequences and in the
`gotchas.md` entry.

**(f-0) Naming: config section `GoogleCalendar:`, routes `/api/google/*` — deliberately asymmetric.**
The config section is **not** `Google:`, because `Geocoding:GoogleApiKey` already exists
(`src/Agent42.Backend/appsettings.json:27`) and two differently-scoped "Google" keys one line apart is
how an operator pastes the wrong secret. So: `GoogleCalendar:ClientId` / `GoogleCalendar:ClientSecret`
→ `GoogleCalendar__*`, and DP purpose `Agent42.GoogleCalendar.Secret.v1`. The **routes** stay
`/api/google/oauth/{start,callback}` because the OAuth *provider* is Google and the redirect URI is a
manual, registered-in-Google-Cloud constant that a future Google feature would reuse. Do not "fix" the
asymmetry into matching names — it is intentional, and changing the route later means re-registering
the redirect URI by hand.

**(f) The scope is the narrow PAIR, not `.../auth/calendar`.** Request exactly:
`https://www.googleapis.com/auth/calendar.events` (read/write events on all the user's calendars) **+**
`https://www.googleapis.com/auth/calendar.calendarlist.readonly` (enumerate calendars). That pair
satisfies decisions 3 and 4 in full while excluding precisely the three things this plan already puts
under *Out of scope*: creating/deleting calendars and changing ACL/sharing.

**Verified at plan time (2026-08-26), not assumed** — `calendar.events` is listed in the "Authorization
scopes" block of `events.insert`, `events.update` and `events.delete` (fetched individually), and
`calendar.calendarlist.readonly` is listed for `calendarList.list`. `events.list`/`events.get` were
**not** fetched individually — they are read methods on the same resource under a read-and-write scope,
so this is an inference; if either 403s at integration time, that is the one place to look first.
The scope's own description is *"View and edit events on **all** your calendars"*, which is what keeps
decision 4 (multiple calendars) intact.

This is a **correction made at review**: an earlier draft asked for the broad `.../auth/calendar` and
called it "the minimum", which was simply false. The distinction is load-bearing given decision 3 (no
confirmation gate) plus an acknowledged prompt-injection channel — under the broad scope a successful
injection can delete a whole calendar or grant a stranger write access to one; under this pair it
cannot. **Do not widen it back** without recording why. If Google's consent UI or a runtime 403 forces
the broad scope after all, that is a plan deviation: take it, and record in the ADR that **the stored
token is then strictly more powerful than the feature it serves**.

The connected account's identity is displayed from the **primary calendar's id** (it is the account
email), so **no extra `openid`/`email` scope is needed** — do not add one.

**(f-2) The OAuth client needs a test seam, or AC6 and AC9 cannot be proved.**
Making the endpoints compile-time constants is right for the *flow* (no discovery) but wrong for
*testability*: `src/Agent42.Backend/Mcp/McpOAuth.cs:69` is shaped the way it is precisely so the whole
flow runs "against a fake authorization server", and it gets that for free because its endpoints are
discovered data. Constants remove that seam, and AC6/AC9 would either hit the live internet from CI or
be untestable. So `GoogleOAuthClient` takes its authorization and token endpoints as **constructor
values with the Google URLs as the pinned defaults** (overridable from config for tests only) — the
`GoogleGeocoder.DefaultBaseAddress` shape, where the constant is a default, not a hard-code. The
production registration must pass nothing and get Google.

**(g) Listing expands recurrences, and that constrains edit/delete.**
`list_calendar_events` uses `singleEvents=true&orderBy=startTime`, which is what makes "what's on my
calendar tomorrow" work. The consequence is that returned ids are **instance** ids, so an edit or delete
the model performs against one touches **that occurrence only** — it cannot edit a series. Say this in
the tool descriptions so the model does not claim otherwise, and record it as a known limitation.

**(h) `sendUpdates=none` on update and delete.** Attendees are out of scope for *writing*, but a
**pre-existing** event may already have them, and a bare `PATCH`/`DELETE` would email real people. Pin
the parameter explicitly on both, and pin it with a test — this is the one irreversible outward-facing
side effect in the feature.

**(h-2) The READ side is the bigger consequence, and it must be recorded — decision 3 only covers writes.**
Decision 3 honestly records injection → *write*. Nothing yet records what a calendar **read** does. Once
`list_calendar_events` returns, event titles, descriptions, locations and the attendees on *pre-existing*
events are in the transcript — and `list_calendars` additionally returns the ids of **shared** calendars,
which are **other people's email addresses**. From the transcript that data:

- **leaves the Entra tenant on any third-party model** (ADR 0013) — this is the headline consequence
  every comparable `CLAUDE.md` bullet carries, and the ADR must carry it too;
- is **drainable** by ADR 0027's allowlist-exempt followed links (gotcha at `.agentic/gotchas.md:199`) —
  the injection→**exfiltrate** path, which is at least as serious as injection→delete;
- is visible in full to anyone holding an ADR 0035 share token — mechanically "no new surfacing", but a
  genuinely new *class* of third-party personal data reaching that surface;
- is mirrored **unencrypted** into origin IndexedDB by ADR 0032;
- is persisted into the 2 MB Cosmos session forever and re-sent every later turn.

Related, and also a consequence rather than an exclusion: because the tools are registered in
`BuildAgent`, they are live on **`ReceiveReminder`** and **`EnqueuePromptAsync`** (`/api/agent-run`,
ADR 0022). A model-invoked **delete** can therefore fire with no user present, from a webhook-triggered
run whose prompt came from outside the tenant. Move this out of *Out of scope* (where an earlier draft
wrongly put it) and into the ADR's consequences — Step 6 makes it reachable whether or not it is
designed for.

**(i-2) The outbound SPAN leaks the calendar id, and OTel does not redact a path.**
Gotcha `.agentic/gotchas.md:424`: OTel redacts an HttpClient span's **query string** by default — it does
**not** redact the path. Calendar REST v3 puts the calendar id (**an email address**) and the event id in
the path: `/calendars/{calendarId}/events/{eventId}`. ADR 0010 stamps `user.email`/`user.full_name` onto
every span under a request identity, so an un-handled span joins one named human to another named
human's calendar in the collector. Derived decision (i) reasons only about the model and the log; this
covers the third sink. Suppress or rewrite the span's URL for this client — and pin it with a test, the
way `GoogleGeocoder` pins its own no-logging claims.

**(i-3) A third Data Protection purpose brings the key-ring trap with it.**
Gotcha `.agentic/gotchas.md:739`: in prod the DP key ring **must persist out-of-process**, or a restart
rotates it and every stored secret becomes undecryptable — and `Unprotect` **swallows**
`CryptographicException` and returns null. For this feature that failure is silent and specific: after a
restart with an unpersisted ring the refresh token is orphaned, and the user's calendar simply goes
quiet with no error anywhere. The `dataprotection` blob container (ADR 0018) already exists, so there is
nothing to provision — but `GoogleSecretProtector` must degrade to "not connected", never throw, and the
ADR must name this failure mode.

**(i) Errors from Google are curated before they reach the model or the log.**
A Google API error body echoes the request URL, and the URL carries the access token in no case here
(it is a header) but the body can carry account and event detail. Follow the ConfigHealth probe
discipline (`.agentic/gotchas.md:1529`): catch, discard, return a constant. Never surface `ex.Message`,
the response body, or the target URL to the model.

**(j) Incognito chats may still write to the calendar — deliberately.**
ADR 0034 blocks documents (durable Cosmos chunks) and refuses scheduling inside `SchedulePromptAsync`
(a durable reminder that would fire the persisting path) — see the ephemeral gotcha at
`.agentic/gotchas.md:347` for both, and for the `_ephemeral` turn-scoped-field trap. A calendar write is
different in kind: the durable record it creates is **outside our system and is the user's own explicit
intent**, exactly like a web search leaving the tenant. The ephemeral guarantee is "not persisted by us,
not listed", not "no side effects". Allow it — **and add a test pinning the deliberate non-refusal**,
so a later reader does not "fix" it into a refusal by analogy with scheduling.

One honest wrinkle to state rather than leave to be discovered: a near-expiry **token refresh** during
an incognito turn writes the new bundle to `IUserGoogleGrain` — a durable write *inside* our system,
triggered by an incognito turn. It carries no conversation content, so it does not breach the ADR 0034
guarantee, but it is not nothing. Say so in the ADR.

**(k) No live ConfigHealth probe.** Every other probe is a keyed operator call; a calendar probe would
need a *user's* token and would be meaningless with none connected. Static presence checks for
`GoogleCalendar:ClientId` and `GoogleCalendar:ClientSecret` only, via
`PresenceCheck(GroupOptional, …)` (`src/Agent42.Backend/Configuration/ConfigHealth.cs:97-107` is the
shape). Do not add a `ProbeAsync` entry.

**(l) The new key will be silently OFF for this user — say so, do not quietly migrate.**
This is the ADR 0048 divergence, and it applies to any *new static* key, not just to skills. `null == all`
only helps a user who has **never** saved a tool selection: the picker materialises "all" as the full
**explicit** list, so anyone who has ever pressed Save in Preferences → Tools (or set a per-conversation
override, or saved an agent) has a **non-null** `EnabledTools` that cannot contain `calendar`, because
`calendar` did not exist when they saved. The six tools will therefore be registered by `BuildAgent` for
nobody, and the feature will look broken while being correct.

Handle it in **this** order, and do not silently invent the alternative:
1. An agent whose stored `EnabledTools` is still `null` (never narrowed in the agent editor) snapshots
   the bare **wildcard** through `BindAgentAsync` and therefore *does* pick the new key up. Only an
   explicitly-saved selection misses it. That is the same two-path divergence CLAUDE.md already records
   for skills — confirm it still holds rather than assuming it, since AC17 pins the resolver half only.
2. **No migration.** ADR 0049's auto-enable hook exists because an upload is an event to hang it on;
   a shipped key has none, and rewriting every stored selection to add a key the user never chose is a
   worse precedent than one re-save. Do **not** add one.
3. **Tell the user.** The ADR's consequences and the MR description must both say: after deploying,
   open Preferences → Tools and press Save once (and tick Calendar on any agent that needs it).

### Governing ADRs and gotchas — read before touching each area

| Area | Read |
|---|---|
| The OAuth flow to copy (and the two traps) | `.agentic/decisions/0025-mcp-interactive-oauth-app-driven-pkce.md`, gotcha at `.agentic/gotchas.md:1305` |
| Tool return values vs the 2 MB session | gotcha at `.agentic/gotchas.md:132`, `.agentic/decisions/0004-attachments-in-blob.md` |
| Selectable-tool keys, `null == all`, wildcard expansion | `.agentic/decisions/0021-*.md`, `0026-*.md`, `0048-*.md`, gotcha at `:1190` |
| The port/adapter + named-HttpClient shape | gotcha at `:251` (retry pipeline), exemplar `src/Agent42.Backend/Location/GoogleGeocoder.cs` |
| `Request.Scheme` behind the ACA ingress | `.agentic/decisions/0042-*.md`, gotcha at `:1409` — the redirect URI is `http://` without it |
| Feature folders / flat namespace / `$type` on disk | `.agentic/decisions/0039-*.md`, `0040-*.md`, gotcha at `:1560` |
| Frontend module + SW cache rules | `.agentic/decisions/0041-*.md`, gotchas at `:1609`, `:1629`, `:1646` |
| Egress: what leaves the tenant | `.agentic/decisions/0013-multi-provider-model-catalog.md` |

**No ADR is contradicted by this plan**, so no `/decide` supersession step is needed — only the new
ADR 0053. ADR 0002's `excludedPaths` array in `container-app.bicep` is **unchanged** — the four entries
stay exactly as ADR 0042 left them. But `EasyAuthExclusionTests` is **not** left untouched: its
`Exclusions_DoNotCoverAnyAuthenticatedPath` theory proves gating by naming paths one at a time, so the
three new `/api/google/*` routes must be **added as rows** or nothing actually asserts they are gated
(AC12). This matters: an OAuth callback reachable outside Easy Auth would make `GetUserId()` return
`local-dev` and bind every user's Google tokens to one phantom principal.

## Steps

**Step 0 — manual prerequisites (the user, not the executor).** Create a Google Cloud project, enable
the Calendar API, create a **Web application** OAuth client, register the redirect URI
`https://chat.agent42.nl/api/google/oauth/callback`, set the consent screen to **In production**
(see derived decision (e)), and add `GOOGLE_OAUTH_CLIENT_ID` + `GOOGLE_OAUTH_CLIENT_SECRET` as GitHub
Actions secrets. The executor cannot do this and must not block on it — every step below is verifiable
against stub HTTP servers and the in-memory service. **Step 11 writes this up properly** in
`docs/integrations/google-calendar/README.md`; treat that guide as the deliverable form of this step,
and keep the two in agreement.

**Step 1 — the port.** `ICalendarService` + DTOs + `CalendarToolLimits` + `InMemoryCalendarService`.
*Files:* `src/Agent42.Agents/Calendar/{ICalendarService,CalendarToolLimits,InMemoryCalendarService}.cs`.
**Also update `ExpectedFolders()` in the same step** — the folder assertion goes red the moment
`Calendar/` exists.
*Verify:* `dotnet build Agent42.slnx` && `dotnet test --filter "ProjectLayoutTests"`. Do **not** add a
`CalendarToolLimitsTests` asserting "the constants exist and are non-zero" — that restates the source
and cannot meaningfully fail. AC5 carries the real property.

**Step 2 — token storage.** `IUserGoogleGrain` (state `"google-credentials"`) and
`UserGoogleOAuthPendingGrain` (state `"google-oauth-pending"`, one-time `TakeAsync`), both riding the
shared `agent-state` provider on distinct state names — **no new Cosmos container, no Bicep change**
(`src/Agent42.Agents/Mcp/UserMcpOAuthPendingGrain.cs:51,62` is the literal shape to copy; the
pre-declared-containers gotcha at `.agentic/gotchas.md:978` is why a new container would be a real cost).
**Both new state types go in the flat `Agent42.Agents` namespace despite living in `Calendar/`** — the
folder and the namespace must disagree. Cosmos grain storage writes the CLR type name into every
document, so a folder-matching namespace makes the state unreadable on the next deploy; this is the
failure mode ADR 0039 exists for (gotcha at `:1560`), and it passes every binary-serializer test while
being broken on disk. **Add both state type names to `GrainStorageSerializerTests.cs:55-68` in this
step**, plus the `GoldenStates.cs` / `SerializationCompatibilityTests.cs` companions.
*Verify:* `dotnet test --filter "GoogleOAuthPendingGrainTests|UserGoogleGrainTests"`.

**Step 3 — OAuth client + protector.** `GoogleOAuth.cs` (`GoogleOAuthClient`, `GoogleOAuthTokens`,
`GooglePkce`) and `GoogleSecretProtector.cs`. No discovery — endpoints are constants.
*Verify:* `dotnet test --filter "GoogleOAuthClientTests|GoogleSecretProtectorTests"`.
*Parallelizable with Step 4 once Step 2 lands.*

**Step 4 — the Google adapter.** `GoogleCalendarService` over Calendar REST v3, a named `HttpClient`
with an explicit timeout and `RemoveAllResilienceHandlers()`, refresh-near-expiry, curated errors,
`sendUpdates=none`, and the return budget applied.
*Verify:* `dotnet test --filter "GoogleCalendarServiceTests"` (stub HTTP server asserting the **outbound
request lines**, the `GoogleGeocoderTests`/`ContextWebPageFetcherTests` shape).

**Step 5 — endpoints.** `MapCalendarEndpoints` with `GET /api/google/status`,
`GET /api/google/oauth/start`, `GET /api/google/oauth/callback`, `DELETE /api/google/connection`;
registered from `src/Agent42.Backend/Api/AgentApi.cs`. Optional service resolution per derived
decision (b). Callback redirects to `/?google_oauth=connected` or `…=error&reason=…`, never echoing a
provider error body. **Update `AgentApiParts()` and add the three `EasyAuthExclusionTests` rows here.**
*Verify:* `dotnet test --filter "GoogleOAuthEndpointTests|ProjectLayoutTests|EasyAuthExclusionTests"`.

**Step 6 — the six tools.** `AgentGrain.CalendarTools.cs` + `ToolCatalog` key + `BuildAgent` registration.
**Update `AgentGrainParts()` and its "ten parts" prose here.**
*Verify:* `dotnet test --filter "CalendarToolTests|ToolCatalogTests|AgentGrainTests|ProjectLayoutTests"`.

**Step 7 — host registrations.** The four edit sites from derived decision (c).
*Verify:* `dotnet test Agent42.slnx` — the whole suite, because a missed silo is a mass activation failure.

**Step 8 — frontend.** `calendar.js`, the tab appended **last** (index 6; **MCP must stay 3**), `dom.js`,
`main.js`, `profile.js`, `styles.css`, `sw.js` `CACHE` → `agent42-shell-v23` + `SHELL` entry.
**Update `FrontEndWiredModules()` and the `PwaAssetsTests.cs:215` cache literal here.**
*Verify:* `dotnet test --filter "CalendarAssetsTests|PwaAssetsTests|ProjectLayoutTests"`.
*Parallelizable with Steps 3–6 — disjoint files. Concurrent write-capable subagents require
`isolation: "worktree"`.*

**Step 9 — infra + config health.** Two optional Bicep params (empty default, **no `@minLength`**),
conditional Container App secret + env (`GoogleCalendar__ClientId`, `GoogleCalendar__ClientSecret`),
workflow wiring, `appsettings.json` section, two `PresenceCheck` entries.
*Verify:* `dotnet test --filter "ConfigHealthTests|EasyAuthExclusionTests"`; `az bicep build` if available.

**Step 10 — docs.** ADR 0053, the new gotcha entries, `CLAUDE.md` (bullet + the four counts),
`README.md` (`:207` endpoints, `:215-217` optional secrets), and move this spec to
`.agentic/specs/completed/`. The guard-list edits are **not** here — each landed in its own step above.
*Verify:* `dotnet test Agent42.slnx` && `dotnet format Agent42.slnx --verify-no-changes`.

**Step 11 — the integration setup guide (`docs/integrations/google-calendar/`).**
Everything in Step 0 that a human must do by hand in Google Cloud, written out properly, so the
prerequisite stops living only in this spec (which moves to `completed/` and is then nobody's
reference).

**Path — read this before creating anything.** The user asked for `/doc/integrations`; the repo already
has **`docs/`** (plural) carrying `docs/features/` — 19 feature pages plus a README index, linked from
the root `README.md:18`. A second `doc/` tree beside `docs/` would be a genuine trap, so this goes in
**`docs/integrations/`**, a sibling of `docs/features/`. Per-integration **subfolders** as asked (unlike
`docs/features/`, which is flat) — an integration guide accumulates screenshots and console
walkthroughs, and those need somewhere to live.

Create:
- `docs/integrations/README.md` — an index in the shape of `docs/features/README.md` (a table, one row
  per integration), so the second integration has an obvious home.
- `docs/integrations/google-calendar/README.md` — the guide itself.
- A link to the new index from the root `README.md`, beside the existing `docs/features/` link.

The guide must cover, in order, and in enough detail to follow without prior Google Cloud knowledge:
1. Create or select a Google Cloud project; **enable the Google Calendar API**.
2. Configure the **OAuth consent screen**: External user type, app name, support email — and
   **publishing status → "In production"**, with the reason stated plainly (in *Testing* status Google
   revokes refresh tokens after **7 days**, so the connection dies weekly). Note that a sensitive scope
   on an unverified app still shows the "Google hasn't verified this app" interstitial, and that this is
   expected and safe for a personal deployment.
3. Add exactly the two scopes — `.../auth/calendar.events` and `.../auth/calendar.calendarlist.readonly`
   — and say what each buys, and what the broader `.../auth/calendar` would have added (calendar
   creation/deletion and ACL changes) and why it is deliberately not requested.
4. Create credentials → **OAuth client ID** → application type **Web application**, with the authorized
   redirect URI `https://chat.agent42.nl/api/google/oauth/callback` **exactly** (and the DevHost origin
   too, if connecting locally). Flag `redirect_uri_mismatch` as the symptom of getting this wrong.
5. Copy the client id and secret into the GitHub Actions secrets `GOOGLE_OAUTH_CLIENT_ID` and
   `GOOGLE_OAUTH_CLIENT_SECRET`; deploy; then connect in **Preferences → Calendar**.
6. **The post-deploy step that is easy to miss:** open Preferences → Tools and press **Save** once, or
   the calendar tools stay invisible (derived decision (l)).
7. A **troubleshooting table** mapping symptom → cause: `redirect_uri_mismatch`; `invalid_client`; no
   refresh token returned (missing `access_type=offline` / `prompt=consent`); connection dies after
   exactly 7 days (consent screen left in Testing); 403 insufficient scope; and *the calendar goes quiet
   after a restart with no error* (Data Protection key ring not persisted — derived decision (i-3)).
8. **How to revoke**: `DELETE /api/google/connection` from the UI, and independently at
   `myaccount.google.com/permissions`.

**The guide contains no secret values and no screenshots showing one.** It is committed to a public-ish
repo and read by the same discipline as `ConfigHealth`: name the setting, never the value.

*Verify:* `node .agentic/scripts/check-doc-paths.mjs` (ADR 0053 will reference the guide path in
backticks, and that script fails CI when a referenced path does not exist), plus the doc-path job in
`.github/workflows/pr-checks.yml:87`.

**Step 12 — manual legs, reported honestly.** (a) DevHost over headless Chrome/CDP: the Calendar tab
renders, the tools appear in the picker, and the in-memory calendar round-trips a create → list →
update → delete. (b) The real Google leg needs Step 0 and can only be smoke-tested in prod. Record
whichever was actually run in the ADR, and record the other as an unverified residual — the ADR 0043
precedent, not the ADR 0049 one.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A connected user's model can list their calendars | `ListCalendars_ReturnsTheUsersCalendars` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC2 | Listing events passes the time window, `singleEvents=true`, `orderBy=startTime` | `ListEvents_PassesTimeWindowAndExpandsRecurrences` | `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` |
| AC3 | create → get → update → delete round-trips | `CreateUpdateDelete_RoundTrips` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC4 | Not connected → a curated notice, never a throw | `NotConnected_ReturnsCuratedNotice_AndDoesNotThrow` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC5 | Return values stay inside the budget (2 MB session) | `ListEvents_RespectsReturnBudget` + existing `ToolResults_DoNotBlowTheSessionSizeBudget` | `CalendarToolTests.cs`, `AgentGrainTests.cs` |
| AC6 | Tokens persist encrypted and are never echoed by any endpoint | `OAuthCallback_PersistsEncryptedTokens`, `Status_NeverEchoesTokens` | `tests/Agent42.Tests/GoogleOAuthEndpointTests.cs` |
| AC7 | The authorization URL carries `access_type=offline`, `prompt=consent`, the calendar scope and an `https` redirect | `AuthorizationUrl_RequestsOfflineAccessAndConsent` | `tests/Agent42.Tests/GoogleOAuthClientTests.cs` |
| AC8 | The callback `state` is one-time; a replay is rejected | `Take_IsOneTime` | `tests/Agent42.Tests/GoogleOAuthPendingGrainTests.cs` |
| AC9 | A near-expiry token refreshes at call time and the new bundle is persisted | `RefreshesNearExpiry_AndPersistsTheNewBundle` | `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` |
| AC10 | The six tools are gated by the `calendar` key, and every registered tool still maps to a catalog key | `Calendar_IsSelectable_AndGatesItsTools` + existing `EveryRegisteredTool_MapsToACatalogKey_OrIsGetDate_OrIsAnMcpTool` | `ToolCatalogTests.cs`, `AgentGrainTests.cs` |
| AC17 | A **stored** selection saved before this shipped resolves to calendar-off, while a `null` selection resolves to calendar-on — the derived-decision (l) divergence is real and pinned, not accidental | `LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes` | `tests/Agent42.Tests/ToolCatalogTests.cs` |
| AC18 | No credential ever crosses the port: `ICalendarService`'s surface and the six tool result DTOs carry no token/secret-shaped member (derived decision (a), pinned by assertion rather than by comment — the `GetCurrentLocation_ExposesNoParameters` discipline) | `CalendarPort_ExposesNoCredential` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC19 | The outbound calendar span does not carry the calendar id / attendee email in its URL path (derived decision (i-2), gotcha `:424`) | `CalendarSpan_DoesNotCarryTheCalendarIdInThePath` | `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` |
| AC20 | An **incognito** chat (ADR 0034) may still create a calendar event — the non-refusal is deliberate and pinned, so it is not later "fixed" by analogy with `SchedulePromptAsync` | `EphemeralChat_MayStillWriteToTheCalendar` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC21 | The tool descriptions state that listing expands recurring events, so an edit/delete touches one occurrence and not the series (derived decision (g)) | `CalendarToolDescriptions_StateTheSingleInstanceLimitation` | `tests/Agent42.Tests/CalendarToolTests.cs` |
| AC22 | The Google Cloud setup guide exists at the path the ADR cites, and every repo path it references resolves | `node .agentic/scripts/check-doc-paths.mjs` (CI job at `.github/workflows/pr-checks.yml:87`) | `docs/integrations/google-calendar/README.md` |
| AC11 | Blank `GoogleCalendar:ClientId`/`ClientSecret` → `GoogleCalendarService` returns the curated not-configured result per call, and ConfigHealth reports it as a warning. (**"the app still boots" is deliberately NOT claimed here** — `ProjectLayoutTests.cs:396-397` records that nothing in the suite runs `Program.cs`, so no test can prove it. Step 12's DevHost leg is the only evidence.) | `BlankClientConfig_ReportsNotConfigured` + `ConfigHealth_ReportsGoogleCalendarClientPresence` | `GoogleCalendarServiceTests.cs`, `ConfigHealthTests.cs` |
| AC12 | Every `/api/google/*` route stays behind Easy Auth | `Exclusions_DoNotCoverAnyAuthenticatedPath` — **add three `[InlineData]` rows** (`/api/google/oauth/start`, `/api/google/oauth/callback`, `/api/google/status`) beside the existing `/api/mcp/oauth/start` row at `:83`. The Bicep `excludedPaths` array itself stays unchanged. | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| AC13 | `calendar.js` is reachable from `main.js` and `sw.js` `CACHE` is bumped with the module in `SHELL` | `FrontEndModule_ExportsItsEntryPoint_AndIsCalledFromTheCompositionRoot`, `ServiceWorkerShell_ListsExactlyTheModuleDirectory`, `ServiceWorker_CacheNameIsCurrent` | `ProjectLayoutTests.cs`, `PwaAssetsTests.cs` |
| AC14 | The Calendar tab is appended last and **MCP stays index 3** | `CalendarTab_IsAppendedLast_AndMcpStaysThird` | `tests/Agent42.Tests/CalendarAssetsTests.cs` |
| AC15 | A Google API error never reaches the model or the log with its body or target URL | `ApiError_ReturnsCuratedMessage_WithoutBodyOrUrl` | `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` |
| AC16 | Update and delete pin `sendUpdates=none` — no mail to pre-existing attendees | `UpdateAndDelete_NeverNotifyAttendees` | `tests/Agent42.Tests/GoogleCalendarServiceTests.cs` |

New test files: `CalendarToolTests.cs`, `GoogleCalendarServiceTests.cs`, `GoogleOAuthClientTests.cs`,
`GoogleOAuthEndpointTests.cs`, `GoogleOAuthPendingGrainTests.cs`, `GoogleSecretProtectorTests.cs`,
`UserGoogleGrainTests.cs`, `CalendarAssetsTests.cs`, `FakeCalendarService.cs`, `CalendarToolLimitsTests.cs`.
Changed: `ToolCatalogTests.cs`, `AgentGrainTests.cs`, `ConfigHealthTests.cs`, `ProjectLayoutTests.cs`,
`PwaAssetsTests.cs`, `TestServiceRegistration.cs`, `CapSizedWebPageFetcher.cs`, `EasyAuthExclusionTests.cs`, `FakeChatClient.cs`
(a keyword → calendar tool call, the `get_current_location` precedent).

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (commit `8dcaf84`) — do not write these from memory:

| Gate | Threshold | Measured by |
|---|---|---|
| `coverage_new_code_pct_min` | **80** | `node .agentic/scripts/check-new-code-coverage.mjs` (coverlet cobertura ∩ `git diff -U0` vs the merge base) |
| `cognitive_complexity_max` | **15** | `SonarAnalyzer.CSharp` rule **S3776** at build time — an **`error`**, so a method over the threshold **fails `dotnet build`** (ADR 0051 cleared the backlog) |
| `dependencies.vulnerable_packages` | **0** | CI package-freshness job — hard block |
| `dependencies.outdated_packages` | report | CI — soft, not blocking |
| `dependencies.add_policy` | latest-stable | resolved at add time, never from memory (hard rule 4) |
| `enforcement.local` | `soft_warning` | render the gate table, ask proceed/abort on FAIL |
| `enforcement.ci` | `hard_block` | build, tests, `dotnet format`, vulnerable packages, doc-path integrity, new-code coverage |

Two notes for the executor. **S3776 blocks the build**, and six tools plus a switch over calendar
operations is exactly the shape that trips it — keep each tool method thin and push branching into the
adapter. **No package version may be written from memory**: this plan adds **no** new package (the
Calendar REST API is called over `HttpClient`, the `GoogleGeocoder` precedent). If the executor decides a
`Google.Apis.*` package is needed after all, that is a plan deviation — resolve the version with
`dotnet add package` and pin it in `Directory.Packages.props`.

## Out of scope

- **Attendees and invitations on write** — `create`/`update` accept no attendee list. (Reading an
  existing event's attendee count is fine; sending mail is not.)
- **Recurring-series editing** — no `RRULE` authoring, and no single-instance-vs-series choice. Listing
  expands instances; an edit touches one occurrence. See derived decision (g).
- **Free/busy queries**, calendar creation/deletion, ACL/sharing changes.
- **Any other Google service** — no Gmail, Drive, Tasks, Contacts.
- **An MCP server, first-party or third-party**, exposing these operations (explicitly rejected — see
  the ADR's alternatives).
- **A scheduled-reminder → calendar-event integration.** Note carefully what this does *not* say: the
  tools **are** live on the reminder and `/api/agent-run` paths, because `BuildAgent` registers them
  there too. That is a **consequence** (derived decision (h-2)), not an exclusion — an earlier draft
  wrongly listed it here. What is out of scope is any *designed* bridge between ADR 0021 scheduling and
  the calendar.
- **A confirmation/approval gate on writes** — rejected by the user; recorded as a residual risk.
- **Sharing calendar state into ADR 0035 public share links** — a shared conversation shows whatever the
  tool already returned into the transcript; no new surfacing.

## Review

A fresh-context `critic` pass ran against this spec at `8dcaf84`. It independently re-verified and
**confirmed** every structural count and citation: the 11th `AgentGrain` part, 14th endpoint group,
18th/19th feature folders, thirteen `MapAgentApi` files, 12th constructor dependency, tab index 6,
`CACHE` v22, the quality-gate table (verbatim against `quality-gates.yaml`), and all eleven cited
gotcha line numbers. It found **no ADR contradiction**, so no `/decide` supersession step is needed.

**Findings accepted and fixed in this spec:**

| # | Finding | Fix |
|---|---|---|
| H1 | A new *static* key lands OFF for anyone who ever pressed Save — the ADR 0048 trap | Derived decision **(l)** + **AC17**. (Already written before the critic reported; it reviewed the earlier draft.) |
| H2 | Constant OAuth endpoints remove the seam AC6/AC9 need | Derived decision **(f-2)** — endpoints are constructor values with Google as the pinned default |
| H3 | `.../auth/calendar` was falsely called "the minimum" | Derived decision **(f)** rewritten: narrowed to `calendar.events` + `calendar.calendarlist.readonly`, which excludes exactly the out-of-scope operations |
| H4 | Read-side egress/exfiltration unrecorded; async-path reachability misfiled | Derived decision **(h-2)** + the *Out of scope* correction |
| M1 | Four steps named a verification that could not pass | Every guard-list edit moved into the step that creates the thing it guards |
| M2 | Missing: `GrainStorageSerializerTests`/`GoldenStates`/`SerializationCompatibilityTests`, `styles.css`, `README.md` | Added to *Affected areas* and to Steps 2/8/10 |
| M3 | AC12 forbade the edit its own exemplar requires | AC12 rewritten to add three `[InlineData]` rows. (Also fixed before the critic reported.) |
| M4 | "No token crosses the port" had no test | **AC18** |
| M5 | No degraded adapter named; AC11 claimed something untestable | Derived decision **(c)** now names the single self-reporting adapter; AC11 drops the "app still boots" claim |
| M6 | Uncited gotchas: DP key ring (`:739`), OTel path-not-redacted (`:424`), ephemeral (`:347`) | Derived decisions **(i-2)**, **(i-3)**, **(j)** + **AC19**, **AC20** |
| L1 | Line-reference drift on three citations | Corrected to `:129-142`, `:156-172`, `:22-38` |
| L2 | Sixth silo configurator unaccounted for | Derived decision (c) now states `ActivityPropagationSiloConfigurator` delegates and needs no edit |
| L3 | `PwaAssetsTests.cs:215` missing from the guard list | Added |
| L4 | `CalendarToolLimitsTests` was tautological | Step 1 now explicitly says not to write it |
| L5 | Instance-id limitation and the `text/plain` gotcha had no anchor | **AC21** + the gotcha `:1011` note in derived decision (b) |

**Findings rejected:** none. Every finding was either accepted or had already been fixed independently
before the critic reported.

**Added after the review, at the user's request:** Step 11 — a written Google Cloud setup guide under
`docs/integrations/<integration>/`, with `google-calendar/` as the first one, plus **AC22**. One
deviation from the literal request, made deliberately: the user wrote `/doc/integrations`, but the repo
already has `docs/` (plural) holding `docs/features/`, so a `doc/` sibling would be a trap. Used
`docs/integrations/`. Per-integration subfolders are as requested.

## Execution notes — where the implementation diverged from this plan

Executed 2026-08-26 on `feature/google-calendar-tools`, from `main @ 8dcaf84` (no drift: HEAD equalled
`planned-against`). Every step landed. The deviations, in the order they mattered:

### Bugs the plan did not anticipate

**The connected account email was always `null` on a first connect.** Found by the Step 5 critic pass,
not by a test. `ResolvePrimaryCalendarIdAsync` was written as an *argument* to
`SetAsync(new GoogleConnection(…))`, so it evaluated **before** the connection was stored — and it goes
out through `ICalendarService`, whose adapter resolves the caller's token from the very grain being
written. The lookup therefore ran against an empty grain, returned `NotConnected`, and stored
`AccountEmail: null`. Three documents state the identity comes from the primary calendar's id and that
this is *why* no `openid`/`email` scope is requested, so the bug contradicted the ADR, the setup guide
and the port doc-comment at once — and the UI hides it behind a silent fallback to bare `"connected"`.

Fixed by storing first, then resolving, then updating. **The test was also wrong**, and that was the
more interesting half: `GoogleOAuthEndpointTests` registered the bare `InMemoryCalendarService`, which
answers regardless of connection state, so `OAuthCallback_PersistsEncryptedTokens` was green against
broken code. It now registers a `ConnectionAwareCalendarService` double that consults
`IUserGoogleGrain` the way production does. Verified falsifiable: reintroducing the old ordering fails
two tests.

### Additions the plan did not list

- **`tests/Agent42.Tests/RecordingHttpHandler.cs`** — a shared recording handler. The repo's existing
  `FakeHttpHandler` is private to `McpOAuthClientTests` and keeps only the *last* body, which cannot
  express a refresh-then-call sequence (AC9) or assert on request *lines* (AC2/AC16/AC19).
- **`CalendarSpanSuppressingHandler`** — derived decision (i-2) said "suppress or rewrite"; suppression
  was chosen. It needed no new package (`OpenTelemetry` arrives transitively).
- **Calendar triggers in `tools/Agent42.DevHost/CannedChatClient.cs`** — that client has its own trigger
  table, separate from the test `FakeChatClient`. Without them Step 12(a)'s round-trip could not be run
  at all; the first attempt produced empty tool chips and looked like a broken feature.
- **A `CalendarHttpClient_IsRegisteredWithTheSpanSuppressingHandler` source-text guard** — added at the
  critic's request. AC19 as planned pinned the *mechanism* but not the *wiring*, so deleting one line
  from `Program.cs` would have left the suite green while every calendar id (an email address) reached
  the collector.

### Things the plan named that were deliberately not done

- **`CalendarToolLimitsTests.cs`** is listed under "New test files" but Step 1 and review finding L4 both
  say not to write it (tautological). Step 1/L4 won; the file does not exist. The two halves of the plan
  contradicted each other.
- **Step 7 was merged into Step 6's gate.** The host registrations are a prerequisite for Step 6's tests
  (a missing `ICalendarService` is a mass activation failure), so they landed together and the full-suite
  gate ran once, at Step 7 as planned.

### Pre-existing failures fixed in-branch

- **`check-doc-paths` was already red on `main`.** ADR 0052 wrote `wwwroot/js/sidebar.js` in `CLAUDE.md`
  without its `src/Agent42.Backend/` prefix. Fixed here rather than deferred — a red gate is a red gate.
- **Two documents quoted the tool-catalog key set and both went stale** when `calendar` was added:
  `CLAUDE.md`'s ADR 0021 bullet and `docs/features/tool-selection.md` (prose *and* a verbatim code
  quote). Hard rule 2, fixed in the same MR.

### Smaller corrections made during execution

- Tool parameters needed `[Description]` attributes **and defaults**; without defaults
  `AIFunctionFactory` treats every parameter as required and a call with an empty argument dictionary
  fails at invocation ("Error: Function failed.").
- `CalendarToolTests` observes `ToolCallEnd` rather than the fake client's `[tool:…]` echo — that echo
  joins every tool result in the conversation and trims to 400 characters, so a 25-event listing arrived
  as truncated non-JSON and a third turn's result was pushed out of the window entirely.
- `Utf8JsonWriter`'s default encoder escapes the `+` of a UTC offset to `+`. Valid JSON, decodes
  identically, but a raw-substring assertion on the request body fails — the test parses instead.
- Bicep `@description` strings cannot carry an unescaped apostrophe; `az bicep build` caught it. The
  possessive was rewritten away rather than escaped.
- Four more critic findings applied: the ConfigHealth "no live probe" assertion was phrased so it could
  never fail (probe keys are `probe:*`); `initGoogleOAuthReturn`'s error note could be blanked by an
  in-flight status render; `WriteDraft`'s `forCreate` branch was dead (both arms returned the same
  value); and a `410 Gone` — what Calendar v3 answers for an event deleted since it was listed — was
  reported as `Failed` rather than `NotFound`, which would have the model retry a doomed call.

### Step 0 and Step 12(b) remain unperformed, by design

The Google Cloud prerequisites cannot be done by an executor. The consent screen, the real code
exchange, the hourly refresh, the 7-day revocation behaviour and every real `403` are unverified
residuals, smoke-testable only in production. `docs/integrations/google-calendar/README.md` is the
deliverable form of Step 0.
