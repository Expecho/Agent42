/* Agent 42 — artifact cards and the artifact side pane.

   ONE implementation for the chat app AND the public share viewer, so it lives under the public /shared/lib/ and is a
   CLASSIC script exposing window.Agent42ArtifactPane (the viewer is a self-contained classic IIFE that must never load
   the authenticated /js/). Each page injects what differs: the URL builders (owner routes or /api/share/{token}
   routes) and its own sanitizing renderMarkdown.

   Safety: html and svg artifacts run ONLY in <iframe sandbox="allow-scripts">, whose server response also carries a
   CSP sandbox header (the header is what protects a frame opened as a top-level page). Never add the same-origin flag.
   Fetched source reaches the DOM as textContent, or as markdown through the injected sanitizing renderMarkdown;
   nothing else.

   Look: /frame injects the app's theme (ArtifactTheme.cs) for the theme passed as ?theme=, so the pane passes the
   page's current data-theme and reloads a framed artifact when it flips. The full-screen toggle widens the pane over
   the whole window (a CSS class, not the browser Fullscreen API, so the browser chrome and Escape stay normal). */
(function () {
  "use strict";

  const KIND_LABEL = { html: "HTML", svg: "SVG", markdown: "Markdown", code: "Code" };
  const LANGUAGE_EXT = {
    python: "py", javascript: "js", typescript: "ts", csharp: "cs", bash: "sh", shell: "sh", powershell: "ps1",
    java: "java", go: "go", rust: "rs", ruby: "rb", php: "php", kotlin: "kt", swift: "swift", sql: "sql",
    json: "json", yaml: "yml", xml: "xml", css: "css", html: "html", markdown: "md", cpp: "cpp", c: "c",
  };

  function el(tag, className, text) {
    const node = document.createElement(tag);
    if (className) node.className = className;
    if (text != null) node.textContent = text;
    return node;
  }

  function button(className, label, text) {
    const b = el("button", className, text);
    b.type = "button";
    b.setAttribute("aria-label", label);
    b.title = label;
    return b;
  }

  function extensionOf(record) {
    if (record.kind === "code") return LANGUAGE_EXT[record.language] || "txt";
    return { html: "html", svg: "svg", markdown: "md" }[record.kind] || "txt";
  }

  function fileName(record) {
    const slug = (record.title || "artifact").toLowerCase().replace(/[^a-z0-9]+/g, "-").replace(/^-|-$/g, "");
    return `${slug || "artifact"}.${extensionOf(record)}`;
  }

  // Flashes a button's glyph to a check (or a cross) and back, like the app's copy buttons.
  function flash(btn, ok) {
    const original = btn.textContent;
    btn.textContent = ok ? "✓" : "✕";
    setTimeout(() => { btn.textContent = original; }, 1500);
  }

  // The theme the page painted (both pages set data-theme on <html> before first paint); dark is the app default.
  function currentTheme() {
    return document.documentElement.dataset.theme === "light" ? "light" : "dark";
  }

  async function fetchText(url) {
    const res = await fetch(url);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    return res.text();
  }

  /* create({ host, urls: { list(), content(id, version), frame(id, version) }, renderMarkdown })
     Builds one pane inside host and returns { renderCard, open, openList, close }. */
  function create({ host, urls, renderMarkdown }) {
    const pane = el("aside", "artifact-pane");
    pane.hidden = true;
    pane.setAttribute("role", "dialog");
    pane.setAttribute("aria-label", "Artifact");

    const head = el("header", "artifact-pane__head");
    const titleBox = el("div", "artifact-pane__titlebox");
    const title = el("div", "artifact-pane__title");
    const kind = el("div", "artifact-pane__kind");
    titleBox.append(title, kind);
    const versionSelect = el("select", "artifact-pane__version");
    versionSelect.setAttribute("aria-label", "Version");
    const copyBtn = button("artifact-pane__btn", "Copy source", "⧉");
    const downloadBtn = button("artifact-pane__btn", "Download", "↓");
    const listBtn = button("artifact-pane__btn", "Artifacts in this chat", "☰");
    const fullBtn = button("artifact-pane__btn artifact-pane__full", "Full screen", "⤢");
    fullBtn.setAttribute("aria-pressed", "false");
    const closeBtn = button("artifact-pane__btn", "Close", "×");
    head.append(titleBox, versionSelect, copyBtn, downloadBtn, listBtn, fullBtn, closeBtn);

    const body = el("div", "artifact-pane__body");
    const list = el("div", "artifact-pane__list");
    list.hidden = true;
    pane.append(head, body, list);
    host.appendChild(pane);

    let artifacts = [];
    let current = null; // { record, version }
    let frameTheme = null; // the theme the open frame was rendered in

    async function refresh() {
      const res = await fetch(urls.list());
      artifacts = res.ok ? await res.json() : [];
    }

    function showMessage(text) {
      body.textContent = "";
      body.appendChild(el("p", "artifact-pane__note", text));
    }

    async function renderBody(record, version) {
      body.textContent = "";
      if (record.kind === "html" || record.kind === "svg") {
        const frame = document.createElement("iframe");
        frame.className = "artifact-pane__frame";
        frame.setAttribute("sandbox", "allow-scripts");
        frame.setAttribute("referrerpolicy", "no-referrer");
        frame.title = record.title;
        frameTheme = currentTheme();
        frame.src = `${urls.frame(record.id, version)}?theme=${frameTheme}`;
        body.appendChild(frame);
        return;
      }

      const source = await fetchText(urls.content(record.id, version));
      if (current?.record !== record || current.version !== version) return; // a newer open won the race
      if (record.kind === "markdown") {
        const prose = el("div", "prose artifact-pane__doc");
        prose.innerHTML = renderMarkdown(source);
        globalThis.Agent42Rich?.enhance(prose);
        body.appendChild(prose);
        return;
      }

      // Code: textContent, never a markdown fence (a source containing three backticks would break out of one).
      const pre = el("pre", "artifact-pane__code");
      const code = el("code", record.language ? `language-${record.language}` : "", null);
      code.textContent = source;
      pre.appendChild(code);
      body.appendChild(pre);
      if (globalThis.hljs && record.language && hljs.getLanguage(record.language)) hljs.highlightElement(code);
    }

    function fillVersions(record, version) {
      versionSelect.textContent = "";
      for (const v of record.versions) {
        const option = el("option", null, v.version === record.versions.length ? `v${v.version} (latest)` : `v${v.version}`);
        option.value = String(v.version);
        option.selected = v.version === version;
        versionSelect.appendChild(option);
      }
      versionSelect.hidden = record.versions.length < 2;
    }

    function show(record, version) {
      current = { record, version };
      list.hidden = true;
      body.hidden = false;
      title.textContent = record.title;
      kind.textContent = record.kind === "code" && record.language ? `${KIND_LABEL.code} · ${record.language}` : KIND_LABEL[record.kind] || record.kind;
      fillVersions(record, version);
      renderBody(record, version).catch(() => showMessage("Couldn't load this artifact."));
    }

    async function open(artifactId, version) {
      pane.hidden = false;
      document.documentElement.classList.add("has-artifact-pane");
      try {
        await refresh();
      } catch {
        artifacts = [];
      }
      const record = artifacts.find((a) => a.id === artifactId);
      if (!record) {
        current = null;
        showMessage("This artifact is no longer available.");
        return;
      }
      const wanted = record.versions.some((v) => v.version === version) ? version : record.versions.length;
      show(record, wanted);
      closeBtn.focus();
    }

    function setFull(on) {
      pane.classList.toggle("artifact-pane--full", on);
      document.documentElement.classList.toggle("has-artifact-pane-full", on);
      fullBtn.setAttribute("aria-pressed", String(on));
      fullBtn.setAttribute("aria-label", on ? "Exit full screen" : "Full screen");
      fullBtn.title = on ? "Exit full screen" : "Full screen";
      fullBtn.textContent = on ? "⤡" : "⤢";
    }

    function close() {
      setFull(false);
      pane.hidden = true;
      body.textContent = ""; // unloads the iframe, so a closed artifact's script stops running
      current = null;
      document.documentElement.classList.remove("has-artifact-pane");
    }

    function renderList() {
      list.textContent = "";
      if (artifacts.length === 0) list.appendChild(el("p", "artifact-pane__note", "No artifacts in this chat yet."));
      for (const record of artifacts) {
        const item = button("artifact-pane__item", `Open ${record.title}`, null);
        item.append(el("span", "artifact-pane__item-title", record.title),
          el("span", "artifact-pane__item-meta", `${KIND_LABEL[record.kind] || record.kind} · v${record.versions.length}`));
        item.addEventListener("click", () => show(record, record.versions.length));
        list.appendChild(item);
      }
    }

    async function withCurrentSource(action, btn) {
      if (!current) return;
      try {
        await action(await fetchText(urls.content(current.record.id, current.version)));
        flash(btn, true);
      } catch {
        flash(btn, false);
      }
    }

    versionSelect.addEventListener("change", () => {
      if (current) show(current.record, Number(versionSelect.value));
    });
    copyBtn.addEventListener("click", () =>
      withCurrentSource((text) => navigator.clipboard.writeText(text), copyBtn));
    downloadBtn.addEventListener("click", () =>
      withCurrentSource((text) => {
        // octet-stream, so the browser saves it rather than rendering it, whatever the kind.
        const url = URL.createObjectURL(new Blob([text], { type: "application/octet-stream" }));
        const a = el("a");
        a.href = url;
        a.download = fileName(current.record);
        document.body.appendChild(a);
        a.click();
        a.remove();
        setTimeout(() => URL.revokeObjectURL(url), 1000);
      }, downloadBtn));
    async function showList() {
      try {
        await refresh();
      } catch {
        artifacts = [];
      }
      renderList();
      body.hidden = true;
      list.hidden = false;
    }

    // Opens the pane on the "Artifacts in this chat" list: the entry point that does not need a card, because cards
    // can vanish from history with their tool messages while the artifacts stay in state.
    async function openList() {
      pane.hidden = false;
      document.documentElement.classList.add("has-artifact-pane");
      body.textContent = ""; // unloads a previous artifact's iframe
      current = null;
      title.textContent = "Artifacts";
      kind.textContent = "";
      versionSelect.hidden = true;
      await showList();
      closeBtn.focus();
    }

    listBtn.addEventListener("click", async () => {
      if (!list.hidden) {
        if (current) show(current.record, current.version);
        return;
      }
      await showList();
    });
    fullBtn.addEventListener("click", () => setFull(!pane.classList.contains("artifact-pane--full")));
    closeBtn.addEventListener("click", close);
    // Escape steps back one level: out of full screen first, then closed.
    pane.addEventListener("keydown", (e) => {
      if (e.key !== "Escape") return;
      if (pane.classList.contains("artifact-pane--full")) setFull(false);
      else close();
    });

    // The frame was rendered for one theme; a theme switch while it is open reloads it in the new one. Markdown and
    // code follow by themselves, they use the page's CSS.
    new MutationObserver(() => {
      const kind = current?.record.kind;
      if (pane.hidden || body.hidden || (kind !== "html" && kind !== "svg") || currentTheme() === frameTheme) return;
      show(current.record, current.version);
    }).observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

    // One card per artifact version a turn produced. Idempotent per (artifact, version), so a replay over a bubble
    // that already holds the live card (stream recovery) does not duplicate it. beforeOpen lets a page point the
    // URL builders at the card's own conversation before the pane fetches anything.
    function renderCard(container, info, { beforeOpen } = {}) {
      let cards = container.querySelector(":scope > .artifact-cards");
      if (!cards) {
        cards = el("div", "artifact-cards");
        const prose = container.querySelector(":scope > .prose");
        if (prose) prose.after(cards);
        else container.appendChild(cards);
      }
      const key = `${info.artifactId}:${info.version}`;
      if (cards.querySelector(`[data-artifact="${CSS.escape(key)}"]`)) return;

      const card = button("artifact-card", `Open ${info.title}`, null);
      card.dataset.artifact = key;
      card.append(el("span", "artifact-card__icon", info.kind === "code" ? "</>" : info.kind === "markdown" ? "¶" : "◧"),
        el("span", "artifact-card__title", info.title),
        el("span", "artifact-card__meta", `${KIND_LABEL[info.kind] || info.kind} · v${info.version}`));
      card.addEventListener("click", () => {
        beforeOpen?.();
        open(info.artifactId, info.version);
      });
      cards.appendChild(card);
    }

    // setFull and isOpen let the chat app drive the pane for the model (ADR 0089's control_artifact_pane).
    return { renderCard, open, openList, close, setFull, isOpen: () => !pane.hidden };
  }

  window.Agent42ArtifactPane = { create };
})();
