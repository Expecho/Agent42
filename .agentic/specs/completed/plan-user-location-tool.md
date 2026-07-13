# Plan — `get_current_location` tool (browser geolocation → Google reverse geocoding)

planned-against: main @ c66b64c

## Overview

Agent 42 cannot answer "what's the weather here", "restaurants near me", or "what's my address" —
it has no idea where the user is. Add a model-invoked tool, `get_current_location`, that returns the
user's current coordinates and a reverse-geocoded street address, working on the mobile PWA (real GPS)
and on desktop browsers (WiFi/IP fix) alike.

The whole design turns on one constraint: **`navigator.geolocation` is a browser API, and tools run
server-side inside `AgentGrain`.** A tool cannot go ask the browser for a fix mid-turn — the chat path
is a one-way NDJSON stream from grain to client (`AgentApi.cs:75-80`). So the position must **ride the
chat request into the turn**, and the tool reads it from turn-scoped state. That is the same shape
attachments already use (`ChatRequest.AttachmentIds` → `ChatAsync(..., attachments)`), and it is the
only shape available.

## Affected areas

| File | Change |
|---|---|
| `src/Agent42.Agents/UserLocation.cs` | **new** — Orleans-serializable per-turn position record |
| `src/Agent42.Agents/IGeocoder.cs` | **new** — reverse-geocoding port + `GeocodedPlace` result |
| `src/Agent42.Agents/CoordinatesOnlyGeocoder.cs` | **new** — offline/no-key implementation |
| `src/Agent42.Agents/IAgentGrain.cs` | `ChatAsync` gains a 4th optional param |
| `src/Agent42.Agents/AgentGrain.cs` | inject `IGeocoder`; turn-scoped location field; `get_current_location` tool + registration |
| `src/Agent42.Agents/GeoToolLimits.cs` *(or extend `WebToolLimits.cs`)* | return-value budget for the tool |
| `src/Agent42.Backend/GoogleGeocoder.cs` | **new** — `IGeocoder` over Google Geocoding API |
| `src/Agent42.Backend/AgentApi.cs` | `ChatRequest.Location`; forward to `ChatAsync` |
| `src/Agent42.Backend/Program.cs` | named `HttpClient` + `IGeocoder` registration (key present → Google, absent → coordinates-only) |
| `src/Agent42.Backend/appsettings.json` | `Geocoding:*` config keys |
| `src/Agent42.Agents/Skills/websearch/SKILL.md` | extend the "never put conversation text in a query" rule to name the user's **location** |
| `src/Agent42.Backend/wwwroot/app.js` | background position capture + one-time "share location" grant control |
| `src/Agent42.Backend/wwwroot/index.html`, `styles.css` | the grant control |
| `tools/Agent42.DevHost/Program.cs` | register `CoordinatesOnlyGeocoder` |
| `src/Agent42.AppHost/AppHost.cs` | **check only** — no `Geocoding__GoogleApiKey` is passed, so the Aspire path falls to `CoordinatesOnlyGeocoder` via `Program.cs`; confirm nothing else is needed |
| `tests/Agent42.Tests/TestServiceRegistration.cs` | fake `IGeocoder` in `AddWebToolFakes` |
| `tests/Agent42.Tests/CapSizedWebPageFetcher.cs` | its silo configurator registers grain deps **inline**, not via `AddWebToolFakes` — it needs the fake `IGeocoder` too, or every grain activation in it fails (and AC5 runs there) |
| `tests/Agent42.Tests/FakeChatClient.cs` | keyword → `get_current_location` tool call |
| `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml` | API key as a Container App secret |
| `.agentic/decisions/0012-*.md`, `.agentic/gotchas.md`, `CLAUDE.md` | doc debt, paid in the same MR |

## Context & constraints

**User decisions taken at plan time (do not relitigate):**

1. **Google Geocoding API**, with an API key the user already holds. (Nominatim/OSM and Azure Maps were
   the alternatives; Google was chosen because the key exists.) The key is a **secret** — it must
   travel the `SEARXNG_SECRET` road: GitHub Actions secret → `@secure()` Bicep param → Container App
   secret → env var. It must never be logged and must never appear in a tool return value.
2. **Auto-attach whenever the browser has already granted permission** — no per-conversation toggle,
   no off switch beyond the browser's own site settings.
3. **Full precision, and yes to persistence** — the exact fix and full address land in chat history.

**Derived decisions the executor must implement (flagged to the user, not silent inventions):**

*(a) A grant affordance, and it cannot be gated on the Permissions API alone.*
"auto-attach when granted" needs *some* first-time affordance, because a permission that is never
requested is never granted, and firing `getCurrentPosition()` on an ordinary send would ambush the user
with a browser prompt. So a small, one-tap **"Share my location"** control triggers the browser prompt
once — a *grant* button, not a toggle.

