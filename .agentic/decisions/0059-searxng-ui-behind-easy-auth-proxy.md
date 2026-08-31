---
id: 0059
date: 2026-08-30
status: accepted
tags: [web-search, searxng, proxy, security, same-origin, csp, easy-auth, container-apps, diagnostics]
supersedes: []
extends: []
amends: [0011]
---

# The SearXNG web UI, proxied onto the app's own origin behind Easy Auth

> **Amends [ADR 0011](0011-web-search-self-hosted-searxng-sidecar.md).** ADR 0011 rejected running SearXNG
> as a separate internal Container App on the grounds that *"a sidecar is one deployment unit,
> `localhost`-only, and therefore unreachable from outside by construction"*, and repeated the property
> under its consequences: *"No new ingress. The sidecar is `localhost`-only and unreachable from outside
> by construction; Easy Auth still fronts the only exposed port (ADR 0002 unweakened)."*
>
> The first half stands and is untouched: **one ingress, one exposed port, Easy Auth unweakened, no
> `excludedPaths` entry, ADR 0002 intact.** The second half is **retired**. *Unreachable by construction*
> becomes **unreachable except through one authenticated, sandboxed, header-scrubbing route.** That is a
> real weakening of a recorded property, and it is why this is an ADR rather than a quiet route addition.

## Context

The sidecar is a black box. `web_search` returns a result list and nothing else; when that list is empty
or odd there is no way to see what the instance actually returned, which engines answered, or whether one
is failing. `GET /api/config-health`'s probe reports reachability and stops there.

SearXNG already serves a full HTML search UI — `infra/searxng/settings.yml` lists both `json` and `html`
under `search.formats` — but nothing outside the replica can reach port 8888.

This is not hypothetical. While measuring for this change, every query on the spike instance returned
`No results`, and only the JSON endpoint explained why:

```json
{"query": "wikipedia", "results": [], "unresponsive_engines": [["duckduckgo", "CAPTCHA"]]}
```

An engine serving a CAPTCHA to the datacenter IP is exactly the failure mode that is invisible today and
that the UI reports plainly.

## Decision

Add one backend reverse-proxy route, `GET`/`POST /searxng/{**path}`, mapping the sidecar's own UI onto
the app's single Easy-Auth-gated ingress. No second ingress, no second origin, no exposed port 8888, no
new secret, no new Cosmos container, no Dockerfile change, no `excludedPaths` entry, and no new mandatory
DI registration in either host.

### 1. The direction that nearly got missed: the sidecar runs on OUR origin

Proxying means SearXNG's HTML and JS execute at `https://chat.agent42.nl/searxng/*` — **same origin as
the chat app**. Absent a control, a script on that page could read the ADR 0032 IndexedDB mirror
(`agent42-cache`, holding conversation text and agent definitions unencrypted) and call any `/api/*` with
the ambient Easy Auth cookie: `GET /api/diagnostics/traces` (ADR 0054 — full prompt and completion text
for **every** user), `POST /api/conversations/{id}/share` (mints a public no-login link, ADR 0035), the
calendar writes (ADR 0053). The upstream image is pinned and has never been bumped, so any XSS in it
would become an XSS on the app origin. There was no CSP anywhere in the repo before this change.

**Every proxied response therefore carries `Content-Security-Policy: sandbox allow-scripts allow-forms;
form-action {scheme}://{host}/searxng/`.** Omitting `allow-same-origin` puts the document in an **opaque
origin**, which stops every **read**. The `form-action` half stops the **writes**, and it is not
belt-and-braces — see below.

This was measured rather than assumed, against a harness that seeded the real `agent42-cache` mirror from
the app origin first and proved it readable there — so the sandboxed result is a *denied read of data that
exists*, not an absent database:

| Probe from the sandboxed page | Measured |
|---|---|
| `indexedDB.open("agent42-cache")` | **SecurityError** |
| `fetch("/api/models")` | **blocked** |
| document.cookie | **SecurityError** |
| `localStorage.setItem` | **SecurityError** |
| Page renders styled | **yes** (screenshot; dark theme, logo, styled search box) |
| Search form submits | **yes** — POST to `/searxng/search`, results page renders |
| Preferences persist | **yes** — HTTP cookies still round-trip; SearXNG reads them server-side |
| `fetch("/searxng/autocompleter")` | **blocked** |

> **Corrected 2026-08-31 — "Preferences persist" holds only where there is no Easy Auth, i.e. not in
> production.** Cookies do round-trip, but *saving* is a POST from the opaque origin, which loses the Easy
> Auth cookie and 403s before reaching SearXNG (see the correction under §1). Reading and searching are
> unaffected. This row was measured in DevHost, which has no Easy Auth — the whole class of bug is
> invisible offline, which is the honest reason it shipped.

