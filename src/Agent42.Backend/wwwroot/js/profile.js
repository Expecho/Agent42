/* Agent 42 — profile / preferences. Split out of app.js (ADR 0041). */

import { loadConfigHealth } from "./confighealth.js";
import { openProfileBtn, prefsBackBtn, prefsCloseBtn, prefsDetailTitle, prefsStatus, profileCancelBtn, profileDialog, profileForm, profileLanguage, profileNav, profileSuggest, profileTabs, profilePalette, profileTheme, profileToolChecks } from "./dom.js";
import { LANGUAGES } from "./format.js";
import { updateLocationControl } from "./location.js";
import { clearMcpTabResult, loadMcpServers } from "./mcp.js";
import { loadProviders } from "./providers.js";
import { loadSkills } from "./skills.js";
import { loadChatConnections } from "./chatgateway.js";
import { updatePushControl } from "./push.js";
import { applyPalette, applyTheme } from "./theme.js";
import { allToolKeys, readToolChecks, refreshToolsBadge, renderToolChecks, toolCatalog } from "./tools.js";

// The last profile fetched from the server. The server is the source of truth; this is
// only a render cache, always reconciled against a fresh GET/PUT response.
export let profile = { preferredLanguage: "", theme: "system", enabledTools: null, suggestFollowUps: true };
let profileSaved = false;         // set by saveProfile so the dialog's close handler skips the revert
// GET the server-stored profile (the source of truth). Silent on failure — the panel still
// opens with the device-language default.
export async function loadProfile() {
  try {
    const res = await fetch("/api/profile");
    if (!res.ok) return;
    profile = await res.json();
  } catch { /* silo may be unreachable; keep the current cached profile */ }
}

// Fills the language <select>, preselecting the stored tag. A stored tag that isn't in the
// curated list is appended so it's never silently dropped.
function fillLanguageOptions(select, selectedTag) {
  select.innerHTML = "";
  for (const lang of LANGUAGES) {
    const opt = document.createElement("option");
    opt.value = lang.tag;
    opt.textContent = lang.name;
    select.appendChild(opt);
  }
  if (selectedTag && ![...select.options].some((o) => o.value === selectedTag)) {
    const opt = document.createElement("option");
    opt.value = selectedTag;
    opt.textContent = selectedTag;
    select.appendChild(opt);
  }
  select.value = selectedTag ?? "";
}
// ── The ≤760px drill-down ──────────────────────────────────────────────────────────────────────
//
// One class on the dialog, meaningful only inside the narrow media block; above 760px the rail and the
// pane are both visible and `is-detail` changes nothing. It is entered by a DELEGATED click on the rail,
// deliberately NOT from wireTabs' select(): openProfileDialog() calls select(0) on every open, so
// driving it from there would land a phone in General's detail view on every single open, and wireTabs
// is shared with the agent dialog and must stay free of prefs concepts.
//
// The one programmatic case that DOES want the detail view is the MCP OAuth return, which calls
// enterPrefsDetail(3) beside its own untouched profileTabs.select(3).
export function enterPrefsDetail(index) {
  const tab = profileNav.querySelectorAll('[role="tab"]')[index];
  if (!tab) return;
  // The LABEL span, never tab.textContent — the Tools button contains the count badge, so textContent
  // there reads "Tools 9/9" (measured) rather than "Tools".
  prefsDetailTitle.textContent = tab.querySelector(".prefs__navlabel")?.textContent ?? "";
  profileDialog.classList.add("is-detail");
}

function exitPrefsDetail() {
  profileDialog.classList.remove("is-detail");
  prefsDetailTitle.textContent = "";
}

// ── The footer status ──────────────────────────────────────────────────────────────────────────
//
// Two states, not three: saveProfile() CLOSES the dialog, so "All changes saved" is observable on open
// and after a reset, never as a post-save confirmation. Its job is to warn that edits are pending.
//
// Wired to the four Save-persisted fields ONLY (language, theme, suggest, tool checks). A form-level
// input/change listener would also fire for the three model selects and every provider/MCP/skill
// control, which persist THEMSELVES the moment they change — claiming "Unsaved changes" for something
// already stored.
function markPrefsDirty() {
  prefsStatus.textContent = "Unsaved changes";
  prefsStatus.classList.add("is-dirty");
}

function resetPrefsStatus() {
  prefsStatus.textContent = "All changes saved";
  prefsStatus.classList.remove("is-dirty");
}

