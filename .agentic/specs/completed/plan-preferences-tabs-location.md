# Plan — Tabbed Preferences dialog + Location in Preferences

planned-against: feature/light-theme @ 4d3981a

> **Stacked work (user's explicit call).** This builds on `feature/light-theme` (PR #38,
> unmerged), NOT `main` — the theme section it re-homes into the General tab lives only on
> that branch. Branch `feature/preferences-tabs-location` FROM `feature/light-theme`. If #38
> merges to `main` first, rebase onto `main` before executing.

## Overview

Refactor the Preferences dialog (`#profileDialog`) into two tabs — **General** (language,
theme, location) and **Model providers** (the existing providers section) — and add a
**Location** control to General that grants geolocation, replacing the composer's
"Share my location" button (removed). Grant logic is unchanged; only the control moves.

Why now: user request. Preferences has outgrown a flat stack of sections; the model-providers
block is large enough to deserve its own tab, and Location wants a discoverable home.

## Affected areas (verified against the tree)

- `src/Agent42.Backend/wwwroot/index.html` — restructure `#profileDialog` (l.200–238) into a
  tablist + two tabpanels; add the Location section to General; **remove** the composer
  `#locationBtn` (l.136–143).
- `src/Agent42.Backend/wwwroot/styles.css` — add tab + location-row styles; **remove** the four
  `.composer__location` rules (l.1063–1092).
- `src/Agent42.Backend/wwwroot/app.js` — a tab controller (panel switching + ARIA + arrow-key
  nav); repoint the location grant UI from `locationBtn` to the new Preferences control
  (`updateLocationControl`, `requestLocationGrant`, `initLocation`, the DOM ref at l.15). All
  auth/background/send logic stays as-is.
- `src/Agent42.Backend/wwwroot/sw.js` — bump `CACHE` (l.5, `agent42-shell-v3` → `-v4`): this
  change edits all three precached shell assets (index/styles/app), served stale-while-revalidate.
- `tests/Agent42.Tests/LocationAssetsTests.cs` — replace `ShareLocationButton_IsPresent`
  (l.22–30) with an assertion on the new Preferences control + the composer button's absence.
- `tests/Agent42.Tests/PwaAssetsTests.cs` — add a tab-structure source-text invariant; update
  `ServiceWorker_CacheNameIsCurrent` (l.164–171) to `agent42-shell-v4`.

## Context & constraints (what the executor can't re-derive cheaply)

- **Tabs are pure presentation — no ADR change, no new ADR.** ADR 0018 (providers), ADR 0012
  (location), ADR 0005 (profile) are all unchanged; markup is relocated, behavior is not. Do
  **not** run `/decide`.
- **The location auth mechanism is load-bearing and must not change** (gotcha
  `.agentic/gotchas.md:301`, "WebKit has no geolocation Permissions API"). The two-legged
  authorization (`isCaptureAuthorized` / `storedGrant`), the gesture-only prompt
  (`requestLocationGrant` fires `getCurrentPosition` ONLY on an explicit tap), the denial-clears-
  grant path (`handlePositionError`), the never-block-send accessor (`currentLocationForSend`),
  and the out-of-band cache warming (`initLocation` → `refreshFix` + `startBackgroundLocationRefresh`,
  the `visibilitychange` + Permissions-`change` listeners) all STAY untouched. This change moves
  only the **grant control**: repoint the click handler, the disable-during-capture, and the
  show/hide-when-authorized from `#locationBtn` to the new Preferences button. The background
  refresh + cache warming are independent of the button and must keep running app-wide even
  though the control now lives in a usually-closed dialog.
- **`requestLocationGrant()` currently couples to `locationBtn`** (disables it during capture,
  app.js ~l.995/1003) and `updateLocationControl()` toggles `locationBtn.hidden` (l.988–989).
  Repoint both to the new element. Keep the "only set the stored grant after a capture actually
  succeeded" ordering — `LocationAssetsTests.App_AuthorizesCaptureOnBothLegs` pins it.
- **The Save/Cancel footer stays GLOBAL, outside the tab panels.** Profile Save persists only
  language + theme (ADR 0005, via `PUT /api/profile`); providers persist **immediately** through
  their own API (`/api/providers`, ADR 0018), independent of the footer. So Save while on the
  Providers tab still saves the General fields and is harmless — do not try to make the footer
  tab-aware. The theme's staged preview/revert (light-theme branch) still keys off the dialog's
  `close` event, which fires regardless of the active tab — leave that wiring intact.
- **`LocationAssetsTests.ShareLocationButton_IsPresent` (l.22–30) pins `id="locationBtn"`
  shipped hidden — it WILL break and must be rewritten** to the new control. The other four
  Location tests assert function bodies that don't move (`currentLocationForSend`,
  `isCaptureAuthorized`, `requestLocationGrant`, `handlePositionError`, background refresh) —
  leave them. The gotcha's prose ("the grant button never hides") stays conceptually true of the
  new control; no gotcha edit needed, but update the `index.html` "add more `<section>` blocks"
  growth comment (l.198–199) to say "add to the right tab panel."
