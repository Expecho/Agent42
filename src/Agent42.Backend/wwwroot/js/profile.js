/* Agent 42 — profile / preferences. Split out of app.js (ADR 0041). */

import { loadConfigHealth } from "./confighealth.js";
import { openProfileBtn, profileCancelBtn, profileDialog, profileForm, profileLanguage, profileSuggest, profileTabs, profileTheme, profileToolChecks } from "./dom.js";
import { LANGUAGES } from "./format.js";
import { updateLocationControl } from "./location.js";
import { clearMcpTabResult, loadMcpServers } from "./mcp.js";
import { loadProviders } from "./providers.js";
import { loadGoogleStatus } from "./calendar.js";
import { loadSkills } from "./skills.js";
import { updatePushControl } from "./push.js";
import { applyTheme } from "./theme.js";
import { allToolKeys, readToolChecks, renderToolChecks, toolCatalog } from "./tools.js";

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
export async function openProfileDialog() {
  await loadProfile(); // reconcile against the server before showing the current value
  fillLanguageOptions(profileLanguage, profile.preferredLanguage ?? "");
  // Null enabledTools (never saved, or saved before ADR 0021) means "all selectable tools".
  renderToolChecks(profileToolChecks, profile.enabledTools ?? allToolKeys());
  profileTheme.value = profile.theme ?? "system";
  // Null-sentinel: null (never saved) and true both mean ON; only an explicit false is unchecked.
  profileSuggest.checked = profile.suggestFollowUps !== false;
  profileSaved = false; // fresh edit session; the close handler reverts a preview unless we save
  profileTabs.select(0); // always open on General
  updateLocationControl(); // refresh the location status/button for the current grant state
  updatePushControl(); // and the push notifications status/button (ADR 0028)
  loadProviders(); // refresh the user's providers list alongside the profile
  clearMcpTabResult(); // a stale connection verdict must not outlive the dialog (ADR 0064)
  loadMcpServers(); // and their MCP servers (config-driven remote MCP servers)
  loadSkills(); // and their uploaded skills (ADR 0049)
  loadGoogleStatus(); // and whether Google Calendar is connected (ADR 0053)
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
  profileSaved = true;
  profileDialog.close();
}

export function wireProfile() {
  openProfileBtn.addEventListener("click", () => openProfileDialog());
  profileCancelBtn.addEventListener("click", () => profileDialog.close());
  // Live-preview the theme as the user changes the select (staged: not persisted until Save).
  profileTheme.addEventListener("change", () => applyTheme(profileTheme.value));
  // Revert an unsaved preview when the dialog closes via Cancel, Esc, or backdrop.
  profileDialog.addEventListener("close", () => {
    if (!profileSaved) applyTheme(profile.theme ?? "system");
    profileSaved = false;
  });
  profileForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProfile();
  });
}
