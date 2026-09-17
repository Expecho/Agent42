---
id: 0042
date: 2026-08-20
status: accepted
tags: [auth, easy-auth, excluded-paths, sharing, infra, azure, container-apps]
supersedes: []
amends: [0002, 0035]
extends: []
---

# Wildcard `excludedPaths` entries, and trusting the ingress's `X-Forwarded-Proto`

> **Extended by [ADR 0076](0076-local-bridge-for-machine-local-mcp-servers.md) (2026-09-17) — a seventh
> entry, `/api/local-bridge`, deliberately EXACT** (the route has no sub-paths), relying on the
> exact-unless-`/*` rule measured here. Whether the ingress passes a WebSocket `Upgrade` through an excluded
> path is not something this ADR measured; 0076 carries that as a post-deploy check.

## Context
ADR 0035 shipped read-only share links whose entire premise is *"anyone with the link can open it, no
sign-in"*. **In production they hit the Entra login page instead.** Two independent defects, both in
config/hosting rather than in the feature's own code — the grains, the token, the handlers, the
fail-closed 404s and the viewer were all correct as shipped.

**Defect 1 — the carve-out matched nothing a recipient loads.** `container-app.bicep` listed
`/api/share` and `/shared` on `globalValidation.excludedPaths`, and ADR 0035 described them as *"two
**prefixes**"*. They are not prefixes: Easy Auth matches an entry **exactly** unless it ends in `/*`.
Every URL a recipient's browser actually requests — `/shared/`, viewer.js, viewer.css, the two
vendored libs, `GET /api/share/{token}`, the attachment route — is a sub-path, so every one of them
was gated. The feature was non-functional, while the Bicep, the ADR, the gotcha and the entire test
suite all read as correct.

**Defect 2 — `Request.Scheme` is `http` inside the container.** Nothing in the tree called
`UseForwardedHeaders` (`grep -rn "ForwardedHeaders" src` → no hits). The ACA ingress terminates TLS and
forwards over plain HTTP, so `ShareUrl` (`src/Agent42.Backend/Sharing/AgentApi.Sharing.cs`) minted
`http://chat.agent42.nl/shared/#<token>`. Observed directly: `GET https://chat.agent42.nl/shared`
returned `301 → http://chat.agent42.nl/shared/` — the framework's own `UseDefaultFiles` redirect building
an absolute URL from the wrong scheme on a request that arrived over TLS.

Sizing the two honestly: defect 1 makes the feature **non-functional**. Defect 2 is **cosmetic plus a
latent MCP bug** — `container-app.bicep` sets no `allowInsecure`, so ACA 301s http→https, the fragment
survives the hop, and the token (being in the fragment) never travelled in plaintext.

**The matching semantics were measured, not inferred** — against the live app (`ca-agent42-backend`,
`rg-agent42`) on 2026-08-20, by temporarily PUTting a widened `authConfigs/current` and curling. This is
the evidence, and the repo has no other way to observe ingress behaviour:

