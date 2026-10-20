import { test } from "node:test";
import assert from "node:assert/strict";
import { createMcpChannel } from "../lib/mcp-server.js";
import * as envelope from "../lib/envelope.js";

function channel(callTool = async () => ({ content: [{ type: "text", text: "ok" }] })) {
  const sent = [];
  const ch = createMcpChannel({
    listTools: () => [{ name: "t", inputSchema: { type: "object" }, annotations: { readOnlyHint: true } }],
    callTool,
    send: (m) => sent.push(m),
    version: "1.2.3",
  });
  return { ch, sent };
}

test("server/discover is answered method-not-found, so the SDK falls back to initialize at once", async () => {
  const { ch, sent } = channel();
  await ch.enqueue({ jsonrpc: "2.0", id: 1, method: "server/discover" });
  assert.deepEqual(sent[0], { jsonrpc: "2.0", id: 1, error: { code: -32601, message: "Method not found" } });
});

test("initialize echoes a supported protocol version, else offers the newest", async () => {
  const { ch, sent } = channel();
  await ch.enqueue({ jsonrpc: "2.0", id: 1, method: "initialize", params: { protocolVersion: "2025-06-18" } });
  await ch.enqueue({ jsonrpc: "2.0", id: 2, method: "initialize", params: { protocolVersion: "1999-01-01" } });
  assert.equal(sent[0].result.protocolVersion, "2025-06-18");
  assert.equal(sent[1].result.protocolVersion, "2026-07-28");
  assert.deepEqual(sent[0].result.serverInfo, { name: "agent42-chrome", version: "1.2.3" });
  assert.deepEqual(sent[0].result.capabilities, { tools: { listChanged: false } });
});

test("notifications get no answer; ping, tools/list and tools/call do", async () => {
  const { ch, sent } = channel();
  await ch.enqueue({ jsonrpc: "2.0", method: "notifications/initialized" });
  await ch.enqueue({ jsonrpc: "2.0", id: 2, method: "ping" });
  await ch.enqueue({ jsonrpc: "2.0", id: 3, method: "tools/list" });
  await ch.enqueue({ jsonrpc: "2.0", id: 4, method: "tools/call", params: { name: "t", arguments: { a: 1 } } });
  assert.deepEqual(sent.map((m) => m.id), [2, 3, 4]);
  assert.equal(sent[1].result.tools[0].annotations.readOnlyHint, true);
  assert.deepEqual(sent[2].result, { content: [{ type: "text", text: "ok" }] });
});

test("messages on one channel are handled strictly in order, even when a call waits", async () => {
  let release;
  const { ch, sent } = channel(() => new Promise((r) => (release = () => r({ content: [] }))));
  ch.enqueue({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "t" } });
  const second = ch.enqueue({ jsonrpc: "2.0", id: 2, method: "ping" });
  await new Promise((r) => setTimeout(r, 10));
  assert.equal(sent.length, 0);
  release();
  await second;
  assert.deepEqual(sent.map((m) => m.id), [1, 2]);
});

test("a throwing tool is an internal error on that request id, and the channel keeps working", async () => {
  const { ch, sent } = channel(async () => {
    throw new Error("boom");
  });
  await ch.enqueue({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "t" } });
  await ch.enqueue({ jsonrpc: "2.0", id: 2, method: "ping" });
  assert.equal(sent[0].error.code, -32603);
  assert.deepEqual(sent[1], { jsonrpc: "2.0", id: 2, result: {} });
});

test("closing the channel aborts the signal a waiting call was given, and its late result is dropped", async () => {
  let signal;
  let finish;
  const { ch, sent } = channel((_name, _args, s) => {
    signal = s;
    return new Promise((r) => (finish = r));
  });
  const done = ch.enqueue({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name: "t" } });
  await new Promise((r) => setTimeout(r, 5));
  ch.close();
  assert.equal(signal.aborted, true);
  finish({ content: [] });
  await done;
  assert.equal(sent.length, 0);
});

test("a closed channel answers nothing more", async () => {
  const { ch, sent } = channel();
  ch.close();
  await ch.enqueue({ jsonrpc: "2.0", id: 1, method: "ping" });
  assert.equal(sent.length, 0);
});

test("the envelope: the C# wire shape, msg omitted when absent, garbage rejected", () => {
  assert.equal(envelope.serialize(0, "ping"), "{\"ch\":0,\"type\":\"ping\"}");
  assert.equal(envelope.serialize(2, "message", { id: 1 }), "{\"ch\":2,\"type\":\"message\",\"msg\":{\"id\":1}}");
  for (const bad of ["not json", "null", "{\"ch\":1,\"type\":\"bogus\"}", "{\"ch\":\"x\",\"type\":\"open\"}", "[]"]) {
    assert.equal(envelope.parse(bad), null, bad);
  }
  assert.deepEqual(envelope.parse("{\"ch\":0,\"type\":\"pong\"}"), { ch: 0, type: "pong" });
});
