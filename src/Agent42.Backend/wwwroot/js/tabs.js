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
  tabPairs.forEach(([tab], i) => {
    tab.addEventListener("click", () => select(i));
    tab.addEventListener("keydown", (e) => {
      const last = tabPairs.length - 1;
      let next = null;
      if (e.key === "ArrowRight" || e.key === "ArrowDown") next = i === last ? 0 : i + 1;
      else if (e.key === "ArrowLeft" || e.key === "ArrowUp") next = i === 0 ? last : i - 1;
      else if (e.key === "Home") next = 0;
      else if (e.key === "End") next = last;
      if (next !== null) {
        e.preventDefault();
        select(next, true);
      }
    });
  });
  return { select };
}
