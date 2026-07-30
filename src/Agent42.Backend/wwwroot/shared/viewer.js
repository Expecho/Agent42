// Public read-only conversation viewer (ADR 0035). Loads a shared conversation by the token in the URL
// fragment and renders it with the same turn shape (role/text/attachments) app.js uses — a trimmed,
// self-contained copy so this page depends on nothing from the authenticated app surface. No compose, no
// send, no auth: an unauthenticated recipient can only read.
(function () {
  "use strict";

  const thread = document.getElementById("thread");
  const stateEl = document.getElementById("viewerState");
  const head = document.getElementById("viewerHead");
  const titleEl = document.getElementById("viewerTitle");
  const agentEl = document.getElementById("viewerAgent");

  // The token rides the URL fragment (never sent on the page navigation). Strip a leading '#'.
  const token = decodeURIComponent((location.hash || "").replace(/^#/, "").trim());

  marked.setOptions({ gfm: true, breaks: true });

  const renderMarkdown = (text) =>
    DOMPurify.sanitize(marked.parse(text), { USE_PROFILES: { html: true } });

  const escapeHtml = (text) =>
    text.replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // Bare http(s) URLs in an otherwise-PLAIN user message made clickable (same rule as app.js: user text is
  // not markdown, so escape the whole string then wrap only matched URLs). rel guards tab-nabbing.
  const linkifyPlainText = (text) => {
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

  // Attachment bytes come through the token-scoped public proxy — the page never holds a blob URL.
  const attachmentUrl = (attachmentId) =>
    `/api/share/${encodeURIComponent(token)}/attachments/${encodeURIComponent(attachmentId)}`;

  function addMessage(role) {
    const msg = document.createElement("article");
    msg.className = role === "user" ? "msg msg--user" : "msg msg--agent";

    const meta = document.createElement("div");
    meta.className = "msg__meta";
    meta.textContent = role === "user" ? "YOU" : "AGENT 42";

    const bubble = document.createElement("div");
    bubble.className = "msg__bubble";
    if (role !== "user") {
      const prose = document.createElement("div");
      prose.className = "prose";
      bubble.appendChild(prose);
    }

    msg.append(meta, bubble);
    thread.appendChild(msg);
    return msg;
  }

  function fillUserBubble(msg, text, attachments) {
    const bubble = msg.querySelector(".msg__bubble");
    bubble.textContent = "";

    if (attachments && attachments.length) {
      const gallery = document.createElement("div");
      gallery.className = "msg__images";
      for (const att of attachments) {
        const img = document.createElement("img");
        img.className = "msg__image";
        img.loading = "lazy";
        img.alt = att.fileName || "attachment";
        img.src = attachmentUrl(att.id);
        gallery.appendChild(img);
      }
      bubble.appendChild(gallery);
    }

    if (text) {
      const body = document.createElement("div");
      body.className = "msg__text";
      body.innerHTML = linkifyPlainText(text);
      bubble.appendChild(body);
    }
  }

  function addAgentImage(msg, attachmentId, fileName) {
    const bubble = msg.querySelector(".msg__bubble");
    let gallery = bubble.querySelector(".msg__images");
    if (!gallery) {
      gallery = document.createElement("div");
      gallery.className = "msg__images";
      bubble.insertBefore(gallery, bubble.firstChild); // above the prose
    }
    const img = document.createElement("img");
    img.className = "msg__image";
    img.loading = "lazy";
    img.alt = fileName || "generated image";
    img.src = attachmentUrl(attachmentId);
    gallery.appendChild(img);
  }

  function renderTurns(turns) {
    for (const turn of turns) {
      const msg = addMessage(turn.role === "user" ? "user" : "agent");
      if (turn.role === "user") {
        fillUserBubble(msg, turn.text, turn.attachments);
      } else {
        msg.querySelector(".prose").innerHTML = renderMarkdown(turn.text || "");
        for (const att of turn.attachments ?? []) addAgentImage(msg, att.id, att.fileName);
      }
    }
  }

  function showState(message) {
    stateEl.textContent = message;
    stateEl.hidden = false;
  }

  async function load() {
    if (!token) {
      showState("This link is missing its token.");
      return;
    }

    let res;
    try {
      res = await fetch(`/api/share/${encodeURIComponent(token)}`);
    } catch {
      showState("Couldn't reach the server. Check your connection and try again.");
      return;
    }

    if (res.status === 404) {
      showState("This link was revoked or doesn't exist.");
      return;
    }
    if (!res.ok) {
      showState("Something went wrong loading this conversation.");
      return;
    }

    const data = await res.json();
    document.title = `${data.title || "Shared conversation"} — Agent 42`;
    titleEl.textContent = data.title || "Shared conversation";
    if (data.agentName) {
      agentEl.textContent = `⛓ ${data.agentName}`;
      agentEl.hidden = false;
    }
    head.hidden = false;

    stateEl.hidden = true;
    if (!data.turns || data.turns.length === 0) {
      showState("This conversation is empty.");
      return;
    }
    renderTurns(data.turns);
  }

  load();
})();