export async function openProfileDialog() {
  await loadProfile(); // reconcile against the server before showing the current value
  fillLanguageOptions(profileLanguage, profile.preferredLanguage ?? "");
  // Null enabledTools (never saved, or saved before ADR 0021) means "all selectable tools".
  renderToolChecks(profileToolChecks, profile.enabledTools ?? allToolKeys());
  refreshToolsBadge();
  profileTheme.value = profile.theme ?? "system";
  profilePalette.value = profile.palette ?? "amber"; // null (never chosen / legacy) = amber
  // Null-sentinel: null (never saved) and true both mean ON; only an explicit false is unchecked.
  profileSuggest.checked = profile.suggestFollowUps !== false;
  profileSaved = false; // fresh edit session; the close handler reverts a preview unless we save
  resetPrefsStatus(); // fresh edit session; nothing is pending yet
  // BEFORE showModal, and it is the EXIT helper on purpose: on a phone every open must land on the rail
  // (the list), not on whichever section was last drilled into. Entering detail is the rail click's job
  // alone — see the note above the helper pair, and the AC5 guard that pins this function clear of it.
  exitPrefsDetail();
  profileTabs.select(0); // always open on General
  updateLocationControl(); // refresh the location status/button for the current grant state
  updatePushControl(); // and the push notifications status/button (ADR 0028)
  loadProviders(); // refresh the user's providers list alongside the profile
  clearMcpTabResult(); // a stale connection verdict must not outlive the dialog (ADR 0064)
  loadMcpServers(); // and their MCP servers (config-driven remote MCP servers)
  loadSkills(); // and their uploaded skills (ADR 0049)
  loadChatConnections(); // and their chat-app bots (ADR 0111)
  loadConfigHealth(); // and the STATIC configuration health (live probes are opt-in, on the button)
  profileDialog.showModal();
  profileLanguage.focus();
}

async function saveProfile() {
  // The endpoint does read-modify-write (absent-means-keep), so sending these fields never wipes the
  // others. enabledTools is the full explicit list — "all" is every key (ADR 0021) — but ONLY when the
  // catalog loaded: if /api/tools failed, toolCatalog is [] and sending [] would wipe the default to
  // "none". Omitting it then leaves the stored default untouched (absent-means-keep).
  const payload = {
    preferredLanguage: profileLanguage.value,
    theme: profileTheme.value,
    palette: profilePalette.value,
    suggestFollowUps: profileSuggest.checked,
  };
  if (toolCatalog.length > 0) payload.enabledTools = readToolChecks(profileToolChecks);
  try {
    const res = await fetch("/api/profile", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) return;
    profile = await res.json(); // reconcile: the server trims/caps the tag and normalizes theme
  } catch { return; /* silo may be unreachable; leave the dialog open */ }

  applyTheme(profile.theme ?? "system"); // commit the server-normalized value (paint + cache)
  applyPalette(profile.palette);
  profileSaved = true;
  profileDialog.close();
}

export function wireProfile() {
  openProfileBtn.addEventListener("click", () => openProfileDialog());
  profileCancelBtn.addEventListener("click", () => profileDialog.close());
  // Same dialog.close() as Cancel, so the theme-revert-on-close handler below still runs. On a phone
  // Cancel is hidden and this × is the only dismiss affordance.
  prefsCloseBtn.addEventListener("click", () => profileDialog.close());
  prefsBackBtn.addEventListener("click", exitPrefsDetail);
  // DELEGATED, and via closest: the rail buttons carry an inline <svg> icon, so e.target is usually a
  // <path> and a bare e.target check would miss most presses. Delegation also means the listener is
  // bound once, not per tab.
  profileNav.addEventListener("click", (e) => {
    const tab = e.target.closest('[role="tab"]');
    if (!tab) return;
    enterPrefsDetail([...profileNav.querySelectorAll('[role="tab"]')].indexOf(tab));
  });
  // On the CONTAINER, so it survives every re-render of the checkboxes inside it.
  profileToolChecks.addEventListener("change", () => {
    refreshToolsBadge();
    markPrefsDirty();
  });
  profileLanguage.addEventListener("change", markPrefsDirty);
  profileSuggest.addEventListener("change", markPrefsDirty);
  // Live-preview the theme as the user changes the select (staged: not persisted until Save).
  profileTheme.addEventListener("change", () => {
    applyTheme(profileTheme.value);
    markPrefsDirty();
  });
  // Same staged preview for the palette (ADR 0129), independent of the mode.
  profilePalette.addEventListener("change", () => {
    applyPalette(profilePalette.value);
    markPrefsDirty();
  });
  // Revert an unsaved preview when the dialog closes via Cancel, Esc, or backdrop.
  profileDialog.addEventListener("close", () => {
    if (!profileSaved) {
      applyTheme(profile.theme ?? "system");
      applyPalette(profile.palette);
    }
    profileSaved = false;
  });
  profileForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProfile();
  });
}
