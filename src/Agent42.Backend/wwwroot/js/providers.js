/* Agent 42 — model providers, per-user overlay (ADR 0018/0030/0038). Split out of app.js (ADR 0041). */

import { addModelRowBtn, addProviderBtn, defaultModelSelect, githubSignInBtn, githubSignInNote, embeddingModelSelect, providerApiKeyInput, providerCancelBtn, providerDialog, providerDisplayNameInput, providerEndpointInput, providerError, providerForm, providerFormTitle, providerKeyInput, providerKindSelect, providerList, providerModelList, providerAnthropicNote, providerResponsesApi, profileTabs, providerResponsesField, speechModelSelect, transcriptionModelSelect } from "./dom.js";
import { fillModelOptions, loadModels, modelsData, providerGroupLabel, selectModel } from "./models.js";
import { enterPrefsDetail, openProfileDialog } from "./profile.js";

// The user's own configured providers (their overlay — ADR 0018), fetched masked (hasKey, never the
// key). Rendered in Preferences; edited through the provider dialog. The key of the one being edited,
// or null when adding a new provider.
let userProviders = [];
let editingProviderKey = null;
// The user's STORED default-model preference (GET /api/providers `default`), as opposed to `defaultModel`
// above, which is the default the server actually RESOLVED (/api/models). They differ when the stored id
// names a provider that no longer resolves — the resolver falls through, but the preference is kept.
let userDefaultModel = null;
// The user's default embedding model id, or null when none is configured. When null, attaching documents
// is blocked in the composer (RAG needs an embedding model). Independent of userDefaultModel (chat).
export let userDefaultEmbeddingModel = null;
// The user's default speech (TTS) model id, or null when none is configured (ADR 0038). When set, the
// hands-free voice conversation (ADR 0037) speaks its reply via server-side /api/speech; when null it
// falls back to the browser's speechSynthesis. Independent of userDefaultModel (chat).
export let userDefaultSpeechModel = null;
// The user's default transcription (speech-to-text) model id, or null when none is configured (ADR 0113). The chat
// gateway transcribes a voice note with it; without one the bot answers a voice note with a curated reply.
export let userDefaultTranscriptionModel = null;
// Whether "Sign in with GitHub" (the GitHub Copilot provider, ADR 0108) is configured on this server.
let gitHubSignIn = false;
// The GitHub Copilot provider: written only by the sign-in callback, so it gets no edit button (the API refuses it).
const GITHUB_COPILOT_KIND = "GitHubCopilot";
export async function loadProviders() {
  try {
    const res = await fetch("/api/providers");
    if (!res.ok) return;
    const data = await res.json();
    userProviders = data.providers ?? [];
    userDefaultModel = data.default ?? null;
    userDefaultEmbeddingModel = data.embeddingDefault ?? null;
    userDefaultSpeechModel = data.speechDefault ?? null;
    userDefaultTranscriptionModel = data.transcriptionDefault ?? null;
    gitHubSignIn = data.gitHubSignIn === true;
    renderProviders();
    syncGitHubSignIn();
    syncDefaultModelSelect();
    syncEmbeddingModelSelect();
    syncSpeechModelSelect();
    syncTranscriptionModelSelect();
  } catch { /* silo may be unreachable; the list refreshes on next open */ }
}

// Repaints the default-model select from the CURRENT modelsData + stored preference. It is an idempotent
// repaint hooked into BOTH of the two loaders, because each owns only half the inputs and they are called
// in different combinations: preferences-open runs loadProviders() alone (which owns the stored value),
// deleteProvider() runs loadModels() alone (which owns the catalog), and saveProvider() runs both. Hooking
// either one alone would leave the select stale on the other's path. Reuses fillModelOptions/selectModel —
// a stored default whose provider is gone shows under "Unavailable" rather than silently resetting.
export function syncDefaultModelSelect() {
  fillModelOptions(defaultModelSelect);
  const blank = document.createElement("option");
  blank.value = "";
  blank.textContent = "First available";
  defaultModelSelect.insertBefore(blank, defaultModelSelect.firstChild);
  defaultModelSelect.value = "";
  selectModel(defaultModelSelect, userDefaultModel);
}

