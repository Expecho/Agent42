/* Agent 42 — markdown/HTML rendering helpers and the language table. Split out of app.js (ADR 0041). */

// Curated BCP-47 tag → display name for the preferred-language picker. This selects the
// *preferred language value* (seeds future speech input); it does not translate the UI.
// An empty tag means "use the device language". Extend this list as needed.
export const LANGUAGES = [
  { tag: "", name: "Use device language" },
  { tag: "en-US", name: "English (United States)" },
  { tag: "en-GB", name: "English (United Kingdom)" },
  { tag: "nl-NL", name: "Nederlands (Nederland)" },
  { tag: "nl-BE", name: "Nederlands (België)" },
  { tag: "de-DE", name: "Deutsch (Deutschland)" },
  { tag: "fr-FR", name: "Français (France)" },
  { tag: "es-ES", name: "Español (España)" },
  { tag: "it-IT", name: "Italiano (Italia)" },
  { tag: "pt-PT", name: "Português (Portugal)" },
  { tag: "pt-BR", name: "Português (Brasil)" },
  { tag: "sv-SE", name: "Svenska (Sverige)" },
  { tag: "pl-PL", name: "Polski (Polska)" },
  { tag: "ja-JP", name: "日本語 (日本)" },
  { tag: "zh-CN", name: "中文 (简体)" },
];

marked.setOptions({ gfm: true, breaks: true });

// Math tokenizing from the shared rich renderer (ADR 0083), a classic global from /shared/lib/richtext.js.
// Guarded: a 404 or a version-skewed cache on that script must degrade to plain markdown, never throw at
// module evaluation (which would blank the whole app).
if (typeof Agent42Rich !== "undefined") marked.use({ extensions: Agent42Rich.mathExtensions });

export const renderMarkdown = (text) =>
  DOMPurify.sanitize(marked.parse(text), { USE_PROFILES: { html: true } });

const escapeHtml = (text) =>
  text.replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

// Turns bare http(s) URLs in an otherwise-PLAIN user message into clickable links. The user's own
// text is not markdown (that would render their `# foo`/`*bar*` unexpectedly and open an XSS surface),
// so we escape the whole string first and then wrap only matched http(s) URLs in an anchor — the only
// HTML introduced is our own <a> over a validated http(s) href. Trailing sentence punctuation is left
// outside the link so "see https://example.com." doesn't swallow the full stop. rel guards tab-nabbing.
export const linkifyPlainText = (text) => {
  const urlPattern = /\bhttps?:\/\/[^\s<]+/g;
  let html = "";
  let lastIndex = 0;
  for (let match; (match = urlPattern.exec(text)) !== null; ) {
    html += escapeHtml(text.slice(lastIndex, match.index));
    let url = match[0];
    const trailing = url.match(/[.,;:!?)\]}'"]+$/);
    const tail = trailing ? trailing[0] : "";
    if (tail) url = url.slice(0, -tail.length);
    const safeUrl = escapeHtml(url);
    html += `<a href="${safeUrl}" target="_blank" rel="noopener noreferrer nofollow">${safeUrl}</a>`;
    html += escapeHtml(tail);
    lastIndex = match.index + match[0].length;
  }
  html += escapeHtml(text.slice(lastIndex));
  return html;
};

export const timeLabel = (iso) => {
  const d = new Date(iso);
  const today = new Date();
  const sameDay = d.toDateString() === today.toDateString();
  return sameDay
    ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
    : d.toLocaleDateString([], { month: "short", day: "numeric" }) +
      " · " + d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
};

// Estimated cost (ADR 0110), from the server's costUsd + costStatus. Unpriced is "—", never "$0": a model with no
// price row is unknown, not free. A sub-cent amount keeps two significant digits so a cheap turn does not read $0.00.
// "partial" means some calls in the aggregate had no price, so the amount is a lower bound.
export const formatCost = (usd, status) => {
  if (status === "included") return "included";
  if (usd == null) return "—";
  const amount = usd === 0 ? "$0.00" : usd < 0.01 ? `$${Number(usd.toPrecision(2))}` : `$${usd.toFixed(2)}`;
  return status === "partial" ? `≥ ${amount}` : amount;
};
