/* Agent 42 — remote MCP servers (ADR 0024/0025/0026), incl. the connection test (ADR 0064). Split out of app.js (ADR 0041). */

import { addMcpServerBtn, mcpAuthSelect, mcpAuthorityField, mcpAuthorityInput, mcpBridgeCommand, mcpBridgeKeyCopyBtn, mcpBridgeKeyReveal, mcpBridgeKeyValue, mcpBridgeRegenerateBtn, mcpBridgeSection, mcpCancelBtn, mcpClientIdField, mcpClientIdInput, mcpClientSecretField, mcpClientSecretInput, mcpConnectBtn, mcpDialog, mcpEnabledInput, mcpEndpointField, mcpEndpointInput, mcpError, mcpForm, mcpFormTitle, mcpHeaderNameField, mcpHeaderNameInput, mcpHeaderValueField, mcpHeaderValueInput, mcpNameInput, mcpOAuthNote, mcpSeedsUrlsInput, mcpScopesField, mcpScopesInput, mcpServerList, mcpTabResult, mcpTestBtn, mcpTestResult, profileTabs } from "./dom.js";
import { enterPrefsDetail, openProfileDialog } from "./profile.js";
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

// The one source of the interactive-OAuth start URL (ADR 0081): used by the settings flow below (a full
// navigation) and by the transcript's Reconnect button (thread.js, a NEW tab, so the open chat survives).
export function mcpOAuthStartUrl(serverId) {
  return `/api/mcp/oauth/start?serverId=${encodeURIComponent(serverId)}`;
}

// The one place the settings sign-in starts, shared by the list row and the dialog button. A full navigation:
// the server 302s to the identity provider and returns to /api/mcp/oauth/callback.
function startMcpOAuth(serverId) {
  window.location.href = mcpOAuthStartUrl(serverId);
}

