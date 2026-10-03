/* Agent 42 — artifact cards and pane in the chat app. A thin adapter over the shared classic script
   /shared/lib/artifactpane.js (window.Agent42ArtifactPane), which the public viewer uses too: this module injects
   the owner routes and the app's sanitizing renderMarkdown, and feeds it cards from the live stream and from
   history. Every use is guarded, so a 404 on the shared script leaves chats without cards, never a blank app. */

import { renderMarkdown } from "./format.js";

let pane = null;
let paneConvId = null;

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
}

// The cards of one stored turn (ChatTurn.artifacts), from a reopened chat or a recovered stream.
export function renderArtifactCards(msg, convId, cards) {
  if (!pane || !cards?.length) return;
  const bubble = msg.querySelector(".msg__bubble");
  // The pane follows the conversation of the card that opened it.
  for (const info of cards) pane.renderCard(bubble, info, { beforeOpen: () => { paneConvId = convId; } });
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

// Switching chats closes the pane: it would otherwise keep showing the previous chat's artifact.
export function closeArtifactPane() {
  pane?.close();
}

