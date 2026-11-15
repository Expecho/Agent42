/* Agent 42 — theme engine (ADR 0005). Split out of app.js (ADR 0041). */

import { loadProfile, profile } from "./profile.js";

// Theme engine (ADR 0005 field #2). The server profile is the source of truth; THEME_KEY is
// only a pre-paint cache the inline head script reads to avoid a dark flash. applyTheme resolves
// "system" via matchMedia and, only for "system", keeps a live OS-follow listener registered.
const THEME_KEY = "agent42.theme";
// Palette is the second axis (ADR 0129): the same server-profile-wins / pre-paint-cache split as the mode.
// The ids mirror ThemePalettes.Ids and the data-palette sets in shared/themes.css; an unknown id falls through
// to the amber fallback blocks there.
const PALETTE_KEY = "agent42.palette";
const PALETTES = ["amber", "ocean", "indigo", "violet", "rose", "crimson", "coral", "forest", "teal", "graphite"];
let systemThemeMedia = null;      // the (prefers-color-scheme: dark) MediaQueryList, when following
let onSystemThemeChange = null;   // its change handler, so it can be removed on an explicit choice
// Paint an already-resolved theme ("light"|"dark"): set data-theme on <html> and sync the
// mobile browser-chrome color. Kept separate so the OS-follow listener can repaint cheaply.
function paintTheme(resolved) {
  document.documentElement.dataset.theme = resolved;
  syncThemeColor(resolved);
}

// The browser chrome follows the palette's own --bg (themes.css), else the amber hexes.
function syncThemeColor(resolved) {
  const meta = document.querySelector('meta[name="theme-color"]');
  if (!meta) return;
  const bg = getComputedStyle(document.documentElement).getPropertyValue("--bg").trim();
  meta.content = bg || (resolved === "dark" ? "#100f0d" : "#f4efe4");
}

// Apply a palette id: set data-palette (unknown/blank -> amber), repaint the theme-color meta, write the
// pre-paint cache. Independent of the mode: the data-theme attribute is untouched.
export function applyPalette(id) {
  const p = PALETTES.includes(id) ? id : "amber";
  document.documentElement.dataset.palette = p;
  syncThemeColor(document.documentElement.dataset.theme === "light" ? "light" : "dark");
  try { localStorage.setItem(PALETTE_KEY, p); } catch { /* private mode — cache is best-effort */ }
}

// Resolve a stored choice to the explicit theme actually painted.
function resolveTheme(choice) {
  if (choice === "light" || choice === "dark") return choice;
  return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
}

// Apply a theme choice ("system"|"light"|"dark"): paint it, write the pre-paint cache, and —
// only for "system" — (re)register a live OS-follow listener (removed for an explicit choice).
export function applyTheme(choice) {
  const c = choice === "light" || choice === "dark" || choice === "system" ? choice : "system";
  paintTheme(resolveTheme(c));
  try { localStorage.setItem(THEME_KEY, c); } catch { /* private mode — cache is best-effort */ }

  if (systemThemeMedia && onSystemThemeChange) {
    systemThemeMedia.removeEventListener("change", onSystemThemeChange);
    systemThemeMedia = null;
    onSystemThemeChange = null;
  }
  if (c === "system") {
    systemThemeMedia = matchMedia("(prefers-color-scheme: dark)");
    onSystemThemeChange = () => paintTheme(resolveTheme("system"));
    systemThemeMedia.addEventListener("change", onSystemThemeChange);
  }
}

// Boot the theme: re-apply the cached choice (the head script painted it but registered no
// OS-follow listener), then reconcile against the server profile, which wins (ADR 0005).
export async function initTheme() {
  let cached = "system";
  try { cached = localStorage.getItem(THEME_KEY) ?? "system"; } catch { /* private mode */ }
  applyTheme(cached);
  let cachedPalette = "amber";
  try { cachedPalette = localStorage.getItem(PALETTE_KEY) ?? "amber"; } catch { /* private mode */ }
  applyPalette(cachedPalette);
  await loadProfile();
  if (profile && profile.theme) applyTheme(profile.theme);
  // A legacy profile carries no palette (null): that means amber, and it must not leave a stale cached one.
  if (profile) applyPalette(profile.palette);
}
