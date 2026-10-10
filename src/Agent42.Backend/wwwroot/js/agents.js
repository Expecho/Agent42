/* Agent 42 — stored agents (ADR 0003/0016/0022) + incognito (ADR 0034). Split out of app.js (ADR 0041). */

import { clearPendingAttachments } from "./attachments.js";
import { cacheGet, cachePut } from "./cache.js";
import { selectConversation, setActiveAgentId, setActiveAgentKind, setActiveId, setPendingAgentId } from "./conversations.js";
import { addAgentBtn, agentCancelBtn, agentContinuousInput, agentContinuousNote, agentDescriptionInput, agentDialog, agentEmailCopyBtn, agentEmailNote, agentEmailSection, agentEmailValue, agentForm, agentFormTitle, agentKeyCopyBtn, agentKindField, agentKindSelect, agentKeyReveal, agentKeySection, agentKeyValue, agentLabel, agentList, agentMcpExposedInput, agentMcpUrl, agentMcpUrlCopyBtn, agentModelSelect, agentNameInput, agentRegenerateEmailBtn, agentRegenerateKeyBtn, agentRunUrl, agentRunUrlCopyBtn, agentSystemPromptInput, agentTabs, agentToolChecks, incognitoBtn, input, modelField, toolsBtn } from "./dom.js";
import { closeDrawer } from "./drawer.js";
import { defaultModel, fillModelOptions, modelLabel, selectModel, setModel } from "./models.js";
import { streaming } from "./send.js";
import { setTopbarShare } from "./sharelinks.js";
import { conversations, renderSidebar } from "./sidebar.js";
import { clearThread, setEmptyVisible } from "./thread.js";
import { allToolKeys, readToolChecks, renderToolChecks, toolCatalog } from "./tools.js";
import { openWebhookPanel } from "./webhooks.js";

// Incognito flag for the active chat (ADR 0034). Fixed at chat creation: settable only on a fresh,
// empty regular chat, then locked once the first message is sent (like the conversation id itself).
export let ephemeral = false;
export let agents = [];
// The agent being edited in the dialog, or null when creating a new one.
let editingAgentId = null;
export async function loadAgents() {
  try {
    const res = await fetch("/api/agents");
    if (!res.ok) return;
    agents = await res.json();
    renderAgents();
    cachePut("agents", agents); // mirror for offline (ADR 0032)
  } catch {
    // Offline/silo unreachable: fall back to the last cached agents instead of an empty list.
    const cached = await cacheGet("agents");
    if (cached && agents.length === 0) { agents = cached; renderAgents(); }
  }
}

export function renderAgents() {
  agentList.innerHTML = "";
  if (agents.length === 0) {
    const li = document.createElement("li");
    li.className = "agents__empty";
    li.textContent = "no agents yet";
    agentList.appendChild(li);
    return;
  }
  for (const agent of agents) {
    const li = document.createElement("li");
    li.className = "agent-row";

    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "agent";
    btn.title = agent.description || agent.name;
    btn.addEventListener("click", () => startAgentChat(agent));

    const name = document.createElement("span");
    name.className = "agent__name";
    name.textContent = agent.name;

    const model = document.createElement("span");
    model.className = "agent__model";
    // The stored value is a qualified id; show the friendly name, not "github:openai/gpt-4o-mini".
    model.textContent = modelLabel(agent.model);
    model.title = agent.model;

    btn.append(name);
    // The seeded research agent (ADR 0082) runs a plan/search/reflect loop and a copilot agent runs the GitHub
    // Copilot loop, which the name alone may not say.
    if (agent.kind === "research" || agent.kind === "copilot") {
      const badge = document.createElement("span");
      badge.className = "agent__badge";
      badge.textContent = agent.kind;
      btn.append(badge);
    }
    btn.append(model);

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "agent__action";
    edit.setAttribute("aria-label", `Edit agent: ${agent.name}`);
    edit.textContent = "✎";
    edit.addEventListener("click", (e) => {
      e.stopPropagation();
      openAgentDialog(agent);
    });

    const del = document.createElement("button");
    del.type = "button";
    del.className = "agent__action";
    del.setAttribute("aria-label", `Delete agent: ${agent.name}`);
    del.textContent = "🗑";
    del.addEventListener("click", (e) => {
      e.stopPropagation();
      deleteAgent(agent.id, agent.name);
    });

    li.append(btn, edit, del);
    agentList.appendChild(li);
  }
}

