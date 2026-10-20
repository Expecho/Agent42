// The real browser adapter behind lib/tools.js: chrome.tabs for tabs, chrome.scripting for the DOM outline and
// element refs, and chrome.debugger (the DevTools protocol) for real input events, console messages, evaluate
// and screenshots. Not unit-testable (it IS the Chrome boundary). The manual check in the ADR covers it.
//
// Every refusal (hard floor, blocked sites) and the approval gate run BEFORE any of this, in lib/tools.js.
// Injected functions take only their explicit arguments and never touch extension storage: content scripts can
// read chrome.storage.local, and the bridge key lives there.
//
// Refs: a snapshot tags each interactive element with data-agent42-ref="<nonce>-<n>" and shows it as "e<n>". The
// nonce is per tab, in memory, so a page cannot pre-plant a ref the model will pick, and refs from before a
// service-worker restart simply stop resolving ("take a new snapshot").

import { jpegSize } from "./lib/jpeg.js";
import { MAX_SCREENSHOT_BASE64, SCREENSHOT_MAX_SIDE } from "./lib/limits.js";

const CDP_VERSION = "1.3";
const NAVIGATION_TIMEOUT_MS = 30_000;
const CONSOLE_BUFFER = 200;
const DISMISSED_TEXT = "The user dismissed the debugging banner on this tab, so the extension will not act on it again " +
  "until the tab is closed. Ask the user, or use another tab.";

const attached = new Set();
const consoleByTab = new Map();
const nonceByTab = new Map();
const screenshotScale = new Map();

// Tabs whose "being debugged" banner the user dismissed. That is the user saying "stop driving this tab", so every
// tool refuses it until the tab is closed. Not until it reloads: a page could reload itself to undo the user's choice.
const dismissed = new Set();

chrome.debugger.onDetach.addListener(({ tabId }, reason) => {
  attached.delete(tabId);
  if (reason === "canceled_by_user") dismissed.add(tabId);
});

chrome.debugger.onEvent.addListener(({ tabId }, method, params) => {
  const lines = consoleByTab.get(tabId);
  if (!lines) return;
  let line = null;
  if (method === "Runtime.consoleAPICalled") {
    const text = (params.args ?? []).map((a) => a.value ?? a.description ?? a.type).join(" ");
    line = `[${params.type}] ${text}`;
  } else if (method === "Runtime.exceptionThrown") {
    line = `[exception] ${params.exceptionDetails?.exception?.description ?? params.exceptionDetails?.text ?? ""}`;
  } else if (method === "Log.entryAdded") {
    line = `[${params.entry.level}] ${params.entry.text}`;
  }
  if (line !== null) {
    lines.push(line.slice(0, 2_000));
    if (lines.length > CONSOLE_BUFFER) lines.shift();
  }
});

chrome.tabs.onRemoved.addListener((tabId) => {
  attached.delete(tabId);
  dismissed.delete(tabId);
  consoleByTab.delete(tabId);
  nonceByTab.delete(tabId);
  screenshotScale.delete(tabId);
});

async function ensureAttached(tabId) {
  if (attached.has(tabId)) return;
  if (dismissed.has(tabId)) throw new Error(DISMISSED_TEXT);
  try {
    await chrome.debugger.attach({ tabId }, CDP_VERSION);
  } catch (error) {
    throw new Error(`Could not attach to the tab: ${error?.message ?? error}`);
  }
  attached.add(tabId);
  consoleByTab.set(tabId, consoleByTab.get(tabId) ?? []);
  await chrome.debugger.sendCommand({ tabId }, "Runtime.enable");
  await chrome.debugger.sendCommand({ tabId }, "Log.enable");
}

async function cdp(tabId, method, params) {
  await ensureAttached(tabId);
  return chrome.debugger.sendCommand({ tabId }, method, params);
}

async function inPage(tabId, func, ...args) {
  const [frame] = await chrome.scripting.executeScript({ target: { tabId }, func, args });
  if (frame?.result && typeof frame.result === "object" && "error" in frame.result) throw new Error(frame.result.error);
  return frame?.result;
}