Everything the feature exists for — seeing the results and the engine errors — works.
The autocompleter row is the only functional loss that was known at the time, and it costs nothing **in this deployment**: SearXNG's JS
autocomplete would be blocked by the same rule, but `infra/searxng/settings.yml` already sets
`autocomplete: ""`, so it is off in the baked image regardless. There is no observable regression here.

#### The sandbox stops reads, not cookie-bearing navigations — which is why `form-action` is required

The probe table above measures *reads*, and reads are all the sandbox stops. Measured separately, against
a page carrying exactly the header above minus `form-action`: **a sandboxed opaque-origin document can
still submit a form to a same-origin URL, and the browser sends the session cookie with it.** That is not
an abstract gap — `POST /api/conversations/{conversationId}/share` takes **no body**
(`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`), so a form submission runs it verbatim and mints a
public, no-login link to a conversation (ADR 0035); the two OAuth `start` routes are state-changing GET
navigations reachable the same way.

`form-action` closes it, and **`form-action 'self'` does not** — measured: `'self'` matches the origin of
the *URL*, not the document's opaque origin, so it still permits every same-origin endpoint and blocks
nothing. The source expression therefore has to carry the **path**, which means naming the scheme and
host, which makes the header **per-request**:

| CSP under test | Attack POST to `/api/share` | SearXNG's own search form |
|---|---|---|
| `sandbox allow-scripts allow-forms` | **reached the server, with the session cookie** | works |
| `… ; form-action 'self'` | **still reached the server** | works |
| `… ; form-action {scheme}://{host}/searxng/` | **blocked — never left the browser** | works |

> **Corrected 2026-08-31 — the "with the session cookie" cell is wrong, and the reason `form-action` is
> needed is not the one given above.** An opaque origin makes the document's form submissions
> **cross-site**, and the Easy Auth session cookie is `SameSite=Lax`, so the browser **omits it on a
> POST**. Measured in one headless-Chrome session against a server issuing a Lax `AppServiceAuthSession`:
> a normal navigation to the sandboxed page carries the cookie, its **GET** form carries the cookie, and
> its **POST** form arrives with `Cookie: none` and `Origin: null`. Production confirms it independently
> and is the better evidence: saving preferences through the proxy **403s**, which is Easy Auth rejecting
> an unauthenticated POST — if the cookie were being sent it would pass and SearXNG would answer 302.
>
> So the `/api/share` minting described above **does not work as described**: that POST arrives
> unauthenticated and Easy Auth stops it before any endpoint runs. **`form-action` is still required, but
> for the GET half of the same paragraph** — a sandboxed **GET** form *does* carry the cookie (Lax permits
> cross-site top-level GET navigation), so the state-changing GET routes named above really are reachable
> without it. Keep the header; fix the reasoning. The consequence for this feature is in §4 below.
This is also a second, quieter dependency on ADR 0042: `Request.Scheme` is `https` behind the ACA ingress
only because `UseForwardedHeaders(XForwardedProto)` runs first. Without it the policy would name
`http://` and **fail closed**, blocking SearXNG's own search form.

### 2. `SEARXNG_BASE_URL` carries the subpath, so no markup is rewritten

Measured on the pinned image (`2026.7.12-c19d86faa`) with `SEARXNG_BASE_URL=…/searxng/`:

| Question | Measured |
|---|---|
| Are asset URLs prefixed? | **yes** — `http://localhost:8888/searxng/static/themes/simple/sxng-ltr.min.css` and its sxng-core.min.js sibling, plus the prefixed `/searxng/preferences`, manifest and opensearch links |
| The search form | `<form id="search" method="POST" action="/searxng/search">` — **POST**, prefixed |

> **Amended by [ADR 0060](0060-enable-google-and-bing-search-engines.md) (2026-08-30) — one measured cell
> in this table is now stale.** ADR 0060 bumped the pinned image to `2026.8.29-d226b78bc`, on which the
> search form is **`method="GET"`**. Everything else here re-measured unchanged, including the form's
> `action` — which is what the `form-action` argument in §1 actually rests on, so the CSP is unaffected.
> §4's `Set-Cookie` count re-measured as 21 rather than 22, all still `Path=/`; §5's absolute redirect is
> unchanged. ADR 0060 carries the full re-measurement.
| Which paths serve | `/searxng/` → 200 **and** `/` → 200; bare `/searxng` → 308 to an **absolute** URL |