// Clicking an agent always starts a NEW chat seeded with that agent's persona. The model
// is locked to the agent's; only pendingAgentId goes on the wire (bound on the first send).
export function startAgentChat(agent) {
  closeDrawer();
  if (streaming) return;
  // Continuous agents route every start onto one deterministic conversation id
  // (`agent-<id>`), so all their chats share one ongoing history (ADR 0016); per-chat
  // agents get a fresh random id. crypto.randomUUID() never emits the `agent-` prefix,
  // so the two id spaces never collide.
  const convId = agent.continuous ? `agent-${agent.id}` : crypto.randomUUID();
  // Resume an existing continuous conversation instead of showing a blank thread over a
  // grain that already has history — delegate to selectConversation, which loads the
  // messages/model/binding and locks the picker.
  if (agent.continuous && conversations.some((c) => c.id === convId)) {
    selectConversation(convId);
    return;
  }
  setActiveId(convId);
  // A fresh agent chat has nothing to share yet. This does NOT come for free from startNewConversation
  // — startAgentChat sets the id itself — and without it the button would stay enabled pointing at the
  // conversation the user just left, so Create would mint a public link to a chat they aren't looking at.
  // (The continuous branch above returns through selectConversation, which enables share correctly.)
  setTopbarShare(null);
  setActiveAgentId(agent.id);
  setActiveAgentKind(agent.kind ?? null);
  setPendingAgentId(agent.id);
  // A copilot chat refuses incognito turns (the runtime records the turn as it runs), so the toggle is locked off.
  if (agent.kind === "copilot") setIncognito(false, false);
  clearPendingAttachments();
  clearThread();
  setEmptyVisible(true);
  setModel(agent.model);
  lockPicker(agent.name, agent.model);
  renderSidebar();
  input.focus();
}

// The history-mode note as index.html writes it, restored when the dialog opens on an ordinary agent.
const CONTINUOUS_NOTE = "All chats started from this agent share one ongoing history.";

function openAgentDialog(agent) {
  editingAgentId = agent?.id ?? null;
  agentFormTitle.textContent = agent ? "Edit agent" : "New agent";
  agentNameInput.value = agent?.name ?? "";
  agentDescriptionInput.value = agent?.description ?? "";
  agentSystemPromptInput.value = agent?.systemPrompt ?? "";
  agentContinuousInput.checked = agent?.continuous ?? false;
  // The type is chosen on create only: an agent's kind never changes once saved, so the field is hidden on edit.
  agentKindField.hidden = !!agent;
  agentKindSelect.value = "";
  applyKindLock(agent?.kind ?? null);
  agentMcpExposedInput.checked = agent?.mcpExposed ?? false;
  // Remembered so saveAgent can spot the OFF -> ON transition (ADR 0070). Read at open rather than from
  // the stored agent at save time, because the list is reloaded asynchronously and could have moved on.
  mcpExposedWhenOpened = agentMcpExposedInput.checked;
  // The key section (with Regenerate) is meaningful only for a SAVED agent; a new agent has no key
  // yet — one is minted on create and revealed then. Always start with the raw key hidden (ADR 0022).
  agentKeySection.hidden = !agent;
  agentKeyReveal.hidden = true;
  agentKeyValue.textContent = "";
  // The run endpoint is agent-independent and not a secret (ADR 0022: the key alone identifies the
  // agent), so show its absolute URL ALWAYS — even for a not-yet-saved agent — for off-browser copy.
  agentRunUrl.textContent = `${location.origin}/api/agent-run`;
  agentRunUrlCopyBtn.textContent = "Copy"; // reset a stale "Copied" from a prior open (button is always visible)
  // Same reasoning as the run endpoint above: agent-independent and not a secret, so shown always.
  agentMcpUrl.textContent = `${location.origin}/api/agent-mcp`;
  agentMcpUrlCopyBtn.textContent = "Copy";
  pendingRegenerate = false;
  pendingEmailRegenerate = false;
  renderEmailAddress(agent);
  // Null enabledTools (a new agent, or one saved before ADR 0021) means "all selectable tools".
  renderToolChecks(agentToolChecks, agent?.enabledTools ?? allToolKeys());
  fillModelOptions(agentModelSelect);
  // Preselect the agent's model when editing, otherwise the server default. Uses the same helper as
  // the composer picker rather than a second copy of the append-if-missing logic.
  selectModel(agentModelSelect, agent?.model ?? defaultModel);
  // The webhook tab is gated on a saved agent, like the key section (ADR 0094).
  openWebhookPanel(agent?.id ?? null);
  agentTabs.select(0); // always open on the General tab, never a stale Tools tab
  agentDialog.showModal();
  agentNameInput.focus();
}

// A research or copilot agent is per-chat only. Research turns are the largest there are (ADR 0082), a copilot chat's
// transcript lives outside the grain, and a continuous chat is one 2 MB grain (ADR 0016). The server forces the flag
// off whatever is sent; the lock says so before a save does. Runs on open and when the create-time type changes.
function applyKindLock(kind) {
  const perChatOnly = kind === "research" || kind === "copilot";
  agentContinuousInput.disabled = perChatOnly;
  if (perChatOnly) agentContinuousInput.checked = false;
  agentContinuousNote.textContent = perChatOnly
    ? `A ${kind} agent always starts a fresh chat: its history can't be shared across chats.`
    : CONTINUOUS_NOTE;
}