function nonceFor(tabId) {
  let nonce = nonceByTab.get(tabId);
  if (!nonce) {
    nonce = crypto.randomUUID().slice(0, 8);
    nonceByTab.set(tabId, nonce);
  }
  return nonce;
}

// ---- injected (runs in the page's isolated world; self-contained, arguments only) ----

function outlineInPage(nonce, query) {
  const attr = "data-agent42-ref";
  const interactive = "a[href],button,input,select,textarea,summary,[role=button],[role=link],[role=checkbox],[role=tab],[role=menuitem],[contenteditable=true]";
  const visible = (el) => {
    const style = getComputedStyle(el);
    return style.visibility !== "hidden" && style.display !== "none" && el.getClientRects().length > 0;
  };
  const clip = (s, n = 160) => (s.length > n ? `${s.slice(0, n)}…` : s);
  // A form field's name is its aria-label or its <label>; never its value (shown separately, and never for a password).
  const fieldName = (el) => el.getAttribute("aria-label") || el.labels?.[0]?.innerText ||
    (/^(submit|button|reset)$/.test(el.type) ? el.value : "") || el.placeholder || el.title || "";
  const isField = (el) => el.matches("input,textarea,select");
  const textOf = (el) => clip((isField(el) ? fieldName(el) : el.getAttribute("aria-label") || el.innerText || el.title || el.alt || "").replace(/\s+/g, " ").trim());
  let next = 0;
  for (const el of document.querySelectorAll(`[${attr}]`)) {
    const n = Number(String(el.getAttribute(attr)).split("-")[1]);
    if (String(el.getAttribute(attr)).startsWith(`${nonce}-`) && n >= next) next = n + 1;
  }
  const lines = [`Page: ${document.title} — ${location.href}`];
  const needle = query ? query.toLowerCase() : null;
  const walker = document.createTreeWalker(document.body ?? document.documentElement, NodeFilter.SHOW_ELEMENT);
  let total = 0;
  for (let el = walker.currentNode; el && total < 400_000; el = walker.nextNode()) {
    if (!(el instanceof HTMLElement) || !visible(el)) continue;
    let line = null;
    if (el.matches(interactive)) {
      let ref = el.getAttribute(attr);
      if (!ref || !ref.startsWith(`${nonce}-`)) {
        ref = `${nonce}-${next++}`;
        el.setAttribute(attr, ref);
      }
      const shown = `e${ref.split("-")[1]}`;
      const tag = el.tagName.toLowerCase();
      const role = el.getAttribute("role") || (tag === "a" ? "link" : tag === "input" ? (el.type || "text") : tag);
      let extra = "";
      if (tag === "a") extra = ` → ${clip(el.href, 120)}`;
      if (tag === "input" && el.type === "password") extra = el.value ? " (filled)" : " (empty)";
      else if ((tag === "input" || tag === "textarea") && el.value) extra = ` value="${clip(el.value, 80)}"`;
      if (el.type === "checkbox" || el.type === "radio") extra = el.checked ? " (checked)" : " (unchecked)";
      if (tag === "select") extra = ` = "${clip(el.selectedOptions[0]?.text ?? "", 60)}"`;
      line = `[${shown}] ${role} "${textOf(el)}"${extra}`;
    } else if (/^H[1-6]$/.test(el.tagName)) {
      line = `${"#".repeat(Number(el.tagName[1]))} ${textOf(el)}`;
    } else if (el.matches("p,li,td,th,label,figcaption,blockquote") && !el.querySelector(interactive)) {
      const text = textOf(el);
      if (text) line = text;
    }
    if (line && (!needle || line.toLowerCase().includes(needle))) {
      lines.push(line);
      total += line.length;
    }
  }
  return lines.join("\n");
}

function centerInPage(attrValue) {
  const el = document.querySelector(`[data-agent42-ref="${CSS.escape(attrValue)}"]`);
  if (!el) return { error: "No element with that ref any more. Take a new snapshot." };
  el.scrollIntoView({ block: "center", inline: "center" });
  const rect = el.getBoundingClientRect();
  return { x: rect.left + rect.width / 2, y: rect.top + rect.height / 2 };
}

