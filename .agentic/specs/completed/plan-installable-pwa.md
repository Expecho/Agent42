# Plan — Make Agent 42 an installable PWA (Android)

planned-against: main @ 15f5cbc

## Overview

Goal: make the Agent 42 web UI an **installable Progressive Web App** so it can be
installed to the Android home screen from Chrome and launched standalone (its own
window, no browser chrome). Why now: the user wants Agent 42 as a home-screen app on
their phone. The frontend is already a static app in `wwwroot/`; installability just
needs the three PWA primitives — a web app manifest, PNG icons (192 + 512), and a
service worker with a `fetch` handler — plus the `<link>`/registration wiring and a
test that proves the assets are served.

## Affected areas

Verified against the tree at `15f5cbc`:

- `src/Agent42.Backend/wwwroot/index.html` — add manifest link + PWA meta tags (modify)
- `src/Agent42.Backend/wwwroot/app.js` — register the service worker (modify; exemplar
  for frontend logic per gotchas)
- `src/Agent42.Backend/wwwroot/manifest.webmanifest` — new
- `src/Agent42.Backend/wwwroot/sw.js` — new (must sit at web root so its scope is `/`)
- `src/Agent42.Backend/wwwroot/icons/icon-192.png` — new
- `src/Agent42.Backend/wwwroot/icons/icon-512.png` — new
- `src/Agent42.Backend/wwwroot/icons/icon-maskable-512.png` — new
- `src/Agent42.Backend/Program.cs` — **only if** the content-type test proves the default
  provider does not serve `.webmanifest` (see Context risk #1); modify to add a
  `StaticFileOptions` MIME mapping. Otherwise untouched.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — new (HTTP-contract test)

## Context & constraints

Judgment/rationale the executor can't cheaply re-derive:

1. **`.webmanifest` MIME risk (verify, don't assume).** Static files are served by the
   default `app.UseStaticFiles()` in `Program.cs:101`. ASP.NET Core's default
   `FileExtensionContentTypeProvider` is *expected* to map `.webmanifest` →
   `application/manifest+json`. The `PwaAssetsTests` manifest test asserts this content
   type, so it is the verification. **If that test fails**, add the mapping in
   `Program.cs` via `new StaticFileOptions { ContentTypeProvider = <provider with
   .webmanifest and .json mappings> }` passed to `UseStaticFiles(...)` — do not
   hand-hack MIME anywhere else. Chrome will refuse to install if the manifest is served
   as `text/plain` or 404s, so this content-type assertion is load-bearing, not cosmetic.

2. **Everything is behind Entra Easy Auth at the ingress** (ADR 0002,
   `.agentic/decisions/0002-entra-easy-auth-gate.md`): UI *and* API. Consequences for the
   service worker design — these are requirements, not preferences:
   - The manifest, icons, `sw.js`, and `start_url` (`/`) are all same-origin and fetched
     with the session cookie from the already-authenticated page, so install works. Keep
     `start_url`/`scope` = `/` (same origin ⇒ cookie applies).
   - An expired session makes navigations return a **302 redirect to Entra login**. The
     service worker MUST be **network-first for navigations** so that redirect passes
     through untouched — never serve a cached `index.html` while online, or auth silently
     breaks. Cached shell is an **offline-only** fallback.
   - The SW MUST **bypass `/api/*` entirely** (no caching): those responses are
     per-user, auth-bearing, and streaming (NDJSON). Caching them would leak/stale chat
     state. This is asserted by a test.

3. **Frontend is deliberately vanilla JS** (CLAUDE.md; exemplar
   `wwwroot/app.js`). No build step, no SPA, no npm PWA plugin. Register the SW with plain
   `navigator.serviceWorker` guarded by feature-detection, appended to the existing IIFE
   in `app.js`. Do not introduce a bundler or framework.

4. **Brand palette** (from `wwwroot/styles.css` `:root`): background `#100f0d`
   (`--bg`), amber accent `#ffb13d` (`--amber`). Icons and `theme_color`/
   `background_color` use these so the standalone splash/status bar match the app. The
   existing favicon is an inline SVG "42" in amber (`index.html:11`) — icons echo it:
   amber "42" centred on `#100f0d`.

5. **Icons are committed binaries, generated once** (not at runtime). Generate them
   during execution with PowerShell + `System.Drawing` (available on this Windows host):
   fill `#100f0d`, draw bold monospace "42" in `#ffb13d`, centred. Regular icons use ~60%
   glyph height; the maskable icon uses ~45% so "42" stays inside the maskable safe zone.
   See Step 3 for the generation snippet.

6. **Design decision — minimal, safe caching (assumption, documented as the chosen
   scope).** The SW precaches only the static app shell and uses
   stale-while-revalidate for static GETs; it does not attempt offline chat. Rationale:
   Agent 42 needs a live LLM + auth to do anything useful, so "offline chat" is
   meaningless, and aggressive HTML/auth caching would break the Easy Auth flow (see #2).
   The user's stated goal is *installable on Android*, which this fully satisfies. Offline
   chat, background sync, and push are explicitly out of scope.

## Steps

Steps 1–5 are mostly independent edits but should land in one branch; do them in order
because the test (Step 6) gates the whole set. Steps 1 (manifest), 2 (icons), 3 (sw.js)
are parallelizable; 4–5 (wiring) depend on 1–3 existing.

**Step 1 — Web app manifest.** Create `wwwroot/manifest.webmanifest`:
```json
{
  "name": "Agent 42",
  "short_name": "Agent 42",
  "description": "Personal AI chat agent — the answer is ready.",
  "id": "/",
  "start_url": "/",
  "scope": "/",
  "display": "standalone",
  "orientation": "any",
  "background_color": "#100f0d",
  "theme_color": "#100f0d",
  "icons": [
    { "src": "/icons/icon-192.png", "sizes": "192x192", "type": "image/png", "purpose": "any" },
    { "src": "/icons/icon-512.png", "sizes": "512x512", "type": "image/png", "purpose": "any" },
    { "src": "/icons/icon-maskable-512.png", "sizes": "512x512", "type": "image/png", "purpose": "maskable" }
  ]
}
```
Verify: covered by Step 6 test (`Manifest_IsServed_WithInstallableFields`).

**Step 2 — Icons.** Generate the three PNGs into `wwwroot/icons/` with PowerShell:
```powershell
Add-Type -AssemblyName System.Drawing
function New-Icon([int]$size, [string]$path, [double]$glyphFrac) {
  $bmp = New-Object System.Drawing.Bitmap($size, $size)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
  $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
  $g.Clear([System.Drawing.ColorTranslator]::FromHtml('#100f0d'))
  $brush = New-Object System.Drawing.SolidBrush([System.Drawing.ColorTranslator]::FromHtml('#ffb13d'))
  $font = New-Object System.Drawing.Font('Consolas', [single]($size * $glyphFrac), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
  $fmt = New-Object System.Drawing.StringFormat
  $fmt.Alignment = [System.Drawing.StringAlignment]::Center
  $fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
  $g.DrawString('42', $font, $brush, (New-Object System.Drawing.RectangleF(0,0,$size,$size)), $fmt)
  $g.Dispose(); $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
}
New-Icon 192 'src/Agent42.Backend/wwwroot/icons/icon-192.png' 0.60
New-Icon 512 'src/Agent42.Backend/wwwroot/icons/icon-512.png' 0.60
New-Icon 512 'src/Agent42.Backend/wwwroot/icons/icon-maskable-512.png' 0.45
```
Verify: files exist and are non-empty PNGs (Step 6 asserts served as `image/png`);
eyeball one to confirm "42" renders.

**Step 3 — Service worker.** Create `wwwroot/sw.js` (root scope):
```js
/* Agent 42 PWA service worker — installability + safe static caching only.
   Never touches /api (auth-bearing, streaming) and is network-first for
   navigations so the Entra Easy Auth redirect is never masked. */
const CACHE = "agent42-shell-v1";
const SHELL = ["/", "/index.html", "/styles.css", "/app.js",
  "/lib/marked.min.js", "/lib/purify.min.js", "/manifest.webmanifest",
  "/icons/icon-192.png", "/icons/icon-512.png", "/icons/icon-maskable-512.png"];

self.addEventListener("install", (event) => {
  event.waitUntil(caches.open(CACHE).then((c) => c.addAll(SHELL)).then(() => self.skipWaiting()));
});

self.addEventListener("activate", (event) => {
  event.waitUntil(
    caches.keys()
      .then((keys) => Promise.all(keys.filter((k) => k !== CACHE).map((k) => caches.delete(k))))
      .then(() => self.clients.claim()));
});

self.addEventListener("fetch", (event) => {
  const req = event.request;
  if (req.method !== "GET") return;
  const url = new URL(req.url);
  if (url.origin !== self.location.origin) return;
  if (url.pathname.startsWith("/api")) return; // never cache chat/auth API

  if (req.mode === "navigate") {
    // Network-first: lets the Easy Auth 302 through; cached shell only when offline.
    event.respondWith(fetch(req).catch(() => caches.match("/index.html")));
    return;
  }

  // Stale-while-revalidate for static assets.
  event.respondWith(
    caches.match(req).then((cached) => {
      const network = fetch(req).then((res) => {
        if (res.ok) { const copy = res.clone(); caches.open(CACHE).then((c) => c.put(req, copy)); }
        return res;
      }).catch(() => cached);
      return cached || network;
    }));
});
```
Verify: Step 6 asserts `/sw.js` served as JS and that its source contains the `/api`
bypass guard.

**Step 4 — index.html wiring.** In `wwwroot/index.html` `<head>` (after the existing
`<link rel="icon" ...>` at line 11) add:
```html
  <link rel="manifest" href="/manifest.webmanifest" />
  <meta name="theme-color" content="#100f0d" />
  <meta name="mobile-web-app-capable" content="yes" />
  <meta name="apple-mobile-web-app-capable" content="yes" />
  <meta name="apple-mobile-web-app-status-bar-style" content="black-translucent" />
  <meta name="apple-mobile-web-app-title" content="Agent 42" />
  <link rel="apple-touch-icon" href="/icons/icon-192.png" />
```
Verify: Step 6 reads index.html and asserts the manifest link + theme-color are present.

**Step 5 — Service worker registration.** Append to the end of the IIFE in
`wwwroot/app.js` (before the closing `})();`):
```js
  /* ————— PWA: register the service worker ————— */
  if ("serviceWorker" in navigator) {
    window.addEventListener("load", () => {
      navigator.serviceWorker.register("/sw.js").catch((err) =>
        console.warn("Service worker registration failed", err));
    });
  }
```
Verify: Step 6 asserts app.js source contains `serviceWorker.register`.

**Step 6 — HTTP-contract + wiring test.** Add `tests/Agent42.Tests/PwaAssetsTests.cs`.
Mirror the philosophy of `AgentApiTests` (exercise the contract the browser depends on)
but for static assets. Because `AgentApiTests` builds a slim server that only maps the API
(no static files), this test stands up its own tiny TestServer with the real Backend
`wwwroot` as web root:
- Resolve the web root robustly: walk up from `AppContext.BaseDirectory` until a directory
  containing `Agent42.slnx` is found (repo root), then
  `src/Agent42.Backend/wwwroot`. (Fail the test with a clear message if not found.)
- Build `WebApplication.CreateSlimBuilder`, `WebHost.UseTestServer()`,
  `WebHost.UseWebRoot(<resolved wwwroot>)`, then `app.UseDefaultFiles(); app.UseStaticFiles();`
  — the same two middleware calls as `Program.cs:100-101`. Note: production/DevHost serve
  the same physical files but via the SDK static-web-assets file provider, not a raw
  `PhysicalFileProvider` over the source `wwwroot`; both resolve MIME through the default
  `FileExtensionContentTypeProvider`, so the content-type contract is equivalent. Keep the
  test's middleware in sync if Step 1/risk #1 forces a `StaticFileOptions` change.
- Tests:
  - `Manifest_IsServed_WithInstallableFields`: GET `/manifest.webmanifest` → 200,
    content-type `application/manifest+json`; parse JSON; assert `name`, `start_url`,
    `display == "standalone"`, and `icons` includes a 192x192 and a 512x512 PNG entry.
  - `ServiceWorker_IsServed_AsJavaScript`: GET `/sw.js` → 200, content-type is a
    JavaScript type (`text/javascript` or `application/javascript`).
  - `ServiceWorker_BypassesApiBeforeCaching`: read `/sw.js` body and assert the `/api`
    guard is present **and positioned before any caching** — i.e. the index of
    `url.pathname.startsWith("/api")` is less than the index of the first `caches`/
    `respondWith` token. This is a source-text heuristic, not a behavioral test (the repo
    has no JS test runner by design — vanilla-JS exemplar), so it can't prove the guard is
    *logically* correct, only that it precedes caching; the residual risk is covered by
    the `/gotcha` in Step 7 and manual DevTools verification. Guards the ADR-0002 invariant.
  - `Icons_AreServed_AsPng`: GET `/icons/icon-192.png`, `/icons/icon-512.png`, and
    `/icons/icon-maskable-512.png` → 200, content-type `image/png`, non-empty body.
  - `IndexHtml_LinksManifest_AndAppRegistersServiceWorker`: GET `/` (or `/index.html`) →
    contains `rel="manifest"` and `theme-color`; GET `/app.js` → contains
    `serviceWorker.register`.

Run: `dotnet test Agent42.slnx` (or filter to `PwaAssetsTests`) — must be green.

**Step 7 — Record the durable constraint (`/gotcha`).** The rule "the service worker
must be network-first for navigations and must bypass `/api` — or it masks the Entra Easy
Auth 302 / caches auth-bearing responses" is a trap the code can't express: it lives only
in a `sw.js` comment and this spec (which is archived to `specs/completed/` on completion).
After Steps 1–6 land, add a `.agentic/gotchas.md` **Constraints** entry via the `/gotcha`
skill — claim as above, evidence `src/Agent42.Backend/wwwroot/sw.js` +
`.agentic/decisions/0002-entra-easy-auth-gate.md`, dated — and add `wwwroot/sw.js` as a
"PWA service worker" exemplar row. This protects a future edit (e.g. "optimize" to
cache-first navigations) from silently breaking auth. A full ADR is not warranted — this
is an implementation constraint neighboring ADR 0002, not a new architectural decision;
the gotcha is the right weight. Verify: entry present with evidence + date.

## Acceptance criteria → test mapping

| Acceptance criterion | Test | File |
|----------------------|------|------|
| Manifest is reachable, correct MIME, has installable fields (name, start_url, standalone, 192+512 icons) | `Manifest_IsServed_WithInstallableFields` | `tests/Agent42.Tests/PwaAssetsTests.cs` |
| Service worker is served as JavaScript (installability precondition) | `ServiceWorker_IsServed_AsJavaScript` | same |
| Service worker never caches auth-bearing `/api` (ADR 0002 not violated) | `ServiceWorker_BypassesApiBeforeCaching` (+ manual DevTools + Step 7 gotcha) | same |
| PNG icons (incl. maskable) are served correctly | `Icons_AreServed_AsPng` | same |
| Page links the manifest and registers the SW | `IndexHtml_LinksManifest_AndAppRegistersServiceWorker` | same |
| Installs + launches standalone on Android Chrome | Manual: DevTools → Application → Manifest shows "Installable"; on device, Chrome ⋮ → *Install app* → launches windowless | manual (documented in MR) |

## Quality gate checklist

Copied from `.agentic/quality-gates.yaml` @ `15f5cbc`:

- new_issues: 0
- new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80  (applies to new C# — the test file; JS/manifest/icons
  are not coverlet-instrumented. If `Program.cs` gains a `StaticFileOptions` line under
  risk #1, it is exercised by every `PwaAssetsTests` case.)
- cognitive_complexity_max: 15  (SW fetch handler and web-root resolver both well under)
- vulnerable_packages: 0  (no new packages — hard rule 4 satisfied trivially)
- Branch policy: `main` is protected → work on a `feature/*` branch + PR; never push to
  `main` directly (CLAUDE.md hard rule 1; enforced by `.claude/hooks/protect-main.mjs`).

Local commands before reporting done: `dotnet build Agent42.slnx`,
`dotnet test Agent42.slnx`, `dotnet format Agent42.slnx --verify-no-changes`.

## Out of scope

- Offline chat / message queueing (impossible without live LLM + auth; see constraint #6).
- Background sync, push notifications, Web Share Target.
- App-store packaging (TWA / Bubblewrap / Play Store listing).
- iOS splash-screen image sets beyond the basic `apple-mobile-web-app-*` meta tags.
- Reworking the auth flow or any Bicep/infra change (assets ride the existing ingress).
- A build/bundler step or SPA framework (repo is deliberately vanilla JS).

## Review

Critic pass (fresh context, no part in authoring): **sound plan, no install-breaking or
auth-breaking defects.** ADR 0002 confirmed *defended* by the SW design (network-first
navigation lets the 302 through; `/api` bypassed; only `index.html` — carrying no user
data — is cached). Findings and disposition:

- **[MEDIUM] No durable home for the SW-vs-auth invariant** → **accepted**: added Step 7
  (`/gotcha` + exemplar row). Agreed a full ADR is overkill.
- **[MEDIUM] The `/api`-bypass AC was proven only by a substring grep** → **accepted**:
  renamed the test to `ServiceWorker_BypassesApiBeforeCaching`, tightened it to require the
  guard to precede any caching token, and documented the residual risk (no JS test runner
  by design; manual DevTools + Step 7 gotcha backstop).
- **[LOW] "Same serving path" overclaim** → **accepted**: reworded Step 6 to state
  production uses the SDK static-web-assets provider; parity is at the content-type level.
- **[LOW] Maskable icon referenced but untested / not precached** → **accepted**: added it
  to the SHELL precache and to `Icons_AreServed_AsPng`.
- **[INFO] `.webmanifest` MIME almost certainly a non-issue** (default provider maps it) →
  **acknowledged**: the Step 1/risk #1 conditional already handles both outcomes safely;
  the `Program.cs` branch will very likely not fire.
- **[INFO] Optional manifest `id`** → **accepted**: added `"id": "/"` to the manifest.

No findings rejected.