| Path | with `['/api/share','/shared']` | with `['/api/share/*','/shared','/shared/*']` |
|---|---|---|
| `/shared` | 301 (reaches app) | 301 |
| `/shared/` | **401** | 200 |
| /shared/viewer.js, /shared/viewer.css, /shared/lib/marked.min.js, /shared/lib/purify.min.js | **401** | 200 |
| `/api/share/{token}` | **401** | 404 (the app's fail-closed) |
| `/api/share/{token}/attachments/{id}` | **401** | 404 |
| `/api/models`, `/api/conversations`, `/api/conversations/x/share` | 401 | 401 |
| `/api/sharezzz`, `/sharedzzz` (boundary probes) | 401 | **401** |
| `/api/share/../models`, `/shared/../api/models` (`curl --path-as-is`) | — | **401** |
| `/api/share/..%2f..%2fmodels`, `/shared/..%2fapi%2fmodels` | — | **401** |

Five facts, each load-bearing:

1. A bare entry is an **exact** match — a prefix does *not* cover its sub-paths.
2. A `/*` entry **spans multiple segments** (`/api/share/*` covered `.../attachments/{id}`).
3. A `/*` entry matches an **empty** remainder (`/shared/*` covered `/shared/`).
4. The wildcard prefix **includes the trailing slash**: `/api/share/*` behaves as
   `StartsWith("/api/share/")`, so it cannot bleed onto a sibling route sharing a name stem.
5. **A wildcard entry is not a traversal bypass.** Raw and `%2f`-encoded dot-segments under an excluded
   prefix do not smuggle an authenticated path through — every probe stayed 401. This is the one new risk
   the wildcard introduces over exact matching, and it is measured, not assumed.

The wildcard form is documented in the App Service auth file-based configuration reference
(`"/path3/subpath/*"`); ACA runs the same Easy Auth middleware. The measurement is what makes it safe to
rely on for Container Apps specifically.

## Decision

**Half 1 — wildcard `excludedPaths`.** The list becomes:

```bicep
excludedPaths: [
  '/api/agent-run'
  '/api/share/*'
  '/shared'
  '/shared/*'
]
```

The bare `/api/share` is **dropped**: nothing is routed there (it 404'd even while excluded), so it was a
hole with nothing behind it. The bare `/shared` **stays** — it is what lets a hand-typed URL without the
trailing slash reach the app's courtesy 301.

Guarded by `tests/Agent42.Tests/EasyAuthExclusionTests.cs`, which parses the array out of the Bicep and
runs it through the **measured** matcher (`entry[..^1]` for a `/*` entry — keeping the slash, per fact 4;
`Equals` otherwise). It asserts that the viewer page, both public API routes and **every same-origin URL
the viewer actually requests** are covered; that `/`, `/index.html` and five authenticated `/api/*` routes
are **not**; that `/api/agent-run` still is; that the wildcard does not match a name stem; and — feeding
the matcher the old literal list — that the shipped bug is reproducible. The viewer URLs are **parsed** out
of `index.html`'s `src`/`href` attributes and `viewer.js`'s API templates rather than globbed from the
folder: "every file under `wwwroot/shared/` is covered" is tautological under `/shared/*` and can never
fail. The failure this must catch is someone pointing the viewer at the app's own /lib/marked.min.js or `/styles.css`
on the authenticated surface — verified by mutation, not assumed.

**Half 2 — trust `X-Forwarded-Proto`.** `UseForwardedHeaders` becomes the **first** middleware in
`Program.cs`, ahead of `UserTelemetryMiddleware` and of `UseDefaultFiles`/`UseStaticFiles` — the static-file
301 is built inside the framework, so anything later cannot fix it:

```csharp
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedProto,
    KnownNetworks = { },
    KnownProxies = { },
});
```

`XForwardedProto` **only** — no consumer here reads the client IP or host, and trusting those from an
unidentified proxy is a strictly larger surface for nothing. `ForwardLimit` stays at its default of 1 (a
single ACA ingress hop). `KnownNetworks`/`KnownProxies` must be **cleared**, because the ingress IP is not
knowable from inside the container. Guarded by a source-text ordering assertion
(`ProjectLayoutTests.Program_TrustsForwardedProtoBeforeAnyOtherMiddleware`) — nothing in the suite runs
`Program.cs`.

## Alternatives considered

**For half 1 — enumerate every public URL as its own exact entry** (`/shared/`, then one apiece for
viewer.js, viewer.css and each vendored lib) plus reshaping the two public API routes so the token stops
being a path segment. Rejected: it only uses the already-proven exact-match mechanism, but it pushes the
token into a query string or a custom header — reversing ADR 0035's deliberate fragment choice, or forcing
the viewer to fetch images as blobs — and it makes adding a file to `wwwroot/shared/` an infra change. The
measurement above removed the only reason to prefer it.

**For half 2 — a configured public base URL** (e.g. `App:PublicBaseUrl`). It pins scheme *and* host and
trusts no spoofable header at all, which would make the spoofing acceptance below unnecessary. Rejected on
three counts: it is a new **mandatory config input on both hosts** (the trap ADR 0029 documents), its value
must be hand-synced with the ingress FQDN, and it does not fix the framework's own static-file redirect —
which builds its URL from `Request.Scheme` regardless.

**For half 2 — `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` in Bicep.** Zero code. Rejected: it enables
`XForwardedFor` and `XForwardedHost` as well — a strictly larger trust surface than the two consumers need
— and it puts a load-bearing behaviour in an env var rather than in reviewable, testable code.

## Consequences

- **ADR 0035 works.** A recipient with the link opens the conversation without an Entra session, and the
  minted URL is `https://`.
- **`X-Forwarded-Proto` is spoofable**, because the known-proxy lists are cleared. Accepted: the blast
  radius is the scheme of a minted URL and of a static-file redirect; every external request arrives through
  the ingress that sets the header; only `XForwardedProto` is trusted; and the share token rides the
  fragment, never the path.
- **The MCP interactive-OAuth `redirect_uri` is repaired, and one case does not self-heal.**
  `src/Agent42.Backend/Mcp/AgentApi.Mcp.cs` builds it from the same `Request.Scheme`, so it has been sending
  `http://chat.agent42.nl/api/mcp/oauth/callback` while the registered URI is the `https://` one — ADR 0025's
  flow is very likely broken today (Entra refuses non-localhost `http://` web redirect URIs outright). The
  flip repairs it. **But** where a server supports dynamic client registration, the minted `ClientId` is
  **persisted on `StoredMcpServer` and reused forever** (the code registers only while `ClientId` is blank);
  a client registered under the `http://` URI will reject the corrected request with `invalid_redirect_uri`,
  permanently. Remediation is manual and user-facing: **clear that server's stored client id** in
  Preferences → MCP servers, which forces re-registration on the next connect. Deliberately not automated —
  auto-clearing would silently orphan registrations on servers that are working fine.
- **The wildcard inverts the default for anything added under `/api/share/` from now on.** Under the bare
  entry, a new route there was Easy-Auth-gated until someone changed the infra; under `/api/share/*` it is
  **public by construction**, with `GetUserId()` returning `local-dev` — precisely the trap the
  excluded-path gotcha's first consequence describes. This is the API-side price of the convenience the
  *Alternatives* section counts as a win on the static side ("adding a file to `wwwroot/shared/` is not an
  infra change"): the same property, and it cuts both ways. `EasyAuthExclusionTests`' negative list is a
  hand-written sample of authenticated paths, **not** a projection of the real route table, so it cannot
  catch a future leak here — the route comment in `src/Agent42.Backend/Sharing/AgentApi.Sharing.cs` carries
  the warning where someone adding a route will actually read it.
- **`/api/share/*` lets an unauthenticated caller force a `ShareLinkDirectoryGrain` activation and a Cosmos
  read per distinct token guess, and there is no rate limiter anywhere in the app** (`grep -rn "RateLimit"
  src` → no hits). This is the same accepted property as ADR 0022's `/api/agent-run`, and 256-bit tokens make
  guessing pointless — but this is the first time it is reachable *by design* from shipped Bicep, so it is
  stated here rather than inherited silently.
- **Production was running a hand-applied ARM patch** with the corrected list, so sharing worked in prod
  before this change while existing only in ARM, not in Bicep — the next deploy from unmodified `main` would
  have silently reverted it. This ADR makes it durable.
- **The verification discipline that was skipped is now cheap to repeat.** The gotcha's *"verify at deploy"*
  line was the control that would have caught this on day one; the post-deploy curl matrix in the spec is
  what it looks like when actually run.
