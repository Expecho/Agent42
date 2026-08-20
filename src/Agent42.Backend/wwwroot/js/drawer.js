/* Agent 42 — mobile drawer. Split out of app.js (ADR 0041). */

import { app, backdrop, menuToggle } from "./dom.js";

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

export function wireDrawer() {
  menuToggle.addEventListener("click", toggleDrawer);
  backdrop.addEventListener("click", closeDrawer);

  document.addEventListener("keydown", (e) => {
    if (e.key === "Escape" && app.classList.contains("is-drawer-open")) {
      closeDrawer();
      menuToggle.focus();
    }
  });
}
