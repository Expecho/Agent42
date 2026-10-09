/* Agent 42 — artifact cards and pane in the chat app. A thin adapter over the shared classic script
   /shared/lib/artifactpane.js (window.Agent42ArtifactPane), which the public viewer uses too: this module injects
   the owner routes and the app's sanitizing renderMarkdown, and feeds it cards from the live stream and from
   history. Every use is guarded, so a 404 on the shared script leaves chats without cards, never a blank app. */

import { artifactsBtn } from "./dom.js";
import { renderMarkdown } from "./format.js";

let pane = null;
let paneConvId = null;
// The conversation the topbar Artifacts button opens the list for; null while the button is hidden.
let listConvId = null;
// Bumped on every chat switch, so a late /artifacts answer for a chat the user has left is dropped.
let generation = 0;

const base = () => `/api/conversations/${encodeURIComponent(paneConvId)}/artifacts`;
const version = (id, v) => `${base()}/${encodeURIComponent(id)}/versions/${encodeURIComponent(v)}`;

export function wireArtifacts() {
  const Pane = globalThis.Agent42ArtifactPane;
  if (!Pane) return;
  pane = Pane.create({
    host: document.body,
    renderMarkdown,
    urls: {
      list: () => base(),
      content: (id, v) => `${version(id, v)}/content`,
      frame: (id, v) => `${version(id, v)}/frame`,
    },
  });
  // The entry point that needs no card: cards can vanish from history with their tool messages (ADR 0084), and a
  // long chat scrolls them out of sight, while the artifacts themselves stay in state.
  artifactsBtn.addEventListener("click", () => {
    if (!listConvId) return;
    paneConvId = listConvId;
    pane.openList();
  });
}

function showArtifactsButton(convId) {
  listConvId = convId;
  artifactsBtn.hidden = false;
}

// A reopened chat: shows the Artifacts button when the conversation holds any artifact, whether or not one of its
// cards made it back into history.
export async function syncArtifactsButton(convId) {
  if (!pane) return;
  const current = generation;
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/artifacts`);
    const list = res.ok ? await res.json() : [];
    if (current === generation && list.length) showArtifactsButton(convId);
  } catch {
    // Offline: the cards history rendered are still a way in.
  }
}

// The cards of one stored turn (ChatTurn.artifacts), from a reopened chat or a recovered stream.
export function renderArtifactCards(msg, convId, cards) {
  if (!pane || !cards?.length) return;
  const bubble = msg.querySelector(".msg__bubble");
  // The pane follows the conversation of the card that opened it.
  for (const info of cards) pane.renderCard(bubble, info, { beforeOpen: () => { paneConvId = convId; } });
  showArtifactsButton(convId);
}

// A live create_artifact / update_artifact result. The result arrives as a JSON string holding the tool's JSON
// (the same SerializeOrNull shape history uses), so it is parsed twice; anything else, a refusal included, is not a
// card. The tool name is required because read_artifact's result has the same fields.
export function artifactCardFromToolCall(msg, convId, toolName, toolResult) {
  if (toolName !== "create_artifact" && toolName !== "update_artifact") return;
  let value;
  try {
    value = JSON.parse(toolResult);
    if (typeof value === "string") value = JSON.parse(value);
  } catch {
    return;
  }
  if (!value?.artifactId || !value.version) return;
  renderArtifactCards(msg, convId, [value]);
}

// The pane, driven by the model through control_artifact_pane (ADR 0089). The server has already checked that the
// artifact exists in this conversation; the pane re-checks against its own list. False when there is no pane.
export function openArtifactInPane(convId, artifactId, version) {
  if (!pane) return false;
  paneConvId = convId;
  pane.open(artifactId, Number.isInteger(version) ? version : undefined);
  return true;
}

// Closes the pane but, unlike closeArtifactPane, keeps the Artifacts button: the chat has not changed.
export function closeArtifactPaneView() {
  pane?.close();
}

// False when no artifact is open, so the model is told rather than a hidden pane being maximized.
export function setArtifactPaneFull(on) {
  if (!pane?.isOpen()) return false;
  pane.setFull(on);
  return true;
}

// Switching chats closes the pane and hides the Artifacts button: both would otherwise keep pointing at the
// previous chat's artifacts.
export function closeArtifactPane() {
  generation++;
  listConvId = null;
  artifactsBtn.hidden = true;
  pane?.close();
}
