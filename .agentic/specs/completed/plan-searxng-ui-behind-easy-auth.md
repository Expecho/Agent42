# Expose the SearXNG web UI through an Easy-Auth-gated backend proxy

planned-against: main @ c86fa93

## Overview

The SearXNG sidecar already serves its full HTML search UI (`settings.yml` lists both `json` and
`html` under `search.formats`), but nothing outside the replica can reach it: port 8888 has no
ingress. Add a backend reverse-proxy route, `/searxng/{**path}`, so a signed-in user can open the
search UI in a browser — inheriting Easy Auth from the app's single ingress rather than publishing a
second, unauthenticated port.

Why now: the sidecar is a black box today. When `web_search` returns something odd, there is no way
to see what the instance actually returns, which engines answered, or whether an engine is failing —
`GET /api/config-health`'s probe reports reachability and nothing more.

## Affected areas

Verified against the tree at `c86fa93`.

| Path | Change |
|---|---|
| `src/Agent42.Backend/Web/AgentApi.Web.cs` | **new** — 16th `AgentApi` partial, `MapWebEndpoints` |
| `src/Agent42.Backend/Web/SearxngProxy.cs` | **new** — header/cookie/redirect translation, unit-testable without a host |
| `src/Agent42.Backend/Api/AgentApi.cs` | add `MapWebEndpoints(app);`; fix the "56 route registrations" prose (line 12) |
| `src/Agent42.Backend/Program.cs` | **new** named client `searxng-ui` (see Step 3 — reuse is not viable) |
| `infra/modules/container-app.bicep` | add `SEARXNG_BASE_URL` to the sidecar's `env` |
| `src/Agent42.AppHost/AppHost.cs` | same env var for the local full-stack run |
| `src/Agent42.Backend/wwwroot/sw.js` | **`/searxng` guard beside the `/api` one**, and `CACHE` bump `-v24` → `-v25` |
| `src/Agent42.Backend/wwwroot/js/confighealth.js` | render the "Open search UI" link |
| `src/Agent42.Backend/wwwroot/index.html` | the anchor in the Configuration tab |
| `src/Agent42.Backend/wwwroot/styles.css` | link styling |
| `tests/Agent42.Tests/SearxngProxyTests.cs` | **new** |
| `tests/Agent42.Tests/PwaAssetsTests.cs` | the pinned shell version (line 215) and a new `/searxng` guard assertion |
| `tests/Agent42.Tests/EasyAuthExclusionTests.cs` | three rows on the authenticated-paths theory |
| `tests/Agent42.Tests/ProjectLayoutTests.cs` | one `AgentApiParts` row; the "fifteen endpoint groups" docstring (line 158) |
| `tests/Agent42.Tests/ConfigHealthAssetsTests.cs` | source-text guard for the link |
| `.agentic/decisions/0059-*.md` | **new** — amends ADR 0011 |
| `.agentic/decisions/0011-web-search-self-hosted-searxng-sidecar.md` | `Amended by ADR 0059` line |
| `.agentic/gotchas.md` | new entry (Step 8) |
| `docs/features/web-search.md` | document the proxy route |
| `CLAUDE.md` | the ADR 0011 bullet **and** the ADR 0040 bullet's "15 per-feature partials" (line 950) |

**No** Bicep ingress change, **no** `excludedPaths` entry, **no** new secret, **no** new Cosmos
container, **no** Dockerfile change, **no** new mandatory DI registration in either host.

## Context & constraints

### 1. The ADR this contradicts — read before writing code

ADR 0011 rejected "SearXNG as a separate internal Container App" with the reason
**"a sidecar is one deployment unit, `localhost`-only, and therefore unreachable from outside by
construction"** (`0011-…md:63-65`), and states again under consequences: *"No new ingress. The sidecar
is `localhost`-only and unreachable from outside by construction; Easy Auth still fronts the only
exposed port (ADR 0002 unweakened)."* (`:170-171`).

This plan keeps the first half (one ingress, Easy Auth unweakened, ADR 0002 untouched) and **retires
the second**. That is a real change to a recorded property and gets an ADR amendment as Step 7 — not a
silent contradiction. Honest framing for the ADR: *unreachable by construction* becomes *unreachable
except through one authenticated, sandboxed, header-scrubbing route*.

### 2. The direction that nearly got missed: the sidecar runs on OUR origin

Proxying means SearXNG's HTML and JS execute at `https://chat.agent42.nl/searxng/*` — **same origin as
the chat app**. Absent a control, a script on that page can:

