---
id: 0145
date: 2026-11-25
status: accepted
tags: [auth, entra, oidc, docker, self-hosting, easy-auth, trust-boundary]
supersedes: []
extends: [0002, 0042, 0090, 0121, 0127]
amends: [0002, 0042, 0090, 0121, 0127]
---

# Native Entra sign-in (`Auth:Mode=entra`) as an opt-in alternative to Easy Auth

## Context
Easy Auth exists only inside Azure Container Apps / App Service. Agent42 has no auth code of its own: it trusts the
`X-MS-CLIENT-PRINCIPAL*` headers (ADR 0002), so the image in plain Docker gives every caller the `local-dev` user
(ADR 0127). The goal is to run in Docker against Azure storage (Cosmos + Blob), keyed by the user's real Entra object id,
with every feature intact. Production keeps Easy Auth and must not change.

## Decision
1. **`Auth:Mode` = `easyauth` | `entra`.** Absent means `easyauth` (today's behaviour, no Bicep change). An unknown value,
   or `entra` without tenant / client id / secret, fails startup: failing open to an anonymous owner is worse than not booting.
2. **In-process OIDC that is an adapter, not a new identity model.** The Backend authenticates with OIDC, then synthesizes the
   same `X-MS-CLIENT-PRINCIPAL*` headers on the request, so `CurrentUser`, aliases, the owner tier, telemetry and usage scoping
   are untouched. The user id is the `oid` claim (Easy Auth's principal id), never `sub` (pairwise per app).
3. **The stamper and the gate are the trust boundary.** `PrincipalHeaderMiddleware` deletes every inbound `X-MS-CLIENT-*`
   header first (public paths too), then sets them only from an authenticated principal. `AuthGateMiddleware` runs before
   `UseDefaultFiles`/`UseStaticFiles` (static files are terminal middleware and never see a fallback authorization policy) and
   challenges anything not in `PublicPaths` or `/.auth/*`. A browser navigation (GET with `Accept: text/html`) is redirected;
   everything else gets a bare 401, never a redirected POST. Android sends no `Accept`, so detection cannot rely on it.
4. **`PublicPaths` is pinned to the Bicep `excludedPaths`** by a test (same matcher: exact unless `/*`, ADR 0042).
5. **Easy Auth contract kept URL- and cookie-compatible.** `/.auth/login/aad`, `/.auth/logout` (also ends the Entra session),
   `/.auth/me`; cookie `AppServiceAuthSession`, HttpOnly, Secure, SameSite=Lax, fixed 14 days (ADR 0090), no tokens saved, a
   trimmed principal so the cookie stays one chunk. Redirect parameters must be same-origin relative paths. The Android app and
   service worker keep working unchanged.
6. **Pre-pipeline span enrichment is disabled in `entra` mode**; the stamper re-applies `user.*` tags after it has stamped the
   headers, so a forged header cannot tag a span.
7. **Public base URL.** `Auth:PublicBaseUrl` (optional) supplies the OIDC redirect URIs behind a proxy; the forwarded-headers
   pin (`XForwardedProto` only) is not weakened.
8. **Data Protection ring must persist** (it now protects sessions as well as provider keys); config health and a startup
   warning flag an ephemeral ring.

## Alternatives considered
- **Reverse proxy that rewrites headers (`oauth2-proxy`)** — an extra container and a second place the header trust lives;
  the user chose in-process OIDC.
- **App Service Auth / Front Door** — they put the app back in Azure, which is what this option avoids.
- **Fallback authorization policy as the gate** — fails open for static files (`/index.html`, `/logs`, `/sw.js`).
- **Rewriting `CurrentUser` to read claims** — touches every consumer for no gain; an adapter keeps one header reader.

## Consequences
- Agent42 can run in plain Docker with real sign-in and Azure storage; prod is unchanged.
- **Accepted risk, not widened (ADR 0121):** the Copilot shell and user-supplied MCP / provider endpoints can forge the
  principal header against the app's own localhost. Stripping inbound headers protects against the network, not against a
  process inside the container. Keep `Copilot__BuiltInToolsEnabled=false` in the compose override and never write
  "only owners can" as a security claim.
- Health endpoints are dev-only (`MapDefaultEndpoints`); `MapOrleansDashboardUi` stays behind `OwnerGate`.
- CSRF: Easy Auth rejects `Origin: null` writes; the gate adds an `Origin` / `Sec-Fetch-Site` check on non-public writes.
  A write with `Sec-Fetch-Site: same-site` (a sibling subdomain) is rejected too: fail closed, since the UI and API share one origin.
- Single replica stays load-bearing (OIDC state is cookie-based, so no new server-side state).
- Needs a live-Entra manual pass (plan step 11); role assignments are user-run (hard rule 5).
