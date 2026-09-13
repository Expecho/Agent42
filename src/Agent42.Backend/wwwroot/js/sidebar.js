/* Agent 42 — conversation sidebar. Split out of app.js (ADR 0041). */

import { cacheDelete, cacheGet, cachePut } from "./cache.js";
import { activeId, selectConversation, startNewConversation } from "./conversations.js";
import { query as searchQuery, renderSearchHits } from "./search.js";
import { conversationList } from "./dom.js";
import { timeLabel } from "./format.js";
import { buildChatGroup, groups, openMoveToGroup } from "./groups.js";
import { streaming } from "./send.js";
import { openShareLink } from "./sharelinks.js";

export let conversations = [];
// Sidebar agent-group state: the set of agent ids whose transmissions group is *expanded*.
// Persisting the expanded set (not the collapsed one) means a newly-seen agent is absent →
// starts collapsed by default, while an explicit expand survives reloads. Lives outside
// renderSidebar (which re-runs on every refresh); persistence is best-effort (private mode throws).
const EXPANDED_AGENTS_KEY = "agent42.sidebar.expandedAgents";
const expandedAgents = loadExpandedAgents();
export async function refreshConversations() {
  try {
    const res = await fetch("/api/conversations");
    if (!res.ok) return;
    conversations = await res.json();
    renderSidebar();
    cachePut("conversations", conversations); // mirror for offline (ADR 0032)
  } catch {
    // Offline/silo unreachable: fall back to the last cached list instead of an empty sidebar.
    // Only when we have nothing yet, so a live list already on screen is never overwritten.
    const cached = await cacheGet("conversations");
    if (cached && conversations.length === 0) { conversations = cached; renderSidebar(); }
  }
}

function loadExpandedAgents() {
  try {
    return new Set(JSON.parse(localStorage.getItem(EXPANDED_AGENTS_KEY) || "[]"));
  } catch { return new Set(); /* private mode or bad cache — all groups start collapsed */ }
}

function saveExpandedAgents() {
  try { localStorage.setItem(EXPANDED_AGENTS_KEY, JSON.stringify([...expandedAgents])); }
  catch { /* private mode — persistence is best-effort */ }
}

