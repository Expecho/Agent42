/* Agent 42 — user location (ADR 0012). Split out of app.js (ADR 0041).

   Since ADR 0093 the position never rides a chat request: get_current_location is a client tool (ADR 0089), and
   this page answers it through locationForTool() only when the model calls it. The send path never touches
   geolocation. The tool path answers from the cached fix when it is fresh, and otherwise may take ONE bounded
   capture, only under an existing grant. */

import { locationEnableBtn, locationStatus } from "./dom.js";

// A fix older than this is never returned: the model must never be told the user is somewhere they
// left half an hour ago.
const LOCATION_MAX_AGE_MS = 30 * 60 * 1000;
// The tool's one fresh capture. Below the server's 15 s wait for an automatic client tool
// (ClientToolLimits.AutomaticTimeout), so the page answers no_fix before the server gives up on it.
const LOCATION_TOOL_TIMEOUT_MS = 10000;
// The background refresh's capture: nobody is waiting on it.
const LOCATION_REFRESH_TIMEOUT_MS = 15000;
// Background refresh cadence while the tab is visible. Nobody is ever waiting on these.
const LOCATION_REFRESH_MS = 5 * 60 * 1000;
// Set ONLY after a getCurrentPosition() actually succeeded — see the WebKit leg below.
const LOCATION_GRANT_KEY = "agent42.location.granted";

const geolocationSupported = "geolocation" in navigator;

// The last known fix, refreshed out of band, so most tool calls are answered at once — see
// locationForTool().
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
//           granted resolves without re-prompting; a PERMISSION_DENIED on any later capture clears it
//           (they revoked it in site settings) and brings the grant button back. The flag is only a
//           flag, though: a site permission reset to "ask" still prompts on the next capture, and that
//           capture can be the tool's, mid-turn (ADR 0093 accepts this).
//
// getCurrentPosition() is never called on the send path under either leg, and on the tool path only
// after isCaptureAuthorized() — so on leg 1 the tool never shows a prompt.
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

function readPosition(timeoutMs = LOCATION_REFRESH_TIMEOUT_MS) {
  return new Promise((resolve, reject) => {
    // enableHighAccuracy is affordable because nobody waits on the background refresh, and the tool's
    // capture is bounded by timeoutMs below the server's own wait.
    navigator.geolocation.getCurrentPosition(resolve, reject, {
      enableHighAccuracy: true,
      timeout: timeoutMs,
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

function isFresh(fix) {
  if (!fix) return false;
  const age = Date.now() - Date.parse(fix.capturedAt);
  return Number.isFinite(age) && age <= LOCATION_MAX_AGE_MS;
}

// THE TOOL PATH (ADR 0093): the answer to get_current_location, run only when the model calls it. The
// grant is checked BEFORE any capture, so an unauthorized page answers not_shared without calling
// getCurrentPosition(). A fresh cached fix is answered at once; otherwise one capture, bounded below the
// server's wait. A stale fix is never returned. The server validates whatever this sends.
export async function locationForTool() {
  if (!(await isCaptureAuthorized())) return { status: "not_shared" };
  if (isFresh(lastFix)) return lastFix;
  try {
    lastFix = toFix(await readPosition(LOCATION_TOOL_TIMEOUT_MS));
    return lastFix;
  } catch (error) {
    handlePositionError(error);
    return error?.code === 1 ? { status: "not_shared" } : { status: "no_fix" };
  }
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

  // Already authorized from an earlier visit: warm the cache now so the first location tool call of
  // the session is answered at once, without prompting (an already-granted permission resolves silently).
  if (await isCaptureAuthorized()) {
    await refreshFix();
    startBackgroundLocationRefresh();
  }
}
