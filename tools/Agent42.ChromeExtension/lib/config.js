// Options parsing and the two refusals the tool layer applies BEFORE the approval gate.
//
// The hard floor is not configurable and binds every tool, `evaluate` and yolo included. chrome.debugger can
// attach to this extension's own pages and evaluate there with full chrome.* (measured), and those pages hold
// the pending approval prompt, the bridge key and the yolo switch. Script there would let the model approve
// itself. Browser-internal pages are refused for the same reason. Chrome refuses chrome:// itself, but the
// rule does not rely on that.

const FLOOR_HOSTS = ["chromewebstore.google.com"];

/**
 * The address whose site a tab really belongs to. A `blob:` URL wraps its creator's origin
 * (`blob:https://bank.example/<uuid>` runs as bank.example), so it is judged by the inner URL. Returns null for
 * anything unparseable.
 */
export function effectiveUrl(url) {
  try {
    const parsed = new URL(url);
    return parsed.protocol === "blob:" ? new URL(parsed.pathname) : parsed;
  } catch {
    return null;
  }
}

/**
 * Why no tool may act on `url`, or null. Fail closed: only http(s) pages (a blob: counts by its creator) and a bare
 * about:blank pass. Everything else is refused: extension and browser-internal pages, file:, data:, about:srcdoc,
 * and any about: page that could inherit an opener's origin, unreadable addresses included.
 */
export function hardFloorRefusal(url) {
  if (url === "about:blank") return null;
  const parsed = effectiveUrl(url);
  if (parsed === null) {
    return "This tab's address could not be read, so the extension will not act on it.";
  }
  if (parsed.protocol !== "http:" && parsed.protocol !== "https:") {
    return "The extension never acts on browser-internal, extension or local pages.";
  }
  if (FLOOR_HOSTS.includes(parsed.hostname) || (parsed.hostname === "chrome.google.com" && parsed.pathname.startsWith("/webstore"))) {
    return "The extension never acts on the Chrome Web Store.";
  }
  return null;
}

/**
 * Normalises the configured Agent42 server to its origin. https only, except plain http on the loopback
 * (DevHost). Returns null for anything else.
 */
export function normalizeServer(input) {
  let parsed;
  try {
    parsed = new URL(String(input ?? "").trim());
  } catch {
    return null;
  }
  const loopback = parsed.hostname === "localhost" || parsed.hostname === "127.0.0.1" || parsed.hostname === "[::1]";
  if (parsed.protocol === "https:" || (parsed.protocol === "http:" && loopback)) return parsed.origin;
  return null;
}

/**
 * Parses the blocked-sites text (one host per line, `#` comments allowed, a pasted URL reduced to its host)
 * into lower-case hostnames.
 */
export function parseBlockedSites(text) {
  const hosts = [];
  for (const raw of String(text ?? "").split(/\r?\n/)) {
    const line = raw.replace(/#.*/, "").trim().toLowerCase();
    if (!line) continue;
    let host = line;
    try {
      host = new URL(line.includes("://") ? line : `https://${line}`).hostname;
    } catch {
      continue;
    }
    if (host && !hosts.includes(host)) hosts.push(host);
  }
  return hosts;
}

/** Whether `url`'s site (a blob: by its creator) is a blocked host or a subdomain of one. Unreadable → blocked. */
export function isBlocked(url, blockedHosts) {
  if (url === "about:blank") return false;
  const parsed = effectiveUrl(url);
  if (parsed === null || !parsed.hostname) return true;
  const host = parsed.hostname.toLowerCase();
  return blockedHosts.some((blocked) => host === blocked || host.endsWith(`.${blocked}`));
}

/** The blocked-site refusal text, or null. */
export function blockedRefusal(url, blockedHosts) {
  return isBlocked(url, blockedHosts)
    ? "This site is on the extension's blocked list, so no browser action may run here."
    : null;
}

/** The default blocked list: the Agent42 server itself, so the agent never drives its own chat UI. */
export function defaultBlockedSites(server) {
  const origin = normalizeServer(server);
  return origin ? new URL(origin).hostname : "";
}
