# Plan — Light / Dark / System theme

planned-against: main @ 4ddfc92

## Overview

Add a user-selectable theme. Today the app is hard-locked to a single dark palette
(`:root` in `styles.css`, l.6–23). Give the user a **light / dark / system** choice in
Preferences, applied instantly, remembered across visits **and across devices** (ADR
0005 makes the profile grain the source of truth), with **no dark flash** on load.

Why now: user request. Small full-stack change: one new `UserProfile` field + a
client-side theme engine.

## Affected areas (verified against the tree)

Backend (ADR-0005 profile plumbing — one field end-to-end):
- `src/Agent42.Agents/UserProfile.cs` — add `[property: Id(1)] string Theme` (trailing,
  after `PreferredLanguage` `[Id(0)]`).
- `src/Agent42.Agents/UserProfileGrain.cs` — normalize/validate Theme in `SaveAsync`
  (l.38–44 pattern), default/clamp unknown → `"system"`.
- `src/Agent42.Backend/AgentApi.cs` — `ProfileUpdateRequest` (l.391) gains `Theme`; the
  `SaveAsync(new UserProfile(...))` call (l.240) passes it through.

Frontend:
- `src/Agent42.Backend/wwwroot/styles.css` — variabilize the hardcoded colors that read
  wrong on light; add `:root[data-theme="light"]` palette override.
- `src/Agent42.Backend/wwwroot/index.html` — inline no-FOUC head script; theme `<select>`
  section in `#profileDialog` (dialog opens l.183; language field l.187–193).
- `src/Agent42.Backend/wwwroot/app.js` — theme engine + wire into the staged profile
  form (`profile` object l.100, `openProfileDialog`/`saveProfile` l.1169–1197).
- `src/Agent42.Backend/wwwroot/sw.js` — bump `CACHE` (l.5) so returning users get the new shell.

Tests:
- `tests/Agent42.Tests/PwaAssetsTests.cs` — extend the source-text asset-invariant
  pattern (l.114–138) for the new frontend hooks.
- `tests/Agent42.Tests/` (profile grain test, if one exists for language — else add) —
  Theme normalization round-trip.

## Context & constraints (what the executor can't cheaply re-derive)

