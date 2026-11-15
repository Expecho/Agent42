# Plan — Ten color themes (palettes), each with dark + light

planned-against: main @ 5915f34

## Overview

Today the app has ONE palette ("amber phosphor", warm near-black / warm paper) with a
System/Light/Dark **mode** choice (ADR 0005, completed `plan-light-theme.md`). Support **10 palettes in
total** (amber stays #1 and stays the default), every one with a dark and a light variant, and let the user
pick the palette in Preferences next to the existing mode select. Palette and mode are two independent
axes: 10 palettes x {dark, light} = 20 token sets; `system|light|dark` keeps meaning what it means today.

Why now: user request. The change is mostly CSS/JS, but the palette is **copy-pasted in 11 places** today
(see Context), so the plan first collapses that into one source before multiplying it by 10.

## Affected areas (verified against the tree @ 5915f34)

Backend / persistence (one new profile field, ADR 0005 pattern):
- `src/Agent42.Agents/Users/UserProfile.cs` — new trailing `[property: Id(4)] string? Palette = null`
  (null = legacy/never chosen = amber). `Empty` factory (`=> new("", "system", null, null)`) gets the extra arg.
- `src/Agent42.Agents/Users/UserProfileGrain.cs` — `SaveAsync` clamps `Palette` to the allow-list (pattern of
  `ValidThemes`, l.27/47-51); unknown/blank -> `"amber"`.
- `src/Agent42.Backend/Users/ProfileContracts.cs` + `AgentApi.Users.cs` (l.25-36) — `Palette` on
  `ProfileUpdateRequest` (null = keep stored) and in the `current with { … }` merge.
- `src/Agent42.Backend/Artifacts/ArtifactTheme.cs` — tokens per palette x mode; `Block(theme)` gains a palette
  argument; `AgentApi.Artifacts.cs:84` and `Fleet/AgentApi.Fleet.cs:414` read `?palette=` beside `?theme=`.
  Palette allow-list lives in ONE C# type (new `ThemePalettes`, flat `Agent42.Backend`/`Agent42.Agents`
  namespace per ADR 0039) that the grain and `ArtifactTheme` both read; the grain is in `Agent42.Agents`, so put
  the id list there and let the Backend reference it.

Frontend:
- NEW `src/Agent42.Backend/wwwroot/shared/themes.css` — the single palette source (all 20 token sets).
  `/shared/*` is already Easy-Auth-excluded (`infra/modules/container-app.bicep` excludedPaths), so the public
  viewer pages can load it too. Nothing new to exclude.
- Delete the duplicated palette blocks and point the pages at `themes.css`: `wwwroot/styles.css` (l.6-57),
  `shared/viewer.css` (l.7-37), `fleet/fleet.css` (l.9-52), `logs/logs.css` (l.7-37), `legal/legal.css` (l.9-28,
  uses `prefers-color-scheme`, no JS — see Out of scope). Non-palette tokens (`--mono`, `--serif`, `--radius`,
  `--agent-s/l`…) stay in their own file.
- `wwwroot/js/theme.js` — palette axis: `agent42.palette` cache key, `applyTheme(mode)` stays, new
  `applyPalette(id)`, `paintTheme` sets `data-palette` + `theme-color` meta from the computed `--bg`.
- `wwwroot/js/profile.js` (l.17, 96, 125, 136-139, 166-173) + `js/dom.js` (l.97) — staged `profilePalette`
  select: live preview, commit on Save, revert on close (copy the existing `profileTheme` handling exactly).
- `wwwroot/index.html` — head no-FOUC script (l.23-37) also sets `data-palette`; `<select id="profilePalette">`
  next to `#profileTheme` (l.534-543); `<link>` to `themes.css`.
- Head no-FOUC scripts in `fleet/index.html` (l.20-31), `logs/index.html` (l.17-29): also read the cached
  palette. `shared/index.html` (l.22-33) and `shared/fleet.html` (l.22-33): unchanged (public, see decisions).
