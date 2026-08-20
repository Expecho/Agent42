/* Agent 42 — remote MCP servers (ADR 0024/0025/0026). Split out of app.js (ADR 0041). */

import { addMcpServerBtn, mcpAuthSelect, mcpAuthorityField, mcpAuthorityInput, mcpCancelBtn, mcpClientIdField, mcpClientIdInput, mcpClientSecretField, mcpClientSecretInput, mcpDialog, mcpEnabledInput, mcpEndpointInput, mcpError, mcpForm, mcpFormTitle, mcpHeaderNameField, mcpHeaderNameInput, mcpHeaderValueField, mcpHeaderValueInput, mcpNameInput, mcpOAuthNote, mcpScopesField, mcpScopesInput, mcpServerList, profileTabs } from "./dom.js";
import { openProfileDialog } from "./profile.js";
import { refreshToolCatalog } from "./tools.js";

// The user's own remote MCP servers (config-driven remote MCP servers), fetched masked (hasHeaderValue,
// never the secret). The id of the one being edited, or null when adding a new server.
let mcpServers = [];
let editingMcpId = null;
export async function loadMcpServers() {
  try {
    const res = await fetch("/api/mcp-servers");
    if (!res.ok) return;
    const data = await res.json();
    mcpServers = data.servers ?? [];
    renderMcpServers();
  } catch { /* silo may be unreachable; the list refreshes on next open */ }
}

function renderMcpServers() {
  mcpServerList.innerHTML = "";
  if (mcpServers.length === 0) {
    const li = document.createElement("li");
    li.className = "providers__empty";
    li.textContent = "no MCP servers added";
    mcpServerList.appendChild(li);
    return;
  }
  for (const server of mcpServers) {
    const li = document.createElement("li");
    li.className = "provider-row";

    const meta = document.createElement("div");
    meta.className = "provider-row__meta";
    const name = document.createElement("span");
    name.className = "provider-row__name";
    name.textContent = server.name;
    const sub = document.createElement("span");
    sub.className = "provider-row__sub";
    // Endpoint + auth + a key glyph when a secret is stored (never the secret) + a pause glyph when off.
    // For OAuth, a connection state (🔗 connected / not connected) instead of the key glyph.
    const isOAuth = server.auth === "InteractiveOAuth";
    const bits = [server.endpoint, server.auth];
    if (server.hasHeaderValue || server.hasClientSecret) bits.push("🔑");
    if (isOAuth) bits.push(server.hasToken ? "🔗 connected" : "not connected");
    if (!server.enabled) bits.push("⏸ off");
    sub.textContent = bits.join(" · ");
    sub.title = server.endpoint;
    meta.append(name, sub);

    // Interactive-OAuth servers get a Connect (or Reconnect) action that starts the sign-in flow. Full
    // navigation to /api/mcp/oauth/start — the server 302s to the identity provider and returns to /callback.
    if (isOAuth) {
      const connect = document.createElement("button");
      connect.type = "button";
      connect.className = "provider-row__action";
      connect.setAttribute("aria-label", `${server.hasToken ? "Reconnect" : "Connect"} MCP server: ${server.name}`);
      connect.textContent = "🔗";
      connect.title = server.hasToken ? "Reconnect (sign in again)" : "Connect (sign in)";
      connect.addEventListener("click", () => {
        window.location.href = `/api/mcp/oauth/start?serverId=${encodeURIComponent(server.id)}`;
      });
      li.append(meta, connect);
    } else {
      li.appendChild(meta);
    }

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "provider-row__action";
    edit.setAttribute("aria-label", `Edit MCP server: ${server.name}`);
    edit.textContent = "✎";
    edit.addEventListener("click", () => openMcpDialog(server));

    const del = document.createElement("button");
    del.type = "button";
    del.className = "provider-row__action";
    del.setAttribute("aria-label", `Delete MCP server: ${server.name}`);
    del.textContent = "🗑";
    del.addEventListener("click", () => deleteMcpServer(server.id));

    li.append(edit, del);
    mcpServerList.appendChild(li);
  }
}

// Each auth mode reveals only the fields it needs: Header → header name/value; ClientCredentials →
// authority/clientId/clientSecret/scopes; InteractiveOAuth → clientId (optional) + scopes, plus a note;
// None → nothing.
function syncMcpAuthUi() {
  const isHeader = mcpAuthSelect.value === "Header";
  const isCc = mcpAuthSelect.value === "ClientCredentials";
  const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
  mcpHeaderNameField.hidden = !isHeader;
  mcpHeaderValueField.hidden = !isHeader;
  mcpAuthorityField.hidden = !isCc;
  mcpClientSecretField.hidden = !isCc;
  mcpClientIdField.hidden = !(isCc || isOAuth);
  mcpScopesField.hidden = !(isCc || isOAuth);
  mcpOAuthNote.hidden = !isOAuth;
}

