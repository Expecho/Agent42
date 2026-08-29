/* Agent 42 — selectable tools (ADR 0021/0026). Split out of app.js (ADR 0041). */

import { activeId } from "./conversations.js";
import { chatToolChecks, profileToolChecks, toolsBtn, toolsDialog, toolsDoneBtn } from "./dom.js";

// The selectable-tools catalog (ADR 0021), fetched once from /api/tools:
// [{key,label,requiresHostedTools,parentKey}]. parentKey is set only on nested rows (per-MCP-tool).
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

// The per-tool keys a container's LAST render had no checkbox for, keyed BY CONTAINER. readToolChecks adds
// them back, so a Save can never drop a selection the user was not shown (see its comment).
//
// A WeakMap, not a module-level slot: renderToolChecks/readToolChecks serve THREE containers
// (profileToolChecks, agentToolChecks, chatToolChecks) plus a round-trip in refreshToolCatalog. A single
// shared slot would be correct only under an unstated "every read follows that container's own render"
// invariant across three dialogs, and would fail silently the day that ordering changes — while a
// source-text assertion still passed. Weak so a detached container is collectable.
const unrenderedKeys = new WeakMap();

// Renders one checkbox per catalog tool into `container`, checked when its key is in `selectedKeys`.
// Two prefixed groups render under their own subheadings beneath the built-in tools: per-skill keys
// (`skill:{name}`, per-skill selectable tools) and MCP keys (ADR 0026, extended to per-tool selection).
//
// MCP now renders NESTED: one master row per server (`mcp:{id}`) with a disclosure button, and that
// server's tools (`mcp:{id}:{toolName}`) indented beneath it, collapsed by default. The partition is on
// `parentKey` — a server row has none, a tool row carries its server's key — and NOT on the key string,
// because `startsWith("mcp:")` matches both and cannot tell them apart.
//
// A stored/effective selection may carry EITHER bare wildcard — `skills` or `mcp` — because a null==all
// default reached the client, or because it is legacy state. Neither has a checkbox, so each is treated as
// "every box in that group checked", so a save re-emits explicit per-entry keys instead of silently dropping
// the wildcard and disabling the whole group. That is the ADR 0026 hazard. `cb.checked` is now a FOUR-way
// OR and every term is load-bearing; none may be removed.
export function renderToolChecks(container, selectedKeys) {
  const selected = new Set(selectedKeys);
  const mcpWildcard = selected.has("mcp");
  const skillsWildcard = selected.has("skills");
  container.innerHTML = "";
  // Built-ins must exclude BOTH prefixes, or the prefixed groups would render twice.
  const builtins = toolCatalog.filter((t) => !t.key.startsWith("mcp:") && !t.key.startsWith("skill:"));
  const skills = toolCatalog.filter((t) => t.key.startsWith("skill:"));
  // Partitioned on parentKey, never on the key prefix alone: a server row and its tool rows both start
  // with "mcp:", so the old filter would sweep the tools in beside their own parents.
  const mcpServers = toolCatalog.filter((t) => t.key.startsWith("mcp:") && !t.parentKey);
  const mcpToolsFor = (serverKey) => toolCatalog.filter((t) => t.parentKey === serverKey);

  // The fourth term: a server's own key grants every one of its tools (ADR 0026 semantics, unchanged), so a
  // tool row is checked when its PARENT key is selected. That is what makes a legacy `mcp:{id}` selection
  // render as "all of this server's tools ticked" rather than as an empty, about-to-be-destroyed group.
  const isChecked = (tool) => selected.has(tool.key)
    || (mcpWildcard && tool.key.startsWith("mcp:"))
    || (skillsWildcard && tool.key.startsWith("skill:"))
    || (!!tool.parentKey && selected.has(tool.parentKey));

  const addCheck = (tool, parent = container) => {
    const label = document.createElement("label");
    label.className = "tool-check";
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.value = tool.key;
    cb.checked = isChecked(tool);
    const span = document.createElement("span");
    span.textContent = tool.requiresHostedTools ? `${tool.label} (Responses)` : tool.label;
    label.append(cb, span);
    parent.appendChild(label);
    return cb;
  };

  const addHeading = (heading) => {
    const el = document.createElement("div");
    el.className = "tool-check__heading";
    el.textContent = heading;
    container.appendChild(el);
  };

  const addGroup = (heading, group) => {
    if (group.length === 0) return;
    addHeading(heading);
    for (const tool of group) addCheck(tool);
  };

  // One server: a master checkbox, a disclosure button, and an indented collapsed list of its tools.
  const addServer = (server) => {
    const tools = mcpToolsFor(server.key);
    const row = document.createElement("div");
    row.className = "tool-server";
    container.appendChild(row);

    const head = document.createElement("div");
    head.className = "tool-server__head";
    row.appendChild(head);
    const master = addCheck(server, head);

    // A server whose tools have never been discovered renders as an ordinary checkbox with no disclosure —
    // the cold-start window, which lasts until this user's next chat activation connects to it. Nothing is
    // lost meanwhile: `mcp:{id}` gates every one of its tools exactly as it always did.
    if (tools.length === 0) {
      const hint = document.createElement("span");
      hint.className = "tool-server__hint";
      hint.textContent = "tools not discovered yet";
      head.appendChild(hint);
      return;
    }

    // A BUTTON, never an <input type=checkbox>: readToolChecks collects EVERY checkbox in the container, so
    // a checkbox disclosure would be swept into the saved keys as a phantom selection.
    const toggle = document.createElement("button");
    toggle.type = "button";
    toggle.className = "tool-server__toggle";
    toggle.setAttribute("aria-expanded", "false");
    toggle.textContent = `▸ ${tools.length}`;
    head.appendChild(toggle);

    const children = document.createElement("div");
    children.className = "tool-server__tools";
    children.hidden = true;
    row.appendChild(children);
    toggle.addEventListener("click", () => {
      children.hidden = !children.hidden;
      toggle.setAttribute("aria-expanded", String(!children.hidden));
      toggle.textContent = `${children.hidden ? "▸" : "▾"} ${tools.length}`;
    });

    const boxes = tools.map((tool) => addCheck(tool, children));

    // Master and children sync BOTH ways. The child-to-master half is not cosmetic: without it, ticking
    // every tool by hand never re-checks the master, so readToolChecks' collapse rule never fires and the
    // selection saves as N per-tool keys instead of one `mcp:{id}`. That still works today, but it freezes
    // the selection against tools the server adds LATER — the server key would have granted them, an
    // explicit list will not.
    master.addEventListener("change", () => {
      for (const box of boxes) box.checked = master.checked;
    });
    for (const box of boxes) {
      box.addEventListener("change", () => {
        master.checked = boxes.every((b) => b.checked);
      });
    }
  };

  for (const tool of builtins) addCheck(tool);
  addGroup("Skills", skills);
  if (mcpServers.length > 0) {
    addHeading("MCP servers");
    for (const server of mcpServers) addServer(server);
  }

  // Remember the per-tool keys this render had no checkbox for, so the next read carries them forward.
  // Only `mcp:` keys: a server whose tools are unknown renders zero children, and a stored partial per-tool
  // selection for it would otherwise be silently destroyed by the next Save. Recomputed on every render, so
  // it can never go stale.
  const rendered = new Set(toolCatalog.map((t) => t.key));
  unrenderedKeys.set(container, selectedKeys.filter((k) => k.startsWith("mcp:") && !rendered.has(k)));
}