Every generated URL is **root-relative and already prefixed**, so the proxy rewrites no markup — the
approach ADR 0011's plan called brittle and this change never needed. Because the sidecar answers on both
`/` and `/searxng/`, `web_search`'s own `/search?format=json` call over `localhost:8888` is unaffected;
that was measured with the new base URL in place.

It is an env var and not `settings.yml` because `base_url` sits under `server:`, which the container's
`SEARXNG_*` overrides **do** cover — the inverse of the `search.formats` case that `infra/searxng/Dockerfile`
records as *not* reachable that way.

### 3. Two allowlists, no denylists

**Outbound**, only `Accept`, `Accept-Language` and `User-Agent` are forwarded. That drops
`X-MS-CLIENT-PRINCIPAL-*` — Easy Auth injects the user's id, email and name into every request, and
forwarding them would hand the sidecar exactly the per-user attribution ADR 0011 says self-hosting buys
the absence of — along with `Authorization`, `Host`, `Referer` and `X-Forwarded-For`, without enumerating
any of them. Cookies are allowlisted by the `sxng_` prefix, which is what keeps the Easy Auth session
cookie (`AppServiceAuthSession`, path `/`) out.

A denylist of known auth cookie and header names was **rejected as unauditable**: it fails open the day
the app gains a cookie nobody remembered to add to it.

**Inbound**, only `Content-Type` is copied. `Content-Encoding` in particular must not survive, since the
handler decompresses.

### 4. Cookie namespacing is necessary, not decorative

One preferences save emits **22 `Set-Cookie` headers, all `Path=/`**, with names like `method`, `tokens`,
`theme`, `language`, `categories` and `safesearch`. On the app's own origin those are live collision
candidates. Each is therefore name-prefixed with `sxng_` and re-scoped to `Path=/searxng/`, keeping the
`Expires`/`Max-Age` that persistence depends on and dropping any upstream `Domain`.

### 5. Redirects: rewrite what we recognise, reject everything else

An absolute `Location` is the **expected** case here, not an exotic one — saving preferences answers 302
with an absolute URL built from `SEARXNG_BASE_URL`. So an absolute value whose authority is either the
sidecar we dialled or the host the browser asked for is reduced to a root-relative path under the prefix;
**every other absolute value, and anything protocol-relative, is rejected with a 502.** The app origin
hosts two OAuth callbacks (`/api/mcp/oauth/callback`, `/api/google/oauth/callback`), so an open redirector
here would not be benign. Because the base URL carries the subpath, upstream paths already start with
`/searxng` and must not be double-prefixed.

### 6. A dedicated `searxng-ui` HTTP client

Reusing `SearxngWebSearch.HttpClientName` is not viable: three of its primary-handler defaults break a
proxy, and all three are shared with `web_search` and the config-health probe.

| Default | Why it breaks the proxy |
|---|---|
| `AllowAutoRedirect = true` | the client follows the 3xx itself, so the endpoint never sees a `Location` — the rewrite rules would be **dead in production while green against a stub**, since the stub *is* the primary handler |
| `UseCookies = true` | the pooled `CookieContainer` is shared across consumers — one user's SearXNG cookie replays onto another user's request *and* onto `web_search`'s own queries |
| `AutomaticDecompression = None` | defence in depth, not a live bug: the browser's `Accept-Encoding` is never forwarded (it is not in the request allowlist), but since `Content-Encoding` is deliberately not copied back, a compressed upstream response would be forwarded as undeclared encoded bytes |

`RemoveAllResilienceHandlers()` for the reason the two existing clients give: a retried proxy request is a
stalled page.

### 7. SSRF: compose the upstream URI, never resolve it

The catch-all route value arrives **percent-decoded**, so `/searxng/%2f%2fevil.example/x` yields the
remainder `//evil.example/x` — which `new Uri(base, remainder)` resolves as protocol-relative to
`http://evil.example/x`, dialled from inside the container. The upstream URI is therefore built by
explicit string concatenation onto the fixed base with leading separators collapsed, and the resolved
authority is re-checked against the base's. ADR 0011's SSRF section is this repo's scar tissue on exactly
this shape.

### 8. The service worker must skip `/searxng` too

These are the **first non-`/api` routes** in `MapAgentApi` (`/logs` and `/shared` are static directories,
not routes), which is what makes this necessary. The proxied subresources carry the Easy Auth cookie and
per-user preference cookies and would land in the shared `agent42-shell-v25` cache via the
stale-while-revalidate branch; worse, the navigate branch's offline fallback would paint `/index.html` —
the chat app — for a `/searxng/` navigation. The guard sits beside the `/api` one, before any
`respondWith`.

## Consequences

