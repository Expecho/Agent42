/* Agent 42 — MCP Apps host (SEP-1865, ADR 0133). A tool that declares a `ui://` view renders it inline in the chat
   bubble, in a sandboxed frame, and talks to it over the spec's postMessage JSON-RPC dialect.

   Safety, in the order it matters:
   - The frame is `<iframe sandbox="allow-scripts">` (NEVER allow-same-origin) and its server response carries the real
     protection, a CSP header built from the origins the server declared (McpAppCsp.cs). The view's origin is opaque
     ("null"), so a message is authenticated by `event.source === iframe.contentWindow` plus its shape, never by origin,
     and replies go to targetOrigin "*" (the only value an opaque origin accepts).
   - Nothing is sent to a view before it says `ui/notifications/initialized`; tool-input goes before tool-result and
     `ui/resource-teardown` before the frame is removed.
   - `ui/message` and `ui/update-model-context` are third-party HTML writing into the conversation: both are gated by the
     confirm dialog (askUserConfirm), whose text is the view's own words shown in full via textContent.
   - A card drawn from a STORED result starts inert: it renders and is fed the stored input/result, but `tools/call`,
     `ui/message` and `ui/update-model-context` are refused until the user clicks "Activate app", so a reload never
     re-runs a server tool a view calls on init. A live card is active at once. */

import { askUserConfirm } from "./clienttools.js";

// The capability the page advertises on a chat request (ClientToolLimits.McpApp): without it the server sends no app.
export const MCP_APP_CAPABILITY = "mcp_app"; // clienttools.js sends the same literal (it cannot import this module)

const PROTOCOL_VERSION = "2026-01-26";
const MAX_TEXT_CHARS = 8000;
const MAX_CONTEXT_CHARS = 8 * 1024;
// All open cards together stay under the server cap (McpToolLimits.MaxAppContextChars, 16 KB) with room for the JSON
// wrapper, so a request's context is never dropped whole for being over it.
const MAX_TOTAL_CONTEXT_CHARS = 12 * 1024;
const MIN_HEIGHT = 80;
const MAX_HEIGHT = 800;
const START_TIMEOUT_MS = 10000;

// What the page does that this module must not import (send.js imports this one): set once by main.js (ADR 0041).
const host = { isStreaming: () => false, send: async () => {} };

export function configureMcpApps(options) {
  Object.assign(host, options);
}

// Every card on the page, by its frame's window, so a message finds its card by `event.source`.
const cards = new Set();
let listening = false;

function cardFor(source) {
  for (const card of cards) {
    if (card.iframe.contentWindow === source) return card;
  }
  return null;
}

/** Renders the card of one MCP App tool call. `live` is a card drawn during the turn that produced it. */
export function renderMcpAppCard(msg, convId, call, { live }) {
  const app = call?.app ?? call?.mcpApp;
  const bubble = msg?.querySelector(".msg__bubble");
  if (!bubble || !app?.resourceUri || !call.toolCallId) return;
  if (bubble.querySelector(`.mcp-app[data-call-id="${CSS.escape(call.toolCallId)}"]`)) return;
  ensureListening();

  const root = el("div", "mcp-app");
  root.dataset.callId = call.toolCallId;
  const bar = el("div", "mcp-app__bar");
  bar.append(el("span", "mcp-app__title", app.toolName));
  const iframe = document.createElement("iframe");
  iframe.className = "mcp-app__frame";
  iframe.title = app.toolName;
  // allow-scripts only. Adding allow-same-origin would hand the third-party view this origin's cookies and /api.
  iframe.setAttribute("sandbox", "allow-scripts");
  iframe.referrerPolicy = "no-referrer";
  iframe.style.height = "240px";

  const card = {
    convId, call, app, root, iframe, live,
    active: live, initialized: false, displayMode: "inline", context: null, started: false,
  };
  if (!live) bar.append(activateButton(card));
  bar.append(modeButton(card));
  root.append(bar, iframe);
  bubble.append(root);
  cards.add(card);

  iframe.src = `/api/mcp-apps/${encodeURIComponent(app.serverId)}/frame?uri=${encodeURIComponent(app.resourceUri)}`;
  // A view that never initializes (the frame failed, or it is not an MCP App) leaves the plain tool chip, which is
  // always there: the card is removed rather than shown blank.
  setTimeout(() => {
    if (!card.initialized) removeCard(card);
  }, START_TIMEOUT_MS);
}