// Builds the flat conversation row (button + move/pin/share/delete) shared by ungrouped chats and the
// items inside a group of either kind.
//
// TWO independent facts decide the badges, which is why one flag could not carry them (ADR 0073):
//   `hoisted` — rendered in the pinned block at the top of the sidebar (ADR 0045), so the row sits
//               outside BOTH kinds of group container;
//   `grouped` — rendered inside a CUSTOM group, so it sits outside its agent group but inside the one
//               that names its custom group.
// A badge exists exactly when the container that would otherwise name the thing is missing: the agent
// badge when the row is outside its agent group (either case), the group badge only when hoisted.
//
// Exported because groups.js builds the members of a custom group — a deliberate import cycle between
// the two modules, which ADR 0041 §5 permits and several modules here already ship. Every binding is
// used at call time, never at module-evaluation time, which is what makes it safe.
export function buildConvRow(conv, hoisted, grouped) {
  const li = document.createElement("li");
  li.className = "conv-row" + (conv.pinned ? " is-pinned" : "");
  // Read by the delegated drag handlers in groups.js, which cannot close over `conv` — the rows are
  // rebuilt on every render, so the listeners live on the list, not on the row.
  li.dataset.convId = conv.id;
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "conv" + (conv.id === activeId ? " is-active" : "");
  btn.addEventListener("click", () => selectConversation(conv.id));

  const title = document.createElement("span");
  title.className = "conv__title";
  title.textContent = conv.title;

  const time = document.createElement("span");
  time.className = "conv__time";
  time.textContent = timeLabel(conv.updatedAt);

  btn.append(title, time);

  // Repay the label the hoist costs: only .conv-group__name renders an agent's name, and a row in the
  // pinned block — or filed into a CUSTOM group, which wins over the agent group (ADR 0073) — has no
  // agent header above it. Without this a pinned or filed agent chat is indistinguishable from a
  // regular one. Keyed on agentId with the same "Agent" fallback buildAgentGroup uses, so the two paths
  // agree: a chat with an agent but a blank name groups under "Agent" and must not appear with no label
  // at all. Keying it on agentName looks equivalent and is exactly the bug the badge exists to prevent.
  if ((hoisted || grouped) && conv.agentId) {
    const badge = document.createElement("span");
    badge.className = "conv__agent";
    badge.textContent = conv.agentName || "Agent";
    btn.append(badge);
  }

  // The same repayment for the custom group, and only in the pinned block: a row rendered INSIDE its
  // group already has that group's header above it.
  if (hoisted && conv.groupId) {
    const group = groups.find((g) => g.id === conv.groupId);
    if (group) {
      const badge = document.createElement("span");
      badge.className = "conv__group";
      badge.textContent = group.name;
      btn.append(badge);
    }
  }

  // File this chat into a custom group (ADR 0073). Present on every row at every width — the touch path
  // for what a mouse does by dragging, and the only way to CLEAR a filing.
  const move = document.createElement("button");
  move.type = "button";
  move.className = "conv__move";
  move.setAttribute("aria-label", `Move conversation to a group: ${conv.title}`);
  // Author-controlled markup, like the pin and share marks — not user data, so not a sanitiser bypass.
  move.innerHTML = '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">'
    + '<path d="M1.9 4.2c0-.6.5-1.1 1.1-1.1h2.8l1.3 1.6h5.9c.6 0 1.1.5 1.1 1.1v6c0 .6-.5 1.1-1.1 1.1H3c-.6 0-1.1-.5-1.1-1.1z"'
    + ' stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>'
    + '</svg>';
  move.addEventListener("click", (e) => {
    e.stopPropagation();
    openMoveToGroup(conv.id, conv.title, conv.groupId ?? null);
  });

  // Pin toggle (ADR 0045): hoists this chat above the recency list and keeps it there.
  const pin = document.createElement("button");
  pin.type = "button";
  pin.className = "conv__pin";
  pin.setAttribute("aria-pressed", String(!!conv.pinned));
  pin.setAttribute("aria-label", `${conv.pinned ? "Unpin" : "Pin"} conversation: ${conv.title}`);
  // Author-controlled markup, like the share mark below — not user data, so not a sanitiser bypass.
  pin.innerHTML = '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">'
    + '<path d="M9.6 1.9l4.5 4.5-1.6 1.6-1.3-.4-2.6 2.6.2 2.1-1.1 1.1L4.4 9.1l1.1-1.1 2.1.2 2.6-2.6-.4-1.3z"'
    + ' stroke="currentColor" stroke-width="1.3" stroke-linejoin="round"/>'
    + '<path d="M4.6 11.4L1.9 14.1" stroke="currentColor" stroke-width="1.3" stroke-linecap="round"/>'
    + '</svg>';
  pin.addEventListener("click", (e) => {
    e.stopPropagation();
    setPinned(conv.id, !conv.pinned);
  });

  // Read-only share link (ADR 0035). Present on every row, so both regular and agent chats can be shared.
  const share = document.createElement("button");
  share.type = "button";
  share.className = "conv__share";
  share.setAttribute("aria-label", `Share conversation: ${conv.title}`);
  // Same three-node share mark as the topbar button, so one action reads as one action. innerHTML is
  // safe here — this string is author-controlled markup, not user data, so it is not a sanitiser bypass.
  share.innerHTML = '<svg width="16" height="16" viewBox="0 0 16 16" fill="none" aria-hidden="true">'
    + '<circle cx="12" cy="3.5" r="1.9" stroke="currentColor" stroke-width="1.4"/>'
    + '<circle cx="4" cy="8" r="1.9" stroke="currentColor" stroke-width="1.4"/>'
    + '<circle cx="12" cy="12.5" r="1.9" stroke="currentColor" stroke-width="1.4"/>'
    + '<path d="M5.7 7.1l4.6-2.6M5.7 8.9l4.6 2.6" stroke="currentColor" stroke-width="1.4" stroke-linecap="round"/>'
    + '</svg>';
  share.addEventListener("click", (e) => {
    e.stopPropagation();
    openShareLink(conv.id, conv.title);
  });

  const del = document.createElement("button");
  del.type = "button";
  del.className = "conv__delete";
  del.setAttribute("aria-label", `Delete conversation: ${conv.title}`);
  del.textContent = "×";
  del.addEventListener("click", (e) => {
    e.stopPropagation();
    deleteConversation(conv.id, conv.title);
  });

  li.append(btn, move, pin, share, del);
  wireRowReveal(li, btn);
  return li;
}


