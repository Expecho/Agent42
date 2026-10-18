/* Agent 42 — Web Push notifications (ADR 0028). Split out of app.js (ADR 0041). */

import { selectConversation } from "./conversations.js";
import { pushSection, pushStatus, pushTestBtn, pushTestStatus, pushToggleBtn } from "./dom.js";

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
    if (pushTestBtn) pushTestBtn.hidden = !sub; // a test only makes sense for a subscribed device
  } catch {
    pushStatus.textContent = "Off";
    pushToggleBtn.textContent = "Enable";
    if (pushTestBtn) pushTestBtn.hidden = true;
  }
}

// The POST /api/push/test verdicts (PushTestOutcomes). "sent" only means the push service accepted it:
// Web Push has no delivery receipt, so the hint points at the OS-level blocks that look like "broken".
const pushTestMessages = {
  sent: "Sent. If nothing shows within a few seconds, check this app's notification settings in Android (and battery optimisation).",
  "not-registered": "This device isn't registered on the server. Disable and enable notifications, then try again.",
  gone: "The push service says this subscription is gone. Disable and enable notifications to re-subscribe.",
  timeout: "The push service didn't answer in time. Try again.",
};

function describePushTest(result) {
  if (result.outcome === "failed") {
    return "The push service rejected the test" + (result.statusCode ? ` (HTTP ${result.statusCode}).` : ".");
  }
  return pushTestMessages[result.outcome] || "Unexpected result: " + result.outcome;
}

// Sends a test notification to THIS device and shows the push service's verdict. Re-registers the
// current subscription first (an idempotent upsert), so a server-side prune doesn't fail the test.
async function sendTestPush() {
  if (Notification.permission === "denied") {
    pushTestStatus.textContent = "Notifications are blocked in your browser settings for this site.";
    return;
  }
  pushTestBtn.disabled = true;
  pushTestStatus.textContent = "Sending…";
  try {
    const sub = await (await navigator.serviceWorker.ready).pushManager.getSubscription();
    if (!sub) {
      pushTestStatus.textContent = "Notifications are not subscribed on this device.";
      await updatePushControl();
      return;
    }
    const keys = sub.toJSON().keys;
    await fetch("/api/push/subscriptions", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ endpoint: sub.endpoint, p256dh: keys.p256dh, auth: keys.auth }),
    });
    const res = await fetch("/api/push/test", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ endpoint: sub.endpoint }),
    });
    pushTestStatus.textContent = res.ok
      ? describePushTest(await res.json())
      : "Test failed: " + (await res.text() || `HTTP ${res.status}`);
  } catch (err) {
    console.warn("Push test failed", err);
    pushTestStatus.textContent = "Test failed: the request didn't complete.";
  } finally {
    pushTestBtn.disabled = false;
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
  pushTestBtn?.addEventListener("click", sendTestPush);

  // A notificationclick in the SW focuses this client and posts which conversation to open.
  navigator.serviceWorker?.addEventListener("message", (event) => {
    if (event.data && event.data.type === "open-conversation" && event.data.conversationId) {
      selectConversation(event.data.conversationId);
    }
  });

  await updatePushControl();
}
