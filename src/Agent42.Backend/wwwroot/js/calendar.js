/* Agent 42 — the Google Calendar connection (ADR 0053). Preferences → Calendar: one per-user OAuth
   connection that the six calendar tools run on.

   Closest sibling is mcp.js's OAuth half, and the differences are all simplifications: there is one
   connection rather than one per server, and no dialog — the whole tab is a status line and a button. */

import {
  googleConnectBtn, googleDisconnectBtn, googleStatus, googleStatusNote, profileTabs,
} from "./dom.js";
import { openProfileDialog } from "./profile.js";

// Why a connect can fail, in words the user can act on. The server sends a CATEGORY, never Google's own
// error text — that text carries the client id and the redirect URI, and it would land in the address
// bar. Anything unrecognised falls through to the generic line rather than being echoed raw.
const CONNECT_ERRORS = {
  denied: "The connection was cancelled, or Google declined it.",
  not_configured: "Google Calendar is not configured on this deployment.",
  missing_code_or_state: "Google's response was incomplete. Try connecting again.",
  unknown_state: "That connect link has already been used. Start again from Preferences → Calendar.",
  no_refresh_token:
    "Google did not return a refresh token, so the connection would have stopped working within the "
    + "hour. Remove Agent 42 at myaccount.google.com/permissions and connect again.",
  exchange_failed: "Google rejected the connection. Check the client id and secret, then try again.",
};

export async function loadGoogleStatus() {
  try {
    const res = await fetch("/api/google/status");
    if (!res.ok) return;
    render(await res.json());
  } catch { /* silo may be unreachable; the tab refreshes on next open */ }
}

function render(status) {
  // Both buttons ship hidden, and at most one is revealed. A deployment with no client credentials
  // offers NEITHER — a Connect button there would open a flow that 409s, which reads as a bug.
  googleConnectBtn.hidden = !status.configured || status.connected;
  googleDisconnectBtn.hidden = !status.connected;

  if (!status.configured) {
    googleStatus.textContent = "not available";
    setNote("This deployment has no Google Calendar client configured, so there is nothing to connect to.");
    return;
  }

  if (!status.connected) {
    googleStatus.textContent = "not connected";
    setNote("");
    return;
  }

  // The account is the primary calendar's id, which for a Google account is the address — which is why
  // the connection asks for no openid/email scope.
  googleStatus.textContent = status.accountEmail
    ? `connected as ${status.accountEmail}`
    : "connected";
  setNote(status.connectedAt ? `Connected ${new Date(status.connectedAt).toLocaleString()}.` : "");
}

function setNote(text, isError = false) {
  googleStatusNote.textContent = text;
  googleStatusNote.hidden = !text;
  googleStatusNote.classList.toggle("agent-form__note--error", isError);
}

// A full navigation, not fetch: the start endpoint 302s to Google's consent screen, and the browser has
// to follow it as a top-level navigation for the user to see and act on it.
function connect() {
  location.assign("/api/google/oauth/start");
}

async function disconnect() {
  if (!confirm("Disconnect Google Calendar? The calendar tools will stop working until you reconnect.")) {
    return;
  }

  try {
    const res = await fetch("/api/google/connection", { method: "DELETE" });
    if (!res.ok) {
      setNote("Could not disconnect. Try again.", true);
      return;
    }

    await loadGoogleStatus();
    // Deliberately said out loud: removing our tokens does NOT withdraw the grant at Google's end.
    setNote("Disconnected. To withdraw the grant at Google too, visit myaccount.google.com/permissions.");
  } catch {
    setNote("Could not disconnect. Try again.", true);
  }
}

// The callback redirects back to /?google_oauth=…, so the app re-opens Preferences on the Calendar tab
// with the outcome already reflected. Mirrors initMcpOAuthReturn, including replacing the history entry
// first so a refresh does not re-trigger this.
export async function initGoogleOAuthReturn() {
  const params = new URLSearchParams(location.search);
  const result = params.get("google_oauth");
  if (!result) return;
  history.replaceState(null, "", location.pathname);

  await openProfileDialog();
  profileTabs.select(6); // the Calendar tab — appended last, so MCP stays 3 (see dom.js)
  await loadGoogleStatus();

  // AFTER the status render, deliberately. openProfileDialog() fires loadGoogleStatus() WITHOUT
  // awaiting it (profile.js), so a response landing later calls render() -> setNote("") and would wipe
  // the only feedback a failed connect gives. Awaiting our own loadGoogleStatus() above settles the
  // in-flight one first; mcp.js sidesteps the same race with an alert(), which is not an option here.
  if (result === "error") {
    setNote(CONNECT_ERRORS[params.get("reason")] ?? "Could not connect Google Calendar.", true);
  }
}

export function wireCalendar() {
  googleConnectBtn.addEventListener("click", connect);
  googleDisconnectBtn.addEventListener("click", disconnect);
}