// --- Long-press reveals a row's actions on touch (ADR 0052) ------------------------------------
// The three row actions are hover-reveals, and a hovering pointer is exactly what a phone lacks. ADR 0045
// answered that by lighting all three permanently under @media (hover: none); this replaces that with an
// explicit press-and-hold, so the list stays quiet and one row at a time shows its controls.
//
// The corresponding CSS lives in the (hover: none) block in styles.css, so on a hovering device this
// class is set but changes nothing — the :hover rules there are gated to (hover: hover) and still win.

// The one row currently showing its actions, or null. Only ever one: revealing clears the previous.
let revealedRow = null;
// Set when the hold completes, consumed by the click that ENDS that hold. Without it the same press both
// reveals the row and opens the chat. Cleared on the next pointerdown too, so a press that ends off-row
// (finger slid away, no click dispatched) can never strand it into the following tap.
let swallowNextClick = false;
let onDocPointerDown = null;

// How long the finger must stay down. Deliberately under Android's ~500ms native long-press threshold, so
// our reveal wins the race against the context menu / selection callout.
const LONG_PRESS_MS = 400;
// Slop allowed before the press is read as a scroll instead.
const LONG_PRESS_SLOP_PX = 10;

function clearReveal() {
  if (revealedRow) revealedRow.classList.remove("is-revealed");
  revealedRow = null;
  if (onDocPointerDown) {
    document.removeEventListener("pointerdown", onDocPointerDown, true);
    onDocPointerDown = null;
  }
}

function wireRowReveal(li, btn) {
  let timer = null;
  let startX = 0;
  let startY = 0;

  // Cancels a PENDING hold only. Never tears down a reveal that already fired: Android dispatches
  // pointercancel for its own long-press gesture at ~500ms — a good 100ms AFTER our timer — so treating
  // that as "dismiss" would make the reveal flash and die on the one platform this is for.
  const cancelPending = () => {
    if (timer === null) return;
    clearTimeout(timer);
    timer = null;
  };

  li.addEventListener("pointerdown", (e) => {
    swallowNextClick = false;
    // Drag-to-file is decided PER INTERACTION from this same pointer type, never from a media query
    // (ADR 0073). A fine-pointer media query reports the PRIMARY pointer, so a touchscreen laptop
    // matches it — and that is precisely the device where a live `draggable` would put the browser's
    // own long-press drag in a race with the 400 ms reveal below. One rule, one source of truth: a
    // mouse press arms the drag, a touch press disarms it and starts the reveal timer instead.
    // (The forbidden query is deliberately NOT spelled out here: a test asserts its absence from the
    // whole script with a plain substring search, which does not skip comments.)
    li.draggable = e.pointerType === "mouse";
    // A mouse has hover, so it never enters the gesture — that is what keeps a hybrid laptop on the
    // desktop behaviour rather than giving it both.
    if (e.pointerType === "mouse") return;
    startX = e.clientX;
    startY = e.clientY;
    cancelPending();
    timer = setTimeout(() => {
      timer = null;
      clearReveal();
      li.classList.add("is-revealed");
      revealedRow = li;
      swallowNextClick = true;
      // Registered at most once — guarded by the null check, not by self-removal alone, so repeated
      // reveals cannot stack listeners.
      if (!onDocPointerDown) {
        onDocPointerDown = (ev) => {
          // Only an outside press dismisses. A press INSIDE the revealed row is how its own pin/share/
          // delete get tapped, so clearing here would eat the very interaction the gesture exists for.
          if (revealedRow && !revealedRow.contains(ev.target)) clearReveal();
        };
        document.addEventListener("pointerdown", onDocPointerDown, true);
      }
    }, LONG_PRESS_MS);
  });

  li.addEventListener("pointermove", (e) => {
    if (Math.abs(e.clientX - startX) > LONG_PRESS_SLOP_PX || Math.abs(e.clientY - startY) > LONG_PRESS_SLOP_PX) cancelPending();
  });
  li.addEventListener("pointerup", cancelPending);
  li.addEventListener("pointercancel", cancelPending);

  // Suppress the native menu while this row is the revealed one — reactive, so it is a backstop to the
  // -webkit-touch-callout / user-select suppression in CSS rather than the primary defence.
  li.addEventListener("contextmenu", (e) => {
    if (revealedRow === li) e.preventDefault();
  });

  // Capture on btn, NOT on li: pin/share/delete are li's children, so an li capture listener would
  // intercept their clicks too, and their own stopPropagation runs later (bubble phase) and could not
  // save them. btn is the only node whose click means "open this chat".
  btn.addEventListener("click", (e) => {
    if (swallowNextClick) {
      // The click that ended the hold. Swallow it, but keep the reveal — it is what the user just asked for.
      swallowNextClick = false;
      e.preventDefault();
      e.stopPropagation();
      return;
    }
    if (revealedRow === li) {
      // A second tap on the row body dismisses rather than navigating.
      clearReveal();
      e.preventDefault();
      e.stopPropagation();
    }
  }, true);
}