But **`navigator.permissions.query({name: "geolocation"})` is not supported in WebKit** — i.e. every
browser on iOS/iPadOS — where it rejects. A design that gates auto-attach *purely* on
`state === "granted"` is therefore **dead on iOS**: the user grants, no fix is ever attached, and the
grant button never hides. (The first draft of this plan had exactly that hole; the `critic` pass found
it.) The gate is therefore two-legged:

- **Permissions API available** (Chrome/Edge/Firefox, desktop + Android): attach when
  `state === "granted"`; hide the grant control; subscribe to the status' `change` event.
- **Permissions API absent or rejecting** (WebKit/iOS): fall back to a `localStorage` flag set only
  after the grant button's `getCurrentPosition()` **succeeds**. A permission the browser has already
  granted resolves without re-prompting, so an authorized capture is safe; a
  `PositionError.PERMISSION_DENIED` on any later capture **clears the flag** (the user revoked it in
  site settings) and shows the grant control again.

Never call `getCurrentPosition()` on a send without one of those two authorizations. This is
device-verifiable only — iOS must be checked on a real device (precedent: the Web Speech gotcha).

*(b) Capture in the background; never make the user wait for a fix.*
An awaited `getCurrentPosition({ enableHighAccuracy: true, timeout: 5000 })` in the send path adds up to
five seconds to **every** message once the cached fix goes stale — and on mobile a cold GPS acquisition
routinely takes seconds. Typing "hi" must not pay for a GPS spin-up. So the frontend keeps a
**last-known fix** refreshed *out of band* (on grant, on `visibilitychange` → visible, and on a modest
interval while the tab is visible) and the send path attaches whatever is currently cached, **without
awaiting anything**. `capturedAt` rides along so the model (and the tool's return value) can say how old
the fix is; a fix older than a sane ceiling (suggest 30 min) is dropped rather than sent.

**Constraints from the knowledge files — each of these has already bitten this repo:**

- **A tool's return value is persisted into the 2 MB Cosmos session item** (gotchas: "An agent tool's
  RETURN value is persisted"; ADR 0004). A raw Google Geocoding response is a large JSON document with
  every address component and multiple candidate results. **Never return it.** Return a compact record
  (formatted address, locality, country, lat/lon, accuracy metres) and cap the formatted address —
  budget it explicitly the way `WebToolLimits` budgets the web tools.
- **A named `HttpClient` silently inherits `AddServiceDefaults`' retry pipeline** (gotchas). The geocode
  client MUST call `.RemoveAllResilienceHandlers()` and set an explicit `Timeout` — `AgentGrain` is
  non-reentrant, so a retried geocode multiplies the stall on the user's turn.
- **This tool is NOT an SSRF sink** — and the reason is narrow, so pin it. Unlike `fetch_web_page`, the
  host is fixed (`maps.googleapis.com`, config-pinned) and the tool takes **zero model-supplied
  parameters**: the only variable input is a lat/lon pair from the *browser*, range-validated
  server-side, and a `double` cannot inject a host. So it must **not** grow a `PrivateAddressGuard` —
  say so in a comment so a later reader doesn't "fix" the asymmetry. But the claim holds *only while
  those two properties hold*: a later contributor adding a `[Description("address to look up")] string`
  parameter turns it into an SSRF sink with the reassuring comment still sitting above it. **Pin it with
  a test** (AC13) rather than a comment alone.
- **The user's position is PII, and this repo already documents two channels that would carry it out.**
  The address lands in chat history at full precision (user decision 3) and is re-sent to the model on
  every later turn — including turns long after this one. The conversation already has two *acknowledged,
  unclosed* outbound text channels: the **model writes the `web_search` query**, which the
  `fetch_web_page` URL allowlist does not cover and which SearXNG forwards to DuckDuckGo/Bing
  (gotchas: "SearXNG is self-hosted, NOT private"), and a fetched page's text can instruct the model to
  exfiltrate context (gotchas: "The fetch allowlist must pin whole URLs"). After this feature, the
  conversation those channels drain may hold the user's **street address**. The existing control is an
  instruction in `Skills/websearch/SKILL.md`; it must be **extended to name the user's location** as
  something never to place in a search query or a URL. That is mitigation, not prevention — and ADR 0012
  must say so, rather than pretending Google is the only outflow.
- **The geocode call is an outbound HTTP span, and this repo stamps user identity onto every span.**
  `AddServiceDefaults` enables `AddHttpClientInstrumentation()` (`ServiceDefaults/Extensions.cs`), and
  `UserTelemetrySpanProcessor.OnStart` stamps `user.email`/`user.full_name`/`user.roles` onto **every**
  span started under a request identity (ADR 0010). So the geocode span joins *this named human* to
  *this outbound call*, and — unless the query string is redacted — could carry `latlng=<exact coords>`
  **and the API key** to the OTLP collector. Verify redaction and, if it isn't on, filter or scrub
  `url.full` for this client. Note the named exemplar, `SearxngWebSearch`, **logs its query** — copying
  it verbatim would log the user's coordinates.
- **Do not use Orleans `RequestContext`** to carry the position. Its one use in this repo is deliberate
  and documented (ADR 0010 / gotchas): it is the *identity* channel, chosen specifically because
  `Activity.Baggage` would leak PII to Azure OpenAI in headers. Position is per-turn payload; it belongs
  in the grain method signature, next to `attachments`.
- **Do not put the position in `AgentGrainState`** — a stale fix persisted across turns would have the
  model confidently reporting where the user was last week. It rides the grain call and lives in a
  private field. **That field lives for the grain *activation*, not the call**, so it must be
  **explicitly cleared** — in a `finally` at the end of `ChatAsync`, and set to null on entry to
  `ReceiveReminder`. Doing nothing on the reminder path does *not* leave it null: `ReceiveReminder` is a
  separate call on the same activation, and after any chat turn that carried a fix the field is still
  populated. (This is precisely the bug AC4 exists to catch; the first draft of this plan contained it.)
  Non-reentrancy is *not* the argument that makes the field safe — `ChatAsync` is an `IAsyncEnumerable`
  method that suspends between `yield return`s — the explicit clear is.
- **Offline parity.** `IWebSearch`/`IWebPageFetcher`/`IDocumentIndex` all follow a
  real-in-Backend / in-memory-in-Agents split (`IWebSearch.cs:11`), registered per-host
  (`Program.cs:176`, `DevHost/Program.cs:45`, `TestServiceRegistration.cs:37`). `IGeocoder` follows it.
  When `Geocoding:GoogleApiKey` is absent (DevHost, AppHost, tests), register `CoordinatesOnlyGeocoder`
  — the tool still works and returns coordinates with no address, rather than the feature vanishing.
- **Secure context.** `navigator.geolocation` requires HTTPS; prod (`chat.agent42.nl`) is HTTPS and
  `http://localhost:61326` (DevHost) is a secure context by definition, so both work with no exception.
  The service worker already returns early for `/api` (gotchas), so nothing there changes.

**Exemplars to copy, not invent:** `src/Agent42.Backend/SearxngWebSearch.cs` (typed client over an
HTTP API, logs and degrades rather than throwing into the turn), `src/Agent42.Agents/InMemoryWebSearch.cs`
(offline twin), `tests/Agent42.Tests/TestHttpServer.cs` (real loopback socket for HTTP-client tests),
`src/Agent42.Agents/AgentGrain.cs:117-177` (tool declaration + `ChatOptions.Tools` registration).

**No ADR is contradicted.** ADR 0008 chose always-on retrieval for *documents* and rejected on-demand
tools there; ADR 0011 already drew the line — a tool is right when the data is not needed every turn.
Location is squarely in the ADR 0011 half (querying the user's position on "hi" would be absurd), so
this extends the established split rather than reopening it.

## Steps

### Step 1 — Contracts + offline geocoder *(parallelizable with Step 2)*
Files: `src/Agent42.Agents/UserLocation.cs`, `IGeocoder.cs`, `CoordinatesOnlyGeocoder.cs`,
`GeoToolLimits.cs`.

`UserLocation(double Latitude, double Longitude, double? AccuracyMetres, DateTimeOffset? CapturedAt)`
as an Orleans `[GenerateSerializer]` record with `[Id(n)]` on every member (copy `AttachmentRef.cs`'s
shape). `IGeocoder.ReverseAsync(UserLocation, CancellationToken) → GeocodedPlace?` returning
`(string FormattedAddress, string? Locality, string? Country)`; null means "no address found", not an
error. `CoordinatesOnlyGeocoder` returns null. `GeoToolLimits` caps the formatted address (suggest 300
chars, matching `WebToolLimits`' snippet cap — read the actual constant, don't guess).

**Verify:** `dotnet build Agent42.slnx`.

### Step 2 — `GoogleGeocoder` *(parallelizable with Step 1 once the interface lands; sequence if unsure)*
Files: `src/Agent42.Backend/GoogleGeocoder.cs`, `tests/Agent42.Tests/GoogleGeocoderTests.cs`.

`GET {base}/maps/api/geocode/json?latlng={lat},{lng}&key={key}` — base address injected so a test can
point it at `TestHttpServer`. Parse `results[0].formatted_address` + the `locality`/`country` address
components. Handle `status: ZERO_RESULTS` (→ null), `OVER_QUERY_LIMIT` / `REQUEST_DENIED` / non-2xx
(→ log a warning, return null — never throw into the middle of the user's turn), and truncate to
`GeoToolLimits`.

Three requirements that are easy to miss and each has a named failure:

- **Format the coordinates with `CultureInfo.InvariantCulture`.** A `double` interpolated under the
  ambient culture renders `52,3676` on a `nl-NL` machine, so `latlng=52,3676,4,9041` — which Google
  rejects. Nothing in this repo sets `InvariantGlobalization`, so this breaks on the *developer's*
  machine and works in the Linux container: the worst way round. AC6's test asserts on the **request URI
  the `TestHttpServer` actually received**, not merely on the parsed result.
- **The key and the coordinates must never be logged**, and must never appear in a returned
  `GeocodedPlace`. Do not copy `SearxngWebSearch`'s query logging.
- **Keep them off the telemetry span too** — see the constraint above. Confirm the OTel HttpClient
  instrumentation redacts the query string for this client; if it does not, scrub/filter `url.full`.

**Verify:** `dotnet test Agent42.slnx --filter GoogleGeocoder`.

### Step 3 — Grain: turn-scoped location + the tool
Files: `src/Agent42.Agents/IAgentGrain.cs`, `AgentGrain.cs`, `tests/Agent42.Tests/FakeChatClient.cs`,
`AgentGrainTests.cs`.

- `IAgentGrain.ChatAsync(string message, string? model, IReadOnlyList<AttachmentRef>? attachments = null,
  UserLocation? location = null)` — a 4th optional param; keep the `[Alias("ChatAsync")]` attribute.
- Inject `IGeocoder`. Add `private UserLocation? _turnLocation;`, assigned at the top of `ChatAsync`,
  **cleared in a `finally` at the end of `ChatAsync`**, and **explicitly set to null on entry to
  `ReceiveReminder`**. The field outlives the call (it lives for the activation) — leaving the reminder
  path alone does *not* leave it null, it leaves it holding the last chat turn's fix. See the constraint
  above; AC3 and AC4 exist to catch exactly this.
- Declare the tool with `AIFunctionFactory.Create(GetCurrentLocationAsync, name: "get_current_location",
  description: …)` and add it to `ChatOptions.Tools`, alongside `webSearch`/`fetchWebPage`
  (`AgentGrain.cs:145-177`). Unconditional, like the others.
- `GetCurrentLocationAsync()` takes **no parameters** — the model must not be able to supply
  coordinates, and that zero-parameter shape is the load-bearing half of the "not an SSRF sink"
  argument, so AC13 pins it. When `_turnLocation` is null, return a plain-language string telling the
  model the user has not shared their location and to ask them to tap "Share my location" (the model
  surfaces that to the user; nothing else can). Otherwise reverse-geocode and return the budgeted
  record, including how old the fix is.
- Extend `src/Agent42.Agents/Skills/websearch/SKILL.md`: its existing rule ("never paste conversation
  contents … into a query") must now **name the user's location** explicitly, in both the query and the
  URL sense. This is the only control on the ADR 0011 exfiltration channel, and this feature raises what
  that channel is worth stealing.
- `FakeChatClient`: map a keyword (e.g. a message containing "where am i") to a
  `get_current_location` call, following `BuildWebSearchCall`'s shape (`FakeChatClient.cs:168`). The
  existing `ToolResultEcho` then surfaces the result in the reply for assertions.

**Verify:** `dotnet test Agent42.slnx --filter AgentGrain`.

### Step 4 — API: carry the position in
Files: `src/Agent42.Backend/AgentApi.cs`, `tests/Agent42.Tests/AgentApiTests.cs`.

`ChatRequest` gains `UserLocation? Location = null` (trailing optional — positional callers keep
compiling, as the existing comment at `AgentApi.cs:246-248` requires) and the endpoint passes it to
`ChatAsync`. Validate the payload server-side: latitude ∈ [-90, 90], longitude ∈ [-180, 180], else drop
it to null rather than 400 — a bad fix should not fail the user's turn.

**Verify:** `dotnet test Agent42.slnx --filter AgentApi`.

### Step 5 — Frontend *(parallelizable with Step 4)*
Files: `src/Agent42.Backend/wwwroot/app.js`, `index.html`, `styles.css`,
`tests/Agent42.Tests/PwaAssetsTests.cs`.

Implement the two derived decisions above — read them before writing code.

- **Authorization gate, two-legged.** Capture only when either the Permissions API reports
  `state === "granted"`, **or** (WebKit/iOS, where that query rejects) a `localStorage` flag set after a
  successful grant says the user has authorized it. Clear the flag on `PERMISSION_DENIED`. Never call
  `getCurrentPosition()` on a send path without one of those.
- **Background capture, cached fix.** Refresh a module-level last-known fix out of band — after a
  successful grant, on `visibilitychange` → visible, and on a modest interval while visible. The send
  path reads the cache **synchronously**; it never awaits a fix. Drop a fix older than ~30 min rather
  than sending a stale one. `enableHighAccuracy: true` is acceptable here *because* nobody is waiting on
  it.
- **Send.** Add `location: {latitude, longitude, accuracyMetres, capturedAt}` to the POST body
  (`app.js:768-776`) when a fresh cached fix exists; omit it otherwise. **No geolocation failure — denied,
  timeout, unsupported, no cached fix — may block or delay a chat.**
- A **"Share my location"** control that calls `getCurrentPosition` once to trigger the browser prompt.
  Hide it once authorized (either leg); subscribe to the permission status' `change` event where the
  Permissions API exists.

**Verify:** `dotnet test Agent42.slnx --filter PwaAssets`, plus a manual browser check. The C# asset
tests are **source-text heuristics** — there is no JS runner in this repo, so they prove the code *says*
the right thing, not that a browser *does* it. The iOS/WebKit leg in particular is only real once it has
run on a device (precedent: the Chrome-on-Android Web Speech gotcha, where desktop and mobile engines
diverged and only the device revealed it).

### Step 6 — Wiring: DI + config + offline hosts
Files: `src/Agent42.Backend/Program.cs`, `appsettings.json`, `tools/Agent42.DevHost/Program.cs`,
`tests/Agent42.Tests/TestServiceRegistration.cs`, `src/Agent42.AppHost/AppHost.cs`.

In `Program.cs`, next to the web-tool block (`Program.cs:126-181`): read `Geocoding:GoogleApiKey`; if
present, `AddHttpClient(GoogleGeocoder.HttpClientName, …)` with an explicit timeout (suggest 5s — the
user's turn is blocked on it) **and `.RemoveAllResilienceHandlers()`**, and register `GoogleGeocoder`; if
absent, register `CoordinatesOnlyGeocoder` and log once that address lookup is disabled. Unlike the
embedding deployment (ADR 0007) this is **not** eager-validated into a startup throw — a missing key
degrades to coordinates-only, it does not take the app down.

Register the fake `IGeocoder` in **both** test silo paths — `TestServiceRegistration.AddWebToolFakes`
*and* the inline registrations in `CapSizedWebPageFetcher.cs`'s configurator, which does not call
`AddWebToolFakes`. A grain dependency missing from one configurator fails **that host's activations
only** (`TestServiceRegistration.cs:31-34` says so), and AC5 runs on the cap-sized silo.

`AppHost.cs` needs no key (the Aspire path lands on `CoordinatesOnlyGeocoder` through the same branch) —
confirm that and change nothing else there.

**Verify:** `dotnet run --project tools/Agent42.DevHost` starts and a chat still works; full
`dotnet test Agent42.slnx` (proves every silo still activates).

### Step 7 — Infra + docs
Files: `infra/main.bicep`, `infra/modules/container-app.bicep`, `.github/workflows/deploy.yml`,
`.agentic/decisions/0012-user-location-google-geocoding.md`, `.agentic/gotchas.md`, `CLAUDE.md`.

`GOOGLE_MAPS_API_KEY` GitHub secret → `@secure()` Bicep param → Container App **secret** →
`Geocoding__GoogleApiKey` env var. **Do not** copy `SEARXNG_SECRET`'s `@minLength(16)` availability
guard: an absent key here must NOT fail the deployment — it degrades to coordinates-only (allow an
empty default). Unlike the SearXNG secret, this key is **not** on the availability path.

Write **ADR 0012** (`/decide`) covering, at minimum:
- the client-side-API / server-side-tool constraint, and why the position must ride the chat request
  (there is no way to ask the browser mid-turn on a one-way NDJSON stream);
- Google chosen because the user already holds a key — *not* on privacy grounds;
- the accepted costs, stated plainly in the spirit of ADR 0011's "self-hosted ≠ private" section: the
  coordinates **leave our infrastructure** (Google), the address is **persisted at full precision** and
  re-sent every later turn, and it therefore **raises the value of the ADR 0011 exfiltration channel**
  that the `websearch` skill only *mitigates*;
- why this tool is not an SSRF sink, and the two properties (zero model-supplied parameters, config-pinned
  host) that the claim depends on — pinned by AC13;
- why it is a tool and not always-on context (the ADR 0008 / ADR 0011 line — location is not needed on
  "hi").

Add **gotchas** entries for: (1) the turn-scoped field that must be *explicitly* cleared because it lives
for the activation, not the call; (2) the not-an-SSRF-sink asymmetry and what keeps it true; (3) the
WebKit Permissions-API gap and the two-legged authorization gate. Update the `CLAUDE.md` repo-notes
bullets with the new **manual prerequisite**: a Google Cloud API key with the **Geocoding API** enabled,
restricted to that API, stored as the `GOOGLE_MAPS_API_KEY` GitHub secret.

**Verify:** `dotnet format Agent42.slnx --verify-no-changes` + full `dotnet test Agent42.slnx`;
`az bicep build` on the changed templates.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| AC1 | A turn that carries a position lets `get_current_location` return coordinates **and** the reverse-geocoded address | `GetCurrentLocation_ReturnsAddress_WhenTurnCarriesPosition` | `tests/Agent42.Tests/AgentGrainTests.cs` |
| AC2 | With no position on the turn, the tool tells the model the user hasn't shared it (and never invents one) | `GetCurrentLocation_WhenNotShared_TellsModelToAskTheUser` | `AgentGrainTests.cs` |
| AC3 | The position is turn-scoped: a later turn without one does **not** see the previous turn's fix, and it is absent from persisted grain state | `Location_IsTurnScoped_AndNotPersisted` | `AgentGrainTests.cs` |
| AC4 | The reminder path has no position (a scheduled prompt never claims to know where the user is) | `ReceiveReminder_HasNoUserLocation` | `AgentGrainTests.cs` |
| AC5 | The tool's return value is budgeted and cannot blow the 2 MB session cap (extend the existing test) | `ToolResults_DoNotBlowTheSessionSizeBudget` | `AgentGrainTests.cs` |
| AC6 | `GoogleGeocoder` parses a real HTTP response into `GeocodedPlace`, over a loopback socket | `Reverse_ParsesFormattedAddress` | `tests/Agent42.Tests/GoogleGeocoderTests.cs` |
| AC7 | `ZERO_RESULTS`, an error status, and a non-2xx each return null instead of throwing into the turn | `Reverse_DegradesToNull_OnZeroResultsAndErrors` | `GoogleGeocoderTests.cs` |
| AC8 | The API key never appears in a `GeocodedPlace` or in a log message | `Reverse_NeverLeaksTheApiKey` | `GoogleGeocoderTests.cs` |
| AC9 | `POST /api/chat/{id}` forwards a valid `location` to the grain, and drops an out-of-range one | `Chat_ForwardsLocation` / `Chat_DropsOutOfRangeLocation` | `tests/Agent42.Tests/AgentApiTests.cs` |
| AC10 | With no key configured, the app runs and the tool returns coordinates with no address | `CoordinatesOnlyGeocoder_ReturnsNoAddress` | `tests/Agent42.Tests/GeocoderTests.cs` |
| AC11 | The frontend never calls `getCurrentPosition` on a send path, and never awaits a fix there — it attaches only an already-cached one | `App_NeverBlocksSendOnGeolocation` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| AC12 | Capture is authorized by *either* leg (Permissions API `granted`, or the post-grant `localStorage` flag on WebKit), and a denial clears the flag | `App_AuthorizesCaptureOnBothLegs` | `PwaAssetsTests.cs` |
| AC13 | `get_current_location` exposes **no parameters** to the model (the SSRF-sink argument depends on it) | `GetCurrentLocation_ExposesNoParameters` — assert the `AIFunction`'s JSON schema has no properties | `AgentGrainTests.cs` |
| AC14 | A stale fix is never sent (older than the freshness ceiling → omitted) | `App_DropsStaleFix` | `PwaAssetsTests.cs` |

AC11/AC12/AC14 are **source-text heuristics** — there is no JS runner in this repo. They prove the code
*says* the right thing, not that a browser *does* it. Real verification is a manual check on desktop,
on an installed **Android** PWA, and on **iOS/Safari** (whose Permissions-API gap is the whole reason
for the second leg). The Web Speech gotcha is the precedent: desktop and mobile engines diverge, and
only the device tells you.

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time (2026-07-13):

- `new_issues: 0`
- `new_security_issues: 0`
- `duplication_pct_max: 3`
- `coverage_new_code_pct_min: 80`
- `cognitive_complexity_max: 15`
- `vulnerable_packages: 0` (hard block)
- Branch: **not** `main` (protected) — `feature/user-location-tool` + MR.
- CI hard-blocks on: build, tests, `dotnet format`, vulnerable packages, doc-path integrity.

## Out of scope

- **Any use of location beyond the tool returning it** — no weather integration, no "near me" search
  wiring, no map rendering. The model can already combine the tool's output with `web_search`.
- **Location history / a "home address" preference.** `IUserProfileGrain` (ADR 0005) would be the place;
  not now.
- **Forward geocoding** ("where is X?") and distance/route calculation.
- **A per-conversation or per-agent location toggle** — explicitly rejected by the user; the browser's
  own site permission is the off switch.
- **Rounding/fuzzing coordinates** — the user chose full precision.
- **IP-based fallback** when the browser has no fix. It would silently return a datacentre's location,
  which is worse than saying "I don't know where you are".
- **Closing the ADR 0011 exfiltration channel.** This plan *extends the skill instruction* to cover the
  user's location and *records* that the channel's value went up. Actually closing it (egress filtering,
  a query allowlist) is a separate, larger piece of work.

## Divergence from the plan (written at execution time)

Implemented on `feature/user-location-tool`. All 7 steps and all 14 ACs landed. Where the
implementation departed from the text above:

1. **Frontend asset tests live in a new `tests/Agent42.Tests/LocationAssetsTests.cs`, not
   `PwaAssetsTests.cs`** (which the AC table and Step 5 named). `PwaAssetsTests` guards PWA
   *installability* (manifest, service worker, icons); `SpeechInputAssetsTests` is the real exemplar for
   composer-feature wiring, and this file copies its shape. The verify command is therefore
   `dotnet test --filter LocationAssets`, not `--filter PwaAssets`. (The `critic` pass on the diff
   endorsed this.) The tests also go further than planned: a `Body()` helper extracts a single
   function's source between its own braces, so `App_NeverBlocksSendOnGeolocation` can assert
   `getCurrentPosition` is absent *from `send()` specifically* rather than merely "somewhere in the
   file" — a real bind instead of a whole-file heuristic.

2. **AC3's persisted-state assertion was rewritten, because the planned one was false.** The plan implied
   no coordinates should reach persisted state. They do, and legitimately: the tool's *return value* is a
   `FunctionResultContent`, which is chat history, and the user's own decision 3 was full precision,
   persisted. What must never exist is a location field on `AgentGrainState`, so that is what the test
   asserts. Relatedly, AC3/AC4 assert on the turn's own `ToolCallEnd` event rather than the reply text:
   `FakeChatClient` echoes *every* tool result the model can see, and by turn 2 it legitimately sees turn
   1's replayed from history — so a reply-text assertion would have been testing the fake, not the field.

3. **`ChatAsync` delegates to a private `ChatCoreAsync` iterator** rather than wrapping its whole body in
   `try`/`finally` in place. Same guarantee (the `finally` still runs on the enumerator's disposal, so an
   abandoned stream still clears the fix), far smaller diff. Honest note on the two clears the plan
   demanded: **each masks the other** in the happy path — the `finally` alone already leaves the field
   null by the time a reminder fires, and the `ReceiveReminder` clear alone would cover the reminder path
   anyway. `ReceiveReminder_HasNoUserLocation` fails only when **both** are removed (verified by
   mutation). They are kept as genuine defence in depth: the `finally` may not run promptly if an
   enumerator is leaked rather than disposed, and PII should not sit in memory between turns.

4. **The OTel question the plan told me to "confirm" was settled by a spike, and the answer was "already
   safe".** `OpenTelemetry.Instrumentation.Http` 1.15.1 redacts an HttpClient span's query string by
   default (observed: `url.full = http://…/maps/api/geocode/json?*`), so the API key and the coordinates
   do **not** reach the collector and no `url.full` scrubbing was needed. Because that is a library
   default an env var can switch off, it is now a gotcha rather than nothing.

5. **`get_current_location` returns an absolute `capturedAt`, not the planned `fixAgeMinutes`.** A
   *relative* age is frozen into permanent chat history and re-sent every turn, so it decays into a lie:
   a fix that was six seconds old when the tool ran would still report "0.1 minutes" three hours and ten
   turns later — precisely the stale-position confidence the field existed to prevent. An absolute
   timestamp stays true, and the model has `get_date` to compute against it. (Found by the `critic` pass
   on the diff.)

6. **The tool takes a `CancellationToken`** — which is *not* a breach of the zero-parameter security
   property: `AIFunctionFactory` supplies it from the invocation context and keeps it out of the JSON
   schema, so the model can neither see nor set it. AC13 therefore asserts on the **schema** (no
   properties), which is the actual claim, rather than on the CLR signature.

7. **Two extra tests beyond the AC table**, both closing gaps the diff `critic` found: the
   `GeoToolLimits` cap was pinned by nothing (`Reverse_CapsTheFormattedAddressAndPlaceNames` now feeds an
   oversized address and asserts the cap binds, mirroring `SearxngWebSearchTests`), and AC10's mapped
   test only checked that `CoordinatesOnlyGeocoder` returns null — it never exercised the *tool* against
   it, so the entire "a missing key degrades, it does not take the app down" claim had no behavioural
   test. `GetCurrentLocation_WithoutAGeocodingKey_ReturnsCoordinatesWithoutAnAddress` runs the tool on a
   silo with the real `CoordinatesOnlyGeocoder`.

8. **Carried in at the user's explicit instruction: an unrelated `infra/searxng/settings.yml` change**
   (enables `google`, drops `wikipedia`, and flips `safe_search: 1 → 0`). Paying its doc debt uncovered a
   live bug: `SearxngWebSearch` hardcoded `&safesearch=1` on every request, and a per-request value
   **overrides the instance setting** — so `safe_search:` in `settings.yml` was dead config, and the
   user's intended flip would have done nothing. The client no longer sends the parameter, so the
   instance owns the policy. Recorded in gotchas; `SKILL.md`, ADR 0011 and the gotchas engine list were
   all corrected to match the new engine set.

## Review

A `critic` pass reviewed the first draft against the knowledge files and the code. It found eight
defects; **all eight were accepted and fixed** in the text above. Nothing was rejected.

A second `critic` pass reviewed the **diff** before the MR (as `/execute-plan` step 5 requires). It found
eight more; all eight were accepted and fixed — items 5, 6, 7 and 8 above, plus a stale `SKILL.md` engine
list, a gotcha still saying "both web clients" when there are now three, trailing whitespace in
`settings.yml`, and — the one that would have hard-blocked CI — the ADR's
`Implemented under .agentic/specs/completed/…` line pointing at a path that only exists once this spec is
moved there. It confirmed the exemplar shapes, the apostrophe-escaping trap, the Bicep optionality, and
all three silo configurators as clean.

| # | Finding | How it was fixed |
|---|---|---|
| HIGH 1 | Threat model stopped at Google; missed that the repo's *own* documented `web_search` / `fetch_web_page` exfiltration channels (ADR 0011) would now drain a conversation containing the user's street address — and no skill file was being touched | Added the PII constraint; `Skills/websearch/SKILL.md` is now in affected areas and in Step 3; ADR 0012 must record that this feature *raises the impact* of the ADR 0011 channel |
| HIGH 2 | "Left null on the `ReceiveReminder` path" was **factually wrong** — a private field lives for the *activation*, not the call, so a reminder after a located turn would reuse that fix. The plan's own AC4 would have failed. The "non-reentrant ⇒ safe" claim was also unevidenced for an `IAsyncEnumerable` grain method | Step 3 now demands an **explicit clear** (`finally` in `ChatAsync`, null on entry to `ReceiveReminder`); the constraint spells out why non-reentrancy is not the argument |
| HIGH 3 | WebKit has **no `permissions.query({name:"geolocation"})`**, so gating auto-attach on `state === "granted"` made the feature **dead on iOS** — a grant button that never hides and never attaches | Derived decision (a) rewritten as a **two-legged gate** (Permissions API, or a post-grant `localStorage` flag cleared on `PERMISSION_DENIED`); AC12 covers it; device verification called out |
| MED-HIGH 4 | The coordinates **and the API key** would ride the outbound HTTP span: `AddHttpClientInstrumentation` is on, and `UserTelemetrySpanProcessor` stamps user identity onto every span (ADR 0010) — joining a named human to exact coordinates at the collector. The named exemplar, `SearxngWebSearch`, logs its query | New constraint + a Step 2 requirement to verify query-string redaction and scrub `url.full` if absent |
| MED 5 | Three silo configurators construct `AgentGrain`; only one was listed. `CapSizedWebPageFetcher.cs` registers deps **inline** (no `AddWebToolFakes`) — and AC5 runs on it, so it would fail to activate | Added to affected areas and Step 6 |
| MED 6 | The "not an SSRF sink" claim rests on two properties (zero model-supplied parameters, pinned host) that **nothing pinned** — a later `string address` parameter would silently make it one | Added **AC13** (`get_current_location` exposes no parameters) and tightened the constraint |
| MED 7 | An awaited `getCurrentPosition` in the send path added **up to 5 s to every message**, GPS cold-start on mobile; "errors don't block" did not cover the *wait* | Derived decision (b): **background capture + cached fix**, send path never awaits; AC11/AC14 |
| LOW 8 | `latlng={lat},{lng}` is culture-sensitive — `52,3676` under `nl-NL` breaks the query, and it breaks on the *dev machine* while working in the container | Step 2 mandates `CultureInfo.InvariantCulture`; AC6 asserts on the **request URI** the test server received |

The critic separately confirmed, and I re-verified: `IAgentGrain.ChatAsync` has exactly one production
caller (`AgentApi.cs:76` — there is no `/api/agents/run` endpoint), the frontend has exactly one send
path (`app.js:768`), trailing-optional params keep every positional caller compiling, tool chips render
`toolName` verbatim (no label map to extend), and the quality-gate numbers match
`.agentic/quality-gates.yaml`.
