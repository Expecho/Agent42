/* Agent 42 — edit the last message, regenerate the last reply, fork a conversation (ADR 0092).

   Every action works on the SERVER's history, never on what the DOM happens to show: a live tool turn renders
   differently from a reload, and an errored turn leaves a user bubble the server never stored (ADR 0046). So each
   action first re-reads /messages, and the concurrency token it sends — the user-turn count and the SHA-256 of
   the last user message — comes from that read. A stale tab gets a 409, never a second truncation. */

import { ephemeral } from "./agents.js";
import { activeAgentKind, activeId, renderTurns, selectConversation } from "./conversations.js";
import { thread } from "./dom.js";
import { send, streaming } from "./send.js";
import { refreshConversations } from "./sidebar.js";

// Hex SHA-256 of a turn's text, the same value the server computes (AgentGrain.HistoryTextHash). crypto.subtle
// exists on HTTPS and localhost, which is everywhere this app runs.
async function sha256Hex(text) {
  const digest = await crypto.subtle.digest("SHA-256", new TextEncoder().encode(text ?? ""));
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, "0")).join("");
}

async function fetchTurns(convId) {
  const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/messages`);
  if (!res.ok) throw new Error(`HTTP ${res.status}`);
  return res.json();
}

// The concurrency token for a history, plus where its last user turn sits.
async function tokenFor(turns) {
  const userTurns = turns.filter((t) => t.role === "user");
  const last = userTurns[userTurns.length - 1] ?? null;
  return {
    lastUserIndex: last ? turns.lastIndexOf(last) : -1,
    lastUser: last,
    expectedUserTurnCount: userTurns.length,
    expectedLastUserTextHash: await sha256Hex(last?.text ?? ""),
  };
}

// Repaints the open thread from a server history, keeping the artifact pane and shared-state panel (unlike
// clearThread, which is for switching conversations).
function repaint(convId, turns) {
  thread.querySelectorAll(".msg, .thread__stopped, .thread__notice").forEach((el) => el.remove());
  renderTurns(convId, turns);
}

function showNotice(text) {
  const note = document.createElement("p");
  note.className = "thread__notice";
  note.textContent = text;
  thread.appendChild(note);
}

// Whether the history actions may show at all. A Copilot chat's real transcript lives outside the grain, so the
// server refuses all three there; offline every one of them needs the server.
function actionsAllowed() {
  return !streaming && activeAgentKind !== "copilot" && navigator.onLine;
}

function actionButton(label, glyph, onClick) {
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "msg__action";
  btn.textContent = `${glyph} ${label}`;
  btn.setAttribute("aria-label", label);
  btn.addEventListener("click", onClick);
  return btn;
}

function actionRow(msg) {
  let row = msg.querySelector(":scope > .msg__actions");
  if (!row) {
    row = document.createElement("div");
    row.className = "msg__actions";
    msg.appendChild(row);
  }
  return row;
}

// Places the action buttons on the thread as it stands: Edit on the last user bubble, Regenerate on the last
// agent bubble, Fork on every agent bubble (not in incognito: a fork needs a listed source). Called after every
// repaint and at both ends of a turn; with actions not allowed (streaming, copilot, offline) it only removes them.
export function syncMessageActions() {
  thread.querySelectorAll(".msg__actions").forEach((el) => el.remove());
  if (!actionsAllowed()) return;

  const users = thread.querySelectorAll(".msg--user");
  const agents = thread.querySelectorAll(".msg--agent:not(.is-error)");
  const lastUser = users[users.length - 1];
  if (lastUser) actionRow(lastUser).appendChild(actionButton("Edit", "✎", () => editLastMessage()));

  const lastAgent = agents[agents.length - 1];
  for (const msg of agents) {
    const row = actionRow(msg);
    if (msg === lastAgent) row.appendChild(actionButton("Regenerate", "↻", () => regenerateLastReply()));
    if (!ephemeral) row.appendChild(actionButton("Fork from here", "⑂", () => forkFrom(msg)));
  }
}

// Regenerate: re-read the history, show it without the last exchange, and send the same message again with
// replaceLastTurn — the server removes the old exchange and runs the turn in its place.
async function regenerateLastReply() {
  if (!actionsAllowed()) return;
  const convId = activeId;
  try {
    const turns = await fetchTurns(convId);
    const token = await tokenFor(turns);
    if (!token.lastUser) return;
    repaint(convId, turns.slice(0, token.lastUserIndex));
    await send(token.lastUser.text, replayOf(token));
  } catch (err) {
    showNotice(`Could not regenerate: ${err.message ?? err}`);
  }
}

// Edit: re-read the history, open an inline editor on the last user message with the SERVER's text, and on Send
// replace the last exchange with the edited message. Cancel repaints the message as it was.
async function editLastMessage() {
  if (!actionsAllowed()) return;
  const convId = activeId;
  let turns;
  let token;
  try {
    turns = await fetchTurns(convId);
    token = await tokenFor(turns);
  } catch (err) {
    showNotice(`Could not edit: ${err.message ?? err}`);
    return;
  }
  if (!token.lastUser) return;

  repaint(convId, turns);
  syncMessageActions();
  const users = thread.querySelectorAll(".msg--user");
  const target = users[users.length - 1];
  if (target) openEditor(target, convId, turns, token);
}

function openEditor(msg, convId, turns, token) {
  thread.querySelectorAll(".msg__actions").forEach((el) => el.remove());
  const bubble = msg.querySelector(".msg__bubble");
  const editor = document.createElement("div");
  editor.className = "msg__edit";
  const box = document.createElement("textarea");
  box.className = "msg__edit-input";
  box.value = token.lastUser.text ?? "";
  box.rows = Math.min(10, Math.max(2, box.value.split("\n").length));
  const buttons = document.createElement("div");
  buttons.className = "msg__edit-buttons";
  const cancel = document.createElement("button");
  cancel.type = "button";
  cancel.className = "msg__edit-cancel";
  cancel.textContent = "Cancel";
  const save = document.createElement("button");
  save.type = "button";
  save.className = "msg__edit-send";
  save.textContent = "Send";
  buttons.append(cancel, save);
  editor.append(box, buttons);
  bubble.replaceChildren(editor);
  box.focus();

  const close = () => {
    repaint(convId, turns);
    syncMessageActions();
  };
  const submit = () => {
    const text = box.value.trim();
    const attachments = token.lastUser.attachments ?? [];
    if ((!text && attachments.length === 0) || streaming || activeId !== convId) return;
    repaint(convId, turns.slice(0, token.lastUserIndex));
    send(text, replayOf(token));
  };
  cancel.addEventListener("click", close);
  save.addEventListener("click", submit);
  box.addEventListener("keydown", (e) => {
    if (e.key === "Escape") close();
    if (e.key === "Enter" && (e.ctrlKey || e.metaKey)) submit();
  });
}

// The send() replay for an edit or regenerate: the original turn's attachments (already uploaded to this
// conversation) and the concurrency token.
function replayOf(token) {
  return {
    attachments: token.lastUser.attachments ?? [],
    replaceLastTurn: true,
    expectedUserTurnCount: token.expectedUserTurnCount,
    expectedLastUserTextHash: token.expectedLastUserTextHash,
  };
}

// Fork from here: the exchange this agent bubble belongs to is the one opened by the nearest user bubble above
// it. The DOM position is trusted only when the DOM shows exactly as many user turns as the server holds;
// otherwise the thread is repainted from the server and the user picks again.
async function forkFrom(msg) {
  if (!actionsAllowed() || ephemeral) return;
  const convId = activeId;
  const domUsers = [...thread.querySelectorAll(".msg--user")];
  const throughUserTurn = domUsers.filter(
    (u) => u.compareDocumentPosition(msg) & Node.DOCUMENT_POSITION_FOLLOWING).length - 1;
  if (throughUserTurn < 0) return;

  try {
    const turns = await fetchTurns(convId);
    const token = await tokenFor(turns);
    if (token.expectedUserTurnCount !== domUsers.length) {
      repaint(convId, turns);
      syncMessageActions();
      showNotice("This conversation was refreshed. Choose the reply to fork from again.");
      return;
    }

    const res = await fetch(`/api/conversations/${encodeURIComponent(convId)}/fork`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        throughUserTurn,
        expectedUserTurnCount: token.expectedUserTurnCount,
        expectedLastUserTextHash: token.expectedLastUserTextHash,
      }),
    });
    if (!res.ok) throw new Error((await res.json().catch(() => null))?.error ?? `HTTP ${res.status}`);
    const { conversationId } = await res.json();
    await refreshConversations();
    await selectConversation(conversationId);
  } catch (err) {
    showNotice(`Could not fork: ${err.message ?? err}`);
  }
}

export function wireEditFork() {
  // The actions need the server, so they follow the connection.
  window.addEventListener("online", syncMessageActions);
  window.addEventListener("offline", syncMessageActions);
}
