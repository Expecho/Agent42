/* ————————————————————————————————————————————————
   Agent 42 · live telemetry console (ADR 0054)

   ONE classic script on purpose — no ES-module graph, no build step. The service worker
   stale-while-revalidates same-origin static assets, so a multi-module page here would carry ADR 0041's
   version-skew hazard (a renamed export across a stale cached module = SyntaxError and a blank page) with
   no CACHE bump to protect it; this page sits outside wwwroot/js, so no bump is due. wwwroot/shared/viewer.js
   is the same call.

   Everything is rendered through textContent / createElement, never innerHTML: this page paints raw
   telemetry — prompt text, tool arguments, fetched web pages — which is attacker-influenced content by
   construction (ADR 0011/0027/0049). It is the one page in the app that must never interpret what it shows.
   ———————————————————————————————————————————————— */
(function () {
  "use strict";

  var TRACES_URL = "/api/diagnostics/traces";
  var LOGS_URL = "/api/diagnostics/logs";
  var METRICS_URL = "/api/diagnostics/metrics";

  // Client-side retention. The server ring is the real bound; this only keeps the DOM from growing
  // without limit across a long session.
  var MAX_SPANS = 2000;
  var MAX_LOGS = 2000;
  var MAX_TRACES_RENDERED = 40;

  var state = {
    tab: "traces",
    paused: false,
    intervalMs: 2000,
    filter: "",
    minLevel: "",
    spans: [],
    logs: [],
    metrics: [],
    tracesCursor: 0,
    logsCursor: 0,
    droppedSpans: 0,
    droppedLogs: 0,
    effectiveLevel: "",
    expanded: {},
    focusTrace: null,
    error: null
  };

  var timer = null;
  var el = {};

  function byId(id) { return document.getElementById(id); }

  function init() {
    el.panels = {
      traces: byId("panelTraces"),
      logs: byId("panelLogs"),
      metrics: byId("panelMetrics")
    };
    el.tabs = Array.prototype.slice.call(document.querySelectorAll(".console__tab"));
    el.filter = byId("filterInput");
    el.level = byId("levelSelect");
    el.interval = byId("intervalSelect");
    el.pause = byId("pauseBtn");
    el.clear = byId("clearBtn");
    el.counts = byId("statusCounts");
    el.statusLevel = byId("statusLevel");

    el.tabs.forEach(function (tab) {
      tab.addEventListener("click", function () { selectTab(tab.dataset.tab); });
    });

    el.filter.addEventListener("input", function () {
      state.filter = el.filter.value.trim().toLowerCase();
      render();
    });

    // A level change re-reads from the start of the server ring: the cursor advanced past records the
    // previous, narrower filter rejected, and they are not coming back otherwise.
    el.level.addEventListener("change", function () {
      state.minLevel = el.level.value;
      state.logs = [];
      state.logsCursor = 0;
      render();
      poll();
    });

    el.interval.addEventListener("change", function () {
      state.intervalMs = parseInt(el.interval.value, 10) || 2000;
      schedule();
    });

    el.pause.addEventListener("click", function () {
      state.paused = !state.paused;
      el.pause.textContent = state.paused ? "Resume" : "Pause";
      el.pause.setAttribute("aria-pressed", state.paused ? "true" : "false");
      schedule();
    });

    el.clear.addEventListener("click", function () {
      state.spans = [];
      state.logs = [];
      state.focusTrace = null;
      state.expanded = {};
      render();
    });

    poll();
    schedule();
  }

  function selectTab(name) {
    state.tab = name;
    el.tabs.forEach(function (tab) {
      var active = tab.dataset.tab === name;
      tab.classList.toggle("is-active", active);
      tab.setAttribute("aria-selected", active ? "true" : "false");
    });
    Object.keys(el.panels).forEach(function (key) {
      el.panels[key].hidden = key !== name;
      el.panels[key].classList.toggle("is-active", key === name);
    });
    render();
  }

  function schedule() {
    if (timer) { clearInterval(timer); timer = null; }
    if (!state.paused) { timer = setInterval(poll, state.intervalMs); }
  }

  // ————— polling —————

  function getJson(url) {
    return fetch(url, { headers: { accept: "application/json" } }).then(function (res) {
      if (!res.ok) { throw new Error(url + " → " + res.status); }
      return res.json();
    });
  }

  function poll() {
    // The cursor is what keeps this cheap: each tick asks only for what arrived since the last one.
    var traces = getJson(TRACES_URL + "?since=" + state.tracesCursor);
    var logs = getJson(LOGS_URL + "?since=" + state.logsCursor +
      (state.minLevel ? "&minLevel=" + encodeURIComponent(state.minLevel) : ""));
    var metrics = getJson(METRICS_URL);

    return Promise.all([traces, logs, metrics]).then(function (results) {
      state.error = null;
      absorbTraces(results[0]);
      absorbLogs(results[1]);
      state.metrics = (results[2] && results[2].metrics) || [];
      render();
    }).catch(function (err) {
      state.error = String(err && err.message ? err.message : err);
      renderStatus();
    });
  }

  function absorbTraces(payload) {
    if (!payload) { return; }
    state.tracesCursor = payload.cursor || state.tracesCursor;
    state.droppedSpans = payload.dropped || 0;
    if (payload.spans && payload.spans.length) {
      state.spans = state.spans.concat(payload.spans).slice(-MAX_SPANS);
    }
  }

  function absorbLogs(payload) {
    if (!payload) { return; }
    state.logsCursor = payload.cursor || state.logsCursor;
    // Tracked separately from the span count: the byte budget is split evenly between the two rings, and
    // a chatty log stream is exactly the case that split was designed against — so the Logs tab is the
    // one that evicts first, and reporting one number for both would hide it.
    state.droppedLogs = payload.dropped || 0;
    state.effectiveLevel = payload.effectiveMinimumLevel || "";
    if (payload.logs && payload.logs.length) {
      state.logs = state.logs.concat(payload.logs).slice(-MAX_LOGS);
    }
  }

  // ————— shared rendering helpers —————

  function elem(tag, className, text) {
    var node = document.createElement(tag);
    if (className) { node.className = className; }
    if (text !== undefined && text !== null) { node.textContent = String(text); }
    return node;
  }

  function clear(node) {
    while (node.firstChild) { node.removeChild(node.firstChild); }
  }

  function empty(node, message, hint) {
    var box = elem("div", "console__empty", message);
    if (hint) {
      box.appendChild(document.createElement("br"));
      box.appendChild(elem("code", null, hint));
    }
    node.appendChild(box);
  }

  function matchesFilter(haystack) {
    return !state.filter || haystack.toLowerCase().indexOf(state.filter) !== -1;
  }

  function time(iso) {
    var d = new Date(iso);
    return isNaN(d.getTime()) ? "" : d.toISOString().substring(11, 23);
  }

  function tagRows(parent, title, tags) {
    if (!tags || !tags.length) { return; }
    parent.appendChild(elem("div", "detail__section", title));
    tags.forEach(function (tag) {
      var row = elem("div", "detail__row");
      row.appendChild(elem("span", "detail__key", tag.name));
      row.appendChild(elem("pre", "detail__val", tag.value));
      parent.appendChild(row);
    });
  }

  function toggleExpanded(key) {
    state.expanded[key] = !state.expanded[key];
    render();
  }

  function render() {
    if (state.tab === "traces") { renderTraces(); }
    else if (state.tab === "logs") { renderLogs(); }
    else { renderMetrics(); }
    renderStatus();
  }

  function renderStatus() {
    var parts = [
      state.spans.length + " spans",
      state.logs.length + " logs",
      state.metrics.length + " instruments"
    ];
    if (state.droppedSpans) { parts.push(state.droppedSpans + " spans dropped"); }
    if (state.droppedLogs) { parts.push(state.droppedLogs + " logs dropped"); }
    if (state.paused) { parts.push("paused"); }
    if (state.error) { parts.push("error: " + state.error); }
    el.counts.textContent = parts.join(" · ");

    // Named with its configuration key rather than as a bare "floor": this is the DEFAULT rule, and a
    // per-category override (Logging__LogLevel__Microsoft__AspNetCore) can still filter a given category
    // differently. Enough to make an empty tab legible without overclaiming.
    el.statusLevel.textContent = state.effectiveLevel
      ? "default log level: " + state.effectiveLevel + " (Logging__LogLevel__Default)"
      : "";
  }

  // ————— traces —————

  // Spans arrive CHILD-FIRST — a child completes, and is therefore captured, before its parent — so the
  // tree is built from parentSpanId over the whole buffer and never from arrival order.
  function groupTraces() {
    var traces = {};
    var order = [];
    state.spans.forEach(function (span) {
      if (!traces[span.traceId]) { traces[span.traceId] = []; order.push(span.traceId); }
      traces[span.traceId].push(span);
    });
    return { traces: traces, order: order };
  }

  function buildTree(spans) {
    var byId = {};
    spans.forEach(function (span) { byId[span.spanId] = { span: span, children: [] }; });

    var roots = [];
    spans.forEach(function (span) {
      var node = byId[span.spanId];
      // A parent outside this window is treated as a root, so a trace whose parent was evicted still
      // renders rather than vanishing.
      var parent = span.parentSpanId ? byId[span.parentSpanId] : null;
      if (parent) { parent.children.push(node); } else { roots.push(node); }
    });

    roots.forEach(sortTree);
    return roots;
  }

  function sortTree(node) {
    node.children.sort(function (a, b) { return new Date(a.span.timestamp) - new Date(b.span.timestamp); });
    node.children.forEach(sortTree);
  }

  function spanText(span) {
    var tags = (span.tags || []).map(function (t) { return t.name + " " + t.value; }).join(" ");
    return span.name + " " + span.source + " " + span.traceId + " " + (span.status || "") + " " + tags;
  }

  function traceMatches(spans) {
    if (!state.filter) { return true; }
    return spans.some(function (span) { return matchesFilter(spanText(span)); });
  }

  function renderTraces() {
    var panel = el.panels.traces;
    clear(panel);

    var grouped = groupTraces();
    var ids = grouped.order.filter(function (id) {
      if (state.focusTrace && id !== state.focusTrace) { return false; }
      return traceMatches(grouped.traces[id]);
    });

    if (!ids.length) {
      empty(panel, state.spans.length
        ? "No trace matches the current filter."
        : "No spans captured yet. Send a chat turn, or reload the app, and they will appear here.");
      return;
    }

    if (state.focusTrace) {
      var back = elem("button", "console__btn", "← all traces");
      back.addEventListener("click", function () { state.focusTrace = null; render(); });
      panel.appendChild(back);
    }

    // Newest traces first, capped so a long session cannot grow the DOM without bound.
    ids.slice(-MAX_TRACES_RENDERED).reverse().forEach(function (id) {
      panel.appendChild(renderTrace(id, grouped.traces[id]));
    });
  }

  function renderTrace(traceId, spans) {
    var box = elem("div", "trace");
    var head = elem("div", "trace__head");
    head.appendChild(elem("span", "trace__id", traceId));

    var total = Math.max.apply(null, spans.map(function (s) { return s.durationMs || 0; }));
    head.appendChild(elem("span", "trace__meta",
      spans.length + " span" + (spans.length === 1 ? "" : "s") + " · " + fmtMs(total)));
    box.appendChild(head);

    buildTree(spans).forEach(function (node) { appendSpan(box, node, 0, total || 1); });
    return box;
  }

  function appendSpan(parent, node, depth, total) {
    var span = node.span;
    var key = "span:" + span.seq;
    var row = elem("button", "span__row");
    if (span.status === "Error") { row.classList.add("is-error"); }

    var name = elem("span", "span__name", repeat("· ", depth) + span.name);
    row.appendChild(name);

    var track = elem("div", "span__bar-track");
    var bar = elem("div", "span__bar");
    bar.style.width = Math.max(1, Math.round(((span.durationMs || 0) / total) * 100)) + "%";
    track.appendChild(bar);
    row.appendChild(track);
    row.appendChild(elem("span", "span__dur", fmtMs(span.durationMs)));

    row.addEventListener("click", function () { toggleExpanded(key); });
    parent.appendChild(row);

    if (state.expanded[key]) { parent.appendChild(spanDetail(span)); }

    node.children.forEach(function (child) { appendSpan(parent, child, depth + 1, total); });
  }

  function spanDetail(span) {
    var detail = elem("div", "detail");
    tagRows(detail, "span", [
      { name: "spanId", value: span.spanId },
      { name: "parentSpanId", value: span.parentSpanId || "(root)" },
      { name: "kind", value: span.kind },
      { name: "source", value: span.source },
      { name: "status", value: span.status + (span.statusDescription ? " — " + span.statusDescription : "") },
      { name: "start", value: span.timestamp }
    ]);
    tagRows(detail, "tags", span.tags);

    (span.events || []).forEach(function (evt) {
      tagRows(detail, "event · " + evt.name, evt.tags);
    });

    return detail;
  }

  function repeat(text, times) {
    var out = "";
    for (var i = 0; i < times; i++) { out += text; }
    return out;
  }

  function fmtMs(ms) {
    if (typeof ms !== "number") { return ""; }
    return ms >= 1000 ? (ms / 1000).toFixed(2) + "s" : ms.toFixed(1) + "ms";
  }

  // ————— logs —————

  function renderLogs() {
    var panel = el.panels.logs;
    clear(panel);

    var rows = state.logs.filter(function (log) {
      return matchesFilter(log.message + " " + log.category + " " + (log.exception || ""));
    });

    if (!rows.length) {
      // The empty state has to explain itself: in production Logging:LogLevel:Default is Warning and
      // ILogger filtering runs BEFORE the OpenTelemetry processor, so Information records never reach the
      // buffer at all and no setting on this page can recover them.
      empty(panel,
        state.logs.length
          ? "No log matches the current filter."
          : "No logs captured. Nothing below the capture floor shown at the bottom right ever reaches the "
            + "buffer — that filtering happens before telemetry. Raise it deliberately with:",
        state.logs.length ? null : "Logging__LogLevel__Default=Information");
      return;
    }

    // Newest first: a diagnostics reader is looking at what just happened.
    rows.slice().reverse().forEach(function (log) { panel.appendChild(renderLog(log)); });
  }

  function renderLog(log) {
    var box = elem("div", "log log--" + log.level);
    var row = elem("button", "log__row");
    var key = "log:" + log.seq;

    row.appendChild(elem("span", "log__time", time(log.timestamp)));
    row.appendChild(elem("span", "log__level", log.level));
    row.appendChild(elem("span", "log__cat", log.category));
    row.appendChild(elem("span", "log__msg", log.message));
    row.addEventListener("click", function () { toggleExpanded(key); });
    box.appendChild(row);

    if (state.expanded[key]) { box.appendChild(logDetail(log)); }
    return box;
  }

  function logDetail(log) {
    var detail = elem("div", "detail");

    if (log.traceId) {
      var jump = elem("button", "log__trace", "view trace " + log.traceId);
      jump.addEventListener("click", function () {
        state.focusTrace = log.traceId;
        selectTab("traces");
      });
      detail.appendChild(jump);
    }

    tagRows(detail, "record", [
      { name: "category", value: log.category },
      { name: "timestamp", value: log.timestamp },
      { name: "traceId", value: log.traceId || "(none)" },
      { name: "spanId", value: log.spanId || "(none)" }
    ]);
    tagRows(detail, "attributes", log.attributes);

    if (log.scopes && log.scopes.length) {
      tagRows(detail, "scopes", log.scopes.map(function (scope, i) {
        return { name: String(i), value: scope };
      }));
    }

    if (log.exception) {
      tagRows(detail, "exception", [{ name: "detail", value: log.exception }]);
    }

    return detail;
  }

  // ————— metrics —————

  function renderMetrics() {
    var panel = el.panels.metrics;
    clear(panel);

    var rows = state.metrics.filter(function (metric) {
      return matchesFilter(metric.name + " " + metric.meter + " " + (metric.description || ""));
    });

    if (!rows.length) {
      empty(panel, state.metrics.length
        ? "No instrument matches the current filter."
        : "No metrics yet. They arrive on the capture reader's export interval "
        + "(Diagnostics:MetricExportIntervalMs), not on demand — give it one interval.");
      return;
    }

    var meters = {};
    var order = [];
    rows.forEach(function (metric) {
      if (!meters[metric.meter]) { meters[metric.meter] = []; order.push(metric.meter); }
      meters[metric.meter].push(metric);
    });

    order.sort().forEach(function (meter) {
      var box = elem("div", "meter");
      box.appendChild(elem("h2", "meter__name", meter));
      meters[meter]
        .slice()
        .sort(function (a, b) { return a.name < b.name ? -1 : 1; })
        .forEach(function (metric) { appendMetric(box, metric); });
      panel.appendChild(box);
    });
  }

  function appendMetric(parent, metric) {
    (metric.points || []).forEach(function (point, index) {
      var row = elem("div", "metric");

      var label = metric.name + (metric.unit ? " (" + metric.unit + ")" : "");
      row.appendChild(elem("span", "metric__name", index === 0 ? label : ""));

      var tags = (point.tags || []).map(function (t) { return t.name + "=" + t.value; }).join(" ");
      row.appendChild(elem("span", "metric__tags", tags || metric.type));

      // A null value is the exporter saying it did not recognise the MetricType, and the detail names it.
      // That is deliberate: a silent NaN would read like a measurement.
      var value = point.value === null || point.value === undefined
        ? (point.detail || "—")
        : formatValue(point.value) + (point.detail ? " · " + point.detail : "");
      row.appendChild(elem("span", "metric__value", value));

      parent.appendChild(row);
    });
  }

  function formatValue(value) {
    if (Number.isInteger(value)) { return String(value); }
    return Math.abs(value) >= 1000 ? value.toFixed(0) : value.toFixed(3);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", init);
  } else {
    init();
  }
})();
