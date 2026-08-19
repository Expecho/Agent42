/* Agent 42 — user location (ADR 0012). Split out of app.js (ADR 0041). */

import { locationEnableBtn, locationStatus } from "./dom.js";

// A fix older than this is dropped rather than sent: the model must never be told the user is
// somewhere they left half an hour ago.
const LOCATION_MAX_AGE_MS = 30 * 60 * 1000;
// Background refresh cadence while the tab is visible. Nobody is ever waiting on these.
const LOCATION_REFRESH_MS = 5 * 60 * 1000;
// Set ONLY after a getCurrentPosition() actually succeeded — see the WebKit leg below.
const LOCATION_GRANT_KEY = "agent42.location.granted";

const geolocationSupported = "geolocation" in navigator;

// The last known fix, refreshed out of band. The send path reads this synchronously and never
// awaits a new one — see currentLocationForSend().
let lastFix = null;
let locationRefreshTimer = null;

// ——— Authorization: two legs, because one of them does not exist on iOS ———
//
// navigator.permissions.query({name: "geolocation"}) is NOT supported in WebKit — i.e. every
// browser on iOS/iPadOS — where it rejects. Gating capture purely on state === "granted" would
// therefore be dead on every iPhone: the user grants, no fix is ever attached, and the grant button
// never hides. So:
//
//   Leg 1 — Permissions API present (Chrome/Edge/Firefox, desktop + Android): trust state === "granted".
//   Leg 2 — Permissions API absent/rejecting (WebKit): trust a localStorage flag that we set only
//           after a getCurrentPosition() has actually SUCCEEDED. A permission the browser has already
//           granted resolves without re-prompting, so capturing under that flag never ambushes the
//           user; a PERMISSION_DENIED on any later capture clears it (they revoked it in site
//           settings) and brings the grant button back.
//
// getCurrentPosition() is never called on a send path under either leg.
async function geolocationPermissionStatus() {
  if (!navigator.permissions?.query) return null;
  try {
    return await navigator.permissions.query({ name: "geolocation" });
  } catch {
    return null; // WebKit: the query itself rejects.
  }
}

function storedGrant() {
  try {
    return localStorage.getItem(LOCATION_GRANT_KEY) === "1";
  } catch {
    return false; // Private mode / storage disabled: no grant, no capture.
  }
}

function setStoredGrant(granted) {
  try {
    if (granted) localStorage.setItem(LOCATION_GRANT_KEY, "1");
    else localStorage.removeItem(LOCATION_GRANT_KEY);
  } catch {
    /* storage unavailable — leg 2 simply stays unauthorized */
  }
}

async function isCaptureAuthorized() {
  if (!geolocationSupported) return false;
  const status = await geolocationPermissionStatus();
  return status ? status.state === "granted" : storedGrant();
}

function readPosition() {
  return new Promise((resolve, reject) => {
    // enableHighAccuracy is affordable precisely BECAUSE nobody is waiting on this: it runs in the
    // background, never in the send path, so a cold GPS spin-up costs the user nothing.
    navigator.geolocation.getCurrentPosition(resolve, reject, {
      enableHighAccuracy: true,
      timeout: 15000,
      maximumAge: 60000,
    });
  });
}

function toFix(position) {
  return {
    latitude: position.coords.latitude,
    longitude: position.coords.longitude,
    accuracyMetres: position.coords.accuracy ?? null,
    capturedAt: new Date(position.timestamp).toISOString(),
  };
}

// PositionError.PERMISSION_DENIED === 1. The user revoked the permission in site settings, so the
// WebKit leg's flag is now a lie — clear it and offer the grant button again.
function handlePositionError(error) {
  if (error?.code === 1) {
    setStoredGrant(false);
    lastFix = null;
    stopBackgroundLocationRefresh();
    updateLocationControl();
  }
}

// Refreshes the cached fix out of band. Nothing awaits this — a failure is silent by design.
async function refreshFix() {
  if (!(await isCaptureAuthorized())) return;
  try {
    lastFix = toFix(await readPosition());
  } catch (error) {
    handlePositionError(error);
  }
}

function startBackgroundLocationRefresh() {
  stopBackgroundLocationRefresh();
  if (document.visibilityState !== "visible") return;
  locationRefreshTimer = setInterval(refreshFix, LOCATION_REFRESH_MS);
}

function stopBackgroundLocationRefresh() {
  if (locationRefreshTimer !== null) {
    clearInterval(locationRefreshTimer);
    locationRefreshTimer = null;
  }
}

// THE SEND PATH. Synchronous, and deliberately so: it reads whatever is already cached and returns
// null if there is nothing fresh. It never calls getCurrentPosition() and never awaits a fix — an
// awaited GPS acquisition would add seconds to every message once the cached fix went stale, and
// typing "hi" must not pay for a GPS spin-up. A stale fix is dropped, never sent.
export function currentLocationForSend() {
  if (!lastFix) return null;
  const age = Date.now() - Date.parse(lastFix.capturedAt);
  if (!Number.isFinite(age) || age > LOCATION_MAX_AGE_MS) return null;
  return lastFix;
}

// Reflects the location state in Preferences → General: status text + the Enable button, which
// shows exactly when we could capture but are not yet allowed to. Safe to call while the dialog
// is closed (updates hidden nodes) — driven on open and from the Permissions 'change' handler.
export async function updateLocationControl() {
  if (!locationStatus || !locationEnableBtn) return;
  if (!geolocationSupported) {
    locationStatus.textContent = "Not available in this browser";
    locationEnableBtn.hidden = true;
    return;
  }
  const authorized = await isCaptureAuthorized();
  locationStatus.textContent = authorized ? "Sharing on" : "Off";
  locationEnableBtn.hidden = authorized;
}

// The one place a browser permission prompt is ever triggered — on an explicit tap.
async function requestLocationGrant() {
  if (!geolocationSupported) return;
  locationEnableBtn.disabled = true;
  try {
    lastFix = toFix(await readPosition());
    setStoredGrant(true); // Leg 2: only ever set after a capture actually succeeded.
    startBackgroundLocationRefresh();
  } catch (error) {
    handlePositionError(error);
  } finally {
    locationEnableBtn.disabled = false;
    await updateLocationControl();
  }
}

export async function initLocation() {
  if (!geolocationSupported) {
    await updateLocationControl(); // paint "Not available" — do NOT gate this behind the early return
    return;
  }

  locationEnableBtn?.addEventListener("click", requestLocationGrant);

  // Leg 1 browsers tell us when the permission changes (granted in site settings, or revoked).
  const status = await geolocationPermissionStatus();
  status?.addEventListener?.("change", async () => {
    if (status.state !== "granted") {
      lastFix = null;
      stopBackgroundLocationRefresh();
    } else {
      await refreshFix();
      startBackgroundLocationRefresh();
    }
    await updateLocationControl();
  });

  document.addEventListener("visibilitychange", async () => {
    if (document.visibilityState === "visible") {
      await refreshFix();
      startBackgroundLocationRefresh();
    } else {
      stopBackgroundLocationRefresh();
    }
  });

  await updateLocationControl();

  // Already authorized from an earlier visit: warm the cache now so the first message of the
  // session can carry a position, without prompting (an already-granted permission resolves silently).
  if (await isCaptureAuthorized()) {
    await refreshFix();
    startBackgroundLocationRefresh();
  }
}