/** The cards of a reopened or recovered turn: its tool calls that rendered an app (ChatToolCall.mcpApp). */
export function renderMcpAppCards(msg, convId, toolCalls) {
  for (const call of toolCalls ?? []) {
    if (call.mcpApp) renderMcpAppCard(msg, convId, call, { live: false });
  }
}

/** The turn was stopped: tell every running view, so it can stop work instead of waiting for a result. */
export function cancelAppCards(reason = "The turn was stopped.") {
  for (const card of cards) notify(card, "ui/notifications/tool-cancelled", { reason });
}

/** What views asked the model to know (`ui/update-model-context`), as JSON text for the next chat request, or null. */
export function getAppContext(convId) {
  const entries = [];
  for (const card of cards) {
    if (card.convId === convId && card.context && card.root.isConnected) {
      entries.push({ tool: card.app.toolName, context: card.context });
    }
  }
  return entries.length ? JSON.stringify(entries) : null;
}

/** The conversation changed: tear every card down properly (the views are told before their frames go). */
export function teardownAppCards() {
  for (const card of [...cards]) removeCard(card);
}

function ensureListening() {
  if (listening) return;
  listening = true;
  window.addEventListener("message", onMessage);
  // A theme or palette flip repaints the page; the views follow.
  new MutationObserver(() => {
    for (const card of cards) notify(card, "ui/notifications/host-context-changed", hostContext(card));
  }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme", "data-palette"] });
}

function onMessage(event) {
  const card = cardFor(event.source);
  // Authenticated by the window the message came from, never by origin (an opaque origin is "null").
  if (!card) return;
  const data = event.data;
  if (!data || typeof data !== "object" || data.jsonrpc !== "2.0") return;
  if (typeof data.method !== "string") return; // a response to a host request: we send none that need one
  handle(card, data).catch((err) => {
    if (data.id !== undefined) fail(card, data.id, -32603, String(err?.message ?? err));
  });
}

async function handle(card, data) {
  const { method, params, id } = data;
  const isRequest = id !== undefined;
  switch (method) {
    case "ui/initialize":
      return reply(card, id, {
        protocolVersion: PROTOCOL_VERSION,
        hostInfo: { name: "Agent42", version: "1" },
        hostCapabilities: {
          openLinks: {}, serverTools: {}, serverResources: {}, logging: {},
          message: { text: {} }, updateModelContext: { text: {} },
          sandbox: {},
        },
        hostContext: hostContext(card),
      });
    case "ui/notifications/initialized":
      return start(card);
    case "ui/notifications/size-changed":
      return resize(card, params);
    case "notifications/message":
      console.debug("[mcp app]", card.app.toolName, params);
      return undefined;
    case "ping":
      return isRequest ? reply(card, id, {}) : undefined;
    default:
      return isRequest ? request(card, method, params, id) : undefined;
  }
}

// The requests that reach out of the frame. Each answers exactly once.
async function request(card, method, params, id) {
  switch (method) {
    case "ui/open-link":
      return openLink(card, params, id);
    case "ui/request-display-mode":
      return reply(card, id, { mode: setDisplayMode(card, params?.mode) });
    case "ui/message":
      return guarded(card, id, () => sendMessage(card, params, id));
    case "ui/update-model-context":
      return guarded(card, id, () => updateContext(card, params, id));
    case "tools/call":
      return guarded(card, id, () => proxy(card, id, "tools/call", { name: params?.name, arguments: params?.arguments }));
    case "resources/read":
      return proxy(card, id, "resources/read", { uri: params?.uri });
    default:
      return fail(card, id, -32601, `Method not found: ${method}`);
  }
}

// An inert card (drawn from a stored result) may not act until the user activates it.
function guarded(card, id, run) {
  return card.active ? run() : fail(card, id, -32000, "This app is inactive. Activate it first.");
}

async function start(card) {
  if (card.started) return;
  card.started = true;
  card.initialized = true;
  notify(card, "ui/notifications/tool-input", { arguments: parseJson(card.call.toolInput) ?? {} });
  const result = await resultOf(card);
  if (result) notify(card, "ui/notifications/tool-result", result);
  else removeCard(card);
}

async function resultOf(card) {
  if (card.call.resultJson) return parseJson(card.call.resultJson);
  try {
    const res = await fetch(`/api/conversations/${encodeURIComponent(card.convId)}/mcp-apps/${encodeURIComponent(card.call.toolCallId)}/result`);
    return res.ok ? await res.json() : null;
  } catch {
    return null;
  }
}

async function proxy(card, id, method, body) {
  const path = method === "tools/call" ? "tools/call" : "resources/read";
  try {
    const res = await fetch(`/api/mcp-apps/${encodeURIComponent(card.app.serverId)}/${path}`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!res.ok) return fail(card, id, -32000, `The request was refused (${res.status}).`);
    return reply(card, id, await res.json());
  } catch {
    return fail(card, id, -32000, "The request could not be made.");
  }
}

function openLink(card, params, id) {
  let url;
  try {
    url = new URL(String(params?.url));
  } catch {
    return fail(card, id, -32602, "Invalid url.");
  }
  if (url.protocol !== "https:" && url.protocol !== "http:") return fail(card, id, -32602, "Only http and https links open.");
  // A real user gesture only: a view must not open tabs on its own.
  if (!navigator.userActivation?.isActive) return fail(card, id, -32000, "Opening a link needs a click.");
  window.open(url.href, "_blank", "noopener,noreferrer");
  return reply(card, id, {});
}

// ui/message: a user turn the view asks for. The user confirms the exact words; it is refused while a turn streams.
async function sendMessage(card, params, id) {
  const text = textOf(params?.content);
  if (!text || text.length > MAX_TEXT_CHARS) return fail(card, id, -32602, "The message is empty or too long.");
  if (host.isStreaming()) return fail(card, id, -32000, "A reply is streaming; try again afterwards.");
  const approved = await askUserConfirm(`The app "${card.app.toolName}" wants to send this message as you:\n\n${text}`);
  if (!approved) return fail(card, id, -32000, "The user declined.");
  if (host.isStreaming()) return fail(card, id, -32000, "A reply is streaming; try again afterwards.");
  await reply(card, id, {});
  host.send(text);
  return undefined;
}

// ui/update-model-context: held per card (last write wins) and sent with the next chat request as untrusted context.
async function updateContext(card, params, id) {
  const text = textOf(params?.content) ?? (params?.structuredContent ? JSON.stringify(params.structuredContent) : null);
  if (text == null || text.length > MAX_CONTEXT_CHARS) return fail(card, id, -32602, "The context is empty or too large.");
  if (otherContextLength(card) + text.length > MAX_TOTAL_CONTEXT_CHARS) {
    return fail(card, id, -32000, "Too much context from open apps already; the assistant cannot take more.");
  }
  const approved = await askUserConfirm(`The app "${card.app.toolName}" wants the assistant to know this:\n\n${text}`);
  if (!approved) return fail(card, id, -32000, "The user declined.");
  card.context = text;
  return reply(card, id, {});
}

function otherContextLength(card) {
  let total = 0;
  for (const other of cards) {
    if (other !== card && other.convId === card.convId && other.context) total += other.context.length;
  }
  return total;
}

function textOf(content) {
  const blocks = Array.isArray(content) ? content : content ? [content] : [];
  const parts = blocks.filter((b) => b?.type === "text" && typeof b.text === "string").map((b) => b.text);
  return parts.length ? parts.join("\n") : null;
}

function setDisplayMode(card, mode) {
  if (mode === "fullscreen" || mode === "inline") {
    card.displayMode = mode;
    card.root.classList.toggle("mcp-app--full", mode === "fullscreen");
    notify(card, "ui/notifications/host-context-changed", { displayMode: mode, containerDimensions: dimensions(card) });
  }
  return card.displayMode; // pip is declined: the current mode is the answer
}

function resize(card, params) {
  const height = Number(params?.height);
  if (Number.isFinite(height) && card.displayMode === "inline") {
    card.iframe.style.height = `${Math.min(MAX_HEIGHT, Math.max(MIN_HEIGHT, Math.ceil(height)))}px`;
  }
}

function hostContext(card) {
  return {
    toolInfo: { id: card.call.toolCallId, tool: { name: card.app.toolName } },
    theme: document.documentElement.dataset.theme === "light" ? "light" : "dark",
    styles: { variables: styleVariables() },
    displayMode: card.displayMode,
    availableDisplayModes: ["inline", "fullscreen"],
    containerDimensions: dimensions(card),
    locale: navigator.language,
    timeZone: Intl.DateTimeFormat().resolvedOptions().timeZone,
    userAgent: navigator.userAgent,
    platform: "web",
    deviceCapabilities: { touch: matchMedia("(pointer: coarse)").matches, hover: matchMedia("(hover: hover)").matches },
  };
}

function dimensions(card) {
  return card.displayMode === "fullscreen"
    ? { width: innerWidth, height: innerHeight }
    : { width: card.iframe.clientWidth, maxHeight: MAX_HEIGHT };
}

// The app's own tokens (themes.css) under the spec's standard names, read from what is painted right now.
function styleVariables() {
  const css = getComputedStyle(document.documentElement);
  const token = (name) => css.getPropertyValue(name).trim();
  const map = {
    "--color-background-primary": "--bg-raised", "--color-background-secondary": "--bg", "--color-background-tertiary": "--bg-inset",
    "--color-text-primary": "--ink", "--color-text-secondary": "--ink-dim", "--color-text-tertiary": "--ink-faint",
    "--color-border-primary": "--line", "--color-border-secondary": "--line-soft",
    "--color-text-danger": "--red", "--color-text-success": "--green", "--color-text-warning": "--warn", "--color-text-info": "--amber",
    "--color-ring-primary": "--amber",
  };
  const variables = {};
  for (const [standard, own] of Object.entries(map)) {
    const value = token(own);
    if (value) variables[standard] = value;
  }
  variables["--font-sans"] = getComputedStyle(document.body).fontFamily;
  variables["--font-mono"] = "ui-monospace, SFMono-Regular, Menlo, Consolas, monospace";
  variables["--border-radius-md"] = "8px";
  variables["--border-width-regular"] = "1px";
  return variables;
}

// Host to view. Nothing goes before `initialized`, and a removed frame has no window to send to.
function notify(card, method, params) {
  if (!card.initialized) return;
  card.iframe.contentWindow?.postMessage({ jsonrpc: "2.0", method, params }, "*");
}

function reply(card, id, result) {
  card.iframe.contentWindow?.postMessage({ jsonrpc: "2.0", id, result }, "*");
}

function fail(card, id, code, message) {
  card.iframe.contentWindow?.postMessage({ jsonrpc: "2.0", id, error: { code, message } }, "*");
}

// The frame goes only after the view has been told (`ui/resource-teardown`), bounded so a silent view cannot stall it.
function removeCard(card) {
  if (!cards.delete(card)) return;
  const win = card.iframe.contentWindow;
  if (card.initialized && win) {
    win.postMessage({ jsonrpc: "2.0", id: `teardown-${Date.now()}`, method: "ui/resource-teardown", params: { reason: "removed" } }, "*");
  }
  setTimeout(() => card.root.remove(), card.initialized ? 100 : 0);
}

function activateButton(card) {
  const button = el("button", "mcp-app__activate", "Activate app");
  button.type = "button";
  button.title = "Let this app call its server and talk to the assistant";
  button.addEventListener("click", () => {
    card.active = true;
    button.remove();
  });
  return button;
}

function modeButton(card) {
  const button = el("button", "mcp-app__mode", "⤢");
  button.type = "button";
  button.title = "Full screen";
  button.setAttribute("aria-label", "Full screen");
  button.addEventListener("click", () => setDisplayMode(card, card.displayMode === "inline" ? "fullscreen" : "inline"));
  return button;
}

function parseJson(text) {
  try {
    return text == null ? null : JSON.parse(text);
  } catch {
    return null;
  }
}

function el(tag, className, text) {
  const node = document.createElement(tag);
  node.className = className;
  if (text != null) node.textContent = text;
  return node;
}
