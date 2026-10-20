import { test } from "node:test";
import assert from "node:assert/strict";
import { TOOLS, createToolRunner, listTools, page } from "../lib/tools.js";
import { MAX_RESULT_CHARS } from "../lib/limits.js";

const OWN = "chrome-extension://ddfhnmhjjdmlfpidpampclimjhnmmnmm/approve.html";

function fakeBrowser(tabs) {
  const calls = [];
  const urls = new Map(tabs.map((t) => [t.id, t.url]));
  const pending = new Map(tabs.filter((t) => t.pendingUrl).map((t) => [t.id, t.pendingUrl]));
  const dismissed = new Set(tabs.filter((t) => t.dismissed).map((t) => t.id));
  const record = (name, value) => async (...args) => {
    calls.push([name, ...args]);
    return typeof value === "function" ? value(...args) : value;
  };
  return {
    calls,
    urls,
    tabInfo: async (id) => (urls.has(id)
      ? { urls: [urls.get(id), pending.get(id)].filter(Boolean), title: tabs.find((t) => t.id === id)?.title ?? "", dismissed: dismissed.has(id) }
      : null),
    listTabs: async () => tabs.map((t) => ({ ...t, url: urls.get(t.id) })),
    newTab: record("newTab", { id: 99 }),
    closeTab: record("closeTab"),
    activateTab: record("activateTab"),
    navigate: record("navigate", (id, url) => ({ url, title: "T" })),
    snapshot: record("snapshot", "s".repeat(20_000)),
    find: record("find", "e1 button OK"),
    readText: record("readText", "hello"),
    consoleMessages: record("consoleMessages", ""),
    screenshot: record("screenshot", { data: "AAAA", mimeType: "image/jpeg", width: 1280, height: 720, scale: 1.5 }),
    click: record("click", "clicked"),
    clickAt: record("clickAt", "clicked at"),
    type: record("type", "typed"),
    key: record("key", "pressed"),
    scroll: record("scroll", "scrolled"),
    evaluate: record("evaluate", "42"),
  };
}

function runner({ tabs = [{ id: 1, url: "https://example.com/", title: "Ex", active: true }], allow = true, blocked = [] } = {}) {
  const browser = fakeBrowser(tabs);
  const reviews = [];
  const gate = {
    review: async (name, args, meta, signal) => {
      reviews.push([name, meta, signal]);
      return typeof allow === "function" ? allow(browser) : allow ? { allowed: true } : { allowed: false, text: "no" };
    },
  };
  return { browser, reviews, run: createToolRunner({ browser, gate, blockedHosts: () => blocked }) };
}

test("only the reading tools are declared read-only, and only evaluate auto-runs", () => {
  const readOnly = listTools().filter((t) => t.annotations.readOnlyHint).map((t) => t.name).sort();
  assert.deepEqual(readOnly, ["console", "find", "list_tabs", "read_text", "screenshot", "scroll", "snapshot"]);
  assert.deepEqual(Object.keys(TOOLS).filter((n) => TOOLS[n].autoRun), ["evaluate"]);
  assert.equal(TOOLS.manage_tabs.readOnly, undefined);
});

test("validation errors are isError results, never exceptions, and nothing runs", async () => {
  const { run, browser, reviews } = runner();
  for (const [name, args] of [["click", { tabId: 1 }], ["click", { tabId: "1", ref: "e1" }], ["snapshot", { tabId: 1, bogus: 1 }], ["nope", {}]]) {
    const result = await run.callTool(name, args);
    assert.equal(result.isError, true, `${name} ${JSON.stringify(args)}`);
  }
  assert.equal(browser.calls.length, 0);
  assert.equal(reviews.length, 0);
});

test("an unknown tab is a clear isError", async () => {
  const { run } = runner();
  const result = await run.callTool("snapshot", { tabId: 7 });
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /no tab with id 7/);
});

// AC 7b: the hard floor binds every tool, evaluate included, before the gate is even asked.
test("no tool acts on the extension's own pages or browser-internal pages", async () => {
  for (const url of [OWN, "chrome://settings", "devtools://devtools/x", "chrome-untrusted://x", "https://chromewebstore.google.com/detail/x"]) {
    const { run, browser, reviews } = runner({ tabs: [{ id: 1, url, title: "x" }] });
    for (const [name, args] of [["evaluate", { tabId: 1, expression: "1" }], ["snapshot", { tabId: 1 }], ["click", { tabId: 1, ref: "e1" }]]) {
      const result = await run.callTool(name, args);
      assert.equal(result.isError, true, `${name} on ${url}`);
    }
    assert.equal(browser.calls.length, 0);
    assert.equal(reviews.length, 0);
  }
});

