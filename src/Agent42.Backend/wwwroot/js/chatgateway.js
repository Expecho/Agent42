/* Agent 42 — chat apps (ADR 0111). Mirrors mcp.js: a per-user list of bots in Preferences, each edited in a dialog.

   The bot token is write-only: the server only ever says whether one is set, so the token field is always empty when
   the dialog opens and is sent only when the user typed a new one. Slack takes two tokens (bot + app-level). Typing
   either sends both fields; on an edit the server keeps the stored token for a field left blank, then validates and
   stores the pair as one credential. After a token is saved the server opens a pairing
   window, and the dialog stays open to show the code the owner sends to their bot.

   Signal (phase 3) has no token: once the connection exists, the dialog registers a spare number in the gateway's
   signal-cli (number → code by text or call → verify), and the server refuses a pasted Signal credential. */

import { agents } from "./agents.js";
import { addChatConnectionBtn, chatAppTokenField, chatAppTokenInput, chatBotTokenField, chatBotTokenInput, chatSignalCaptcha, chatSignalCode, chatSignalNote, chatSignalNumber, chatSignalPin, chatSignalRegister, chatSignalRegisterBtn, chatSignalSection, chatSignalVerify, chatSignalVerifyBtn, chatSignalVerifyText, chatSignalVoice, chatCancelBtn, chatConnectionList, chatDeleteBtn, chatDialog, chatEnabledInput, chatError, chatGroupsInput, chatNotificationsInput, chatVoiceNotesInput, chatForm, chatFormTitle, chatNameInput, chatPairingCode, chatPairingCodeBox, chatPairingCopyBtn, chatPairingSection, chatPairingText, chatPlatformSelect, chatRepairBtn, chatSetupNote, chatTargetSelect } from "./dom.js";

let platforms = [];
let connections = [];
let editing = null; // the connection open in the dialog, or null for a new one

// How to get a token, per platform. Only platforms the server reports are offered.
const setupNotes = {
  Telegram: "In Telegram, message @BotFather, send /newbot and follow the steps. Paste the token it gives you (it looks like 123456789:ABC…).",
  Slack: "Create a Slack app from the manifest in the setup guide (docs/integrations/chat-apps), install it to your workspace, and paste its Bot User OAuth Token (xoxb-…) and an app-level token (xapp-…) with connections:write. Commands start with ! in Slack.",
  Discord: "In the Discord Developer Portal, create an application, open Bot, reset and copy the token, and paste it here. Invite the bot to a server you share, or add it to your apps, so you can DM it.",
  Signal: "Signal needs a spare number of your own (a SIM, a VoIP number, or a landline that can take a call). Use one that isn't on a phone with Signal: registering it here signs that phone out. Save, then register the number below.",
};

// Signal's plain-words egress note (ADR 0111 phase 3): what the operator's storage holds for a Signal bot.
const signalEgressNote = "Replies are end-to-end encrypted to your devices, but this bot number's keys and the messages waiting for it live in this Agent42's storage, so whoever runs this Agent42 could read what the bot receives. Your own Signal account is never involved.";

// Platforms registered here instead of pasted (the server refuses a pasted credential for them).
const registeredNotPasted = new Set(["Signal"]);

// Platforms whose credentials include Slack's app-level token.
const needsAppToken = new Set(["Slack"]);

// The server's status strings (ChatConnectionStatus) and how the list shows them.
const statusLabels = {
  "disabled": () => "off",
  "needs-credentials": () => "needs a bot token",
  "needs-registration": () => "needs a number registered",
  "awaiting-code": (c) => `waiting for the code sent to ${c.pendingSignalNumber}`,
  "awaiting-pairing": () => "waiting for the pairing code",
  "pairing-locked": () => "pairing expired: get a new code",
  "faulted": (c) => `stopped: ${c.faultReason}`,
  "connecting": () => "connecting…",
  "connected": (c) => `connected as @${c.botUserName}`,
};

export async function loadChatConnections() {
  try {
    const res = await fetch("/api/chat-connections");
    if (!res.ok) return;
    const data = await res.json();
    platforms = data.platforms ?? [];
    connections = data.connections ?? [];
    renderChatConnections();
  } catch { /* silo may be unreachable; the list refreshes on next open */ }
}

