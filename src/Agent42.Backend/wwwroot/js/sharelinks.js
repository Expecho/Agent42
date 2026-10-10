/* Agent 42 — read-only share link (ADR 0035). Split out of app.js (ADR 0041). */

import { exportJsonLink, exportMarkdownLink, exportZipLink, shareLinkCloseBtn, shareLinkConvTitle, shareLinkCopyBtn, shareLinkCreateBtn, shareLinkDialog, shareLinkResult, shareLinkRevokeBtn, shareLinkShareBtn, shareLinkUrl, topbarShareBtn } from "./dom.js";
import { streaming } from "./send.js";

let shareLinkConvId = null;

// The RAW conversation title. shareLinkConvTitle.textContent is quote-wrapped for display, so it
// cannot be reused as the share payload's title.
let shareLinkConvTitleText = "";

// Does this browser have the OS share sheet? Evaluated once at import time — the speech.js
// SpeechRecognition idiom, which voice.js and push.js both branch on to hide their control.
// This is NOT an Android test: desktop Chrome and Edge on Windows, Safari on macOS/iOS and
// Firefox on Android all satisfy it, so Copy disappears there too. Accepted, deliberately — a
// pure capability check, no pointer or media test and no UA sniff (ADR 0047).
const canNativeShare = typeof navigator.share === "function";

// What the topbar share button currently points at. Held here rather than imported from
// conversations.js because the callers already hold both values and simply hand them over — the same
// setter-slot seam as send.js's voice/stopSpeech/sendAbort (ADR 0041 §5). Not a cycle workaround:
// cycles between feature modules are explicitly fine here, and several already ship.
let topbarConvId = null;
let topbarConvTitle = "";

// Points the topbar button at one conversation, or disables it with a null convId. Disabled — not
// hidden — whenever there is nothing worth sharing: a fresh empty chat has no content to expose, and
// an ephemeral one (ADR 0034) is never persisted, so a link to it would resolve to nothing.
export function setTopbarShare(convId, title) {
  topbarConvId = convId ?? null;
  topbarConvTitle = title ?? "This conversation";
  topbarShareBtn.disabled = !topbarConvId;
}
// Toggles the dialog between its unshared (Create) and shared (URL + Share-or-Copy/Revoke) states from
// a url (a non-empty string when shared, null/absent when not). Which of Share/Copy is visible is
// settled once at wire time by canNativeShare (ADR 0047), never here.
function renderShareLinkState(url) {
  const shared = typeof url === "string" && url.length > 0;
  shareLinkResult.hidden = !shared;
  shareLinkCreateBtn.hidden = shared;
  if (shared) shareLinkUrl.value = url;
}

// Opens the share dialog for one conversation and loads its current link state. A public link lets
// ANYONE holding it view this conversation read-only WITHOUT signing in (ADR 0035), so nothing is minted
// until the user presses Create — that press is their explicit consent to the exposure (ADR 0013).
export async function openShareLink(convId, title) {
  if (streaming) return;
  if (!convId) return; // no target — the same guard create/revoke already carry, so a null slip can't
                       // render “undefined” and fetch /api/conversations//share
  shareLinkConvId = convId;
  shareLinkConvTitleText = title;
  // The download links (ADR 0091) ride this dialog's gate: it only opens for a persisted, non-empty chat.
  const exportBase = `/api/conversations/${encodeURIComponent(convId)}/export?format=`;
  exportMarkdownLink.href = exportBase + "markdown";
  exportJsonLink.href = exportBase + "json";
  exportZipLink.href = exportBase + "zip";
  shareLinkConvTitle.textContent = `“${title}”`;
  renderShareLinkState(null); // optimistic unshared until the fetch resolves
  shareLinkCreateBtn.disabled = false;
  shareLinkDialog.showModal();
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/share`);
    if (res.ok) renderShareLinkState((await res.json()).url);
  } catch { /* silo unreachable — leave the unshared state; Create will retry */ }
}

// Hands the link to the OS share sheet. Both rejections it can raise are non-events: AbortError when
// the user dismisses the sheet, and NotAllowedError when there is no transient user activation at call
// time. Activation is a Window-scoped window of a few seconds that share itself consumes, so it can
// simply have expired; WebKit additionally requires the call to sit in the same task as the gesture.
// Either way the link is already on screen, so nothing is surfaced. Title plus url only, no text field
// — a field repeating the URL renders twice in some targets.
async function nativeShareLink() {
  const url = shareLinkUrl.value;
  if (!url) return;
  try {
    await navigator.share({ title: `Agent 42 — ${shareLinkConvTitleText}`, url });
  } catch { /* dismissed, or blocked with no activation — stay silent, the link is in the dialog */ }
}

async function createShareLink() {
  if (!shareLinkConvId) return;
  shareLinkCreateBtn.disabled = true;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(shareLinkConvId)}/share`, { method: "POST" });
    if (!res.ok) return;
    const url = (await res.json()).url;
    // Render BEFORE sharing — the ordering is load-bearing. The sheet can be dismissed or blocked
    // outright, and the await on the POST above can outlive the transient-activation window, so the
    // user must be left holding the link either way.
    renderShareLinkState(url);
    // Auto-open on the same press: one tap from unshared to the OS sheet. The await sits inside this
    // try on purpose — the finally then re-enables Create only once the sheet closes, which is
    // harmless because renderShareLinkState has already hidden that button. Not a bug.
    if (url && canNativeShare) await nativeShareLink();
  } catch { /* silo unreachable — stay unshared */ }
  finally { shareLinkCreateBtn.disabled = false; }
}

async function revokeShareLink() {
  if (!shareLinkConvId) return;
  if (!confirm("Revoke this link? Anyone holding it will lose access.")) return;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(shareLinkConvId)}/share`, { method: "DELETE" });
    if (res.ok || res.status === 404) renderShareLinkState(null);
  } catch { /* silo unreachable — leave it shared */ }
}

async function copyShareLink() {
  try {
    await navigator.clipboard.writeText(shareLinkUrl.value);
    const label = shareLinkCopyBtn.textContent;
    shareLinkCopyBtn.textContent = "Copied";
    setTimeout(() => { shareLinkCopyBtn.textContent = label; }, 1500);
  } catch {
    // Clipboard API unavailable (insecure context / denied) — select the field so the user can copy manually.
    shareLinkUrl.focus();
    shareLinkUrl.select();
  }
}

export function wireShareLinks() {
  shareLinkCreateBtn.addEventListener("click", createShareLink);
  shareLinkShareBtn.addEventListener("click", nativeShareLink);
  shareLinkCopyBtn.addEventListener("click", copyShareLink);
  shareLinkRevokeBtn.addEventListener("click", revokeShareLink);
  shareLinkCloseBtn.addEventListener("click", () => shareLinkDialog.close());
  topbarShareBtn.addEventListener("click", () => openShareLink(topbarConvId, topbarConvTitle));
  // Share REPLACES Copy where the sheet exists — a swap, not an add. Both ship in the markup and
  // Share starts hidden, so an unsupported browser and the pre-JS paint both show Copy.
  shareLinkShareBtn.hidden = !canNativeShare;
  shareLinkCopyBtn.hidden = canNativeShare;
}
