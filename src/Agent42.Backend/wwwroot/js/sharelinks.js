/* Agent 42 — read-only share link (ADR 0035). Split out of app.js (ADR 0041). */

import { shareLinkCloseBtn, shareLinkConvTitle, shareLinkCopyBtn, shareLinkCreateBtn, shareLinkDialog, shareLinkResult, shareLinkRevokeBtn, shareLinkUrl, topbarShareBtn } from "./dom.js";
import { streaming } from "./send.js";

let shareLinkConvId = null;

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
// Toggles the dialog between its unshared (Create) and shared (URL + Copy/Revoke) states from a url
// (a non-empty string when shared, null/absent when not).
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
  shareLinkConvTitle.textContent = `“${title}”`;
  renderShareLinkState(null); // optimistic unshared until the fetch resolves
  shareLinkCreateBtn.disabled = false;
  shareLinkDialog.showModal();
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/share`);
    if (res.ok) renderShareLinkState((await res.json()).url);
  } catch { /* silo unreachable — leave the unshared state; Create will retry */ }
}

async function createShareLink() {
  if (!shareLinkConvId) return;
  shareLinkCreateBtn.disabled = true;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(shareLinkConvId)}/share`, { method: "POST" });
    if (res.ok) renderShareLinkState((await res.json()).url);
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
  shareLinkCopyBtn.addEventListener("click", copyShareLink);
  shareLinkRevokeBtn.addEventListener("click", revokeShareLink);
  shareLinkCloseBtn.addEventListener("click", () => shareLinkDialog.close());
  topbarShareBtn.addEventListener("click", () => openShareLink(topbarConvId, topbarConvTitle));
}
