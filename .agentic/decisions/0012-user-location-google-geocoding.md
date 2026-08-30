---
id: 0012
date: 2026-07-13
status: accepted
tags: [location, tools, privacy, pii, geocoding, prompt-injection, frontend, container-apps]
---

# The user's location as a function tool, carried in on the chat request

## Context
Agent 42 could not answer "what's the weather here", "anything good to eat near me", or "what's my
address". It had no idea where the user was — on the phone in their pocket or the laptop on their desk.

The whole design turns on one constraint, and everything else follows from it:

**`navigator.geolocation` is a browser API, and tools run server-side inside `AgentGrain`.** A tool
cannot go and ask the browser for a fix mid-turn — the chat path is a **one-way NDJSON stream** from
grain to client (`AgentApi.cs`), so there is no channel back. There is no version of this feature where
the server pulls the position on demand.

So the position must **ride the chat request into the turn**, and the tool reads it from turn-scoped
state. That is the same shape attachments already use (`ChatRequest.AttachmentIds` → `ChatAsync(…,
attachments)`), and it is the only shape available.

## Decision
A model-invoked tool, **`get_current_location`**, returning the user's coordinates and — when it can be
resolved — a reverse-geocoded street address. The position is captured by the browser, sent on
`ChatRequest.Location`, held in a **turn-scoped field** on the grain, and never persisted as grain state.
Reverse geocoding goes through the **Google Geocoding API** behind an `IGeocoder` port, with an
in-memory twin (`CoordinatesOnlyGeocoder`) for DevHost, the AppHost and tests.

**Google was chosen because the user already holds a key — explicitly not on privacy grounds.**
Nominatim/OSM and Azure Maps were the alternatives. This is a cost-and-friction argument, not a
confidentiality one, and it must not be restated as the latter. See the costs below.

## Why a tool, and not always-on context
ADR 0008 chose **always-on** `TextSearchProvider` retrieval for *documents* and explicitly rejected an
on-demand tool there. ADR 0011 already drew the line that resolves the apparent tension: always-on is
right when the data is small, local, free and known-relevant every turn; a tool is right when the data is
none of those. Querying the user's position on "hi" would be absurd — and would mean a geolocation lookup
and a Google round-trip on every message. Location is squarely on the ADR 0011 side of the line. This
extends the established split rather than reopening it. All three ADRs stand.

## The costs, stated plainly
In the spirit of ADR 0011's "self-hosted ≠ private" section, the point of this section is that none of
this is hidden behind a comfortable phrase.

1. **The coordinates leave our infrastructure.** Every reverse geocode sends the user's exact position to
   **Google**, authenticated with our API key. There is no local geocoding here. Accepted.
2. **The address is persisted at full precision, and re-sent to the model on every later turn.** The tool's
   return value is a `FunctionResultContent`, which stays in chat history (ADR 0004 / ADR 0011) — so the
   user's street address sits in the conversation forever, and goes back to Azure OpenAI with every
   subsequent message in that conversation, long after the turn that asked. The user chose this
   explicitly (full precision, persistence, no rounding or fuzzing). Accepted.
3. **This raises the value of the ADR 0011 exfiltration channel, and does not close it.** That is the
   consequence that deserves the most honesty, so it gets its own section.

### It raises the stakes of an outbound channel we have already accepted
ADR 0011 recorded two acknowledged, *unclosed* outbound text channels: the **model writes the
`web_search` query**, which the `fetch_web_page` URL allowlist does not cover and which SearXNG forwards
on to DuckDuckGo/Bing (and Google, since ADR 0060); and a fetched page's text can instruct the model to act on it.

Before this feature, the worst thing draining those channels was conversation text and document excerpts.
**After it, the conversation may hold the user's street address.** The feature does not open a new hole —
it makes the existing one worth more.

The control is the same one ADR 0011 named, extended: `src/Agent42.Agents/Skills/websearch/SKILL.md` already told the model
never to put conversation contents in a search query, and now **names the user's location explicitly** —
never in a query, never in a URL, search for the *place* and not the person standing in it. That is
**mitigation, not prevention**, exactly as it was before. Actually closing the channel (egress filtering,
a query allowlist) remains separate, larger work and was not undertaken here. Recorded rather than
papered over.

