/* Agent 42 — a stored agent's outbound webhook (ADR 0094): the Webhook tab of the agent dialog. Imported by
   agents.js, which opens it with the agent and wires it once. Every server string reaches the page through
   textContent and no HTML parsing: the delivery log carries error classes and the URL the owner typed. */

import {
  agentWebhookLog, agentWebhookRemoveBtn, agentWebhookRotateBtn, agentWebhookSaveBtn, agentWebhookSecretCopyBtn,
  agentWebhookSecretReveal, agentWebhookSecretValue, agentWebhookSection, agentWebhookStatus, agentWebhookTestBtn,
  agentWebhookUnsaved, agentWebhookUrl,
} from "./dom.js";

// A delivery can take ~40 s (three 10 s attempts plus the waits), so the test button waits a little longer than that.
const TEST_TIMEOUT_MS = 45_000;

// The saved agent this panel is showing, or null for a not-yet-saved one (the panel is gated on it).
let agentId = null;
let configured = false;

function endpoint(suffix = "") {
  return `/api/agents/${encodeURIComponent(agentId)}/webhook${suffix}`;
}

function setStatus(text) {
  agentWebhookStatus.textContent = text;
}

function hideSecret() {
  agentWebhookSecretReveal.hidden = true;
  agentWebhookSecretValue.textContent = "";
  agentWebhookSecretCopyBtn.textContent = "Copy";
}

function syncButtons() {
  agentWebhookTestBtn.disabled = !configured;
  agentWebhookRotateBtn.disabled = !configured;
  agentWebhookRemoveBtn.disabled = !configured;
}

// One row per delivery, newest first: when, what, and how it went. Text nodes only.
function renderLog(deliveries) {
  agentWebhookLog.replaceChildren();
  if (!deliveries?.length) {
    const empty = document.createElement("li");
    empty.className = "agent-webhook__row agent-webhook__row--empty";
    empty.textContent = configured ? "No deliveries yet." : "No webhook configured.";
    agentWebhookLog.append(empty);
    return;
  }

  for (const d of deliveries) {
    const row = document.createElement("li");
    const ok = d.statusCode >= 200 && d.statusCode < 300;
    row.className = `agent-webhook__row${ok ? "" : " agent-webhook__row--failed"}`;
    const when = new Date(d.at).toLocaleString();
    const what = d.type === "webhook.test" ? "test" : d.trigger ?? d.type;
    const outcome = d.statusCode ?? d.error ?? "no answer";
    row.textContent = `${when} · ${what} · ${outcome} · ${d.attempts} attempt${d.attempts === 1 ? "" : "s"}`;
    agentWebhookLog.append(row);
  }
}

async function refresh() {
  if (!agentId) return;
  try {
    const res = await fetch(endpoint());
    if (!res.ok) return;
    const body = await res.json();
    configured = !!body.url;
    agentWebhookUrl.value = body.url ?? "";
    syncButtons();
    renderLog(body.deliveries);
  } catch {
    /* silo unreachable — the panel keeps what it showed */
  }
}

// Called from openAgentDialog, and again from saveAgent once a new agent has its id.
export function openWebhookPanel(id) {
  agentId = id ?? null;
  configured = false;
  agentWebhookUnsaved.hidden = !!agentId;
  agentWebhookSection.hidden = !agentId;
  agentWebhookUrl.value = "";
  setStatus("");
  hideSecret();
  syncButtons();
  renderLog([]);
  refresh();
}

async function save(rotateSecret) {
  const url = agentWebhookUrl.value.trim();
  if (!agentId || !url) return;
  try {
    const res = await fetch(endpoint(), {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ url, rotateSecret }),
    });
    if (!res.ok) {
      setStatus(res.status === 400 ? await res.text() : "Couldn't save the webhook.");
      return;
    }
    const body = await res.json();
    configured = true;
    syncButtons();
    setStatus(rotateSecret ? "Secret rotated." : "Webhook saved.");
    // The secret is in this response only (minted on the first save and on a rotate) — reveal it once.
    if (body.secret) {
      agentWebhookSecretValue.textContent = body.secret;
      agentWebhookSecretReveal.hidden = false;
      agentWebhookSecretCopyBtn.textContent = "Copy";
    }
    refresh();
  } catch {
    setStatus("Couldn't save the webhook.");
  }
}

async function sendTest() {
  if (!agentId) return;
  agentWebhookTestBtn.disabled = true;
  setStatus("Sending a test…");
  const abort = new AbortController();
  const timer = setTimeout(() => abort.abort(), TEST_TIMEOUT_MS);
  try {
    const res = await fetch(endpoint("/test"), { method: "POST", signal: abort.signal });
    if (!res.ok) {
      setStatus(res.status === 409 ? "Save a webhook first." : "Couldn't send the test.");
      return;
    }
    const r = await res.json();
    setStatus(r.statusCode >= 200 && r.statusCode < 300
      ? `Delivered: ${r.statusCode} in ${r.durationMs} ms.`
      : `Not delivered: ${r.statusCode ?? r.error} after ${r.attempts} attempt${r.attempts === 1 ? "" : "s"}.`);
    refresh();
  } catch {
    setStatus("The test timed out.");
  } finally {
    clearTimeout(timer);
    syncButtons();
  }
}

async function remove() {
  if (!agentId || !confirm("Remove this agent's webhook? Its signing secret and delivery log are deleted.")) return;
  try {
    const res = await fetch(endpoint(), { method: "DELETE" });
    if (!res.ok) return;
    hideSecret();
    setStatus("Webhook removed.");
    refresh();
  } catch {
    /* silo unreachable — leave the panel as it was */
  }
}

export function wireWebhooks() {
  agentWebhookSaveBtn.addEventListener("click", () => save(false));
  agentWebhookRotateBtn.addEventListener("click", () => {
    if (confirm("Rotate the signing secret? Requests signed with the old one stop verifying.")) save(true);
  });
  agentWebhookTestBtn.addEventListener("click", sendTest);
  agentWebhookRemoveBtn.addEventListener("click", remove);
  // Enter in the URL field saves the webhook instead of submitting (and saving) the whole agent form.
  agentWebhookUrl.addEventListener("keydown", (e) => {
    if (e.key === "Enter") {
      e.preventDefault();
      save(false);
    }
  });
  agentWebhookSecretCopyBtn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(agentWebhookSecretValue.textContent);
      agentWebhookSecretCopyBtn.textContent = "Copied";
    } catch {
      /* clipboard blocked — the secret is visible to select manually */
    }
  });
}
