import { test } from "node:test";
import assert from "node:assert/strict";
import { Status, bridgeUrls, createBridge } from "../lib/bridge.js";
import { REPLACED_CLOSE_REASON, SUBPROTOCOL } from "../lib/limits.js";

class FakeSocket {
  constructor(url, protocols) {
    this.url = url;
    this.protocols = protocols;
    this.readyState = 0;
    this.sent = [];
    this.closedWith = null;
  }
  open() {
    this.readyState = 1;
    this.onopen?.();
  }
  receive(frame) {
    this.onmessage?.({ data: JSON.stringify(frame) });
  }
  send(text) {
    this.sent.push(JSON.parse(text));
  }
  close(code, reason) {
    this.closedWith = { code, reason };
    this.drop(reason);
  }
  drop(reason = "") {
    this.readyState = 3;
    this.onclose?.({ code: 1006, reason });
  }
}

const tick = (ms = 0) => new Promise((r) => setTimeout(r, ms));

function harness({ preflight = [400], timing = {} } = {}) {
  const sockets = [];
  const statuses = [];
  const fetches = [];
  const channels = [];
  const bridge = createBridge({
    server: "https://chat.example",
    key: "sk_bridge_test",
    connect: (url, protocols) => {
      const s = new FakeSocket(url, protocols);
      sockets.push(s);
      return s;
    },
    fetch: async (url, init) => {
      fetches.push({ url, init });
      const next = preflight.length > 1 ? preflight.shift() : preflight[0];
      if (next instanceof Error) throw next;
      return { status: next };
    },
    openChannel: (send) => {
      const channel = { received: [], closed: false, send, enqueue(m) { this.received.push(m); }, close() { this.closed = true; } };
      channels.push(channel);
      return channel;
    },
    onStatus: (s) => statuses.push(s),
    timing: { minBackoffMs: 5, maxBackoffMs: 20, heartbeatMs: 10_000, pongTimeoutMs: 10_000, ...timing },
  });
  return { bridge, sockets, statuses, fetches, channels };
}

test("the URLs: https → wss, the route appended, any path on the configured server dropped", () => {
  assert.deepEqual(bridgeUrls("https://chat.example/whatever"), {
    http: "https://chat.example/api/local-bridge",
    ws: "wss://chat.example/api/local-bridge",
  });
  assert.equal(bridgeUrls("http://localhost:61326").ws, "ws://localhost:61326/api/local-bridge");
});

test("the preflight carries the key in X-Api-Key; the socket offers the subprotocol and the key", async () => {
  const { bridge, sockets, fetches } = harness();
  bridge.start();
  await tick();
  assert.equal(fetches[0].init.headers["X-Api-Key"], "sk_bridge_test");
  assert.deepEqual(sockets[0].protocols, [SUBPROTOCOL, "sk_bridge_test"]);
  bridge.stop();
});

test("a 401 preflight stops for good without retrying", async () => {
  const { bridge, sockets, fetches } = harness({ preflight: [401] });
  assert.equal(await bridge.start(), Status.KEY_REJECTED);
  await tick(50);
  assert.equal(fetches.length, 1);
  assert.equal(sockets.length, 0);
});

test("a 5xx or a network error backs off and retries; the next 400 connects", async () => {
  const { bridge, sockets, fetches, statuses } = harness({ preflight: [502, new Error("offline"), 400] });
  bridge.start();
  await tick(80);
  assert.ok(fetches.length >= 3);
  assert.equal(sockets.length, 1);
  assert.ok(statuses.includes(Status.RETRYING));
  bridge.stop();
});

test("the replaced close reason stops the loop; any other close retries", async () => {
  const replaced = harness();
  const done = replaced.bridge.start();
  await tick();
  replaced.sockets[0].open();
  replaced.sockets[0].drop(REPLACED_CLOSE_REASON);
  assert.equal(await done, Status.REPLACED);
  await tick(50);
  assert.equal(replaced.sockets.length, 1);

  const dropped = harness();
  dropped.bridge.start();
  await tick();
  dropped.sockets[0].open();
  dropped.sockets[0].drop("network");
  await tick(50);
  assert.equal(dropped.sockets.length, 2);
  dropped.bridge.stop();
});

test("a ping goes out on channel 0, and a missing pong closes the socket and reconnects", async () => {
  const { bridge, sockets } = harness({ timing: { heartbeatMs: 10, pongTimeoutMs: 15 } });
  bridge.start();
  await tick();
  sockets[0].open();
  await tick(60);
  assert.deepEqual(sockets[0].sent[0], { ch: 0, type: "ping" });
  assert.deepEqual(sockets[0].closedWith, { code: 4000, reason: "no pong" });
  assert.ok(sockets.length >= 2);
  bridge.stop();
});

test("a pong keeps the socket open", async () => {
  const { bridge, sockets } = harness({ timing: { heartbeatMs: 10, pongTimeoutMs: 25 } });
  bridge.start();
  await tick();
  const socket = sockets[0];
  socket.open();
  const answer = setInterval(() => socket.receive({ ch: 0, type: "pong" }), 5);
  await tick(80);
  clearInterval(answer);
  assert.equal(socket.closedWith, null);
  bridge.stop();
});

test("channels open, carry messages both ways, close, and are capped at 16", async () => {
  const { bridge, sockets, channels } = harness();
  bridge.start();
  await tick();
  const socket = sockets[0];
  socket.open();
  socket.receive({ ch: 1, type: "open" });
  socket.receive({ ch: 1, type: "message", msg: { jsonrpc: "2.0", id: 1, method: "ping" } });
  assert.deepEqual(channels[0].received, [{ jsonrpc: "2.0", id: 1, method: "ping" }]);
  channels[0].send({ jsonrpc: "2.0", id: 1, result: {} });
  assert.deepEqual(socket.sent.at(-1), { ch: 1, type: "message", msg: { jsonrpc: "2.0", id: 1, result: {} } });
  socket.receive({ ch: 1, type: "close" });
  assert.equal(channels[0].closed, true);

  for (let ch = 2; ch <= 17; ch++) socket.receive({ ch, type: "open" });
  socket.receive({ ch: 18, type: "open" });
  assert.equal(channels.length, 1 + 16);
  assert.deepEqual(socket.sent.at(-1), { ch: 18, type: "close" });
  socket.receive({ ch: 0, type: "open" });
  assert.equal(channels.length, 17, "channel 0 is never an MCP channel");
  bridge.stop();
});

test("an answer bigger than a frame closes its channel, never the bridge", async () => {
  const { bridge, sockets, channels } = harness();
  bridge.start();
  await tick();
  const socket = sockets[0];
  socket.open();
  socket.receive({ ch: 3, type: "open" });
  channels[0].send({ jsonrpc: "2.0", id: 1, result: { text: "x".repeat(1024 * 1024) } });
  assert.deepEqual(socket.sent.at(-1), { ch: 3, type: "close" });
  assert.equal(channels[0].closed, true);
  assert.equal(socket.closedWith, null);
  bridge.stop();
});

test("a disconnect closes every open channel", async () => {
  const { bridge, sockets, channels } = harness();
  bridge.start();
  await tick();
  sockets[0].open();
  sockets[0].receive({ ch: 1, type: "open" });
  sockets[0].receive({ ch: 2, type: "open" });
  sockets[0].drop("gone");
  assert.ok(channels.every((c) => c.closed));
  bridge.stop();
});
