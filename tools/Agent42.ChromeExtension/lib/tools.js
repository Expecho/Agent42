// The browser tools the extension serves over MCP, independent of chrome.*: definitions, argument checks, the
// refusals, the gate and result shaping all live here, over an injected `browser` adapter (browser.js is the
// real one; node tests pass a fake).
//
// Order per call: validate → hard floor / blocked site on the tab (and on a navigation target) → approval gate →
// the SAME refusals again on the tab's CURRENT address (it may have navigated while the prompt was open) → run.
// Every failure is a tool result with isError, never an exception, so the model reads what went wrong.
//
// The tool descriptions are what the model reads, so they carry the rules that matter: refs come from the
// latest snapshot/find of that tab, and long results are paged with `offset`.

import { blockedRefusal, hardFloorRefusal } from "./config.js";
import { CANCELLED_TEXT, denialResult } from "./gate.js";
import { MAX_RESULT_CHARS, MAX_SCREENSHOT_BASE64 } from "./limits.js";

const tabId = { type: "integer", description: "The tab id, from list_tabs." };
const offset = { type: "integer", minimum: 0, description: "Character offset to continue a paged result from. Default 0." };
const ref = { type: "string", description: "An element ref (like \"e12\") from the latest snapshot or find of this tab." };

function schema(properties, required = []) {
  return { type: "object", properties, required, additionalProperties: false };
}

/** name → definition. `readOnly` passes the gate unprompted (unless an argument is URL-shaped); `autoRun` always does. */
export const TOOLS = {
  list_tabs: {
    description: "List the open tabs in the user's Chrome: id, title, URL and which one is active.",
    inputSchema: schema({}),
    readOnly: true,
  },
  manage_tabs: {
    description: "Open a new tab (optionally at a URL), close a tab, or bring a tab to the front.",
    inputSchema: schema({
      action: { type: "string", enum: ["new", "close", "activate"] },
      tabId: { ...tabId, description: "The tab to close or activate." },
      url: { type: "string", description: "For action \"new\": the address to open." },
    }, ["action"]),
  },
  navigate: {
    description: "Load a URL in a tab and wait for it to finish loading.",
    inputSchema: schema({ tabId, url: { type: "string", description: "An http(s) address." } }, ["tabId", "url"]),
    needsTab: true,
  },
  snapshot: {
    description: "A compact text outline of the page in a tab: headings, text, links, buttons and form fields, each " +
      "interactive element with a ref for click/type. Refs are only valid until the page changes; take a new " +
      "snapshot after navigating. Long pages are paged: pass offset to continue.",
    inputSchema: schema({ tabId, offset }, ["tabId"]),
    readOnly: true,
    needsTab: true,
  },
  find: {
    description: "Find elements on the page whose text, label or role matches a query. Returns refs for click/type.",
    inputSchema: schema({ tabId, query: { type: "string", description: "Text to look for." } }, ["tabId", "query"]),
    readOnly: true,
    needsTab: true,
  },
  read_text: {
    description: "The page's readable text, without markup. Long pages are paged: pass offset to continue.",
    inputSchema: schema({ tabId, offset }, ["tabId"]),
    readOnly: true,
    needsTab: true,
  },
  screenshot: {
    description: "A picture of the visible part of the page. The result says how image pixels map to page pixels; " +
      "use click_at with coordinates from this image. Prefer snapshot/find when you only need text or refs.",
    inputSchema: schema({ tabId }, ["tabId"]),
    readOnly: true,
    needsTab: true,
  },
  click: {
    description: "Click an element by its ref from the latest snapshot or find of this tab.",
    inputSchema: schema({ tabId, ref }, ["tabId", "ref"]),
    needsTab: true,
  },
  click_at: {
    description: "Click at a point given in the image pixels of the latest screenshot of this tab.",
    inputSchema: schema({ tabId, x: { type: "number", minimum: 0 }, y: { type: "number", minimum: 0 } }, ["tabId", "x", "y"]),
    needsTab: true,
  },
  type: {
    description: "Focus an element by ref and type text into it. Set submit to press Enter afterwards.",
    inputSchema: schema({ tabId, ref, text: { type: "string" }, submit: { type: "boolean" } }, ["tabId", "ref", "text"]),
    needsTab: true,
  },
  key: {
    description: "Press a key or a combination in a tab, for example \"Enter\", \"Escape\", \"Tab\" or \"Control+A\".",
    inputSchema: schema({ tabId, keys: { type: "string" } }, ["tabId", "keys"]),
    needsTab: true,
  },
  scroll: {
    description: "Scroll the page up or down by about one screen, or scroll an element (by ref) into view.",
    inputSchema: schema({ tabId, direction: { type: "string", enum: ["up", "down"] }, ref }, ["tabId"]),
    readOnly: true,
    needsTab: true,
  },
  console: {
    description: "Recent console messages from a tab (logs, warnings, errors) since the extension first used it.",
    inputSchema: schema({ tabId, offset }, ["tabId"]),
    readOnly: true,
    needsTab: true,
  },
  evaluate: {
    description: "Run a JavaScript expression in the page and return its value as JSON. Runs without asking " +
      "the user; it is refused on browser-internal pages and on sites the user blocked.",
    inputSchema: schema({ tabId, expression: { type: "string" } }, ["tabId", "expression"]),
    autoRun: true,
    needsTab: true,
  },
};