- read the ADR 0032 IndexedDB mirror (`agent42-cache`), which holds **conversation text and agent
  definitions unencrypted**;
- call any `/api/*` with the ambient Easy Auth cookie — `GET /api/diagnostics/traces` (ADR 0054, full
  prompt and completion text for **every** user), `POST /api/conversations/{id}/share` (mints a
  public no-login link, ADR 0035), the calendar writes (ADR 0053).

The upstream is pinned and has never been bumped (`infra/searxng/Dockerfile` → `2026.7.12-c19d86faa`),
so any XSS in it becomes an XSS on the app origin. There is no CSP anywhere in the repo today
(zero hits for `Content-Security-Policy` across `src/` and `infra/`).

**User's decision (interview, this session): CSP sandbox.** Every proxied response carries
`Content-Security-Policy: sandbox allow-scripts allow-forms`. Omitting `allow-same-origin` puts the
document in an **opaque origin**, so it reaches none of the above. Preferences still work because
SearXNG reads them from HTTP cookies server-side, not from `document.cookie`. **Accepted cost:**
SearXNG's JS autocomplete breaks — from an opaque origin its `fetch` to `/searxng/autocompleter` is
cross-origin and gets CORS-blocked. A separate origin (`search.agent42.nl`) was rejected as reopening
exactly what ADR 0011 turned down; accepting the risk undocumented was rejected outright.

This is the single most important property of the feature. Step 1 measures what else it breaks.

### 3. The subpath question is unsettled and is Step 1's job

SearXNG's `server.base_url` accepts a URL with a path and is settable via `SEARXNG_BASE_URL`. It sits
in the `server:` section, and `infra/searxng/Dockerfile` records that the container's `SEARXNG_*`
overrides "target `server`/`general`" — so if this works it needs **no Dockerfile change**. (Note ADR
0011 itself records only the *negative* case, that `search.formats` under `search:` is **not** reachable
that way; the positive claim comes from the Dockerfile comment and is an upstream generalisation.)

