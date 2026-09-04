---
id: 0053
date: 2026-08-26
status: superseded
superseded_by: 0063
tags: [tools, calendar, oauth, google, egress, prompt-injection, per-user, secrets]
supersedes: []
extends: [0012, 0013, 0018, 0021, 0039, 0040]
amends: []
---

# Six native Google Calendar tools over a per-user OAuth connection

## Context
Agent 42 can schedule a durable reminder *for itself* (ADR 0021's `scheduling`), and that is the whole
of its relationship with time. It cannot see what is actually on the user's calendar, and it cannot put
anything there. Every "am I free Thursday" ends with the user going and looking.

Three shapes were considered. The user asked first for a **co-hosted first-party MCP server** in the
same process, then reversed that in the same round of conversation ("forget about it, make it a tool
instead of mcp server" / "replace the mcp with tools"). A **third-party remote MCP server** added
through ADR 0024 was the other option, and was rejected with it. Both rejections are recorded below so
the reversal stays legible later; **neither is to be relitigated.**

Native tools are also the cheapest shape available: they reuse `IGeocoder`'s port/adapter pattern
exactly, and add no new transport, no new process and no new protocol.

## Decision
Six model-invoked tools — `list_calendars`, `list_calendar_events`, `get_calendar_event`,
`create_calendar_event`, `update_calendar_event`, `delete_calendar_event` — over the Google Calendar
REST API v3, authorized by a **per-user** Google OAuth connection the user makes once from
**Preferences → Calendar**.

Five decisions taken with the user, up front:

1. **Native tools, not MCP** — see above.
2. **Per-user OAuth**, not an operator refresh token and not a service account. Each user connects
   their own Google account; tokens are stored per user, DP-encrypted, and refreshed at call time.
3. **Full read + write, with no confirmation gate.** `create` / `update` / `delete` are model-invoked
   directly, like every other tool here. The residual is recorded honestly below, not mitigated.
4. **Multiple calendars.** `list_calendars`, plus an optional `calendarId` on every other tool
   defaulting to `primary`.
5. **Attendees/invitations and recurring-series editing are out of scope.**

### Shape
`ICalendarService` in `src/Agent42.Agents/Calendar/` is the port; `GoogleCalendarService` in
`src/Agent42.Backend/Calendar/` is the adapter; `InMemoryCalendarService` is a **working** canned
calendar for DevHost and tests. `AgentGrain` gains a twelfth constructor dependency and an eleventh
partial (`src/Agent42.Agents/Calendar/AgentGrain.CalendarTools.cs`, ADR 0039); `MapCalendarEndpoints` is the fourteenth
endpoint group (ADR 0040). Both projects gain a `Calendar/` feature folder — the eighteenth and
nineteenth — and, per ADR 0039, both keep the **flat namespace** the folders deliberately disagree with.

### The credential never crosses the port
`IGeocoder` is user-agnostic; a calendar is not. So every `ICalendarService` method takes `userId`
first, and the **Backend** adapter resolves, decrypts and refreshes the token itself via
`IGrainFactory` + `IGoogleSecretProtector` — the `UserMcpToolProvider.ResolveOAuthAccessTokenAsync`
shape, including its 2-minute refresh skew and its persist-the-refreshed-bundle step. `AgentGrain`
reads `_userId` from its own grain key and never sees a token.

`CalendarPort_ExposesNoCredential` pins this by **assertion over the port's reflected surface**, not by
comment — the `GetCurrentLocation_ExposesNoParameters` discipline. A later contributor adding an
`accessToken` parameter "just for the adapter" would otherwise pass review.

### One adapter, self-reporting — no `NotConfiguredCalendarService`
There is deliberately no twin of `CoordinatesOnlyGeocoder`. `GoogleCalendarService` is registered
unconditionally and returns the same curated `NotConnected` result whether the *deployment* has no
client credentials or *this user* has not connected. Two types would duplicate one message, because the
remedy is identical: connect in Preferences → Calendar. This is the `ContextWebPageFetcher` shape (ADR
0027 — a blank key degrades per call), not the `IGeocoder` two-type shape.

`InMemoryCalendarService` is DevHost/tests **only** and must never be registered by
`src/Agent42.Backend/Program.cs`. A fake calendar answering in production would have the model
confidently report meetings that do not exist — far worse than an honest "not connected".

### Google is a confidential client, and its endpoints are constants
A Google **Web application** OAuth client requires `client_secret` on the token exchange, so this needs
**two** config values where the MCP flow (a public, PKCE-only client) needed one. PKCE is still added
as defence in depth, with `GooglePkce` a deliberate **copy** of `McpPkce` rather than a reuse — the
same preference ADR 0035 exercised when `ShareToken` copied `ApiKey`, and for the same reason: coupling
`Calendar/` to an `Mcp`-named type to save thirty lines makes one flow's evolution a constraint on the
other's.

The authorization and token endpoints are documented constants, so **PRM/RFC-8414 discovery is skipped
entirely** — the one part of ADR 0025's client that does not carry over. But they are **constructor
values with the Google URLs as pinned defaults**, not hard-codes, because `McpOAuthClient` is testable
against a fake authorization server precisely by virtue of its endpoints being data. Hard-coding would
have left the exchange and refresh paths either untested or dependent on the live internet from CI.
Production passes nothing and gets Google.

**Two wire facts are load-bearing and were confirmed against Google's web-server OAuth guide
(2026-08-26):** the authorization URL must carry **`access_type=offline` *and* `prompt=consent`**.
Without both, Google returns no refresh token on a re-consent and the connection dies at the first
access-token expiry — silently, an hour later, not at connect time. `AuthorizationUrl_RequestsOfflineAccessAndConsent`
pins both, and the callback additionally **refuses a connection that came back without a refresh
token** rather than reporting a success that will evaporate.

### The scope is the narrow PAIR, not `.../auth/calendar`
Exactly `https://www.googleapis.com/auth/calendar.events` (read/write events on **all** the user's
calendars) plus `https://www.googleapis.com/auth/calendar.calendarlist.readonly` (enumerate calendars).

An earlier draft asked for the broad `.../auth/calendar` and called it "the minimum", which was simply
false. The pair satisfies decisions 3 and 4 in full while excluding precisely the three things already
out of scope: creating calendars, deleting calendars, and changing ACL/sharing. Given decision 3 (no
confirmation gate) plus an acknowledged injection channel, that is the difference between an injected
*"delete that meeting"* and an injected *"delete that calendar"*. **Do not widen it** without recording
why.

Verified at plan time, not assumed: `calendar.events` is listed under "Authorization scopes" for
`events.insert`, `events.update` and `events.delete`; `calendar.calendarlist.readonly` for
`calendarList.list`. `events.list` / `events.get` were **not** fetched individually — they are read
methods on the same resource under a read-and-write scope, so that is an inference, and the first place
to look if either 403s in production.

No `openid`/`email` scope: the connected identity is read from the **primary calendar's id**, which for
a Google account is the account address.

### The consent screen must be *In production*
With an External user type and **Testing** publishing status, Google **revokes refresh tokens after 7
days**. The connection would work and then die weekly with no error anywhere. A sensitive scope on an
unverified app still shows the "Google hasn't verified this app" interstitial, which is acceptable for
a personal deployment but must be known.

This is a **manual prerequisite the executor cannot perform**, and it is why this ADR ships with a
written guide rather than only a spec: `docs/integrations/google-calendar/README.md`, the first entry
in a new `docs/integrations/` tree (a sibling of `docs/features/`, with per-integration subfolders).

### Listing expands recurrences, and that constrains edit and delete
`list_calendar_events` uses `singleEvents=true&orderBy=startTime`, which is what makes "what's on my
calendar tomorrow" work at all. The consequence is that every returned id is an **instance** id, so an
edit or delete against one touches **that occurrence only** — it cannot change a series. That is stated
in the tool *descriptions*, because a model that does not know it will cheerfully claim it rescheduled
a weekly meeting. `CalendarToolDescriptions_StateTheSingleInstanceLimitation` pins it.

### `sendUpdates=none` on every write
Attendees are out of scope for *writing*, but a **pre-existing** event may already carry them, and a
bare `PATCH` or `DELETE` would email real people. This is the one irreversible outward-facing side
effect in the feature, so the parameter is pinned explicitly and asserted on the **outbound request
line** by `UpdateAndDelete_NeverNotifyAttendees`.

### Errors, logs and spans are all curated
A Google error body echoes the request URL, the calendar id and event detail. The adapter catches,
discards, and returns a constant (`CalendarMessages.*`) — the ConfigHealth probe discipline. Logs carry
the status code or the exception **type**, never the message (an `HttpRequestException` message can
carry the request URI, and the URI carries the calendar id).

The **span** is the third sink and needed its own mechanism. OTel redacts an HttpClient span's *query
string* by default; it does **not** redact the path — and Calendar v3 puts the calendar id (an email
address, somebody *else's* for a shared calendar) and the event id in the path. ADR 0010 stamps
`user.email` onto every span under a request identity, so an unhandled span would join one named human
to another named human's calendar in the collector, permanently. A `CalendarSpanSuppressingHandler`
therefore **suppresses instrumentation** for the duration of each calendar send. Suppressing rather
than rewriting is deliberate: a rewrite must keep working as URL shapes grow, and timing data for a
third-party GET does not justify defending that.

### An incognito chat may still write to the calendar
ADR 0034 blocks documents (durable Cosmos chunks) and refuses scheduling (a durable reminder that would
fire the persisting, pushing path). A calendar write is **different in kind**: the durable record it
creates is *outside our system* and is the user's own explicit intent, exactly like a web search
leaving the tenant. The ephemeral guarantee is "not persisted by us, not listed", not "no side
effects". `EphemeralChat_MayStillWriteToTheCalendar` pins the **non-refusal**, so it is not later
"fixed" by analogy with `SchedulePromptAsync`.

One honest wrinkle rather than a discovery for later: a near-expiry **token refresh** during an
incognito turn writes the new bundle to `IUserGoogleGrain` — a durable write *inside* our system,
triggered by an incognito turn. It carries no conversation content, so it does not breach ADR 0034's
guarantee. It is not nothing either.

### Storage, config and infra
Two new persisted state types ride the existing `agent-state` provider on distinct state names —
`"google-credentials"` (`UserGoogleState`) and `"google-oauth-pending"` (`UserGoogleOAuthPendingState`).
**No new Cosmos container, no new RBAC step, no Bicep storage change.** Both are added to
`GrainStorageSerializerTests`' hard-coded `$type` list, with golden blobs in `GoldenStates` /
`SerializationCompatibilityTests`.

A **third Data Protection purpose**, `Agent42.GoogleCalendar.Secret.v1`, keeps Google tokens in their
own rotation scope. It brings the key-ring trap with it: in production the ring **must** persist
out-of-process (it does — the `dataprotection` blob container of ADR 0018), or a restart makes every
stored refresh token undecryptable. `Unprotect` **swallows** the `CryptographicException` and returns
null, so the failure is silent and specific: the user's calendar simply goes quiet with no error
anywhere, and the fix is to reconnect. Named here because nothing else will name it.

Config is `GoogleCalendar:ClientId` / `GoogleCalendar:ClientSecret` while the routes are
`/api/google/*` — **a deliberate asymmetry.** The section is not `Google:` because
`Geocoding:GoogleApiKey` already exists one line away, and two differently-scoped "Google" keys
adjacent in `appsettings.json` is how an operator pastes the wrong secret. The routes stay
provider-named because the redirect URI is a manually-registered constant in Google Cloud that a future
Google feature would reuse; changing it later means re-registering it by hand. **Do not "fix" the
asymmetry into matching names.**

Two new **optional** GitHub Actions secrets, `GOOGLE_OAUTH_CLIENT_ID` and
`GOOGLE_OAUTH_CLIENT_SECRET` — the `GOOGLE_MAPS_API_KEY`/`CONTEXT_API_KEY` shape (empty default, no
`@minLength`, secret and env var emitted only when supplied), explicitly **not** the `SEARXNG_SECRET`
shape whose absence takes the backend down.

### The endpoints add zero mandatory registrations
All four routes — `GET /api/google/status`, `GET /api/google/oauth/start`,
`GET /api/google/oauth/callback`, `DELETE /api/google/connection` — stay **behind Easy Auth**;
`excludedPaths` in `container-app.bicep` is **unchanged** and still holds exactly ADR 0042's four
entries. `EasyAuthExclusionTests` gains three `[InlineData]` rows, because that theory proves gating by
naming paths one at a time and would otherwise assert nothing about them. An OAuth callback reachable
outside Easy Auth would make `GetUserId()` return the local-dev principal and bind every user's tokens
to one phantom identity.

`IGoogleSecretProtector` and `GoogleOAuthClient` are resolved **optionally through `IServiceProvider`**
(the `/api/speech` pattern), never as handler parameters. `MapAgentApi` infers every endpoint's
parameters up front, so a parameter a host cannot resolve is treated as a request body and breaks
routing for that **whole host** — which `IMcpSecretProtector` once did to all thirteen `MapAgentApi`
callers at once. `ICalendarService` **is** a mandatory *grain* dependency (every silo that activates
`AgentGrain` must register it), which is four edit sites covering six silo configurators and both real
hosts.

## Consequences

**The read side is the bigger egress story, and decision 3 only covered writes.** Once
`list_calendar_events` returns, event titles, descriptions, locations and the attendee counts of
pre-existing events are in the transcript — and `list_calendars` additionally returns the ids of
**shared** calendars, which are **other people's email addresses**. From the transcript that data:

- **leaves the Entra tenant on any third-party model** (ADR 0013);
- is **drainable** through ADR 0027's allowlist-exempt followed links — the injection→**exfiltrate**
  path, at least as serious as injection→delete;
- is visible in full to anyone holding an ADR 0035 share token — mechanically "no new surfacing", but a
  genuinely new *class* of third-party personal data reaching that surface;
- is mirrored **unencrypted** into origin IndexedDB by ADR 0032;
- is persisted into the 2 MB Cosmos session forever and re-sent every later turn.

**A prompt injection can now rewrite or delete calendar entries.** Decision 3 accepted this knowingly.
An injection arriving through a fetched page (ADR 0011/0027) or an uploaded skill (ADR 0049) reaches
the same tools the user does, with no confirmation gate in between. The narrow scope pair bounds the
blast radius to *events*; nothing bounds it further.

**The tools are live on the asynchronous paths.** Because `BuildAgent` registers them, they are
available on `ReceiveReminder` and on `EnqueuePromptAsync` (`/api/agent-run`, ADR 0022). A
model-invoked **delete** can fire with no user present, from a webhook-triggered run whose prompt came
from outside the tenant. This is a consequence, not an exclusion — it is reachable whether or not it
was designed for. (What *is* out of scope is any designed bridge between ADR 0021 scheduling and the
calendar.)

**The `calendar` key lands OFF for anyone who has ever pressed Save.** `null == all` only helps a user
who has never narrowed a selection: the picker materialises "all" as the full **explicit** list, so any
stored `EnabledTools` is non-null and cannot contain a key that did not exist when it was saved. The
tools will be registered for nobody and the feature will look broken while being correct. Handled in
this order, deliberately:

1. An agent whose stored `EnabledTools` is still `null` snapshots the bare wildcard through
   `BindAgentAsync` and *does* pick the new key up. Only an explicitly-saved selection misses it — the
   same two-path divergence ADR 0048 records for skills.
2. **No migration.** ADR 0049's auto-enable hook exists because an upload is an event to hang it on; a
   shipped key has none, and rewriting every stored selection to add a key the user never chose is a
   worse precedent than one re-save.
3. **Tell the user.** After deploying: open Preferences → Tools and press **Save** once, and tick
   Calendar on any agent that needs it. This is in the MR description and in the setup guide.

`LegacyStoredSelection_DoesNotEnableCalendar_ButNullDoes` pins both halves so the behaviour reads as
designed rather than as a bug.

**Calendar spans no longer appear in the collector**, so calendar-call latency is not observable in
tracing. Accepted, for the reason above.

**Disconnecting does not revoke the grant at Google.** `DELETE /api/google/connection` removes our
stored tokens; the grant itself is withdrawn at `myaccount.google.com/permissions`. Both the UI and the
guide say so, because believing otherwise means being wrong about what access still exists.

## Alternatives rejected
- **A co-hosted first-party MCP server** exposing the same operations, in the same process. Asked for
  by the user and then reversed by them in the same round. It would have added a protocol, a transport
  and a discovery step to reach code that already lives in this process.
- **A third-party remote MCP server** added through ADR 0024. Rejected with the above. It would also
  have put a third party between the app and the user's calendar for no gain.
- **An operator refresh token or a service account.** One shared identity for a multi-user app; every
  user's calendar behind one credential.
- **A confirmation gate on writes** — explicitly rejected by the user. Recorded as the residual above.
- **The broad `.../auth/calendar` scope.** See the scope section: it buys only the three out-of-scope
  operations, and costs the blast-radius bound.
- **Reusing `McpPkce`** rather than copying it into `GooglePkce`. See above.
- **A `NotConfiguredCalendarService`** twin of `CoordinatesOnlyGeocoder`. Two types, one message.
- **A live ConfigHealth probe.** Every other probe is an operator-keyed call; a calendar probe needs a
  *user's* token and is meaningless with none connected. Static presence checks only — and two of them,
  not one derived flag, because the halves fail separately and an operator who set only the id needs to
  see which is missing.

## What the automated tests can and cannot reach
The adapter's wire behaviour is real: `GoogleCalendarServiceTests` asserts on **outbound request
lines** against a recording handler (the `GoogleGeocoderTests`/`ContextWebPageFetcherTests` shape), so
`singleEvents`, `orderBy`, `sendUpdates=none`, the refresh-then-call sequence and the curated errors
are all genuinely pinned. The OAuth flow runs end to end against a fake authorization server. The tool
half runs through a real Orleans cluster and a real `BuildAgent`.

The frontend assertions in `CalendarAssetsTests` are source-text heuristics — the repo has no JS runner
by design. They prove the code *says* the right thing, never that a browser does it.

Nothing here touches Google. That is Step 12.

## The manual pass: one leg run, one leg honestly unverified
**(a) The DevHost leg was run**, and these are the measured values — headless Chrome over the
DevTools protocol against `http://localhost:61326` (throwaway driver, deliberately not committed),
the ADR 0044/0049 method. Recorded here rather than only in an MR description, so the claim stays
falsifiable after the MR is merged and forgotten.

- **The module loads and is wired.** `import("/js/calendar.js")` resolved with exactly
  `initGoogleOAuthReturn, loadGoogleStatus, wireCalendar`.
- **Tab order.** `#profileForm [role="tab"]` read
  `[tabGeneral, tabTools, tabProviders, tabMcp, tabConfig, tabSkills, tabCalendar]` — Calendar at
  index **6**, MCP still at **3**. That is AC14 measured rather than only source-text asserted.
- **The unconfigured panel is honest.** With no `GoogleCalendar:*` set, the panel rendered
  status `not available`, the note *"This deployment has no Google Calendar client configured, so
  there is nothing to connect to."*, and **both** buttons hidden — no Connect button offering a flow
  that would 409. `GET /api/google/status` returned
  `{"configured":false,"connected":false,"accountEmail":null,"connectedAt":null}` and
  `GET /api/google/oauth/start` returned **409**.
- **The picker carries the key.** Preferences → Tools listed
  `[web_search, location, scheduling, code_interpreter, calendar, skill:scheduledprompt,
  skill:websearch]`, with `calendar` present, labelled **"Calendar"**, and checked (this profile had
  never saved a narrowed selection — the `null == all` path, which is exactly the half that still
  works for a new user).
- **The tools round-trip end to end through real chat turns**, observed as tool chips in the thread:
  `list_calendars` returned both canned calendars; `create_calendar_event` returned
  `{"created":{"Id":"52f4e23d…","Title":"Lunch with Sam",…}}`; `list_calendar_events` then showed
  **2** events; `update_calendar_event` on that id returned `"Title":"Lunch with Sam (moved)"`;
  `delete_calendar_event` returned `{"deleted":true,…}`; and the final `list_calendar_events` showed
  **1** — the created event gone. Six chips, all `is-done`.

Two honest notes on that leg. `get_calendar_event` is **not** in it: DevHost's `CannedChatClient`
has no trigger for it (the test fake does), so the offline round-trip is create → list → update →
delete. And the whole leg runs against `InMemoryCalendarService` — it proves the tool path, the
picker, the tab and the UI, and nothing whatsoever about Google.

**(b) The real Google leg was not, and cannot be from here.** It needs the Step 0 prerequisites — a
Google Cloud project, a published consent screen and a registered redirect URI — none of which the
executor can create. The consent screen, the code exchange, the refresh after an hour, the seven-day
revocation behaviour and every real 403 are **unverified residuals**, smoke-testable only in
production. This is the ADR 0043 treatment (say plainly what was not run), not the ADR 0049 one.