// Persists the pick immediately (there is no Save button on this tab — the value lives in the provider
// overlay grain, not the profile). A blank selection clears the preference back to "first available".
// A failed save snaps the select BACK to the stored value: with no Save button there is no other signal,
// and leaving the rejected pick on screen would read as saved.
async function persistDefaultModel() {
  const value = defaultModelSelect.value;
  try {
    const res = await fetch("/api/default-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ default: value || null }),
    });
    if (!res.ok) return syncDefaultModelSelect();
  } catch { return syncDefaultModelSelect(); /* silo unreachable — don't show an unsaved value */ }
  userDefaultModel = value || null;
  loadModels({ pin: false }); // refresh the resolved default WITHOUT retargeting the open conversation
}

// Repaints the embedding-model select from the models a provider marked as embedding models, plus a leading
// "None" option. Hooked into loadProviders() (owns the stored value) and loadModels() (owns the catalog),
// same idempotent-repaint reasoning as syncDefaultModelSelect(). A stored id whose model is gone shows under
// "Unavailable" via selectModel rather than silently resetting.
export function syncEmbeddingModelSelect() {
  if (!embeddingModelSelect) return;
  embeddingModelSelect.innerHTML = "";
  const none = document.createElement("option");
  none.value = "";
  none.textContent = "None (document Q&A off)";
  embeddingModelSelect.appendChild(none);

  const groups = new Map();
  for (const m of modelsData.models ?? []) {
    if (!m.supportsEmbeddings) continue; // only embedding-capable models are pickable here
    const provider = m.provider ?? "";
    if (!groups.has(provider)) {
      const group = document.createElement("optgroup");
      group.label = providerGroupLabel(m);
      groups.set(provider, group);
      embeddingModelSelect.appendChild(group);
    }
    const opt = document.createElement("option");
    opt.value = m.id;
    opt.textContent = m.displayName ?? m.id;
    groups.get(provider).appendChild(opt);
  }

  embeddingModelSelect.value = "";
  selectModel(embeddingModelSelect, userDefaultEmbeddingModel);
}

// Persists the embedding-model pick immediately (no Save button — the value lives in the provider overlay
// grain). Blank clears it (document Q&A off). A failed save snaps the select back to the stored value.
async function persistEmbeddingModel() {
  const value = embeddingModelSelect.value;
  try {
    const res = await fetch("/api/default-embedding-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ embeddingDefault: value || null }),
    });
    if (!res.ok) return syncEmbeddingModelSelect();
  } catch { return syncEmbeddingModelSelect(); /* silo unreachable — don't show an unsaved value */ }
  userDefaultEmbeddingModel = value || null;
}

// Repaints the speech-model select from the models a provider marked as speech models, plus a leading
// "None" option (ADR 0038). Same idempotent-repaint reasoning as syncEmbeddingModelSelect(); a stored id
// whose model is gone shows under "Unavailable" via selectModel rather than silently resetting.
export function syncSpeechModelSelect() {
  if (!speechModelSelect) return;
  speechModelSelect.innerHTML = "";
  const none = document.createElement("option");
  none.value = "";
  none.textContent = "None (browser voice)";
  speechModelSelect.appendChild(none);

  const groups = new Map();
  for (const m of modelsData.models ?? []) {
    if (!m.supportsSpeech) continue; // only speech-capable models are pickable here
    const provider = m.provider ?? "";
    if (!groups.has(provider)) {
      const group = document.createElement("optgroup");
      group.label = providerGroupLabel(m);
      groups.set(provider, group);
      speechModelSelect.appendChild(group);
    }
    const opt = document.createElement("option");
    opt.value = m.id;
    opt.textContent = m.displayName ?? m.id;
    groups.get(provider).appendChild(opt);
  }

  speechModelSelect.value = "";
  selectModel(speechModelSelect, userDefaultSpeechModel);
}

// Persists the speech-model pick immediately (no Save button — the value lives in the provider overlay
// grain). Blank clears it (browser voice). A failed save snaps the select back to the stored value.
async function persistSpeechModel() {
  const value = speechModelSelect.value;
  try {
    const res = await fetch("/api/default-speech-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ speechDefault: value || null }),
    });
    if (!res.ok) return syncSpeechModelSelect();
  } catch { return syncSpeechModelSelect(); /* silo unreachable — don't show an unsaved value */ }
  userDefaultSpeechModel = value || null;
}

