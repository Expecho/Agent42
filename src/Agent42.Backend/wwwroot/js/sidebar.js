/* Agent 42 — conversation sidebar. Split out of app.js (ADR 0041). */

import { cacheDelete, cacheGet, cachePut } from "./cache.js";
import { activeId, selectConversation, startNewConversation } from "./conversations.js";
import { conversationList } from "./dom.js";
import { timeLabel } from "./format.js";
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

// Builds the flat conversation row (button + delete) shared by ungrouped chats and the
// items inside an agent group.
function buildConvRow(conv) {
  const li = document.createElement("li");
  li.className = "conv-row";
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

  li.append(btn, share, del);
  return li;
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
  for (const conv of convs) body.appendChild(buildConvRow(conv));

  li.append(header, body);
  return li;
}

export function renderSidebar() {
  conversationList.innerHTML = "";
  if (conversations.length === 0) {
    const li = document.createElement("li");
    li.className = "conversations__empty";
    li.textContent = "no transmissions yet";
    conversationList.appendChild(li);
    return;
  }

  // Walk the (recency-sorted) list once. Non-agent chats render as flat rows; agent chats
  // collect into a per-agent group placed at its first-encountered (= most-recent) member,
  // so groups interleave with regular chats by recency.
  const groups = new Map();
  for (const conv of conversations) {
    if (!conv.agentId) {
      conversationList.appendChild(buildConvRow(conv));
      continue;
    }
    let group = groups.get(conv.agentId);
    if (!group) {
      // Placeholder <li> reserves this group's slot in recency order; filled in below.
      const placeholder = document.createElement("li");
      conversationList.appendChild(placeholder);
      group = { name: conv.agentName, convs: [], placeholder };
      groups.set(conv.agentId, group);
    }
    group.convs.push(conv);
  }

  for (const [agentId, group] of groups) {
    group.placeholder.replaceWith(buildAgentGroup(agentId, group.name, group.convs));
  }
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
