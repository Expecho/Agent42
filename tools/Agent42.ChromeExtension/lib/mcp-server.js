// A minimal MCP server, one instance per bridge channel. The backend is the MCP client (the SDK's McpClient over
// LocalBridgeChannelTransport), so this only has to answer what that client sends: the `server/discover` probe
// (answered "method not found", so the SDK falls back to initialize at once instead of after its 5 s timeout),
// initialize, ping, tools/list and tools/call. Messages on one channel are handled strictly in order, so a call
// waiting on the approval gate holds back only its own channel.

const SUPPORTED_VERSIONS = ["2026-07-28", "2025-11-25", "2025-06-18", "2025-03-26"];
const METHOD_NOT_FOUND = -32601;
const INVALID_PARAMS = -32602;

/**
 * @param {object} deps
 * @param {() => Array<object>} deps.listTools  the tools/list entries (name, description, inputSchema, annotations)
 * @param {(name: string, args: object, signal: AbortSignal) => Promise<object>} deps.callTool  resolves to an MCP
 *   CallToolResult. `signal` aborts when this channel closes, so a call still waiting on the approval prompt is
 *   cancelled and never runs for a turn that is gone (the console helper's channel token, ported).
 * @param {(message: object) => void} deps.send  sends one JSON-RPC message back on this channel
 * @param {string} deps.version  the extension version, for serverInfo
 */
export function createMcpChannel({ listTools, callTool, send, version }) {
  let queue = Promise.resolve();
  let closed = false;
  const lifetime = new AbortController();

  const reply = (id, result) => send({ jsonrpc: "2.0", id, result });
  const fail = (id, code, message) => send({ jsonrpc: "2.0", id, error: { code, message } });

  async function handle(message) {
    if (message === null || typeof message !== "object" || typeof message.method !== "string") {
      return; // a response or garbage: this server sends no requests, so there is nothing to match it to
    }
    const { id, method, params } = message;
    const isRequest = id !== undefined && id !== null;
    if (!isRequest) {
      return; // notifications/initialized, notifications/cancelled, …
    }

    switch (method) {
      case "initialize": {
        const requested = params?.protocolVersion;
        reply(id, {
          protocolVersion: SUPPORTED_VERSIONS.includes(requested) ? requested : SUPPORTED_VERSIONS[0],
          capabilities: { tools: { listChanged: false } },
          serverInfo: { name: "agent42-chrome", version },
        });
        return;
      }
      case "ping":
        reply(id, {});
        return;
      case "tools/list":
        reply(id, { tools: listTools() });
        return;
      case "tools/call": {
        if (typeof params?.name !== "string") {
          fail(id, INVALID_PARAMS, "tools/call needs a tool name");
          return;
        }
        const args = params.arguments && typeof params.arguments === "object" ? params.arguments : {};
        const result = await callTool(params.name, args, lifetime.signal);
        if (!closed) reply(id, result);
        return;
      }
      default:
        fail(id, METHOD_NOT_FOUND, "Method not found");
    }
  }

  return {
    /** Queues one message from the backend; handled after every message before it. */
    enqueue(message) {
      queue = queue
        .then(() => (closed ? undefined : handle(message)))
        .catch((error) => {
          if (message?.id !== undefined && message?.id !== null && !closed) {
            fail(message.id, -32603, `The extension failed this request: ${error?.message ?? error}`);
          }
        });
      return queue;
    },
    close() {
      closed = true;
      lifetime.abort();
    },
  };
}
