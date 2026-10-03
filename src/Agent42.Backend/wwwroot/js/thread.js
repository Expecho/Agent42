/* Agent 42 — thread rendering — messages, tool calls, token usage. Split out of app.js (ADR 0041). */

import { chatScroll, emptyState, thread } from "./dom.js";
import { linkifyPlainText, renderMarkdown } from "./format.js";
import { mcpOAuthStartUrl } from "./mcp.js";
import { send, streaming } from "./send.js";

export function clearThread() {
  thread.querySelectorAll(".msg").forEach((el) => el.remove());
}

export function setEmptyVisible(visible) {
  emptyState.style.display = visible ? "" : "none";
}

export function addMessage(role, options = {}) {
  setEmptyVisible(false);
  const msg = document.createElement("article");
  msg.className = role === "user" ? "msg msg--user" : "msg msg--agent";
  if (options.error) msg.classList.add("is-error");

  const meta = document.createElement("div");
  meta.className = "msg__meta";
  meta.textContent = role === "user" ? "YOU" : "AGENT 42";

  const bubble = document.createElement("div");
  bubble.className = "msg__bubble";
  if (role !== "user") {
    const prose = document.createElement("div");
    prose.className = "prose";
    bubble.appendChild(prose);
  }

  msg.append(meta, bubble);
  thread.appendChild(msg);
  return msg;
}

// Fills a user bubble with its (optional) images followed by its text. Images are always
// fetched through the first-party display proxy by id, so the same path renders a live send
// and a reloaded turn — the page never holds a blob URL.
export function fillUserBubble(msg, text, attachments, convId) {
  const bubble = msg.querySelector(".msg__bubble");
  bubble.textContent = "";

  if (attachments && attachments.length) {
    const gallery = document.createElement("div");
    gallery.className = "msg__images";
    for (const att of attachments) {
      const img = document.createElement("img");
      img.className = "msg__image";
      img.loading = "lazy";
      img.alt = att.fileName || "attachment";
      img.src = attachmentUrl(convId, att.id);
      gallery.appendChild(img);
    }
    bubble.appendChild(gallery);
  }

  if (text) {
    const body = document.createElement("div");
    body.className = "msg__text";
    // Plain text with bare http(s) URLs made clickable — not markdown (see linkifyPlainText).
    body.innerHTML = linkifyPlainText(text);
    bubble.appendChild(body);
  }
}

export const attachmentUrl = (convId, attachmentId) =>
  `/api/conversations/${encodeURIComponent(convId)}/attachments/${encodeURIComponent(attachmentId)}`;

// Adds a generated image (ADR 0020) to an assistant bubble, ABOVE its prose. Fetched through the same
// first-party display proxy by id as user images, so a live image stream event and a reloaded assistant
// turn render through one path — the page never holds a blob URL.
export function addAgentImage(msg, convId, attachmentId, fileName) {
  const bubble = msg.querySelector(".msg__bubble");
  let gallery = bubble.querySelector(".msg__images");
  if (!gallery) {
    gallery = document.createElement("div");
    gallery.className = "msg__images";
    bubble.insertBefore(gallery, bubble.firstChild); // above the prose
  }
  const img = document.createElement("img");
  img.className = "msg__image";
  img.loading = "lazy";
  img.alt = fileName || "generated image";
  img.src = attachmentUrl(convId, attachmentId);
  gallery.appendChild(img);
}
export function setAgentMarkdown(msg, text, { caret = false } = {}) {
  const prose = msg.querySelector(".prose");
  prose.innerHTML = renderMarkdown(text);
  if (caret) {
    const caretEl = document.createElement("span");
    caretEl.className = "caret";
    (prose.lastElementChild ?? prose).appendChild(caretEl);
  } else {
    // A final render (stream end, stop, error, history, recovery): diagrams, highlighting, math and charts
    // (ADR 0083). Never per chunk: every streaming caller asks for the caret. globalThis, not a bare
    // name, so a missing richtext.js leaves plain markdown instead of a ReferenceError.
    globalThis.Agent42Rich?.enhance(prose);
  }
}

