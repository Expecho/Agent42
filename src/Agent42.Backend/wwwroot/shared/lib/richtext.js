/* Agent 42 — rich inline rendering of assistant replies (ADR 0083).

   ONE renderer for the chat app AND the public share viewer, which is why it lives under the public
   /shared/lib/ and is a CLASSIC script exposing window.Agent42Rich (the viewer is a self-contained classic
   IIFE and must never load the authenticated /lib/ or /js/). Two halves:

   - mathExtensions: marked extensions that turn $…$, $$…$$, \(…\) and \[…\] into placeholders
     (.math-inline / .math-block carrying the TeX in data-tex). They run inside marked.parse, on every
     stream chunk, so they only tokenize; they never typeset.
   - enhance(root): runs on a FINALIZED reply only (never per chunk: a half-streamed diagram or JSON is
     invalid, and re-rendering per token would flicker). Highlights code + adds a copy button, then
     lazy-loads mermaid / KaTeX / Chart.js only when a matching element exists.

   Safety: mermaid is strict and its SVG goes through DOMPurify; charts are JSON.parse only (no functions
   can be expressed, so no callbacks run); KaTeX runs with trust off. Every lib is vendored and same-origin,
   so there is no new egress. A failing block keeps its code and gets a .rich-error note; it never stops the
   other blocks. */
