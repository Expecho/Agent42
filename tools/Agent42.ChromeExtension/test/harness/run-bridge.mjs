// The extension's real core (bridge loop, MCP server, gate, tools) over a FAKE browser, against a real Agent42 bridge
// endpoint. Driven by ChromeExtensionBridgeTests (C#), which starts Kestrel and reads this process's stdout.
//
// It connects exactly as the extension does: Origin chrome-extension://<the pinned id> and the subprotocol pair.
// Node 22's WebSocket may set Origin (a browser's may not), which is why the backend never treats the Origin as a
// credential.
//
// Env: AGENT42_SERVER, AGENT42_BRIDGE_KEY, AGENT42_EXTENSION_ORIGIN, AGENT42_ANSWERS (comma-separated prompt answers,
// in order), AGENT42_HEARTBEAT_MS and AGENT42_PONG_TIMEOUT_MS (optional). Stdout: one line per event, "STATUS <s>", "PROMPT <tool>",
// "CALL <adapter method>". Exit: 0 stopped, 3 key rejected, 4 replaced.

import { Status, createBridge } from "../../lib/bridge.js";
import { createGate } from "../../lib/gate.js";
import { createMcpChannel } from "../../lib/mcp-server.js";
import { createToolRunner, listTools } from "../../lib/tools.js";

const env = process.env;
const answers = (env.AGENT42_ANSWERS ?? "").split(",").filter(Boolean);
const out = (line) => process.stdout.write(`${line}\n`);

// A tiny real JPEG header (SOI + APP0) is enough for the backend's magic-byte check.
const JPEG = Buffer.from([0xff, 0xd8, 0xff, 0xe0, 0x00, 0x10, 0x4a, 0x46, 0x49, 0x46, 0x00, 0x01, 0x42, 0x42]).toString("base64");

const call = (name, value) => async (...args) => {
  out(`CALL ${name} ${JSON.stringify(args)}`);
  return value;
};

const browser = {
  tabInfo: async (id) => (id === 1 ? { urls: ["https://example.com/"], title: "Example Domain", dismissed: false } : null),
  listTabs: async () => [{ id: 1, url: "https://example.com/", title: "Example Domain", active: true }],
  newTab: call("newTab", { id: 2 }),
  closeTab: call("closeTab"),
  activateTab: call("activateTab"),
  navigate: call("navigate", { url: "https://example.com/next", title: "Next" }),
  snapshot: call("snapshot", "Page: Example Domain\n[e0] link \"More information\" → https://www.iana.org/"),
  find: call("find", "[e0] link \"More information\""),
  readText: call("readText", `${"Ünïcödé <tag> & \"quotes\"\n".repeat(400)}`),
  consoleMessages: call("consoleMessages", ""),
  screenshot: call("screenshot", { data: JPEG, mimeType: "image/jpeg", width: 1280, height: 720, scale: 1 }),
  click: call("click", "Clicked e0."),
  clickAt: call("clickAt", "Clicked at."),
  type: call("type", "Typed."),
  key: call("key", "Pressed."),
  scroll: call("scroll", "Scrolled."),
  evaluate: call("evaluate", "42"),
};

const approved = new Set();
const gate = createGate({
  prompt: async ({ tool }) => {
    out(`PROMPT ${tool}`);
    return answers.shift() ?? "deny";
  },
  isYolo: () => false,
  approved: { has: (n) => approved.has(n), add: (n) => void approved.add(n) },
});
const tools = createToolRunner({ browser, gate, blockedHosts: () => [] });

const bridge = createBridge({
  server: env.AGENT42_SERVER,
  key: env.AGENT42_BRIDGE_KEY,
  connect: (url, protocols) => new WebSocket(url, { protocols, headers: { Origin: env.AGENT42_EXTENSION_ORIGIN } }),
  fetch: (url, init) => fetch(url, init),
  openChannel: (send) => createMcpChannel({ listTools, callTool: tools.callTool, send, version: "0.0.0-harness" }),
  onStatus: (status, detail) => out(`STATUS ${status}${detail ? ` ${detail}` : ""}`),
  timing: env.AGENT42_HEARTBEAT_MS
    ? { heartbeatMs: Number(env.AGENT42_HEARTBEAT_MS), pongTimeoutMs: Number(env.AGENT42_PONG_TIMEOUT_MS ?? env.AGENT42_HEARTBEAT_MS) }
    : {},
});

process.on("SIGTERM", () => bridge.stop());
const ended = await bridge.start();
process.exit(ended === Status.KEY_REJECTED ? 3 : ended === Status.REPLACED ? 4 : 0);