// Repaints the transcription-model select from the models a provider marked as transcription models, plus a leading
// "None" option (ADR 0113). Same idempotent-repaint reasoning as syncSpeechModelSelect().
export function syncTranscriptionModelSelect() {
  if (!transcriptionModelSelect) return;
  transcriptionModelSelect.innerHTML = "";
  const none = document.createElement("option");
  none.value = "";
  none.textContent = "None (voice notes are not transcribed)";
  transcriptionModelSelect.appendChild(none);

  const groups = new Map();
  for (const m of modelsData.models ?? []) {
    if (!m.supportsTranscription) continue; // only transcription-capable models are pickable here
    const provider = m.provider ?? "";
    if (!groups.has(provider)) {
      const group = document.createElement("optgroup");
      group.label = providerGroupLabel(m);
      groups.set(provider, group);
      transcriptionModelSelect.appendChild(group);
    }
    const opt = document.createElement("option");
    opt.value = m.id;
    opt.textContent = m.displayName ?? m.id;
    groups.get(provider).appendChild(opt);
  }

  transcriptionModelSelect.value = "";
  selectModel(transcriptionModelSelect, userDefaultTranscriptionModel);
}

// Persists the transcription-model pick immediately (no Save button). Blank clears it. A failed save snaps the
// select back to the stored value.
async function persistTranscriptionModel() {
  const value = transcriptionModelSelect.value;
  try {
    const res = await fetch("/api/default-transcription-model", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ transcriptionDefault: value || null }),
    });
    if (!res.ok) return syncTranscriptionModelSelect();
  } catch { return syncTranscriptionModelSelect(); /* silo unreachable — don't show an unsaved value */ }
  userDefaultTranscriptionModel = value || null;
}

function renderProviders() {
  providerList.innerHTML = "";
  if (userProviders.length === 0) {
    const li = document.createElement("li");
    li.className = "providers__empty";
    li.textContent = "no providers added";
    providerList.appendChild(li);
    return;
  }
  for (const provider of userProviders) {
    const li = document.createElement("li");
    li.className = "provider-row";

    const meta = document.createElement("div");
    meta.className = "provider-row__meta";
    const name = document.createElement("span");
    name.className = "provider-row__name";
    name.textContent = provider.displayName || provider.key;
    const sub = document.createElement("span");
    sub.className = "provider-row__sub";
    // Endpoint plus a lock glyph when a key is stored — never the key itself (it is never returned).
    const github = provider.kind === GITHUB_COPILOT_KIND;
    sub.textContent = github
      ? "your GitHub account · Copilot agents only"
      : `${provider.key} · ${provider.endpoint ?? ""}${provider.hasKey ? " · 🔑" : ""}`;
    sub.title = provider.endpoint ?? "";
    meta.append(name, sub);

    const edit = document.createElement("button");
    edit.type = "button";
    edit.className = "provider-row__action";
    edit.setAttribute("aria-label", `Edit provider: ${provider.key}`);
    edit.textContent = "✎";
    edit.addEventListener("click", () => openProviderDialog(provider));

    const del = document.createElement("button");
    del.type = "button";
    del.className = "provider-row__action";
    del.setAttribute("aria-label", github ? "Disconnect GitHub" : `Delete provider: ${provider.key}`);
    del.textContent = "🗑";
    del.addEventListener("click", () => deleteProvider(provider.key));

    if (github) li.append(meta, del);
    else li.append(meta, edit, del);
    providerList.appendChild(li);
  }
}
// The sign-in button shows only when the server can run the flow and the account isn't connected yet; the note
// explains the add-on (an ordinary provider is still needed, ADR 0108) whenever either is the case.
function syncGitHubSignIn() {
  const connected = userProviders.some((p) => p.kind === GITHUB_COPILOT_KIND);
  githubSignInBtn.hidden = !gitHubSignIn || connected;
  githubSignInNote.hidden = !gitHubSignIn && !connected;
}

// Run at init: the GitHub sign-in callback redirects back to /?github=connected|error. Strip the param (so a reload
// isn't a re-trigger) and open Preferences → Model providers so the new row shows.
export async function initGitHubSignInReturn() {
  const params = new URLSearchParams(location.search);
  const result = params.get("github");
  if (!result) return;
  history.replaceState(null, "", location.pathname);
  if (result === "error") {
    alert(`Could not sign in with GitHub: ${params.get("reason") || "unknown error"}`);
  }
  await openProfileDialog();
  profileTabs.select(2); // the Model providers tab
  enterPrefsDetail(2);
  if (result === "connected") loadModels({ pin: false }); // the plan's models join the Copilot pickers
}