/** The tools/list entries. */
export function listTools() {
  return Object.entries(TOOLS).map(([name, tool]) => ({
    name,
    description: tool.description,
    inputSchema: tool.inputSchema,
    annotations: { readOnlyHint: Boolean(tool.readOnly) },
  }));
}

const textResult = (text) => ({ content: [{ type: "text", text }] });

/**
 * Cuts `text` to the result budget and says how to read on, so the backend's 8 KiB truncation never silently
 * eats the tail. The hint counts against the budget.
 */
export function page(text, from = 0) {
  const start = Math.min(Math.max(0, from | 0), text.length);
  const room = MAX_RESULT_CHARS - 120;
  const slice = text.slice(start, start + room);
  const end = start + slice.length;
  if (end >= text.length) return start === 0 ? slice : `[from offset ${start}]\n${slice}`;
  return `${start === 0 ? "" : `[from offset ${start}]\n`}${slice}\n[… ${text.length - end} more characters. Call again with offset=${end}.]`;
}

function validate(def, args) {
  const { properties, required } = def.inputSchema;
  for (const name of required) {
    if (args[name] === undefined || args[name] === null) return `Missing required argument "${name}".`;
  }
  for (const [name, value] of Object.entries(args)) {
    const prop = properties[name];
    if (!prop) return `Unknown argument "${name}".`;
    if (prop.type === "integer" && !Number.isInteger(value)) return `"${name}" must be an integer.`;
    if (prop.type === "number" && (typeof value !== "number" || !Number.isFinite(value))) return `"${name}" must be a number.`;
    if (prop.type === "string" && typeof value !== "string") return `"${name}" must be a string.`;
    if (prop.type === "boolean" && typeof value !== "boolean") return `"${name}" must be true or false.`;
    if (prop.enum && !prop.enum.includes(value)) return `"${name}" must be one of ${prop.enum.join(", ")}.`;
    if (prop.minimum !== undefined && value < prop.minimum) return `"${name}" must be at least ${prop.minimum}.`;
  }
  return null;
}

/** The address a call would load, if any: refused like a tab address before anything runs. */
function navigationTarget(name, args) {
  if (name === "navigate") return args.url;
  if (name === "manage_tabs" && args.action === "new" && args.url) return args.url;
  return null;
}

function targetTab(name, args) {
  if (TOOLS[name].needsTab) return args.tabId;
  if (name === "manage_tabs" && args.action !== "new") return args.tabId;
  return undefined;
}

// A bare about:blank (a new tab) passes the floor, so the agent can navigate it, and that is all it can do there.
// A blank page can inherit an opener's origin (window.open from a blocked site), so no script or read runs on one.
const BLANK_TAB_TOOLS = new Set(["navigate", "manage_tabs"]);

/**
 * @param {object} deps
 * @param {object} deps.browser  the adapter (see browser.js)
 * @param {{review(name: string, args: object, meta: object): Promise<{allowed: boolean, text?: string}>}} deps.gate
 * @param {() => Promise<string[]> | string[]} deps.blockedHosts
 */
