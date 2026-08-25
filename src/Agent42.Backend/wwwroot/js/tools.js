/* Agent 42 — selectable tools (ADR 0021/0026). Split out of app.js (ADR 0041). */

import { activeId } from "./conversations.js";
import { chatToolChecks, profileToolChecks, toolsBtn, toolsDialog, toolsDoneBtn } from "./dom.js";

// The selectable-tools catalog (ADR 0021), fetched once from /api/tools: [{key,label,requiresHostedTools}].
export let toolCatalog = [];
// The current conversation's effective tool selection (list of keys). Set on new/select; the composer
// popover renders from it and PUTs changes. "All" is the full explicit list — never null (server: null==all).
let chatTools = [];
// The catalog is the single source of truth in the backend (ToolCatalog); fetch it once and drive
// every checkbox group off it, so a new tool group appears in all three surfaces with no UI change.
export async function loadTools() {
  try {
    const res = await fetch("/api/tools");
    if (!res.ok) return;
    toolCatalog = await res.json();
  } catch { /* silo may be unreachable; the catalog stays empty and no checkboxes render */ }
}

export const allToolKeys = () => toolCatalog.map((t) => t.key);

// Re-fetch /api/tools after MCP CRUD (ADR 0026): the catalog is per-user for the MCP portion, so a newly
// added/removed/toggled server must appear/disappear in the pickers without a page reload. Re-render the
// Preferences Tools tab in place (preserving current checks) so it reflects the change even if it was
// already open; the composer popover and agent editor re-render on their next open.
export async function refreshToolCatalog() {
  await loadTools();
  if (profileToolChecks.children.length > 0) {
    renderToolChecks(profileToolChecks, readToolChecks(profileToolChecks));
  }
}

// Renders one checkbox per catalog tool into `container`, checked when its key is in `selectedKeys`.
// Two prefixed groups render under their own subheadings beneath the built-in tools: per-skill keys
// (`skill:{name}`, per-skill selectable tools) and per-server MCP keys (`mcp:{id}`, ADR 0026).
//
// A stored/effective selection may carry EITHER bare wildcard — `skills` or `mcp` — because a null==all
// default reached the client, or because it is legacy state. Neither has a checkbox, so each is treated as
// "every box in that group checked", so a save re-emits explicit per-entry keys instead of silently dropping
// the wildcard and disabling the whole group. That is the ADR 0026 hazard, and it now has two instances:
// both wildcard terms below are load-bearing and neither may be removed.
export function renderToolChecks(container, selectedKeys) {
  const selected = new Set(selectedKeys);
  const mcpWildcard = selected.has("mcp");
  const skillsWildcard = selected.has("skills");
  container.innerHTML = "";
  // Built-ins must exclude BOTH prefixes, or the prefixed groups would render twice.
  const builtins = toolCatalog.filter((t) => !t.key.startsWith("mcp:") && !t.key.startsWith("skill:"));
  const skills = toolCatalog.filter((t) => t.key.startsWith("skill:"));
  const mcpServers = toolCatalog.filter((t) => t.key.startsWith("mcp:"));

  const addCheck = (tool) => {
    const label = document.createElement("label");
    label.className = "tool-check";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.value = tool.key;
    cb.checked = selected.has(tool.key)
      || (mcpWildcard && tool.key.startsWith("mcp:"))
      || (skillsWildcard && tool.key.startsWith("skill:"));
    const span = document.createElement("span");
    span.textContent = tool.requiresHostedTools ? `${tool.label} (Responses)` : tool.label;
    label.append(cb, span);
    container.appendChild(label);
  };

  const addGroup = (heading, group) => {
    if (group.length === 0) return;
    const el = document.createElement("div");
    el.className = "tool-check__heading";
    el.textContent = heading;
    container.appendChild(el);
    for (const tool of group) addCheck(tool);
  };

  for (const tool of builtins) addCheck(tool);
  addGroup("Skills", skills);
  addGroup("MCP servers", mcpServers);
}

export const readToolChecks = (container) =>
  [...container.querySelectorAll("input[type=checkbox]")].filter((c) => c.checked).map((c) => c.value);

// The per-conversation popover. Renders from chatTools; each toggle PUTs the new set for the active
// conversation. The id is minted upfront (startNewConversation), so this persists an override even
// before the first message — the grain activates and stores it, and the first send reuses that grain.
function openToolsDialog() {
  renderToolChecks(chatToolChecks, chatTools);
  toolsDialog.showModal();
}

async function persistChatTools() {
  chatTools = readToolChecks(chatToolChecks);
  if (!activeId) return;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(activeId)}/tools`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ tools: chatTools }),
    });
    if (res.ok) chatTools = await res.json(); // reconcile with the server-sanitized effective set
  } catch { /* silo may be unreachable; keep the local selection, retried on the next toggle */ }
}

export function wireTools() {
  toolsBtn.addEventListener("click", openToolsDialog);
  toolsDoneBtn.addEventListener("click", () => toolsDialog.close());
  chatToolChecks.addEventListener("change", persistChatTools);
}

export function setChatTools(value) {
  chatTools = value;
}