// Adds a hover-revealed copy button to the bottom of a finalized agent bubble. The raw markdown
// is captured by closure here — it is NOT recoverable from the DOM (the .prose div holds only
// rendered HTML). Mirrors the agent-key/run-URL clipboard buttons and the JS-created glyph
// actions (.conv__delete "×", .agent__action "✎"): swap the glyph to a check on success, revert
// after ~1.5s, swallow the catch. Skips empty/image-only replies (nothing to copy).
export function addCopyButton(msg, rawText) {
  if (!rawText || !rawText.trim()) return;
  const bubble = msg.querySelector(".msg__bubble");
  if (!bubble) return;
  const btn = document.createElement("button");
  btn.type = "button";
  btn.className = "msg__copy";
  btn.setAttribute("aria-label", "Copy response");
  btn.textContent = "⧉";
  btn.addEventListener("click", async () => {
    try {
      await navigator.clipboard.writeText(rawText);
      btn.textContent = "✓";
      setTimeout(() => { btn.textContent = "⧉"; }, 1500);
    } catch {
      /* clipboard blocked (insecure context / permission) — the reply is visible to select manually */
    }
  });
  bubble.appendChild(btn);
}

// Marks a turn the user stopped (ADR 0078). Live-only, like reasoning (ADR 0071) and the usage line — but
// no longer like tool chips, which ADR 0080 moved out of that set. A reload shows the partial reply as
// ordinary text, because nothing about "stopped" is persisted.
//
// The two branches are not cosmetic. With text, the note goes inside the bubble — after the copy button,
// which captured the raw reply by closure, so the note can never end up on the clipboard. Without text,
// the whole agent bubble is REMOVED and the note becomes a sibling of the messages, because a bubble with
// nothing in it would leave the thread with two .msg elements for the one turn the server stored — and the
// ADR 0046 recovery counts .msg elements against stored turns, so from then on every backgrounded turn in
// this session would be reported as "Transmission failed" over a reply that actually persisted.
export function markStopped(msg, hasText) {
  if (hasText) {
    const bubble = msg.querySelector(".msg__bubble");
    if (!bubble) return;
    const note = document.createElement("div");
    note.className = "msg__stopped";
    note.textContent = "Stopped.";
    bubble.appendChild(note);
    return;
  }

  const note = document.createElement("p");
  note.className = "thread__stopped";
  note.textContent = "Stopped before the reply started.";
  msg.replaceWith(note);
}

// An InteractiveOAuth MCP server this conversation uses needs a fresh sign-in (ADR 0081): a block in the bubble
// (the markStopped shape) with a Reconnect button. It opens the OAuth flow in a NEW tab — the active conversation
// lives only in a module variable, so the settings flow's full navigation would drop the open chat. The next
// message picks the tools back up (the grain re-resolves). Live-only: not replayed by renderTurns.
export function renderMcpReauth(msg, serverId, name) {
  const bubble = msg.querySelector(".msg__bubble");
  if (!bubble || !serverId) return;
  const block = document.createElement("div");
  block.className = "msg__reauth";
  const text = document.createElement("span");
  text.className = "msg__reauth-text";
  text.textContent = `⚠ ${name || serverId} is disconnected: its sign-in expired.`;
  const button = document.createElement("button");
  button.type = "button";
  button.className = "msg__reauth-btn";
  button.textContent = "Reconnect";
  button.setAttribute("aria-label", `Reconnect MCP server: ${name || serverId}`);
  button.addEventListener("click", () => {
    window.open(mcpOAuthStartUrl(serverId), "_blank", "noopener");
    text.textContent = "Finish signing in in the new tab, then send your message again.";
  });
  block.append(text, button);
  bubble.appendChild(block);
}

// Follow-up suggestion chips (up to 3) under a reply. Clicking one sends it immediately as the next
// message — a click is explicit intent — guarded so it never fires mid-stream. Live-only: not replayed
// by renderTurns, so they exist only under the freshly streamed turn and are cleared when the next turn
// starts (clearSuggestions in send()).
export function renderSuggestions(msg, items) {
  if (!Array.isArray(items) || items.length === 0) return;
  const bubble = msg.querySelector(".msg__bubble");
  if (!bubble) return;
  const row = document.createElement("div");
  row.className = "msg__suggestions";
  for (const text of items.slice(0, 3)) {
    const chip = document.createElement("button");
    chip.type = "button";
    chip.className = "suggestion-chip";
    chip.textContent = text;
    chip.addEventListener("click", () => { if (!streaming) send(text); });
    row.appendChild(chip);
  }
  bubble.appendChild(row);
}

