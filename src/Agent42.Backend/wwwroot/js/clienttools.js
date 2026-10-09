/* Agent 42 — client tools (ADR 0089): tools the model calls and THIS browser runs. The server streams the call as a
   `toolCallStart` flagged `clientTool`, waits, and resumes the turn when the result is posted back here. The server
   defines the tools; the browser only says which it can run (CLIENT_TOOLS, sent with every chat request).

   A run is never awaited by the stream reader: send.js hands the call over and keeps reading, so the turn's own
   events (including a stop) still arrive while a dialog is open. A late answer (the wait timed out or the turn was
   stopped) is a 404, which is expected and ignored. */

import { closeArtifactPaneView, openArtifactInPane, setArtifactPaneFull } from "./artifacts.js";
import { confirmActionApproveBtn, confirmActionDeclineBtn, confirmActionDialog, confirmActionText } from "./dom.js";

// Every client tool this page can run, by name. Sent as `clientTools` with a chat request; a tool missing here
// answers "unavailable" on the server without ever reaching the page.
export const CLIENT_TOOLS = ["confirm_action", "control_artifact_pane", "get_browser_context"];

const MAX_SELECTION_CHARS = 2000;

// Calls being run on this page right now, so the live stream and stream recovery never run one call twice. Only
// while in flight: once the result is posted the server no longer lists the call, and a provider is free to reuse
// an id in a later turn.
const handled = new Set();

// The confirm dialog shows one request at a time; later ones wait their turn.
let dialogQueue = Promise.resolve();
// Resolves the request currently on screen, or null.
let answerDialog = null;

const handlers = {
  confirm_action: confirmAction,
  control_artifact_pane: controlArtifactPane,
  get_browser_context: browserContext,
};

// Runs one flagged call and posts its result. Never throws and is never awaited by the stream reader.
export async function runClientTool(convId, call) {
  const key = `${convId}:${call?.toolCallId}`;
  if (!call?.toolCallId || handled.has(key)) return;
  handled.add(key);
  try {
    await runAndPost(convId, call);
  } finally {
    handled.delete(key);
  }
}

async function runAndPost(convId, call) {
  let result;
  try {
    const handler = handlers[call.toolName];
    result = handler ? await handler(parseInput(call.toolInput), convId) : { status: "unavailable" };
  } catch (err) {
    result = { ok: false, error: String(err?.message ?? err) };
  }
  try {
    // 204 accepted, 404 too late (timed out or stopped): nothing to tell the user either way.
    await fetch(`/api/chat/${encodeURIComponent(convId)}/tool-result`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ toolCallId: call.toolCallId, result }),
    });
  } catch {
    // Offline: the server's wait times out and the turn carries on with a timeout result.
  }
}

// Stream recovery (ADR 0046): a page whose stream died (a frozen PWA) asks which calls are still waiting for it and
// runs them, so a confirm the user never saw is shown when they come back, if that is within the wait.
export async function resumePendingClientTools(convId) {
  try {
    const res = await fetch(`/api/chat/${encodeURIComponent(convId)}/pending-tool-calls`);
    if (!res.ok) return;
    for (const call of await res.json()) runClientTool(convId, call);
  } catch {
    // Still offline; the next recovery attempt asks again.
  }
}

// The turn is over (done, stopped or failed): a dialog still open belongs to a call the server no longer waits
// for. Closing it declines, and that answer's 404 is ignored.
export function closeClientToolDialogs() {
  if (confirmActionDialog?.open) confirmActionDialog.close("stale");
}

function parseInput(toolInput) {
  try {
    const value = JSON.parse(toolInput ?? "{}");
    return value && typeof value === "object" ? value : {};
  } catch {
    return {};
  }
}

function confirmAction(input) {
  const ask = () => new Promise((resolve) => {
    if (!confirmActionDialog) {
      resolve({ status: "unavailable" });
      return;
    }
    // textContent, never innerHTML: the description is model output.
    confirmActionText.textContent = String(input.action ?? "The assistant wants to take an action.");
    answerDialog = resolve;
    // Reset first, and this is a safety line: Esc closes a dialog WITHOUT setting returnValue, so the previous
    // request's "approve" would otherwise still be there and turn an Esc into a yes.
    confirmActionDialog.returnValue = "";
    confirmActionDialog.showModal();
  });
  const answer = dialogQueue.then(ask);
  dialogQueue = answer.catch(() => {});
  return answer;
}

// Every way the dialog closes answers the request on screen: Approve is the only yes; Decline, Esc and a close by
// closeClientToolDialogs() are all a no.
function settleDialog() {
  const resolve = answerDialog;
  answerDialog = null;
  resolve?.({ approved: confirmActionDialog.returnValue === "approve" });
}

async function controlArtifactPane(input, convId) {
  switch (input.action) {
    case "open":
      return openArtifactInPane(convId, input.artifactId, input.version) ? { ok: true } : { ok: false, error: "the pane is not available" };
    case "close":
      closeArtifactPaneView();
      return { ok: true };
    case "fullscreen":
    case "exitFullscreen":
      return setArtifactPaneFull(input.action === "fullscreen") ? { ok: true } : { ok: false, error: "no artifact is open" };
    default:
      return { ok: false, error: "unknown action" };
  }
}

function browserContext() {
  const dark = document.documentElement.dataset.theme
    ? document.documentElement.dataset.theme === "dark"
    : matchMedia("(prefers-color-scheme: dark)").matches;
  const now = new Date();
  const offset = -now.getTimezoneOffset();
  const pad = (n) => String(Math.trunc(Math.abs(n))).padStart(2, "0");
  return {
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    locale: navigator.language,
    localTime: now.toLocaleString(navigator.language),
    utcOffset: `${offset >= 0 ? "+" : "-"}${pad(offset / 60)}:${pad(offset % 60)}`,
    colorScheme: dark ? "dark" : "light",
    viewport: { width: innerWidth, height: innerHeight },
    installedApp: matchMedia("(display-mode: standalone)").matches,
    online: navigator.onLine,
    // What the user has selected on the page right now, capped; no clipboard (it needs a gesture) and no position
    // (ADR 0012 owns that, behind its own consent).
    selectedText: String(getSelection?.() ?? "").slice(0, MAX_SELECTION_CHARS),
  };
}

export function wireClientTools() {
  if (!confirmActionDialog) return;
  confirmActionApproveBtn.addEventListener("click", () => confirmActionDialog.close("approve"));
  confirmActionDeclineBtn.addEventListener("click", () => confirmActionDialog.close("decline"));
  // Esc fires `cancel` and then `close` with an empty returnValue, which settleDialog reads as a decline.
  confirmActionDialog.addEventListener("close", settleDialog);
}