- **ADR 0005 governs this: server grain is the source of truth, localStorage is only a
  cache.** `.agentic/decisions/0005-user-level-preferences-grain.md` established the
  Preferences panel + `UserProfile` grain and explicitly says new prefs are added "with
  the next `[Id(n)]`" and that `localStorage` is "allowed as an optional client cache …
  always reconciled against the server response." So theme is **`UserProfile.Theme
  [Id(1)]`**, saved via the existing `PUT /api/profile`; `localStorage["agent42.theme"]`
  is a **pre-paint render hint only**, reconciled on load. This is *following* ADR 0005,
  not contradicting it — **no `/decide` needed**. (Rejected the localStorage-only design
  the earlier draft had: ADR 0005 rejects it as a source of truth, and cross-device is a
  real user benefit.)
- **Why localStorage still exists here (the cache) — no-FOUC.** The stylesheet paints
  dark by default; the profile fetch is async, so without a synchronous pre-paint value
  the page flashes dark. An **inline** `<script>` in `<head>` reads
  `localStorage.getItem("agent42.theme")`, resolves `system` via `matchMedia`, and sets
  `document.documentElement.dataset.theme` before first paint. Inline is required (an
  external file is another round-trip = flash). No CSP header exists to block it
  (verified). On startup the app fetches the profile and, if the server theme differs
  from the cached one, applies it and rewrites the cache — server wins, per ADR 0005.
- **JS resolves `system` to an explicit `light`/`dark`.** Keep CSS simple: base `:root`
  stays **dark** (default/fallback); add one `:root[data-theme="light"]` override block.
  JS always writes an explicit `data-theme` (never `"system"`), so CSS needs no
  `prefers-color-scheme` query. When the stored choice is `system`, subscribe to
  `matchMedia('(prefers-color-scheme: dark)').addEventListener('change', …)` and
  re-resolve; when the choice is explicit, remove that listener. Centralize this in one
  `applyTheme(choice)` so the head script (inline, minimal) and the app engine agree.
- **The Preferences dialog is a STAGED editor — theme must respect Save/Cancel.**
  `openProfileDialog` fills from server, `saveProfile` PUTs **only on submit**, and
  Cancel/Esc just `close()` and discard (`app.js` l.1169–1197). So the theme select must
  behave like the language field: changing it may **live-preview** (`applyTheme` for
  instant feedback), but the value is **committed only on Save** (added to the PUT payload
  + cache rewritten) and **reverted on close-without-save**. Implement revert with a
  `profileDialog.addEventListener("close", …)` that re-applies `profile.theme` whenever
  the dialog closed without a successful save (the native `<dialog>` `close` fires on
  Cancel, Esc, and backdrop). Without this, previewing Light then hitting Cancel would
  wrongly stick — the exact divergence from the exemplar to avoid.
- **Variabilize hardcoded colors first — inventory is NON-exhaustive.** ~15 rgba/hex
  literals sit outside `:root` (not the ~8 the first draft claimed): body grain gradient
  (l.44), brand-mark glow/shadow (l.90–91 and a second at ~l.1415–1416), attachment-chip
  button bg (l.894), dialog shadow + modal/mobile backdrops (l.1075, 1079, 1452), green
  dot glow (~l.418/462/594), amber hovers (~l.650/811/1208), SVG data-URL icon colors
  (l.997, 1171). They carry ~8 distinct alpha values — do **not** force them into a
  single `--wash` var. Approach: sweep every out-of-`:root` literal; for each, either
  promote to a purpose-named var (`--backdrop`, `--shadow`, `--grain`, …) with a light
  value in the `[data-theme="light"]` block, or leave it if it reads fine on both (the
  amber/green accent glows generally do). The **backdrops** (`rgba(6,5,4,.6)` — near-black
  scrim) and the **grain gradient** are the ones that visibly break on light and MUST be
  variabilized.
- **Light palette (concrete starting values — executor tunes for WCAG AA):**
  ```
  --bg:#f4efe4; --bg-raised:#fbf7ee; --bg-inset:#e9e2d2;
  --line:#d6cdb8; --line-soft:#e2dac7;
  --ink:#2a2620; --ink-dim:#5c5647; --ink-faint:#8a8270;
  --amber:#a86611; --amber-deep:#7f4e0a; --amber-glow:rgba(168,102,17,0.12);
  --green:#5a7d3a; --red:#bf3c30;
  ```
  Warm-paper mirror of the "warm near-black + amber phosphor" identity (styles.css l.3).
  `--amber` MUST darken — `#ffb13d` fails contrast on pale bg; verify `--ink`/`--amber`
  on `--bg`/`--bg-raised` meet AA (≥4.5:1 text).
- **Keep meta theme-color in sync.** `<meta name="theme-color">` (index.html l.13) is
  static `#100f0d`; `applyTheme` updates its `content` per resolved theme so mobile chrome
  matches. (PwaAssetsTests l.134 already asserts the meta exists — keep it present.)
- **Bump the SW cache.** `sw.js` serves the shell **cache-first + background revalidate**
  (l.36–41), so a returning user keeps old CSS/JS for one extra load unless `CACHE`
  (l.5, `"agent42-shell-v2"`) → `-v3`. No test pins the cache string; navigations stay
  network-first (gotchas.md SW row is **not** tripped — only the version bumps).

## Steps

1. **Backend: `UserProfile.Theme` end-to-end** (`UserProfile.cs`, `UserProfileGrain.cs`,
   `AgentApi.cs`) — add `[Id(1)] string Theme`; normalize in `SaveAsync` (unknown/empty →
   `"system"`, allowed set `system|light|dark`); thread through `ProfileUpdateRequest` and
   the l.240 construction. *Verify:* `dotnet build`; new grain test (step 6) round-trips.