// Removes any suggestion chips currently in the thread (only the newest turn should carry them).
export function clearSuggestions() {
  thread.querySelectorAll(".msg__suggestions").forEach((el) => el.remove());
}

export const scrollToBottom = () => { chatScroll.scrollTop = chatScroll.scrollHeight; };
const prettyJson = (text) => {
  try { return JSON.stringify(JSON.parse(text), null, 2); } catch { return text; }
};

// A reasoning model's "thinking", shown as ONE collapsed disclosure above the reply (ADR 0071).
//
// Mutated in place as the stream advances, like addToolCall/completeToolCall rather than
// renderSuggestions — reasoning arrives in deltas, so the element is created on the first one and
// appended to thereafter. It sits before .prose so the reply text stays the last thing in the bubble.
//
// Collapsed by design: the reply is what the user asked for, and the thinking is there to be opened
// if they want it. Live-only — renderTurns never replays it, because it is never persisted.
export function renderReasoning(msg, text) {
  if (!text) return;
  const bubble = msg.querySelector(".msg__bubble");
  if (!bubble) return;

  let item = bubble.querySelector(".msg__thinking");
  if (!item) {
    item = document.createElement("details");
    item.className = "msg__thinking is-thinking";
    const summary = document.createElement("summary");
    summary.className = "msg__thinking-summary";
    summary.textContent = "Thinking…";
    const body = document.createElement("div");
    body.className = "msg__thinking-body";
    item.append(summary, body);
    bubble.insertBefore(item, bubble.querySelector(".prose"));
  }

  // textContent, never innerHTML: reasoning is model output and is NOT run through the markdown
  // renderer or the sanitizer the reply uses, so it must never be parsed as markup.
  const body = item.querySelector(".msg__thinking-body");
  if (body) body.textContent += text;
}

// Closes out the disclosure when the turn ends: the summary stops saying "Thinking…" (present tense on
// a finished turn reads as a hung reply) and the running class drops so the pulse animation stops.
// Safe to call on a turn that never reasoned — there is simply nothing to find.
export function finishReasoning(msg) {
  const item = msg.querySelector(".msg__thinking.is-thinking");
  if (!item) return;
  item.classList.remove("is-thinking");
  item.classList.add("is-done");
  const summary = item.querySelector(".msg__thinking-summary");
  if (summary) summary.textContent = "Thought process";
}

// Above this many calls in ONE turn, the chips fold into a single counted disclosure. Below it they
// render flat, exactly as they always have: the common one-search turn is not worth a wrapper.
const TOOL_GROUP_THRESHOLD = 3;

// The per-turn wrapper the chips live in, created on the first call of the turn.
//
// It is always a <details>, whatever the count — the threshold only decides whether its summary is
// DRAWN (see .msg__toolgroup:not(.is-grouped) in styles.css). Building it unconditionally is what keeps
// the 4th call of a turn a class toggle rather than a re-parent of live, already-open chips.
function toolGroup(bubble) {
  let group = bubble.querySelector(".msg__toolgroup");
  if (group) return group;

  group = document.createElement("details");
  group.className = "msg__toolgroup is-running";
  group.open = true; // open WHILE the turn streams: the running chips are the progress indicator.

  const summary = document.createElement("summary");
  summary.className = "msg__toolgroup-summary";
  const status = document.createElement("span");
  status.className = "tool-call__status";
  const label = document.createElement("span");
  label.className = "msg__toolgroup-label";
  summary.append(status, label);

  const tools = document.createElement("div");
  tools.className = "msg__tools";
  group.append(summary, tools);
  bubble.insertBefore(group, bubble.querySelector(".prose"));
  return group;
}

// Re-derives the summary's count and the group's two state classes from the chips actually present.
// Recomputed rather than counted incrementally so it stays right however the chips were added.
function syncToolGroup(group) {
  const count = group.querySelectorAll(".tool-call").length;
  const label = group.querySelector(".msg__toolgroup-label");
  if (label) label.textContent = `${count} tool ${count === 1 ? "call" : "calls"}`;
  group.classList.toggle("is-grouped", count > TOOL_GROUP_THRESHOLD);
  group.classList.toggle("is-running", !!group.querySelector(".tool-call.is-running"));
}

