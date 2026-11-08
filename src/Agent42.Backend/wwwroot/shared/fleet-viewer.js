// Public read-only fleet task viewer (ADR 0120). Loads a shared task by the token in the URL fragment and shows its request,
// result, canvas pages and workspace files. Self-contained like viewer.js: it depends on nothing from the authenticated
// app. No compose, no send, no auth: an unauthenticated recipient can only read.
//
// Every API URL below is a literal starting /api/share/fleet/ so EasyAuthExclusionTests can check it against the
// excludedPaths matcher. Don't assemble one by concatenation.
(function () {
  "use strict";

  const sections = document.getElementById("sections");
  const stateEl = document.getElementById("viewerState");
  const head = document.getElementById("viewerHead");
  const titleEl = document.getElementById("viewerTitle");
  const metaEl = document.getElementById("viewerMeta");

  // The token rides the URL fragment (never sent on the page navigation). Strip a leading '#'.
  // A malformed percent-escape leaves the token empty, so the page says the link is missing it instead of hanging.
  const token = (() => {
    try { return decodeURIComponent((location.hash || "").replace(/^#/, "").trim()); } catch { return ""; }
  })();
  const enc = encodeURIComponent(token);

  marked.setOptions({ gfm: true, breaks: true });

  // The shared rich renderer (ADR 0083), guarded so a missing lib/richtext.js leaves plain markdown.
  const rich = typeof Agent42Rich !== "undefined" ? Agent42Rich : null;
  if (rich) marked.use({ extensions: rich.mathExtensions });

  const renderMarkdown = (text) =>
    DOMPurify.sanitize(marked.parse(text), { USE_PROFILES: { html: true } });

  const taskUrl = () => `/api/share/fleet/${enc}`;
  const filesUrl = () => `/api/share/fleet/${enc}/files`;
  const canvasUrl = () => `/api/share/fleet/${enc}/canvas`;
  const fileUrl = (path) => {
    const encoded = path.split("/").map(encodeURIComponent).join("/");
    return `/api/share/fleet/${enc}/files/${encoded}`;
  };
  const canvasVersionUrl = (id, version, route) => {
    const page = encodeURIComponent(id);
    return `/api/share/fleet/${enc}/canvas/${page}/versions/${encodeURIComponent(version)}/${route}`;
  };

  const canvasPane = typeof Agent42ArtifactPane !== "undefined"
    ? Agent42ArtifactPane.create({
      host: document.body,
      renderMarkdown,
      urls: {
        list: () => canvasUrl(),
        content: (id, v) => canvasVersionUrl(id, v, "content"),
        frame: (id, v) => canvasVersionUrl(id, v, "frame"),
      },
    })
    : null;

  // Everything the owner or a model wrote is set as text, never as HTML. Only the result goes through the sanitized
  // markdown renderer.
  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function section(label) {
    const wrap = el("section", "fleet-section");
    wrap.appendChild(el("h2", "fleet-section__label", label));
    sections.appendChild(wrap);
    return wrap;
  }

  const formatBytes = (n) => (n < 1024 ? `${n} B` : n < 1048576 ? `${(n / 1024).toFixed(1)} KB` : `${(n / 1048576).toFixed(1)} MB`);

  const formatTime = (iso) => {
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? "" : d.toLocaleString();
  };

  function renderRequest(task) {
    const wrap = section("Request");
    wrap.appendChild(el("div", "fleet-request", task.request));
  }

  function renderResult(task) {
    const wrap = section("Result");
    if (task.result) {
      const prose = el("div", "prose");
      prose.innerHTML = renderMarkdown(task.result);
      if (rich) rich.enhance(prose);
      wrap.appendChild(el("div", "msg msg--agent")).appendChild(el("div", "msg__bubble")).appendChild(prose);
      return;
    }
    const waiting = task.status === "running" ? "The task is still running." : "This task has no result.";
    wrap.appendChild(el("p", "fleet-note", task.reason || waiting));
  }

  function renderCanvas(pages) {
    if (!pages.length) return;
    const wrap = section("Canvas");
    const list = el("div", "artifact-cards");
    for (const page of pages) {
      const version = page.versions.length ? page.versions[page.versions.length - 1].version : 0;
      const card = el("button", "artifact-card");
      card.type = "button";
      card.title = `Open ${page.title}`;
      card.append(
        el("span", "artifact-card__icon", page.kind === "code" ? "</>" : page.kind === "markdown" ? "¶" : "◧"),
        el("span", "artifact-card__title", page.title),
        el("span", "artifact-card__meta", `${page.kind} · v${version}`));
      card.addEventListener("click", () => canvasPane?.open(page.id, version));
      list.appendChild(card);
    }
    wrap.appendChild(list);
  }

  function renderFiles(files) {
    if (!files.length) return;
    const wrap = section("Files");
    const list = el("ul", "fleet-files");
    for (const file of files) {
      const item = el("li");
      const link = el("a", null, file.path);
      link.href = fileUrl(file.path);
      link.download = file.path.split("/").pop();
      item.append(link, el("span", "fleet-files__size", formatBytes(file.size)));
      list.appendChild(item);
    }
    wrap.appendChild(list);
  }

  function showState(message) {
    stateEl.textContent = message;
    stateEl.hidden = false;
  }

  // The files and the canvas are secondary: if one fails the task itself still shows.
  async function loadList(url) {
    try {
      const res = await fetch(url);
      return res.ok ? await res.json() : [];
    } catch {
      return [];
    }
  }

  async function load() {
    if (!token) {
      showState("This link is missing its token.");
      return;
    }

    let res;
    try {
      res = await fetch(taskUrl());
    } catch {
      showState("Couldn't reach the server. Check your connection and try again.");
      return;
    }

    if (res.status === 404) {
      showState("This link was revoked or doesn't exist.");
      return;
    }
    if (!res.ok) {
      showState("Something went wrong loading this task.");
      return;
    }

    const task = await res.json();
    document.title = "Shared fleet task — Agent 42";
    metaEl.textContent = [task.status, task.startedAt && formatTime(task.startedAt)].filter(Boolean).join(" · ");
    head.hidden = false;
    stateEl.hidden = true;

    renderRequest(task);
    renderResult(task);

    const [pages, files] = await Promise.all([loadList(canvasUrl()), loadList(filesUrl())]);
    renderCanvas(pages);
    renderFiles(files);
  }

  load();
})();
