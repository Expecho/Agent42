# Plan — Mobile conversation drawer

planned-against: main @ fe3dc0a
> Investigated on `fix/sync-template-drift` @ 8a4e1d8, but `src/Agent42.Backend/wwwroot/`
> is byte-identical to `main` (the branch's only commit is an unrelated template-drift
> sync that doesn't touch the frontend). All file/line claims below hold on `main`.
> **Branch fresh off `main`** as `feature/mobile-conversation-drawer` — do NOT stack this
> on `fix/sync-template-drift` (unrelated in-flight work). Hard rule 1: never commit to `main`.

## Overview

On viewports ≤760px the sidebar is hidden outright (`styles.css:723-728` — the block
also sets `.app { grid-template-columns: 1fr }`, but `.sidebar { display: none }` at
`725-727` is what removes navigation). That sidebar is the *only* place to see the conversation
list, switch conversations, or start a new one — so on a phone the user is stuck in
whichever conversation loaded and cannot navigate. Fix: turn the existing sidebar into
an off-canvas **slide-in drawer** toggled by a hamburger in a new **mobile-only top bar**,
with a dimming backdrop. Desktop layout is unchanged.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/wwwroot/index.html` — add the mobile top bar (hamburger + brand)
  and a backdrop element. Sidebar markup is reused verbatim as the drawer.
- `src/Agent42.Backend/wwwroot/styles.css` — replace the `@media (max-width: 760px)`
  block (currently just hides the sidebar) with drawer + backdrop + top-bar + hamburger
  styles; add the open-state rules.
- `src/Agent42.Backend/wwwroot/app.js` — wire the toggle (open/close), backdrop click,
  Escape-to-close, `aria-expanded` sync, and auto-close the drawer when a conversation is
  selected or a new one is started.

No backend, Bicep, or C# changes. `src/Agent42.Backend/AgentApi.cs` and the grains are
untouched — this is purely presentational.

## Context & constraints (what the executor can't cheaply re-derive)

- **Exemplar to copy:** `src/Agent42.Backend/wwwroot/app.js` is the frontend exemplar
  (gotchas.md:25) — vanilla JS, single IIFE, `"use strict"`, elements grabbed by id at the
  top, functions grouped under `/* ————— section ————— */` dividers, all listeners wired
  in the `init` block at the bottom. Match that shape; no framework, no build step
  (CLAUDE.md: "Frontend is deliberately vanilla JS … no SPA build").
- **Existing tap handlers already exist** — `selectConversation(id)` (`app.js:259`) and
  `startNewConversation()` (`app.js:285`) already fire on every conversation/new-chat tap.
  Auto-close = call `closeDrawer()` from inside those two functions; do NOT add per-element
  close listeners. Deleting the *active* conversation routes through `startNewConversation`
  (`app.js:136`), so that path closes too; deleting a non-active row intentionally leaves
  the drawer open.
- **Keep it out of the tab order when closed.** An off-canvas drawer whose buttons are
  still Tab-reachable is an a11y trap. Use `visibility: hidden` on the closed mobile drawer
  (transitioned alongside `transform`) rather than JS `inert` juggling — `visibility:hidden`
  removes descendants from the tab order and reverts cleanly to `visible` on desktop where
  the sidebar is static. (Decision made in interview; simpler than viewport-conditional
  `inert`.)
- **Breakpoint stays 760px** — reuse the existing single breakpoint; do not introduce a
  second one. Above it, the top bar and backdrop are `display: none` and the grid stays
  `300px 1fr` exactly as today.
- **Interview decisions (2026-07-06):** (1) slide-in drawer reusing the full sidebar, not a
  top-bar dropdown switcher; (2) toggle lives in a slim mobile top bar carrying the `42`
  brand mark + hamburger, not a floating button; (3) verification is manual via the offline
  DevHost — **no** JS/browser test harness is added (the repo has none today; introducing
  Playwright was explicitly rejected as out of scope).
- **No ADR / no gotcha needed** — a responsive drawer is a conventional pattern, not an
  architectural decision, and the constraint is expressible in the CSS/JS itself. Grepped
  `.agentic/decisions/` (0001 grain-per-conversation, 0002 Easy-Auth) — none touch the
  frontend. No doc in the repo asserts mobile behavior, so no doc-debt to pay beyond the
  stale `/* narrow screens: stack */` CSS comment, which Step 2 rewrites.

## Steps

### Step 1 — Mobile top bar + backdrop markup (`index.html`)
Inside `<main class="chat">`, add as the **first child** a mobile-only header:
```
<header class="topbar" id="topbar">
  <button class="topbar__menu" id="menuToggle" type="button"
          aria-label="Open conversations" aria-controls="sidebar" aria-expanded="false">
    <span class="topbar__bars" aria-hidden="true"></span>
  </button>
  <span class="topbar__brand"><span class="topbar__mark">42</span>AGENT&nbsp;42</span>
</header>
```
Add a backdrop as a sibling inside `.app` (after `</main>`):
`<div class="backdrop" id="backdrop"></div>`. No `hidden` attribute — Step 2's mobile
`.backdrop { display: block }` would override it anyway; show/hide is owned by the
`opacity`/`pointer-events` transition keyed on `.is-drawer-open`.
Leave the existing `<aside class="sidebar" id="sidebar">` markup unchanged (it already has
the id the toggle references).
**Verify:** page still loads and renders identically on desktop (top bar/backdrop are
`display:none` until Step 2's CSS, so structurally inert); no console errors.

### Step 2 — Drawer, backdrop, top-bar & hamburger styles (`styles.css`)
Replace the whole `@media (max-width: 760px)` block (`styles.css:723-728`) and add the
default (desktop) hidden state for the new elements:
- Default (all widths): `.topbar { display: none }`, `.backdrop { display: none }` so
  desktop is untouched.
- Hamburger `.topbar__bars`: a 3-line icon (e.g. an 18px box using `currentColor` via a
  background-gradient or three spans) in the amber/ink palette; `.topbar__menu` sized ~40px
  tap target, transparent border matching `.new-chat` hover idiom.
- `.topbar__mark`: mirror `.brand__mark` at a smaller scale (amber, bordered `42`).
- `@media (max-width: 760px)`:
  - `.app { grid-template-columns: 1fr }` (chat fills width).
  - `.topbar { display: flex; align-items: center; gap: 12px; padding: 12px 16px;
    border-bottom: 1px dashed var(--line) }`.
  - `.sidebar` becomes the drawer: `position: fixed; inset: 0 auto 0 0;
    width: min(300px, 84vw); z-index: 30; transform: translateX(-100%);
    visibility: hidden; transition: transform .25s ease, visibility .25s ease;`.
  - `.app.is-drawer-open .sidebar { transform: none; visibility: visible }`.
  - `.backdrop { display: block; position: fixed; inset: 0; z-index: 20;
    background: rgba(6,5,4,.6); opacity: 0; pointer-events: none;
    transition: opacity .25s ease }` — `display:block` is required so the opacity
    transition can animate (hence no `hidden` attribute in Step 1).
  - `.app.is-drawer-open .backdrop { opacity: 1; pointer-events: auto }`.
Keep the amber-phosphor aesthetic (reuse `--amber`, `--line`, `--bg-raised` tokens); do not
add new color literals beyond the backdrop scrim.
**Verify:** in browser devtools responsive mode — desktop (>760px) pixel-identical to
before; at ≤760px the sidebar is off-screen, the top bar shows, and adding
`class="app is-drawer-open"` in the inspector slides the drawer in over a dimmed backdrop.

### Step 3 — Toggle wiring & auto-close (`app.js`)
- Grab `menuToggle` and `backdrop` via `getElementById` alongside the other element grabs
  at the top, plus `app` via `document.querySelector(".app")` (`.app` has no id). Do NOT
  grab `sidebar` — the drawer animates purely via the CSS `.app.is-drawer-open .sidebar`
  rule, so JS never needs a handle to it.
- Add an `/* ————— mobile drawer ————— */` section with `openDrawer()` / `closeDrawer()`
  that toggle the `is-drawer-open` class on `app.classList` and set
  `menuToggle.setAttribute("aria-expanded", …)`; a `toggleDrawer()` helper. The backdrop's
  visibility follows the `is-drawer-open` class in CSS — no `hidden` attribute toggling.
- Call `closeDrawer()` at the top of `selectConversation` (`app.js:259`) and
  `startNewConversation` (`app.js:285`) so any tap that changes the view also closes the
  drawer. (Both are already the single choke points for those actions.)
- In `init`: `menuToggle.addEventListener("click", toggleDrawer)`,
  `backdrop.addEventListener("click", closeDrawer)`, and a `document` `keydown` listener
  that closes on `Escape` when open and returns focus to `menuToggle`.
**Verify:** manual DevHost run (see AC table) — tapping the hamburger opens the drawer,
tapping a conversation or "New conversation" closes it and shows that thread, backdrop tap
and Escape close it.

### Step 4 — End-to-end manual verification (no files)
Run `dotnet run --project tools/Agent42.DevHost` (canned LLM, in-memory grains, UI at
http://localhost:61326). In the browser at a ≤760px width, walk the AC table below. Then
run the repo gates (`dotnet build`, `dotnet test Agent42.slnx`, `dotnet format --verify-no-changes`)
to confirm the C#/build side is untouched.

## Acceptance criteria → verification mapping

Frontend-only change; the repo has no JS test runner (interview decision), so ACs are
verified **manually in the DevHost UI at ≤760px**, not by an automated test file.

| # | Acceptance criterion | How verified |
|---|----------------------|--------------|
| 1 | At ≤760px a top bar with a hamburger + `42` brand is visible; sidebar is off-screen | DevHost, devtools responsive ≤760px |
| 2 | Tapping the hamburger slides the drawer in with a dimmed backdrop | DevHost manual |
| 3 | Tapping a conversation loads that thread **and** closes the drawer | DevHost manual (multiple convs) |
| 4 | Tapping "New conversation" starts a new chat **and** closes the drawer | DevHost manual |
| 5 | Backdrop tap and `Escape` both close the drawer | DevHost manual |
| 6 | When closed, drawer controls are not Tab-reachable | DevHost — Tab through page at ≤760px |
| 7 | Above 760px the layout is pixel-identical to today (no top bar, no backdrop) | DevHost at desktop width; visual diff vs `main` |
| 8 | Deleting the active conversation still resets to a new chat (no regression) | DevHost manual |
| 9 | Build, tests, and `dotnet format` still pass (no C# touched) | `dotnet build` · `dotnet test Agent42.slnx` · `dotnet format --verify-no-changes` |

## Quality gate checklist (copied from `.agentic/quality-gates.yaml`)

- `new_issues: 0`, `new_security_issues: 0` — no new C#/analyzer surface; JS is DOM wiring
  with no `innerHTML` of untrusted input (existing `DOMPurify` path unchanged).
- `duplication_pct_max: 3` — drawer reuses existing sidebar markup; no duplicated blocks.
- `coverage_new_code_pct_min: 80` — **N/A**: coverlet measures C# only and no C# changes;
  there is no JS coverage tooling (interview decision to not add one).
- `cognitive_complexity_max: 15` — `openDrawer`/`closeDrawer`/`toggleDrawer` are trivial.
- `dependencies.vulnerable_packages: 0` — no dependencies added (vanilla JS, no build).
- Enforcement: `ci: hard_block` on build, tests, `dotnet format`, vulnerable packages,
  doc-path integrity — all unaffected; no doc paths added/changed except this spec.

## Out of scope

- No JS/browser test harness (Playwright etc.) — explicitly rejected in the interview.
- No full focus-trap / focus-cycling inside the drawer (only Escape returns focus to the
  toggle); a personal single-user app doesn't warrant a trap library.
- No swipe-to-open gesture, no drawer for tablet-specific widths, no new breakpoint.
- No changes to conversation data flow, API, streaming, or grains.
- No redesign of the sidebar contents — same brand, list, new-chat, footer.

## Review

Critic pass (fresh context, read-only) on 2026-07-06 — verdict: **sound plan, minor
findings only**. It independently confirmed every file/line claim (`styles.css:723-728`
media block, `app.js:259/285/136` choke points, sidebar/`.app`/`newChatBtn` markup), the
no-ADR / no-gotcha calls, the exemplar-shape conformance, and the technical soundness of
the `visibility:hidden` tab-order removal + z-index layering. Four findings, all **fixed
in this spec**:

1. **(medium) Backdrop double hide mechanism** — `hidden` attribute + `display:block`
   fought each other; the `hidden` toggling was dead code. → Removed the `hidden` attribute
   (Step 1) and the JS toggle (Step 3); show/hide is CSS-only via `opacity`/`pointer-events`.
2. **(low) Unused `sidebar` grab** — Step 3 grabbed a `sidebar` handle nothing reads. →
   Dropped it; noted the drawer animates via CSS only.
3. **(low) Inconsistent line refs / imprecise "just hides"** — Overview said `723-728`,
   Step 2 said `721-728`, and the block also sets `grid-template-columns:1fr`. → Unified to
   `723-728` and corrected the description in both places.
4. **(low) "grab `app` by id" but `.app` has no id** → clarified to `querySelector(".app")`.

No findings were rejected; all were legitimate and applied.