function renderChatConnections() {
  chatConnectionList.innerHTML = "";
  addChatConnectionBtn.disabled = platforms.length === 0;
  if (connections.length === 0) {
    const li = document.createElement("li");
    li.className = "providers__empty";
    li.textContent = "no bots connected";
    chatConnectionList.appendChild(li);
    return;
  }
  for (const connection of connections) {
    const li = document.createElement("li");
    li.className = "provider-row";

    const meta = document.createElement("div");
    meta.className = "provider-row__meta";
    const name = document.createElement("span");
    name.className = "provider-row__name";
    name.textContent = connection.name;
    const sub = document.createElement("span");
    sub.className = "provider-row__sub";
    sub.textContent = `${connection.platform} · ${statusText(connection)}`;
    meta.append(name, sub);

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "provider-row__action";
    edit.setAttribute("aria-label", `Edit bot: ${connection.name}`);
    edit.textContent = "✎";
    edit.addEventListener("click", () => openChatDialog(connection));

    li.append(meta, edit);
    chatConnectionList.appendChild(li);
  }
}

function statusText(connection) {
  const label = statusLabels[connection.status];
  return label ? label(connection) : connection.status;
}

function fillTargets(selected) {
  chatTargetSelect.innerHTML = "";
  const plain = new Option("Plain chat (your default model and tools)", "");
  chatTargetSelect.add(plain);
  for (const agent of agents) {
    chatTargetSelect.add(new Option(agent.name, agent.id));
  }
  chatTargetSelect.value = selected ?? "";
}

function fillPlatforms(selected) {
  chatPlatformSelect.innerHTML = "";
  for (const platform of platforms) {
    chatPlatformSelect.add(new Option(platform, platform));
  }
  chatPlatformSelect.value = selected ?? platforms[0] ?? "";
  chatPlatformSelect.disabled = editing !== null; // a bot belongs to one platform
  syncSetupNote();
}

function syncSetupNote() {
  chatSetupNote.textContent = setupNotes[chatPlatformSelect.value] ?? "";
  chatAppTokenField.hidden = !needsAppToken.has(chatPlatformSelect.value);
  chatBotTokenField.hidden = registeredNotPasted.has(chatPlatformSelect.value);
}

// Signal's registration steps: the number until a code was requested, then the code, then (credentials set) nothing.
function renderSignal(connection) {
  const signal = connection?.platform === "Signal";
  chatSignalSection.hidden = !signal || connection.hasCredentials;
  if (chatSignalSection.hidden) return;

  const awaitingCode = connection.status === "awaiting-code";
  chatSignalNote.textContent = signalEgressNote;
  chatSignalRegister.hidden = awaitingCode;
  chatSignalVerify.hidden = !awaitingCode;
  chatSignalVerifyText.textContent = awaitingCode
    ? `Signal sent a code to ${connection.pendingSignalNumber}. Enter it here. Wrong number or no code? Delete the bot and start again in a few minutes.`
    : "";
  chatSignalNumber.value = "";
  chatSignalCaptcha.value = "";
  chatSignalVoice.checked = false;
  chatSignalCode.value = "";
  chatSignalPin.value = "";
}