- **New Location control shape:** a General-tab section `// location` with a status line and an
  `Enable` button (`id="locationEnableBtn"`). States: unsupported → "Not available in this
  browser" (no button); authorized → "Sharing on" + button hidden, note "Turn it off in your
  browser's site settings" (the browser is the off switch, matching the removed composer
  comment); not-yet-authorized → "Off" + an enabled Enable button. Refresh this on dialog open
  AND from the existing Permissions-`change` handler.
- **Accessibility — build real tabs, not styled divs.** `role="tablist"` wrapping two
  `<button role="tab">` (each `aria-selected`, `aria-controls`, `id`), two
  `<div role="tabpanel">` (each `aria-labelledby`, inactive one `hidden`). Arrow-Left/Right (and
  Home/End) move between tabs; click activates. Panel toggling uses `hidden` — the existing
  `[hidden] { display: none !important }` rule (styles.css:29) guarantees it beats the form's
  flex display. Default active tab = General; reset to General on each open (no persistence).
- **CRITICAL — every new `<button>` inside `#profileForm` MUST carry `type="button"`.** The tab
  buttons and the Location `Enable` button live inside `<form id="profileForm">`, whose submit
  saves the profile and closes the dialog (`profileForm` submit → `saveProfile()` → `close()`). A
  bare `<button>` in a form defaults to `type="submit"`, so without `type="button"` a tab click
  would submit-and-close the dialog (tabs totally non-functional) and `Enable` would close instead
  of prompting (`requestLocationGrant` does not `preventDefault`). The existing in-form buttons all
  carry `type="button"` (e.g. `addProviderBtn`, `index.html:227`) — copy that. This is the single
  most likely silent break; it is invisible unless you read the submit wiring.
- **Paint the unsupported location status without gating it behind `initLocation`'s early return.**
  `initLocation` returns early on `!geolocationSupported` (app.js ~l.1009–1012), before its
  `updateLocationControl()` call — so the "Not available in this browser" status is painted from
  the `openProfileDialog` call added in step 4, not from `initLocation`. Don't wire the status
  behind that early return.

## Steps

1. **Markup: tabs + Location, remove composer button** (`index.html`) — wrap the General fields
   (language, theme, new location section) and the providers section in two `role="tabpanel"`
   divs under a `role="tablist"`; keep the title and the Save/Cancel footer outside the panels;
   delete `#locationBtn` from the composer **and its explanatory comment above it** (l.132–135);
   give every new in-form button `type="button"`. *Verify:* DevHost UI — dialog shows two tabs,
   footer below; composer has no location button; clicking a tab does NOT close the dialog.
2. **CSS: tab + location styling, remove composer-button rules** (`styles.css`) — style
   `.agent-form__tablist`/`__tab`/`__tab[aria-selected="true"]` and the location row using the
   existing tokens (amber active underline/indicator, `--ink-faint` inactive); delete the four
   `.composer__location` rules. *Verify:* tabs read as tabs in both light and dark; active tab is
   distinct; no orphaned styles (grep `composer__location` → none).
3. **app.js: tab controller** (`app.js`) — a small controller that, on tab click/arrow-key,
   sets `aria-selected` + `tabindex` and toggles the panels' `hidden`; reset to General in
   `openProfileDialog`. *Verify:* click and keyboard both switch panels; General is active on
   open.
4. **app.js: relocate location grant UI** (`app.js`) — swap the `locationBtn` DOM ref for
   `locationEnableBtn` (+ a status element); repoint `updateLocationControl` (status text + button
   state per the three states above) and `requestLocationGrant` (disable the new button during
   capture); call `updateLocationControl()` in `openProfileDialog`; keep all auth/background/send
   logic and the `initLocation` listeners unchanged. *Verify:* from Preferences, Enable → browser
   prompt → status flips to "Sharing on", button hides; a granted state on reopen shows "Sharing
   on" with no re-prompt.
5. **sw.js: bump the shell cache** (`sw.js`) — `CACHE = "agent42-shell-v4"`, since all three
   precached shell assets changed. *Verify:* grep shows v4; no stray v3.
6. **Tests: update Location asset test, add tab invariant, bump cache assertion**
   (`LocationAssetsTests.cs`, `PwaAssetsTests.cs`) — rewrite `ShareLocationButton_IsPresent` →
   `LocationControl_IsInPreferences_AndComposerButtonRemoved` (assert `id="locationEnableBtn"`
   present; assert `id="locationBtn"` and `composer__location` absent); add
   `Preferences_HasGeneralAndProvidersTabs` (assert `role="tablist"`, both tab labels, two
   `role="tabpanel"`); update `ServiceWorker_CacheNameIsCurrent` to `agent42-shell-v4`.
   *Verify:* `dotnet test` green.

