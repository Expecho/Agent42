// Every bound the extension keeps, in one place. The values that mirror the backend are pinned against it by
// ChromeExtensionScriptTests (source-text), so a change on either side fails the build instead of the wire.

/** The subprotocol offered next to the bridge key (LocalBridgeLimits.ExtensionSubprotocol). */
export const SUBPROTOCOL = "agent42-bridge.v1";

/** The close reason a displaced bridge gets (LocalBridgeLimits.ReplacedCloseReason). The loop STOPS on it. */
export const REPLACED_CLOSE_REASON = "replaced by a newer connection";

/** The route the extension dials, relative to the configured server. */
export const ROUTE = "/api/local-bridge";

/** Channel 0 carries the heartbeat; the backend numbers MCP channels from 1. */
export const CONTROL_CHANNEL = 0;

/** Open MCP channels per bridge (LocalBridgeLimits.MaxChannels). The backend never opens more. */
export const MAX_CHANNELS = 16;

/** Largest frame either way (LocalBridgeLimits.MaxMessageBytes). A bigger answer closes its channel. */
export const MAX_FRAME_BYTES = 1024 * 1024;

/**
 * App-level heartbeat. An MV3 service worker stays alive only on WebSocket MESSAGE traffic (control frames do
 * not count, and a page cannot see them), so the extension pings on channel 0 and treats a missing pong as a
 * dead path.
 */
export const HEARTBEAT_MS = 20_000;
export const PONG_TIMEOUT_MS = 20_000;

/** Reconnect backoff, as the console helper: 1 s doubling to 60 s. */
export const MIN_BACKOFF_MS = 1_000;
export const MAX_BACKOFF_MS = 60_000;

/**
 * The longest text a tool result carries. The backend cuts MCP results at 8 * 1024 characters
 * (McpToolLimits.MaxToolResultLength). Measured with SDK 2.2.0, a text-only result reaches it as a TextContent
 * whose length is the text itself. The margin covers the paging hint and any future envelope, so a page's
 * tail is cut here, with a hint, and never silently by the backend.
 */
export const MAX_RESULT_CHARS = 6_000;

/** Largest screenshot, as base64 characters. Keeps a frame under MAX_FRAME_BYTES with room to spare. */
export const MAX_SCREENSHOT_BASE64 = 700_000;

/** The longest side of a screenshot, in image pixels. */
export const SCREENSHOT_MAX_SIDE = 1280;

/** Approval gate (ConfirmationGate in the console helper). */
export const MAX_REVIEWABLE_ARG_CHARS = 20_000;
export const CONFIRM_TIMEOUT_MS = 60_000;