// Renders one collapsible agent group: a header (chevron + name + count) toggling a body
// list of that agent's conversations. Groups start collapsed; an explicit expand is keyed
// by agent id and persisted.
function buildAgentGroup(agentId, agentName, convs) {
  const li = document.createElement("li");
  const collapsed = !expandedAgents.has(agentId);
  li.className = "conv-group" + (collapsed ? " is-collapsed" : "");

  const header = document.createElement("button");
  header.type = "button";
  header.className = "conv-group__header";
  header.setAttribute("aria-expanded", String(!collapsed));

  const chevron = document.createElement("span");
  chevron.className = "conv-group__chevron";
  chevron.setAttribute("aria-hidden", "true");
  chevron.textContent = "▸";

  const name = document.createElement("span");
  name.className = "conv-group__name";
  name.textContent = agentName || "Agent";

  const count = document.createElement("span");
  count.className = "conv-group__count";
  count.textContent = String(convs.length);

  header.append(chevron, name, count);
  header.addEventListener("click", () => {
    if (expandedAgents.has(agentId)) expandedAgents.delete(agentId);
    else expandedAgents.add(agentId);
    saveExpandedAgents();
    renderSidebar();
  });

  const body = document.createElement("ul");
  body.className = "conv-group__body";
  // Inside its agent group: neither badge is needed, since this header is the thing that names it.
  for (const conv of convs) body.appendChild(buildConvRow(conv, false, false));

  li.append(header, body);
  return li;
}

// Pass 1 — the pinned block (ADR 0045). Pinned chats render FLAT at the top, agent-bound, filed or
// neither. Hoisting rather than re-sorting is required, not cosmetic: pass 3 places an agent's group at
// its first-encountered member, so a pinned agent chat left in the walk would drag its ENTIRE group to
// the top. The server already returns pinned-first, sorted by recency among themselves — so this emits
// in order and does not re-sort. A hoisted row is skipped by passes 2 and 3, never rendered twice.
// Returns the last row emitted, for the divider marker.
function renderPinnedBlock() {
  let lastPinned = null;
  for (const conv of conversations) {
    if (conv.pinned) lastPinned = conversationList.appendChild(buildConvRow(conv, true, false));
  }
  return lastPinned;
}

// Pass 2 — the user's custom groups (ADR 0073), in their MANUAL order, as a stable block between the
// pinned rows and the recency walk. Empty groups are rendered too: a group with no members is still a
// valid drop target, and being able to exist with none is the whole reason groups are first-class
// entities rather than a label denormalized onto each conversation.
function renderCustomGroups() {
  for (const group of groups) {
    const members = conversations.filter((c) => !c.pinned && c.groupId === group.id);
    conversationList.appendChild(buildChatGroup(group, members));
  }
}

