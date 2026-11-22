/* Agent 42 — model picker (ADR 0013/0018). Split out of app.js (ADR 0041). */

import { input, modelSelect, noModelHint } from "./dom.js";
import { syncDefaultModelSelect, syncEmbeddingModelSelect, syncSpeechModelSelect, syncTranscriptionModelSelect } from "./providers.js";
import { streaming } from "./send.js";

// The reserved picker value for the Auto router (ADR 0137). It exists only on the wire and in the composer picker; the server
// answers it with a real model, and it is offered only while /api/models says the user has a router (`auto: true`).
export const AUTO_MODEL = "auto";

export let defaultModel = null;
export let modelsData = { models: [], default: null };
// Live-queried from Azure AI Foundry: the deployments the agent can run on.
//
// pin:false skips re-pointing the composer at the default. The default-model preference handler needs
// the refreshed catalog but must NOT retarget the conversation the user currently has open — send()
// transmits modelSelect.value, so re-pinning here would silently move an open chat onto another model.
export async function loadModels({ pin = true } = {}) {
  try {
    const res = await fetch("/api/models");
    if (!res.ok) return;
    modelsData = await res.json();
    defaultModel = modelsData.default ?? null;

    // The refill resets the select to its first option, which is Auto when the user has a router (ADR 0137). A refresh that must
    // not retarget the open chat (pin:false) therefore puts the previous choice back, so it can never silently move onto Auto.
    const kept = modelSelect.value;
    fillModelOptions(modelSelect, { auto: true });
    // Auto as the default (ADR 0139): the server sets autoDefault only while the router still exists, so this can't pin a stale Auto.
    if (pin && modelsData.autoDefault && modelsData.auto) setModel(AUTO_MODEL);
    else if (pin && defaultModel) setModel(defaultModel);
    else if (kept) setModel(kept);
    modelSelect.disabled = modelSelect.options.length === 0;
    applyModelAvailability();
    syncDefaultModelSelect();
    syncEmbeddingModelSelect();
    syncSpeechModelSelect();
    syncTranscriptionModelSelect();
  } catch { /* leave the picker disabled; chat still uses the server's default model */ }
}

// Gates the composer on whether ANY chat provider is configured (ADR 0019). With none, /api/models is
// empty: disable the input and show the hint, so the user can't transmit into a grain that has no
// model (it would only answer with a "configure a provider" notice). Re-run after every loadModels() —
// including the one saveProvider() fires — so adding a provider re-enables the composer live, no restart.
// The streaming lock wins while a turn is in flight: a mid-turn call (saveProvider() fires one) must not
// disable the box the user may already be typing their next prompt into. send()'s finally re-applies it.
export function applyModelAvailability() {
  // GitHub Copilot models (ADR 0108) don't count: they run only in Copilot agents, so a GitHub-only user is still gated.
  const hasModel = (modelsData.models ?? []).some((m) => !m.copilotOnly);
  noModelHint.hidden = hasModel;
  if (!streaming) input.disabled = !hasModel;
}

// A model is named by a qualified id — "provider:model", split on the FIRST colon (a provider's own
// model name can contain a "/", e.g. github:openai/gpt-4o-mini). A legacy id has no colon at all and
// belongs to the default provider; conversations created before multi-provider hold those.
export function modelLabel(id) {
  if (!id) return "";
  if (id === AUTO_MODEL) return "Auto";
  const known = (modelsData.models ?? []).find((m) => m.id === id);
  if (known?.displayName) return known.displayName;
  const colon = id.indexOf(":");
  return colon < 0 ? id : id.slice(colon + 1);
}

// The label for a provider's optgroup. Models are GROUPED by provider key (ids are qualified by it),
// but the group is LABELLED with the provider's display name when it has one — falling back to the raw
// key, then to "Models" for the unkeyed synthesized default. Shared by every provider-grouped select.
export function providerGroupLabel(m) {
  return (m.providerDisplayName ?? m.provider) || "Models";
}

// Fills a <select> with the available models, grouped by provider. Shared by the composer picker AND
// the agent dialog — both get the grouping, and neither hand-rolls its own option list. GitHub Copilot models
// (copilotOnly, ADR 0108) run only in Copilot agents, so they are left out unless the caller asks for them.
export function fillModelOptions(select, { copilot = false, auto = false } = {}) {
  select.innerHTML = "";
  if (auto && modelsData.auto) {
    const opt = document.createElement("option");
    opt.value = AUTO_MODEL;
    opt.textContent = "Auto";
    opt.title = "Picks one of your models for each message";
    select.appendChild(opt);
  }
  const groups = new Map();
  for (const m of modelsData.models ?? []) {
    if (m.copilotOnly && !copilot) continue;
    // Embedding, speech and transcription models can't chat: they have their own default selectors in Preferences.
    if (m.supportsEmbeddings || m.supportsSpeech || m.supportsTranscription) continue;
    const provider = m.provider ?? "";
    if (!groups.has(provider)) {
      const group = document.createElement("optgroup");
      group.label = providerGroupLabel(m);
      groups.set(provider, group);
      select.appendChild(group);
    }

    const opt = document.createElement("option");
    opt.value = m.id;
    opt.textContent = m.displayName ?? m.id;
    if (m.version) opt.title = `${m.model ?? m.id} · ${m.version}`;
    groups.get(provider).appendChild(opt);
  }
}

// Selects a model in a <select>, adding it first if the list doesn't have it — a conversation pinned
// to a model that has since been removed from config, or whose provider was dropped. It goes into its
// own optgroup rather than as a bare <option>: appended straight to a select that already has
// optgroups, it would render as their sibling and read as if it belonged to the last group.
export function selectModel(select, id) {
  if (!id) return;
  // A stale Auto (the router was removed, ADR 0137) must never become an "Unavailable" option: it is not a model.
  if (id === AUTO_MODEL && ![...select.options].some((o) => o.value === id)) return;
  if (![...select.options].some((o) => o.value === id)) {
    // A Copilot agent's chat on a GitHub Copilot model (ADR 0108): the model exists, it is only left out of this
    // picker, so it is labelled with its provider rather than as unavailable.
    const known = (modelsData.models ?? []).find((m) => m.id === id && m.copilotOnly);
    let unknown = select.querySelector("optgroup[data-unknown]");
    if (!unknown) {
      unknown = document.createElement("optgroup");
      unknown.label = known ? providerGroupLabel(known) : "Unavailable";
      unknown.setAttribute("data-unknown", "");
      select.appendChild(unknown);
    }

    const opt = document.createElement("option");
    opt.value = id;
    opt.textContent = modelLabel(id);
    unknown.appendChild(opt);
  }
  select.value = id;
}

export function setModel(id) {
  selectModel(modelSelect, id);
}