// The keys to save for `container`. Two rules beyond "whatever is checked":
//
//   COLLAPSE — a server whose master is checked saves as the bare `mcp:{id}`, and its per-tool keys are
//   dropped. That keeps ADR 0026's meaning ("all of this server's tools", including ones it grows later)
//   instead of freezing today's list. A PARTIALLY ticked server saves its per-tool keys and NOT the server
//   key, which would re-grant everything the user just deselected.
//
//   PRESERVE — per-tool keys belonging to servers whose children were not rendered are carried forward
//   untouched. The picker cannot show a checkbox for a tool it does not know about, and a Save must never
//   delete a selection the user was never given the chance to see. This is the client-side backstop for the
//   one residual path the server-side rule cannot cover: a server that legitimately drops to ZERO tools
//   after having had some. Be honest about its status — it has no runtime test in this repo (there is no JS
//   runner, by design), so a source-text assertion is the only guard.
export function readToolChecks(container) {
  const checked = [...container.querySelectorAll("input[type=checkbox]")].filter((c) => c.checked);
  const collapsedServers = [];
  const keys = [];

  for (const box of checked) {
    // A master is the checkbox inside a server row's HEAD; that server's tools live in a sibling element,
    // so this distinguishes the two without parsing the key.
    if (box.closest(".tool-server__head") !== null) collapsedServers.push(box.value);
    keys.push(box.value);
  }

  // The preserved keys go through the SAME collapse rule. Normally they belong to a server whose master is
  // unchecked (that is the case they exist for), but if the user ticks that master to grant the whole server,
  // its stale per-tool keys would otherwise ride along forever, redundant with the key that subsumes them.
  const preserved = unrenderedKeys.get(container) ?? [];
  const collapse = (list) => list.filter((k) => !collapsedServers.some((s) => k.startsWith(`${s}:`)));
  return [...collapse(keys), ...collapse(preserved)];
}

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