What is **not** established is whether `base_url` with a subpath actually prefixes the *asset* URLs in
the served HTML. This is the classic subfolder trap and upstream has a long-standing issue about it
(searx#366). Nothing in this repo's ADRs or gotchas touches `base_url`. Do not assume; Step 1 measures.

**Fallback if it fails (user's choice):** proxy SearXNG's own root paths (`/search`, `/static/*`,
`/autocompleter`, `/opensearch.xml`, …). `wwwroot/` holds `icons/ index.html js/ lib/ logs/
manifest.webmanifest shared/ styles.css sw.js` — **no `static/`** — so the prefix is free. Markup
rewriting was explicitly rejected as brittle.

**The fallback is not just a route-template swap** — two of Step 3's inbound rules change meaning, and
Step 1 must record which branch was taken so the executor applies the right pair:

| Rule | Subpath branch | Root-path branch |
|---|---|---|
| `Set-Cookie` scoping | force `Path=/searxng/` | must be `Path=/` — so the `sxng_` **name** prefix becomes the only isolation, and AC 5 asserts that instead |
| `Location` rewrite | prefix root-absolute paths | no-op; only the reject rules of AC 6 apply |

The SW guard (Step 4) also has to cover the wider path set on that branch.

### 4. Other user decisions from the interview

- **Discovery:** a link in Preferences → Configuration, beside the SearXNG reachability check that
  already lives there. Not the sidebar footer.
- **Surface:** pass **everything** through, including `/preferences`.
- **Offline:** AppHost only; DevHost returns a curated 503.

### 5. Traps this feature walks into

1. **`IHttpClientFactory` is not registered in DevHost** (no `AddServiceDefaults`), and a minimal-API
   parameter the host cannot resolve is treated as a *body*, breaking routing for the **whole host**.
   Inject `IServiceProvider` and resolve with `GetService<T>()`, exactly as `GET /api/config-health`
   does (`ConfigHealth.cs` → `ProbeSearxngAsync`'s null-factory branch). This is also what makes the
   DevHost 503 fall out for free.
2. **A named `HttpClient` silently inherits `AddServiceDefaults`' retry pipeline** — 3 retries, and
   `HttpClient.Timeout` does not opt out. The new client needs `RemoveAllResilienceHandlers()`.
3. **The Easy Auth session cookie must never reach the sidecar.** The browser sends
   `AppServiceAuthSession` (path `/`) on every `/searxng/*` request. A denylist of known auth cookie
   names was considered and rejected as unauditable — Step 3 uses an allowlist.
4. **Easy Auth injects `X-MS-CLIENT-PRINCIPAL-*` request headers** carrying the user's id, email and
   name. Forwarding them gives the sidecar the per-user attribution ADR 0011 says self-hosting buys us
   the absence of.
5. **An `AgentApi` partial that exists but is never called from the composition root compiles cleanly
   and maps nothing** (`ProjectLayoutTests.AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot`).
6. **`{**path}` does not match a missing segment**, so `/searxng` needs its own route.
7. **This is the first non-`/api` route in `MapAgentApi`** — all 44 existing templates are under
   `/api/`, and `/logs` and `/shared` are static directories, not routes. See Step 4 for why the
   service worker makes that load-bearing.

## Steps

### Step 1 — Spike: `SEARXNG_BASE_URL` prefixing **and** what the sandbox breaks

Outside the repo tree, run the pinned image directly:

```
docker run --rm -e SEARXNG_SECRET=spike -e SEARXNG_PORT=8888 \
  -e SEARXNG_BASE_URL=http://localhost:8888/searxng/ -p 8888:8888 \
  $(docker build -q infra/searxng)
```

Record verbatim: (a) do `<link>`/`<script>` hrefs read `/searxng/static/...` or `/static/...`?
(b) does the search `<form>` carry the prefix, and is its `method` GET or POST? (c) does `/searxng/`
itself serve the page, or only `/`? (d) serve one captured page with
`Content-Security-Policy: sandbox allow-scripts allow-forms` and record **what still works** — does
the search form submit, do preferences persist across a request, and is autocomplete the only
casualty?

**Verify:** measured values pasted into the ADR (Step 7). No code changes yet. Record the chosen
branch (subpath vs root-path) at the top of the spec's Review section so later steps are unambiguous.

### Step 2 — Config: point SearXNG at its public base URL

`infra/modules/container-app.bicep` (sidecar `env`, beside `SEARXNG_PORT`) and
`src/Agent42.AppHost/AppHost.cs`. The comment must say why it is an env var and not `settings.yml`,
citing the Dockerfile's `server`/`general` note — the inverse of the `search.formats` case.

**Verify:** `dotnet build Agent42.slnx`. Skip if Step 1 chose the fallback.

### Step 3 — Transport + `SearxngProxy`: the translation rules

**A dedicated named client, not the existing one.** Reuse of `SearxngWebSearch.HttpClientName` is
**not viable** — three primary-handler defaults break the design, and all three are shared with
`SearxngWebSearch` (`Program.cs:262`) and `ProbeSearxngAsync` (`ConfigHealth.cs:302`):

| Default | Why it breaks the proxy |
|---|---|
| `AllowAutoRedirect = true` | the client follows the 3xx itself, so the endpoint never sees a `Location` — Step 3's rewrite and AC 6 would be **dead in production while green in the stub**, since `StubHandler` *is* the primary handler |
| `UseCookies = true` | the pooled `CookieContainer` is shared across consumers — one user's SearXNG cookie replays onto another user's request *and* onto `web_search`'s own tool queries, silently changing search behaviour (the `&safesearch=` gotcha's shape) |
| `AutomaticDecompression = None` | forward the browser's `Accept-Encoding` and the proxy emits gzip bytes labelled `text/html` |

So register `searxng-ui` in `Program.cs` with the same `BaseAddress` and 10 s timeout,
`RemoveAllResilienceHandlers()`, and `ConfigurePrimaryHttpMessageHandler` setting all three
(`AllowAutoRedirect = false`, `UseCookies = false`, `AutomaticDecompression = All`).

**`src/Agent42.Backend/Web/SearxngProxy.cs`** — no HTTP host, no DI, so it is testable directly:

*Outbound (browser → sidecar)*
- Forward method, path remainder, query string, and the POST body.
- **Build the upstream URI by explicit string composition, never `new Uri(base, remainder)`.** The
  catch-all value is percent-decoded, so `/searxng/%2f%2fevil.example/x` yields `//evil.example/x`,
  which `Uri` resolves as protocol-relative → `http://evil.example/x`, dialled from inside the
  container (reaching the backend's own 8080, or the Cosmos emulator under AppHost). Normalize to a
  single leading slash, concatenate onto the fixed base, and **reject if the resolved `Authority`
  differs from the base's**. This repo's scar tissue on exactly this is ADR 0011's SSRF section.
- **Headers: allowlist, not denylist** — the same argument Step 3 makes for cookies applies here.
  Forward only what the UI needs (`Accept`, `Accept-Language`, `Content-Type`, `Content-Length`,
  `User-Agent`). Everything else is dropped, which covers `X-MS-*`, `Authorization`, `Host`, `Referer`
  (would hand the sidecar the app URL) and `X-Forwarded-For` without enumerating them.
- **Cookies: allowlist by prefix.** Only `sxng_*`, prefix stripped. An allowlist means a cookie the app
  adds later cannot leak by omission.

*Inbound (sidecar → browser)*
- **Add `Content-Security-Policy: sandbox allow-scripts allow-forms`** — §2. This overwrites any
  upstream CSP rather than appending, since two CSP headers intersect.
- Pass status code and `Content-Type`. Copy **no other response header by default** (allowlist again);
  `Content-Encoding` in particular must not be forwarded once the handler decompresses.
- `Set-Cookie`: prefix the name with `sxng_`, and scope per the branch table in §3.
- `Location` on a 3xx: rewrite a **root-absolute** path under the prefix; **reject** an absolute or
  protocol-relative value (replace with a 502). The app origin hosts two OAuth callbacks
  (`/api/mcp/oauth/callback`, `/api/google/oauth/callback`), so an open redirector here is not benign.
- Drop hop-by-hop headers (`Connection`, `Transfer-Encoding`, `Keep-Alive`).

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~SearxngProxyTests`.

### Step 4 — The endpoint, and the service-worker guard

`src/Agent42.Backend/Web/AgentApi.Web.cs` — `private static void MapWebEndpoints(IEndpointRouteBuilder app)`,
flat `Agent42.Backend` namespace, `partial class AgentApi`. Routes: `GET /searxng` → 302 to
`/searxng/`; `GET`/`POST /searxng/{**path}` → proxy. Inject `IServiceProvider`, resolve
`IHttpClientFactory` with `GetService`, curated 503 when null. Register `MapWebEndpoints(app);` in
`Api/AgentApi.cs` and fix its "56 route registrations" prose.

**The service worker must skip `/searxng` too.** `sw.js:79` returns early only for `/api`, and the
gotcha behind it — never cache per-user, auth-bearing responses in the shared shell cache — applies
verbatim here: proxied subresources carry the Easy Auth cookie and per-user SearXNG preference
cookies, and would land in `agent42-shell-v25` via the stale-while-revalidate branch. Worse, the
navigate branch's offline fallback would paint `/index.html` — the chat app — for a `/searxng/`
navigation. Add the guard **beside** the `/api` one, before any `respondWith`.

Also update the `AgentApiParts` docstring ("fifteen endpoint groups" → sixteen).

**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~SearxngProxyTests|FullyQualifiedName~ProjectLayoutTests|FullyQualifiedName~PwaAssetsTests"`.

### Step 5 — Easy Auth assertions (parallelizable with Step 6)

`tests/Agent42.Tests/EasyAuthExclusionTests.cs`: add `/searxng`, `/searxng/` and
`/searxng/static/x.css` to `Exclusions_DoNotCoverAnyAuthenticatedPath`. No Bicep change — the point is
proving the route stays gated. Do not touch `excludedPaths`.

**Verify:** `dotnet test Agent42.slnx --filter FullyQualifiedName~EasyAuthExclusionTests`.

### Step 6 — The Configuration-tab link (parallelizable with Step 5)

Anchor in `index.html`'s Configuration tab, rendered in `confighealth.js`, styled in `styles.css`,
opening `/searxng/` in a new tab. Bump `CACHE` in `sw.js` to `agent42-shell-v25` **and the pinned
version in `PwaAssetsTests.cs:215`** — that assertion is `Assert.Contains("agent42-shell-v24", sw)`
and fails otherwise. `js/confighealth.js` is already in `sw.js`'s `SHELL`, so no `SHELL` edit is due.

**Verify:** `dotnet test Agent42.slnx --filter "FullyQualifiedName~ConfigHealthAssetsTests|FullyQualifiedName~PwaAssetsTests"`.

### Step 7 — ADR 0059 amending ADR 0011

Via `/decide`. Must record: the retired "unreachable by construction" property; **the same-origin
analysis of §2, the CSP sandbox, and the two rejected alternatives (separate origin, accept-and-document)**;
Step 1's measured values including what the sandbox broke; the cookie and header allowlists with the
rejected denylist; the SSRF and open-redirect rules; the shared-instance and `/preferences`
consequences; and an ADR 0054-style read-surface note. Add `Amended by ADR 0059` to ADR 0011.

**Verify:** `node .agentic/scripts/check-doc-paths.mjs` — doc-path integrity is a **script** run by
`.github/workflows/pr-checks.yml:88`, not an xUnit test.

### Step 8 — Gotcha + docs

New gotcha: proxying a third-party app onto the app's own origin, covering both the auth-cookie /
`X-MS-*` leak outbound and the origin-privilege escalation inbound, with the allowlists and the CSP
sandbox as the fixes. Update `docs/features/web-search.md`, the ADR 0011 bullet in `CLAUDE.md`, and
the ADR 0040 bullet's "15 per-feature partials" (`CLAUDE.md:950`).

**Verify:** `dotnet test Agent42.slnx` + `dotnet format Agent42.slnx --verify-no-changes`.

### Step 9 — Manual leg in AppHost

`dotnet run --project src/Agent42.AppHost`, open `/searxng/`, and record: the page renders **styled**;
a search returns results; `/preferences` loads and persists; the sidecar's received request carries
**no** `AppServiceAuthSession` cookie and **no** `X-MS-*` header; and the sandbox holds — from the
page's console, `indexedDB.open("agent42-cache")` and `fetch("/api/models")` both fail.

Record what this leg cannot show: AppHost has no Easy Auth, so real gating is a post-deploy check (the
ADR 0042 gotcha's standing instruction); and the installed-PWA behaviour of §S13 below.

## Acceptance criteria → test mapping

| # | Acceptance criterion | Test | File |
|---|---|---|---|
| 1 | A `/searxng/*` request reaches the sidecar; body/status/content-type come back intact | `Proxy_ForwardsThePathAndReturnsTheUpstreamResponse` | `tests/Agent42.Tests/SearxngProxyTests.cs` |
| 2 | The Easy Auth session cookie is never forwarded upstream | `Proxy_DoesNotForwardTheEasyAuthCookie` | same |
| 3 | Only `sxng_`-prefixed cookies are forwarded, prefix stripped | `Proxy_ForwardsOnlyItsOwnPrefixedCookies` | same |
| 4 | Request headers are allowlisted — `X-MS-*`, `Authorization`, `Referer` all dropped | `Proxy_ForwardsOnlyAllowlistedRequestHeaders` | same |
| 5 | Upstream `Set-Cookie` is name-prefixed and scoped per the chosen branch | `Proxy_NamespacesAndScopesUpstreamCookies` | same |
| 6 | A root-absolute `Location` is rewritten; absolute / protocol-relative is rejected | `Proxy_RewritesRedirectLocation`, `Proxy_RejectsOffSiteRedirects` | same |
| 7 | POST (the search form) is forwarded with its body | `Proxy_ForwardsPostBodies` | same |
| 8 | `/searxng` redirects to `/searxng/` | `Proxy_RedirectsTheBarePrefix` | same |
| 9 | Every proxied response carries the sandbox CSP | `Proxy_SandboxesEveryResponse` | same |
| 10 | A `//host` remainder cannot steer the upstream call off `localhost:8888` | `Proxy_CannotBeSteeredOffTheSidecarByAProtocolRelativePath` | same |
| 11 | With no `IHttpClientFactory` the route 503s and other routes still work | `Proxy_ReturnsServiceUnavailable_WhenNoHttpClientFactory` | same (**second fixture** — see below) |
| 12 | The route is Easy-Auth gated, not excluded | `Exclusions_DoNotCoverAnyAuthenticatedPath` (3 new rows) | `tests/Agent42.Tests/EasyAuthExclusionTests.cs` |
| 13 | The partial exists and is called from the composition root | `AgentApiPart_ExistsAndIsRegisteredFromTheCompositionRoot` (new row) | `tests/Agent42.Tests/ProjectLayoutTests.cs` |
| 14 | The service worker never caches `/searxng` | new assertion beside the `/api`-guard one | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| 15 | The Configuration tab links to the UI | `ConfigurationTab_LinksToTheSearchUi` | `tests/Agent42.Tests/ConfigHealthAssetsTests.cs` |
| 16 | The shell cache version was bumped | existing guard, **updated to `-v25`** | `tests/Agent42.Tests/PwaAssetsTests.cs:215` |

**Fixture mechanics — the plan's first draft got this wrong.** `SharedApiFixture` does **not** run
`Program.cs`; it builds a fresh `WebApplication.CreateSlimBuilder()` and calls `AddRouteMatcherServices`
(`SharedApiFixture.cs:86-108`), which registers **no `IHttpClientFactory` and no named clients**. So:

- AC 1–10 need a fixture subclass that **creates** the `searxng-ui` client (`AddHttpClient` +
  `ConfigurePrimaryHttpMessageHandler(() => stub)`), not one that overrides an existing one. Note
  "registration order is last-wins" is true of `AddSingleton` but **not** of `AddHttpClient`, whose
  configuration is additive — only the primary handler is effectively last-wins.
- AC 11 needs a **second fixture** (or the bare `SharedApiFixture`), because installing the stub is
  what registers the factory. Budget the extra cluster deploy: under ADR 0055 each fixture is one.
- Prefer testing `SearxngProxy` directly for AC 2–10 and keeping the host-level fixture for AC 1 and
  11 — that is why Step 3 makes it a DI-free class.

**Not covered by any test, and the ADR must say so:** that the real ACA ingress gates `/searxng/*`
(TestServer has no Easy Auth — ADR 0042's standing residual); whether the real SearXNG HTML renders
correctly under the prefix and under the sandbox (Steps 1 and 9 measure it, neither is automated).

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` at plan time:

| Gate | Threshold | Tool |
|---|---|---|
| `coverage_new_code_pct_min` | 80 | `node .agentic/scripts/check-new-code-coverage.mjs` |
| `cognitive_complexity_max` | 15 | `SonarAnalyzer.CSharp` S3776 at build time — **blocks** (`error`) |
| `vulnerable_packages` | 0 | CI package-freshness job |
| Branch policy | never commit to `main` | `feature/searxng-ui-proxy` + MR |

Watch S3776 on the header-translation methods — a cookie loop, a header loop and several conditionals
is exactly the shape that tips over 15. Split per direction (outbound/inbound) rather than compressing
with ternaries. `Program.cs` gains one named-client registration; if that tips the composition root
over, lift it into a `Web/` registration extension (the `DocumentIndexRegistration` /
`HindsightRegistration` answer), never a ternary.

## Out of scope

- Any `excludedPaths` change. The route is Easy-Auth gated like every other browser surface.
- A second ingress, a second origin, or exposing port 8888 publicly.
- Restricting which SearXNG paths are reachable — the user chose full pass-through.
- Bumping the pinned upstream SearXNG image. Worth doing (§2 notes it has never been bumped), but it
  is a separate change with its own verification.
- A CSP for the rest of the app. This plan adds one header to proxied responses only.
- DevHost support beyond the curated 503.
- Any change to `web_search` / `fetch_web_page` or `WebToolLimits`.
- Rate-limiting the proxy, or re-enabling SearXNG's own `limiter`.

## Review

### Step 1 measurements (executed 2026-08-30) — **BRANCH TAKEN: SUBPATH**

`SEARXNG_BASE_URL` subpath prefixing **works**. Steps 2-4 take the subpath column of the §3 branch
table: `Set-Cookie` forced to `Path=/searxng/`, `Location` rewritten. The root-path fallback is
**not** used.

Measured against the pinned image (`infra/searxng/Dockerfile` -> `2026.7.12-c19d86faa`), run with
`SEARXNG_BASE_URL=http://localhost:8888/searxng/`:

| # | Question | Measured |
|---|---|---|
| a | asset hrefs prefixed? | **yes** - `/searxng/static/themes/simple/sxng-ltr.min.css`, `src="/searxng/static/themes/simple/sxng-core.min.js"`, plus `/searxng/preferences`, `/searxng/manifest.json`, `/searxng/opensearch.xml`, `/searxng/info/en/about` |
| b | search form | `<form id="search" method="POST" action="/searxng/search">` - **POST**, prefixed. AC 7 is load-bearing, not defensive |
| c | which paths serve | `/searxng/` -> 200 **and** `/` -> 200 (upstream answers both, so forwarding the stripped remainder is correct). Bare `/searxng` -> **308** with an **absolute** `location: http://localhost:8888/searxng/` |
| d | under `sandbox allow-scripts allow-forms` | origin **opaque**; `indexedDB.open("agent42-cache")` -> **SecurityError**; `fetch("/api/models")` -> **blocked**; `document.cookie` -> **SecurityError**; `localStorage` -> **SecurityError**; page renders **fully styled** (screenshot); form **submits** (POST -> `/searxng/search`, results page renders); **HTTP cookies round-trip** (probe cookie returned on the 2nd request), so preferences persist server-side; `fetch("/searxng/autocompleter")` -> **blocked** - autocomplete is the only casualty, as predicted |

Method: the ADR 0032 IndexedDB mirror was **seeded from the real app origin first** and proved
readable there, so the sandboxed `SecurityError` is a denied read of data that exists - not an
absent database. Driven by headless Chrome over CDP against a Node harness that mimicked the app
origin (`/api/models` + the seeded mirror) and applied the CSP to proxied responses.

**Two corrections to Step 3, found by measurement:**

1. **The plan's "reject an absolute `Location` (502)" rule would break the normal preferences save.**
   Saving preferences emits `302` with an **absolute** `location: http://<base_url authority>/searxng/`
   - and in production `SEARXNG_BASE_URL` is the *public* origin, so upstream will emit
   `https://chat.agent42.nl/searxng/`. A strict reject 502s the single most common redirect in the UI.
   Corrected rule: an absolute `Location` whose authority matches **either the upstream dial authority
   or the request's own host** is reduced to its path+query and re-emitted root-relative; every other
   absolute or protocol-relative value is rejected. The open-redirector property AC 6 protects is
   preserved.
2. **Do not double-prefix.** Because `SEARXNG_BASE_URL` carries the subpath, upstream-generated paths
   **already start with `/searxng`**. The rewrite must prefix only when it is absent.

**Confirmation that the cookie rules are necessary, not theoretical:** one preferences save emits
**22 `Set-Cookie` headers**, all `Path=/`, with generic unprefixed names - `method`, `tokens`,
`theme`, `language`, `categories`, `autocomplete`, `safesearch`, `enabled_engines`, ... On the app's
own origin at `Path=/` those are live collision candidates. The `sxng_` name prefix plus
`Path=/searxng/` scoping is what prevents it.

**Incidental, and it is the feature's own justification:** the spike instance returned `No results`
for every query, and only the JSON endpoint explained why - `"unresponsive_engines": [["duckduckgo",
"CAPTCHA"]]`. Identical direct-to-sidecar with no proxy and no sandbox, so it is an environment
fact, not a proxy defect. It is exactly the black-box symptom the Overview cites.

### Step 9 measurements (executed 2026-08-30) — the AppHost leg WAS run

`dotnet run --project src/Agent42.AppHost`, backend on `localhost:56196`, driven by curl and headless
Chrome over CDP.

| Check | Measured |
|---|---|
| `/searxng/` | **200**, fully styled (screenshot: dark theme, logo, styled search box) |
| Asset URLs in the served HTML | prefixed — `/searxng/static/themes/simple/sxng-ltr.min.css` |
| That asset through the proxy | **200**, `text/css`, 85,693 bytes |
| CSP on the page **and** on the stylesheet | `sandbox allow-scripts allow-forms` on both |
| `/searxng` (bare) | **302 → `/searxng/`** |
| `/searxng/` (canonical) | **200 — does not loop** |
| `GET /searxng/preferences` | **200** |
| `POST /searxng/preferences` | **302**, `Location: /searxng/` (rewritten from an absolute), 22 cookies rewritten to `sxng_*` … `Path=/searxng/` |
| Easy Auth cookie + unprefixed `language=de` sent | sidecar selected **`en`** — neither reached it |
| `sxng_language=de` sent | sidecar selected **`de`** — forwarded, prefix stripped |
| `X-Forwarded-For` sent | sidecar logged **`X-Forwarded-For nor X-Real-IP header is set!`** — dropped |

**The sandbox, proved against the real app origin.** The chat app was loaded first and its ADR 0032
mirror seeded and read back (`{"secret":"conversation text"}`), and `/api/models` returned **200** —
so the privileges genuinely exist on that origin. From `/searxng/` on the **same** origin:

| Probe | Measured |
|---|---|
| `origin` | **opaque** (`"null"`) |
| `indexedDB.open("agent42-cache")` | **SecurityError** |
| `fetch("/api/models")` | **blocked** |
| `fetch("/api/diagnostics/traces")` (ADR 0054) | **blocked** |
| `document.cookie` / `localStorage` | **SecurityError** |

**One correction to Step 3, found only by this leg.** Under AppHost the sidecar is its own container
and Aspire resolves its endpoint to the **container-network name**, so `SEARXNG_BASE_URL` becomes
`http://searxng.dev.internal:8888/searxng/` — an authority the backend never dials and that is not the
request host either. The two-clause redirect rule matched neither and **502'd the preferences save**.
`IsTrusted` gained a third clause: an absolute `Location` whose path is already under our own route
prefix is honoured. It is safe because the authority is discarded either way — what is emitted is
always a root-relative path on our origin — and an off-site value with no `/searxng` path is still
rejected (`Proxy_RejectsOffSiteRedirects` gained a row for exactly that).

**What this leg cannot show:** AppHost has no Easy Auth, so real gating is a post-deploy check (ADR
0042's standing instruction); and the installed-PWA behaviour of S13 below (the link opens inside the
standalone window) needs an Android device.



A `critic` subagent reviewed the first draft against the knowledge files and code, returning **not
ready** with 14 findings. All were verified against the code before acting; every mechanical claim it
made was correct.

**Accepted and folded in:** S1 (same-origin script execution — reshaped the feature, §2, and was put
to the user as a design decision); S2 (the three primary-handler defaults → dedicated `searxng-ui`
client, Step 3); S3 (the fixture cannot override a client that was never registered, and AC 11 needs
its own fixture); S4 (`PwaAssetsTests.cs:215` pins `v24`); S5 (the service worker's `/api` guard, Step
4); S6 and S7 (SSRF and open-redirect rules — S6 had already been caught and fixed independently
before the review returned); S8 (header allowlist instead of denylist); S9 (the fallback branch changes
two inbound rules, now a table in §3); S10 (the doc-path check is a script, fixed before the review
returned); S11 (doc debt: `AgentApi.cs:12`, `ProjectLayoutTests.cs:158`, `CLAUDE.md:950`); S12 (ADR
0011 records only the negative `search:` case — re-cited to the Dockerfile); S14 (the `/preferences`
per-browser claim was conditional on `UseCookies=false`, now guaranteed by Step 3).

**Recorded, not fixed:** S13 — `manifest.webmanifest` has `"scope": "/"`, so on the installed Android
PWA the "new tab" link opens **inside** the standalone window, replacing the chat UI with no browser
chrome, and Step 9's desktop AppHost leg cannot observe it. Not fixed because the alternatives
(narrowing the PWA scope, or dropping the link) cost more than the annoyance: scope is load-bearing for
ADR 0023's share target and ADR 0032's offline shell. Recorded as a known consequence for the ADR; the
back gesture returns to the app.


## Divergence from the plan

Everything shipped; the acceptance criteria are unchanged and all 16 are covered. Six places where the
implementation differs from what the plan specified, each forced by something measured:

1. **`SEARXNG_BASE_URL` was CHANGED, not added.** The plan's Affected-areas table said "add
   `SEARXNG_BASE_URL` to the sidecar's `env`"; it was already there, set to `http://localhost:8888/`.
   Step 2 changed its value. In Bicep it becomes `https://{customDomainName}/searxng/` when a custom
   domain is bound and falls back to `http://localhost:8888/searxng/` otherwise — the authority is only
   a correctness nicety (redirects, opensearch.xml), since the proxy rewrites redirects either way.
2. **The redirect rule accepts absolutes; the plan rejected them.** Measured: a preferences save answers
   302 with an absolute `Location` built from the base URL, so "reject an absolute or protocol-relative
   value" would 502 the commonest redirect in the UI. See the Step 1 section.
3. **A third trust clause was added after the AppHost leg** — an absolute `Location` whose path is
   already under our route prefix. Aspire resolves the sidecar's own endpoint to the container-network
   name, which the backend never dials. See the Step 9 section.
4. **The bare-prefix route is not an unconditional redirect.** Endpoint routing matches a trailing slash
   against the literal `/searxng` template and the literal out-ranks the catch-all, so `/searxng/` — the
   canonical URL — arrives at that route and a bare redirect loops forever. The handler tests for the
   trailing slash and proxies instead. The plan's trap #6 had the converse half of this right and this
   half missing; it was found by a test failing for the wrong reason, and is now pinned by
   `Proxy_ServesTheCanonicalSlashUrlWithoutLooping`.
5. **Both new test classes derive from `SharedClusterTests<TFixture>`** (ADR 0055), which the plan did
   not mention. `SharedClusterSeamTests` fails a class that takes a cluster fixture without it.
6. **`Content-Length` is not in the forwarded request-header allowlist.** The plan listed it; it is a
   content header set by `HttpContent`, and forwarding it by hand would conflict with the body actually
   sent. `Content-Type` rides on the content for the same reason.

**Not done, and deliberately:** bumping the pinned upstream SearXNG image (explicitly out of scope in
the plan — a separate change with its own verification), and any `excludedPaths`, ingress or second-origin
change.

**One residual carried forward:** S13 from the plan's own review — on the installed Android PWA the
`manifest.webmanifest` scope is `/`, so the "new tab" link opens inside the standalone window. Recorded
in ADR 0059's consequences; not observable from the desktop AppHost leg.