- **A read surface, in the ADR 0054 sense.** Any signed-in user can drive the sidecar's UI. There is no
  role system, so this is the same "all allowlisted users see everything" posture as `/logs` and the usage
  dashboard — but materially narrower than `/logs`, because SearXNG holds no conversation content. What it
  does expose is **shared**: preference cookies are per-browser (guaranteed by `UseCookies = false`), but
  the *instance* is one, so a query typed into the UI is a query from the app's egress IP like any other.
- **The full surface is passed through**, including `/preferences` — but **saving preferences does not
  work in production** (corrected 2026-08-31): the Save POST leaves the opaque origin without the Easy
  Auth cookie and is rejected with a 403. Reading the page, searching and reading engine errors all work.
  The loss is small and was accepted rather than fixed, because nothing set there reaches `web_search`
  anyway — which dials the sidecar directly with no cookie — so the diagnostic purpose of the page is
  intact. The alternatives all cost more than the feature is worth: granting `allow-same-origin` reopens
  everything §1 closes, and an `excludedPaths` carve-out would make the page publicly reachable.
- **Autocomplete is broken** in the proxied UI, and deliberately. See §1.
- **Egress is essentially unchanged.** The sidecar was already forwarding queries to DuckDuckGo/Bing
  (ADR 0011's non-privacy note), and this adds a *read* path rather than a new server-side egress path.
  The one genuinely new thing is that a human now drives the UI in a browser, so result links and any
  non-proxied thumbnails are fetched by **the user's browser** from third parties — ordinary web
  browsing, but worth stating rather than claiming a flat "unchanged".
- **On the installed Android PWA the link opens inside the standalone window.** `manifest.webmanifest` has
  `"scope": "/"`, so the "new tab" is the PWA window itself and the chat UI is replaced with no browser
  chrome; the back gesture returns. Narrowing the scope was rejected — it is load-bearing for ADR 0023's
  share target and ADR 0032's offline shell — and dropping the link costs more than the annoyance.
- **The pinned upstream image is now attack surface on our origin**, bounded by the sandbox. Bumping it is
  worth doing and is deliberately **out of scope** here: it is a separate change with its own verification.

## Alternatives considered

- **A separate origin (`search.agent42.nl`).** Solves the same-origin problem outright, and was **rejected
  as reopening exactly what ADR 0011 turned down** — a second ingress, a second certificate, and a second
  thing to gate. The CSP sandbox buys the same isolation without any of it.
- **Accepting the same-origin risk and documenting it.** Rejected outright. The exposure is the ADR 0054
  trace surface and a share-link minting endpoint, reachable from a pinned third-party image.
- **Restricting which SearXNG paths are reachable.** Rejected by the user in favour of full pass-through;
  a path allowlist would break the UI's own navigation for no security gain the sandbox does not give.
- **Rewriting the served markup** to inject the prefix. Rejected as brittle — and unnecessary once
  `SEARXNG_BASE_URL` was measured to prefix every generated URL already.
- **Rejecting every absolute `Location`** (the simpler redirect rule, and the one this plan originally
  specified). Rejected because measurement showed it would 502 the single most common redirect in the UI.

## Verification

Full suite green (1057 tests; the one failure, `OtelConfigurationTests.AddLiveTelemetryCapture_…`, is a
**pre-existing flake reproduced on clean `main` at `c86fa93`** and is unrelated). `dotnet format` clean,
`dotnet build` clean with S3776 at `error`, `az bicep build` clean.

34 tests cover the route, 25 of them on the translation rules directly and 9 through the host. Two things are pinned that no
review caught:

1. **The canonical URL would have been an infinite redirect loop.** Endpoint routing matches a trailing
   slash against the literal `/searxng` template, and the literal **out-ranks the catch-all** — so
   `/searxng/`, the URL every link on the page points at, arrives at the bare-prefix route rather than the
   proxy route. A bare `Results.Redirect` there redirects to itself forever. Found by
   `Proxy_ReturnsServiceUnavailable_WhenNoHttpClientFactory` failing for the wrong reason, and now pinned
   by `Proxy_ServesTheCanonicalSlashUrlWithoutLooping`.
2. **The stub is installed as the PRIMARY handler**, because that is where the real client disables
   redirect-following and cookies. A stub anywhere else would let the redirect tests pass while the
   behaviour was dead in production.

**Not covered by any test, and stated plainly:**

- **That the real ACA ingress gates `/searxng/*`.** TestServer has no Easy Auth — ADR 0042's standing
  residual. `EasyAuthExclusionTests` asserts the Bicep does not exclude the paths, which is the strongest
  claim available offline; the gating itself is a post-deploy check.
- **That the real SearXNG HTML renders correctly under the prefix and the sandbox in production.** It was
  measured against the pinned image directly (§1, §2) and in AppHost, neither of which is production.
