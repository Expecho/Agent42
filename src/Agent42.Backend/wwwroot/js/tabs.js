/* Agent 42 — shared ARIA tablist controller. Split out of app.js (ADR 0041). */

// Shared ARIA tablist controller. Given [tab, panel] pairs in tablist order, attaches each tab's
// click + arrow/Home/End keyboard nav and returns { select(index, focus) } — select toggles
// aria-selected + roving tabindex on the tabs and `hidden` on the panels; `focus` moves keyboard
// focus to the newly selected tab (arrow-key nav). Used by both the Preferences and agent dialogs.
export function wireTabs(tabPairs) {
  function select(index, focus = false) {
    tabPairs.forEach(([tab, panel], i) => {
      const active = i === index;
      tab.setAttribute("aria-selected", active ? "true" : "false");
      tab.tabIndex = active ? 0 : -1;
      panel.hidden = !active;
      if (active && focus) tab.focus();
    });
  }
  // The keyboard moves over the tabs that are SHOWN. A [hidden] tab (the owner-only Configuration tab for
  // a non-owner) must be skipped: select() would un-hide its panel, and focus() on a hidden button is a no-op.
  function nextIndex(key, from) {
    const shown = tabPairs.map((_, i) => i).filter((i) => !tabPairs[i][0].hidden);
    const at = shown.indexOf(from);
    if (shown.length === 0 || at < 0) return null;
    if (key === "ArrowRight" || key === "ArrowDown") return shown[(at + 1) % shown.length];
    if (key === "ArrowLeft" || key === "ArrowUp") return shown[(at - 1 + shown.length) % shown.length];
    if (key === "Home") return shown[0];
    if (key === "End") return shown[shown.length - 1];
    return null;
  }
  tabPairs.forEach(([tab], i) => {
    tab.addEventListener("click", () => select(i));
    tab.addEventListener("keydown", (e) => {
      const next = nextIndex(e.key, i);
      if (next !== null) {
        e.preventDefault();
        select(next, true);
      }
    });
  });
  return { select };
}
