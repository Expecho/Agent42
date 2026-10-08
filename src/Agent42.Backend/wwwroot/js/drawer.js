/* Agent 42 — sidebar chrome: the mobile drawer and the desktop collapse. Split out of app.js (ADR 0041). */

import { app, backdrop, collapseSidebarBtn, menuToggle } from "./dom.js";

const root = document.documentElement;
const mobile = matchMedia("(max-width: 760px)");
// Must match the pre-paint rule in index.html, which applies the same initial state before first paint.
const narrow = matchMedia("(min-width: 761px) and (max-width: 1100px)");
const COLLAPSED_KEY = "agent42.sidebar.collapsed";

// Off-canvas sidebar toggle for ≤760px. The backdrop's visibility and the slide
// animation are owned entirely by the CSS `.app.is-drawer-open` rules; JS only flips
// that class and keeps aria-expanded in sync. No-ops harmlessly on desktop.
function openDrawer() {
  app.classList.add("is-drawer-open");
  menuToggle.setAttribute("aria-expanded", "true");
}

export function closeDrawer() {
  app.classList.remove("is-drawer-open");
  menuToggle.setAttribute("aria-expanded", "false");
}

function toggleDrawer() {
  if (app.classList.contains("is-drawer-open")) closeDrawer();
  else openDrawer();
}

// Desktop collapse (>760px). Two inputs decide it:
//   `userCollapsed` — the last explicit choice, persisted per device (best-effort: private mode throws);
//   `autoCollapsed` — a narrow window or an open artifact pane, both of which want the room.
// An auto trigger collapses on its EDGE only, so expanding by hand while it holds sticks until it
// changes; when it clears, the sidebar returns to the user's own choice. The CSS only applies the
// `html.is-sidebar-collapsed` class above 760px, so the mobile drawer never sees it.
let userCollapsed = readCollapsed();
let autoCollapsed = autoTrigger();

function readCollapsed() {
  try { return localStorage.getItem(COLLAPSED_KEY) === "1"; } catch { return false; }
}

function autoTrigger() {
  return narrow.matches || (!mobile.matches && root.classList.contains("has-artifact-pane"));
}

function setCollapsed(collapsed) {
  root.classList.toggle("is-sidebar-collapsed", collapsed);
  if (!mobile.matches) menuToggle.setAttribute("aria-expanded", String(!collapsed));
}

function chooseCollapsed(collapsed) {
  userCollapsed = collapsed;
  try { localStorage.setItem(COLLAPSED_KEY, collapsed ? "1" : "0"); } catch { /* not persisted */ }
  setCollapsed(collapsed);
}

function onAutoTriggerChange() {
  const next = autoTrigger();
  if (next === autoCollapsed) return;
  autoCollapsed = next;
  setCollapsed(autoCollapsed || userCollapsed);
}

export function wireDrawer() {
  setCollapsed(autoCollapsed || userCollapsed);

  // One hamburger, two jobs: the drawer on mobile, reopening a collapsed sidebar on desktop (where the
  // CSS shows it only while collapsed).
  menuToggle.addEventListener("click", () => {
    if (mobile.matches) toggleDrawer();
    else chooseCollapsed(false);
  });
  collapseSidebarBtn.addEventListener("click", () => {
    if (mobile.matches) { closeDrawer(); menuToggle.focus(); }
    else { chooseCollapsed(true); menuToggle.focus(); }
  });
  backdrop.addEventListener("click", closeDrawer);

  narrow.addEventListener("change", onAutoTriggerChange);
  mobile.addEventListener("change", onAutoTriggerChange);
  // The artifact pane is shared with the public viewer (shared/lib/artifactpane.js) and signals only
  // through this class on <html>, so watch the class rather than coupling the pane to the sidebar.
  new MutationObserver(onAutoTriggerChange).observe(root, { attributes: true, attributeFilter: ["class"] });

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && app.classList.contains("is-drawer-open")) {
      closeDrawer();
      menuToggle.focus();
    }
  });
}
