// The composition root (the service worker): storage → bridge → per-channel MCP server → tool runner → gate →
// browser adapter. Everything with logic lives in lib/ and is node-tested. This file only wires chrome.* in.
//
// The service worker can be suspended and woken at any time. Top-level code runs on every wake, so the bridge is
// (re)started from here, and a one-minute alarm wakes the worker if nothing else does.

import { browser } from "./browser.js";
import { Status, createBridge } from "./lib/bridge.js";
import { normalizeServer, parseBlockedSites } from "./lib/config.js";
import { createGate } from "./lib/gate.js";
import { createMcpChannel } from "./lib/mcp-server.js";
import { createToolRunner, listTools } from "./lib/tools.js";

const VERSION = chrome.runtime.getManifest().version;
const APPROVE_PAGE = chrome.runtime.getURL("approve.html");
const APPROVED_KEY = "approvedTools";

// ---- the "always for this tool" set: chrome.storage.session (survives a suspended worker, dies with the
// browser session, unreadable from content scripts at the default access level) ----
const approved = {
  async has(name) {
    const { [APPROVED_KEY]: names = [] } = await chrome.storage.session.get(APPROVED_KEY);
    return names.includes(name);
  },
  async add(name) {
    const { [APPROVED_KEY]: names = [] } = await chrome.storage.session.get(APPROVED_KEY);
    if (!names.includes(name)) await chrome.storage.session.set({ [APPROVED_KEY]: [...names, name] });
  },
};

const settings = () => chrome.storage.local.get({ server: "", key: "", yolo: false, blocked: "" });

// ---- approval prompts: one popup window per request; the page fetches the request and posts the answer ----
const pending = new Map();

// `signal` aborts when the gate stops waiting (timeout, or the request's channel closed): the window closes at once,
// so a late click never looks accepted.
function prompt(request, signal) {
  const id = crypto.randomUUID();
  return new Promise((resolve, reject) => {
    pending.set(id, { request, resolve, windowId: null });
    signal?.addEventListener("abort", () => resolve("deny"), { once: true });
    chrome.windows.create({ url: `${APPROVE_PAGE}?id=${id}`, type: "popup", width: 560, height: 680, focused: true })
      .then((win) => {
        const entry = pending.get(id);
        if (entry) entry.windowId = win.id;
        else chrome.windows.remove(win.id).catch(() => {}); // aborted before the window even opened
      })
      .catch((error) => {
        pending.delete(id);
        reject(error);
      });
  }).finally(() => {
    const entry = pending.get(id);
    pending.delete(id);
    if (entry?.windowId != null) chrome.windows.remove(entry.windowId).catch(() => {});
  });
}

// A closed approval window is a deny (the gate treats a rejected prompt as one).
chrome.windows.onRemoved.addListener((windowId) => {
  for (const [id, entry] of pending) {
    if (entry.windowId === windowId) {
      pending.delete(id);
      entry.resolve("deny");
    }
  }
});

// Only this extension's own approval page may fetch a request or answer one. No content scripts are declared and
// externally_connectable is absent, so web pages cannot message the worker at all; this is defence in depth.
chrome.runtime.onMessage.addListener((message, sender, reply) => {
  const fromApprovePage = sender.id === chrome.runtime.id && typeof sender.url === "string" && sender.url.startsWith(`${APPROVE_PAGE}?`);
  if (message?.type === "approval-request" && fromApprovePage) {
    reply(pending.get(message.id)?.request ?? null);
    return false;
  }
  if (message?.type === "approval-answer" && fromApprovePage) {
    const entry = pending.get(message.id);
    if (entry) {
      entry.resolve(message.answer);
    }
    return false;
  }
  if (message?.type === "reconnect" && sender.id === chrome.runtime.id && !sender.tab) {
    restart();
    return false;
  }
  return false;
});

const gate = createGate({ prompt, isYolo: async () => (await settings()).yolo === true, approved });
const tools = createToolRunner({ browser, gate, blockedHosts: async () => parseBlockedSites((await settings()).blocked) });

// ---- status: badge + chrome.storage.session for the popup ----
const BADGES = {
  [Status.CONNECTED]: ["on", "#2e7d32"],
  [Status.CONNECTING]: ["…", "#757575"],
  [Status.RETRYING]: ["…", "#ef6c00"],
  [Status.KEY_REJECTED]: ["key", "#c62828"],
  [Status.REPLACED]: ["off", "#757575"],
  [Status.STOPPED]: ["off", "#757575"],
  unconfigured: ["set", "#c62828"],
};

async function showStatus(status, detail = "") {
  const { yolo } = await settings();
  const [text, color] = BADGES[status] ?? ["?", "#757575"];
  await chrome.action.setBadgeText({ text: yolo && status === Status.CONNECTED ? "!" : text });
  await chrome.action.setBadgeBackgroundColor({ color: yolo ? "#c62828" : color });
  await chrome.storage.session.set({ status: { status, detail, at: Date.now() } });
}

// ---- the bridge loop ----
let current = null;

async function start() {
  if (current) return;
  const { server, key } = await settings();
  const origin = normalizeServer(server);
  if (!origin || !key) {
    await showStatus("unconfigured", "Open the extension's options and enter the server and the bridge key.");
    return;
  }
  const bridge = createBridge({
    server: origin,
    key,
    connect: (url, protocols) => new WebSocket(url, protocols),
    fetch: (url, init) => fetch(url, init),
    openChannel: (send) => createMcpChannel({ listTools, callTool: tools.callTool, send, version: VERSION }),
    onStatus: (status, detail) => void showStatus(status, detail),
  });
  current = bridge;
  bridge.start().then(() => {
    if (current === bridge) current = null;
  });
}

function restart() {
  const old = current;
  current = null;
  old?.stop();
  start();
}

chrome.storage.onChanged.addListener((changes, area) => {
  if (area === "local" && ("server" in changes || "key" in changes)) restart();
  if (area === "local" && "yolo" in changes) chrome.storage.session.get("status").then(({ status }) => status && showStatus(status.status, status.detail));
});

chrome.alarms.create("bridge-keepalive", { periodInMinutes: 1 });
chrome.alarms.onAlarm.addListener(() => start());
chrome.runtime.onStartup.addListener(() => start());
chrome.runtime.onInstalled.addListener(() => start());
start();