## Consequences

### The turn-scoped field lives for the ACTIVATION, and must be cleared explicitly
The position is deliberately **not** in `AgentGrainState`: a persisted fix would outlive the turn it was
captured for, and the model would confidently report where the user was last week. It rides the grain
call into a private field.

The trap — and the first draft of the plan had it — is thinking that a private field is naturally
per-call. **It is not: it lives for the grain activation.** A reminder firing on the same activation as an
earlier located chat turn would still see that turn's fix, and a scheduled prompt would announce the
user's position hours later, from somewhere else entirely. Nor does non-reentrancy save you: `ChatAsync`
is an `IAsyncEnumerable` method that suspends between `yield return`s.

So the clear is explicit, in two places: a **`finally` in `ChatAsync`** (which also runs if the client
abandons the stream, on the enumerator's disposal), and an assignment to **null on entry to
`ReceiveReminder`**. `ReceiveReminder_HasNoUserLocation` fires a reminder whose own prompt asks where the
user is, and asserts the tool says it does not know — it fails if both clears are removed.

### `get_current_location` is NOT an SSRF sink — and the reason is narrow, so it is pinned by a test
It makes an outbound HTTP call from the same container whose managed identity IMDS will mint Cosmos,
Storage and Azure OpenAI tokens for. That is precisely the setup that made `fetch_web_page` dangerous
(ADR 0011). It is nevertheless **not** an SSRF sink, and must **not** grow an SSRF address-guard — the reason is
that the host is fixed and the tool takes no model-supplied parameters, not that some other tool is
guarded (ADR 0027 later removed `fetch_web_page`'s guard entirely, so there is no asymmetry left).

Two properties make the claim true, and **both are load-bearing**:

1. The host is **fixed** — `maps.googleapis.com`, config-pinned, never model-supplied.
2. The tool takes **zero model-supplied parameters**. The only variable input is a lat/lon pair from the
   *browser*, range-validated server-side, and a `double` cannot inject a host.

The danger is that these are exactly the sort of properties a later contributor dissolves without
noticing: adding a `[Description("address to look up")] string` parameter would turn this into an SSRF
sink *with the reassuring "not an SSRF sink" comment still sitting above it*. So the claim is pinned by
**`GetCurrentLocation_ExposesNoParameters`**, which asserts the JSON schema the model actually sees has no
properties at all. A comment would not have survived; a test does.

### The tool's return value is budgeted, like every other tool's
A raw Google Geocoding response is a large JSON document — several candidate results, each with every
address component and its bounding geometry. **None of it is returned.** The tool hands back a compact
record (coordinates, formatted address, locality, country, fix age), and the formatted address is capped
in `GeoToolLimits` (300 chars, matching `WebToolLimits`' snippet cap). Same reason as ADR 0011: the return
value is persisted into a single 2 MB Cosmos item and re-sent every turn.

### The key and the coordinates stay off the logs and off the telemetry span
`UserTelemetrySpanProcessor` stamps `user.email` / `user.full_name` / `user.roles` onto **every** span
started under a request identity (ADR 0010), and `AddServiceDefaults` enables
`AddHttpClientInstrumentation()`. So the outbound geocode span joins *this named human* to *this outbound
call* — and the API key and the exact coordinates both ride in that request's **query string**.

A spike settled the actual behaviour rather than trusting it: **OpenTelemetry.Instrumentation.Http 1.15.1
redacts the query string by default**, recording `url.full` as
`http://…/maps/api/geocode/json?*`. Verified output, not a recollection. So no scrubbing is needed today —
but it is a *library default* that the `OTEL_DOTNET_EXPERIMENTAL_HTTPCLIENT_DISABLE_URL_QUERY_REDACTION`
env var can switch off, which would immediately start shipping the key and the user's coordinates to the
collector. Recorded in gotchas.

Separately, `GoogleGeocoder` logs neither the coordinates, the key, nor the request URI — including on the
failure path, where an `HttpRequestException`'s message can itself embed the URI. The named exemplar,
`SearxngWebSearch`, **logs its query**; copying it verbatim would have logged the user's position.

### Latency: nobody waits for a GPS fix
An awaited `getCurrentPosition({enableHighAccuracy: true})` in the send path would add **up to several
seconds to every message** once the cached fix went stale — a cold GPS acquisition on a phone is not fast.
Typing "hi" must not pay for a GPS spin-up.

So the browser keeps a **last-known fix refreshed out of band** (on grant, on `visibilitychange` → visible,
and on an interval while the tab is visible), and the **send path reads that cache synchronously and never
awaits anything**. `capturedAt` rides along; a fix older than **30 minutes is dropped rather than sent**, so
the model is never told the user is somewhere they left. No geolocation outcome — denied, timed out,
unsupported, absent, stale — may block or delay a chat.

Server-side, the geocode *is* on the turn's critical path (the grain is non-reentrant), so its named
`HttpClient` carries an explicit **5s timeout** and calls **`RemoveAllResilienceHandlers()`** — without
which `AddServiceDefaults`' standard handler would silently add three retries and multiply the stall
(ADR 0011's lesson, and the reason that gotcha exists).

### The authorization gate has two legs, because one of them does not exist on iOS
"Auto-attach whenever the browser has already granted permission" (the user's decision — no
per-conversation toggle; the browser's own site setting is the off switch) still needs a *first-time*
affordance: a permission that is never requested is never granted, and firing `getCurrentPosition()` on an
ordinary send would ambush the user with a browser prompt. Hence a one-tap grant control — a *grant*
button, not a toggle, hidden once authorized.

> **Amendment (2026-07-17, `plan-preferences-tabs-location`):** the grant control moved from the
> composer toolbar ("Share my location") into **Preferences → General** (an `Enable` button + status
> line), at the user's request. This is a *placement* change only — the two-legged authorization, the
> gesture-only prompt, the out-of-band cache warming, and the never-block-send accessor below are all
> unchanged. The composer no longer carries a location affordance.

The trap: **`navigator.permissions.query({name: "geolocation"})` is not supported in WebKit** — i.e. every
browser on iOS/iPadOS — where it **rejects**. A design that gates auto-attach purely on
`state === "granted"` is therefore **dead on iOS**: the user grants, no fix is ever attached, and the grant
button never hides. (The plan's first draft had exactly that; a `critic` pass found it.) So:

- **Permissions API present** (Chrome/Edge/Firefox, desktop + Android): attach when `state === "granted"`,
  and subscribe to the status' `change` event.
- **Permissions API absent/rejecting** (WebKit): fall back to a `localStorage` flag set **only after a
  `getCurrentPosition()` has actually succeeded**. An already-granted permission resolves without
  re-prompting, so capturing under that flag never ambushes anyone; a `PERMISSION_DENIED` on any later
  capture **clears the flag** (revoked in site settings) and brings the grant control back.

`getCurrentPosition()` is never called on a send path under either leg. This is **device-verifiable only**
— the C# asset tests are source-text heuristics (no JS runner in the repo, by design), so they prove the
code *says* the right thing, not that a browser *does* it. The Chrome-on-Android Web Speech gotcha is the
precedent: engines diverge, and only the device tells you.

### Operational
- **Manual prerequisite, and deliberately NOT on the availability path.** A Google Cloud API key with the
  **Geocoding API** enabled (and restricted to it), stored as the **`GOOGLE_MAPS_API_KEY`** GitHub Actions
  secret. Unlike `SEARXNG_SECRET` (ADR 0011), an absent key **must not fail the deployment**: there is no
  `@minLength` guard, the Bicep parameter defaults to empty, and the secret and its env var are then **not
  emitted at all**. The backend registers `CoordinatesOnlyGeocoder`, logs once, starts normally, and the
  tool still reports coordinates — it just cannot name the street. A degradation, not an outage.
- **No new RBAC.** The key is a Container App secret; nothing in Azure IAM changes.
- The tool is on for every chat — no per-conversation or per-agent toggle (explicitly rejected by the user).

## Out of scope
Weather/"near me" wiring (the model can already combine this tool with `web_search`), location history or
a stored home address (`IUserProfileGrain`, ADR 0005, would be the place), forward geocoding and routing,
coordinate rounding/fuzzing (the user chose full precision), and an IP-based fallback — which would
silently return a datacentre's location, which is worse than saying "I don't know where you are".

Implemented under `.agentic/specs/completed/plan-user-location-tool.md`.
