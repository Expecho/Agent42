---
id: 0086
date: 2026-10-08
status: accepted
tags: [frontend, ux, sidebar, topbar, artifacts, css, pwa]
supersedes: []
extends: []
amends: [0044]
---

# Let the desktop sidebar collapse, by hand or automatically

## Context
Above 760px the 300px sidebar was always on screen. Since the artifact pane (ADR 0084) takes
`min(720px, 48vw)` from the right, a 1400px window left the chat about 430px. Narrow desktop windows had
the same squeeze. The sidebar could only be hidden at ≤760px, where it is the off-canvas drawer.
ADR 0044 decision 6 made the topbar hamburger and brand mobile-only, which left no way to reopen a hidden
sidebar on desktop.

## Decision
- **Manual.** A hide button (`#collapseSidebarBtn`) sits in the sidebar header. The topbar hamburger
  reopens the sidebar. On desktop the hamburger shows **only while the sidebar is collapsed**, and the
  topbar brand shows with it. This amends ADR 0044 decision 6: the "42 AGENT 42" twice trap cannot occur,
  because the sidebar header is gone. On mobile the same hide button closes the drawer.
- **Auto.** The sidebar collapses while the window is 761–1100px wide or the artifact pane is open.
  `src/Agent42.Backend/wwwroot/js/drawer.js` watches the pane's `html.has-artifact-pane` class with a `MutationObserver`. That keeps
  `src/Agent42.Backend/wwwroot/shared/lib/artifactpane.js`, which the public viewer also loads, unaware of the sidebar.
- **Precedence.** An auto trigger acts only when it **starts or stops holding**. Reopening the sidebar by
  hand while a trigger holds therefore sticks until the trigger changes. When a trigger clears, the
  sidebar returns to the user's own last choice.
- **State.** One class, `html.is-sidebar-collapsed`, applied only inside `@media (min-width: 761px)`, so
  it never affects the mobile drawer. The user's choice is stored per device in `localStorage`
  (`agent42.sidebar.collapsed`). The inline script in `index.html` applies the same initial rule before
  first paint, so a stored collapse does not flash the sidebar in.
- **No animation.** The toggle is instant.

## Alternatives considered
- **Hover-reveal auto-hide** (the sidebar slides out when the pointer reaches the left edge). Rejected by
  the owner in favour of collapsing in response to the pane and the window width.
- **Store the choice in the server profile** (as the theme is, ADR 0005). Rejected: whether the sidebar
  fits depends on the device's viewport. A phone and a 4K monitor should not share the setting, and
  per-device `localStorage` is the existing pattern for sidebar view state (expanded agent and group
  sections).
- **A Preferences switch to turn auto-collapse off.** Not built. Reopening by hand already overrides a
  trigger for as long as it holds. Add the switch if auto-collapse proves annoying in use.
- **Animate the grid track to 0.** Rejected: as the column narrows, the sidebar's content either reflows
  at every frame or overflows across the chat. A clean slide would need the sidebar taken out of the grid
  (`position: fixed`, as the mobile drawer does), which is a larger layout change than this feature
  justifies.
- **A new `sidebarcollapse.js` module.** Rejected: `drawer.js` already owns the sidebar's chrome and the
  hamburger. A new module would also need a `SHELL` entry and its own wiring row.

## Consequences
- The 1100px threshold appears in **two** places: the pre-paint script in `index.html` and `drawer.js`.
  `SidebarCollapseAssetsTests.PrePaintRule_MatchesTheModule` pins that the two agree.
- `TopbarActionsAssetsTests.TopbarBrandAndMenu_AreMobileOnly` still holds for the base rules and the ≤760px
  block. The desktop exception for a collapsed sidebar is pinned separately.
- No automated test checks which rule wins at a given width (the same cascade limit as ADR 0044).
  **Manual check pending:** the browser extension was unavailable when this was built. Check by hand in
  DevHost: hide and reopen; resize across 1100px; open and close an artifact; reload while collapsed.
