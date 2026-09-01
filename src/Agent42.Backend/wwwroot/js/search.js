import { searchInput } from "./dom.js";
import { conversations, renderSidebar } from "./sidebar.js";
import { selectConversation } from "./conversations.js";

// The active query, and the hits it produced. Read by renderSidebar (which owns the DOM) — the
// search branch lives INSIDE that function rather than beside it, because renderSidebar is called
// from nine sites that fire independently of this box (a background refresh, ADR 0032 cache
// hydration, an agent save, send()'s success path). A separate render path outside it would be
// silently wiped by any of them while the user is reading the results. See ADR 0061.
export let query = "";
export let hits = [];

// Debounce timer and a monotonic request id: keystrokes race, and without the id a slow early
// response can land after a fast later one and paint stale hits for a query the user has moved on
// from. Compared on BOTH the success and failure paths, so a failed early request cannot clear a
// newer request's results either.
let timer = null;
let latest = 0;

const DEBOUNCE_MS = 250;
const SNIPPET_RADIUS = 60;

export function wireSearch() {
  if (!searchInput) return;

  searchInput.addEventListener("input", () => {
    clearTimeout(timer);
    timer = setTimeout(() => runSearch(searchInput.value.trim()), DEBOUNCE_MS);
  });

  // Escape clears the box and returns the list, without waiting out the debounce.
  searchInput.addEventListener("keydown", (e) => {
    if (e.key !== "Escape") return;
    clearTimeout(timer);
    searchInput.value = "";
    runSearch("");
  });
}

async function runSearch(q) {
  query = q;

  if (!q) {
    hits = [];
    latest++; // invalidate anything in flight, so a late response cannot repaint a cleared box
    renderSidebar();
    return;
  }

  const mine = ++latest;
  try {
    const res = await fetch(`/api/search?q=${encodeURIComponent(q)}`);
    if (mine !== latest) return;
    hits = res.ok ? (await res.json()).hits ?? [] : [];
  } catch {
    if (mine !== latest) return;
    hits = []; // offline or silo unreachable: an empty result, not a broken list
  }

  renderSidebar();
}

// Renders the hit list into the conversation list element. Called from inside renderSidebar, which
// has already cleared the element.
export function renderSearchHits(listEl) {
  if (hits.length === 0) {
    const li = document.createElement("li");
    li.className = "conversations__empty";
    li.textContent = "no matches";
    listEl.appendChild(li);
    return;
  }

  for (const hit of hits) {
    listEl.appendChild(buildHitRow(hit));
  }
}

function buildHitRow(hit) {
  const li = document.createElement("li");
  li.className = "conv-row";

  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "conv search-hit";

  const title = document.createElement("span");
  title.className = "search-hit__title";
  title.textContent = titleFor(hit.conversationId);

  const snippet = document.createElement("span");
  snippet.className = "search-hit__snippet";
  // textContent, never innerHTML: this is message text, and the match is highlighted by CSS on the
  // row rather than by injecting markup around the term.
  snippet.textContent = snippetOf(hit.text, query);

  btn.append(title, snippet);
  btn.addEventListener("click", () => selectConversation(hit.conversationId));

  li.appendChild(btn);
  return li;
}

// The conversation's title if the sidebar knows it. A hit can outlive the cached list (a chat
// indexed on another device, say), so this degrades to the id rather than rendering an empty row.
function titleFor(conversationId) {
  const conv = conversations.find((c) => c.id === conversationId);
  return conv?.title || conversationId;
}

// A window of text around the first match, so the row shows why it matched rather than the opening
// words of a long message.
function snippetOf(text, q) {
  const at = text.toLowerCase().indexOf(q.toLowerCase());
  if (at < 0) return text.slice(0, SNIPPET_RADIUS * 2);

  const start = Math.max(0, at - SNIPPET_RADIUS);
  const end = Math.min(text.length, at + q.length + SNIPPET_RADIUS);
  return (start > 0 ? "…" : "") + text.slice(start, end) + (end < text.length ? "…" : "");
}