export function createToolRunner({ browser, gate, blockedHosts }) {
  async function refusalFor(url) {
    return hardFloorRefusal(url) ?? blockedRefusal(url, await blockedHosts());
  }

  // Returns {refusal} or {target}: the text the reviewer is shown about where the call acts.
  async function checkTargets(name, args) {
    const tab = targetTab(name, args);
    let target = "";
    if (tab !== undefined) {
      if (!Number.isInteger(tab)) return { refusal: "This action needs a tabId." };
      const info = await browser.tabInfo(tab);
      if (info === null) return { refusal: `There is no tab with id ${tab}. Call list_tabs.` };
      if (info.dismissed && name !== "manage_tabs") {
        return { refusal: "The user dismissed the debugging banner on this tab, so the extension will not act on it again until the tab is closed." };
      }
      // Both the committed and a pending address: mid-navigation, the old one alone would be judged.
      for (const url of info.urls) {
        const refused = await refusalFor(url);
        if (refused) return { refusal: refused };
        if (url === "about:blank" && !BLANK_TAB_TOOLS.has(name)) {
          return { refusal: "This tab has no page loaded. Navigate it to an address first." };
        }
      }
      target = `Tab ${tab}: ${info.title || "(untitled)"} — ${info.urls.join(" → ")}`;
    }
    const destination = navigationTarget(name, args);
    if (destination !== null) {
      let parsed;
      try {
        parsed = new URL(destination);
      } catch {
        return { refusal: "The url must be an absolute http(s) address." };
      }
      if (parsed.protocol !== "http:" && parsed.protocol !== "https:") return { refusal: "The url must be an absolute http(s) address." };
      const refused = await refusalFor(destination);
      if (refused) return { refusal: refused };
      target = target ? `${target}, then ${destination}` : `New tab at ${destination}`;
    }
    return { target };
  }

  async function run(name, args) {
    switch (name) {
      case "list_tabs": {
        const tabs = await browser.listTabs();
        const visible = [];
        for (const t of tabs) {
          // Floor and blocked tabs are listed by id only, so the model can still close or avoid them.
          const hidden = t.url !== "about:blank" && (await refusalFor(t.url)) !== null;
          visible.push(hidden
            ? `${t.id}${t.active ? " (active)" : ""}: [not available to the agent]`
            : `${t.id}${t.active ? " (active)" : ""}: ${t.title} — ${t.url}`);
        }
        return textResult(page(visible.join("\n") || "No open tabs."));
      }
      case "manage_tabs": {
        if (args.action === "new") {
          const tab = await browser.newTab(args.url);
          return textResult(`Opened tab ${tab.id}${args.url ? ` at ${args.url}` : ""}.`);
        }
        if (args.action === "close") {
          await browser.closeTab(args.tabId);
          return textResult(`Closed tab ${args.tabId}.`);
        }
        await browser.activateTab(args.tabId);
        return textResult(`Tab ${args.tabId} is now in front.`);
      }
      case "navigate": {
        const result = await browser.navigate(args.tabId, args.url);
        return textResult(`Loaded ${result.url}${result.title ? ` — ${result.title}` : ""}.`);
      }
      case "snapshot":
        return textResult(page(await browser.snapshot(args.tabId), args.offset));
      case "find":
        return textResult(page(await browser.find(args.tabId, args.query)));
      case "read_text":
        return textResult(page(await browser.readText(args.tabId), args.offset));
      case "console":
        return textResult(page(await browser.consoleMessages(args.tabId), args.offset));
      case "screenshot": {
        const shot = await browser.screenshot(args.tabId);
        if (shot.data.length > MAX_SCREENSHOT_BASE64) return denialResult("The screenshot was too large to send.");
        return {
          content: [
            { type: "image", data: shot.data, mimeType: shot.mimeType },
            {
              type: "text",
              text: `Screenshot of tab ${args.tabId}: ${shot.width}×${shot.height} image pixels; ` +
                `1 image pixel = ${shot.scale} page pixels. Pass x,y in image pixels to click_at.`,
            },
          ],
        };
      }
      case "click":
        return textResult(await browser.click(args.tabId, args.ref));
      case "click_at":
        return textResult(await browser.clickAt(args.tabId, args.x, args.y));
      case "type":
        return textResult(await browser.type(args.tabId, args.ref, args.text, Boolean(args.submit)));
      case "key":
        return textResult(await browser.key(args.tabId, args.keys));
      case "scroll":
        return textResult(await browser.scroll(args.tabId, args.direction ?? "down", args.ref));
      case "evaluate":
        return textResult(page(await browser.evaluate(args.tabId, args.expression)));
      default:
        return denialResult(`Unknown tool "${name}".`);
    }
  }

  /**
   * Runs one tools/call and always resolves to a CallToolResult. `signal` aborts when the request's channel closes:
   * a call cancelled while its prompt was open is never run.
   */
  async function callTool(name, rawArgs, signal) {
    const def = Object.hasOwn(TOOLS, name) ? TOOLS[name] : null;
    if (!def) return denialResult(`Unknown tool "${name}".`);
    const args = rawArgs ?? {};
    const invalid = validate(def, args);
    if (invalid) return denialResult(invalid);

    try {
      const before = await checkTargets(name, args);
      if (before.refusal) return denialResult(before.refusal);

      const meta = { readOnly: def.readOnly, autoRun: def.autoRun, target: before.target };
      const decision = await gate.review(name, args, meta, signal);
      if (!decision.allowed) return denialResult(decision.text);
      if (signal?.aborted) return denialResult(CANCELLED_TEXT);

      const after = await checkTargets(name, args); // the tab may have navigated while the prompt was open
      if (after.refusal) return denialResult(after.refusal);

      return await run(name, args);
    } catch (error) {
      return denialResult(`The browser action failed: ${error?.message ?? error}`);
    }
  }

  return { callTool };
}