## Acceptance criteria → test mapping

Behavior (tab switching, keyboard nav, live grant flow) has no JS runner by design — driven in
the DevHost UI (`/verify`). Structural invariants are pinned by C# source-text asset tests.

| AC | How proven | Where |
|----|-----------|-------|
| Preferences has General + Model providers tabs | `role="tablist"` + both labels present | PwaAssetsTests + `/verify` |
| Switching tabs shows the right panel | manual | `/verify` |
| Tabs are keyboard/ARIA accessible | `role="tab"`/`aria-selected` present + manual arrow-key | PwaAssetsTests + `/verify` |
| General holds language, theme, location | manual | `/verify` |
| Location can be granted from Preferences | `id="locationEnableBtn"` present + manual grant | LocationAssetsTests + `/verify` |
| Composer no longer has the location button | `locationBtn`/`composer__location` absent | LocationAssetsTests |
| Location auth still two-legged + gesture-only | unchanged function-body assertions still pass | LocationAssetsTests (existing) |
| Profile save (language+theme) still works | existing profile tests unaffected + manual | AgentApiTests (existing) + `/verify` |
| Providers CRUD still works from its tab | manual | `/verify` |

## Quality gate checklist (from .agentic/quality-gates.yaml @ plan time)

- new_issues: 0 · new_security_issues: 0
- duplication_pct_max: 3
- coverage_new_code_pct_min: 80 — this change is JS/CSS/HTML only (no .NET product code);
  coverlet is .NET-only, so new-code coverage is **N/A**, guarded instead by the asset-invariant
  tests. Stated openly, not skipped.
- cognitive_complexity_max: 15 — the tab controller is a small switch; keep it under the bar.
- Lint: `dotnet format Agent42.slnx --verify-no-changes`. Build: `dotnet build Agent42.slnx`.
  Test: `dotnet test Agent42.slnx` green.
- vulnerable_packages: 0 — no dependency change.

## Out of scope

- No change to the provider add/edit sub-dialog (`#providerDialog`) or the providers API.
- No change to the location auth mechanism, the send path, geocoding, or background refresh.
- No persistence of the active tab across opens (always opens on General).
- No tabs on any other dialog (the agent editor stays as-is).
- No new ADR (pure presentation refactor).

## Review (critic findings & resolution)

Critic verdict: no ADR/gotcha violations; fix 2 Major before executing. All addressed:
- **MAJOR 1 (sw.js cache bump omitted):** *Fixed.* Added `sw.js` v3→v4 to affected areas, a
  dedicated step 5, and the `ServiceWorker_CacheNameIsCurrent` update — the change edits all three
  precached shell assets, and the existing test passes on whatever literal is present so it
  wouldn't have caught the miss.
- **MAJOR 2 (in-form buttons default to `type="submit"`):** *Fixed.* Added a CRITICAL constraint
  and a step-1 verify that clicking a tab must not close the dialog; every new in-form button
  carries `type="button"`.
- **MINOR 3 (orphaned composer comment):** *Fixed.* Step 1 now deletes the l.132–135 comment with
  the button.
- **MINOR 4 (unsupported status behind `initLocation` early return):** *Fixed.* Constraint added:
  the status is painted from `openProfileDialog`, not gated behind the early return.
- **MINOR 5 (removing the composer control is a UX decision with an alternative → maybe /decide):**
  *Acknowledged, not blocking.* The removal is **user-directed** (they explicitly chose "move to
  Preferences only" over "keep both"), so it isn't a decision the executor is making. But it does
  reverse ADR 0012's deliberate placement of the grant in the composer, so at execution close-out
  record it as a short **amendment note on ADR 0012** (not a full new ADR) rather than leaving
  0012's rationale silently stale.

## Divergences from plan (recorded at execution)

1. **`AgentGrain.cs` added to affected areas — a missed consumer of the removed button.** A
   post-implementation critic pass found the `get_current_location` "not shared" return value
   (model-facing product text, `AgentGrain.cs` ~l.1197) told the user to tap *"Share my location"
   below the message box* — the button this change deletes. Updated it to "enable location sharing
   in Preferences". The plan's "grant logic is unchanged, only the control moves" glossed over this
   live copy. Also hardened `GetCurrentLocation_WhenNotShared_TellsModelToAskTheUser`
   (`AgentGrainTests.cs`) with a quote-free `Assert.Contains("Preferences", …)` so the same copy
   can't rot undetected again.
2. **ADR 0012 amended** with the placement note the plan's Review flagged (auth-gate section) —
   a *placement* change only; the two-legged auth, gesture-only prompt, cache warming, and
   never-block-send accessor are unchanged.

Verification: build 0 errors · `dotnet format` exit 0 · 325/325 tests · runtime smoke test against
DevHost confirmed the served tab markup, `type="button"` tabs, Location control, removed composer
button, and `agent42-shell-v4` cache.