// Set by the Regenerate-key button so the next save rotates the key (ADR 0022). Reset on every
// dialog open and after each save, so a plain Save never regenerates.
let pendingRegenerate = false;

// Set by the Regenerate-address button so the next save rotates the inbound email address (ADR 0095). Reset like
// pendingRegenerate, so a plain Save never rotates it.
let pendingEmailRegenerate = false;

// GET /api/email/config, fetched once: whether inbound email is on, and the domain an agent's token is shown at.
let emailConfig = null;

async function loadEmailConfig() {
  if (emailConfig) return emailConfig;
  try {
    const res = await fetch("/api/email/config");
    if (res.ok) emailConfig = await res.json();
  } catch { /* offline: leave it unknown and try again on the next open */ }
  return emailConfig ?? { enabled: false, domain: "" };
}

// The address is shown persistently for a saved agent (it is stored raw, ADR 0095), never revealed-once like the key.
async function renderEmailAddress(agent) {
  agentEmailSection.hidden = !agent;
  agentEmailCopyBtn.textContent = "Copy";
  if (!agent) return;
  const config = await loadEmailConfig();
  const address = config.enabled && agent.emailToken ? `${agent.emailToken}@${config.domain}` : "";
  agentEmailValue.textContent = address
    || (config.enabled ? "No address yet. Regenerate to create one." : "Inbound email isn't set up on this server.");
  agentEmailCopyBtn.hidden = !address;
  agentRegenerateEmailBtn.hidden = !config.enabled;
  agentEmailNote.hidden = !config.enabled;
}

// What the MCP-exposure box read when the dialog was opened, so a save can detect the OFF -> ON
// transition (ADR 0070) and offer a key rotation for it.
let mcpExposedWhenOpened = false;

async function saveAgent() {
  const payload = {
    name: agentNameInput.value.trim(),
    description: agentDescriptionInput.value.trim(),
    model: agentModelSelect.value,
    systemPrompt: agentSystemPromptInput.value.trim(),
    continuous: agentContinuousInput.checked,
    mcpExposed: agentMcpExposedInput.checked,
  };
  // The type rides a create only; the server ignores it on an update.
  if (!editingAgentId && agentKindSelect.value) payload.kind = agentKindSelect.value;
  // ADR 0070's headline consequence, surfaced at the one moment the user can act on it. The ADR 0022 key
  // was minted under a WRITE-ONLY contract — it could push a prompt in and read nothing back. Exposing the
  // agent hands the reply out, which can carry RAG excerpts, the user's street address and prior turns of a
  // reused conversation. So a key handed to a cron job months ago silently becomes a read credential unless
  // it is rotated, and nothing else in the UI would ever mention that. Only on an EXISTING agent: a create
  // mints a fresh key anyway, so there is nothing to re-consent to.
  if (editingAgentId && agentMcpExposedInput.checked && !mcpExposedWhenOpened
      && confirm("Exposing this agent lets anyone holding its API key read its replies, not just send it prompts. Regenerate the key now so the old one stops working?")) {
    pendingRegenerate = true;
  }
  // The full explicit list (never null) — "all selected" is the whole list, per ADR 0021. But ONLY
  // when the catalog actually loaded: if /api/tools failed, toolCatalog is [] and readToolChecks would
  // return [] (= none), silently disabling every tool. Omit the field so the server keeps the default
  // (a create then stores null == all) rather than storing an accidental empty set.
  if (toolCatalog.length > 0) payload.enabledTools = readToolChecks(agentToolChecks);
  // Regenerate only on an existing agent (PUT) — a create always mints a fresh key anyway.
  if (editingAgentId && pendingRegenerate) payload.regenerateKey = true;
  if (editingAgentId && pendingEmailRegenerate) payload.regenerateEmail = true;
  if (!payload.name || !payload.systemPrompt || !payload.model) return;

  const url = editingAgentId ? `/api/agents/${encodeURIComponent(editingAgentId)}` : "/api/agents";
  const method = editingAgentId ? "PUT" : "POST";
  let body;
  try {
    const res = await fetch(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) return;
    body = await res.json().catch(() => null);
  } catch { return; /* silo may be unreachable; leave the dialog open */ }

  pendingRegenerate = false;
  pendingEmailRegenerate = false;
  // The save succeeded, so this IS the state now — without adopting it, a dialog that stays open (the
  // key-reveal path below) would re-prompt for a rotation on every subsequent Save.
  mcpExposedWhenOpened = payload.mcpExposed;
  loadAgents();

  // The response is { agent, apiKey }. A non-null apiKey means one was just minted (create, or a
  // regenerate) — reveal it ONCE and keep the dialog open (now in edit mode) so the user can copy it;
  // it is never returned again. Otherwise this was a plain save, so just close.
  if (body && body.apiKey) {
    editingAgentId = body.agent.id;
    agentFormTitle.textContent = "Edit agent";
    agentTabs.select(2); // the key lives in the API-key panel — show it even if Save came from another tab
    agentKeySection.hidden = false;
    agentKeyValue.textContent = body.apiKey;
    agentKeyReveal.hidden = false;
    agentKeyCopyBtn.textContent = "Copy";
    // A new agent has its id now, so its webhook tab can open up.
    openWebhookPanel(body.agent.id);
    renderEmailAddress(body.agent);
  } else if (body && payload.regenerateEmail) {
    // Stay open on the API tab so the new address can be copied; the old one is already dead.
    agentTabs.select(2);
    renderEmailAddress(body.agent);
  } else {
    agentDialog.close();
  }
}