const ANTHROPIC_ENDPOINT = "https://api.anthropic.com";

// The Responses-API toggle is Foundry-only (ADR 0014) — an OpenAI-compatible provider ignores it.
// Anthropic (ADR 0103) pre-fills its API base, but only into an EMPTY endpoint: a value the user typed
// (or a stored one being edited) is never overwritten.
function syncProviderKindUi() {
  const kind = providerKindSelect.value;
  providerResponsesField.hidden = kind !== "AzureOpenAI";
  providerAnthropicNote.hidden = kind !== "Anthropic";
  if (kind === "Anthropic" && !providerEndpointInput.value.trim()) {
    providerEndpointInput.value = ANTHROPIC_ENDPOINT;
  }
}

// Appends one editable model row. tools/vision gate what is sent to the model (ADR 0013), so they
// default OFF for a new row — the unsafe direction is a wrong "true".
function addModelRow(model) {
  const li = document.createElement("li");
  li.className = "provider-model-row";

  const id = document.createElement("input");
  id.type = "text";
  id.className = "agent-form__input provider-model-row__id";
  id.placeholder = "model id (e.g. llama3.1)";
  id.value = model?.id ?? "";

  const display = document.createElement("input");
  display.type = "text";
  display.className = "agent-form__input provider-model-row__display";
  display.placeholder = "display name (optional)";
  display.value = model?.displayName ?? "";

  // Capabilities as a compact dropdown (a <details> disclosure) rather than four inline checkboxes, so a
  // model row stays short. Order is load-bearing: collectModels reads the .agent-form__checkbox inputs
  // positionally (tools, vision, image gen, embeddings), so keep this list in that order.
  // image gen (ADR 0020) turns a turn into an image; embeddings marks a document-RAG model (ADR 0030).
  const caps = document.createElement("details");
  caps.className = "provider-model-row__caps";
  const capsSummary = document.createElement("summary");
  capsSummary.className = "provider-model-row__caps-summary";
  caps.appendChild(capsSummary);

  const capsBody = document.createElement("div");
  capsBody.className = "provider-model-row__caps-body";
  const capDefs = [
    { label: "tools", checked: model?.supportsTools ?? false },
    { label: "vision", checked: model?.supportsVision ?? false },
    { label: "image gen", checked: model?.supportsImageGeneration ?? false },
    { label: "embeddings", checked: model?.supportsEmbeddings ?? false },
    { label: "speech", checked: model?.supportsSpeech ?? false },
    { label: "reasoning summary", checked: model?.supportsReasoning ?? false },
    { label: "transcription", checked: model?.supportsTranscription ?? false },
  ];
  const capBoxes = [];
  const updateCapsSummary = () => {
    const on = capBoxes.filter((c) => c.box.checked).map((c) => c.label);
    capsSummary.textContent = on.length ? on.join(", ") : "capabilities";
  };
  for (const def of capDefs) {
    const flag = document.createElement("label");
    flag.className = "provider-model-row__flag";
    const box = document.createElement("input");
    box.type = "checkbox";
    box.className = "agent-form__checkbox";
    box.checked = def.checked;
    box.addEventListener("change", updateCapsSummary);
    flag.append(box, document.createTextNode(def.label));
    capsBody.appendChild(flag);
    capBoxes.push({ box, label: def.label });
  }
  caps.appendChild(capsBody);
  updateCapsSummary();
  // Close the dropdown on an outside click, like a normal menu.
  caps.addEventListener("toggle", () => {
    if (!caps.open) return;
    const onDocClick = (e) => {
      if (!caps.contains(e.target)) { caps.open = false; document.removeEventListener("click", onDocClick); }
    };
    document.addEventListener("click", onDocClick);
  });

  const remove = document.createElement("button");
  remove.type = "button";
  remove.className = "provider-model-row__remove";
  remove.setAttribute("aria-label", "Remove model");
  remove.textContent = "×";
  remove.addEventListener("click", () => li.remove());

  li.append(id, display, caps, remove);
  providerModelList.appendChild(li);
}