- `wwwroot/sw.js` — add `/shared/themes.css` to `SHELL`, bump `CACHE` (currently `agent42-shell-v78`).
- `wwwroot/shared/lib/artifactpane.js` (l.64 `currentTheme`, l.294 observer) — pass `palette=` to the frame URL
  and reload the framed artifact when `data-palette` changes too (observer's `attributeFilter`).
- `wwwroot/js/clienttools.js` l.148 (`browserContext`) — also report the palette (optional, tiny).
- Amber literals that do not follow the palette today (must become palette-aware): `styles.css` l.71, 146-147,
  969, 1013, 1474, 2099, 2587, 2624, 3279-3280, 3431 (the three `%23c97f1b` chevron data-URLs), plus 4
  literals in `viewer.css`, 3 in `fleet.css`, 3 in `logs.css`, and the favicon in `index.html` l.15.

Tests (source-text/asset pattern, no JS runner by design):
- `tests/Agent42.Tests/UserProfileGrainTests.cs` (palette clamp + default + merge), `ArtifactThemeTests.cs`,
  `ArtifactsApiTests.cs` (`?palette=`), `PwaAssetsTests.cs` + the four tests that pin `agent42-shell-v78`
  (`ClientToolsAssetsTests`, `ResearchAssetsTests`, `SearchAssetsTests`, `PwaAssetsTests`) — bump all of them,
  `SerializationCompatibilityTests.cs` l.328 (new field on the Cosmos JSON path), NEW `ThemePaletteTests.cs`.

## Context & constraints (judgment the executor can't cheaply re-derive)

- **Governing ADRs/gotchas:** ADR 0005 (profile grain = source of truth, localStorage = pre-paint cache; new
  fields get the next `[Id(n)]`), ADR 0039 (flat namespace), ADR 0041 (ES modules; `SHELL` is all-or-nothing),
  CLAUDE.md cross-cutting rules 2 and 3 (trailing `[Id]` field with default, proven on the Cosmos JSON path in
  `GrainStorageSerializerTests`/`SerializationCompatibilityTests`; every MR touching `wwwroot/js/` bumps `CACHE`),
  gotchas entry on `ArtifactTheme` ("keep its tokens in sync with `styles.css`", `.agentic/gotchas.md` ~l.4141).
  Read `plan-light-theme.md` (completed) for the staged Save/Cancel + stale-client-merge reasoning; it all still holds.
  This plan **extends** ADR 0005 and ADR 0083 (highlight themes); no existing decision is contradicted. Because
  it changes where the palette lives (one shared stylesheet), record it with `/decide` (step 9).
- **Two axes, not 20 modes.** Keep `UserProfile.Theme` = `system|light|dark` UNCHANGED (renaming a persisted
  field is exactly what ADR 0005 forbids) and add `Palette`. Reject encoding `"ocean-dark"` into `Theme`:
  it breaks "System follows the OS" and the existing merge/normalize tests.
- **Single palette source first.** The 7 token blocks are copied by hand today (`styles.css`, `viewer.css`,
  `fleet.css`, `logs.css`, `legal.css` + `ArtifactTheme.cs` DarkTokens/LightTokens + 5 head scripts hardcoding
  `#100f0d`/`#f4efe4`). Adding 9 palettes by hand to each would be 60+ drifting copies. Decision: `shared/themes.css`
  owns every palette; the pages load it. `ArtifactTheme.cs` cannot read a CSS file (sandboxed frame, no origin),
  so it keeps its own C# table and **`ThemePaletteTests` parses `themes.css` and asserts the C# table equals it**
  (token for token, 20 sets). That test is the drift guard; do not skip it.
- **Selector shape.** JS and every head script ALWAYS write both `data-palette` and `data-theme`. In
  `themes.css` use fully qualified `:root[data-palette="x"][data-theme="dark|light"]` (specificity 0,3,0, no
  source-order games), plus a bare `:root` amber-dark fallback so a page without JS or with a failed stylesheet
  stays readable. Unknown `data-palette` falls through to that fallback.
- **Token contract per set** (superset of today's three copies; unused ones are harmless): `--bg --bg-raised
  --bg-inset --line --line-soft --ink --ink-dim --ink-faint --amber --amber-deep --amber-glow --green --red
  --backdrop --shadow --chip-btn-bg --warn --agent-s --agent-l` and `color-scheme`. **Keep the token NAME
  `--amber` as "the accent"** across all palettes — renaming it touches thousands of references for no user
  value; note in the CSS header comment that `--amber` means accent now.
- **Accent literals -> tokens.** Replace the hardcoded `rgba(255,177,61,a)` with
  `color-mix(in srgb, var(--amber) <a%>, transparent)`; replace the `%23c97f1b` chevron data-URLs (cannot use
  `var()` inside a data URL) with a `mask`/`background-color: var(--amber-deep)` + `mask-image` rule, or one shared
  `--chevron` custom property holding the whole `url(...)` per palette in `themes.css`. Executor picks the
  smaller diff; whichever, the SVG must recolor with the palette. Favicon: leave amber (static asset) — Out of scope.
- **The ten palettes** (ids are the contract; each gets a dark and a light set; accent hue in parentheses):
  `amber` (warm, existing values stay byte-identical) · `ocean` (blue) · `indigo` · `violet` · `rose` ·
  `crimson` (red) · `coral` (orange-red) · `forest` (green) · `teal` · `graphite` (neutral grey, white/black
  accent). Executor tunes exact hex values; constraints are measured, not eyeballed:
  `--ink` on `--bg`, `--bg-raised`, `--bg-inset` >= 4.5:1; `--amber` on `--bg`, `--bg-raised` >= 4.5:1;
  `--ink-dim` on `--bg` >= 4.5:1; `--ink-faint` on `--bg` >= 3:1; `--red`/`--green` on `--bg` >= 3:1.
  `ThemePaletteTests` computes WCAG contrast from the parsed values for all 20 sets and fails the build below
  these floors — that is the AC proof, never a manual claim.
- **Which pages follow the palette (user-facing decision, defaulted — flag it in the final message):**
  the main app, `/fleet/` (already reads the cache) and `/logs` (owner console, same origin) follow the user's
  cached palette + mode. The **public** recipient pages (`/shared/*`, `/legal/*`) have no profile and a recipient
  must not inherit anything; they stay amber + OS mode. Their `<link>` to `themes.css` is still needed because
  `viewer.css` loses its own copy of amber.
- **Pre-paint + `theme-color`.** `theme-color` meta must match the palette's `--bg`. The head script sits AFTER
  the `themes.css` `<link>` (a stylesheet blocks the script that follows it) and reads
  `getComputedStyle(document.documentElement).getPropertyValue("--bg")`; if that returns empty fall back to
  the amber hexes. Executor verifies in the DevHost that the meta really updates on first paint. Do not
  hand-copy 20 background hexes into five head scripts.
- **Staged select, same discipline as mode.** Palette is a staged field: live preview on `change`, committed only
  on Save, reverted by the existing dialog `close` handler when not saved (`profile.js` l.166-173). A stale cached
  client that PUTs without `palette` must not reset it: `Palette` is nullable, null = keep (the merge in
  `AgentApi.Users.cs`).
- **Frame reloads.** The pane reloads a framed artifact when `data-theme` flips (`artifactpane.js` l.289-295); it
  must also do so when `data-palette` flips, and `frameTheme` bookkeeping must key on palette+mode.
- **Highlight.js / mermaid / charts** keep their binary light/dark pairing (github / github-dark). Per-palette
  code themes are Out of scope; `richtext.js` already reads CSS variables for charts (l.299) so those follow.
- **Persisted field rule.** `Palette` is `[Id(4)]`, nullable with default, no migration; legacy state deserializes
  null = amber. Never write a collection expression into grain state (not relevant here, one scalar).
- **The Android app** (`tools/Agent42.Android`) renders the web UI in a WebView and keeps its own Compose theme in
  `theme/Color.kt`; it is a separate native surface and is not touched.

## Steps

1. **Palette source + backend model** (not parallelizable with 2; start here) — add `ThemePalettes` (ids + default),
   `UserProfile.Palette [Id(4)]`, grain clamp, `ProfileUpdateRequest.Palette`, merge in `PUT /api/profile`.
   *Verify:* `dotnet build Agent42.slnx`; `UserProfileGrainTests` (clamp, default, null-keeps) and
   `SerializationCompatibilityTests` (Cosmos JSON path incl. legacy doc without the field) green.
2. **`themes.css`** — author the 20 token sets + the `:root` fallback; amber values copied byte-for-byte from the
   current `styles.css` l.6-57 (the amber/dark and amber/light sets must equal today's, so existing users see no
   change). *Verify:* `ThemePaletteTests` (step 3) passes the contrast floors.
3. **`ThemePaletteTests`** (write alongside step 2) — parse `shared/themes.css`; assert: exactly the 10 ids from
   `ThemePalettes`, each with dark+light, every contract token present, contrast floors, `ArtifactTheme` table
   equals the CSS for the shared tokens, amber/light and amber/dark equal the pre-change values. *Verify:*
   `dotnet test --filter ThemePaletteTests`.
4. **De-duplicate the pages onto `themes.css`** (parallelizable per file, each file one edit; use
   `step-implementer` with worktree isolation if parallel) — `styles.css`, `viewer.css`, `fleet.css`, `logs.css`,
   `legal.css`: remove palette blocks, keep non-palette tokens; add the `<link>` to each page's HTML before its own
   stylesheet; convert the amber literals and chevron data-URLs. *Verify:* DevHost
   (`dotnet run --project tools/Agent42.DevHost`, http://localhost:61326): set `data-palette` + `data-theme`
   from devtools through all 20 combos, walk chat, Preferences, fleet, logs; no orphaned amber. amber/dark and
   amber/light look unchanged versus `main`.
5. **Engine + Preferences UI** (`theme.js`, `profile.js`, `dom.js`, `index.html`) — `agent42.palette` cache,
   `applyPalette`, `paintTheme` sets `data-palette` and the meta from `--bg`, `#profilePalette` select (10 options,
   a color swatch dot per option is nice-to-have), staged live-preview / Save / revert, boot reconcile against the
   profile. Head scripts in `index.html`, `fleet/index.html`, `logs/index.html` read the cached palette.
   *Verify:* DevHost: each option previews live, Cancel reverts palette AND mode, Save persists across a reload
   and a second browser profile, System + OS flip still follows, no flash on hard reload with a non-amber palette
   cached.
6. **Artifact frames** (`ArtifactTheme.cs`, the two endpoint call sites, `artifactpane.js`) — per-palette token
   table, `?palette=` query, observer reloads on palette change. *Verify:* `ArtifactThemeTests` +
   `ArtifactsApiTests.Frame_InjectsTheAppTheme` extended for a palette; DevHost: open an html artifact, switch
   palette in Preferences preview, frame recolors.
7. **Service worker + pins** — `/shared/themes.css` into `SHELL`, `CACHE` -> `agent42-shell-v79`, update the four
   tests that pin v78 and `SearchAssetsTests`' `DoesNotContain` list. *Verify:* `dotnet test` asset tests.
8. **Asset-invariant tests** — extend `PwaAssetsTests` (index.html has `profilePalette`, `data-palette` in the head
   script, `themes.css` linked and in `SHELL`; `theme.js` has `applyPalette`/`agent42.palette`; `profile.js` sends
   `palette`). *Verify:* `dotnet test Agent42.slnx`.
9. **Docs** — `/decide` a new ADR ("Palette is a second axis; one shared `themes.css`; ArtifactTheme parity
   test") that amends 0005 and 0083; add it to the Feature map row "PWA and UI" in `CLAUDE.md`; `/gotcha` entry:
   palette edits happen in `themes.css` only, and `ArtifactTheme.cs` is guarded by `ThemePaletteTests`;
   fix the "Theme" wording in `ProfileContracts.cs`/`UserProfile.cs` comments. `/sync-docs`.

## Acceptance criteria -> test mapping

| AC | How proven | Where |
|----|-----------|-------|
| 10 palettes exist, each with dark and light | parsed from `themes.css`, ids equal `ThemePalettes` | `ThemePaletteTests` |
| Every set is legible (WCAG floors in Context) | computed contrast over all 20 sets | `ThemePaletteTests` |
| amber/dark and amber/light unchanged | values pinned to the pre-change hexes | `ThemePaletteTests` |
| Palette persists per user; unknown/blank -> amber | grain round-trip + clamp | `UserProfileGrainTests` |
| A stale client omitting `palette` keeps the stored one | PUT without palette keeps value | `AgentApiTests` (profile section) |
| Legacy profile (no `Palette` field) deserializes | Cosmos JSON path | `SerializationCompatibilityTests` |
| Framed artifacts match the chosen palette | `?palette=` selects tokens; C# table equals CSS | `ArtifactThemeTests`, `ArtifactsApiTests`, `ThemePaletteTests` |
| Settings has a palette picker (live preview, Save, Cancel reverts) | marker presence + manual DevHost walk | `PwaAssetsTests` + manual |
| No flash on load with a cached non-amber palette | inline script marker + manual hard reload | `PwaAssetsTests` + manual |
| `themes.css` is offline-cached and users get the new shell | `SHELL` entry + `CACHE` bump | `PwaAssetsTests` (+3 pin files) |
| Public pages unaffected by the user's palette | `shared/*`, `legal/*` heads do not read `agent42.palette` | `PwaAssetsTests` |

## Quality gate checklist (from `.agentic/quality-gates.yaml` @ plan time)

- `coverage_new_code_pct_min: 80` — measured by `node .agentic/scripts/check-new-code-coverage.mjs`
  (coverlet x git diff). The .NET additions (`ThemePalettes`, grain clamp, `ArtifactTheme` palette table, endpoint
  merge) are covered by the tests above. Frontend JS/CSS has no coverage tooling; guarded by the asset tests.
- `cognitive_complexity_max: 15` — SonarAnalyzer S3776 at build; `ArtifactTheme.Block` palette lookup must stay a
  dictionary lookup, not a switch ladder.
- `vulnerable_packages: 0`, no dependency change. CI also runs build, `dotnet format --verify-no-changes`, tests.

## Out of scope

- Per-palette code-highlight / mermaid themes (they keep the light/dark pair).
- Palette on public recipient pages (`/shared/*`, `/legal/*`) and the favicon / PWA manifest colors (static).
- The Android app's native Compose theme.
- Custom/user-defined palettes, per-agent or per-conversation palettes, font changes.
- Renaming `--amber` to `--accent` across the CSS.

## Implementation divergences (executed 2026-11-15)

- **Amber/light accent floor.** `#a86611` is 4.00:1 on `--bg` and 4.29:1 on `--bg-raised`, so the plan's 4.5:1 floor and
  "amber byte-identical" cannot both hold. Amber/light is pinned and exempted in `ThemePaletteTests` (3.9 / 4.2); the nine
  new palettes meet 4.5.
- **Fallbacks.** `themes.css` has a bare amber-light `:root[data-theme="light"]` block as well as the amber-dark `:root`, so
  `/shared/*` pages (which set only `data-theme`) keep OS light. Found by the plan critic.
- **`legal.css` untouched.** It has no JS and its own `prefers-color-scheme` block; deleting it would force dark. It stays
  amber + OS mode and does not load `themes.css`.
- **`--chevron` token.** Chevron data-URLs became a per-set `--chevron` custom property in `themes.css`.
- **`ArtifactTheme.Block/Apply`** take `palette` as an optional trailing argument (old callers compile); the table lives in
  `ArtifactThemePalettes.cs` and is public for the parity test.
- **Tests.** Palette frame tests went into `ThemePaletteTests` and `ArtifactsApiTests` (not `ArtifactThemeTests`); added a
  populated Palette round-trip + legacy document in `GrainStorageSerializerTests`; public-page checks in `PwaAssetsTests`.
- **`logs/` now follows the cached mode** as well as the palette (it was OS-only). `--green` there now equals the main app's.
- **Not done:** `clienttools.js` `browserContext` palette report (optional); the DevHost walk of the 20 combos (DevHost does
  not build under SDK 11 locally) — needs a manual look. New-code coverage gate reported "no production code changed"
  on an uncommitted tree; rerun after commit.