async function deleteAgent(id, name) {
  if (!confirm(`Delete agent "${name}"? Existing chats keep their persona.`)) return;
  try {
    const res = await fetch(`/api/agents/${encodeURIComponent(id)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; /* silo may be unreachable; leave the row in place */ }

  agents = agents.filter((a) => a.id !== id);
  renderAgents();
  loadAgents();
}

// In an agent chat the model is fixed to the agent's, so the picker is replaced by a
// static "agent · model" label; a normal chat shows the live picker.
export function lockPicker(name, model) {
  modelField.hidden = true;
  agentLabel.hidden = false;
  agentLabel.textContent = `⛓ ${name} · ${modelLabel(model)}`;
  // An agent chat's tools come from its bind snapshot (ADR 0021), so the per-chat popover is hidden —
  // a PUT before the first agent turn would be clobbered by the bind anyway. Incognito is likewise a
  // regular-chat-only affordance (ADR 0034), so it hides under the same lock.
  toolsBtn.hidden = true;
  incognitoBtn.hidden = true;
}

export function unlockPicker() {
  modelField.hidden = false;
  agentLabel.hidden = true;
  toolsBtn.hidden = false;
  incognitoBtn.hidden = false;
}

// Reflect the incognito flag onto the toggle and the empty-state hint (ADR 0034). `interactive` is
// false once a chat has a message or history — ephemerality is fixed at creation, so the button locks.
export function setIncognito(on, interactive) {
  ephemeral = on;
  incognitoBtn.setAttribute("aria-pressed", on ? "true" : "false");
  incognitoBtn.classList.toggle("is-active", on);
  if (interactive !== undefined) incognitoBtn.disabled = !interactive;
  document.body.classList.toggle("is-incognito", on);
}

export function wireAgents() {
  // Flip incognito for the fresh chat (ADR 0034). The button is disabled once a chat has started, so
  // this only ever fires while the flag is still settable.
  incognitoBtn.addEventListener("click", () => setIncognito(!ephemeral));
  addAgentBtn.addEventListener("click", () => openAgentDialog(null));
  agentKindSelect.addEventListener("change", () => applyKindLock(agentKindSelect.value || null));
  agentCancelBtn.addEventListener("click", () => agentDialog.close());
  agentForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveAgent();
  });

  // Regenerate the agent's API key (ADR 0022): confirm (the current key stops working), then save with
  // the regenerate flag so the new key comes back and gets revealed once.
  agentRegenerateKeyBtn.addEventListener("click", () => {
    if (!editingAgentId) return;
    if (!confirm("Regenerate this agent's API key? The current key stops working immediately.")) return;
    pendingRegenerate = true;
    saveAgent();
  });

  // Regenerate the inbound email address (ADR 0095): the current address stops working, including in any
  // forwarding rule that points at it.
  agentRegenerateEmailBtn.addEventListener("click", () => {
    if (!editingAgentId) return;
    if (!confirm("Regenerate this agent's email address? The current address stops working immediately, including in any forwarding rule.")) return;
    pendingEmailRegenerate = true;
    saveAgent();
  });

  agentEmailCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentEmailValue.textContent);
      agentEmailCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked — the address is visible to select manually */
    }
  });

  agentKeyCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentKeyValue.textContent);
      agentKeyCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked (insecure context / permission) — the key is visible to select manually */
    }
  });

  agentRunUrlCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentRunUrl.textContent);
      agentRunUrlCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked (insecure context / permission) — the URL is visible to select manually */
    }
  });

  agentMcpUrlCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentMcpUrl.textContent);
      agentMcpUrlCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked — the URL is visible to select manually */
    }
  });
}

export function setAgents(value) {
  agents = value;
}