// Pass 3 — the unchanged agent-grouping walk, over what neither earlier pass took: unpinned chats that
// are not filed into a KNOWN custom group. Non-agent chats render as flat rows; agent chats collect
// into a per-agent group placed at its first-encountered (= most-recent) member, so those groups
// interleave with regular chats by recency. An agent whose every chat is pinned or filed has no members
// left here and so gets no group at all — correct, because those rows carry the agent badge.
//
// Testing membership against the known ids rather than `conv.groupId` alone is what makes a dangling
// filing self-healing: a conversation pointing at a group that no longer exists falls through to here
// and renders ungrouped, instead of vanishing from the sidebar entirely.
function renderAgentWalk() {
  const knownGroupIds = new Set(groups.map((g) => g.id));
  const agentGroups = new Map();

  for (const conv of conversations) {
    if (conv.pinned || (conv.groupId && knownGroupIds.has(conv.groupId))) continue;
    if (!conv.agentId) {
      conversationList.appendChild(buildConvRow(conv, false, false));
      continue;
    }
    let group = agentGroups.get(conv.agentId);
    if (!group) {
      // Placeholder <li> reserves this group's slot in recency order; filled in below.
      const placeholder = document.createElement("li");
      conversationList.appendChild(placeholder);
      group = { name: conv.agentName, convs: [], placeholder };
      agentGroups.set(conv.agentId, group);
    }
    group.convs.push(conv);
  }

  for (const [agentId, group] of agentGroups) {
    group.placeholder.replaceWith(buildAgentGroup(agentId, group.name, group.convs));
  }
}

export function renderSidebar() {
  // Every row is about to be destroyed and rebuilt, so the revealed node is going with them.
  clearReveal();
  conversationList.innerHTML = "";

  // Search (ADR 0061). This branch is INSIDE renderSidebar on purpose: nine call sites re-render
  // this list independently of the search box — a background refresh, cache hydration (ADR 0032),
  // an agent save, send()'s success path — and every one of them would wipe an active result list
  // if the hits were painted by a separate render path beside this one.
  if (searchQuery) {
    renderSearchHits(conversationList);
    return;
  }

  // A user with groups but no chats yet is not empty — their groups are on screen and droppable.
  if (conversations.length === 0 && groups.length === 0) {
    const li = document.createElement("li");
    li.className = "conversations__empty";
    li.textContent = "no transmissions yet";
    conversationList.appendChild(li);
    return;
  }

  // Three passes, in this order. They are separate functions because this one measured exactly at the
  // cognitive-complexity ceiling with two (S3776, threshold 15 — a build error, ADR 0051), and a third
  // pass tipped it. What stays HERE is deliberate and load-bearing rather than leftover: clearReveal,
  // the search branch, the empty guard and the divider marker below are all asserted against this
  // function's own body by existing source-text guards.
  const lastPinned = renderPinnedBlock();
  renderCustomGroups();
  renderAgentWalk();

  // Mark the row that ends the pinned block so CSS can draw the divider below it.
  // Done here rather than with a :has() selector: pass 1 already knows which row is last, this is the
  // only place in the sheet that would need :has(), and — unlike the selector — it can also tell that
  // something FOLLOWS the block, so an all-pinned list draws no trailing divider under its last row.
  if (lastPinned && conversationList.lastElementChild !== lastPinned) {
    lastPinned.classList.add("is-pinned-last");
  }
}

// Pins or unpins a conversation, then reconciles the sidebar. Mirrors deleteConversation's
// mutate-then-reconcile shape: on failure the row is left exactly as it was.
async function setPinned(id, pinned) {
  if (streaming) return;

  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(id)}/pin`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ pinned }),
    });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the row as it is */ }

  conversations = conversations.map((c) => (c.id === id ? { ...c, pinned } : c));
  renderSidebar();
  refreshConversations();
}

// Permanently deletes a conversation (session + reminders) after confirmation, then
// reconciles the sidebar. If the open conversation is deleted, reset to a new chat.
async function deleteConversation(id, title) {
  if (streaming) return;
  if (!confirm(`Delete "${title}"? This can't be undone.`)) return;

  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(id)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the row in place */ }

  conversations = conversations.filter((c) => c.id !== id);
  cacheDelete("messages:" + id); // drop the offline copy; refreshConversations re-caches the list
  if (id === activeId) startNewConversation();
  else renderSidebar();
  refreshConversations();
}

export function setConversations(value) {
  conversations = value;
}