function focusInPage(attrValue) {
  const el = document.querySelector(`[data-agent42-ref="${CSS.escape(attrValue)}"]`);
  if (!el) return { error: "No element with that ref any more. Take a new snapshot." };
  el.scrollIntoView({ block: "center" });
  el.focus();
  return { ok: true };
}

function scrollInPage(direction, attrValue) {
  if (attrValue) {
    const el = document.querySelector(`[data-agent42-ref="${CSS.escape(attrValue)}"]`);
    if (!el) return { error: "No element with that ref any more. Take a new snapshot." };
    el.scrollIntoView({ block: "center" });
  } else {
    window.scrollBy(0, (direction === "up" ? -0.9 : 0.9) * window.innerHeight);
  }
  return { y: Math.round(window.scrollY), height: document.documentElement.scrollHeight };
}

function textInPage() {
  return `Page: ${document.title} — ${location.href}\n\n${document.body?.innerText ?? ""}`;
}

// ---- keys ----

const KEYS = {
  Enter: [13, "Enter", "\r"], Escape: [27, "Escape"], Tab: [9, "Tab"], Backspace: [8, "Backspace"],
  Delete: [46, "Delete"], Space: [32, "Space", " "], ArrowUp: [38, "ArrowUp"], ArrowDown: [40, "ArrowDown"],
  ArrowLeft: [37, "ArrowLeft"], ArrowRight: [39, "ArrowRight"], Home: [36, "Home"], End: [35, "End"],
  PageUp: [33, "PageUp"], PageDown: [34, "PageDown"],
};
const MODIFIERS = { Alt: 1, Control: 2, Ctrl: 2, Meta: 4, Cmd: 4, Shift: 8 };

async function press(tabId, combo) {
  const parts = combo.split("+").map((p) => p.trim()).filter(Boolean);
  const name = parts.pop();
  let modifiers = 0;
  for (const m of parts) {
    if (!(m in MODIFIERS)) throw new Error(`Unknown modifier "${m}".`);
    modifiers |= MODIFIERS[m];
  }
  let event;
  if (name in KEYS) {
    const [code, keyName, text] = KEYS[name];
    event = { key: keyName === "Space" ? " " : keyName, code: keyName, windowsVirtualKeyCode: code, text: modifiers ? undefined : text };
  } else if (name.length === 1) {
    const upper = name.toUpperCase();
    event = { key: name, code: /[A-Z]/.test(upper) ? `Key${upper}` : undefined, windowsVirtualKeyCode: upper.charCodeAt(0), text: modifiers & 6 ? undefined : name };
  } else {
    throw new Error(`Unknown key "${name}".`);
  }
  await cdp(tabId, "Input.dispatchKeyEvent", { type: event.text ? "keyDown" : "rawKeyDown", modifiers, ...event });
  await cdp(tabId, "Input.dispatchKeyEvent", { type: "keyUp", modifiers, ...event, text: undefined });
}

async function clickPoint(tabId, x, y) {
  for (const type of ["mouseMoved", "mousePressed", "mouseReleased"]) {
    await cdp(tabId, "Input.dispatchMouseEvent", { type, x, y, button: type === "mouseMoved" ? "none" : "left", clickCount: 1 });
  }
}

function waitForLoad(tabId) {
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      chrome.tabs.onUpdated.removeListener(listener);
      reject(new Error("The page did not finish loading within 30 s."));
    }, NAVIGATION_TIMEOUT_MS);
    function listener(id, info, tab) {
      if (id === tabId && info.status === "complete") {
        clearTimeout(timer);
        chrome.tabs.onUpdated.removeListener(listener);
        resolve(tab);
      }
    }
    chrome.tabs.onUpdated.addListener(listener);
  });
}

// ---- the adapter ----