// The masked record for a stored id, or null. Read AFTER loadMcpServers() for a fresh hasToken.
function storedMcpServer(id) {
  return mcpServers.find((s) => s.id === id) ?? null;
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
    // For OAuth, a connection state (🔗 connected / ⚠ reconnect needed / not connected) instead of the key glyph.
    // "reconnect needed" (ADR 0081): the authorization server rejected the stored grant, so a sign-in is due.
    const isOAuth = server.auth === "InteractiveOAuth";
    // A local-bridge server has no endpoint (ADR 0076): name where it lives instead of an empty first bit.
    const isBridge = server.auth === "LocalBridge";
    const bits = isBridge ? ["local bridge"] : [server.endpoint, server.auth];
    if (server.hasHeaderValue || server.hasClientSecret || server.hasBridgeKey) bits.push("🔑");
    if (isOAuth) bits.push(!server.hasToken ? "not connected" : server.needsReauth ? "⚠ reconnect needed" : "🔗 connected");
    if (!server.enabled) bits.push("⏸ off");
    sub.textContent = bits.join(" · ");
    sub.title = isBridge ? "reached through the Agent42 local bridge on your machine" : server.endpoint;
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
      connect.addEventListener("click", () => startMcpOAuth(server.id));
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
// None → nothing. LocalBridge (ADR 0076) → no endpoint at all, and the bridge-key section instead.
function syncMcpAuthUi() {
  const isHeader = mcpAuthSelect.value === "Header";
  const isCc = mcpAuthSelect.value === "ClientCredentials";
  const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
  const isBridge = mcpAuthSelect.value === "LocalBridge";
  mcpEndpointField.hidden = isBridge;
  // A hidden `required` input still blocks form submission — the browser just cannot show the bubble.
  mcpEndpointInput.required = !isBridge;
  mcpBridgeSection.hidden = !isBridge;
  // Regenerate needs a STORED bridge server; a new one gets its first key from the save itself.
  mcpBridgeRegenerateBtn.hidden = !(isBridge && storedMcpServer(editingMcpId)?.auth === "LocalBridge");
  mcpHeaderNameField.hidden = !isHeader;
  mcpHeaderValueField.hidden = !isHeader;
  mcpAuthorityField.hidden = !isCc;
  mcpClientSecretField.hidden = !isCc;
  mcpClientIdField.hidden = !(isCc || isOAuth);
  mcpScopesField.hidden = !(isCc || isOAuth);
  mcpOAuthNote.hidden = !isOAuth;
  syncMcpConnectBtn();
}

function openMcpDialog(server) {
  editingMcpId = server?.id ?? null;
  mcpFormTitle.textContent = server ? "Edit MCP server" : "New MCP server";
  mcpError.hidden = true;
  // Clear BOTH result lines, or server A's verdict renders under server B's form. The tab line is cleared
  // here too because the dialog is the other way into it.
  clearTestResult(mcpTestResult);
  clearTestResult(mcpTabResult);
  syncMcpTestBtn();
  mcpNameInput.value = server?.name ?? "";
  mcpEndpointInput.value = server?.endpoint ?? "";
  mcpEnabledInput.checked = server?.enabled ?? true;
  // Defaults OFF for a NEW server too, not just an unmigrated one — the opt-in is the whole control.
  mcpSeedsUrlsInput.checked = server?.seedsFetchableUrls ?? false;
  mcpAuthSelect.value = server?.auth ?? "None";
  mcpHeaderNameInput.value = server?.headerName ?? "Authorization";
  mcpHeaderValueInput.value = ""; // never prefilled — write-only (blank = keep on edit)
  mcpAuthorityInput.value = server?.authority ?? "";
  mcpClientIdInput.value = server?.clientId ?? "";
  mcpClientSecretInput.value = ""; // never prefilled — write-only (blank = keep on edit)
  mcpScopesInput.value = (server?.scopes ?? []).join(" ");
  hideBridgeKey(); // a key is shown once, for the server it was minted for — never carried into another form
  syncMcpAuthUi();
  mcpCancelBtn.textContent = "Cancel"; // a previous save may have relabelled it to Close

  mcpDialog.showModal();
  mcpNameInput.focus();
}

// Connect round-trips through the identity provider and back into this user's overlay, so like Test it
// needs a STORED id. Labelled from the CURRENT stored token, so an already-connected server offers a
// Reconnect rather than implying it is unconnected.
//
// It navigates away, so any unsaved edits in the form are dropped — the same trade the list row’s 🔗 makes,
// and the reason the button sits beside Save rather than replacing it.
function syncMcpConnectBtn() {
  const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
  mcpConnectBtn.hidden = !(isOAuth && editingMcpId);
  mcpConnectBtn.textContent = storedMcpServer(editingMcpId)?.hasToken ? "Reconnect" : "Connect";
}

// An unsaved server has no id, and the endpoint takes a STORED id (a blank secret means "keep", which
// cannot be resolved from an unsaved form) — so Test is only available once the server exists.
function syncMcpTestBtn() {
  mcpTestBtn.disabled = !editingMcpId;
  mcpTestBtn.title = editingMcpId ? "Connect to this server and list its tools" : "Save the server first";
}

// The tab's result line survives the dialog, so Preferences must clear it on open — otherwise an OAuth-return
// verdict lingers across a close/reopen, and outlives the very server it describes if that server is deleted.
// Called synchronously from openProfileDialog, which is why it cannot race the un-awaited loadMcpServers().
export function clearMcpTabResult() {
  clearTestResult(mcpTabResult);
}

function clearTestResult(target) {
  target.hidden = true;
  target.textContent = "";
  target.classList.remove("agent-form__note--ok", "agent-form__note--error");
}

// ONE renderer, two targets (the dialog line and the MCP tab line): the OAuth return has no dialog open, so
// the tab needs its own. Deliberately NOT solved by opening the edit dialog on the OAuth return — landing a
// user in an edit form after signing in is a non-sequitur.
function renderTestResult(target, outcome) {
  target.hidden = false;
  target.classList.toggle("agent-form__note--ok", outcome.ok);
  target.classList.toggle("agent-form__note--error", !outcome.ok);
  if (!outcome.ok) {
    target.textContent = `✗ ${outcome.reason}`;
    return;
  }
  const names = (outcome.toolNames ?? []).join(", ");
  target.textContent = outcome.toolCount > 0
    ? `✓ ${outcome.reason} — ${outcome.toolCount} tool${outcome.toolCount === 1 ? "" : "s"}: ${names}`
    : `✓ ${outcome.reason}`;
}

// Tests one STORED server and renders the verdict into `target`. A reachable-but-failing test is a 200 with
// ok:false — the request succeeded and the diagnosis IS the payload — so only a non-ok response is a
// transport/plumbing problem.
async function testMcpServer(id, target) {
  target.hidden = false;
  target.classList.remove("agent-form__note--ok", "agent-form__note--error");
  target.textContent = "testing…";
  let outcome;
  try {
    const res = await fetch(`/api/mcp-servers/${encodeURIComponent(id)}/test`, { method: "POST" });
    if (!res.ok) {
      renderTestResult(target, { ok: false, reason: (await res.text()) || "Could not test this server." });
      return;
    }
    outcome = await res.json();
  } catch {
    renderTestResult(target, { ok: false, reason: "Could not reach Agent 42 to run the test." });
    return;
  }
  renderTestResult(target, outcome);
  // A successful test RECORDS the discovered names, so the per-tool checkboxes appear without waiting for a
  // chat activation (ADR 0058's cold start). No-op for a DISABLED server: GET /api/tools emits a descriptor
  // only per management-enabled server.
  if (outcome.ok) await refreshToolCatalog();
}

// The raw bridge key exists in plaintext exactly once — in the response to the write that minted it — so it
// is rendered here and nowhere else, with the command that uses it (ADR 0076).
function showBridgeKey(key) {
  mcpBridgeKeyValue.textContent = key;
  // The released self-contained executable (release-local-bridge.yml). "./" works in macOS/Linux shells and
  // in PowerShell (which resolves the .exe); cmd.exe would need "Agent42.LocalBridge.exe" instead.
  mcpBridgeCommand.textContent = `./Agent42.LocalBridge --server ${location.origin}`;
  mcpBridgeKeyCopyBtn.textContent = "Copy";
  mcpBridgeKeyReveal.hidden = false;
}

function hideBridgeKey() {
  mcpBridgeKeyReveal.hidden = true;
  mcpBridgeKeyValue.textContent = "";
}

// Rotates the key without a Save (a Save would also re-run the auto-test). The server closes the socket the
// old key opened, so a running helper reconnects, is refused, and stops.
async function regenerateBridgeKey() {
  if (!editingMcpId) return;
  if (!confirm("Regenerate this server's bridge key? The current key stops working and a running local bridge disconnects.")) return;
  try {
    const res = await fetch(`/api/mcp-servers/${encodeURIComponent(editingMcpId)}/bridge-key`, { method: "POST" });
    if (!res.ok) {
      mcpError.textContent = (await res.text()) || "Could not regenerate the bridge key.";
      mcpError.hidden = false;
      return;
    }
    const saved = await res.json();
    if (saved.bridgeKey) showBridgeKey(saved.bridgeKey);
    clearTestResult(mcpTestResult);
  } catch { /* silo may be unreachable; the old key is unchanged */ }
}

async function saveMcpServer() {
  const isHeader = mcpAuthSelect.value === "Header";
  const isCc = mcpAuthSelect.value === "ClientCredentials";
  const isOAuth = mcpAuthSelect.value === "InteractiveOAuth";
  const isBridge = mcpAuthSelect.value === "LocalBridge";
  const payload = {
    name: mcpNameInput.value.trim(),
    // A local-bridge server has no endpoint; the server stores "" and validation exempts only that mode.
    endpoint: isBridge ? "" : mcpEndpointInput.value.trim(),
    auth: mcpAuthSelect.value,
    enabled: mcpEnabledInput.checked,
    // A plain setting: unlike the write-only secrets, false really means off, so unticking turns seeding
    // back off (the grain keeps no previous value for it — deliberately, see ADR 0067).
    seedsFetchableUrls: mcpSeedsUrlsInput.checked,
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
  if (!payload.name || (!isBridge && !payload.endpoint)) return;

  const url = editingMcpId
    ? `/api/mcp-servers/${encodeURIComponent(editingMcpId)}`
    : "/api/mcp-servers";
  const method = editingMcpId ? "PUT" : "POST";
  let mintedBridgeKey = false;
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
    const saved = await res.json();
    // POST ALWAYS INSERTS (the id is server-minted), and this dialog now STAYS OPEN after a save — so it
    // must flip into edit mode on the id we just got back. Miss this and a second Save silently creates a
    // duplicate server. There is no server-side guard; this line is the whole defence.
    editingMcpId = saved.id ?? editingMcpId;
    syncMcpTestBtn();
    syncMcpConnectBtn();
    // Present only on the save that MINTED a bridge key (create, or a switch to Local bridge).
    if (saved.bridgeKey) {
      showBridgeKey(saved.bridgeKey);
      mintedBridgeKey = true;
    }
  } catch { return; /* silo may be unreachable; leave the dialog open */ }

  // The dialog stays open so the auto-test below has somewhere to report. Cancel becomes Close: the save
  // already happened, so "Cancel" would misdescribe what the button does.
  mcpCancelBtn.textContent = "Close";
  await loadMcpServers();
  syncMcpAuthUi(); // the server is stored now, so a Local bridge one can offer Regenerate
  // UNCONDITIONAL, and it must stay that way (ADR 0026): a save whose auto-test then FAILS must still update
  // the pickers, because the server's own checkbox is driven by the save, not by reachability. Do not move
  // this inside the test's success branch.
  await refreshToolCatalog(); // the added/edited server must (dis)appear in the tool pickers (ADR 0026)
  // Tell the user straight away whether what they just saved actually works (ADR 0064) — the point of the
  // feature is that a typo'd host stops saving cleanly and silently.
  //
  // EXCEPT for a disabled server, which is deliberately NOT dialled here. The chat path connects only to
  // enabled servers (UserMcpToolProvider's `Where(s => s.Enabled)`), so auto-dialling one on save would send
  // its decrypted credentials somewhere they have never been — new egress, from a save rather than from an
  // explicit press. Pressing Test still dials it: that is a deliberate user action, and diagnosing a switched
  // off server is exactly what the button is for.
  if (!editingMcpId) return;
  if (!mcpEnabledInput.checked) {
    renderTestResult(mcpTestResult, { ok: false, reason: "not tested — this server is switched off. Press Test to check it anyway." });
    return;
  }
  // ...and EXCEPT for an interactive-OAuth server that has never signed in. It cannot hold a token at this
  // point — Save does not perform the sign-in — so the dial could only ever return "not connected (no OAuth
  // token yet)": an accurate diagnosis rendered as a failure, at the one moment it is guaranteed AND the
  // user cannot act on it (this dialog is modal, so the list’s Connect glyph is inert underneath). Report
  // the save as the success it is and point at the Connect button instead. hasToken comes from the
  // loadMcpServers() above, so it is fresh; a RECONNECT of an already-connected server still auto-tests.
  if (mcpAuthSelect.value === "InteractiveOAuth" && !storedMcpServer(editingMcpId)?.hasToken) {
    renderTestResult(mcpTestResult, { ok: true, toolCount: 0, reason: "saved. Press Connect to sign in — this server is not usable until you do." });
    return;
  }
  // ...and EXCEPT on the save that minted a local-bridge key (ADR 0076), for the OAuth reason above: the key
  // was created a moment ago, so no helper can be connected with it yet — the dial could only say "local
  // bridge not connected". A later save of the same server auto-tests as usual.
  if (mintedBridgeKey) {
    renderTestResult(mcpTestResult, { ok: true, toolCount: 0, reason: "saved. Copy the key, start the local bridge on your computer, then press Test." });
    return;
  }
  await testMcpServer(editingMcpId, mcpTestResult);
}

async function deleteMcpServer(id) {
  try {
    const res = await fetch(`/api/mcp-servers/${encodeURIComponent(id)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; }
  mcpServers = mcpServers.filter((s) => s.id !== id);
  clearMcpTabResult(); // a verdict about a server that no longer exists is worse than none
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
  // ...and on a phone, drill INTO it: the rail is the list view, so select(3) alone leaves the user
  // looking at the section list with MCP merely marked active behind it. Above 760px this is inert.
  enterPrefsDetail(3);

  // "Connected" should mean "connected AND reachable", so auto-test the server the callback named (ADR
  // 0064). Into the TAB's result line — there is no dialog open on the OAuth return.
  const serverId = params.get("serverId");
  if (result === "connected" && serverId) await testMcpServer(serverId, mcpTabResult);
}

export function wireMcp() {
  addMcpServerBtn.addEventListener("click", () => openMcpDialog(null));
  mcpAuthSelect.addEventListener("change", syncMcpAuthUi);
  mcpCancelBtn.addEventListener("click", () => mcpDialog.close());
  mcpTestBtn.addEventListener("click", () => {
    if (editingMcpId) testMcpServer(editingMcpId, mcpTestResult);
  });
  mcpConnectBtn.addEventListener("click", () => {
    if (editingMcpId) startMcpOAuth(editingMcpId);
  });
  mcpBridgeRegenerateBtn.addEventListener("click", regenerateBridgeKey);
  mcpBridgeKeyCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(mcpBridgeKeyValue.textContent);
      mcpBridgeKeyCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked (insecure context / permission) — the key is visible to select manually */
    }
  });
  mcpForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveMcpServer();
  });
}