async function signalStep(path, body) {
  if (!editing) return;
  setError("");
  try {
    const res = await fetch(`/api/chat-connections/${encodeURIComponent(editing.id)}/signal/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      // Every refusal answers text/plain with a sentence written for the user (a captcha demand carries the link).
      setError(await res.text() || "Signal didn't accept that.");
      return;
    }
    const view = await res.json();
    await loadChatConnections();
    openChatDialog(view);
  } catch {
    setError("Couldn't reach Agent42. Try again.");
  }
}

function openChatDialog(connection) {
  editing = connection ?? null;
  chatFormTitle.textContent = connection ? "Edit bot" : "Connect a bot";
  fillPlatforms(connection?.platform);
  chatNameInput.value = connection?.name ?? "";
  chatBotTokenInput.value = "";
  chatBotTokenInput.placeholder = connection?.hasCredentials ? "leave blank to keep the stored token" : "paste the token";
  chatAppTokenInput.value = "";
  chatAppTokenInput.placeholder = connection?.hasCredentials ? "leave blank to keep the stored token" : "paste the xapp- token";
  fillTargets(connection?.targetAgentId);
  chatEnabledInput.checked = connection?.enabled ?? true;
  chatGroupsInput.checked = connection?.groupsEnabled ?? false;
  chatVoiceNotesInput.checked = connection?.voiceNotesEnabled ?? false;
  chatNotificationsInput.checked = connection?.notificationsEnabled ?? false;
  chatDeleteBtn.hidden = !connection;
  setError("");
  renderPairing(connection);
  renderSignal(connection);
  if (!chatDialog.open) chatDialog.showModal();
}

// Pairing is shown once a token is saved: the code while the window is open, or a way to open a new one.
function renderPairing(connection) {
  const status = connection?.status;
  chatPairingSection.hidden = !connection || !connection.hasCredentials;
  if (chatPairingSection.hidden) return;

  const awaiting = status === "awaiting-pairing";
  chatPairingCodeBox.hidden = !awaiting;
  chatPairingCode.textContent = awaiting ? connection.pairingCode : "";
  chatPairingText.textContent = awaiting
    ? `Open your bot in ${connection.platform} and send it this code as a message to pair it with your account. It expires at ${new Date(connection.pairingExpiresAt).toLocaleTimeString()}.`
    : status === "pairing-locked"
      ? "The pairing code expired or was entered wrong too often. Get a new one to pair the bot."
      : "Paired. Getting a new code un-pairs the bot until you send the new code.";
}

async function saveChatConnection() {
  const body = {
    platform: chatPlatformSelect.value,
    name: chatNameInput.value.trim(),
    targetAgentId: chatTargetSelect.value || null,
    enabled: chatEnabledInput.checked,
    groupsEnabled: chatGroupsInput.checked,
    voiceNotesEnabled: chatVoiceNotesInput.checked,
    notificationsEnabled: chatNotificationsInput.checked,
  };
  const token = registeredNotPasted.has(chatPlatformSelect.value) ? "" : chatBotTokenInput.value.trim();
  const appToken = needsAppToken.has(chatPlatformSelect.value) ? chatAppTokenInput.value.trim() : "";
  if (token || appToken) body.credentials = { botToken: token, appToken: appToken || null };

  setError("");
  try {
    const res = await fetch(editing ? `/api/chat-connections/${encodeURIComponent(editing.id)}` : "/api/chat-connections", {
      method: editing ? "PUT" : "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) {
      // 400/502 answer text/plain with a sentence written for the user.
      setError(await res.text() || "Could not save the bot.");
      return;
    }
    const saved = await res.json();
    await loadChatConnections();
    // A fresh token opens pairing: keep the dialog open on the code instead of closing it. Only for a token saved just
    // now, so renaming a bot that is still awaiting pairing closes the dialog like any other save.
    // A new Signal bot stays open too, on its registration steps.
    if ((body.credentials && saved.status === "awaiting-pairing") || (!editing && registeredNotPasted.has(saved.platform))) {
      openChatDialog(saved);
    } else {
      chatDialog.close();
    }
  } catch {
    setError("Could not save the bot.");
  }
}

async function repairChatConnection() {
  if (!editing) return;
  try {
    const res = await fetch(`/api/chat-connections/${encodeURIComponent(editing.id)}/repair`, { method: "POST" });
    if (!res.ok) {
      setError("Could not get a new pairing code.");
      return;
    }
    const repaired = await res.json();
    await loadChatConnections();
    openChatDialog(repaired);
  } catch {
    setError("Could not get a new pairing code.");
  }
}

async function deleteChatConnection() {
  if (!editing || !confirm(`Disconnect the bot "${editing.name}"? Its conversations stay in your chat list.`)) return;
  try {
    const res = await fetch(`/api/chat-connections/${encodeURIComponent(editing.id)}`, { method: "DELETE" });
    if (!res.ok && res.status !== 404) {
      setError("Could not delete the bot.");
      return;
    }
    chatDialog.close();
    await loadChatConnections();
  } catch {
    setError("Could not delete the bot.");
  }
}

function setError(text) {
  chatError.textContent = text;
  chatError.hidden = !text;
}

export function wireChatGateway() {
  addChatConnectionBtn.addEventListener("click", () => openChatDialog(null));
  chatPlatformSelect.addEventListener("change", syncSetupNote);
  chatCancelBtn.addEventListener("click", () => chatDialog.close());
  chatRepairBtn.addEventListener("click", repairChatConnection);
  chatDeleteBtn.addEventListener("click", deleteChatConnection);
  chatSignalRegisterBtn.addEventListener("click", () => signalStep("register", {
    number: chatSignalNumber.value.trim(),
    captcha: chatSignalCaptcha.value.trim() || null,
    voice: chatSignalVoice.checked,
  }));
  chatSignalVerifyBtn.addEventListener("click", () => signalStep("verify", {
    code: chatSignalCode.value.trim(),
    pin: chatSignalPin.value || null,
  }));
  chatPairingCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(chatPairingCode.textContent);
    } catch { /* the code is visible and selectable */ }
  });
  chatForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveChatConnection();
  });
  // Pairing happens in the chat app while this dialog is open: closing it refreshes the list, so "connected" shows.
  chatDialog.addEventListener("close", () => loadChatConnections());
}
