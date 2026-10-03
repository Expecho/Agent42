/* Agent 42 — conversation switching. Split out of app.js (ADR 0041). */

import { lockPicker, setIncognito, unlockPicker } from "./agents.js";
import { renderArtifactCards } from "./artifacts.js";
import { clearPendingAttachments } from "./attachments.js";
import { cacheGet, cachePut } from "./cache.js";
import { input, newChatBtn } from "./dom.js";
import { closeDrawer } from "./drawer.js";
import { defaultModel, setModel } from "./models.js";
import { profile } from "./profile.js";
import { streaming, voice } from "./send.js";
import { setTopbarShare } from "./sharelinks.js";
import { conversations, renderSidebar } from "./sidebar.js";
import { addAgentImage, addCopyButton, addMessage, clearThread, fillUserBubble, replayToolCalls, scrollToBottom, setAgentMarkdown, setEmptyVisible } from "./thread.js";
import { allToolKeys, setChatTools } from "./tools.js";

export let activeId = crypto.randomUUID();
// The agent id to *bind*, sent on the wire only on the first turn of a freshly started
// agent chat, then cleared. Kept distinct from activeAgentId so a resumed agent chat never
// re-resolves (and rebinds against) a possibly-deleted agent.
export let pendingAgentId = null;
// The agent id of the current chat (fresh or resumed). Drives the picker lock + label only.
export let activeAgentId = null;
export async function selectConversation(id) {
  closeDrawer();
  if (streaming || id === activeId) return;
  voice.stop(); // exit hands-free mode when switching chats (ADR 0037)
  activeId = id;
  // An opened conversation is a persisted one (an ephemeral chat never gets a sidebar row), so
  // incognito is off and locked here — it can only be chosen on a fresh chat (ADR 0034).
  setIncognito(false, false);
  // ...and it is persisted WITH history (an ephemeral chat never gets a sidebar row to open), so it is
  // shareable. Title from the same list the row action reads; the constant covers a not-yet-loaded list.
  setTopbarShare(id, conversations.find((c) => c.id === id)?.title ?? "This conversation");
  clearPendingAttachments();
  renderSidebar();
  clearThread();
  setEmptyVisible(true);

  try {
    const [msgRes, modelRes, agentRes, toolsRes] = await Promise.all([
      fetch(`/api/conversations/${encodeURIComponent(id)}/messages`),
      fetch(`/api/conversations/${encodeURIComponent(id)}/model`),
      fetch(`/api/conversations/${encodeURIComponent(id)}/agent`),
      fetch(`/api/conversations/${encodeURIComponent(id)}/tools`),
    ]);
    if (!msgRes.ok) return;
    const turns = await msgRes.json();
    renderTurns(id, turns);
    cachePut("messages:" + id, turns); // mirror this history for offline viewing (ADR 0032)
    // Restore the model picker only after the history is on screen, so a failure
    // here can never prevent the conversation itself from rendering.
    if (modelRes.ok) setModel(await modelRes.json());
    // Restore this conversation's effective tool selection for the popover (ADR 0021). Hidden on an
    // agent chat, but harmless to hold the snapshot's set.
    if (toolsRes.ok) setChatTools(await toolsRes.json());
    // Then apply the agent binding — after the render + setModel, for the same reason.
    // The binding is already snapshotted server-side, so leave pendingAgentId null: a
    // resumed agent chat must never re-resolve (and rebind against) a deleted agent.
    const binding = agentRes.ok ? await agentRes.json() : null;
    if (binding) {
      activeAgentId = binding.agentId;
      pendingAgentId = null;
      setModel(binding.model);
      lockPicker(binding.name, binding.model);
    } else {
      activeAgentId = null;
      pendingAgentId = null;
      unlockPicker();
    }
  } catch {
    // Offline: render this chat's history from the cache if it was opened while online before.
    // The model/agent/tools siblings simply degrade — the composer can't send offline anyway.
    const cached = await cacheGet("messages:" + id);
    if (cached) renderTurns(id, cached);
  }
}

// Render a history array (from the network or the offline cache) into the open thread. Extracted
// so the live path and the offline-cache fallback in selectConversation share one renderer.
function renderTurns(id, turns) {
  for (const turn of turns) {
    const msg = addMessage(turn.role === "user" ? "user" : "agent");
    if (turn.role === "user") {
      fillUserBubble(msg, turn.text, turn.attachments, id);
    } else {
      setAgentMarkdown(msg, turn.text);
      // History-restored reply: capture turn.text by closure so copy works after a reload too.
      addCopyButton(msg, turn.text);
      // An assistant turn may carry a generated image (ADR 0020); re-render it above the prose.
      for (const att of turn.attachments ?? []) addAgentImage(msg, id, att.id, att.fileName);
      // ...and the tool calls it made (ADR 0080), as the same folded section the live turn showed.
      // Order within this arm is irrelevant: the group self-positions above .prose.
      replayToolCalls(msg, turn.toolCalls);
      // ...and the artifacts it created or updated, as cards under the prose.
      renderArtifactCards(msg, id, turn.artifacts);
    }
  }
  scrollToBottom();
}

export function startNewConversation() {
  closeDrawer();
  if (streaming) return;
  voice.stop(); // exit hands-free mode when switching chats (ADR 0037)
  activeId = crypto.randomUUID();
  activeAgentId = null;
  pendingAgentId = null;
  // A new regular chat starts from the user's default tool set (ADR 0021); null profile default = all.
  setChatTools(profile.enabledTools ?? allToolKeys());
  // ...and from their default model, so the picker doesn't inherit whatever the last-opened
  // conversation was pinned to. `defaultModel` is the server-RESOLVED default (/api/models).
  if (defaultModel) setModel(defaultModel);
  clearPendingAttachments();
  unlockPicker();
  // A fresh chat is where incognito can be chosen — off by default, and interactive until first send.
  setIncognito(false, true);
  // ...and there is nothing in it to share yet. Clearing also drops the previous conversation as the
  // target, so Create can't mint a public link to a chat the user has just navigated away from.
  setTopbarShare(null);
  clearThread();
  setEmptyVisible(true);
  renderSidebar();
  input.focus();
}

export function wireConversations() {
  newChatBtn.addEventListener("click", startNewConversation);
}

export function setActiveId(value) {
  activeId = value;
}

export function setActiveAgentId(value) {
  activeAgentId = value;
}

export function setPendingAgentId(value) {
  pendingAgentId = value;
}