function openMcpDialog(server) {
  editingMcpId = server?.id ?? null;
  mcpFormTitle.textContent = server ? "Edit MCP server" : "New MCP server";
  mcpError.hidden = true;
  mcpNameInput.value = server?.name ?? "";
  mcpEndpointInput.value = server?.endpoint ?? "";
  mcpEnabledInput.checked = server?.enabled ?? true;
  mcpAuthSelect.value = server?.auth ?? "None";
  mcpHeaderNameInput.value = server?.headerName ?? "Authorization";
  mcpHeaderValueInput.value = ""; // never prefilled — write-only (blank = keep on edit)
  mcpAuthorityInput.value = server?.authority ?? "";
  mcpClientIdInput.value = server?.clientId ?? "";
  mcpClientSecretInput.value = ""; // never prefilled — write-only (blank = keep on edit)
  mcpScopesInput.value = (server?.scopes ?? []).join(" ");
  syncMcpAuthUi();

  mcpDialog.showModal();
  mcpNameInput.focus();
}

async function saveMcpServer() {
  const isHeader = mcpAuthSelect.value === "Header";
  const isCc = mcpAuthSelect.value === "ClientCredentials";
  const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
  const payload = {
    name: mcpNameInput.value.trim(),
    endpoint: mcpEndpointInput.value.trim(),
    auth: mcpAuthSelect.value,
    enabled: mcpEnabledInput.checked,
    headerName: isHeader ? mcpHeaderNameInput.value.trim() : null,
    // Blank means "keep the stored value" on edit, or "no value" on create — the server maps both.
    headerValue: isHeader ? mcpHeaderValueInput.value : null,
    authority: isCc ? mcpAuthorityInput.value.trim() : null,
    // clientId is shared by ClientCredentials and InteractiveOAuth (optional for OAuth — dynamic otherwise).
    clientId: (isCc || isOAuth) ? mcpClientIdInput.value.trim() : null,
    clientSecret: isCc ? mcpClientSecretInput.value : null, // write-only, blank = keep
    // Scopes are space- or comma-separated in the UI; the API stores a list.
    scopes: (isCc || isOAuth) ? mcpScopesInput.value.split(/[\s,]+/).filter(Boolean) : null,
  };
  if (!payload.name || !payload.endpoint) return;

  const url = editingMcpId
    ? `/api/mcp-servers/${encodeURIComponent(editingMcpId)}`
    : "/api/mcp-servers";
  const method = editingMcpId ? "PUT" : "POST";
  try {
    const res = await fetch(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) {
      // The endpoint returns a plain-text reason for a 400 (bad endpoint/auth) — surface it.
      mcpError.textContent = (await res.text()) || "Could not save the MCP server.";
      mcpError.hidden = false;
      return;
    }
  } catch { return; /* silo may be unreachable; leave the dialog open */ }

  mcpDialog.close();
  await loadMcpServers();
  await refreshToolCatalog(); // the added/edited server must (dis)appear in the tool pickers (ADR 0026)
}

async function deleteMcpServer(id) {
  try {
    const res = await fetch(`/api/mcp-servers/${encodeURIComponent(id)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; }
  mcpServers = mcpServers.filter((s) => s.id !== id);
  renderMcpServers();
  await refreshToolCatalog(); // the deleted server must drop out of the tool pickers (ADR 0026)
}
// Run at init: the interactive-OAuth callback redirects back to /?mcp_oauth=connected|error. Strip the
// param (so a reload isn't a re-trigger) and open Preferences → MCP servers so the connection status shows.
export async function initMcpOAuthReturn() {
  const params = new URLSearchParams(location.search);
  const result = params.get("mcp_oauth");
  if (!result) return;
  history.replaceState(null, "", location.pathname);
  if (result === "error") {
    alert(`Could not connect the MCP server: ${params.get("reason") || "unknown error"}`);
  }
  await openProfileDialog();
  profileTabs.select(3); // the MCP servers tab
}

export function wireMcp() {
  addMcpServerBtn.addEventListener("click", () => openMcpDialog(null));
  mcpAuthSelect.addEventListener("change", syncMcpAuthUi);
  mcpCancelBtn.addEventListener("click", () => mcpDialog.close());
  mcpForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveMcpServer();
  });
}