test("a navigation target on the floor or the blocked list is refused", async () => {
  const { run, browser } = runner({ blocked: ["bank.example"] });
  for (const url of [OWN, "chrome://settings", "https://www.bank.example/login", "javascript:alert(1)", "file:///etc/passwd"]) {
    assert.equal((await run.callTool("navigate", { tabId: 1, url })).isError, true, url);
    assert.equal((await run.callTool("manage_tabs", { action: "new", url })).isError, true, url);
  }
  assert.equal(browser.calls.length, 0);
});

test("a blocked site refuses every tool, evaluate included", async () => {
  const { run, browser } = runner({ tabs: [{ id: 1, url: "https://login.bank.example/", title: "Bank" }], blocked: ["bank.example"] });
  assert.equal((await run.callTool("evaluate", { tabId: 1, expression: "document.cookie" })).isError, true);
  assert.equal((await run.callTool("read_text", { tabId: 1 })).isError, true);
  assert.equal(browser.calls.length, 0);
});

// The tab may navigate while the approval prompt is open: the floor is re-checked at execution.
test("the tab's address is re-checked after the gate", async () => {
  const { run, browser } = runner({
    allow: (b) => {
      b.urls.set(1, OWN);
      return { allowed: true };
    },
  });
  const result = await run.callTool("click", { tabId: 1, ref: "e1" });
  assert.equal(result.isError, true);
  assert.equal(browser.calls.length, 0);
});

test("a denied call never reaches the browser", async () => {
  const { run, browser } = runner({ allow: false });
  const result = await run.callTool("click", { tabId: 1, ref: "e1" });
  assert.deepEqual(result, { content: [{ type: "text", text: "no" }], isError: true });
  assert.equal(browser.calls.length, 0);
});

test("the gate is told each tool's own declaration", async () => {
  const { run, reviews } = runner();
  await run.callTool("snapshot", { tabId: 1 });
  await run.callTool("evaluate", { tabId: 1, expression: "1" });
  await run.callTool("click", { tabId: 1, ref: "e1" });
  assert.deepEqual(reviews.map(([n, m]) => [n, Boolean(m.readOnly), Boolean(m.autoRun)]), [
    ["snapshot", true, false], ["evaluate", false, true], ["click", false, false],
  ]);
});

// AC 9: the result fits the backend's 8 KiB cut with a paging hint, and paging reaches the end.
test("long results are paged under the cap and say how to continue", async () => {
  const { run } = runner();
  let offset = 0;
  let collected = "";
  for (let i = 0; i < 10; i++) {
    const text = (await run.callTool("snapshot", { tabId: 1, offset })).content[0].text;
    assert.ok(text.length <= MAX_RESULT_CHARS, `page ${i} is ${text.length}`);
    collected += text.replace(/^\[from offset \d+\]\n/, "").replace(/\n\[… \d+ more characters\. Call again with offset=\d+\.\]$/, "");
    const next = text.match(/offset=(\d+)\.\]$/);
    if (!next) break;
    offset = Number(next[1]);
  }
  assert.equal(collected, "s".repeat(20_000));
  assert.ok(MAX_RESULT_CHARS < 8 * 1024);
});

test("page() leaves short text untouched", () => {
  assert.equal(page("short"), "short");
  assert.equal(page("abcdef", 3), "[from offset 3]\ndef");
});

test("a screenshot is an MCP image block plus the pixel mapping", async () => {
  const { run } = runner();
  const result = await run.callTool("screenshot", { tabId: 1 });
  assert.deepEqual(result.content[0], { type: "image", data: "AAAA", mimeType: "image/jpeg" });
  assert.match(result.content[1].text, /1280×720 image pixels; 1 image pixel = 1.5 page pixels/);
});

