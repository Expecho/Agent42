// The approval gate, on the machine whose browser is being driven: the extension twin of the console helper's
// ConfirmationGate (ADR 0076), ported rule for rule. The backend relays whatever the model asks for, and this is
// the only control.
//
// Fail closed. A call passes unprompted only when its tool is declared read-only AND no string argument looks
// like a URL (a page load can carry data out). Every other call prompts, and the answers are: once, always for
// this tool (this browser session), or deny. A prompt that is closed or not answered in 60 s is a deny.
// Prompts are serialized; after waiting for the lock the "always" set is checked again, so calls queued behind
// the prompt that approved them are released rather than asked again. The RAW tool name is recorded.
//
// Two deliberate exceptions, both user decisions recorded in the ADR:
//  - a tool marked `autoRun` (evaluate) never prompts;
//  - "yolo" (an options-page checkbox, off by default) passes everything.
// Neither reaches the hard floor or the blocked sites: those are checked by the tool layer BEFORE this gate.

import { CONFIRM_TIMEOUT_MS, MAX_REVIEWABLE_ARG_CHARS } from "./limits.js";

export const DECLINED_TEXT = "The user declined this browser action.";
export const TOO_LARGE_TEXT = "The browser action was too large to review, so it was declined.";
export const TIMED_OUT_TEXT = "The user did not answer in time, so the browser action was declined.";
export const CANCELLED_TEXT = "The request was cancelled before the user answered, so the browser action did not run.";

/** The three answers a prompt can give. Anything else is a deny. */
export const Answer = Object.freeze({ DENY: "deny", ONCE: "once", ALWAYS: "always" });

const URL_LIKE = /^(?:[A-Za-z][A-Za-z0-9+.-]*:|\/\/)/;

// C0/C1 controls (ESC among them), DEL, format characters (the bidi overrides, zero-width joiners), lone
// surrogates and unassigned code points: everything that can rewrite or reorder what the reviewer sees.
const UNSAFE = /[\p{Cc}\p{Cf}\p{Cs}\p{Cn}]/gu;

/** Strips characters that can disguise text. Newlines survive only when asked (the argument block). */
export function sanitize(text, keepNewlines) {
  if (!keepNewlines) return text.replace(UNSAFE, "");
  return text.split("\n").map((line) => line.replace(UNSAFE, "")).join("\n");
}

/** True when any string anywhere in the arguments looks like a URL: `scheme:` or `//host`. */
export function containsUrlLikeString(value) {
  if (typeof value === "string") return URL_LIKE.test(value.trimStart());
  if (Array.isArray(value)) return value.some(containsUrlLikeString);
  if (value && typeof value === "object") return Object.values(value).some(containsUrlLikeString);
  return false;
}

/** The MCP CallToolResult a refused call is answered with: a result with isError, so the model reads why. */
export function denialResult(text) {
  return { content: [{ type: "text", text }], isError: true };
}

/**
 * @param {object} deps
 * @param {(request: {tool: string, args: string, target: string}, signal: AbortSignal) => Promise<string>} deps.prompt
 *   shows the request and returns an Answer. `signal` aborts when the gate stops waiting (a timeout, or the channel
 *   closing), and the prompt must then close its window, so a late click can never look accepted.
 * @param {() => Promise<boolean> | boolean} deps.isYolo
 * @param {{has(name: string): Promise<boolean>|boolean, add(name: string): Promise<void>|void}} deps.approved
 *   the "always for this tool" set. chrome.storage.session in the extension: it survives the service worker
 *   being suspended, dies with the browser session, and content scripts cannot read it.
 * @param {number} [deps.timeoutMs]
 */
export function createGate({ prompt, isYolo, approved, timeoutMs = CONFIRM_TIMEOUT_MS }) {
  let lock = Promise.resolve();

  // Resolves to the answer, TIMED_OUT (null) or CANCELLED. Whichever comes first wins, and the prompt is told to close.
  const CANCELLED = Symbol("cancelled");
  const ask = (request, cancel) =>
    new Promise((resolve) => {
      const stop = new AbortController();
      const settle = (value) => {
        clearTimeout(timer);
        cancel?.removeEventListener("abort", onCancel);
        stop.abort();
        resolve(value);
      };
      const onCancel = () => settle(CANCELLED);
      const timer = setTimeout(() => settle(null), timeoutMs);
      cancel?.addEventListener("abort", onCancel);
      Promise.resolve()
        .then(() => prompt(request, stop.signal))
        .then((answer) => settle(answer), () => settle(Answer.DENY));
    });

  /**
   * Reviews one call.
   * @param {string} name  the raw tool name
   * @param {object} args
   * @param {{readOnly?: boolean, autoRun?: boolean, target?: string}} meta  the tool's own declaration, and the
   *   tab the call acts on (title and address), shown to the reviewer: a ref or a tab id alone says nothing
   * @param {AbortSignal} [cancel]  aborts when the request is gone (its channel or the socket closed). The console
   *   helper cancels its prompt the same way.
   * @returns {Promise<{allowed: true} | {allowed: false, text: string}>}
   */
  async function review(name, args, meta = {}, cancel) {
    if ((await isYolo()) || meta.autoRun || (await approved.has(name))) return { allowed: true };
    if (meta.readOnly && !containsUrlLikeString(args)) return { allowed: true };

    const shown = sanitize(JSON.stringify(args ?? {}, null, 2), true);
    if (shown.length > MAX_REVIEWABLE_ARG_CHARS) return { allowed: false, text: TOO_LARGE_TEXT };

    const previous = lock;
    let release;
    lock = new Promise((resolve) => (release = resolve));
    await previous;
    try {
      if (cancel?.aborted) return { allowed: false, text: CANCELLED_TEXT };
      // The answer to the call ahead of this one may have just approved this tool for the session.
      if (await approved.has(name)) return { allowed: true };

      const target = sanitize(meta.target ?? "", false);
      const answer = await ask({ tool: sanitize(name, false), args: shown, target }, cancel);
      if (answer === CANCELLED) return { allowed: false, text: CANCELLED_TEXT };
      if (answer === null) return { allowed: false, text: TIMED_OUT_TEXT };
      if (answer === Answer.ALWAYS) {
        await approved.add(name); // the RAW name: that is what later calls arrive with
        return { allowed: true };
      }
      return answer === Answer.ONCE ? { allowed: true } : { allowed: false, text: DECLINED_TEXT };
    } finally {
      release();
    }
  }

  return { review };
}
