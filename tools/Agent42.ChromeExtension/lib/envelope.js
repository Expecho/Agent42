// One frame on the bridge socket: {"ch":<int>,"type":"open"|"message"|"close"|"ping"|"pong","msg":<json-rpc>?}.
// The third copy of LocalBridgeEnvelope (backend) and BridgeEnvelope (console helper). The key order matters
// only for readability; ChromeExtensionScriptTests pins the literal prefix against the C# pair.

export const OPEN = "open";
export const MESSAGE = "message";
export const CLOSE = "close";
export const PING = "ping";
export const PONG = "pong";

const TYPES = new Set([OPEN, MESSAGE, CLOSE, PING, PONG]);

/** Serializes a frame. `msg` is omitted when absent, as the C# copies do. */
export function serialize(ch, type, msg) {
  return msg === undefined ? JSON.stringify({ ch, type }) : JSON.stringify({ ch, type, msg });
}

/** Parses a frame, or returns null for anything that is not a well-formed envelope. */
export function parse(text) {
  let value;
  try {
    value = JSON.parse(text);
  } catch {
    return null;
  }
  if (value === null || typeof value !== "object" || !Number.isInteger(value.ch) || !TYPES.has(value.type)) {
    return null;
  }
  return value;
}