(function () {
  "use strict";

  const LIB = "/shared/lib/";
  const MAX_DIAGRAM_SOURCE = 50000;
  const MAX_CHART_SOURCE = 100000;
  const MAX_CHART_POINTS = 5000;
  const MAX_HIGHLIGHT_SOURCE = 50000;
  const MAX_AUTODETECT_SOURCE = 5000;

  // ————— lazy loading (memoized; a failed load is forgotten so a later reply can retry) —————

  const loads = new Map();

  function load(path, create) {
    if (!loads.has(path)) {
      loads.set(path, new Promise((resolve, reject) => {
        const el = create(LIB + path);
        el.onload = () => resolve();
        el.onerror = () => {
          loads.delete(path);
          el.remove();
          reject(new Error(`could not load ${path}`));
        };
        document.head.appendChild(el);
      }));
    }
    return loads.get(path);
  }

  function loadScript(path) {
    return load(path, (src) => {
      const s = document.createElement("script");
      s.src = src;
      return s;
    });
  }

  function loadStyle(path) {
    return load(path, (href) => {
      const l = document.createElement("link");
      l.rel = "stylesheet";
      l.href = href;
      return l;
    });
  }

  // ————— theme —————

  // Both pages paint an explicit data-theme on <html> before first paint; fall back to the OS preference.
  function currentTheme() {
    const painted = document.documentElement.dataset.theme;
    if (painted === "light" || painted === "dark") return painted;
    return matchMedia("(prefers-color-scheme: dark)").matches ? "dark" : "light";
  }

  // The page ships both highlight themes as <link data-hljs-theme="light|dark">; enable the matching one.
  // Cheap, so it also follows a live theme toggle through the observer below.
  function syncCodeTheme() {
    const theme = currentTheme();
    for (const link of document.querySelectorAll("link[data-hljs-theme]")) {
      link.disabled = link.dataset.hljsTheme !== theme;
    }
  }

  syncCodeTheme();
  new MutationObserver(syncCodeTheme)
    .observe(document.documentElement, { attributes: true, attributeFilter: ["data-theme"] });

  // ————— math: marked extensions (tokenize only) —————

  const escapeHtml = (text) =>
    text.replace(/[&<>"']/g, (c) =>
      ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

  // The escaped TeX is both the attribute and the visible fallback, so an un-enhanced reply (streaming,
  // or KaTeX failed to load) still shows the source.
  const mathPlaceholder = (tag, cls, tex) =>
    `<${tag} class="${cls}" data-tex="${escapeHtml(tex)}">${escapeHtml(tex)}</${tag}>`;

  // Block math: $$…$$ or \[…\] OPENING A LINE and closing at the end of a line; may span blank lines
  // (an inline-only rule would be split at the paragraph boundary). start() only reports line-start
  // delimiters, so a $$ in the middle of a sentence never clips its paragraph; the inline rule takes it.
  // The content may not contain its own closing delimiter: otherwise "$$x$$ and text" (closed mid-line, so
  // not a block) would stretch to some LATER line-ending $$ and swallow everything in between.
  const BLOCK_MATH =
    /^ {0,3}(?:\$\$((?:(?!\$\$)[\s\S])+?)\$\$|\\\[((?:(?!\\\])[\s\S])+?)\\\])[ \t]*(?:\n|$)/;

  const mathBlock = {
    name: "mathBlock",
    level: "block",
    // marked hands start() src.slice(1), so index 0 here is mid-line: only a real newline counts. A
    // delimiter at the very start of src needs no cut; the tokenizer is tried there first anyway.
    start(src) {
      const m = /\n {0,3}(\$\$|\\\[)/.exec(src);
      return m ? m.index + m[0].length - m[1].length : undefined;
    },
    tokenizer(src) {
      const m = BLOCK_MATH.exec(src);
      if (m) return { type: "mathBlock", raw: m[0], text: (m[1] ?? m[2]).trim() };
      return undefined;
    },
    renderer(token) {
      return mathPlaceholder("div", "math-block", token.text) + "\n";
    },
  };

  // Inline math. $$…$$ and \(…\) always count. A single $…$ counts only when the character after the
  // opening $ and before the closing $ is not whitespace, and the closing $ is not followed by a digit,
  // so "$5 and $10" stays text. Code spans/fences never reach here: their tokenizers win first.
  const INLINE_MATH = [
    { re: /^\$\$((?:\\[\s\S]|[^\\$])+?)\$\$/, display: true },
    { re: /^\\\(([\s\S]+?)\\\)/, display: false },
    { re: /^\$(?!\s)((?:\\.|[^\\$\n])+?)(?<!\s)\$(?!\d)/, display: false },
  ];

  const mathInline = {
    name: "mathInline",
    level: "inline",
    start(src) {
      const m = /\$|\\\(/.exec(src);
      return m ? m.index : undefined;
    },
    tokenizer(src) {
      for (const { re, display } of INLINE_MATH) {
        const m = re.exec(src);
        if (m) return { type: "mathInline", raw: m[0], text: m[1].trim(), display };
      }
      return undefined;
    },
    renderer(token) {
      return mathPlaceholder("span", token.display ? "math-inline math-inline--display" : "math-inline",
        token.text);
    },
  };

  // ————— per-block helpers —————

  // A failed renderer leaves the original block and says why, next to it.
  function markError(el, what, err) {
    const note = document.createElement(el.tagName === "SPAN" ? "span" : "div");
    note.className = "rich-error";
    note.textContent = `Couldn't render ${what}: ${(err && err.message) || err}`;
    el.after(note);
    console.warn(`[rich] ${what}`, err);
  }

  // Runs one block's renderer, sync or async, so that its failure stays with that block.
  function guard(el, what, task) {
    try {
      Promise.resolve(task()).catch((err) => markError(el, what, err));
    } catch (err) {
      markError(el, what, err);
    }
  }

  function languageOf(code) {
    const cls = [...code.classList].find((c) => c.startsWith("language-"));
    return cls ? cls.slice("language-".length).toLowerCase() : "";
  }

  // Same glyph and feedback idiom as the per-message copy button (thread.js addCopyButton). Copies the
  // raw source (textContent), not the highlighted markup.
  function addCodeCopy(wrap, code) {
    const btn = document.createElement("button");
    btn.type = "button";
    btn.className = "rich-code__copy";
    btn.setAttribute("aria-label", "Copy code");
    btn.textContent = "⧉";
    btn.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(code.textContent);
        btn.textContent = "✓";
        setTimeout(() => { btn.textContent = "⧉"; }, 1500);
      } catch {
        /* clipboard blocked (insecure context / permission) — the code is visible to select manually */
      }
    });
    wrap.appendChild(btn);
  }

  // Wraps the <pre> so the copy button stays put while the code scrolls sideways. A rendered diagram or
  // chart is inserted before the wrapper and hides it; a failed one leaves it showing.
  function wrapCode(pre, code) {
    const wrap = document.createElement("div");
    wrap.className = "rich-code";
    pre.before(wrap);
    wrap.appendChild(pre);
    addCodeCopy(wrap, code);
    return wrap;
  }

  // Highlighting runs on the main thread for every block of every reloaded turn, so it is bounded: past
  // MAX_HIGHLIGHT_SOURCE a block stays plain, and language auto-detection (tries every grammar) stops sooner.
  function highlight(code, lang) {
    if (typeof hljs === "undefined") return;
    const source = code.textContent;
    const known = lang && hljs.getLanguage(lang);
    if (source.length > MAX_HIGHLIGHT_SOURCE || (!known && source.length > MAX_AUTODETECT_SOURCE)) return;
    const result = known
      ? hljs.highlight(source, { language: lang, ignoreIllegals: true })
      : hljs.highlightAuto(source);
    code.innerHTML = result.value; // hljs escapes the source; the only markup it adds is its own spans
    code.classList.add("hljs");
  }

  // ————— mermaid —————

  let mermaidTheme = null;
  let diagramSeq = 0;

  async function renderDiagram(wrap, code) {
    const source = code.textContent;
    if (source.length > MAX_DIAGRAM_SOURCE) throw new Error("diagram source is too large");
    await loadScript("mermaid/mermaid.min.js");

    const theme = currentTheme() === "dark" ? "dark" : "default";
    if (mermaidTheme !== theme) {
      // htmlLabels off: HTML labels are <foreignObject>, which the svg sanitize profile (rightly) strips.
      // secure: the keys a %%{init: …}%% directive in the diagram text may NOT override. Mermaid's default
      // list leaves themeCSS/fontFamily open, which would let model-written text inject page-global CSS.
      mermaid.initialize({
        startOnLoad: false,
        securityLevel: "strict",
        secure: ["secure", "securityLevel", "startOnLoad", "maxTextSize", "suppressErrorRendering", "maxEdges",
          "themeCSS", "themeVariables", "fontFamily", "altFontFamily"],
        theme,
        htmlLabels: false,
        flowchart: { htmlLabels: false },
        suppressErrorRendering: true,
      });
      mermaidTheme = theme;
    }

    const { svg } = await mermaid.render(`rich-diagram-${++diagramSeq}`, source);
    const figure = document.createElement("div");
    figure.className = "rich-diagram";
    figure.innerHTML = DOMPurify.sanitize(svg, { USE_PROFILES: { svg: true, svgFilters: true } });
    neutralizeCss(figure);
    wrap.before(figure);
    addCodeCopy(figure, code);
    wrap.hidden = true;
  }

  // An inline SVG's <style> applies to the WHOLE page, and DOMPurify keeps it (mermaid needs it). Diagram text
  // can still reach CSS through `style`/`classDef` statements, so any url() that is not a same-document
  // fragment (mermaid's own markers use url(#…)) and any @import is removed: no request leaves the page.
  const EXTERNAL_CSS = /@import[^;]*;?|url\(\s*(?!['"]?#)[^)]*\)/gi;

  function neutralizeCss(root) {
    for (const style of root.querySelectorAll("style")) {
      style.textContent = style.textContent.replace(EXTERNAL_CSS, "none");
    }
    for (const el of root.querySelectorAll("[style]")) {
      el.setAttribute("style", el.getAttribute("style").replace(EXTERNAL_CSS, "none"));
    }
  }

  // ————— charts —————

  // A JSON config cannot carry functions, so nothing in it can execute. Bound its size so a runaway
  // model output cannot freeze the tab.
  function parseChart(source) {
    if (source.length > MAX_CHART_SOURCE) throw new Error("chart config is too large");
    const config = JSON.parse(source);
    if (!config || typeof config.type !== "string" || !config.data) {
      throw new Error("a chart needs a \"type\" and \"data\"");
    }
    const datasets = Array.isArray(config.data.datasets) ? config.data.datasets : [];
    const points = datasets.reduce((n, d) => n + (Array.isArray(d && d.data) ? d.data.length : 0), 0);
    if (points > MAX_CHART_POINTS) throw new Error(`more than ${MAX_CHART_POINTS} data points`);
    return config;
  }

  async function renderChart(wrap, code) {
    const config = parseChart(code.textContent);
    await loadScript("chart/chart.umd.min.js");

    // Axis/legend ink and grid lines follow the page tokens, so a chart reads in both themes. The grid goes
    // through the scale defaults, NOT Chart.defaults.borderColor: changing that global switches off Chart.js's
    // automatic dataset palette, and every uncolored chart turns grey.
    const css = getComputedStyle(document.documentElement);
    const ink = css.getPropertyValue("--ink-dim").trim();
    const line = css.getPropertyValue("--line").trim();
    if (ink) Chart.defaults.color = ink;
    if (line) {
      Chart.defaults.scale.grid.color = line;
      Chart.defaults.scale.border.color = line;
    }

    const box = document.createElement("div");
    box.className = "rich-chart";
    const canvas = document.createElement("canvas");
    box.appendChild(canvas);
    wrap.before(box);
    try {
      new Chart(canvas, config);
    } catch (err) {
      box.remove();
      throw err;
    }
    addCodeCopy(box, code);
    wrap.hidden = true;
  }

  // ————— math typesetting —————

  async function renderMath(nodes) {
    await Promise.all([loadScript("katex/katex.min.js"), loadStyle("katex/katex.min.css")]);
    for (const el of nodes) {
      guard(el, "formula", () => katex.render(el.dataset.tex, el, {
        displayMode: el.classList.contains("math-block") || el.classList.contains("math-inline--display"),
        throwOnError: false,
        trust: false,
        maxSize: 50,
        maxExpand: 1000,
      }));
    }
  }

  // ————— entry point —————

  function enhanceCode(pre) {
    const code = pre.querySelector("code");
    if (!code) return;
    const lang = languageOf(code);
    const wrap = wrapCode(pre, code);
    if (lang === "mermaid") {
      guard(wrap, "diagram", () => renderDiagram(wrap, code));
    } else if (lang === "chart") {
      guard(wrap, "chart", () => renderChart(wrap, code));
    } else {
      guard(wrap, "code highlighting", () => highlight(code, lang));
    }
  }

  // Idempotent: processed elements are marked data-rich="1", so a second call is a no-op for them.
  function enhance(root) {
    if (!root) return;
    for (const pre of root.querySelectorAll("pre:not([data-rich])")) {
      pre.dataset.rich = "1";
      enhanceCode(pre);
    }

    const math = [...root.querySelectorAll(".math-inline:not([data-rich]), .math-block:not([data-rich])")];
    if (math.length === 0) return;
    for (const el of math) el.dataset.rich = "1";
    renderMath(math).catch((err) => console.warn("[rich] math", err)); // fallback: the TeX stays as text
  }

  window.Agent42Rich = {
    mathExtensions: [mathBlock, mathInline],
    enhance,
    loadScript,
    loadStyle,
  };
})();
