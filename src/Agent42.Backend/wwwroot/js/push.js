/* Agent 42 — Web Push notifications (ADR 0028). Split out of app.js (ADR 0041). */

import { selectConversation } from "./conversations.js";
import { pushSection, pushStatus, pushToggleBtn } from "./dom.js";

// Filled from GET /api/push/config on init: whether the server enabled push and the VAPID public key
// the browser needs to subscribe. pushSupported also requires the browser APIs (feature detection).
let pushConfig = null;
const pushSupported =
  "serviceWorker" in navigator && "PushManager" in window && "Notification" in window;

// VAPID public keys are base64url; pushManager.subscribe wants a Uint8Array applicationServerKey.
function urlBase64ToUint8Array(base64) {
  const padding = "=".repeat((4 - (base64.length % 4)) % 4);
  const normalized = (base64 + padding).replace(/-/g, "+").replace(/_/g, "/");
  const raw = atob(normalized);
  return Uint8Array.from(raw, (c) => c.charCodeAt(0));
}

// Reflects the push state in Preferences → General. Safe to call while the dialog is closed. The whole
// section stays hidden unless the server enabled push AND this browser supports it (so unsupported
// browsers, including iOS-in-browser, simply never see the toggle).
export async function updatePushControl() {
  if (!pushSection) return;
  if (!pushConfig || !pushConfig.enabled || !pushSupported) {
    pushSection.hidden = true;
    return;
  }
  pushSection.hidden = false;
  try {
    const reg = await navigator.serviceWorker.ready;
    const sub = await reg.pushManager.getSubscription();
    pushStatus.textContent = sub ? "On" : "Off";
    pushToggleBtn.textContent = sub ? "Disable" : "Enable";
  } catch {
    pushStatus.textContent = "Off";
    pushToggleBtn.textContent = "Enable";
  }
}

// Subscribe (prompting for permission) or unsubscribe this device, then sync with the server. The only
// place a notification permission prompt is triggered — on an explicit tap.
async function togglePush() {
  if (!pushConfig || !pushConfig.enabled || !pushSupported) return;
  pushToggleBtn.disabled = true;
  try {
    const reg = await navigator.serviceWorker.ready;
    const existing = await reg.pushManager.getSubscription();
    if (existing) {
      await fetch("/api/push/subscriptions?endpoint=" + encodeURIComponent(existing.endpoint),
        { method: "DELETE" });
      await existing.unsubscribe();
    } else {
      if (await Notification.requestPermission() !== "granted") return;
      const sub = await reg.pushManager.subscribe({
        userVisibleOnly: true,
        applicationServerKey: urlBase64ToUint8Array(pushConfig.publicKey),
      });
      const keys = sub.toJSON().keys;
      await fetch("/api/push/subscriptions", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ endpoint: sub.endpoint, p256dh: keys.p256dh, auth: keys.auth }),
      });
    }
  } catch (err) {
    console.warn("Push toggle failed", err);
  } finally {
    pushToggleBtn.disabled = false;
    await updatePushControl();
  }
}

export async function initPush() {
  if (!pushSupported) return; // feature-detect: unsupported browsers keep the section hidden
  try {
    const res = await fetch("/api/push/config");
    if (res.ok) pushConfig = await res.json();
  } catch { /* config unreachable — leave push disabled */ }

  pushToggleBtn?.addEventListener("click", togglePush);

  // A notificationclick in the SW focuses this client and posts which conversation to open.
  navigator.serviceWorker?.addEventListener("message", (event) => {
    if (event.data && event.data.type === "open-conversation" && event.data.conversationId) {
      selectConversation(event.data.conversationId);
    }
  });

  await updatePushControl();
}