2. **CSS: variabilize + light palette** (`styles.css`) — sweep out-of-`:root` literals,
   promote the theme-sensitive ones, add `:root[data-theme="light"]` with the palette.
   *Verify:* DevHost UI, set `document.documentElement.dataset.theme="light"` in devtools
   → whole app flips, no orphaned dark patches (walk Preferences dialog, backdrops,
   attachment chips, brand mark).
3. **No-FOUC head script + theme-color** (`index.html`) — inline `<script>` in `<head>`
   resolving cached/system theme onto `documentElement` before paint; leave `theme-color`
   meta present. *Verify:* `localStorage.setItem("agent42.theme","light")`, hard-reload →
   **no** dark flash. *(Depends on step 2.)*
4. **Preferences select markup** (`index.html`) — new `<section class="agent-form__section">`
   in `#profileDialog` with `<select id="profileTheme">` (System / Light / Dark) +
   `agent-form__note`, mirroring the language field (l.187–193). *(After step 3 — same file.)*
5. **app.js: theme engine + staged wiring** (`app.js`) — `THEME_KEY="agent42.theme"`;
   `resolveTheme(choice)` (system→matchMedia), `applyTheme(choice)` (set `dataset.theme` +
   meta theme-color + add/remove the matchMedia listener + rewrite cache); add `theme` to
   the `profile` object (l.100); in `openProfileDialog` set `profileTheme.value =
   profile.theme`; on select `change` → live-preview via `applyTheme`; in `saveProfile`
   add `theme: profileTheme.value` to the PUT payload and, on success, set `profile.theme`
   + cache; add a `profileDialog` `close` listener that re-applies `profile.theme` if the
   dialog closed without a save (revert-on-cancel); at startup, after profile fetch,
   reconcile server theme over the cached value. *Verify:* pick each option → live; Save →
   persists (reload sticks, incl. other browser after profile sync); Cancel after preview
   → reverts; System + OS theme flip → follows.
6. **Tests: asset invariants + grain round-trip** (`PwaAssetsTests.cs` + a profile grain
   test) — extend the source-text pattern (l.114–138): assert index.html contains the
   inline theme head script marker + `#profileTheme` + `data-theme`; app.js contains
   `THEME_KEY`/`applyTheme` + the PUT `theme` payload + the `matchMedia` listener; styles.css
   contains `[data-theme="light"]`; sw.js `CACHE` is `-v3`. Add/extend a `UserProfileGrain`
   test asserting an unknown Theme normalizes to `system` and `light`/`dark` round-trip.
   *Verify:* `dotnet test Agent42.slnx` green.
7. **Bump SW cache** (`sw.js`) — `CACHE = "agent42-shell-v3"`. *Verify:* asset test (step 6)
   asserts v3; no stray v2 remains.

## Acceptance criteria → test mapping