export const browser = {
  // Both the committed and the pending address, so a tab mid-navigation is judged on both. A tab with neither yields
  // [""], which the floor refuses (fail closed).
  async tabInfo(tabId) {
    let tab;
    try {
      tab = await chrome.tabs.get(tabId);
    } catch {
      return null;
    }
    const urls = [...new Set([tab.url, tab.pendingUrl].filter(Boolean))];
    return { urls: urls.length ? urls : [""], title: tab.title ?? "", dismissed: dismissed.has(tabId) };
  },

  async listTabs() {
    return (await chrome.tabs.query({})).map((t) => ({ id: t.id, url: t.url || t.pendingUrl || "", title: t.title ?? "", active: t.active }));
  },

  async newTab(url) {
    const tab = await chrome.tabs.create({ url: url || "about:blank", active: false });
    if (url) await waitForLoad(tab.id).catch(() => {});
    return tab;
  },

  closeTab: (tabId) => chrome.tabs.remove(tabId),

  async activateTab(tabId) {
    const tab = await chrome.tabs.update(tabId, { active: true });
    await chrome.windows.update(tab.windowId, { focused: true });
  },

  async navigate(tabId, url) {
    const loaded = waitForLoad(tabId);
    await chrome.tabs.update(tabId, { url });
    const tab = await loaded;
    nonceByTab.delete(tabId);
    return { url: tab.url, title: tab.title };
  },

  snapshot: (tabId) => inPage(tabId, outlineInPage, nonceFor(tabId), null),
  find: (tabId, query) => inPage(tabId, outlineInPage, nonceFor(tabId), query),
  readText: (tabId) => inPage(tabId, textInPage),

  async consoleMessages(tabId) {
    await ensureAttached(tabId);
    const lines = consoleByTab.get(tabId) ?? [];
    return lines.length ? lines.join("\n") : "No console messages since the extension started watching this tab.";
  },

  async click(tabId, ref) {
    const point = await inPage(tabId, centerInPage, `${nonceFor(tabId)}-${String(ref).replace(/^e/, "")}`);
    await clickPoint(tabId, point.x, point.y);
    return `Clicked ${ref}.`;
  },

  async clickAt(tabId, x, y) {
    const scale = screenshotScale.get(tabId);
    if (!scale) throw new Error("Take a screenshot of this tab first; click_at uses its coordinates.");
    await clickPoint(tabId, x * scale, y * scale);
    return `Clicked at (${x}, ${y}) in the screenshot.`;
  },

  async type(tabId, ref, text, submit) {
    await inPage(tabId, focusInPage, `${nonceFor(tabId)}-${String(ref).replace(/^e/, "")}`);
    await cdp(tabId, "Input.insertText", { text });
    if (submit) await press(tabId, "Enter");
    return `Typed ${text.length} characters into ${ref}${submit ? " and pressed Enter" : ""}.`;
  },

  async key(tabId, keys) {
    await press(tabId, keys);
    return `Pressed ${keys}.`;
  },

  async scroll(tabId, direction, ref) {
    const ref2 = ref ? `${nonceFor(tabId)}-${String(ref).replace(/^e/, "")}` : null;
    const where = await inPage(tabId, scrollInPage, direction, ref2);
    return `Scrolled; now at ${where.y} of ${where.height} page pixels.`;
  },

  async evaluate(tabId, expression) {
    const { result, exceptionDetails } = await cdp(tabId, "Runtime.evaluate", {
      expression, returnByValue: true, awaitPromise: true, userGesture: true, timeout: 10_000,
    });
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
    return result.type === "undefined" ? "undefined" : JSON.stringify(result.value ?? result.description ?? null);
  },

  async screenshot(tabId) {
    const { cssVisualViewport: vp } = await cdp(tabId, "Page.getLayoutMetrics");
    const longest = Math.max(vp.clientWidth, vp.clientHeight);
    const shrink = longest > SCREENSHOT_MAX_SIDE ? SCREENSHOT_MAX_SIDE / longest : 1;
    for (const quality of [60, 40, 25]) {
      const { data } = await cdp(tabId, "Page.captureScreenshot", {
        format: "jpeg", quality,
        clip: { x: vp.pageX, y: vp.pageY, width: vp.clientWidth, height: vp.clientHeight, scale: shrink },
      });
      if (data.length > MAX_SCREENSHOT_BASE64) continue;
      const size = jpegSize(data) ?? { width: Math.round(vp.clientWidth * shrink), height: Math.round(vp.clientHeight * shrink) };
      const scale = Number((vp.clientWidth / size.width).toFixed(4));
      screenshotScale.set(tabId, scale);
      return { data, mimeType: "image/jpeg", width: size.width, height: size.height, scale };
    }
    throw new Error("The page could not be captured small enough to send.");
  },
};
