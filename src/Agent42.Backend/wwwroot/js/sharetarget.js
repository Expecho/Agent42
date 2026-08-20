/* Agent 42 — Web Share Target hand-off (ADR 0023). Split out of app.js (ADR 0041). */

import { agents, loadAgents, startAgentChat } from "./agents.js";
import { handleFiles } from "./attachments.js";
import { autosize } from "./composer.js";
import { startNewConversation } from "./conversations.js";
import { input, shareAgentList, shareCancelBtn, shareDialog, shareNewChatBtn } from "./dom.js";

// The service worker stashed the shared payload in IndexedDB before redirecting here (see sw.js).
// These constants MIRROR the SW's db/store/key — the two scripts share the store, not the code.
const SHARE_DB = "agent42-share";
const SHARE_STORE = "pending";
const SHARE_KEY = "payload";

// Read the stashed payload and clear it (a reload must not re-open the picker). Best-effort:
// any IndexedDB failure resolves null and the share is silently dropped — never blocks load.
function readSharePayload() {
  return new Promise((resolve) => {
    const open = indexedDB.open(SHARE_DB, 1);
    open.onupgradeneeded = () => open.result.createObjectStore(SHARE_STORE);
    open.onerror = () => resolve(null);
    open.onsuccess = () => {
      const tx = open.result.transaction(SHARE_STORE, "readwrite");
      const store = tx.objectStore(SHARE_STORE);
      const get = store.get(SHARE_KEY);
      get.onsuccess = () => { store.delete(SHARE_KEY); resolve(get.result || null); };
      get.onerror = () => resolve(null);
    };
  });
}

// Rebuild File objects from the stored {name,type,blob} records so the shared files run through
// the normal handleFiles upload path unchanged (images → attachments, documents → RAG).
const filesFromShare = (record) =>
  (record.files || []).map((f) => new File([f.blob], f.name, { type: f.type }));

// Prefill text for the composer: a shared note and/or url (both → note then url), else the title.
function sharePrefillText(record) {
  const parts = [record.text, record.url].filter(Boolean);
  return parts.length ? parts.join("\n") : (record.title || "");
}

// Populate the picker's agent list — one row per stored agent; a click chooses that destination.
function renderShareAgents(onChoose) {
  shareAgentList.innerHTML = "";
  for (const agent of agents) {
    const li = document.createElement("li");
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "share-agent";
    btn.textContent = agent.name;
    btn.title = agent.description || agent.name;
    btn.addEventListener("click", () => onChoose(agent));
    li.appendChild(btn);
    shareAgentList.appendChild(li);
  }
}

// After a destination is chosen: prefill the composer and push shared files through the upload
// path. NEVER auto-sends — the user reviews and presses Send, keeping egress an explicit act
// (ADR 0013); with no model the composer stays gated (applyModelAvailability), so it can't send.
function applyShare(record) {
  input.value = sharePrefillText(record);
  autosize();
  const files = filesFromShare(record);
  if (files.length) handleFiles(files);
  input.focus();
}

// Run at init: if this load is a share hand-off (/?share=1), read the stash and open the picker.
// loadAgents() is awaited so the agent list is populated before the picker opens (init's own
// loadAgents is fire-and-forget — an eager open would render an empty list).
export async function initShare() {
  if (!new URLSearchParams(location.search).has("share")) return;
  history.replaceState(null, "", location.pathname); // drop ?share=1 so a reload isn't a re-share
  const record = await readSharePayload();
  if (!record) return;

  await loadAgents();
  renderShareAgents((agent) => { shareDialog.close(); startAgentChat(agent); applyShare(record); });
  shareNewChatBtn.onclick = () => { shareDialog.close(); startNewConversation(); applyShare(record); };
  shareCancelBtn.onclick = () => shareDialog.close();
  shareDialog.showModal();
}