Frontend behavior (live apply, revert-on-cancel, OS-follow, no-FOUC) has **no JS test
runner by design** (CLAUDE.md; PwaAssetsTests l.119 comment) — those are verified by
driving the DevHost UI (`/verify` at http://localhost:61326). The **structural
invariants** that silently regress are pinned by C# source-text asset tests, per the
established repo pattern.

| AC | How proven | Where |
|----|-----------|-------|
| Preferences offers System / Light / Dark | `#profileTheme` present + manual | PwaAssetsTests + `/verify` |
| Choice applies instantly (live preview) | manual | `/verify` — DevHost |
| Save persists across reload + devices | Theme round-trips server; manual reload | UserProfileGrain test + `/verify` |
| Cancel/Esc after preview reverts | manual | `/verify` (close listener) |
| System follows OS + reacts live | manual | `/verify` (matchMedia listener; test asserts listener present) |
| No dark flash on light load | manual + inline-script marker present | `/verify` + PwaAssetsTests |
| Light palette: no orphaned dark patches | manual walk; `[data-theme="light"]` present | `/verify` + PwaAssetsTests |
| Returning users get new shell | `CACHE` == v3 | PwaAssetsTests |
| Unknown/blank Theme is safe | normalizes to `system` | UserProfileGrain test |

## Quality gate checklist (from .agentic/quality-gates.yaml @ plan time)

- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80 — the **.NET** new code (the `Theme` field + normalization)
  IS covered by the step-6 grain test. Frontend JS/CSS has no coverage tooling (coverlet is
  .NET-only) and is guarded by asset-invariant tests instead — stated openly, not skipped.
- cognitive_complexity_max: 15 — new fns (`resolveTheme`/`applyTheme`, Theme normalization)
  are trivial.
- Lint: `dotnet format Agent42.slnx --verify-no-changes`. Build: `dotnet build Agent42.slnx`.
- Test: `dotnet test Agent42.slnx` green.
- vulnerable_packages: 0 — no dependency change.

## Out of scope

- Per-agent or per-conversation theming (theme is a user-level scalar, ADR 0005).
- Reworking the amber accent identity or adding palettes beyond light + dark.
- A full WCAG audit beyond the AA text-contrast check on the new light palette.
- Theming PWA icon assets (`icons/`, manifest colors) beyond the `theme-color` meta.

## Review (critic findings & resolution)

Critic verdict: revise before executing — 3 Major, 3 Minor. All addressed:
- **MAJOR 1 (contradicts ADR 0005 — localStorage as source of truth):** *Fixed.* Redesigned
  to `UserProfile.Theme [Id(1)]` server source of truth + localStorage as the ADR-permitted
  pre-paint cache. This follows ADR 0005's extension pattern, so no `/decide` is required.
- **MAJOR 2 (apply-on-change breaks staged Save/Cancel):** *Fixed.* Theme is now a staged
  field — live-preview on change, commit on Save, revert-on-close (step 5, close listener).
- **MAJOR 3 ("no test surface" ignores the asset-test pattern):** *Fixed.* Added step 6 —
  PwaAssetsTests source-text invariants + a UserProfileGrain normalization test; hard rule 3
  honored.
- **MINOR 1 (literal inventory undercounted, `--wash` can't be one var):** *Fixed.* Inventory
  now flagged non-exhaustive with the extra lines; single-`--wash` collapse removed.
- **MINOR 2 (invalid `localStorage.agent42.theme=` JS):** *Fixed.* Verify snippets use
  `localStorage.setItem("agent42.theme", …)`.
- **MINOR 3 (line-ref drift):** *Fixed.* Dialog opens l.183, language field l.187–193.

## Divergences from plan (recorded at execution)

1. **Grain gradient NOT variabilized** — the plan's Context said the body grain gradient
   "MUST be variabilized" because it "visibly breaks on light." On inspection it does not:
   it's white fractal noise at 3% alpha + a 5% amber radial — neither is a dark patch, both
   read fine on paper. A post-implementation critic pass independently confirmed the plan's
   "MUST" was mistaken. Left untouched (styles.css body rule).
2. **`--chip-btn-bg` added beyond the initial inventory** — the plan told the executor to
   leave the attachment-chip remove-button bg "only if it reads fine on both." It did not
   (near-black circle + dark `--ink` glyph = unreadable on light, and it also sits on
   document pills, not only image thumbnails), so it was promoted to a theme variable.
3. **Endpoint merges onto stored profile (absent field = keep)** — the plan had
   `ProfileUpdateRequest.Theme` default `"system"`. A critic pass showed that lets a stale
   client (old cached app.js in a second tab) reset a saved theme on a language-only save.
   Changed to nullable `Theme` (`null == keep`) + a `current with { … }` merge in
   `PUT /api/profile`. Added two `AgentApiTests` (persist + omitted-keeps-stored) beyond the
   plan's step-6 test list.
4. **Field default via positional record** — `Theme` carries `= "system"` as its positional
   default so `UserProfile.Empty` and the existing single-arg call sites/tests keep compiling
   with no churn; legacy state deserializes `Theme` as null and normalizes to `system`.

Verification: build 0 errors · `dotnet format` exit 0 · 324/324 tests · runtime smoke test
against DevHost confirmed theme persist + stale-client merge-keep end to end.
