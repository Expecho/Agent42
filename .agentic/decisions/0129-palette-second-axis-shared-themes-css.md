---
id: 0129
date: 2026-11-15
status: accepted
tags: [frontend, theme, pwa, profile, artifacts]
supersedes: []
extends: [0005, 0083]
amends: []
---

# Palette is a second axis; one shared `themes.css`; ArtifactTheme parity test

## Context
The app had one palette (amber phosphor) with a System/Light/Dark mode (ADR 0005). The palette was copy-pasted into
five stylesheets, `ArtifactTheme.cs` and five head scripts. Ten palettes, each with a dark and a light set, would
have been 60+ drifting copies.

## Decision
1. **Two independent axes.** `UserProfile.Theme` (`system|light|dark`) is unchanged. `UserProfile.Palette`
   (`[Id(4)]`, nullable, null = amber) is new; `ThemePalettes.Ids` (in `Agent42.Agents`) is the allow-list, and the
   grain clamps unknown/blank to `amber`. `PUT /api/profile` treats a null `palette` as "keep stored".
2. **`src/Agent42.Backend/wwwroot/shared/themes.css` is the single palette source**: 10 palettes x {dark, light} as
   `:root[data-palette][data-theme]` sets, plus an amber-dark `:root` and an amber-light `:root[data-theme="light"]`
   fallback so a page that sets only `data-theme` (the public `/shared` pages) or neither stays readable and amber.
   `--amber` keeps its name and means "the accent". The chevron data-URLs became a per-set `--chevron` token.
3. **`ArtifactTheme` keeps a C# table** (`ArtifactThemePalettes.cs`; a framed artifact has an opaque origin and cannot
   load the CSS). `ThemePaletteTests` parses `themes.css` and asserts the table equals it, the ten ids match
   `ThemePalettes`, every contract token exists, and the WCAG floors hold. `/frame` reads `?palette=` beside `?theme=`.
4. **Who follows the palette:** the main app, `/fleet/` and `/logs` (cached `agent42.palette` + mode). The public
   recipient pages (`/shared/*`, `/legal/*`) have no profile and stay amber + OS mode. `legal.css` keeps its own small
   `prefers-color-scheme` block (no JS there).
5. `theme-color` is read from the resolved `--bg` after `themes.css` loads; head scripts sit after the link.

## Alternatives considered
- **Encode `"ocean-dark"` into `Theme`.** Breaks "System follows the OS" and renames a persisted field (ADR 0005).
- **Generate the C# table from the CSS at build time.** More machinery than a parity test for 20 short strings.
- **Per-palette highlight.js/mermaid themes.** Out of scope; they keep the light/dark pair.

## Consequences
- Palette edits happen in `themes.css` only; a C# table edit alone fails `ThemePaletteTests`.
- **Amber light's accent (#a86611) is 4.00:1 on `--bg`**, under the 4.5:1 floor the nine new palettes meet. It is
  pinned byte-identical so existing users see no change; the test names it as the one exception.
- `/logs` now follows the user's cached mode too (it was OS-only); its `--green` now equals the main app's.
- The amber glow/shadow literals now follow the accent via `color-mix`, so amber/light glows are slightly darker.
- The favicon, PWA manifest colours, the Android Compose theme and code-highlight themes stay amber / light-dark.

## Evidence
`src/Agent42.Backend/wwwroot/shared/themes.css`, `src/Agent42.Agents/Users/ThemePalettes.cs`,
`src/Agent42.Backend/Artifacts/ArtifactThemePalettes.cs`, `tests/Agent42.Tests/ThemePaletteTests.cs`,
`src/Agent42.Backend/wwwroot/js/theme.js`.