// Tool activity is shown as collapsible chips above the reply text, one per call.
export function addToolCall(msg, { toolCallId, toolName, toolInput }) {
  const bubble = msg.querySelector(".msg__bubble");
  const group = toolGroup(bubble);
  const tools = group.querySelector(".msg__tools");

  const item = document.createElement("details");
  item.className = "tool-call is-running";
  if (toolCallId) item.dataset.callId = toolCallId;

  const summary = document.createElement("summary");
  const status = document.createElement("span");
  status.className = "tool-call__status";
  const name = document.createElement("span");
  name.className = "tool-call__name";
  name.textContent = toolName ?? "tool";
  summary.append(status, name);
  item.appendChild(summary);

  if (toolInput) appendToolIo(item, "input", toolInput);
  tools.appendChild(item);
  syncToolGroup(group);
}

export function completeToolCall(msg, { toolCallId, toolResult }) {
  const item =
    (toolCallId && msg.querySelector(`.tool-call[data-call-id="${CSS.escape(toolCallId)}"]`)) ??
    msg.querySelector(".tool-call.is-running");
  if (!item) return;
  item.classList.remove("is-running");
  item.classList.add("is-done");
  if (toolResult) appendToolIo(item, "result", toolResult);
  const group = item.closest(".msg__toolgroup");
  if (group) syncToolGroup(group);
}

// Closes out the turn's tool group, the finishReasoning counterpart: the lamp stops pulsing, and a
// group that actually crossed the threshold folds itself shut so a long chain of calls stops pushing
// the reply down the bubble. A short turn stays flat and open — there is nothing to tidy away.
//
// Only collapsed here, never on the way in: while the turn streams, the chips ARE the progress
// indicator. Safe on a turn that called no tools, and idempotent.
export function finishToolCalls(msg) {
  const group = msg.querySelector(".msg__toolgroup");
  if (!group) return;
  group.classList.remove("is-running");
  if (group.classList.contains("is-grouped")) group.open = false;
}

// Repaints a finished turn's tool chips from the history the server projected (ADR 0080), so a reopened
// chat shows the same folded section the live turn showed. THE one replay path, shared by both painters
// of a stored ChatTurn: renderTurns (conversations.js — a reopened chat, and the offline-cache replay)
// and recoverPersistedReply (send.js — a turn whose stream died, ADR 0046).
//
// That second caller is why the de-dupe exists and why it branches on three states rather than looping.
// Its bubble may ALREADY hold chips that arrived before the disconnect, and neither primitive is
// idempotent: addToolCall appends unconditionally, so re-adding duplicates the chip, and completeToolCall
// appends its result block unconditionally, so re-completing a finished chip gives it two result bodies.
// Skipping only the chips that are already done also HEALS the one the disconnect left pulsing.
//
// completeToolCall runs for every call, never conditionally on a result being present. A tool that
// returned null has no result to show, and a call the history holds no result for at all would otherwise
// replay stuck is-running, pulsing forever in a finished transcript — finishToolCalls clears the group's
// lamp, not the chip's. Passing every call through marks it done and simply appends no result body.
export function replayToolCalls(msg, calls) {
  for (const call of calls ?? []) {
    const existing = call.toolCallId
      ? msg.querySelector(`.tool-call[data-call-id="${CSS.escape(call.toolCallId)}"]`)
      : null;
    if (existing?.classList.contains("is-done")) continue;
    if (!existing) addToolCall(msg, call);
    completeToolCall(msg, call);
  }
  finishToolCalls(msg);
}

function appendToolIo(item, label, json) {
  const io = document.createElement("div");
  io.className = "tool-call__io";
  const lab = document.createElement("div");
  lab.className = "tool-call__io-label";
  lab.textContent = label;
  const pre = document.createElement("pre");
  pre.textContent = prettyJson(json);
  io.append(lab, pre);
  item.appendChild(io);
}

export function showUsage(msg, { inputTokens, outputTokens, cachedTokens }) {
  if (inputTokens == null && outputTokens == null) return;
  const parts = [];
  if (inputTokens != null) parts.push(`${inputTokens.toLocaleString()} in`);
  if (outputTokens != null) parts.push(`${outputTokens.toLocaleString()} out`);
  if (cachedTokens != null) parts.push(`${cachedTokens.toLocaleString()} cached`);
  const usage = document.createElement("div");
  usage.className = "msg__usage";
  usage.textContent = `tokens · ${parts.join(" · ")}`;
  msg.appendChild(usage);
}
