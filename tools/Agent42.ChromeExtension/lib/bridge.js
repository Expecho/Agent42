// The extension's side of the bridge socket — the browser twin of the console helper's BridgeClient (ADR 0076).
// It knows nothing about chrome.*: the socket, fetch, timers and the per-channel MCP server are injected, so node
// tests drive every branch.
//
// A browser cannot see a handshake's status code (a refused upgrade is just close code 1006), so before each
// connect it sends a PREFLIGHT: a plain GET with the key in X-Api-Key, which fetch may set. 401 means the key is
// revoked → stop for good, never retry (the helper's rule). 400 means the key is good but this was not an
// upgrade → connect. Anything else (a 5xx during a revision swap, a network error) → back off and retry.

import * as envelope from "./envelope.js";
import {
  CONTROL_CHANNEL, HEARTBEAT_MS, MAX_BACKOFF_MS, MAX_CHANNELS, MAX_FRAME_BYTES, MIN_BACKOFF_MS, PONG_TIMEOUT_MS,
  REPLACED_CLOSE_REASON, ROUTE, SUBPROTOCOL,
} from "./limits.js";

/** Where the loop stands, for the badge and the popup. */
export const Status = Object.freeze({
  CONNECTING: "connecting",
  CONNECTED: "connected",
  RETRYING: "retrying",
  KEY_REJECTED: "key-rejected",
  REPLACED: "replaced",
  STOPPED: "stopped",
});

/** The http(s) preflight URL and the ws(s) socket URL for a configured server origin. */
export function bridgeUrls(server) {
  const base = new URL(server);
  const http = new URL(ROUTE, base.origin);
  const ws = new URL(http);
  ws.protocol = base.protocol === "https:" ? "wss:" : "ws:";
  return { http: http.href, ws: ws.href };
}

/**
 * @param {object} deps
 * @param {string} deps.server  the Agent42 origin, e.g. https://chat.agent42.nl
 * @param {string} deps.key  the bridge key
 * @param {(url: string, protocols: string[]) => WebSocket} deps.connect
 * @param {typeof fetch} deps.fetch
 * @param {(send: (msg: object) => void) => {enqueue(msg: object): unknown, close(): void}} deps.openChannel
 * @param {(status: string, detail?: string) => void} [deps.onStatus]
 * @param {object} [deps.timing]  overrides for tests: heartbeatMs, pongTimeoutMs, minBackoffMs, maxBackoffMs
 */
export function createBridge({ server, key, connect, fetch, openChannel, onStatus = () => {}, timing = {} }) {
  const heartbeatMs = timing.heartbeatMs ?? HEARTBEAT_MS;
  const pongTimeoutMs = timing.pongTimeoutMs ?? PONG_TIMEOUT_MS;
  const minBackoffMs = timing.minBackoffMs ?? MIN_BACKOFF_MS;
  const maxBackoffMs = timing.maxBackoffMs ?? MAX_BACKOFF_MS;
  const urls = bridgeUrls(server);

  let stopped = false;
  let backoff = minBackoffMs;
  let socket = null;
  let retryTimer = null;
  let finished;
  const done = new Promise((resolve) => (finished = resolve));

  const finish = (status, detail) => {
    stopped = true;
    clearTimeout(retryTimer);
    onStatus(status, detail);
    finished(status);
  };

  const scheduleRetry = (detail) => {
    if (stopped) return;
    onStatus(Status.RETRYING, detail);
    retryTimer = setTimeout(attempt, backoff);
    backoff = Math.min(backoff * 2, maxBackoffMs);
  };

  async function attempt() {
    if (stopped) return;
    onStatus(Status.CONNECTING);
    let status;
    try {
      status = (await fetch(urls.http, { headers: { "X-Api-Key": key }, cache: "no-store" })).status;
    } catch (error) {
      scheduleRetry(`server unreachable (${error?.message ?? error})`);
      return;
    }
    if (stopped) return;
    if (status === 401) {
      finish(Status.KEY_REJECTED, "the server rejected the bridge key");
      return;
    }
    if (status !== 400) {
      scheduleRetry(`unexpected preflight status ${status}`);
      return;
    }
    open();
  }

  function open() {
    const channels = new Map();
    let heartbeat = null;
    let pongTimer = null;
    let ws;
    try {
      ws = connect(urls.ws, [SUBPROTOCOL, key]);
    } catch (error) {
      scheduleRetry(`could not open the socket (${error?.message ?? error})`);
      return;
    }
    socket = ws;

    const send = (ch, type, msg) => {
      const text = envelope.serialize(ch, type, msg);
      if (text.length > MAX_FRAME_BYTES) {
        // Too big to relay: close this channel, never the bridge (LocalBridgeLimits' rule).
        closeChannel(ch, true);
        return;
      }
      if (ws.readyState === 1) ws.send(text);
    };

    const closeChannel = (ch, tellBackend) => {
      const channel = channels.get(ch);
      if (!channel) return;
      channels.delete(ch);
      channel.close();
      if (tellBackend && ws.readyState === 1) ws.send(envelope.serialize(ch, envelope.CLOSE));
    };

    ws.onopen = () => {
      backoff = minBackoffMs;
      onStatus(Status.CONNECTED);
      heartbeat = setInterval(() => {
        if (ws.readyState !== 1) return;
        ws.send(envelope.serialize(CONTROL_CHANNEL, envelope.PING));
        if (pongTimer === null) {
          // A dead path (a sleeping laptop, a dropped Wi-Fi) gives no close event until TCP gives up.
          pongTimer = setTimeout(() => ws.close(4000, "no pong"), pongTimeoutMs);
        }
      }, heartbeatMs);
    };

    ws.onmessage = (event) => {
      const frame = envelope.parse(typeof event.data === "string" ? event.data : "");
      if (!frame) return;
      switch (frame.type) {
        case envelope.PONG:
          clearTimeout(pongTimer);
          pongTimer = null;
          return;
        case envelope.OPEN:
          if (frame.ch === CONTROL_CHANNEL || channels.has(frame.ch)) return;
          if (channels.size >= MAX_CHANNELS) {
            ws.send(envelope.serialize(frame.ch, envelope.CLOSE));
            return;
          }
          channels.set(frame.ch, openChannel((msg) => send(frame.ch, envelope.MESSAGE, msg)));
          return;
        case envelope.MESSAGE:
          channels.get(frame.ch)?.enqueue(frame.msg);
          return;
        case envelope.CLOSE:
          closeChannel(frame.ch, false);
          return;
        default:
          return; // a ping from the server: not part of the protocol, ignored
      }
    };

    ws.onclose = (event) => {
      clearInterval(heartbeat);
      clearTimeout(pongTimer);
      for (const ch of [...channels.keys()]) closeChannel(ch, false);
      socket = null;
      if (stopped) return;
      if (event?.reason === REPLACED_CLOSE_REASON) {
        // Another extension or helper took over this key. Reconnecting would displace it back, every second.
        finish(Status.REPLACED, "another bridge connected with this key");
        return;
      }
      scheduleRetry(event?.reason ? `disconnected (${event.reason})` : "disconnected");
    };

    ws.onerror = () => {
      // Always followed by onclose, which decides what happens next.
    };
  }

  return {
    /** Starts the loop. Resolves when it stops for good: key rejected, replaced, or stop(). */
    start() {
      attempt();
      return done;
    },
    stop() {
      if (stopped) return;
      const open = socket;
      finish(Status.STOPPED);
      open?.close(1000, "extension stopping");
    },
  };
}
