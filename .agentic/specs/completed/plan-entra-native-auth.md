# Plan: native Entra ID sign-in (replaces Easy Auth when running outside Azure Container Apps)

planned-against: main @ 8d176fa

## Overview

Easy Auth only exists inside Azure Container Apps / App Service. Agent42 has no auth code of its own: it trusts the
`X-MS-CLIENT-PRINCIPAL*` headers (`Api/CurrentUser.cs`, ADR 0002). Running the image in plain Docker therefore gives every
caller the `local-dev` user (ADR 0127). Goal: an opt-in **`Auth:Mode=entra`** that does the Entra OIDC sign-in **inside the
Backend**, so Agent42 can run in plain Docker against Azure storage (Cosmos + Blob), keyed by the user's real Entra object
id, with every current feature intact. Default (`Auth:Mode` absent / `easyauth`) changes nothing; prod keeps Easy Auth.

Design choice (user, this session): "option 5" = in-process OIDC, not a reverse proxy.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/Program.cs` — composition root; `UseForwardedHeaders` is first (line ~475), then `UserTelemetryMiddleware`,
  `UseOwnerGate`, `UseAgentMcpKeyGate`, `UseLocalBridge`, `UseDefaultFiles/UseStaticFiles`, `MapAgentApi`. The new auth
  middleware slots between `UseForwardedHeaders` and `UserTelemetryMiddleware`. S3776 is a build error at 15 — the feature's
  registration goes in its own folder (as `HindsightRegistration` / `StorageRegistration` do), not inline.
- `src/Agent42.Backend/Api/CurrentUser.cs` — the single header reader. **Must not change** except possibly the claim spellings.
- New folder `src/Agent42.Backend/Auth/` (flat namespace `Agent42.Backend`, ADR 0039/0040): registration, header-stamping
  middleware, `/.auth/*` endpoints, public-path matcher.
- `src/Agent42.Backend/Storage/AzureStorageRegistration.cs` — the Data Protection key ring is persisted to Blob only on the
  `Storage:BlobEndpoint` path. Cookie auth makes the ring load-bearing for **sessions** too, not only provider API keys.
- `src/Agent42.Backend/Web/SearxngProxy.cs` — already allowlists cookies by the `sxng_` prefix; verify the new cookie never
  forwards. No code change expected.
- `src/Agent42.Backend/wwwroot/index.html:136` (sign-out link `/.auth/logout?post_logout_redirect_uri=/`) and
  `wwwroot/sw.js:104` (bypasses `/.auth`): both keep working if the endpoints keep the Easy Auth URL shapes. No `wwwroot/js/`
  change planned; if one becomes necessary, bump `CACHE` in `sw.js` (cross-cutting rule 3).
- `tools/Agent42.Android` — contract: loads `/.auth/login/aad?post_login_redirect_uri=/`, reads cookie `AppServiceAuthSession`
  from the WebView jar, then sends it as `Cookie:`. `Credentials.kt` treats **401, any redirect, or an HTML body where JSON was
  expected** as "sign in again" and never follows redirects. **No Android change planned.**
- `infra/modules/container-app.bicep` — `excludedPaths` (line ~467) is the **source of truth** for the public list; do not edit.
- `tests/Agent42.Tests/` — `EasyAuthExclusionTests` (matcher + Bicep list), `ProjectLayoutTests` (middleware order pins),
  `ComposeFileTests`, `AgentApiTests` (TestServer).
- Docker: new `compose.azure.yaml` override + `docs/features/self-hosting.md` section (or a sibling doc) + `.env.example` additions.
- `.agentic/decisions/` — new ADR (next free number; check `ls`), amends 0002, 0042, 0090, 0121, 0127.
- `Directory.Packages.props` — add the OIDC package(s); **resolve the version with the tooling, never from memory** (hard rule 4).

## Context & constraints (what the executor cannot cheaply re-derive)

1. **Identity must keep entering through the same headers.** `CurrentUser`, aliases (ADR 0074), `IOwnerPolicy`/`OwnerGate`
   (ADR 0121), `UserTelemetryMiddleware`, usage scoping and `NonOwnerContentRedactor` all read `X-MS-CLIENT-PRINCIPAL-ID`
   (+ `-NAME`, `-IDP`, and the base64 `X-MS-CLIENT-PRINCIPAL` claims blob). So the new code is an **adapter**: authenticate with
   OIDC, then *synthesize those headers* on the request. No consumer is touched. Evidence: `Api/CurrentUser.cs`.
2. **Header trust is the whole security model.** In Azure the ingress strips inbound `X-MS-CLIENT-*` (ADR 0002). In Docker
   nothing does. In `entra` mode the middleware must **remove every inbound `X-MS-CLIENT-*` header first**, then set them only
   from an authenticated principal. An unauthenticated request on a public path must carry *no* principal id (so `GetUserId()`
   returns `local-dev`, exactly like an Easy Auth excluded path — gotcha "An Easy-Auth `excludedPaths` endpoint is publicly
   reachable…": excluded endpoints take the userId from a directory grain, never `GetUserId`).
3. **Public paths = the Bicep `excludedPaths`, with its matcher.** Exact match unless the entry ends in `/*`; `/*` spans any
   number of segments, matches an empty remainder, keeps the trailing slash (ADR 0042, measured). Currently nine entries:
   `/api/agent-run`, `/api/share/*`, `/shared`, `/shared/*`, `/api/agent-mcp`, `/api/agent-mcp/*`, `/api/local-bridge`,
   `/api/email-in`, `/legal/*`. Recount from the Bicep, do not trust this sentence. The in-app list must be **derived from or
   test-pinned to the Bicep list**, never a second hand-maintained copy that can drift (that drift is what shipped a dead
   feature in ADR 0035). Prefer: one `PublicPaths` constant in code + a test that parses the Bicep and asserts set-equality.
4. **Endpoints that must stay BEHIND auth** (never public): the MCP OAuth callback `/api/mcp/oauth/callback` and `/start`
   (gotcha "MCP interactive OAuth is hand-rolled…"), `/api/providers/github/*`, `/dashboard`, `/logs`. `GetUserId()` for the
   returning browser comes from the session cookie.
5. **Browser vs API shape.** Easy Auth answers an unauthenticated *browser* navigation with a 302 to Entra and an
   *API-shaped* request (Android, fetch with `Accept: application/json`) with a bare 401 (`Credentials.kt` comment). Reproduce
   that split (challenge for navigations, 401 for `Accept: application/json` / non-GET / `X-Requested-With`), and **never
   redirect a POST** (the Web Share Target gotcha: a redirected POST loses its body; the SW stash exists for that).
6. **Cookie contract.** Name `AppServiceAuthSession`, path `/`, `HttpOnly`, `Secure` (needs `Request.Scheme == https`, hence
   `UseForwardedHeaders` first and the proxy/TLS requirement), `SameSite=Lax`, fixed 14-day lifetime (ADR 0090, no sliding
   expiry), non-persistent token storage (the app never reads an access/refresh token, so do **not** request or store tokens;
   `SaveTokens=false`). `post_login_redirect_uri` must be **validated as a same-origin relative path** (open-redirect).
7. **Issuer/audience.** Tenant-specific issuer (`https://login.microsoftonline.com/{tenant}/v2.0`), single-tenant, invite-only via
   "assignment required" on the enterprise app (ADR 0121 amendment). No `allowedPrincipals` equivalent; document the
   `az ad sp show … appRoleAssignmentRequired` check. No `login_hint` (ADR 0121). The user id is the **`oid`** claim (Easy Auth's
   `X-MS-CLIENT-PRINCIPAL-ID`), **not** `sub` (pairwise, differs per app) — a wrong choice silently orphans all stored data. Pin with a test.
8. **Claim blob.** `CurrentUser.ParsePrincipalClaims` expects Easy Auth's `{"auth_typ":..,"claims":[{"typ","val"}]}` shape and
   matches claim *typ* candidates; emit `preferred_username`, `name`, `roles`, `oid`, so `user.email`/`full_name`/`roles`
   telemetry keeps working (gotcha "Easy Auth claim `typ` spellings…" — a prod-only fact; measure, do not assume).
9. **`/.auth/*` surface** (URL-compatible with Easy Auth so the sidebar link, SW bypass and Android keep working):
   `GET /.auth/login/aad[?post_login_redirect_uri=]`, `GET /.auth/logout[?post_logout_redirect_uri=]` (clears the cookie **and**
   ends the Entra session via OIDC sign-out — ADR 0090's "must end the Entra session" check), `GET /.auth/me` (JSON, 401 if
   anonymous). These live **above** the auth challenge and are not subject to the fallback policy.
10. **Data Protection.** Cookies are encrypted with the DP ring. The ring persists to Blob only when `Storage:BlobEndpoint` is set
    and `ConnectionStrings:blobs` is absent (`AzureStorageRegistration`). In Docker with a connection string the ring is ephemeral
    ⇒ every restart signs everyone out **and** orphans stored provider keys / chat-bot credentials. `entra` mode with Azure
    storage should require the endpoint path; with local storage (ADR 0127) the key ring is already on the volume. Add a
    **config-health** check (ADR 0029) that fails loudly instead of silently running with an ephemeral ring.
11. **Accepted risk carried over, not widened.** The Copilot shell / user-supplied MCP & provider endpoints can forge the principal
    header against the app's own localhost (ADR 0121, gotcha 14). That is true today and stays true. In `entra` mode the same
    caveat applies to the new middleware: stripping inbound headers protects against the *network*, not a process inside the
    container. Say so in the ADR; keep `Copilot__BuiltInToolsEnabled=false` in the compose override; never write "only owners can".
12. **Single replica / in-process state is already load-bearing** (rule 12). OIDC correlation/nonce cookies are cookie-based, so
    no new server-side state is introduced.
13. **Mode matrix.** `Auth:Mode` ∈ `easyauth` (default; do nothing — absent must equal today's behaviour, so prod's Bicep needs no
    change), `entra`, and the existing implicit "no auth" (local storage / DevHost). `entra` with a missing client id / tenant /
    secret **fails startup** with a clear message (do not fall back to `local-dev`: failing open to an anonymous owner is worse
    than not booting). `entra` + `Storage:Provider=local` is allowed (self-host behind real sign-in).
14. **Access:Owners** keeps working unchanged (object ids). Document that the owner list for a Docker deployment is the user's own
    object id; `local-dev` is **never** listed in `entra` mode (ADR 0062/0121).
16. **Gate = explicit middleware, NOT the fallback policy (critic H1).** `UseDefaultFiles`/`UseStaticFiles` are terminal middleware, not
    endpoints, so a fallback authorization policy never sees `/index.html`, `/logs`, `/sw.js` or `/shared/*`. Easy Auth gated all of
    those at the ingress. In `entra` mode an explicit `AuthGateMiddleware` sits **before** `UseDefaultFiles` and challenges/401s
    anything not authenticated unless it matches `PublicPaths` or `/.auth/*`. Decision for the browser-fetched PWA assets
    (`/manifest.webmanifest`, `/sw.js`, `/icons/*`): they stay **gated**, as under Easy Auth today (same-origin requests carry the
    session cookie); the Step 11 manual pass verifies that installing the PWA still works.
17. **Pre-pipeline telemetry enrichment reads raw headers (critic H2).** `Program.cs:59-60` (`EnrichWithHttpRequest` ->
    `ApplyUserTelemetryTags(request.Headers, ...)`) runs at `HttpRequestIn.Start`, before any middleware, so in `entra` mode it would
    tag server spans with a **forged** `user.id`/`email`/`roles` (feeding `/logs`, `/usage` and `NonOwnerContentRedactor`) and tag an
    authenticated user's spans with nothing. In `entra` mode that enrichment becomes a no-op and the stamper re-applies
    `user.*` tags onto `Activity.Current` after it has stamped the headers.
18. **Android never sends `Accept: application/json`** (checked `Credentials.kt`: only `Cookie` is set). So "API-shaped" must not be
    detected by `Accept`. Rule: **redirect only a GET whose `Accept` contains `text/html`** (a browser/WebView navigation); everything
    else (no `Accept`, non-GET, `application/json`, `text/event-stream`, fetch/XHR) gets a bare 401. Android's own detection
    (401, any redirect, or `text/html`) then triggers its "sign in again" path.
19. **Cookie size / chunking (critic M5).** ASP.NET's `ChunkingCookieManager` splits an oversized ticket into `AppServiceAuthSession` +
    `...C1`; Android sends only the one cookie, so an oversized ticket means an endless sign-in loop. Keep the ticket small:
    `SaveTokens=false`, and trim the principal to `oid`, `name`, `preferred_username`, `roles` before it is stored (drop groups etc.).
    Assert it stays a single cookie.
20. **Public base URL (critic M4).** OIDC builds `redirect_uri` from `Request.Host`, and the forwarded-headers pin forbids trusting
    `XForwardedHost` (`ProjectLayoutTests`, ADR 0042) - do not weaken it. Add `Auth:PublicBaseUrl` (optional; absent = request host) used
    for the OIDC redirect/sign-out URIs when a proxy rewrites Host. Cookie `Secure` follows the effective scheme.
21. **`UseTokenLifetime=false`** (critic M8): the session is the fixed 14 days of ADR 0090 (`SlidingExpiration=false`), never the
    id-token lifetime.
22. **CSRF drift (critic L10).** Easy Auth rejects a write with `Origin: null` (403, see gotchas.md); a cookie scheme has only
    `SameSite=Lax`. The gate middleware adds an `Origin`/`Sec-Fetch-Site` check for non-GET requests on non-public paths (reject
    cross-site), or the ADR records the difference as accepted. Decide in step 1; default = add the check.
15. Credential for Azure from a container: `DefaultAzureCredential` via `AZURE_TENANT_ID` / `AZURE_CLIENT_ID` /
    `AZURE_CLIENT_SECRET` (see `AzureCredentialSettings`); the executor documents this and **does not run any role assignment**
    (hard rule 5) — the plan hands the exact `az` commands to the user.

## Steps

> Each step: write its tests alongside; run the narrowest matching test immediately. Hold to the mode matrix: nothing in steps
> 1–8 may change behaviour when `Auth:Mode` is absent.

1. **ADR (needs `/decide`).** Record: in-app OIDC as an opt-in alternative to Easy Auth; alternatives rejected (reverse proxy +
   header rewrite — `oauth2-proxy`; App Service Auth; Front Door — see this session's option table); amends 0002/0042/0090/0121/0127.
   Include the accepted-risk paragraph (context 11). Verify: `ls .agentic/decisions`, supersession check, `ProjectLayoutTests`
   doc-path integrity.
2. **Mode setting.** `Auth/AuthMode.cs`: `AuthModeSetting.Read(IConfiguration)` mirroring `StorageProviderSetting`. Unknown value →
   startup error. Verify: new `AuthModeOptionsTests` (absent ⇒ `EasyAuth`; `entra`; garbage throws).
3. **Public-path matcher.** `Auth/PublicPaths.cs`: the measured matcher (exact unless `/*`, case-insensitive, trailing-slash-keeping)
   and the list. Verify: `PublicPathsTests` incl. `/shared/`, `/api/share/x/y`, `/api/sharezzz` gated, `/api/share/../models` gated,
   percent-encoded traversal gated. **Plus** a test that parses `container-app.bicep` and asserts the code list equals the Bicep list
   (so the two cannot drift). Parallelizable with 2.
4. **Header stamping middleware.** `Auth/PrincipalHeaderMiddleware.cs`: (a) always delete inbound `X-MS-CLIENT-*`; (b) if the
   request is authenticated, set `-ID` (= `oid`), `-NAME` (= `preferred_username`), `-IDP` (= `aad`), and the base64 claims blob
   in Easy Auth shape. Verify: `PrincipalHeaderTests` — a forged `X-MS-CLIENT-PRINCIPAL-ID` on an anonymous request is gone; on an
   authenticated request it is replaced, not appended; `CurrentUser.GetUserId` / `GetUserTelemetryContext` over the produced headers
   return the expected id/email/roles (round-trip through the real parser). Also: header names matched case-insensitively, repeated values removed; stripping runs on **public paths too** (a forged header on
   `/api/agent-run` must never reach `GetUserId`); it also re-applies `user.*` tags to `Activity.Current` (context 17). Add
   `PrincipalHeaderTests.ForgedHeader_OnPublicPath_NeverReachesGetUserId` and `ServerSpan_UserTags_ComeFromPrincipalNotForgedHeader`.
   Depends on 2.
5. **OIDC + cookie registration** `Auth/EntraAuthRegistration.cs`: package (resolve version via `dotnet add package`), cookie
   `AppServiceAuthSession` 14-day fixed, `SaveTokens=false`, no token request beyond the id token, tenant issuer, client secret from
   config, `MapInboundClaims=false`, `oid` required (reject a token without it). Challenge scheme = OIDC. Browser-vs-API response
   split via cookie events (`OnRedirectToLogin` → 401 for API-shaped requests). Fallback authorization policy = require authenticated
   user; `PublicPaths` match → anonymous. Verify: `EntraAuthTests` with a fake OIDC handler on TestServer — anonymous browser GET ⇒
   302; anonymous `Accept: application/json` ⇒ 401; anonymous POST ⇒ 401 (never 302); public paths ⇒ pass-through. Add `AuthGateMiddleware` (context 16/18/22): anonymous non-public request = 302 only for `GET` + `Accept: text/html`, else 401; applies to
   static files too; `UseTokenLifetime=false`, single-cookie trim (context 19). Extra tests: `Anonymous_StaticFile_Challenged`
   (`/index.html`, `/logs`, `/sw.js`; `/shared/` stays public), `Android_Request_NoAccept_Gets401`, `Cookie_StaysSingleChunk`,
   `CrossSiteWrite_Rejected`. Depends on 2-4.
6. **`/.auth/*` endpoints.** `Auth/AuthEndpoints.cs` (`MapAuthEndpoints`, called **from `Program.cs`, only in `entra` mode**, after the gate; not an `AgentApi.*` partial, so
   `MapAgentApi` does not own it). Pin both the call and its mode gating in `ProjectLayoutTests` (source text), since nothing runs `Program.cs`.
   Also configure OIDC `SignedOutCallbackPath`/`RemoteSignOutPath` and assert them in a test (Entra-session sign-out, ADR 0090). `post_login_redirect_uri` / `post_logout_redirect_uri` validated as same-origin
   relative paths (reject `//evil`, `https://…`, `\\`). Verify: `AuthEndpointTests` (open-redirect matrix, `/.auth/me` 200/401,
   logout clears the cookie). Depends on 5.
7. **Wire into `Program.cs` + `ProjectLayoutTests` pins.** Register only when `Mode == Entra`. Order:
   `UseForwardedHeaders` → `UseAuthentication` → `PrincipalHeaderMiddleware` → `UserTelemetryMiddleware` → `UseAuthorization` →
   (existing gates) — the stamper must precede `UserTelemetryMiddleware` because that reads the headers; `AuthGateMiddleware` goes right after the stamper and **before** `UseOwnerGate`, `UseAgentMcpKeyGate`, `UseLocalBridge`
   and `UseDefaultFiles/UseStaticFiles`; stamper and gate must precede `UseWebSockets` (inside `UseLocalBridge`). The `EnrichWithHttpRequest`
   delegate is skipped in `entra` mode (context 17). Add a source-text pin that registration is skipped when the mode is not `entra`. Verify: extend `Program_TrustsForwardedProtoBeforeAnyOtherMiddleware` and add a pin that the
   stamper precedes `UserTelemetryMiddleware` and that `UseDefaultFiles/UseStaticFiles` remain after the gates (gated `/logs` is a
   static page — gotcha in `Program.cs` comments).
8. **Fail-closed startup + config health.** `entra` without tenant/client id/secret ⇒ throw. Also a **startup warning** (like `LocalModeWarning`) when the Data Protection ring is not persisted in `entra` mode: the Azure path needs
   `Storage:BlobEndpoint` (not `ConnectionStrings:blobs`) and the SP's Blob data role; the local-storage path already persists the ring to the
   volume (`LocalStorageRegistration.cs:77`). Config-health row (ADR 0029): auth mode,
   whether the DP ring is persisted, whether `Access:Owners` contains `local-dev` (red). Verify: extend `ConfigHealthTests`.
9. **Compose override.** `compose.azure.yaml` (Auth__Mode=entra, Cosmos/Blob endpoints, `AZURE_*` env, no Postgres, `Access__Owners__0`
   = object id placeholder, built-in Copilot tools off, port bound to 127.0.0.1 unless a TLS-terminating proxy is added), `.env.example`
   additions, docs section. List the `AZURE_*` SP env vars and, in the docs, the Cosmos data-plane + Storage Blob Data Contributor roles (covering `dataprotection/keys.xml`)
   as user-run commands. Cookie `Secure` needs HTTPS: document `localhost` as the secure-context exception **and** what to do on a
   LAN host (put Caddy/Traefik in front; `UseForwardedHeaders` already trusts `X-Forwarded-Proto`). Verify: extend `ComposeFileTests`.
   Parallelizable with 8.
10. **Docs, GitHub Pages site and gates.** The published site is MkDocs Material (`mkdocs.yml`, `docs/`, deployed by
    `.github/workflows/docs.yml` to GitHub Pages) and is built with `strict: true`, so a broken link or a nav entry pointing at a
    missing file fails the build. Work:
    - **New page** `docs/setup/docker-entra.md` ("Run in Docker with Azure storage and Entra sign-in"): when to use it vs Easy Auth vs
      local storage, the `Auth__*` settings, the `compose.azure.yaml` override, the service principal env vars, the Entra app
      registration (redirect URI `<base>/signin-oidc` or the chosen callback, "assignment required" + the
      `az ad sp show ... appRoleAssignmentRequired` check), the Cosmos/Blob role assignments as **user-run** commands (hard rule 5),
      `Auth:PublicBaseUrl` behind a proxy, HTTPS/cookie `Secure` notes, the accepted-risk paragraph (Copilot shell can forge the header;
      keep built-in tools off), and troubleshooting (signed out on restart = Data Protection ring, redirect_uri mismatch, AADSTS50105).
      Plain-language, user-facing: link to ADRs only through the existing repo-relative convention the other pages use.
    - **`mkdocs.yml` nav**: add the page under "Run it" (next to "Self-hosting"). A page missing from nav is not a build error but is
      unreachable, so it is required.
    - **Update existing pages** (each gets a short section or pointer, no duplicated detail): `docs/setup/local.md` and
      `docs/features/self-hosting.md` (self-hosting with real sign-in instead of `local-dev`; replace "no authentication" wording where
      it would now be wrong), `docs/setup/azure.md` + `docs/features/azure-deployment.md` (Easy Auth stays the Azure default; link to the
      Docker option), `docs/features/pwa-and-sign-in.md` (sign-in/sign-out and the 14-day session in both modes; the cookie is shared
      by Android), `docs/features/configuration-health.md` (the new auth rows), `docs/index.md` and `docs/features/README.md` /
      repo `README.md` if they list run options, and `compose.yaml` header comment + `.env.example` (point at the new page).
    - **Build gate**: run `pip install -r requirements-docs.txt` and `NO_MKDOCS_2_WARNING=1 mkdocs build --strict --site-dir
      <scratchpad dir>` locally and report the real output (do not claim it passed without running it). CI's `docs.yml` job is the
      authority; it only triggers on `docs/**`, `mkdocs.yml`, `requirements-docs.txt` and its own file, which this change touches.
    - **Repo docs**: `CLAUDE.md` (Commands table: the Docker + Azure storage + Entra run line; feature map row for auth; hard-rule-2
      consistency: fix any "Easy Auth only" claim), `.agentic/gotchas.md` (`/gotcha`): "in `entra` mode the header stamper and gate
      middleware are the trust boundary; `oid`, not `sub`; public list pinned to Bicep; static files are not covered by authorization
      policies", and `/sync-docs` - include the `CurrentUser.cs` remarks ("ingress strips..."), the `SearxngProxy` docs and the
      SearXNG-proxy gotcha (add a test that the new cookie is not forwarded to the sidecar). Run `/audit-docs` at the end.
11. **Manual verification pass (needs the user; not automatable).** Real Entra: sign-in, 14-day cookie, sign-out ends the Entra
    session (account picker returns), assigned vs unassigned user (AADSTS50105), Android sign-in round-trip, share link / agent-run /
    MCP / email-in as an anonymous caller, `/dashboard` as owner vs non-owner. Hand the user the `az` commands for the redirect URI
    registration (`http://localhost:<port>/signin-oidc` or the chosen path) and the Cosmos/Blob data-plane role assignments.

## Acceptance criteria → test mapping

| AC | Test | File |
|----|------|------|
| Absent `Auth:Mode` leaves every request path identical to today | `AuthMode_Absent_IsEasyAuth` (+ no auth services registered) | `AuthModeOptionsTests.cs` |
| Unknown mode / missing client id, tenant or secret fails startup | `Entra_MissingSettings_Throws` | `AuthModeOptionsTests.cs` |
| Forged inbound `X-MS-CLIENT-*` never reaches the app | `Anonymous_ForgedPrincipalHeader_IsStripped`, `Authenticated_ClientHeader_IsReplaced` | `PrincipalHeaderTests.cs` |
| User id is the `oid` claim and round-trips through `CurrentUser` | `Principal_UsesOid_NotSub`, `RoundTrip_ThroughCurrentUser` | `PrincipalHeaderTests.cs` |
| Token without `oid` is rejected | `Token_WithoutOid_Rejected` | `EntraAuthTests.cs` |
| Public list equals the Bicep `excludedPaths` | `PublicPaths_EqualBicepExcludedPaths` | `PublicPathsTests.cs` |
| Matcher semantics (exact unless `/*`, stem, traversal) | `PublicPaths_Matcher_*` | `PublicPathsTests.cs` |
| Anonymous browser GET ⇒ redirect; API-shaped / POST ⇒ 401, never 302 | `Anonymous_Browser_Redirects`, `Anonymous_Api_401`, `Anonymous_Post_401` | `EntraAuthTests.cs` |
| MCP OAuth callback and `/dashboard` stay gated | `ProtectedPaths_AreNotPublic` | `PublicPathsTests.cs` |
| Cookie is `AppServiceAuthSession`, HttpOnly, Secure, 14 days fixed, no tokens saved | `SessionCookie_Contract` | `EntraAuthTests.cs` |
| Redirect params cannot leave the origin | `PostLoginRedirect_RejectsAbsoluteAndProtocolRelative` | `AuthEndpointTests.cs` |
| `/.auth/me` and logout behave | `AuthMe_*`, `Logout_ClearsCookie` | `AuthEndpointTests.cs` |
| Stamper precedes telemetry middleware; forwarded headers first | `Program_*` pins | `ProjectLayoutTests.cs` |
| `/.auth/*` endpoints are mapped (reachability) | extend reachability test | `AgentApiReachabilityTests.cs` |
| Ephemeral DP ring / `local-dev` owner flagged | `ConfigHealth_Entra_*` | `ConfigHealthTests.cs` |
| Anonymous static content (`/index.html`, `/logs`, `/sw.js`) is challenged; `/shared/` stays public | `Anonymous_StaticFile_Challenged` | `EntraAuthTests.cs` |
| Forged header on a public path never reaches `GetUserId`; span tags come from the principal | `ForgedHeader_OnPublicPath_*`, `ServerSpan_UserTags_*` | `PrincipalHeaderTests.cs` |
| Android request (cookie only, no `Accept`) gets 401, not a redirect; cookie stays one chunk | `Android_Request_NoAccept_Gets401`, `Cookie_StaysSingleChunk` | `EntraAuthTests.cs` |
| Cross-site write rejected | `CrossSiteWrite_Rejected` | `EntraAuthTests.cs` |
| Mode-gated registration and `MapAuthEndpoints` wiring pinned | source-text pins | `ProjectLayoutTests.cs` |
| The Pages site builds strictly and the new page is in the nav | `mkdocs build --strict` run locally and in CI `docs.yml`; plus a source-text test that every `docs/**` page named in this plan is reachable from `mkdocs.yml` nav | `mkdocs.yml`, `tests/Agent42.Tests` doc-path integrity test (extend the existing one if present, otherwise add `DocsNavTests.cs`) |
| Compose override well-formed, loopback-bound, shell off | `ComposeAzure_*` | `ComposeFileTests.cs` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml` at plan time)

- `coverage_new_code_pct_min`: **80** — `node .agentic/scripts/check-new-code-coverage.mjs`
- `cognitive_complexity_max`: **15** — S3776 at build; `dotnet build Agent42.slnx`
- `dependencies.vulnerable_packages`: **0** (hard block); `add_policy: latest-stable` (resolve the OIDC package from the feed)
- `mkdocs build --strict` (workflow `docs.yml`, runs on any `docs/**` / `mkdocs.yml` change) must pass.
- CI also blocks on build, tests, `dotnet format --verify-no-changes`, doc-path integrity, `bicep build`.
- Never edit `quality-gates.yaml`; report SKIPPED rather than estimating.

## Out of scope

- Multi-tenant / "any Microsoft account" sign-in (ADR 0121 chose invite-only).
- Replacing Easy Auth in the Azure deployment, or any Bicep change.
- Rewriting unrelated feature pages or the nav structure beyond the one new entry.
- Android app changes; Token acquisition (OBO / Graph) — the app never reads an access token.
- A real fix for the header-forging caveat (needs process isolation of the Copilot shell; separate decision).
- Role assignments (handed to the user, hard rule 5); automated live-Entra tests.
- Local-bridge/`/api/agent-mcp` key schemes — unchanged, still self-enforcing.

## Review

Critic verdict: **not a clean pass** (2 HIGH, several MEDIUM). Handling:

- **H1 static files fail open** - accepted, verified (`Program.cs` static-file middleware is terminal). Added `AuthGateMiddleware` (context 16, steps 5/7) + test.
- **H2 pre-pipeline span enrichment reads forged headers** - accepted, verified (`Program.cs:59-60`). Context 17, steps 4/7 + tests.
- **M3 stripping scope** - accepted (step 4: case-insensitive, public paths, ordering vs `UseWebSockets`).
- **M4 forwarded host** - accepted; `Auth:PublicBaseUrl` instead of touching the `XForwardedHost` pin (context 20).
- **M5 Android** - accepted and verified: Android sends no `Accept`, so the heuristic was replaced (context 18); chunking risk added (19).
- **M6 DP ring** - accepted; startup warning added; local-storage claim now has evidence (`LocalStorageRegistration.cs:77`).
- **M7 AC gaps** - accepted (new AC rows, source-text pins). Health endpoints are dev-only (`MapDefaultEndpoints`) and `MapOrleansDashboardUi`
  stays behind `OwnerGate`; the ADR states this rather than adding code.
- **M8 `UseTokenLifetime`, sign-out callbacks** - accepted (context 21, step 6).
- **L9 step 6 wording** - fixed. **L10 CSRF** - accepted as default behaviour (context 22). **L12 doc drift** - added to step 10.
- **L13 gate thresholds** - copied from `.agentic/quality-gates.yaml` (80 / 15 / 0) and matched when this plan was written.
- Rejected: none.

## Implementation notes (divergences from the plan)

Executed on `feature/entra-native-auth` (2026-11-25), ADR 0145.

- **No fallback authorization policy and no `UseAuthorization`.** Context 16 already made the explicit `AuthGateMiddleware` the
  gate (it must sit above the terminal static-file middleware), so a second, weaker policy layer added nothing and nothing in
  the app uses `[Authorize]`. Step 5/7's "fallback policy" and `UseAuthorization` were dropped.
- **Middleware order is `UseForwardedHeaders` → `UseAuthentication` → stamper → gate → `UserTelemetryMiddleware` → owner/MCP/bridge
  gates → static files.** The plan had telemetry before the gate; the gate goes first so a rejected request never reaches the
  telemetry carrier. Pinned in `ProjectLayoutTests`.
- **Settings keys**: `Auth:Entra:TenantId|ClientId|ClientSecret` and `Auth:PublicBaseUrl` (the plan left the names open).
- **OIDC uses `response_mode=query`** (the code-flow default, so the parameter is omitted) with Lax correlation/nonce cookies,
  instead of the handler's `form_post` default. A SameSite=None cookie must be Secure, which would break plain `http://localhost`.
  Session cookie `Secure` is `SameAsRequest` (follows the forwarded scheme).
- **`compose.azure.yaml` is a standalone file, not an override.** Compose cannot remove the `postgres` dependency `compose.yaml`
  gives the app (that needs `!override`, Compose 2.24+). Run it with `-f compose.azure.yaml` alone. Its `Access__Owners__0` is
  `${OWNER_OBJECT_ID:?}` (required), not a placeholder.
- **Tests landed in new files** rather than extending existing ones: `AuthConfigHealthTests` (not `ConfigHealthTests`),
  `ComposeAzureFileTests` (not `ComposeFileTests`, whose loader is hard-wired to `compose.yaml`), `EntraDocsAndProxyTests` (the
  SearXNG cookie guard and the nav check, instead of a `DocsNavTests`). `AgentApiReachabilityTests` was not extended:
  `/.auth/*` is not an `AgentApi.*` partial; its mapping and mode gating are pinned in `ProjectLayoutTests` and exercised in
  `AuthEndpointTests`.
- **`LocalModeWarning` is silent when `Auth:Mode=entra`** (its "no authentication" text would be false) and `ConfigHealth` gained
  an always-present `Auth:Mode` row; `Auth:DataProtectionKeyRing` and `Auth:OwnersLocalDev` appear only in `entra` mode.
- **Also changed**: ADR 0039/0040's folder inventory and `ProjectLayoutTests.ExpectedFolders` gained `Auth/`; `CurrentUser.cs`
  remarks; the `Microsoft.AspNetCore.Authentication.OpenIdConnect` package (11.0.0, resolved by `dotnet add package`).
- **Critic**: found an open redirect in the gate's return target (it skipped `SafeRelativeTarget`); fixed and tested.
- **Not done / left for the user (step 11):** the live-Entra manual pass, the app registration and the Cosmos/Blob role
  assignments (hard rule 5). `/audit-docs` was not run in this session.
- **Test run**: the full suite passes except five `Live*` tests that call a real model and get HTTP 401 in this environment.