function collectModels() {
  const models = [];
  for (const row of providerModelList.querySelectorAll(".provider-model-row")) {
    const id = row.querySelector(".provider-model-row__id").value.trim();
    if (!id) continue; // an empty row is dropped, not sent
    // POSITIONAL, and paired by ORDER with capDefs above — a flag added there must be added here too,
    // in the same position, or every flag after the insertion point silently reads the wrong checkbox.
    const [tools, vision, image, embed, speech, reasoning, transcription] =
      row.querySelectorAll(".agent-form__checkbox");
    const display = row.querySelector(".provider-model-row__display").value.trim();
    models.push({
      id,
      displayName: display || null,
      supportsTools: tools.checked,
      supportsVision: vision.checked,
      supportsImageGeneration: image.checked,
      supportsEmbeddings: embed.checked,
      supportsSpeech: speech.checked,
      supportsReasoning: reasoning.checked,
      supportsTranscription: transcription.checked,
    });
  }
  return models;
}

function openProviderDialog(provider) {
  editingProviderKey = provider?.key ?? null;
  providerFormTitle.textContent = provider ? "Edit provider" : "New provider";
  providerError.hidden = true;
  providerKeyInput.value = provider?.key ?? "";
  // The key is the identity — changing it would be a new provider, so it is read-only when editing.
  providerKeyInput.readOnly = Boolean(provider);
  providerKindSelect.value = provider?.kind ?? "OpenAICompatible";
  providerDisplayNameInput.value = provider?.displayName ?? "";
  providerEndpointInput.value = provider?.endpoint ?? "";
  providerApiKeyInput.value = ""; // never prefilled — the key is write-only (blank = keep on edit)
  providerResponsesApi.checked = provider?.useResponsesApi ?? false;
  syncProviderKindUi();

  providerModelList.innerHTML = "";
  for (const model of provider?.models ?? []) addModelRow(model);

  providerDialog.showModal();
  providerKeyInput.focus();
}

async function saveProvider() {
  const payload = {
    key: providerKeyInput.value.trim(),
    kind: providerKindSelect.value,
    displayName: providerDisplayNameInput.value.trim() || null,
    endpoint: providerEndpointInput.value.trim(),
    // Blank means "keep the stored key" on edit, or "no key" on create — the server maps both.
    apiKey: providerApiKeyInput.value,
    models: collectModels(),
    useResponsesApi: providerResponsesApi.checked,
  };
  if (!payload.key || !payload.endpoint) return;

  const url = editingProviderKey
    ? `/api/providers/${encodeURIComponent(editingProviderKey)}`
    : "/api/providers";
  const method = editingProviderKey ? "PUT" : "POST";
  try {
    const res = await fetch(url, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(payload),
    });
    if (!res.ok) {
      // The endpoint returns a plain-text reason for a 400 (bad endpoint/kind) — surface it.
      providerError.textContent = (await res.text()) || "Could not save the provider.";
      providerError.hidden = false;
      return;
    }
  } catch { return; /* silo may be unreachable; leave the dialog open */ }

  providerDialog.close();
  await loadProviders();
  loadModels(); // the picker reflects the change immediately — no restart (ADR 0018)
}

async function deleteProvider(key) {
  try {
    const res = await fetch(`/api/providers/${encodeURIComponent(key)}`, { method: "DELETE" });
    if (!res.ok) return;
  } catch { return; }
  userProviders = userProviders.filter((p) => p.key !== key);
  renderProviders();
  loadModels();
}

export function wireProviders() {
  defaultModelSelect.addEventListener("change", persistDefaultModel);
  if (embeddingModelSelect) embeddingModelSelect.addEventListener("change", persistEmbeddingModel);
  if (speechModelSelect) speechModelSelect.addEventListener("change", persistSpeechModel);
  if (transcriptionModelSelect) transcriptionModelSelect.addEventListener("change", persistTranscriptionModel);
  addProviderBtn.addEventListener("click", () => openProviderDialog(null));
  githubSignInBtn.addEventListener("click", () => { location.href = "/api/providers/github/start"; });
  addModelRowBtn.addEventListener("click", () => addModelRow(null));
  providerKindSelect.addEventListener("change", syncProviderKindUi);
  providerCancelBtn.addEventListener("click", () => providerDialog.close());
  providerForm.addEventListener("submit", (e) => {
    e.preventDefault();
    saveProvider();
  });
}