test("list_tabs hides the address of floor and blocked tabs", async () => {
  const { run } = runner({
    tabs: [{ id: 1, url: "https://a.example/", title: "A", active: true }, { id: 2, url: OWN, title: "Approve" }, { id: 3, url: "https://bank.example/", title: "Bank" }],
    blocked: ["bank.example"],
  });
  const text = (await run.callTool("list_tabs", {})).content[0].text;
  assert.match(text, /^1 \(active\): A — https:\/\/a\.example\//);
  assert.match(text, /2: \[not available to the agent\]/);
  assert.match(text, /3: \[not available to the agent\]/);
  assert.ok(!text.includes("chrome-extension"));
  assert.ok(!text.includes("bank.example"));
});

// Critic M2: a blob: runs as its creator's origin, so a blocked site's blob tab is blocked too, evaluate included.
test("a blob: tab is judged by the site that created it", async () => {
  const { run, browser } = runner({ tabs: [{ id: 1, url: "blob:https://mybank.example/7d1c", title: "statement.pdf" }], blocked: ["mybank.example"] });
  assert.equal((await run.callTool("evaluate", { tabId: 1, expression: "document.cookie" })).isError, true);
  assert.equal(browser.calls.length, 0);
  const own = runner({ tabs: [{ id: 1, url: "blob:chrome-extension://ddfhnmhjjdmlfpidpampclimjhnmmnmm/x", title: "x" }] });
  assert.equal((await own.run.callTool("read_text", { tabId: 1 })).isError, true);
});

// A blank page can inherit an opener's origin, so only navigation (and closing/activating) is allowed on one.
test("an about:blank tab can be navigated, and nothing else", async () => {
  const { run, browser } = runner({ tabs: [{ id: 1, url: "about:blank", title: "" }] });
  for (const [name, args] of [["evaluate", { tabId: 1, expression: "1" }], ["read_text", { tabId: 1 }], ["click", { tabId: 1, ref: "e0" }]]) {
    assert.equal((await run.callTool(name, args)).isError, true, name);
  }
  assert.equal(browser.calls.length, 0);
  assert.equal((await run.callTool("navigate", { tabId: 1, url: "https://example.com/" })).isError, undefined);
  assert.equal((await run.callTool("manage_tabs", { action: "close", tabId: 1 })).isError, undefined);
});

// Critic L2: mid-navigation, the pending address is judged too.
test("a pending navigation to a refused address refuses the tab", async () => {
  const { run, browser } = runner({ tabs: [{ id: 1, url: "https://example.com/", pendingUrl: "https://mybank.example/", title: "Ex" }], blocked: ["mybank.example"] });
  assert.equal((await run.callTool("read_text", { tabId: 1 })).isError, true);
  assert.equal(browser.calls.length, 0);
});

// Critic M1: a dismissed debugging banner means "stop driving this tab", for every tool but closing it.
test("a tab whose banner the user dismissed is refused until it is closed", async () => {
  const { run, browser } = runner({ tabs: [{ id: 1, url: "https://example.com/", title: "Ex", dismissed: true }] });
  for (const [name, args] of [["read_text", { tabId: 1 }], ["snapshot", { tabId: 1 }], ["navigate", { tabId: 1, url: "https://example.com/x" }], ["evaluate", { tabId: 1, expression: "1" }]]) {
    const result = await run.callTool(name, args);
    assert.equal(result.isError, true, name);
    assert.match(result.content[0].text, /dismissed the debugging banner/);
  }
  assert.equal(browser.calls.length, 0);
  assert.equal((await run.callTool("manage_tabs", { action: "close", tabId: 1 })).isError, undefined);
});

// Critic M3: the reviewer is told which tab (title and address) a ref-only call acts on.
test("the gate is told the target tab and the navigation destination", async () => {
  const { run, reviews } = runner();
  await run.callTool("click", { tabId: 1, ref: "e1" });
  await run.callTool("navigate", { tabId: 1, url: "https://next.example/" });
  await run.callTool("manage_tabs", { action: "new", url: "https://new.example/" });
  assert.equal(reviews[0][1].target, "Tab 1: Ex — https://example.com/");
  assert.equal(reviews[1][1].target, "Tab 1: Ex — https://example.com/, then https://next.example/");
  assert.equal(reviews[2][1].target, "New tab at https://new.example/");
});

// Critic M4: a call whose channel closed while the prompt was open never runs.
test("a call cancelled during the gate never reaches the browser", async () => {
  const cancel = new AbortController();
  const { run, browser, reviews } = runner({
    allow: () => {
      cancel.abort();
      return { allowed: true };
    },
  });
  const result = await run.callTool("click", { tabId: 1, ref: "e1" }, cancel.signal);
  assert.equal(result.isError, true);
  assert.equal(reviews[0][2], cancel.signal, "the signal reaches the gate");
  assert.equal(browser.calls.length, 0);
});

test("an adapter failure is an isError result", async () => {
  const { run, browser } = runner();
  browser.click = async () => {
    throw new Error("No node with ref e9");
  };
  const result = await run.callTool("click", { tabId: 1, ref: "e9" });
  assert.equal(result.isError, true);
  assert.match(result.content[0].text, /No node with ref e9/);
});
